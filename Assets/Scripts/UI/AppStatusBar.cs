using System;
using RobotSNAP;
using RobotSNAP.Agents;
using RobotSNAP.CameraControl;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using RobotSNAP.ROS;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The application bar along the bottom of the window: what the simulation is doing and for how
/// long, how many agents are around, whether the ROS bridge is up, the ROS chip that hands the robot
/// the view is on to ROS 2, and the power button that closes the application.
///
/// The bar reads the simulation first: the state comes from the event bus, the agent figures are
/// counted from the scene, and the clock only advances while the simulation runs. Its two buttons are
/// the exception - the ROS chip changes the control mode of one robot, and the power button closes
/// the application - and both ask for that through an effect a test can stand in for.
/// </summary>
public sealed class AppStatusBar : IDisposable
{
    private const float AgentCountRefresh = 0.5f;
    private const string AgentsIconPath = "Icons/agents";
    private const string PowerIconPath = "Icons/power";

    /// <summary>The name <see cref="RobotInputController.GetModeString"/> gives the ROS control mode.</summary>
    private const string RosModeName = "ROS";

    private readonly Label _messageLabel;
    private readonly Label _timeLabel;
    private readonly Label _agentsLabel;
    private readonly VisualElement _rosDot;
    private readonly Button _rosChip;
    private readonly Button _quitButton;
    private readonly Action _quitRequested;
    private readonly Action _rosControlToggled;
    private readonly Func<bool> _isRosControlled;
    private readonly VisualElement _root;

    /// <summary>
    /// The simulation view, which owns the robot selection. Looked up on the first refresh and then
    /// kept, the way the ROS environment is, so the slow path does not search the scene every frame.
    /// </summary>
    private CameraController _camera;

    /// <summary>The question standing in front of the close, while one is being asked.</summary>
    private ConfirmationDialog _quitDialog;

    private readonly ScenarioManager _scenarioManager;
    private EnvROS _envRos;

    private SimulationState _state = SimulationState.Idle;
    private string _scenarioName;
    private float _elapsedSeconds;
    private float _nextAgentCount;
    private int _robotCount;
    private int _humanCount;

    public AppStatusBar(VisualElement root) : this(root, QuitApplication)
    {
    }

    /// <summary>
    /// Builds the bar with the effect a click on the power button has. The effect is a parameter so an
    /// edit-mode test can observe the click path without stopping Play mode or closing the editor;
    /// the application hands it <see cref="QuitApplication"/>, which is also the default. The ROS chip
    /// keeps its own effect and reading.
    /// </summary>
    public AppStatusBar(VisualElement root, Action quitRequested)
        : this(root, quitRequested, null)
    {
    }

    /// <summary>
    /// The above, with the effect a click on the ROS chip has. Both effects are parameters for the same
    /// reason: an edit-mode tree has no panel, so a test raises the click through <see cref="RequestRosControl"/>
    /// and reads what the bar asked of it.
    /// </summary>
    public AppStatusBar(VisualElement root, Action quitRequested, Action rosControlToggled)
        : this(root, quitRequested, rosControlToggled, null)
    {
    }

    /// <summary>
    /// The above, with the reading that decides whether the ROS chip shows as armed. It is a parameter so a
    /// test can say "the targeted robot obeys ROS" without a scene; the application leaves it null and the
    /// bar then reads the control mode off the robot the view is on, so a switch made from the bridge lights
    /// the chip on its own.
    /// </summary>
    public AppStatusBar(VisualElement root, Action quitRequested, Action rosControlToggled, Func<bool> isRosControlled)
    {
        _root = root;
        _quitRequested = quitRequested ?? QuitApplication;
        _rosControlToggled = rosControlToggled ?? ToggleSelectedRobotRosControl;
        _isRosControlled = isRosControlled ?? SelectedRobotUnderRosControl;

        if (root == null)
        {
            Debug.LogWarning("[AppStatusBar] Root element is null; the application bar stays inert.");
            return;
        }

        // The simulation view hosts a status bar of its own that reuses the same element names, so
        // the queries are scoped to the application bar. The power button is the anchor: it is the
        // one piece of this bar the simulation view does not carry.
        VisualElement bar = root.Q<Button>("QuitAppButton")?.parent;
        if (bar == null)
        {
            Debug.LogWarning("[AppStatusBar] The application bar is missing; it stays inert.");
            return;
        }

        _messageLabel = Query<Label>(bar, "StatusMessage");
        _timeLabel = Query<Label>(bar, "StatusTime");
        _agentsLabel = Query<Label>(bar, "StatusAgentsLabel");
        VisualElement agentsIcon = Query<VisualElement>(bar, "StatusAgentsIcon");
        _rosDot = Query<VisualElement>(bar, "StatusRosDot");
        _rosChip = Query<Button>(bar, "StatusRos");
        _quitButton = Query<Button>(bar, "QuitAppButton");

        if (_messageLabel == null || _timeLabel == null || _agentsLabel == null ||
            _rosChip == null || _quitButton == null)
        {
            Debug.LogWarning("[AppStatusBar] The application bar is incomplete; it stays inert.");
            return;
        }

        ApplyIcon(agentsIcon, AgentsIconPath);
        ApplyIcon(_quitButton, PowerIconPath);

        _rosChip.clicked += RequestRosControl;
        _quitButton.clicked += OnQuitClicked;

        _scenarioManager = UnityEngine.Object.FindAnyObjectByType<ScenarioManager>();
        if (_scenarioManager != null)
        {
            _scenarioManager.OnScenarioApplied += OnScenarioApplied;
            if (_scenarioManager.CurrentScenarioData != null)
                OnScenarioApplied(_scenarioManager.CurrentScenarioData);
        }

        // The ROS bridge comes with the environment, which can be built after this bar appears, so a
        // missing reference is looked up again on the slow refresh instead of being cached for good.
        _envRos = UnityEngine.Object.FindAnyObjectByType<EnvROS>();
        EventBus.Instance.Subscribe<SimulationStateChangedEvent>(OnStateChanged);

        CountAgents();
        ApplyMessage();
        ApplyTime();
        ApplyRosState();
    }

    public void Dispose()
    {
        EventBus.Instance.Unsubscribe<SimulationStateChangedEvent>(OnStateChanged);

        if (_scenarioManager != null)
            _scenarioManager.OnScenarioApplied -= OnScenarioApplied;

        if (_quitButton != null)
            _quitButton.clicked -= OnQuitClicked;

        if (_rosChip != null)
            _rosChip.clicked -= RequestRosControl;

        _quitDialog?.RemoveFromHierarchy();
        _quitDialog = null;
    }

    /// <summary>
    /// Called every frame: it refreshes the mission time and the slow figures. The time it shows comes from the
    /// simulation clock, and only a scene that carries none leaves the bar advancing its own count.
    /// </summary>
    public void Tick()
    {
        // The bar shows the mission's time and the mission's time is the simulation clock's - the same counter
        // the state snapshot publishes as sim_time_seconds - so the bar reads it instead of keeping one of its
        // own. Counting frames here is what let the two disagree: a stopped world still looked to be moving on
        // a bar that scaled the wall clock by the configured scale, and a run at any other speed drifted. Only
        // a scene that carries no clock leaves the bar with nothing to read, and only then does it fall back
        // to its own accumulator so the display keeps moving rather than freezing.
        if (_state == SimulationState.Running && Clock.Instance == null)
            _elapsedSeconds += SimulatedDelta(Time.unscaledDeltaTime, Time.timeScale);

        ApplyTime();

        if (Time.unscaledTime >= _nextAgentCount)
        {
            _nextAgentCount = Time.unscaledTime + AgentCountRefresh;
            _envRos ??= UnityEngine.Object.FindAnyObjectByType<EnvROS>();
            CountAgents();
        }

        ApplyRosState();
    }

    /// <summary>
    /// How far the fallback accumulator moves for one frame of <paramref name="unscaledDeltaSeconds"/> seconds
    /// of wall time, at <paramref name="timeScale"/>. It is the rule the bar follows only while the scene
    /// carries no clock to read (<see cref="DisplayedSeconds"/>), kept as a pure function so an edit-mode test
    /// can measure it with synthetic values instead of waiting on frames: a negative delta or a negative scale
    /// contributes nothing rather than taking time back.
    /// </summary>
    public static float SimulatedDelta(float unscaledDeltaSeconds, float timeScale)
    {
        return Mathf.Max(0f, unscaledDeltaSeconds) * Mathf.Max(0f, timeScale);
    }

    /// <summary>
    /// The mission seconds the bar shows. The scene's clock is the mission's time - it is the counter the
    /// state snapshot publishes as <c>sim_time_seconds</c> - so it is read whenever there is one, and the
    /// bar and a client cannot then disagree about how far the world has moved. A scene that carries no clock
    /// (an authored test scene, or one whose clock was orphaned) leaves the bar with nothing to read and it
    /// falls back to <paramref name="fallbackSeconds"/>, so the display keeps time rather than standing still.
    /// </summary>
    public static double DisplayedSeconds(Clock clock, double fallbackSeconds)
    {
        return clock != null ? clock.ElapsedSeconds : fallbackSeconds;
    }

    /// <summary>
    /// Restarts the mission clock. Applying a scenario asks for it through this method, so no other
    /// path of this class ever takes the time back to zero.
    /// </summary>
    public void ResetClock()
    {
        _elapsedSeconds = 0f;
        ApplyTime();
    }

    /// <summary>
    /// What a click on the power button does: it asks the application to close. The request is its own
    /// method so the wiring can be exercised without a click - and so a confirmation can later be put
    /// in front of the close without touching the button.
    /// </summary>
    public void RequestQuit()
    {
        Debug.Log("[AppStatusBar] Quit requested.");
        _quitRequested?.Invoke();
    }

    /// <summary>
    /// What a click on the ROS chip does: it hands one robot to ROS 2, or takes it back to the scenario when
    /// ROS is already driving it. Its own method for the same reason <see cref="RequestQuit"/> has one - the
    /// wiring of a button can be exercised without a panel to raise a click through.
    /// </summary>
    public void RequestRosControl()
    {
        _rosControlToggled?.Invoke();
        ApplyRosState();
    }

    /// <summary>
    /// What a click on the power button does: it asks first.
    ///
    /// Closing ends the session, and the episodes this session recorded live only in memory - the Analysis tab
    /// exports them, nothing keeps them by itself - so the one action of this bar that cannot be taken back is
    /// the one that gets a question in front of it. It is the same modal the Analysis tab puts in front of its
    /// own irreversible actions, so the two read alike.
    /// </summary>
    private void OnQuitClicked()
    {
        if (_quitDialog != null)
            return; // Already asking; a second press must not stack a second question.

        _quitDialog = new ConfirmationDialog(
            "Quit RobotSNAP?",
            "The simulation stops and the application closes. Episodes recorded during this session are kept "
            + "in memory only - export them from the Analysis tab first if you want to keep them.",
            "Quit");

        _quitDialog.Confirmed += () =>
        {
            _quitDialog = null;
            RequestQuit();
        };
        _quitDialog.Cancelled += () => _quitDialog = null;

        _quitDialog.Show(_root);
    }

    /// <summary>
    /// Closes the application. In the editor it leaves Play mode, which is what the editor can close;
    /// a built player is asked to quit.
    /// </summary>
    private static void QuitApplication()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OnScenarioApplied(ScenarioData scenario)
    {
        _scenarioName = scenario?.Name;
        ResetClock();
        ApplyMessage();
    }

    private void OnStateChanged(SimulationStateChangedEvent evt)
    {
        _state = evt.NewState;
        ApplyMessage();
    }

    private void ApplyMessage()
    {
        if (_messageLabel == null) return;

        string state = StateText(_state);
        _messageLabel.text = string.IsNullOrEmpty(_scenarioName) ? state : $"{_scenarioName} — {state}";
    }

    private void ApplyTime()
    {
        if (_timeLabel != null)
            _timeLabel.text = $"Time: {FormatElapsed(DisplayedSeconds(Clock.Instance, _elapsedSeconds))}";
    }

    private void ApplyRosState()
    {
        // Two readings, two marks: the dot keeps saying whether the bridge is reachable, and the chip
        // lights up while the robot it targets obeys ROS. Both are re-read on the bar's own refresh, so a
        // mode switched from Python or ROS lights the chip here without anybody clicking it.
        _rosDot?.EnableInClassList("is-online", _envRos != null && _envRos.IsInitialized);
        _rosChip?.EnableInClassList("is-ros-controlled", _isRosControlled != null && _isRosControlled());
    }

    /// <summary>
    /// The robot the ROS chip acts on: the one the simulation view is following, which is the same selection
    /// the keyboard obeys and the agent panel shows. Nothing selected leaves the chip the roster's primary,
    /// <c>robot_1</c>, or the first robot of a scenario that named its robots differently - so a session nobody
    /// has picked still has a target.
    /// </summary>
    private Robot TargetRobot()
    {
        _camera ??= UnityEngine.Object.FindAnyObjectByType<CameraController>();
        Transform followed = _camera != null ? _camera.GetCurrentFollowTarget() : null;
        if (followed != null)
        {
            Robot selected = followed.GetComponentInParent<Robot>();
            if (selected != null)
                return selected;
        }

        RobotRoster roster = RobotRoster.Current;
        if (roster != null && roster.Primary != null)
            return roster.Primary;

        // A scene with neither a selection nor a roster is the single-robot scene these commands always
        // resolved before the roster existed.
        return UnityEngine.Object.FindAnyObjectByType<Robot>();
    }

    /// <summary>
    /// Input controller of the targeted robot: the one that lives on it, falling back to the single controller
    /// of a scene that keeps it elsewhere - the same pair the bridge command router resolves.
    /// </summary>
    private RobotInputController TargetInputController()
    {
        Robot robot = TargetRobot();
        RobotInputController controller = robot != null ? robot.GetComponentInChildren<RobotInputController>() : null;
        return controller != null ? controller : UnityEngine.Object.FindAnyObjectByType<RobotInputController>();
    }

    /// <summary>
    /// Hands the targeted robot to ROS 2, or back to the scenario when ROS is already driving it. The controller
    /// clears its target speeds and stops the robot on the change, so the switch is a takeover rather than an
    /// accelerator, and a second press gives the robot back the route the scenario had given it.
    /// </summary>
    private void ToggleSelectedRobotRosControl()
    {
        RobotInputController controller = TargetInputController();
        if (controller == null)
        {
            Debug.LogWarning("[AppStatusBar] No robot input controller to hand to ROS; the chip stays inert.");
            return;
        }

        bool underRos = controller.GetModeString() == RosModeName;
        controller.SetControlMode(underRos
            ? RobotInputController.ControlMode.Scenario
            : RobotInputController.ControlMode.ROS);
    }

    /// <summary>Whether the targeted robot is being driven by ROS right now.</summary>
    private bool SelectedRobotUnderRosControl()
    {
        RobotInputController controller = TargetInputController();
        return controller != null && controller.GetModeString() == RosModeName;
    }

    private void CountAgents()
    {
        // A scenario's robots are owned by the roster, which lists every one of them whether or not the
        // scene search would reach it; a scene without a roster - a hand-placed robot, or a test - keeps
        // the old count.
        RobotRoster roster = RobotRoster.Current;
        _robotCount = roster != null && roster.Count > 0
            ? roster.Count
            : UnityEngine.Object.FindObjectsByType<Robot>().Length;

        _humanCount = UnityEngine.Object.FindObjectsByType<HumanAgent>().Length;

        if (_agentsLabel == null) return;

        int total = _robotCount + _humanCount;
        string breakdown = $"{_robotCount} {Plural("robot", _robotCount)}, {_humanCount} {Plural("human", _humanCount)}";

        // "8 agents" says nothing about a scenario that runs two robots among six pedestrians, so the chip
        // spells the split out; the tooltip carries the same figures for a pointer that hovers it.
        _agentsLabel.text = $"{total} {Plural("agent", total)} ({breakdown})";
        _agentsLabel.tooltip = breakdown;
    }

    private static string StateText(SimulationState state) => state switch
    {
        SimulationState.Ready => "Ready",
        SimulationState.Running => "Simulation running",
        SimulationState.Paused => "Paused",
        _ => "Idle"
    };

    private static string Plural(string noun, int count) => count == 1 ? noun : noun + "s";

    private static void ApplyIcon(VisualElement element, string resourcePath)
    {
        if (element == null) return;

        Texture2D texture = Resources.Load<Texture2D>(resourcePath);
        if (texture != null)
            element.style.backgroundImage = new StyleBackground(texture);
    }

    private static string FormatElapsed(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0.0, seconds));

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private static T Query<T>(VisualElement root, string name) where T : VisualElement
    {
        T element = root.Q<T>(name);
        if (element == null)
            Debug.LogWarning($"[AppStatusBar] Element '{name}' ({typeof(T).Name}) not found in the application bar; that part of the bar stays inert.");
        return element;
    }
}
