using UnityEngine;
using System;
using System.Collections.Generic;
using RobotSNAP.Core;

namespace RobotSNAP.Agents
{
    public class Robot : BaseAgent
    {
        [Header("Robot Specific")]
        [SerializeField] private float maxLinearSpeed = 1.0f;
        [SerializeField] private float maxAngularSpeed = 2.0f;
        [SerializeField] private GameObject baseLink;
        [SerializeField] private AgentDetector detector;
        [SerializeField] private bool keepParentAtOrigin = true;

        private float _targetLinearSpeed;
        private float _targetAngularSpeed;
        private ArticulationBody _baseLinkArticulation;
        private Supervisor _supervisor;
        private readonly Queue<Vector3> _routeGoals = new();
        private bool _followingRoute;
        private bool _goalReached;

        /// <summary>
        /// The last point of the route a scenario handed this robot, kept apart from <see cref="_currentGoal"/>.
        ///
        /// <see cref="_currentGoal"/> is the waypoint being walked right now, so it moves along the route. The
        /// mission is over when the *last* point is reached, which is why a driver who takes the robot over
        /// and reaches the destination is judged against this, not against a waypoint the scenario would have
        /// consumed on its way there.
        /// </summary>
        private Vector3 _routeFinalGoal;
        private bool _hasRouteFinalGoal;

        /// <summary>
        /// How close, on the map plane, a robot has to be to a goal to have reached it. Shared by the scenario
        /// driver and the arrival watch so both call "arrived" at the same distance.
        /// </summary>
        private const float ArrivalDistanceMetres = 0.2f;

        /// <summary>
        /// What turns a velocity command into motion: the wheels of a wheeled base, or the kinematic base of
        /// a body that has none. Resolved on the first frame, so a prefab only has to carry the one it uses.
        /// </summary>
        private IRobotDrive _drive;

        /// <summary>
        /// True while the route a scenario gave this robot is the thing steering it.
        ///
        /// A scenario hands every robot its trajectory when it is applied. That trajectory is what the robot
        /// follows when the scenario drives it, and it is not what happens when a person or a client drives
        /// it: without this, a robot left in keyboard mode walked off to its goal the moment a scenario was
        /// loaded, before anybody had touched a key. The route is kept, it simply waits.
        /// </summary>
        private bool _routeOwnedByScenario = true;

        public event Action<float, float> OnVelocityCommandReceived;
        public event Action<Vector3, Quaternion> OnMovementUpdated;

        // Propriétés héritées de BaseAgent (implémentation)
        public override Vector3 Position => baseLink != null ? baseLink.transform.position : transform.position;
        public override Quaternion Rotation => baseLink != null ? baseLink.transform.rotation : transform.rotation;
        public override Vector3 Forward => Rotation * Vector3.forward;
        public override Vector3 Velocity => _baseLinkArticulation != null ? _baseLinkArticulation.linearVelocity : Vector3.zero;
        public override float AngularSpeed => _baseLinkArticulation != null ? _baseLinkArticulation.angularVelocity.y : 0f;

        /// <summary>
        /// Vrai quand le robot a parcouru la route du scénario jusqu'à son dernier point.
        ///
        /// Le panneau d'agent s'en sert pour dire si la mission est finie. Il déduisait auparavant ce
        /// résultat de l'absence d'objectif, ce qui annonçait une mission accomplie dès qu'une prise de
        /// contrôle effaçait l'objectif : c'est ce drapeau, et lui seul, qui veut dire "arrivé".
        /// </summary>
        public override bool GoalReached => _goalReached;

        public Transform RobotTransform => baseLink != null ? baseLink.transform : transform;

        /// <summary>The type this robot drives as, or the default one before a roster applied one.</summary>
        public RobotProfile Profile { get; private set; }

        /// <summary>
        /// How far the origin of the base link sits above the ground the robot rests on.
        ///
        /// A base is authored with its own idea of where its floor is: the wheels of a Freight touch y = 0
        /// under its base link, the wheels of a Jackal hang 6.5 cm below the chassis link. Placing a robot by
        /// its base link would bury one and float the other, so the roster raises it by this much and every
        /// type then stands on the same plane.
        /// </summary>
        public float GroundOffset { get; private set; }

        /// <summary>True while the trajectory of the scenario is what steers this robot.</summary>
        public bool RouteOwnedByScenario => _routeOwnedByScenario;

        // ==================== Unity Lifecycle ====================
        private void Awake()
        {
            _supervisor = Supervisor.Instance;
            EnsureComponents();
            EnforceParentOrigin();
        }

        private void Start() => EnforceParentOrigin();

        private void FixedUpdate()
        {
            if (_drive != null && (_supervisor ??= Supervisor.Instance) != null && !_supervisor.IsPaused)
                _drive.SetRobotVelocity(_targetLinearSpeed, _targetAngularSpeed);

            if (_hasGoal && _routeOwnedByScenario)
                UpdateScenarioMovement();
            // A robot driven by a person or a client no longer walks the route, so nothing above would ever
            // set the flag the mission reads: the driver reaches the destination and the run still ends in a
            // timeout. Watching the same destination here is what makes "arrived" mean the same thing whoever
            // steered.
            else if (!_routeOwnedByScenario && _hasRouteFinalGoal)
                WatchManualArrival();

            OnMovementUpdated?.Invoke(Position, Rotation);
        }

        // ==================== Initialization ====================
        private void EnsureComponents()
        {
            // Find baseLink if not assigned
            if (baseLink == null)
            {
                var child = GetComponentInChildren<ArticulationBody>();
                if (child != null)
                    baseLink = child.gameObject;
                else
                    baseLink = gameObject;
            }

            // Get ArticulationBody on baseLink
            _baseLinkArticulation = baseLink.GetComponent<ArticulationBody>();
            if (_baseLinkArticulation == null)
                Debug.LogWarning($"[Robot] baseLink {baseLink.name} has no ArticulationBody. Velocity will be zero.");

            // Find the chassis if not assigned. A wheeled base drives its wheels; a body without wheels is
            // moved by its base. Both answer the same interface, so nothing below this line has to care which.
            _drive ??= GetComponent<IRobotDrive>() ?? GetComponentInChildren<IRobotDrive>();
            if (_drive == null)
                Debug.LogWarning($"[Robot] No drive found on {name}: it will take commands and never move.");

            if (detector == null)
            {
                detector = GetComponentInChildren<AgentDetector>();
                if (detector == null)
                    Debug.LogWarning($"[Robot] No AgentDetector found on {name}");
            }
        }

        private void EnforceParentOrigin()
        {
            if (keepParentAtOrigin)
            {
                Debug.LogWarning($"[Robot] Enforcing parent origin for {name}. Parent will be reset to (0,0,0).");
                transform.position = Vector3.zero;
                transform.rotation = Quaternion.identity;
            }
        }

        // ==================== Robot Type ====================

        /// <summary>
        /// Makes this instance drive as <paramref name="profile"/>: its footprint, its mass, the speeds it
        /// may be commanded, and the shape of its lidar.
        ///
        /// It is meant to run once, on an instance the roster has just created and before the components of
        /// the prefab had their first frame - the scanner rebuilds its rays here, and the publisher of the
        /// scan reads the rate at its own start.
        ///
        /// The footprint is what the rest of the application reads: the crowd's social force model gives a
        /// Bibus a wider berth than a Kuri, and the spawn check uses the same number to decide whether two
        /// robots may be placed side by side.
        /// </summary>
        public void ApplyProfile(RobotProfile profile)
        {
            if (profile == null)
                return;

            Profile = profile;

            maxLinearSpeed = Mathf.Max(0.01f, profile.MaxLinearSpeed);
            maxAngularSpeed = Mathf.Max(0.01f, profile.MaxAngularSpeed);
            SetRadius(profile.Radius);
            SetMass(profile.Mass);

            ApplyMass();
            ApplyLidar(profile);
        }

        private void ApplyMass()
        {
            if (_baseLinkArticulation != null)
                _baseLinkArticulation.mass = Profile != null ? Profile.Mass : _baseLinkArticulation.mass;
        }

        /// <summary>
        /// Measures where this body rests: the distance from its base link down to the lowest shape the
        /// physics knows about, pictures being the fallback when a body declares no collider at all.
        ///
        /// It is measured on a live instance rather than read from a table because it is a property of the
        /// model, not of its type: two prefabs of the same type can differ, and a robot somebody adds later
        /// gets the right answer without anybody writing its number down.
        /// </summary>
        public float MeasureGroundOffset()
        {
            Transform reference = RobotTransform;
            if (reference == null)
            {
                GroundOffset = 0f;
                return GroundOffset;
            }

            float lowest = float.PositiveInfinity;
            foreach (Collider collider in GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled)
                    continue;

                // A mesh collider whose mesh never came across covers nothing, and its bounds would report
                // the origin as if it were a shape the robot stands on.
                if (collider is MeshCollider mesh && (mesh.sharedMesh == null || mesh.sharedMesh.vertexCount == 0))
                    continue;

                lowest = Mathf.Min(lowest, collider.bounds.min.y);
            }

            if (float.IsInfinity(lowest))
            {
                foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || !renderer.enabled)
                        continue;

                    lowest = Mathf.Min(lowest, renderer.bounds.min.y);
                }
            }

            // How far the base link floats above the ground this body rests on: the distance from the lowest
            // shape up to the origin of the base. A body whose wheels hang below its chassis link has a
            // positive offset and is raised by that much, instead of being dropped through the floor.
            GroundOffset = float.IsInfinity(lowest) ? 0f : reference.position.y - lowest;
            return GroundOffset;
        }

        private void ApplyLidar(RobotProfile profile)
        {
            RaycastLaserScanner scanner = GetLaserScanner();
            if (scanner != null)
            {
                scanner.samples = profile.LidarRays;
                scanner.angle_min = profile.LidarAngleMin;
                scanner.angle_max = profile.LidarAngleMax;
                scanner.range_max = profile.LidarRange;

                // The height of the laser plane is what decides whether a wall, a table or a pedestrian
                // blocks the beam, so the profile gives the height above the ground and the offset the
                // scanner carries is what remains once its mount has been taken into account.
                float mountHeight = scanner.transform.position.y - Position.y;
                scanner.laserHeight = Mathf.Max(0f, profile.LidarHeight - mountHeight);

                // Rebuilds the rays and the buffers the scan writes into.
                scanner.Init();
            }

            LaserScanPublisher publisher = GetLaserPublisher();
            if (publisher != null)
                publisher.SetPublishFrequency(profile.LidarFrequencyHz);
        }

        // ==================== Scenario Movement ====================

        /// <summary>
        /// Hands the trajectory of the scenario back to the robot, or takes it away from it.
        ///
        /// A scenario gives every robot its route when it is applied. That route is what steers the robot
        /// while the scenario owns it - the mode the editor calls "scenario" - and it is deliberately not
        /// what steers it while a person or a client is driving: a robot left in keyboard mode used to walk
        /// off to its goal the instant a scenario was loaded, before anybody had touched anything.
        /// </summary>
        public void SetRouteOwnedByScenario(bool owned)
        {
            if (_routeOwnedByScenario == owned)
                return;

            _routeOwnedByScenario = owned;
            if (!owned)
                Stop();
        }

        private void UpdateScenarioMovement()
        {
            // The goal of a scenario is a point on the map, and the map is a plane: measuring the distance in
            // three dimensions would make a robot whose base link floats above the floor - any type that is
            // not the base one - aim at a point below its goal and slow down before ever reaching it.
            Vector3 here = Position;
            Vector3 planar = new Vector3(_currentGoal.x - here.x, 0f, _currentGoal.z - here.z);
            Vector3 direction = planar.sqrMagnitude > 0.0001f ? planar.normalized : Forward;
            float distance = planar.magnitude;

            float targetSpeed = _currentSpeed;
            if (distance < 1.0f)
                targetSpeed = _currentSpeed * (distance / 1.0f);

            // SignedAngle repond dans le sens d'Unity : positif quand le but est a droite, c'est-a-dire
            // quand le lacet Unity doit augmenter. La consigne de vitesse angulaire des conduites est en
            // convention ROS, positive vers la gauche, donc le signe se retourne ici. Sans cela le robot
            // s'eloigne du but qu'il doit rejoindre.
            float angleToGoal = -Vector3.SignedAngle(Forward, direction, Vector3.up);
            float angularSpeed = Mathf.Clamp(angleToGoal * 2.0f, -maxAngularSpeed, maxAngularSpeed);

            SetVelocity(targetSpeed, angularSpeed);

            if (distance < ArrivalDistanceMetres)
            {
                if (_followingRoute && _routeGoals.Count > 0)
                {
                    SetNextRouteGoal();
                }
                else
                {
                    _followingRoute = false;
                    _hasGoal = false;
                    // Le dernier point de la route est atteint : c'est la seule façon d'arriver.
                    _goalReached = true;
                    Stop();
                }
            }
        }

        /// <summary>
        /// Latches "arrived" for a robot steered by somebody else, once its position on the map plane is at the
        /// destination the scenario set.
        ///
        /// Only the destination counts, never the velocity: a driver who overshoots and comes back has still
        /// arrived, and one who crosses the circle on the way past has arrived too - which is the rule the
        /// scenario driver already follows. Latching is deliberate, so a robot driven away after reaching the
        /// goal keeps the mission it completed.
        /// </summary>
        private void WatchManualArrival()
        {
            if (_goalReached)
                return;

            Vector3 here = Position;
            float dx = _routeFinalGoal.x - here.x;
            float dz = _routeFinalGoal.z - here.z;
            if (dx * dx + dz * dz > ArrivalDistanceMetres * ArrivalDistanceMetres)
                return;

            _goalReached = true;
            _hasGoal = false;
        }

        public override void SetGoal(Vector3 goal)
        {
            _routeGoals.Clear();
            _followingRoute = false;
            _goalReached = false;
            _routeFinalGoal = goal;
            _hasRouteFinalGoal = true;
            base.SetGoal(goal);
        }

        public void SetGoals(IEnumerable<Vector3> goals)
        {
            _routeGoals.Clear();
            _goalReached = false;
            _hasRouteFinalGoal = false;
            if (goals != null)
            {
                foreach (Vector3 goal in goals)
                {
                    _routeGoals.Enqueue(goal);
                    _routeFinalGoal = goal;
                    _hasRouteFinalGoal = true;
                }
            }

            _followingRoute = _routeGoals.Count > 0;
            if (_followingRoute)
                SetNextRouteGoal();
            else
                ClearGoal();
        }

        public override void ClearGoal()
        {
            _routeGoals.Clear();
            _followingRoute = false;
            _goalReached = false;
            _hasRouteFinalGoal = false;
            base.ClearGoal();
        }

        private void SetNextRouteGoal()
        {
            if (_routeGoals.Count == 0)
                return;
            base.SetGoal(_routeGoals.Dequeue());
        }

        // ==================== Public API - Movement Control ====================
        public void SetVelocity(float linearSpeed, float angularSpeed)
        {
            _targetLinearSpeed = Mathf.Clamp(linearSpeed, -maxLinearSpeed, maxLinearSpeed);
            _targetAngularSpeed = Mathf.Clamp(angularSpeed, -maxAngularSpeed, maxAngularSpeed);
            OnVelocityCommandReceived?.Invoke(_targetLinearSpeed, _targetAngularSpeed);
        }

        public override void Stop()
        {
            _targetLinearSpeed = 0f;
            _targetAngularSpeed = 0f;
            if (_drive != null)
                _drive.SetRobotVelocity(0f, 0f);
        }

        public override void Reset()
        {
            Stop();
            _routeGoals.Clear();
            _followingRoute = false;
            ClearGoal();
            EnforceParentOrigin();
            if (_drive != null)
                _drive.ResetDrives();
        }

        // ==================== Public API - Teleportation ====================
        public void SetBaseLinkPose(Vector3 position, Quaternion rotation)
        {
            if (baseLink == null)
            {
                Debug.LogError("[Robot] baseLink is null, cannot set pose.");
                return;
            }
            ArticulationBody ab = baseLink.GetComponent<ArticulationBody>();
            if (ab == null)
            {
                Debug.LogError("[Robot] baseLink has no ArticulationBody, cannot set pose.");
                return;
            }
            ab.TeleportRoot(position, rotation);
            // A teleported articulation is settled where it was put, and a drive command on a body that is
            // asleep is ignored: the robot of a scenario would sit at its start with its wheels commanded.
            ab.WakeUp();

            // A chassis that integrates its own pose - a body without wheels - has to be told where it was
            // put. Left to read its transform, it would read the pose it had before the teleport, which is
            // only written at the next physics step, and walk the robot back to where it came from.
            if (_drive is KinematicBaseDrive kinematic)
                kinematic.SetBasePose(position, rotation);

            Stop();
            EnforceParentOrigin();
        }

        public void SetBaseLinkPosition(Vector3 position)
        {
            if (baseLink == null) return;
            Debug.Log($"[Robot] Teleporting baseLink to {position}");
            ArticulationBody ab = baseLink.GetComponent<ArticulationBody>();
            if (ab == null)
            {
                Debug.LogError("[Robot] baseLink has no ArticulationBody, cannot set position.");
                return;
            }
            ab.TeleportRoot(position, baseLink.transform.rotation);
            ab.WakeUp();
            if (_drive is KinematicBaseDrive movedByPosition)
                movedByPosition.SetBasePose(position, baseLink.transform.rotation);
            Stop();
            EnforceParentOrigin();
        }

        public void SetBaseLinkRotation(Quaternion rotation)
        {
            if (baseLink == null) return;
            ArticulationBody ab = baseLink.GetComponent<ArticulationBody>();
            if (ab == null)
            {
                Debug.LogError("[Robot] baseLink has no ArticulationBody, cannot set rotation.");
                return;
            }
            ab.TeleportRoot(baseLink.transform.position, rotation);
            ab.WakeUp();
            if (_drive is KinematicBaseDrive turned)
                turned.SetBasePose(baseLink.transform.position, rotation);
            Stop();
            EnforceParentOrigin();
        }

        public void SetPose(Vector3 position, Quaternion rotation) => SetBaseLinkPose(position, rotation);

        // ==================== IAgent Methods Overrides ====================
        public override void SetBehavior(string behavior)
        {
            base.SetBehavior(behavior); // met à jour _currentBehavior
            switch (behavior.ToLower())
            {
                case "cautious":
                    maxLinearSpeed = 0.8f;
                    break;
                case "assertive":
                    maxLinearSpeed = 1.5f;
                    break;
                case "socially_aware":
                    maxLinearSpeed = 1.2f;
                    break;
                default:
                    maxLinearSpeed = 1.2f;
                    break;
            }
        }

        // ==================== Sensors ====================
        public void SetLaserSample(int laserSample)
        {
            var laser = GetComponentInChildren<RaycastLaserScanner>();
            if (laser != null)
            {
                laser.samples = laserSample;
                laser.Init();
            }
        }

        public RaycastLaserScanner GetLaserScanner() => GetComponentInChildren<RaycastLaserScanner>();
        public LaserScanPublisher GetLaserPublisher() => GetComponentInChildren<LaserScanPublisher>();
        public AgentDetector GetAgentDetector() => detector;

        // ==================== Editor Utilities ====================
        [ContextMenu("Stop")]
        private void EditorStop() => Stop();

        [ContextMenu("Reset")]
        private void EditorReset() => Reset();

        [ContextMenu("Set Test Goal")]
        private void EditorSetTestGoal() => SetGoal(new Vector3(5, 0, 5));

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(Position, radius);
            if (_hasGoal && Application.isPlaying)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(Position, _currentGoal);
                Gizmos.DrawWireSphere(_currentGoal, 0.2f);
            }
        }

        public override string ToString()
        {
            return $"{gameObject.name} (Behavior: {_currentBehavior}, Speed: {_currentSpeed}, Goal: {(_hasGoal ? _currentGoal.ToString() : "none")})";
        }
    }
}
