using UnityEngine;
using System.Collections.Generic;
using RobotSNAP.Agents.Movement.Controllers;
using RobotSNAP.Agents.Movement.Interfaces;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using UnityEngine.AI;

namespace RobotSNAP.Agents
{
    [RequireComponent(typeof(Rigidbody))]
    public class HumanMovement : MonoBehaviour
    {
        [Header("Fallback Settings")]
        [SerializeField] private float _maxSpeed = 2.0f;
        [SerializeField] private float _angularSpeed = 180f;

        [Header("Obstacle SphereCast Settings")]
        [SerializeField] private float _sphereCastRadiusScale = 0.8f;
        [SerializeField] private int _raycastCountPerSide = 6;
        [SerializeField] private float _angleStepDegrees = 30f;
        [SerializeField] private float _verticalOffset = 0.5f;

        /// <summary>Distance an agent walks before its fan of static-obstacle casts is recomputed, in metres.</summary>
        private const float ObstacleScanStep = 0.35f;

        /// <summary>How far the heading may turn before the fan is cast again, as a dot product (about 10 degrees).</summary>
        private const float ObstacleScanFacing = 0.985f;

        // References
        private HumanManager _humanManager;
        private int _agentId;

        /// <summary>True while this human is part of the manager's neighbour index.</summary>
        private bool _registered;

        private HumanAgent _avatar;
        private IMovementController _controller;
        private HumanConfig _config;
        private Rigidbody _rb;

        /// <summary>Where the fan of static-obstacle casts was last cast from, and which way the agent faced.</summary>
        private Vector2 _obstacleScanOrigin;
        private Vector2 _obstacleScanForward;
        private bool _hasObstacleScan;

        // NavMesh
        private NavMeshPath _navMeshPath;
        private Vector3[] _pathCorners;
        private float _lastPathUpdate;
        private Vector2 _currentGoalPoint;

        // Scenario grid path (see RobotSNAP.Core.Scenario.ScenarioNavigation)
        private readonly List<Vector2> _plannedPath = new List<Vector2>();
        private Vector2 _plannedGoal;
        private bool _scenarioPathInUse;

        // Set when the human walks on a velocity commanded from outside Unity (the Python API).
        private ExternalControlController _externalController;
        private bool _warnedAboutExternalCommand;

        // Set when the human walks the recorded track of an episode instead of deciding its own movement.
        private RecordedTrajectoryController _recordedController;

        /// <summary>Destination move that makes the current path obsolete, in metres.</summary>
        private const float GoalChangeTolerance = 0.5f;

        /// <summary>How far the agent may drift from its path before it is replanned, in metres.</summary>
        private const float PathDeviationTolerance = 0.6f;

        /// <summary>Delay before a plan postponed by the frame budget is asked again, in seconds.</summary>
        private const float DeferredReplanDelay = 0.03f;

        // State
        private Vector2 _currentVelocity;
        private Vector2 _currentPosition;
        private bool _isPlaying = true;
        private MovementControllerType _currentControllerType;
        private float _cruiseSpeedOverride;

        // Neighbors
        private readonly List<Vector2> _neighborPositions = new List<Vector2>();
        private readonly List<Vector2> _neighborVelocities = new List<Vector2>();

        /// <summary>
        /// Smallest radius the neighbour query uses. The social repulsion only matters under a metre or two, but
        /// anticipatory avoidance has to see a conflict coming from further away, so the query is widened.
        /// </summary>
        private const float AnticipationLookAhead = 4.5f;

        // Obstacles
        private readonly List<Vector2> _tempObstacles = new List<Vector2>();

        // Robot to yield to (cached, refreshed periodically)
        private Robot _robot;
        private float _nextRobotLookup;
        private const float RobotLookupInterval = 2f;

        #region Unity Lifecycle

        private void Awake()
        {
            InitializeComponents();
        }

        private void FixedUpdate()
        {
            // The yaw of a pedestrian belongs to the steering step below, never to the solver. Two bodies that
            // collide hand each other an angular velocity, and nothing in this simulation damps it: the X and Z
            // axes are frozen, but Y is free, so a pedestrian the crowd bumped into kept turning on the spot for
            // the rest of the run - measured at 0.7 to 2.4 rad/s with a linear velocity of zero. Cancelling it
            // here also covers the agents that are not stepping at all, held at a start or stopped at their goal.
            if (_rb != null)
                _rb.angularVelocity = Vector3.zero;

            if (!_isPlaying || _avatar == null)
                return;

            // An externally driven human owns no goal: the Python API commands its velocity, so the movement
            // step has to run even though nobody ever gave the agent a destination.
            if (_avatar.hasDestination || IsExternallyControlled || IsReplayControlled)
                Move();
        }

        #endregion

        #region Initialization

        private void InitializeComponents()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            _rb.useGravity = false;
            _rb.isKinematic = false;                 // Pour utiliser linearVelocity
            _rb.constraints = RigidbodyConstraints.FreezeRotationX |
                              RigidbodyConstraints.FreezeRotationZ |
                              RigidbodyConstraints.FreezePositionY;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0f;
            _rb.interpolation = RigidbodyInterpolation.None;

            _navMeshPath = new NavMeshPath();
            _pathCorners = new Vector3[0];
            SetupCollider();
            _currentVelocity = Vector2.zero;
        }

        private void SetupCollider()
        {
            CapsuleCollider collider = GetComponent<CapsuleCollider>();
            if (collider == null) collider = gameObject.AddComponent<CapsuleCollider>();

            // The body the social forces keep apart is the one physics has to resolve. A capsule narrower than
            // the configured radius lets two agents stand inside each other: the model believes they are
            // touching while the solver still sees daylight between them.
            float radius = _config != null ? Mathf.Max(0.05f, _config.agentRadius) : 0.25f;

            var renderer = GetComponentInChildren<SkinnedMeshRenderer>();
            if (renderer != null)
            {
                var bounds = renderer.bounds;
                float height = bounds.extents.y * 2;
                collider.radius = radius;
                collider.height = Mathf.Max(height, radius * 2f);
                collider.center = Vector3.up * height / 1.95f;
            }
            else
            {
                collider.radius = radius;
                collider.height = Mathf.Max(1.7f, radius * 2f);
                collider.center = Vector3.up * 0.85f;
            }
        }

        public void Initialize(HumanAgent avatar, HumanConfig config)
        {
            _avatar = avatar;
            _config = config;
            // The capsule radius comes from the configuration, which only exists from here on.
            SetupCollider();
            _agentId = avatar.agentId;
            // The crowd index is joined as soon as the agent is enabled, and the pool hands the manager over
            // before this call, so the id may have been registered while this avatar was still unknown. The
            // command path that drives a human from outside Unity looks the agent up by id; without this the
            // lookup finds nothing and every command is refused as an unknown id.
            _humanManager?.SetAgentOwner(_agentId, avatar);
            _currentControllerType = config.controllerType;
            _warnedAboutExternalCommand = false;
            InitializeController(_currentControllerType);
        }

        private void OnEnable()
        {
            TryJoinNeighbourIndex();
        }

        private void OnDisable()
        {
            TryLeaveNeighbourIndex();
        }

        private void OnDestroy()
        {
            TryLeaveNeighbourIndex();
        }

        /// <summary>
        /// Adds this human to the shared neighbour index. Called when it is enabled and again from the
        /// movement step, so an agent that starts before it knows its manager still ends up in the crowd.
        /// </summary>
        private void TryJoinNeighbourIndex()
        {
            if (_registered || _humanManager == null || !isActiveAndEnabled)
                return;

            Vector2 currentPos = new Vector2(transform.position.x, transform.position.z);
            _humanManager.RegisterAgent(_agentId, currentPos, _avatar);
            _humanManager.UpdateAgent(_agentId, currentPos, _currentVelocity);
            _registered = true;
        }

        /// <summary>
        /// Leaves the index exactly once. Tracking it here — rather than testing the id — keeps the
        /// first pooled human, whose id is 0, from staying in the crowd forever.
        /// </summary>
        private void TryLeaveNeighbourIndex()
        {
            if (!_registered)
                return;

            _humanManager?.UnregisterAgent(_agentId);
            _registered = false;
        }

        /// <summary>
        /// Pushes the position this agent is standing at into the shared crowd index.
        ///
        /// An agent held at its start never runs the movement step, and the index would otherwise keep the
        /// place it was enabled at - the pool, which sits at the origin of the map - for the whole hold. Every
        /// other agent then walks around a body that is not there, and a crowd whose destination is that
        /// origin is pushed away from its own goal. The route is set once the spawn has been applied, so this
        /// is the moment the index can be told where the agent really stands.
        /// </summary>
        public void SyncNeighbourIndex()
        {
            if (_humanManager == null)
                return;

            TryJoinNeighbourIndex();
            if (!_registered)
                return;

            _humanManager.UpdateAgent(
                _agentId,
                new Vector2(transform.position.x, transform.position.z),
                _currentVelocity);
        }

        private void InitializeController(MovementControllerType type)
        {
            switch (type)
            {
                // Manual and External are the same controller: both own no goal and replay the last velocity
                // they were handed. Only the hand that refreshes that velocity differs — a keyboard inside the
                // application, or a driver on the other end of the connector — and the type is remembered so
                // the snapshot can say which of the two is walking the agent.
                case MovementControllerType.External:
                case MovementControllerType.Manual:
                    _externalController = new ExternalControlController(_config);
                    _controller = _externalController;
                    _recordedController = null;
                    break;
                // A replayed human owns no goal either: its recorded track says where it goes, and the
                // controller steers the body back onto it. See RecordedTrajectoryController.
                case MovementControllerType.Replay:
                    _externalController = null;
                    _recordedController = new RecordedTrajectoryController(_config);
                    _controller = _recordedController;
                    break;
                default: // SFM, and any value a scene still carries from an older build.
                    _externalController = null;
                    _recordedController = null;
                    _controller = new SFMController(_config, _avatar);
                    break;
            }
        }

        /// <summary>
        /// Desired velocity of a human driven from outside Unity, in world units per second. It is ignored —
        /// with a warning — when the agent is not configured with the external controller.
        /// </summary>
        public void SetExternalVelocity(Vector2 velocity)
        {
            if (_externalController == null)
            {
                if (!_warnedAboutExternalCommand)
                {
                    _warnedAboutExternalCommand = true;
                    Debug.LogWarning(
                        "[HumanMovement] External velocity ignored: this human does not use the External " +
                        "controller. Set movement_controller.type to 'external' in the scenario.");
                }
                return;
            }

            _externalController.SetCommand(velocity);
        }

        /// <summary>Stops a human driven from outside Unity, and forgets its command.</summary>
        public void ClearExternalVelocity() => _externalController?.ClearCommand();

        /// <summary>True when the velocity of this human comes from outside Unity.</summary>
        public bool IsExternallyControlled => _externalController != null;

        /// <summary>
        /// Hands this human the recorded track of an episode, in the episode's own world seconds and world
        /// XZ metres. It is ignored unless the agent is walking with the replay controller, which is the same
        /// contract <see cref="SetExternalVelocity"/> keeps.
        /// </summary>
        public void SetRecordedTrack(IReadOnlyList<double[]> samples) => _recordedController?.SetTrack(samples);

        /// <summary>True when this human repeats the recorded movement of an episode.</summary>
        public bool IsReplayControlled => _currentControllerType == MovementControllerType.Replay;

        /// <summary>The controller this human is walking with right now.</summary>
        public MovementControllerType ControllerType => _currentControllerType;

        /// <summary>
        /// True when the velocity of this human comes from a keyboard inside the application, rather than
        /// from the Python API, ROS2, or the social force model.
        /// </summary>
        public bool IsManuallyControlled => _currentControllerType == MovementControllerType.Manual;

        public void SetHumanManager(HumanManager manager)
        {
            if (manager == _humanManager)
                return;

            // The index belongs to the manager that holds it: leave the old crowd before joining the new one.
            TryLeaveNeighbourIndex();
            _humanManager = manager;
            TryJoinNeighbourIndex();
        }

        /// <summary>
        /// Turns the agent on the spot. The controller only rotates while it is walking, so a freshly spawned
        /// agent used to keep facing whatever direction its prefab was authored with.
        /// </summary>
        public void SnapRotation(Vector2 direction)
        {
            if (direction.sqrMagnitude < 0.0001f)
                return;

            Quaternion target = Quaternion.LookRotation(new Vector3(direction.x, 0f, direction.y), Vector3.up);
            transform.rotation = target;
            if (_rb != null)
                _rb.rotation = target;
        }

        #endregion

        #region Movement Logic

        private void Move()
        {
            _currentPosition = new Vector2(transform.position.x, transform.position.z);

            // What the body really did during the last physics step, which is not what that step asked for:
            // a wall stops the agent, a neighbour pushes it aside, and a crowd pressed together leaves several
            // of them standing still with a commanded velocity they cannot use. Facing, the walk animation and
            // the crowd index all follow this value, so an agent squeezed by the crowd stops turning on the
            // spot and stops walking in place.
            Vector2 actualVelocity = new Vector2(_rb.linearVelocity.x, _rb.linearVelocity.z);

            UpdateNavMeshPath();

            // Récupération des voisins
            _neighborPositions.Clear();
            _neighborVelocities.Clear();
            if (_humanManager != null)
            {
                // Anticipation needs to see further ahead than the repulsion does: a conflict four metres away
                // is already worth steering for, and this query is what bounds what the controller can see.
               _humanManager.GetNeighbors(
                    _agentId,
                    _currentPosition,
                    Mathf.Max(_config.perceptionRadiusAgent, AnticipationLookAhead),
                    _neighborPositions,
                    _neighborVelocities
                );
            }

            // Détection des obstacles statiques (SphereCast)
            RefreshObstacleScan();

            // Calcul de la nouvelle vitesse via le contrôleur
            Vector2 desiredVelocity = _controller.ComputeVelocity(
                _currentPosition,
                _currentVelocity,
                _currentGoalPoint,
                _neighborPositions,
                _neighborVelocities,
                _tempObstacles,
                ObserveRobot(),
                Time.fixedDeltaTime,
                _cruiseSpeedOverride
            );

            // Application de la vélocité via le Rigidbody
            Vector3 finalVelocity3D = new Vector3(desiredVelocity.x, 0, desiredVelocity.y);
            float maxSpeed = _config != null ? _config.maxSpeed : _maxSpeed;
            if (finalVelocity3D.sqrMagnitude > maxSpeed * maxSpeed)
                finalVelocity3D = finalVelocity3D.normalized * maxSpeed;

            _rb.linearVelocity = finalVelocity3D;

            // Mise à jour de notre vélocité stockée (pour l'animation, etc.)
            _currentVelocity = new Vector2(finalVelocity3D.x, finalVelocity3D.z);

            // Rotation via MoveRotation (pour éviter les conflits)
            UpdateRotation(actualVelocity);

            // Mise à jour du HumanManager
            if (_humanManager != null)
            {
                TryJoinNeighbourIndex();
                _humanManager.UpdateAgent(_agentId, _currentPosition, actualVelocity);
            }

            // Transmission de la vélocité réelle à l'agent (pour l'animation)
            _avatar?.SetVelocity(new Vector3(actualVelocity.x, 0f, actualVelocity.y));

            KeepOutOfTheWall();
        }

        /// <summary>
        /// Casts the fan of static-obstacle rays, but only when the answer can have changed.
        ///
        /// The walls do not move: the fan is a function of where the agent stands and which way it faces, so
        /// thirteen sphere casts per agent per physics step mostly repeated an empty result - it was the most
        /// expensive thing a crowd did, and every agent paid for it on every step. An agent standing still now
        /// casts nothing at all, and one walking a straight line casts about three times a second.
        /// </summary>
        private void RefreshObstacleScan()
        {
            Vector2 forward2D = new Vector2(transform.forward.x, transform.forward.z);
            if (_hasObstacleScan &&
                Vector2.Distance(_currentPosition, _obstacleScanOrigin) < ObstacleScanStep &&
                Vector2.Dot(forward2D, _obstacleScanForward) > ObstacleScanFacing)
                return;

            _hasObstacleScan = true;
            _obstacleScanOrigin = _currentPosition;
            _obstacleScanForward = forward2D;

            _tempObstacles.Clear();
            int layerMask = 1 << LayerMask.NameToLayer("Obstacle");
            float sphereRadius = _config.agentRadius * _sphereCastRadiusScale;
            float maxDistance = _config.obstaclePerceptionRadius;
            Vector3 origin = transform.position + Vector3.up * _verticalOffset;
            Vector3 forward = transform.forward;

            void CastAndAdd(Vector3 direction)
            {
                if (Physics.SphereCast(origin, sphereRadius, direction, out RaycastHit hit, maxDistance, layerMask))
                {
                    Vector3 closestPoint = hit.point - hit.normal * sphereRadius;
                    Vector2 obsPos = new Vector2(closestPoint.x, closestPoint.z);
                    float dist = Vector2.Distance(_currentPosition, obsPos);
                    if (dist > 0.01f)
                        _tempObstacles.Add(obsPos);
                }
            }

            CastAndAdd(forward);
            for (int i = 1; i <= _raycastCountPerSide; i++)
            {
                float angle = i * _angleStepDegrees;
                CastAndAdd(Quaternion.AngleAxis(angle, Vector3.up) * forward);
                CastAndAdd(Quaternion.AngleAxis(-angle, Vector3.up) * forward);
            }
        }

        /// <summary>
        /// Puts an agent the crowd has pushed into a wall back onto walkable ground.
        ///
        /// The occupancy grid of the scenario is the authority on where a pedestrian may stand, and physics
        /// alone does not enforce it: a dense crowd presses an agent through the edge of its capsule, and it
        /// then has no way out on its own, because the neighbours pushing it in are the ones it would have to
        /// walk through. The correction is the projection the planner already uses for a waypoint dropped
        /// against a wall, and it is bounded to twice the agent radius so nobody is teleported across a room.
        /// </summary>
        private void KeepOutOfTheWall()
        {
            // Only an agent standing in the wall itself is corrected. The walkable grid is inflated by the
            // agent radius, so it also covers the strip along a wall where a pedestrian may legitimately walk;
            // correcting against it would push everybody away from every wall.
            OccupancyGrid obstacles = ScenarioNavigation.Obstacles;
            if (obstacles == null || !obstacles.IsValid || obstacles.IsWorldWalkable(_currentPosition))
                return;

            float radius = _config != null ? Mathf.Max(0.1f, _config.agentRadius) : 0.25f;
            if (!ScenarioNavigation.TryProjectToWalkable(_currentPosition, radius * 2f, out Vector2 walkable))
                return;

            _rb.position = new Vector3(walkable.x, transform.position.y, walkable.y);
            _currentPosition = walkable;
        }

        #endregion

        #region Navigation

        /// <summary>
        /// Describes the robot for the movement controller so humans yield to it.
        /// The lookup is cached because finding it every physics step would be wasteful.
        /// </summary>
        /// <summary>
        /// Snapshot of the robot for the movement controller. Public because the group also needs it: a member
        /// steps out of its slot for the robot the same way it does for a stranger.
        /// </summary>
        public RobotObservation ObserveRobot()
        {
            if (_robot == null && Time.time >= _nextRobotLookup)
            {
                _nextRobotLookup = Time.time + RobotLookupInterval;
                _robot = FindAnyObjectByType<Robot>();
            }

            if (_robot == null || !_robot.gameObject.activeInHierarchy)
                return RobotObservation.None;

            Vector3 position = _robot.Position;
            Vector3 velocity = _robot.Velocity;
            return new RobotObservation(
                true,
                new Vector2(position.x, position.z),
                new Vector2(velocity.x, velocity.z),
                Mathf.Max(0.25f, _robot.Radius));
        }

        private void UpdateNavMeshPath()
        {
            if (_config == null) return;

            // Nothing to plan for an externally driven human: the controller ignores the goal, so planning
            // towards the authored route — or towards the (0,0) placeholder — would only burn grid searches.
            // A replayed human ignores its goal the same way: its route belongs to the recording.
            if (IsExternallyControlled || IsReplayControlled) return;

            if (Time.time - _lastPathUpdate < _config.pathUpdateInterval) return;
            _lastPathUpdate = Time.time;

            Vector2 destination = _avatar.currentDestination;

            // The scenario's walkable grid is the one the editor drew on: when it is loaded, the agents follow
            // exactly the trajectories the author validated instead of a NavMesh built from the scene.
            if (ScenarioNavigation.IsAvailable)
            {
                // A path that is still valid is not replanned: in a group walking an open corridor, every member
                // would otherwise run a full grid search five times a second for a one-metre leg.
                if (CanKeepScenarioPath(destination))
                {
                    RefreshScenarioPathEnd(destination);
                    return;
                }

                bool planned = ScenarioNavigation.TryPlan(_currentPosition, destination, _plannedPath);

                // The first path of an agent is never postponed: without one it would steer straight through
                // the walls until the frame budget lets it through.
                if (!planned && !_scenarioPathInUse)
                {
                    List<Vector2> unbudgeted = ScenarioNavigation.Plan(_currentPosition, destination);
                    if (unbudgeted != null && unbudgeted.Count >= 2)
                    {
                        _plannedPath.Clear();
                        _plannedPath.AddRange(unbudgeted);
                        planned = true;
                    }
                }

                if (planned)
                {
                    ApplyPlannedPath(_plannedPath);
                    _plannedGoal = destination;
                    _scenarioPathInUse = true;
                    return;
                }

                // The frame's search budget is spent: keep the current path and ask again shortly, instead of
                // making the whole crowd replan in the same frame.
                if (_scenarioPathInUse && _pathCorners is { Length: >= 2 })
                {
                    _lastPathUpdate = Time.time - _config.pathUpdateInterval + DeferredReplanDelay;
                    return;
                }
            }

            _scenarioPathInUse = false;
            Vector3 start3D = new Vector3(_currentPosition.x, 0, _currentPosition.y);
            Vector3 goal3D = new Vector3(destination.x, 0, destination.y);

            if (NavMesh.CalculatePath(start3D, goal3D, NavMesh.AllAreas, _navMeshPath))
            {
                if (_navMeshPath.status == NavMeshPathStatus.PathComplete)
                {
                    _pathCorners = _navMeshPath.corners;
                    UpdateCurrentGoalPoint();
                }
                else
                {
                    _currentGoalPoint = _avatar.currentDestination;
                }
            }
            else
            {
                _currentGoalPoint = _avatar.currentDestination;
            }
        }

        /// <summary>
        /// True while the path already computed still leads where the agent is going: its destination has not
        /// moved by more than <see cref="GoalChangeTolerance"/>, and the agent has not been pushed off the
        /// polyline by more than <see cref="PathDeviationTolerance"/>.
        /// </summary>
        private bool CanKeepScenarioPath(Vector2 destination)
        {
            if (!_scenarioPathInUse || _pathCorners is not { Length: >= 2 })
                return false;

            if ((destination - _plannedGoal).sqrMagnitude > GoalChangeTolerance * GoalChangeTolerance)
                return false;

            return ScenarioNavigation.IsPathStillValid(_currentPosition, _pathCorners, PathDeviationTolerance);
        }

        /// <summary>
        /// Re-anchors a path that is kept: the current position replaces the stale start and the destination
        /// replaces the last corner, so a slowly moving goal — a formation slot — is followed without paying
        /// for a search. The corners the agent has already reached are dropped on the way, and that part is what
        /// keeps the path leading forward: keeping them left a head behind the agent, the steering target
        /// flipped between that corner and the destination, and the agent looped on the spot until its route
        /// gave up on it — which is exactly what the Default scenario's pedestrian did.
        /// </summary>
        private void RefreshScenarioPathEnd(Vector2 destination)
        {
            Vector2 target = ScenarioNavigation.TryProjectToWalkable(
                destination,
                ScenarioNavigation.SnapRadius,
                out Vector2 walkable)
                ? walkable
                : destination;

            float advanceDistance = _config != null
                ? Mathf.Max(_config.nextNavMinDistance, _config.slowDownDistance)
                : 1f;
            int consumed = HumanPathTargetSelector.CountConsumedCorners(
                _currentPosition,
                _pathCorners,
                advanceDistance);
            if (consumed > 0)
            {
                for (int index = consumed; index < _pathCorners.Length; index++)
                    _pathCorners[index - consumed] = _pathCorners[index];

                System.Array.Resize(ref _pathCorners, _pathCorners.Length - consumed);
            }

            _pathCorners[0] = new Vector3(_currentPosition.x, 0f, _currentPosition.y);
            _pathCorners[^1] = new Vector3(target.x, 0f, target.y);
            _plannedGoal = destination;
            UpdateCurrentGoalPoint();
        }

        /// <summary>Turns a planned polyline into the corner list the steering target is selected from.</summary>
        private void ApplyPlannedPath(IReadOnlyList<Vector2> planned)
        {
            if (_pathCorners == null || _pathCorners.Length != planned.Count)
                _pathCorners = new Vector3[planned.Count];

            for (int index = 0; index < planned.Count; index++)
                _pathCorners[index] = new Vector3(planned[index].x, 0f, planned[index].y);

            UpdateCurrentGoalPoint();
        }

        private void UpdateCurrentGoalPoint()
        {
            // A scenario plan ends on walkable ground, which is not necessarily the authored point: steering
            // towards the authored goal would push the agent into the wall the planner routed around.
            Vector2 destination = _scenarioPathInUse && _pathCorners is { Length: > 0 }
                ? new Vector2(_pathCorners[^1].x, _pathCorners[^1].z)
                : _avatar.currentDestination;

            if (_pathCorners == null || _pathCorners.Length == 0)
            {
                _currentGoalPoint = destination;
                return;
            }

            float destinationReachedDistance = _config != null ? _config.goalReachedDistance : 0.2f;
            float waypointAdvanceDistance = _config != null
                ? Mathf.Max(_config.nextNavMinDistance, _config.slowDownDistance)
                : 1f;
            _currentGoalPoint = HumanPathTargetSelector.SelectTarget(
                _currentPosition,
                destination,
                _pathCorners,
                waypointAdvanceDistance,
                destinationReachedDistance);
        }

        #endregion

        #region Rotation

        private void UpdateRotation(Vector2 movementDirection)
        {
            if (movementDirection.sqrMagnitude < 0.001f) return;

            float angle = Mathf.Atan2(movementDirection.x, movementDirection.y) * Mathf.Rad2Deg;
            Quaternion targetRotation = Quaternion.Euler(0, angle, 0);
            float angularSpeed = _config != null ? _config.angularSpeed : _angularSpeed;

            // Utiliser MoveRotation pour être cohérent avec la physique
            _rb.MoveRotation(Quaternion.RotateTowards(
                _rb.rotation,
                targetRotation,
                angularSpeed * Time.fixedDeltaTime
            ));
        }

        #endregion

        #region Public API

        public void SetPlaying(bool playing)
        {
            if (_isPlaying == playing)
                return;

            _isPlaying = playing;

            // A pause stops the body, nothing else. Zeroing the stored velocity and the avatar's
            // as well — which is what this used to do — threw away the walk the agent was in the
            // middle of: it resumed from a standstill and the animation started over. The rigid
            // body still has to be stopped, or the agent keeps gliding through the pause.
            if (!_isPlaying)
                _rb.linearVelocity = Vector3.zero;
        }

        public void Stop()
        {
            _rb.linearVelocity = Vector3.zero;
            _currentVelocity = Vector2.zero;
            _avatar?.SetVelocity(Vector3.zero);
        }

        /// <summary>
        /// Imposes a cruise speed for the next frames (0 restores the configured one).
        /// Used by group followers that have to close a gap on their leader.
        /// </summary>
        public void SetCruiseSpeedOverride(float speed) => _cruiseSpeedOverride = Mathf.Max(0f, speed);

        public void SetGoal(Vector2 goal)
        {
            if (_avatar != null)
            {
                _avatar.currentDestination = goal;
                _avatar.hasDestination = true;
            }
            // Never expose the reset value (0,0) as a temporary steering target.
            _currentGoalPoint = goal;
            _lastPathUpdate = -_config.pathUpdateInterval;
        }

        public void Reset()
        {
            _controller?.Reset();
            _cruiseSpeedOverride = 0f;
            _rb.linearVelocity = Vector3.zero;
            _currentVelocity = Vector2.zero;
            _avatar?.SetVelocity(Vector3.zero);
            _pathCorners = new Vector3[0];
            _currentGoalPoint = Vector2.zero;
            // A pooled agent is about to be put down somewhere else entirely, so the fan of obstacle casts
            // taken where it stood before says nothing about where it lands.
            _hasObstacleScan = false;
            _plannedPath.Clear();
            _plannedGoal = Vector2.zero;
            _scenarioPathInUse = false;
            _externalController?.ClearCommand();
            _recordedController?.SetTrack(null);
            _neighborPositions.Clear();
            _neighborVelocities.Clear();
            _tempObstacles.Clear();
        }

        public void SwitchController(MovementControllerType newType)
        {
            _currentControllerType = newType;
            _controller?.Reset();
            InitializeController(newType);
        }

        public void SetControllerType(int type)
        {
            MovementControllerType controllerType =
                (MovementControllerType)Mathf.Clamp(type, 0, (int)MovementControllerType.Replay);
            if (_currentControllerType != controllerType)
                SwitchController(controllerType);
        }

        #endregion

        #region Getters

        public float GetConfidence() => _controller?.GetConfidence() ?? 0f;
        public bool HasGoal => _avatar != null && _avatar.hasDestination;
        public Vector2 GoalPosition2D => _avatar != null ? _avatar.currentDestination : Vector2.zero;

        /// <summary>
        /// Radius, in metres, at which this pedestrian perceives the other agents. It is the figure its
        /// social force model works with, read out so the interface draws the detection area the simulation
        /// actually uses instead of one of its own.
        /// </summary>
        public float PerceptionRadius => _config != null ? Mathf.Max(0f, _config.perceptionRadiusAgent) : 0f;

        #endregion

        #region Debug

        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying) return;

            if (_navMeshPath != null && _navMeshPath.corners != null)
            {
                Gizmos.color = Color.black;
                Vector3 lastPos = transform.position;
                foreach (Vector3 position in _navMeshPath.corners)
                {
                    float dist = Vector2.Distance(_currentPosition, new Vector2(position.x, position.z));
                    if (dist > _config.goalReachedDistance)
                    {
                        Debug.DrawLine(lastPos, position, Color.black);
                        Gizmos.DrawCube(position, new Vector3(0.15f, 0.15f, 0.15f));
                        lastPos = position;
                    }
                }
            }

            if (_avatar != null && _avatar.hasDestination)
            {
                Vector3 goal3D = new Vector3(_avatar.currentDestination.x, 0, _avatar.currentDestination.y);
                Gizmos.color = Color.blue;
                Gizmos.DrawCube(goal3D, new Vector3(0.25f, 0.25f, 0.25f));
            }

            if (_currentGoalPoint != Vector2.zero)
            {
                Vector3 nextGoal3D = new Vector3(_currentGoalPoint.x, 0, _currentGoalPoint.y);
                Gizmos.color = Color.red;
                Gizmos.DrawCube(nextGoal3D, new Vector3(0.25f, 0.25f, 0.25f));
            }

            if (_avatar != null)
            {
                Gizmos.color = new Color(0, 1, 0, 0.2f);
                Gizmos.DrawWireSphere(transform.position, _avatar.PersonalSpace);
                Gizmos.color = new Color(0, 0, 1, 0.15f);
                Gizmos.DrawWireSphere(transform.position, _avatar.InteractionRadius);
            }
        }

        #endregion
    }
}
