using System;
using System.Collections.Generic;
using System.Globalization;
using RobotSNAP.Agents;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using RobotSNAP.ROS;
using RosMessageTypes.Std;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using UnityEngine;

namespace RobotSNAP.Metrics
{
    /// <summary>
    /// Watches a running session and turns it into episodes. One episode is one scenario run of one robot,
    /// between the moment the world starts moving and the moment it stops - the red Stop, a reset, another
    /// scenario, or the end of the time the mission was given - the thing a benchmarking campaign compares and
    /// a reinforcement learning loop calls a rollout.
    ///
    /// Reaching the goal, colliding and leaving the map name the run without ending it: they decide the
    /// outcome the episode is filed under, and the samples keep coming afterwards, so the trajectory of a
    /// session is the whole trajectory the session drove rather than the part of it before the first thing
    /// that happened. Only the end of the session, or the time limit, closes the episode.
    ///
    /// It samples on the fixed step rather than once per frame, because that is the step a velocity command is
    /// applied on: the world moves once per physics step, a frame at a raised time scale carries several of
    /// them, and a trajectory sampled per frame would be missing the positions the robot actually took in
    /// between. One sample per physics step is therefore the finest resolution at which the recorded path is
    /// the path the robot drove, and it is the step the metrics are defined on.
    ///
    /// Nothing here runs while the world is held at a zero time scale, which is what the lockstep gate does
    /// between two released periods: FixedUpdate is the loop a stopped world ends, so a client that takes the
    /// session one period at a time still gets exactly one episode, sampled at its own control step.
    ///
    /// An episode is closed once and never reopened: after a finish the recorder waits for a state that is not
    /// running - a reset, a stop, a scenario load - before it starts the next one, so a session left running
    /// after its robot reached the goal does not manufacture one identical episode per time limit.
    ///
    /// The episode has one subject: the roster's primary robot, the one a client reaches without naming one.
    /// Its id is the episode's <c>robot</c>, and the scalar metrics - path, speed, distances - describe it, so
    /// a benchmark still compares one controlled robot against one mission however many bodies the scenario
    /// fields. Every other robot of the roster is recorded beside it as its own trajectory, under its own
    /// roster id, together with every human of the applied world: a scenario with several robots therefore
    /// draws every robot's path while the numbers stay the primary robot's.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class MetricsRecorder : MonoBehaviour
    {
        [Header("Episode")]
        [Tooltip(
            "Radius in metres of the disc the social metrics measure against. It is the definition of " +
            "personal space this recorder uses: it is the clearance left between the outlines of the two " +
            "bodies - centre distance minus the robot's footprint radius and the crowd member's - so a " +
            "passage of half a metre between the bodies is what the number means, whatever the agents are " +
            "made of. The value is recorded in every episode, with both radii, so a reader never guesses " +
            "it. Half a metre is Hall's close personal distance, the usual floor for a comfortable passage.")]
        [SerializeField] private double personalSpaceRadiusMetres = 0.5;

        [Tooltip(
            "Footprint radius in metres to fall back on for a crowd member whose agent the manager cannot " +
            "hand back. It is only a safety net: the radius of the real agent is what the clearance uses.")]
        [SerializeField] private double humanRadiusFallbackMetres = 0.25;

        [Tooltip(
            "How close to an obstacle, in metres, the robot has to come for the episode to end as a collision. " +
            "Measured on the robot's lidar: the smallest range under this distance ends the episode.")]
        [SerializeField] private double collisionDistanceMetres = 0.25;

        [Tooltip(
            "Longest episode, in simulated seconds. The episode ends as a timeout at this world time, whatever " +
            "the session's time scale is, because the limit is a limit of the mission and not of the wall clock.")]
        [SerializeField] private double episodeTimeLimitSeconds = 120.0;

        [Tooltip("Points kept per agent and per episode; see TrajectoryBuffer for what happens beyond it.")]
        [SerializeField] private int maxTrajectoryPointsPerAgent = 2000;

        [Header("Output")]
        [Tooltip("Write the session to StreamingAssets/metrics after every finished episode.")]
        [SerializeField] private bool exportOnFinish = true;

        [Tooltip("Close the episode as a collision when the robot's lidar reports a range under the collision distance.")]
        [SerializeField] private bool detectCollisions = true;

        [Tooltip("Close the episode as out of bounds when the robot leaves the footprint of the applied map.")]
        [SerializeField] private bool detectOutOfBounds = true;

        [Header("ROS Configuration")]
        [Tooltip("Take the topic prefix from EnvROS, like the other publishers of this folder.")]
        [SerializeField] private bool autoDetectPrefix = true;

        [Tooltip("Prefix used when auto detection is off.")]
        [SerializeField] private string customPrefix = "";

        [Header("Debug")]
        [SerializeField] private bool logEpisodes = false;

        private static MetricsRecorder _instance;

        private readonly List<HumanSample> _humanSamples = new();
        private readonly List<int> _humanIds = new();
        private readonly List<HumanManager> _humanManagers = new();
        private readonly List<Robot> _robots = new();
        private readonly List<RobotSample> _robotSamples = new();

        private ROSConnection _ros;
        private EnvROS _envROS;
        private string _topic;
        private bool _topicRegistered;
        private bool _initialized;

        private ScenarioManager _manager;
        private Clock _clock;
        private Robot _robot;
        private string _robotId;

        private EpisodeAccumulator _accumulator;
        private string _scenario;
        private double _startedWallSeconds;
        private bool _armed = true;

        // World time of the last step the open episode recorded, and whether it recorded one at all. A clock
        // that reads earlier than the first of those has been reset under the episode - a reset leaves the
        // session running and puts the world back to zero - which is a boundary; the pair also keeps the
        // closed episode from being stamped with the rewound reading.
        private double _lastWorldSeconds;
        private bool _episodeSampled;

        /// <summary>
        /// The recorder of the running session, or null outside play mode. It is created by
        /// <see cref="Bootstrap"/> so the session records itself without an object of the scene naming it, which
        /// is what lets the same binary be played by a human and driven by Python through the same bridge.
        /// </summary>
        public static MetricsRecorder Instance => _instance;

        #region Bootstrap

        /// <summary>
        /// Puts one recorder in the session, after the scene is loaded and only once per play session. The
        /// object carries no scene reference on purpose: loading a scenario destroys the environment and every
        /// component the scene authored, and a recorder that lived in the environment would lose the episodes
        /// of the session every time a map is loaded.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            // A play session is a session: a static store that survived the previous one - which is what
            // "Enter Play Mode Options" and no domain reload leave behind - would otherwise hand this run the
            // episodes of the last, under an identity that is no longer its own.
            MetricsStore.Instance.Clear();

            var host = new GameObject("RobotSNAP Metrics");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<MetricsRecorder>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void Start()
        {
            Initialize();
        }

        /// <summary>
        /// Resolves the connection and registers the episode topic. Called from Start, and safe to call again:
        /// a session without ROS server registers nothing and keeps recording locally, which is the case this
        /// project usually runs in.
        /// </summary>
        public void Initialize()
        {
            _ros = ROSConnection.GetOrCreateInstance();
            EnsureTopic();
            _initialized = _ros != null;

            if (!_initialized && logEpisodes)
                Debug.LogWarning("[MetricsRecorder] ROSConnection unavailable, episodes will be recorded but not published");
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// A session that ends without a stop - the editor's play mode was left, a build was closed while the
        /// robot was still driving - is still a run that happened, and its trajectory is the part of it no
        /// later moment can reconstruct. The open episode is closed where it really was and the store is
        /// exported, so "the trajectory is never lost" holds for the run in progress and not only for the one
        /// that had already reached its outcome. A run that stopped before this has nothing open, and the
        /// export then simply finds every episode already written.
        /// </summary>
        private void OnApplicationQuit()
        {
            CloseOpenEpisodeForShutdown();
        }

        /// <summary>
        /// A pause is where a mobile build is killed without ever seeing a quit, so the same guaranteed write
        /// happens there.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (paused)
                CloseOpenEpisodeForShutdown();
        }

        /// <summary>
        /// Files the run in progress as one the session stopped, then writes everything the session holds. An
        /// accumulator that never sampled a step is not a run: it is dropped rather than filed as an empty
        /// trajectory. Nothing here throws, because an application going away is not a place to fail.
        /// </summary>
        private void CloseOpenEpisodeForShutdown()
        {
            try
            {
                if (_accumulator != null)
                {
                    if (_episodeSampled)
                        Finish(MetricsContract.OutcomeStopped, _lastWorldSeconds);
                    else
                        Discard();
                }

                MetricsExporter.Export(MetricsStore.Instance);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[MetricsRecorder] saving the session on exit failed: {exception.Message}");
            }
        }

        #endregion

        #region Sampling

        private void FixedUpdate()
        {
            if (!_initialized) return;

            ScenarioManager manager = ResolveManager();
            Clock clock = ResolveClock();
            if (manager == null || clock == null) return;

            string scenario = manager.CurrentScenarioId;
            bool running = manager.CurrentState == SimulationState.Running;

            if (!running)
            {
                if (_accumulator != null)
                    Finish(MetricsContract.OutcomeStopped, ClosingWorldTime(clock.ElapsedSeconds));

                // A state that is not running is the session saying "start the next run whenever you like", so
                // a reset or a scenario load re-arms the recorder for the episode that follows.
                _armed = true;
                return;
            }

            if (_accumulator != null && !string.Equals(_scenario, scenario, StringComparison.Ordinal))
                Finish(MetricsContract.OutcomeStopped, ClosingWorldTime(clock.ElapsedSeconds));

            if (_accumulator == null)
            {
                if (!_armed) return;
                Begin(manager, clock, scenario);
                if (_accumulator == null) return;
            }

            Robot robot = ResolveRobot();
            if (robot == null) return;

            EnsureTopic();
            CollectHumans();
            CollectRobots();

            double world = clock.ElapsedSeconds;

            // A clock that reads earlier than the last step this episode recorded was reset under it. The
            // reset is a boundary: the episode is closed where it really was - never at the rewound reading,
            // which would file a run of thirty seconds as a run of none - and the world that follows opens
            // the next one at its new origin. An accumulator a reset landed under before it recorded a single
            // step is not a run, and is dropped rather than filed.
            if (_accumulator != null && world < _lastWorldSeconds)
            {
                if (_episodeSampled)
                    Finish(MetricsContract.OutcomeStopped, _lastWorldSeconds);
                else
                    Discard();

                Begin(manager, clock, scenario);
                if (_accumulator == null) return;
            }

            if (robot.HasGoal) _accumulator.SetGoal(robot.Goal);

            _accumulator.Sample(world, _robotSamples, _humanSamples);
            _lastWorldSeconds = world;
            _episodeSampled = true;

            // Reaching the goal, touching something or leaving the map names the run; it does not end it. A
            // driver who arrives and keeps going, or clips a wall and backs off, is still driving the session,
            // and the trajectory of that session is what the session ran. The first of those moments to happen
            // is the outcome the episode will be filed under.
            _accumulator.LatchOutcome(Classify(robot, world));

            // The time limit is the one condition the mission itself imposes, and the only one that closes an
            // episode on its own: an episode never runs past the time it was given.
            if (_accumulator.ElapsedSeconds(world) >= episodeTimeLimitSeconds && episodeTimeLimitSeconds > 0.0)
                Finish(MetricsContract.OutcomeTimeout, world);
        }

        /// <summary>
        /// Opens an episode: the identity comes from the store, the world time from the clock, so the record
        /// names the same session the router and the exporter read.
        /// </summary>
        private void Begin(ScenarioManager manager, Clock clock, string scenario)
        {
            MetricsStore store = MetricsStore.Instance;
            _robot = ResolveRobot();
            if (_robot == null) return;

            _robotId = ResolveRobotId(_robot);
            _scenario = scenario;
            _startedWallSeconds = Time.realtimeSinceStartupAsDouble;
            _armed = false;

            // The origin is the last time the episode has recorded until it records one, so a clock reset
            // before the first step is told from a clock that simply has not moved yet.
            double origin = clock.ElapsedSeconds;
            _lastWorldSeconds = origin;
            _episodeSampled = false;

            _accumulator = new EpisodeAccumulator(
                store.NextEpisodeId(out int index),
                index,
                store.SessionId,
                scenario,
                _robotId,
                personalSpaceRadiusMetres,
                maxTrajectoryPointsPerAgent,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                origin);

            RefreshHumanManagers();
        }

        /// <summary>
        /// Names what happened to the run, in the project's own vocabulary, or null while nothing has.
        ///
        /// The order is the order of the conditions a mission cares about: the fleet that reached every one of
        /// its goals has succeeded even if a robot of it is standing close to a wall, and a robot that left
        /// the map is out of the mission whether or not it is also close to something. These are names, not
        /// ends - the episode carries on after them - so the time limit is deliberately not part of this: it
        /// is the one condition that closes a run, and the recorder applies it where it closes one.
        /// </summary>
        private string Classify(Robot robot, double worldSeconds)
        {
            if (AllRobotsReachedTheGoal(robot))
                return MetricsContract.OutcomeGoal;

            if (detectCollisions && NearestObstacleMetres(robot) is double range && range < collisionDistanceMetres)
                return MetricsContract.OutcomeCollision;

            if (detectOutOfBounds && !InsideMap(robot.Position))
                return MetricsContract.OutcomeOutOfBounds;

            return null;
        }

        /// <summary>
        /// Whether the mission the episode files is over: every robot of the scenario has reached its own
        /// goal. A run that fields several robots is a success only once the last of them arrives - the
        /// primary robot reaching its goal while another one is still driving is not a mission the fleet
        /// finished.
        ///
        /// A robot nobody gave a goal never reports having reached one, so it keeps the fleet short of the
        /// goal, and a roster with no robot in it is not an arrived one either. A scene with no roster is the
        /// single-robot case the recorder has always measured, where the one tracked robot's arrival is the
        /// whole mission.
        /// </summary>
        private bool AllRobotsReachedTheGoal(Robot robot)
        {
            RobotRoster roster = RobotRoster.Current;
            if (roster == null || _robots.Count == 0)
                return robot.GoalReached;

            for (int index = 0; index < _robots.Count; index++)
            {
                Robot member = _robots[index];
                if (member == null || !member.GoalReached) return false;
            }

            return true;
        }

        /// <summary>
        /// Smallest lidar range of the robot, or null when the robot carries no scanner or has measured
        /// nothing yet. A zero reading is the scanner's own "no return" and is skipped, so a missing wall is
        /// never mistaken for a collision.
        /// </summary>
        private static double? NearestObstacleMetres(Robot robot)
        {
            var scanner = robot.GetComponentInChildren<RaycastLaserScanner>(true);
            float[] ranges = scanner != null ? scanner.Ranges : null;
            if (ranges == null) return null;

            double nearest = double.PositiveInfinity;
            for (int index = 0; index < ranges.Length; index++)
            {
                float range = ranges[index];
                if (range > 0f && range < nearest) nearest = range;
            }
            return double.IsPositiveInfinity(nearest) ? null : nearest;
        }

        /// <summary>
        /// Whether the robot's centre still stands inside the footprint of the applied map. A session that has
        /// no usable grid answers true: "cannot say" must never be recorded as "out of bounds".
        /// </summary>
        private static bool InsideMap(Vector3 position)
        {
            OccupancyGrid grid = ScenarioNavigation.Obstacles;
            if (grid == null || !grid.IsValid) return true;

            Bounds bounds = grid.WorldBounds;
            return position.x >= bounds.min.x && position.x <= bounds.max.x
                   && position.z >= bounds.min.z && position.z <= bounds.max.z;
        }

        private void CollectHumans()
        {
            _humanSamples.Clear();

            for (int index = 0; index < _humanManagers.Count; index++)
            {
                HumanManager humans = _humanManagers[index];
                if (humans == null) continue;

                humans.GetAgentIds(_humanIds);
                for (int idIndex = 0; idIndex < _humanIds.Count; idIndex++)
                {
                    int id = _humanIds[idIndex];
                    if (!humans.TryGetAgentData(id, out Vector2 position, out _)) continue;
                    // The body is what the social distance is measured from, so the agent itself is asked for
                    // its footprint rather than the manager's bare numbers. The fallback covers a crowd whose
                    // agents the manager does not own - a pool removed mid-run, say - without ever turning a
                    // real radius into a zero.
                    float radius = humans.TryGetAgent(id, out HumanAgent agent) && agent != null
                        ? agent.Radius
                        : (float)humanRadiusFallbackMetres;
                    _humanSamples.Add(new HumanSample(id, new Vector3(position.x, 0f, position.y), radius));
                }
            }
        }

        /// <summary>
        /// Every robot of the applied scenario, one sample each, so the episode carries a track per robot and
        /// not only the primary one. The roster is the source of truth while it exists; a scene that carries a
        /// robot nobody registered keeps the single robot a recorder always resolved.
        /// </summary>
        private void CollectRobots()
        {
            _robotSamples.Clear();

            RobotRoster roster = RobotRoster.Current;
            if (roster != null)
            {
                _robots.Clear();
                roster.FillRobots(_robots);

                for (int index = 0; index < _robots.Count; index++)
                {
                    Robot robot = _robots[index];
                    if (robot == null) continue;
                    _robotSamples.Add(new RobotSample(ResolveRobotId(robot), robot.Position, robot.Radius));
                }
            }

            if (_robotSamples.Count > 0) return;

            Robot single = ResolveRobot();
            if (single != null)
                _robotSamples.Add(new RobotSample(ResolveRobotId(single), single.Position, single.Radius));
        }

        /// <summary>
        /// The world time an episode is closed at: the clock's own reading, unless the clock now stands behind
        /// the last step the episode recorded - the time a reset puts it back to - in which case the run is
        /// closed at the last moment it truly reached rather than at a reading earlier than its own samples.
        /// </summary>
        private double ClosingWorldTime(double worldSeconds) =>
            _lastWorldSeconds > worldSeconds ? _lastWorldSeconds : worldSeconds;

        /// <summary>
        /// Drops the open episode without filing it. An accumulator a reset landed under before it recorded a
        /// single step is not a run, and filing it would add a row the session never drove.
        /// </summary>
        private void Discard()
        {
            _accumulator = null;
            _armed = false;
        }

        /// <summary>
        /// Closes the episode, records it, publishes it and exports the session. The order matters: the record
        /// is kept before anything that can fail, so an unanswered publish or an unwritable folder never costs
        /// the session an episode.
        /// </summary>
        private void Finish(string outcome, double worldSeconds)
        {
            EpisodeAccumulator accumulator = _accumulator;
            _accumulator = null;
            _armed = false;
            if (accumulator == null) return;

            double wallSeconds = Math.Max(0.0, Time.realtimeSinceStartupAsDouble - _startedWallSeconds);
            EpisodeMetrics episode = accumulator.Finish(outcome, worldSeconds, wallSeconds);
            MetricsStore.Instance.Add(episode);

            Publish(episode);
            if (exportOnFinish)
                MetricsExporter.Export(MetricsStore.Instance);

            if (logEpisodes)
            {
                Debug.Log(
                    $"[MetricsRecorder] {episode.Id} {episode.Outcome} in {episode.WorldSeconds:F2}s world " +
                    $"({episode.WallSeconds:F2}s wall, {episode.Steps} steps, {episode.PathLengthMetres:F2}m)");
            }
        }

        #endregion

        #region Publish

        private void EnsureTopic()
        {
            if (_ros == null) return;

            _envROS ??= FindAnyObjectByType<EnvROS>();

            string prefix = "";
            if (autoDetectPrefix && _envROS != null)
                prefix = _envROS.Prefix;
            else if (!string.IsNullOrEmpty(customPrefix))
                prefix = customPrefix;

            string topic = MetricsContract.EpisodeTopicFor(prefix);
            if (_topicRegistered && topic == _topic) return;

            _topic = topic;
            RosTopicState state = _ros.GetTopic(_topic);
            if (state != null && state.IsPublisher) return;

            _ros.RegisterPublisher<StringMsg>(_topic);
            _topicRegistered = true;

            if (logEpisodes)
                Debug.Log($"[MetricsRecorder] Publishing episodes on {_topic}");
        }

        /// <summary>
        /// Sends one finished episode as JSON. A session with nothing listening is the normal case and stays
        /// quiet: the project runs without a ROS server far more often than with one, and the episode is
        /// already in the store and on disk by the time this is called.
        /// </summary>
        private void Publish(EpisodeMetrics episode)
        {
            if (_ros == null || string.IsNullOrEmpty(_topic) || episode == null) return;

            RosTopicState state = _ros.GetTopic(_topic);
            if (state == null || !state.IsPublisher) return;

            _ros.Publish(_topic, new StringMsg(episode.ToJson()));
        }

        #endregion

        #region References

        private ScenarioManager ResolveManager()
        {
            if (_manager != null) return _manager;
            _manager = FindAnyObjectByType<ScenarioManager>();
            return _manager;
        }

        private Clock ResolveClock() => _clock != null ? _clock : (_clock = Clock.Instance);

        private Robot ResolveRobot()
        {
            RobotRoster roster = RobotRoster.Current;
            if (roster != null && roster.Primary != null)
                return roster.Primary;

            if (_robot == null)
                _robot = FindAnyObjectByType<Robot>();
            return _robot;
        }

        private static string ResolveRobotId(Robot robot)
        {
            RobotRoster roster = RobotRoster.Current;
            string id = roster != null ? roster.IdOf(robot) : null;
            return string.IsNullOrEmpty(id) ? RobotRoster.PrimaryId : id;
        }

        /// <summary>
        /// Gathers the crowd managers of the applied world. The environment is rebuilt on every scenario load,
        /// so the list is refreshed when the episode starts rather than cached for the session.
        /// </summary>
        private void RefreshHumanManagers()
        {
            _humanManagers.Clear();
            foreach (GameManager manager in FindObjectsByType<GameManager>(FindObjectsInactive.Exclude))
            {
                HumanManager humans = manager.HumanManager;
                if (humans != null) _humanManagers.Add(humans);
            }
        }

        #endregion
    }
}
