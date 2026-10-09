using System.Collections.Generic;
using RobotSNAP.Agents;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using RobotSNAP.Metrics;
using UnityEngine;

/// <summary>
/// Re-runs a recorded episode: it re-applies the scenario the run happened on and makes the crowd repeat the
/// walk it was recorded making, instead of letting the social force model decide one of its own.
///
/// The difference matters for a benchmark. Replaying the trajectories on a map - what the trajectory panel
/// already draws - shows a run; re-running the scenario with the recorded crowd produces one, with a live
/// robot and the same people in it, which is what a reader needs to try a solution against the exact
/// situation it is being judged on instead of a second, different crowd.
///
/// The scenario is loaded through the ordinary <see cref="ScenarioManager"/> path, so the map, the robot and
/// the crowd all come back the way the run built them; the people are then handed their recorded tracks
/// rather than their targets, one <see cref="RecordedTrajectoryController"/> per body. Nothing here writes a
/// trajectory: the crowd is simulated as usual, and only where it walks is dictated by the record.
///
/// One clock drives the whole crowd. The trajectories carry world seconds, and their zero is the first robot
/// sample of the episode - the same anchor the trajectory map uses - so the replay instant is that zero plus
/// the simulated seconds that elapsed since the scenario was applied. The instant is published as
/// <see cref="CurrentSeconds"/>, which every replayed controller reads, which is what keeps the people
/// moving together instead of each on a clock of its own.
///
/// The object installs itself after the scene loads, the way the metrics recorder does, because a replay is a
/// property of the session and not of one scene document, and it outlives the environments a scenario load
/// tears down and rebuilds.
/// </summary>
public sealed class EpisodeReplayDirector : MonoBehaviour
{
    private static EpisodeReplayDirector _instance;

    /// <summary>The episode being replayed, or null while none is.</summary>
    public static EpisodeMetrics Episode => _instance != null ? _instance._episode : null;

    /// <summary>True while a replay is up, from the request until <see cref="Stop"/> or the object's death.</summary>
    public static bool IsActive => _instance != null && _instance._running;

    /// <summary>
    /// The instant every replayed human walks towards, in the episode's own world seconds. It is the single
    /// clock of the replay, and a static because a crowd of controllers has to read one shared value rather
    /// than one each: a per-controller clock would let the people drift apart by however many steps they were
    /// asked on. It stands at zero while no replay is up.
    /// </summary>
    public static double CurrentSeconds { get; private set; }

    /// <summary>One replayed human and the controller it was walking with before the replay took it.</summary>
    private struct Replayed
    {
        public HumanAgent Human;
        public MovementControllerType Previous;
    }

    private readonly List<Replayed> _replaying = new();

    private EpisodeMetrics _episode;
    private ScenarioManager _scenarioManager;
    private double _startSeconds;
    private double _clockBase;
    private bool _clockAnchored;
    private bool _scenarioApplied;
    private bool _running;

    /// <summary>
    /// Puts one director in the session, after the scene is loaded and only once per play session. The object
    /// carries no scene reference on purpose: loading a scenario destroys the environment and every component
    /// the scene authored, and a director that lived in the environment would not survive the very load it
    /// starts.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null)
            return;

        var host = new GameObject("RobotSNAP Episode Replay");
        DontDestroyOnLoad(host);
        _instance = host.AddComponent<EpisodeReplayDirector>();
    }

    /// <summary>The session's director, created on first use so a scene reload cannot leave the tab without one.</summary>
    private static EpisodeReplayDirector Instance
    {
        get
        {
            if (_instance == null)
                Bootstrap();

            return _instance;
        }
    }

    /// <summary>
    /// Replays <paramref name="episode"/>: re-applies its scenario and hands the recorded crowd its tracks.
    /// Asking again while a replay is up replaces it rather than stacking two, because there is one world and
    /// one crowd in it.
    /// </summary>
    public static void Start(EpisodeMetrics episode) => Instance.BeginReplay(episode);

    /// <summary>Ends the replay: every replayed human goes back to the controller the scenario gave it.</summary>
    public static void Stop()
    {
        if (_instance != null)
            _instance.EndReplay();
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

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;

        Unsubscribe();
        // A scenario applied after this object is gone must not find a replay still armed: the subscription
        // may outlive the object for the rest of the frame, and the flag is what the callback asks first.
        _running = false;
        _scenarioApplied = false;
        _episode = null;
        _replaying.Clear();
        CurrentSeconds = 0.0;
    }

    /// <summary>
    /// Advances the shared clock once per frame. The instant is the episode's zero plus the simulation
    /// seconds that elapsed since the scenario was applied - not the raw clock, because the clock carries the
    /// session's origin and the episode does not.
    ///
    /// The base is taken on the first frame after the scenario applied rather than inside the apply callback,
    /// because the loader resets the clock after it announces the scenario: anchoring to the value the
    /// callback read would date the replay against the run before the reset and leave the crowd standing
    /// until the clock caught up.
    /// </summary>
    private void Update()
    {
        if (!_running || !_scenarioApplied)
            return;

        double now = ClockSeconds();
        if (!_clockAnchored)
        {
            _clockBase = now;
            _clockAnchored = true;
        }

        CurrentSeconds = _startSeconds + (now - _clockBase);
    }

    private void BeginReplay(EpisodeMetrics episode)
    {
        if (episode == null)
            return;

        // One world, one crowd: a replay already up is ended before the new one starts, which also puts its
        // humans back on their scenario controller.
        EndReplay();

        _episode = episode;
        _startSeconds = StartSecondsOf(episode);
        _running = true;

        _scenarioManager = FindAnyObjectByType<ScenarioManager>();
        if (_scenarioManager == null)
        {
            Debug.LogWarning("[EpisodeReplayDirector] No ScenarioManager in the scene: the episode cannot be replayed.");
            _episode = null;
            _running = false;
            return;
        }

        _scenarioManager.OnScenarioApplied += HandleScenarioApplied;
        _scenarioManager.LoadScenario(episode.Scenario, startClock: true, autoApply: true, resetClock: true);
    }

    private void EndReplay()
    {
        Unsubscribe();
        RestoreHumans();

        _episode = null;
        _scenarioManager = null;
        _scenarioApplied = false;
        _clockAnchored = false;
        _running = false;
        CurrentSeconds = 0.0;
    }

    private void Unsubscribe()
    {
        if (_scenarioManager != null)
            _scenarioManager.OnScenarioApplied -= HandleScenarioApplied;
    }

    /// <summary>
    /// The scenario is in place: hand every human with a recorded track that track, and leave the others on
    /// the controller the scenario gave them. The callback is ignored once the replay is over, because a
    /// scenario applied afterwards - a reader loading another map, a command from the bridge - must not see
    /// its crowd taken over by a record that is no longer on screen.
    /// </summary>
    private void HandleScenarioApplied(ScenarioData scenario)
    {
        if (!_running || _episode == null)
            return;

        ApplyTracks();
        _scenarioApplied = true;
        _clockAnchored = false;
    }

    private void ApplyTracks()
    {
        IReadOnlyDictionary<string, List<double[]>> tracks = MetricsStore.Instance.TracksOf(_episode);

        HumanAgent[] humans = FindObjectsByType<HumanAgent>(FindObjectsInactive.Exclude);
        foreach (HumanAgent human in humans)
        {
            HumanMovement movement = human.GetMovement();
            if (movement == null)
                continue;

            if (!tracks.TryGetValue(MetricsContract.HumanTrackKey(human.agentId), out List<double[]> track) ||
                track == null || track.Count == 0)
                continue;

            // What it was is remembered, so the end of the replay puts it back where the scenario had it
            // rather than always dropping it onto the social force model.
            _replaying.Add(new Replayed { Human = human, Previous = movement.ControllerType });
            movement.SetControllerType((int)MovementControllerType.Replay);
            human.SetRecordedTrack(track);
        }
    }

    private void RestoreHumans()
    {
        for (int index = 0; index < _replaying.Count; index++)
        {
            HumanAgent human = _replaying[index].Human;
            // A human destroyed with the environment it lived in reads as null here, and there is nothing
            // left to put back.
            if (human == null)
                continue;

            HumanMovement movement = human.GetMovement();
            if (movement == null)
                continue;

            movement.SetControllerType((int)_replaying[index].Previous);
            human.SetRecordedTrack(null);
        }

        _replaying.Clear();
    }

    /// <summary>
    /// The world-second the episode's trajectories start at, taken from the robot's track - the same anchor
    /// the trajectory map measures its cursor from. Zero for an episode that kept no robot path, which puts
    /// the replay on the raw clock.
    /// </summary>
    private static double StartSecondsOf(EpisodeMetrics episode)
    {
        IReadOnlyDictionary<string, List<double[]>> tracks = MetricsStore.Instance.TracksOf(episode);
        string robotKey = MetricsContract.RobotTrackKey(episode.Robot);
        return tracks.TryGetValue(robotKey, out List<double[]> robot)
            ? AnalysisTrackReader.FirstSeconds(robot)
            : 0.0;
    }

    /// <summary>
    /// The session's simulation clock in seconds. A session that carries no clock - which the simulation
    /// builds for itself, so this is the odd case - falls back to the frame clock rather than to a constant:
    /// a replay anchored on a value that never moves would leave the crowd standing at its spawn.
    /// </summary>
    private static double ClockSeconds()
    {
        Clock clock = Clock.Instance;
        return clock != null ? clock.CurrentTimeSeconds : Time.timeAsDouble;
    }
}
