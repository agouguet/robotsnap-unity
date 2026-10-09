using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;
using RobotSNAP.Agents;
using RobotSNAP.CameraControl;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using UnityEngine;
using NavMsgs = RosMessageTypes.Nav;

namespace RobotSNAP.ROS
{
    /// <summary>
    /// Publishes the whole running application on ROS: the occupancy grid of the applied scenario on
    /// <c>/map</c>, a JSON snapshot of the runtime state on <c>/simulation/state</c>, and the
    /// <c>/reset_done</c> handshake that tells a client the world is ready, so a client can follow a
    /// session without reading the Unity scene.
    ///
    /// One component owns those streams because they answer one question together, and because they do not
    /// cost the same: the state snapshot is small and runs at a steady rate while the grid is heavy and
    /// changes only when a scenario is applied, so the grid is built once per applied scenario instead of
    /// once per frame and published at most once per second.
    ///
    /// The state stream is paced in simulated seconds and the grid in wall seconds, because only the first
    /// describes the moving world: a client that raises the session's time scale gets its state at the same
    /// rate per simulated second it always had, while the grid keeps its bounded cost. The state stream is
    /// also the one a client needs while nothing is moving, so it falls back to the wall clock whenever the
    /// simulation is held at a zero time scale - a lockstep gate between two releases, a stopped session. See
    /// <see cref="Update"/> and <see cref="FixedUpdate"/>.
    ///
    /// Nothing here may throw when no ROS server is listening, which is how this project usually runs: the
    /// connector raises an exception on a topic that has no registered publisher, so every publish goes
    /// through <see cref="CanPublish"/> first.
    /// </summary>
    public class SimulationStatePublisher : MonoBehaviour
    {
        [Header("ROS Configuration")]
        [Tooltip("Take the topic prefix from EnvROS, like the other publishers of this folder.")]
        [SerializeField] private bool autoDetectPrefix = true;

        [Tooltip("Prefix used when auto detection is off.")]
        [SerializeField] private string customPrefix = "";

        [Tooltip("Rate of the JSON state snapshot, in snapshots per simulated second, so it follows the session's time scale. Zero disables that stream.")]
        [SerializeField] private float publishFrequencyHz = 10f;

        [Tooltip("Shortest delay between two publishes of the occupancy grid, in seconds of wall time. The whole grid is serialized on every publish and the grid does not move with the world, so this one interval deliberately does not follow the time scale; it is floored at one second.")]
        [SerializeField] private float mapIntervalSeconds = 1f;

        [Header("Topic Names (the EnvROS prefix is prepended)")]
        [Tooltip("Occupancy grid of the applied scenario, as nav_msgs/OccupancyGrid.")]
        [SerializeField] private string mapTopic = RobotSNAPTopics.Map;
        [Tooltip("World ready handshake, published as std_msgs/Bool every time a scenario is applied.")]
        [SerializeField] private string resetDoneTopic = RobotSNAPTopics.ResetDone;
        [SerializeField] private string stateTopic = RobotSNAPTopics.SimulationState;

        [Header("Frame ID")]
        [Tooltip("Frame written in every header this component stamps.")]
        [SerializeField] private string frameId = "/map";

        [Header("Debug")]
        [SerializeField] private bool logPublishEvents = false;

        [Header("References")]
        [SerializeField] private EnvROS _envROS;

        // ROS side
        private ROSConnection _ros;
        private string _prefix = "";
        private string _mapTopicName;
        private string _resetDoneTopicName;
        private string _stateTopicName;
        private bool _initialized;

        // Application side: the references are refreshed lazily because the environments - and the EnvROS
        // that lives inside them - are destroyed and rebuilt each time a scenario is loaded.
        private ScenarioManager _scenarioManager;
        private Clock _clock;
        private CameraController _cameraController;
        private SimulationState? _lastState;

        // Pacing. The state snapshot describes the moving world, so its interval counts simulated seconds:
        // the rate above is a rate of the simulation, and the world is sampled the same number of times per
        // simulated second whatever the time scale is. The grid is paced on the wall instead, because it
        // describes the applied scenario rather than the moving world and serializing it is the expensive half
        // of this component - a session running ten times faster must not serialize ten grids a second for a
        // grid that only changes when a scenario is applied. Neither interval stops when the simulation clock
        // is paused: the pause freezes the agents, not Time.timeScale, so both streams keep the client current.
        private float _stateInterval;
        private float _mapInterval;
        private float _nextStateTime;
        private float _stoppedTimer;
        private bool _wasRunning = true;
        private float _mapTimer;

        // Occupancy grid of the applied scenario, rebuilt only on application and never per frame.
        private sbyte[] _mapData;
        private int _mapWidth;
        private int _mapHeight;
        private float _mapResolution;
        private PoseMsg _mapOrigin = new PoseMsg();
        private bool _mapPublishPending;

        #region Unity Lifecycle

        private void Start()
        {
            Initialize();
        }

        private void Update()
        {
            if (!_initialized) return;

            _mapTimer += Time.unscaledDeltaTime;
            if (_mapPublishPending || _mapTimer >= _mapInterval)
            {
                _mapTimer = 0f;
                _mapPublishPending = false;
                PublishMap();
            }

            // A stopped world is still a world a client has to be able to read. The fixed step above is where
            // this stream belongs while the simulation moves, but it is also the loop a zero time scale stops
            // - and a zero time scale is exactly what the pacing gate holds between two releases, and what a
            // session left behind by a killed client keeps. A client that cannot read the state cannot tell a
            // stopped session from a dead one, cannot place anything it is shown, and cannot ask for the
            // scenario it needs: the stream it is waiting for is the one the stop silenced. So while the world
            // is at a zero scale the same snapshot goes out on the wall clock, at the rate the running world
            // would have used. The two rules never both fire: the wall one needs a zero scale and the fixed
            // one needs a scale above zero, so the stream keeps one publisher and no doubled message.
            // A world that stops has to say so once, at once. A client holding a lockstep session reads this
            // stream to learn that the period it released is spent - the fixed step it would otherwise be read
            // from is the loop the stop just ended - so a snapshot published only on the next tick makes every
            // released period cost the publishing interval on top of the period itself: a tenth of a second of
            // simulation, which at one times speed is a tenth of a second of wall time added to every step,
            // and a world that visibly hitches between two decisions. The edge publish is what bounds a step
            // by the period it asked for rather than by the rate of this stream. The wall heartbeat that
            // follows is for a world that stays stopped - a client that has gone, a session left paused - where
            // there is no edge left to publish on and the snapshot has to keep saying where everything is.
            bool running = Time.timeScale > 0f;
            if (running)
            {
                _wasRunning = true;
                _stoppedTimer = 0f;
                return;
            }

            bool justStopped = _wasRunning;
            _wasRunning = false;
            _stoppedTimer += Time.unscaledDeltaTime;
            if (!justStopped && _stoppedTimer < _stateInterval) return;

            _stoppedTimer = 0f;
            PublishState();
        }

        /// <summary>
        /// The state snapshot describes the moving world, so it is paced on the simulation clock and published
        /// from the fixed step rather than the frame. The frame rate is what a raised time scale squeezes
        /// first: at five times speed a 60 fps session asks for fifty snapshots per second of wall time, and a
        /// stream published once per frame cannot answer more than sixty however fast the world is going, so
        /// the rate a client declared would quietly fall as the session sped up. The physics loop runs several
        /// times per frame and keeps it. The grid is not published here: it is wall-paced, see Update.
        /// </summary>
        private void FixedUpdate()
        {
            if (!_initialized) return;

            if (Time.fixedTime < _nextStateTime) return;

            _nextStateTime = Time.fixedTime + _stateInterval;
            PublishState();
        }

        private void OnDestroy()
        {
            if (_scenarioManager != null)
            {
                _scenarioManager.OnScenarioLoaded -= OnScenarioLoaded;
                _scenarioManager.OnScenarioApplied -= OnScenarioApplied;
            }

            EventBus.Instance.Unsubscribe<SimulationStateChangedEvent>(OnSimulationStateChanged);
        }

        #endregion

        #region Initialization

        /// <summary>
        /// The snapshot that <c>/simulation/state</c> carries, without waiting for the next tick. Handy to
        /// check the bridge from the editor, and the only way to see the payload when nothing is listening.
        /// </summary>
        public string CurrentStateJson => BuildStateJson();

        /// <summary>
        /// Resolves the connection, registers the three publishers and hooks the events whose payload the
        /// streams need: the applied scenario for the grid and the reset handshake, and the state the
        /// manager publishes for the JSON snapshot. Called from Start, and once more from the inspector
        /// menu to retry by hand.
        /// </summary>
        public void Initialize()
        {
            _ros = ROSConnection.GetOrCreateInstance();
            if (_ros == null)
            {
                Debug.LogError($"[{name}] ROSConnection unavailable, simulation state will not be published");
                enabled = false;
                return;
            }

            ResolveTopics(force: true);

            _scenarioManager ??= FindAnyObjectByType<ScenarioManager>();
            if (_scenarioManager != null)
            {
                _scenarioManager.OnScenarioApplied -= OnScenarioApplied;
                _scenarioManager.OnScenarioApplied += OnScenarioApplied;
                _scenarioManager.OnScenarioLoaded -= OnScenarioLoaded;
                _scenarioManager.OnScenarioLoaded += OnScenarioLoaded;
            }

            _clock ??= Clock.Instance;

            // Timing is set here so a rate edited in the inspector is honoured on the next initialize, and
            // a rate of zero parks its stream instead of publishing it every frame.
            _stateInterval = Interval(1f / Mathf.Max(0f, RobotSNAPTopics.ResolveFrequency(publishFrequencyHz)));
            _mapInterval = Mathf.Max(1f, mapIntervalSeconds);
            _nextStateTime = Time.fixedTime + _stateInterval;

            // The grid of the scenario that is already loaded goes out on the first tick of the loop.
            RebuildMapCache();
            _mapPublishPending = true;

            EventBus.Instance.Unsubscribe<SimulationStateChangedEvent>(OnSimulationStateChanged);
            EventBus.Instance.Subscribe<SimulationStateChangedEvent>(OnSimulationStateChanged);

            _initialized = true;

            if (logPublishEvents)
            {
                Debug.Log($"[{name}] Publishing simulation state:\n" +
                          $"  Map: {_mapTopicName} every {_mapInterval} s\n" +
                          $"  Reset done: {_resetDoneTopicName}\n" +
                          $"  State: {_stateTopicName} at {_stateInterval} s");
            }
        }

        /// <summary>Interval of a stream, where a zero rate means "never" instead of "every frame".</summary>
        private static float Interval(float seconds)
        {
            return seconds <= 0f ? float.PositiveInfinity : seconds;
        }

        /// <summary>
        /// Builds the three topic names from the EnvROS prefix and registers them. Loading a scenario
        /// destroys and rebuilds the environments, so the EnvROS instance - and the prefix it carries - can
        /// change mid-session. The names are rebuilt, and the topics re-registered, only when it did change,
        /// because the connector warns about a topic that is registered twice.
        /// </summary>
        private void ResolveTopics(bool force)
        {
            // A destroyed EnvROS compares equal to null, so this looks the new one up after a scenario load.
            _envROS ??= FindAnyObjectByType<EnvROS>();

            string prefix = "";
            if (autoDetectPrefix && _envROS != null)
                prefix = _envROS.Prefix;
            else if (!string.IsNullOrEmpty(customPrefix))
                prefix = customPrefix;

            prefix = (prefix ?? "").Trim('/');
            if (!force && prefix == _prefix && _stateTopicName != null)
                return;

            _prefix = prefix;
            _mapTopicName = BuildTopic(prefix, RobotSNAPTopics.Resolve(RosTopicSlot.Map, mapTopic));
            _resetDoneTopicName = BuildTopic(prefix, RobotSNAPTopics.Resolve(RosTopicSlot.ResetDone, resetDoneTopic));
            _stateTopicName = BuildTopic(prefix, RobotSNAPTopics.Resolve(RosTopicSlot.SimulationState, stateTopic));

            RegisterTopic<NavMsgs.OccupancyGridMsg>(_mapTopicName);
            RegisterTopic<BoolMsg>(_resetDoneTopicName);
            RegisterTopic<StringMsg>(_stateTopicName);
        }

        /// <summary>
        /// Registers a topic unless it already carries a publisher: the connector warns on a topic
        /// registered twice, and initializing the component again must stay a quiet operation.
        /// </summary>
        private void RegisterTopic<T>(string topicName) where T : Message
        {
            if (_ros == null || string.IsNullOrEmpty(topicName)) return;

            RosTopicState topic = _ros.GetTopic(topicName);
            if (topic != null && topic.IsPublisher) return;

            _ros.RegisterPublisher<T>(topicName);
        }

        /// <summary>
        /// Topic name in the house shape (/prefix/topic), built by the one rule of the project so this
        /// publisher and the client cannot disagree on a name. See <see cref="RobotSNAPTopics.Full"/>.
        /// </summary>
        private static string BuildTopic(string prefix, string topic)
            => RobotSNAPTopics.Full(topic, prefix);

        /// <summary>Frame id of every header this component stamps, prefixed the same way a topic is.</summary>
        private string FullFrameId => RobotSNAPTopics.Full(frameId, _prefix);

        /// <summary>
        /// True when a topic can be published to. The connector throws on a topic that holds no publisher,
        /// which is exactly the state of a session running without a ROS server, so asking first keeps that
        /// case quiet.
        /// </summary>
        private bool CanPublish(string topicName)
        {
            if (_ros == null || string.IsNullOrEmpty(topicName)) return false;

            RosTopicState topic = _ros.GetTopic(topicName);
            return topic != null && topic.IsPublisher;
        }

        #endregion

        #region Scenario Event

        private void OnSimulationStateChanged(SimulationStateChangedEvent evt)
        {
            _lastState = evt.NewState;
        }

        /// <summary>
        /// A new scenario replaces the grid a few frames after this event - the map is built with the
        /// environment - so the previous grid is dropped now rather than published once more as if it
        /// belonged to the scenario being loaded.
        /// </summary>
        private void OnScenarioLoaded(ScenarioData scenario)
        {
            if (!_initialized) return;

            ClearMapCache();
            _mapPublishPending = true;
        }

        /// <summary>
        /// A new scenario means a new grid and a world that is ready to be driven: the grid is sampled once
        /// here rather than frame after frame, and the reset handshake goes out on the same event, right
        /// before the grid is published on the next tick.
        /// </summary>
        private void OnScenarioApplied(ScenarioData scenario)
        {
            if (!_initialized) return;

            ResolveTopics(force: false);
            RebuildMapCache();
            _mapPublishPending = true;
            _mapTimer = 0f;

            PublishResetDone();
        }

        /// <summary>
        /// Tells a client that the world of the applied scenario is ready. The flag is not a status: a
        /// client that resets waits for this message to know the new world, its grid and its agents exist.
        /// </summary>
        private void PublishResetDone()
        {
            if (!CanPublish(_resetDoneTopicName)) return;

            _ros.Publish(_resetDoneTopicName, new BoolMsg(true));

            if (logPublishEvents)
                Debug.Log($"[{name}] Published reset done to {_resetDoneTopicName}");
        }

        #endregion

        #region Scenario Helpers

        // What the loaded scenario declares - its environment, the poses it authored and the groups of its
        // crowd - which describe the session rather than its live state, and which /simulation/state carries
        // so a client needs no other stream to name the scenario.

        /// <summary>
        /// The environment the application names for a scenario: its map when it has one, its declared
        /// location otherwise. Same value the scenario browser shows under "Environment".
        /// </summary>
        private static string EnvironmentOf(ScenarioData scenario)
        {
            if (scenario == null) return "";
            if (!string.IsNullOrEmpty(scenario.MapImage)) return scenario.MapImage;
            return scenario.Info?.Location ?? "";
        }

        /// <summary>Pose of an authored scenario point in the ROS frame, null when the scenario names none.</summary>
        private object ScenarioPointJson(ScenarioData scenario, string reference)
        {
            ScenarioLoader loader = _scenarioManager != null ? _scenarioManager.Loader : null;
            if (scenario == null || loader == null || string.IsNullOrEmpty(reference))
                return null;

            (Vector3 position, Quaternion rotation) = loader.GetPositionAndRotation(scenario, reference);
            return PoseJson(position, rotation);
        }

        /// <summary>Groups the scenario declares, read from the authored group ids of its humans.</summary>
        private static int CountGroups(ScenarioData scenario)
        {
            if (scenario?.Humans == null) return 0;
            return scenario.Humans
                .Where(human => human != null && !string.IsNullOrEmpty(human.Group))
                .Select(human => human.Group)
                .Distinct()
                .Count();
        }

        #endregion

        #region Occupancy Grid

        /// <summary>
        /// Occupancy grid of the applied scenario, as a standard <c>nav_msgs/OccupancyGrid</c> on
        /// <c>/map</c>: index = row * width + column, a column counting along the ROS +x axis and a row
        /// along the ROS +y one, both growing away from <c>info.origin</c>, the pose of cell (0, 0). That
        /// origin is the (min ROS x, min ROS y) corner of the map, which is the occupancy image's
        /// (max Unity x, min Unity z) corner.
        ///
        /// Walls are 100 and free cells 0, the values the rest of the project uses for occupancy. The
        /// message reuses the cached array, which is only ever replaced when a scenario is applied, so a
        /// message still waiting in the connector cannot see its data change under it.
        /// </summary>
        private void PublishMap()
        {
            if (!CanPublish(_mapTopicName)) return;

            // A scenario that is loaded but not applied still builds its map, and nothing signals that: the
            // cache is filled here the first time a runtime grid is available, and left alone afterwards.
            // The grid is already sampled by the environment builder, so this costs a null check per tick.
            if (_mapData == null && ScenarioNavigation.IsAvailable)
                RebuildMapCache();

            if (_mapData == null || _mapWidth <= 0 || _mapHeight <= 0) return;

            var msg = new NavMsgs.OccupancyGridMsg
            {
                info = new NavMsgs.MapMetaDataMsg
                {
                    resolution = _mapResolution,
                    width = (uint)_mapWidth,
                    height = (uint)_mapHeight,
                    origin = _mapOrigin
                },
                data = _mapData
            };

            ROSTimeUtils.UpdateHeader(msg.header);
            msg.header.frame_id = FullFrameId;

            _ros.Publish(_mapTopicName, msg);

            if (logPublishEvents)
                Debug.Log($"[{name}] Published map to {_mapTopicName}: {_mapWidth}x{_mapHeight} at {_mapResolution} m/cell");
        }

        /// <summary>
        /// Samples the grid of the scenario being applied, once. The grid the agents plan on is the
        /// authority - the environment builder builds it from the very image the map is made of - and the
        /// loader's map asset is only sampled when the scenario has no runtime grid at all.
        /// </summary>
        private void RebuildMapCache()
        {
            ClearMapCache();

            OccupancyGrid grid = ScenarioNavigation.Obstacles;
            if (grid == null || !grid.IsValid)
                grid = BuildGridFromMapAsset();

            if (grid == null || !grid.IsValid) return;

            // Both axes are sampled with the same cell count, so one resolution describes the grid; the cell
            // along the published x axis is named when rounding makes the two differ.
            _mapResolution = grid.CellSizeZ;
            _mapOrigin = MapOrigin(grid);

            // The cells, and the two dimensions the message counts along +x and +y.
            _mapData = MapDataInRosOrder(grid, out _mapWidth, out _mapHeight);
        }

        /// <summary>
        /// The cells of a walkability grid in the order the message carries them, with the two dimensions
        /// it counts along +x and +y.
        ///
        /// ROS x is Unity z and ROS y is the negated Unity x, so the published column counts the grid's
        /// second index - the one that walks the image towards +z - and the published row counts its first,
        /// which walks it towards -x. A grid of <c>w</c> by <c>h</c> cells is therefore published as
        /// <c>h</c> by <c>w</c>.
        /// </summary>
        public static sbyte[] MapDataInRosOrder(OccupancyGrid grid, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (grid == null || !grid.IsValid)
                return null;

            width = grid.Height;
            height = grid.Width;

            var data = new sbyte[grid.CellCount];
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                    data[row * width + column] = grid.IsWalkable(row, column) ? (sbyte)0 : (sbyte)100;
            }

            return data;
        }

        /// <summary>
        /// Forgets the cached grid, which the map stream reads as "nothing to publish until a new one is
        /// sampled".
        /// </summary>
        private void ClearMapCache()
        {
            _mapData = null;
            _mapWidth = 0;
            _mapHeight = 0;
            _mapResolution = 0f;
            _mapOrigin = new PoseMsg();
        }

        /// <summary>
        /// Samples the occupancy image of the scenario when no runtime grid exists. The texture belongs to
        /// this call - the loader creates it - so it is released once sampled instead of leaking one per
        /// applied scenario.
        /// </summary>
        private OccupancyGrid BuildGridFromMapAsset()
        {
            ScenarioManager manager = _scenarioManager ??= FindAnyObjectByType<ScenarioManager>();
            ScenarioData scenario = manager != null ? manager.CurrentScenarioData : null;
            ScenarioLoader loader = manager != null ? manager.Loader : null;

            if (scenario == null || loader == null || string.IsNullOrEmpty(scenario.MapImage))
                return null;

            MapAsset asset = loader.LoadMap(scenario.MapImage);
            if (asset == null || asset.Kind != MapAssetKind.Image || asset.Texture == null)
                return null;

            OccupancyGrid grid = OccupancyGrid.FromTexture(asset.Texture, asset.Bounds, ScenarioNavigation.Resolution);
            Destroy(asset.Texture);
            return grid;
        }

        /// <summary>
        /// Pose of the corner cell (0, 0) starts at, in the ROS frame the other publishers use. The grid
        /// follows the occupancy image, whose first pixel sits at the (max x, min z) corner of the bounds,
        /// so the origin is that corner rather than the bottom-left corner of a ROS map.
        /// </summary>
        private static PoseMsg MapOrigin(OccupancyGrid grid)
        {
            Bounds bounds = grid.WorldBounds;
            return Util.Geometry.GetMPose(new Vector3(bounds.max.x, 0f, bounds.min.z), Quaternion.identity);
        }

        #endregion

        #region Human Helpers

        /// <summary>
        /// Humans of the scene, pooled ones excluded: a pooled agent is not being simulated and would
        /// otherwise be published as a person standing at the pool's origin.
        /// </summary>
        private static HumanAgent[] FindHumans()
        {
            HumanAgent[] humans = FindObjectsByType<HumanAgent>(FindObjectsInactive.Exclude);
            return humans.OrderBy(human => human.agentId).ToArray();
        }

        #endregion

        #region Runtime State

        /// <summary>
        /// JSON snapshot of everything the session is: the run state, the clock, what the applied scenario
        /// declares, the placement of the published grid, the crowd, the robot and what the camera looks at.
        ///
        /// Keys, with every pose in the ROS frame the other publishers use (x forward, y left, z up) and
        /// yaw in radians:
        ///   simulation_state       "idle", "ready", "running" or "paused", as the ScenarioManager publishes it
        ///   playing                true while a scenario is applied and the clock runs
        ///   paused                 true while the simulation clock is paused
        ///   stopped                true while no scenario is applied (idle or ready)
        ///   scenario_applied       true while the agents of the current scenario are in the scene
        ///   sim_time_seconds       time of the simulation clock, in seconds, null without a clock
        ///   time_scale             time scale applied to the simulation clock, null without a clock
        ///   held                   true while the world is effectively stopped (a lockstep client between
        ///                          two releases, a frozen session), false otherwise
        ///   scenario_id            name the scenario was loaded under (its YAML file)
        ///   scenario_name          name declared inside the scenario, null when none is loaded
        ///   environment            map of the scenario, or its declared location
        ///   map_name               map identifier authored in the scenario
        ///   robot_start_pose       start the scenario authored for the PRIMARY robot {x, y, z, yaw}, null without one
        ///   robot_target_pose      target the scenario authored for the PRIMARY robot {x, y, z, yaw}, null without one
        ///   num_groups             groups the scenario declares, read from the group ids of its humans
        ///   map_width              width of the occupancy grid, in cells, 0 without a grid
        ///   map_height             height of the occupancy grid, in cells, 0 without a grid
        ///   map_resolution         metres per grid cell, 0 without a grid
        ///   map_origin_x           x of the origin of the published grid, in the ROS frame, 0 without a grid
        ///   map_origin_y           y of the origin of the published grid, in the ROS frame, 0 without a grid
        ///   human_count            humans currently simulated
        ///   humans                 one entry per human: id, x, y, z, vx, vy, vz, speed, goal, group,
        ///                          controller, end_behavior
        ///   robots                 one entry per live robot, in the scenario's order: id, type, is_primary,
        ///                          x, y, z, yaw, has_goal, goal, start_pose and target_pose; empty without a robot
        ///   robot_count            robots the session runs, 0 when the scene has none
        ///   robot                  PRIMARY robot pose {x, y, z, yaw}, null when the scene has no robot
        ///   robot_has_goal         true while the PRIMARY robot drives towards a goal
        ///   robot_goal             PRIMARY robot goal {x, y, z}, null when it has none
        ///   camera_focus           name of the agent the camera follows, null when it follows none
        ///   camera_focus_is_robot  true when the followed agent is the robot
        ///   camera_focus_is_human  true when the followed agent is a human
        ///   camera_focus_agent_id  id of the followed human, null otherwise
        ///   camera_tool            "select", "move", "rotate" or "zoom", the tool of the view
        ///   camera_mode            "free", "topdown", "firstperson", "thirdperson" or "orbit"
        ///   camera_pose            camera pose {x, y, z, yaw}, null without a camera
        ///   refreshed_at           unix epoch seconds (UTC, fractional) this snapshot was built at
        /// </summary>
        private string BuildStateJson()
        {
            ScenarioManager manager = _scenarioManager ??= FindAnyObjectByType<ScenarioManager>();
            Supervisor supervisor = Supervisor.Instance;
            _clock ??= Clock.Instance;
            CameraController camera = _cameraController ??= FindAnyObjectByType<CameraController>();
            List<Robot> robots = ResolveRobots();
            RobotRoster roster = RobotRoster.Current;
            Robot primary = PrimaryOf(roster, robots);

            ScenarioData scenario = manager != null ? manager.CurrentScenarioData : null;
            List<RobotScenarioConfig> robotConfigs = scenario != null ? scenario.NormalizedRobots() : null;
            // The legacy poses keep describing the primary robot: its entry when the scenario names the fleet,
            // and the single "robot" section the snapshot has always read when no live robot carries an id.
            RobotScenarioConfig primaryConfig = ConfigOf(robotConfigs, IdOf(roster, primary)) ?? scenario?.Robot;
            SimulationState state = ResolveState(manager, supervisor);
            bool paused = supervisor != null ? supervisor.IsPaused : (_clock != null && _clock.IsPaused);
            bool applied = state == SimulationState.Running || state == SimulationState.Paused;

            Transform focus = camera != null ? camera.GetCurrentFollowTarget() : null;
            HumanAgent focusedHuman = focus != null ? focus.GetComponentInParent<HumanAgent>() : null;

            var payload = new Dictionary<string, object>
            {
                { "simulation_state", StateName(state) },
                { "playing", state == SimulationState.Running && !paused },
                { "paused", paused },
                { "stopped", !applied },
                { "scenario_applied", applied },
                { "sim_time_seconds", _clock != null ? (object)_clock.CurrentTimeSeconds : null },
                { "time_scale", _clock != null ? (object)_clock.TimeScale : null },
                // The scale above is the one the session was configured with and stays above zero while a
                // lockstep client holds the world stopped, so it cannot tell a client the world is not moving.
                // This is the engine's own scale, at or below zero exactly while the world is held.
                { "held", Time.timeScale <= 0f },
                { "scenario_id", manager != null ? manager.CurrentScenarioId : null },
                { "scenario_name", scenario != null ? scenario.Name : null },
                { "environment", EnvironmentOf(scenario) },
                { "map_name", scenario != null ? scenario.MapImage : null },
                { "robot_start_pose", ScenarioPointJson(scenario, primaryConfig?.StartRef) },
                { "robot_target_pose", ScenarioPointJson(scenario, primaryConfig?.GoalRef) },
                { "num_groups", CountGroups(scenario) },
                { "map_width", _mapWidth },
                { "map_height", _mapHeight },
                { "map_resolution", _mapResolution },
                { "map_origin_x", _mapOrigin.position.x },
                { "map_origin_y", _mapOrigin.position.y },
                { "human_count", FindHumans().Length },
                { "humans", HumansJson(FindHumans()) },
                { "robots", RobotsJson(robots, roster, primary, scenario, robotConfigs) },
                { "robot_count", robots.Count },
                { "robot", primary != null ? PoseJson(primary.RobotTransform) : null },
                { "robot_has_goal", primary != null && primary.HasGoal },
                { "robot_goal", primary != null && primary.HasGoal ? PositionJson(primary.Goal) : null },
                { "camera_focus", focus != null ? focus.name : null },
                { "camera_focus_is_robot", focus != null && focus.GetComponentInParent<Robot>() != null },
                { "camera_focus_is_human", focusedHuman != null },
                { "camera_focus_agent_id", focusedHuman != null ? (object)focusedHuman.agentId : null },
                { "camera_tool", camera != null ? camera.ActiveTool.ToString().ToLowerInvariant() : null },
                { "camera_mode", camera != null ? camera.CurrentMode.ToString().ToLowerInvariant() : null },
                { "camera_pose", camera != null && camera.mainCamera != null ? PoseJson(camera.mainCamera.transform) : null },
                { "refreshed_at", NowUnixSeconds() }
            };

            return JsonConvert.SerializeObject(payload, Formatting.None);
        }

        /// <summary>
        /// Live robots of the session, in the order the scenario lists them, which is the order the roster
        /// walks. A scene without a roster - an editor test, a scene authored before the fleet existed -
        /// resolves the single robot this snapshot has always described, so it keeps working unchanged.
        /// </summary>
        private static List<Robot> ResolveRobots()
        {
            var robots = new List<Robot>();

            RobotRoster roster = RobotRoster.Current;
            if (roster != null)
                roster.FillRobots(robots);

            if (robots.Count == 0)
            {
                Robot single = FindAnyObjectByType<Robot>();
                if (single != null)
                    robots.Add(single);
            }

            return robots;
        }

        /// <summary>
        /// The robot the legacy keys describe: the roster's primary, which is <c>robot_1</c> whenever the
        /// scenario runs one. A scene without a roster runs a single robot, which is the primary by definition.
        /// </summary>
        private static Robot PrimaryOf(RobotRoster roster, List<Robot> robots)
        {
            Robot primary = roster != null ? roster.Primary : null;
            if (primary != null)
                return primary;

            return robots.Count > 0 ? robots[0] : null;
        }

        /// <summary>
        /// The robots array of the snapshot: one entry per live robot, in the scenario's order. It is empty
        /// rather than null when the scene runs no robot, so a client never has to test the key for null.
        /// </summary>
        private object RobotsJson(
            List<Robot> robots,
            RobotRoster roster,
            Robot primary,
            ScenarioData scenario,
            List<RobotScenarioConfig> configs)
        {
            var entries = new List<object>(robots.Count);

            foreach (Robot robot in robots)
                entries.Add(RobotJson(robot, roster, primary, scenario, configs));

            return entries;
        }

        /// <summary>
        /// One entry of the robots array: who the robot is, where it stands in the ROS frame, and the two
        /// scenario poses it was applied with. Every key is written for every robot, so a reader does not have
        /// to test whether one is there before reading it.
        /// </summary>
        private object RobotJson(
            Robot robot,
            RobotRoster roster,
            Robot primary,
            ScenarioData scenario,
            List<RobotScenarioConfig> configs)
        {
            Transform transform = robot.RobotTransform;
            (float x, float y, float z, float yaw) = PoseValues(transform.position, transform.rotation);

            RobotIdentity identity = RobotIdentity.Of(robot);
            string id = IdOf(roster, robot) ?? RobotRoster.PrimaryId;
            RobotScenarioConfig config = ConfigOf(configs, id);

            return new
            {
                id,
                type = identity != null ? identity.TypeId : RobotProfiles.DefaultId,
                is_primary = identity != null ? identity.IsPrimary : robot == primary,
                x,
                y,
                z,
                yaw,
                has_goal = robot.HasGoal,
                goal = robot.HasGoal ? PositionJson(robot.Goal) : null,
                start_pose = ScenarioPointJson(scenario, config?.StartRef),
                target_pose = ScenarioPointJson(scenario, config?.GoalRef)
            };
        }

        /// <summary>The scenario entry that names an id, or null when the scenario does not carry one.</summary>
        private static RobotScenarioConfig ConfigOf(List<RobotScenarioConfig> configs, string id)
        {
            if (configs == null || string.IsNullOrEmpty(id))
                return null;

            foreach (RobotScenarioConfig config in configs)
            {
                if (config != null && string.Equals(config.Id, id, StringComparison.OrdinalIgnoreCase))
                    return config;
            }

            return null;
        }

        /// <summary>Id a robot is addressed by: the one the roster holds, or the one its identity carries.</summary>
        private static string IdOf(RobotRoster roster, Robot robot)
        {
            if (robot == null)
                return null;

            string id = roster != null ? roster.IdOf(robot) : null;
            if (!string.IsNullOrEmpty(id))
                return id;

            RobotIdentity identity = RobotIdentity.Of(robot);
            return identity != null ? identity.Id : null;
        }

        private void PublishState()
        {
            if (!CanPublish(_stateTopicName)) return;

            string json = BuildStateJson();
            _ros.Publish(_stateTopicName, new StringMsg(json));

            if (logPublishEvents && Time.frameCount % 60 == 0)
                Debug.Log($"[{name}] Published state to {_stateTopicName}: {json}");
        }

        /// <summary>
        /// Run state as the application understands it. The manager publishes every transition, so its last
        /// word is used, corrected by the two things the manager cannot see: the clock is the authority on
        /// pausing, and a scenario that is no longer loaded means idle whatever was published before.
        /// </summary>
        private SimulationState ResolveState(ScenarioManager manager, Supervisor supervisor)
        {
            if (manager == null || !manager.HasScenarioLoaded)
                return SimulationState.Idle;

            SimulationState state = _lastState ?? SimulationState.Ready;
            if (supervisor != null && supervisor.IsPaused && state == SimulationState.Running)
                return SimulationState.Paused;

            return state;
        }

        private static string StateName(SimulationState state)
        {
            switch (state)
            {
                case SimulationState.Idle: return "idle";
                case SimulationState.Running: return "running";
                case SimulationState.Paused: return "paused";
                default: return "ready";
            }
        }

        /// <summary>Pose of a transform in the ROS frame, or null when there is no transform.</summary>
        private static object PoseJson(Transform transform)
        {
            if (transform == null) return null;

            return PoseJson(transform.position, transform.rotation);
        }

        /// <summary>
        /// Pose of a world position and rotation in the ROS frame, in the shape every pose key of the
        /// snapshot uses.
        /// </summary>
        private static object PoseJson(Vector3 worldPosition, Quaternion worldRotation)
        {
            (float x, float y, float z, float yaw) = PoseValues(worldPosition, worldRotation);

            return new { x, y, z, yaw };
        }

        /// <summary>
        /// The four numbers every pose is made of, in the ROS frame, for the flat entry of the robots array
        /// which carries them next to the identity of its robot.
        /// </summary>
        private static (float x, float y, float z, float yaw) PoseValues(Vector3 worldPosition, Quaternion worldRotation)
        {
            Vector3<FLU> position = worldPosition.To<FLU>();
            Vector3<FLU> forward = (worldRotation * Vector3.forward).To<FLU>();

            return (position.x, position.y, position.z, Mathf.Atan2(forward.y, forward.x));
        }

        /// <summary>Position in the ROS frame, as a JSON object.</summary>
        private static object PositionJson(Vector3 worldPosition)
        {
            Vector3<FLU> position = worldPosition.To<FLU>();
            return new { x = position.x, y = position.y, z = position.z };
        }

        /// <summary>
        /// One entry per simulated human, in the ROS frame: the same pose and twist the robot-centric
        /// <c>/simulation/agents</c> stream reads from the other side, plus what the crowd makes of the
        /// agent - its goal, its group and the controller that drives it.
        /// </summary>
        private static object HumansJson(HumanAgent[] humans)
        {
            var list = new List<object>(humans.Length);

            foreach (HumanAgent human in humans)
            {
                Vector3<FLU> position = human.Position.To<FLU>();
                Vector3<FLU> velocity = human.Velocity.To<FLU>();

                list.Add(new
                {
                    id = human.agentId,
                    x = position.x,
                    y = position.y,
                    z = position.z,
                    vx = velocity.x,
                    vy = velocity.y,
                    vz = velocity.z,
                    speed = human.CurrentSpeed,
                    goal = human.HasDestination ? PositionJson(human.CurrentGoal) : null,
                    group = human.Group != null ? (object)human.Group.Id : null,
                    controller = human.IsManuallyControlled ? "manual"
                        : human.IsReplayControlled ? "replay"
                        : human.IsExternallyControlled ? "external"
                        : "sfm",
                    end_behavior = human.EndBehavior.ToString().ToLowerInvariant()
                });
            }

            return list;
        }

        /// <summary>
        /// Unix epoch seconds, UTC and fractional, so a client can date a snapshot against its own clock
        /// instead of assuming the message arrived when it was sent.
        /// </summary>
        private static double NowUnixSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        }

        #endregion

        #region Editor Utilities

        [ContextMenu("Log Configuration")]
        private void EditorLogConfiguration()
        {
            Debug.Log($"[{name}] Configuration:\n" +
                      $"  Prefix: '{_prefix}'\n" +
                      $"  Map: {_mapTopicName}\n" +
                      $"  Reset done: {_resetDoneTopicName}\n" +
                      $"  State: {_stateTopicName}\n" +
                      $"  Grid: {_mapWidth}x{_mapHeight} at {_mapResolution} m/cell\n" +
                      $"  ROS connection: {(_ros != null ? "OK" : "Missing")}\n" +
                      $"  EnvROS: {(_envROS != null ? "OK" : "Missing")}");
        }

        [ContextMenu("Rebuild Map Cache")]
        private void EditorRebuildMapCache()
        {
            RebuildMapCache();
            _mapPublishPending = true;
            Debug.Log($"[{name}] Grid cache rebuilt: {_mapWidth}x{_mapHeight}");
        }

        #endregion
    }
}
