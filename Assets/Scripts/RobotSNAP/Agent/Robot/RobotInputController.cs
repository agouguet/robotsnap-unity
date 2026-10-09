using System.Collections.Generic;
using UnityEngine;
using RobotSNAP.CameraControl;
using RobotSNAP.Core;
using RobotSNAP.ROS;
using RobotSNAP.Agents;

namespace RobotSNAP
{
    public class RobotInputController : MonoBehaviour
    {
        [Header("Control Mode")]
        [SerializeField] private ControlMode controlMode = ControlMode.Keyboard;
        [SerializeField] private KeyCode toggleModeKey = KeyCode.M;

        [Tooltip("When set, the keys drive this robot only while the simulation view has it selected. " +
                 "Turn it off for a scene with a single robot and no agent list to select from.")]
        [SerializeField] private bool requireSelection = true;

        [Header("Keyboard Settings (Arrow Keys)")]
        [SerializeField] private float maxLinearSpeed = 2f;
        [SerializeField] private float maxAngularSpeed = 2f;
        [SerializeField] private KeyCode forwardKey = KeyCode.UpArrow;
        [SerializeField] private KeyCode backwardKey = KeyCode.DownArrow;
        [SerializeField] private KeyCode leftKey = KeyCode.LeftArrow;
        [SerializeField] private KeyCode rightKey = KeyCode.RightArrow;
        [SerializeField] private KeyCode stopKey = KeyCode.Space;

        [Header("ROS Settings")]
        [SerializeField] private bool autoDetectPrefix = true;
        [SerializeField] private string customPrefix = "";
        [SerializeField] private string cmdVelTopic = RobotSNAPTopics.CmdVel;
        [Tooltip("Silence a client is allowed before this robot stops obeying it, in seconds of wall time. A client publishes on its own clock, so this one timeout must not follow the simulation's time scale: at a scale of five a client publishing at 10 Hz would otherwise look silent for half a second of simulated time between two commands.")]
        [SerializeField] private float rosCommandTimeout = 0.5f;

        [Header("Debug")]
        [SerializeField] private bool showDebugInfo = false;
        [SerializeField] private EnvROS _envROS;
        [SerializeField] private Supervisor _supervisor;

        private Robot _robot;
        /// <summary>
        /// Every command stream this robot listens to: its own id, plus the legacy `/cmd_vel` for the first
        /// robot, so an old client drives robot 1 and a new one can address any robot by id.
        /// </summary>
        private readonly List<string> _fullCmdVelTopics = new List<string>(2);
        // When the last velocity command arrived, read on the wall clock: the timeout it is compared against
        // asks whether the *client* is still talking, and a client knows nothing of the simulation's scale.
        private float _lastRosCommandTime;
        /// <summary>What the keys are asking for, kept apart from what a client asks for.</summary>
        private float _keyLinear;
        private float _keyAngular;
        /// <summary>What the last velocity message asked for.</summary>
        private float _rosLinear;
        private float _rosAngular;
        private float _targetLinear;
        private float _targetAngular;
        private bool _rosSubscribed;

        /// <summary>
        /// True while a movement key is held. A keyboard that is doing nothing has nothing to say to the
        /// robot, and it used to say it anyway: it wrote a zero velocity on every physics step, which
        /// cancelled the route the scenario had just given the robot. Nobody noticed while a session was
        /// always driven from outside; the moment a scenario drives several robots, all of them stand still.
        /// </summary>
        private bool _keyboardDriving;

        /// <summary>
        /// True while the last command given to this robot is still the one it should obey. The wheels keep the
        /// command they were handed until another arrives, so the step that has no command left has to say so
        /// once - without this a tap on the forward key left the robot walking for the rest of the session.
        /// </summary>
        private bool _driving;

        /// <summary>The simulation view, which owns the selection. Looked up once, on the first frame it is needed.</summary>
        private CameraController _camera;

        // Détection du changement d'état de pause
        private bool _wasPaused = false;

        public enum ControlMode { Keyboard, ROS, Hybrid, Scenario }
        public ControlMode CurrentMode => controlMode;

        private void Awake()
        {
            _robot = GetComponent<Robot>();
            if (_robot == null)
            {
                Debug.LogError("Robot component missing");
                enabled = false;
                return;
            }

            // A scenario hands every robot its route when it is applied, and that route is the robot's only
            // while the scenario is the one driving it. Saying so before the roster has built or placed
            // anything keeps a robot left in keyboard mode standing still, instead of walking off to its goal
            // the moment the scenario loads - which is what it did, and what nobody had asked for.
            _robot.SetRouteOwnedByScenario(controlMode == ControlMode.Scenario);
        }

        private void Start()
        {
            if (_robot == null)
                return;

            _supervisor ??= Supervisor.Instance;
            _envROS ??= FindAnyObjectByType<EnvROS>();
            if (_envROS != null && (controlMode == ControlMode.ROS || controlMode == ControlMode.Hybrid))
                SubscribeToROS();
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleModeKey)) ToggleControlMode();

            // --- Gestion de la pause ---
            bool isPaused = (_supervisor ??= Supervisor.Instance) != null && _supervisor.IsPaused;

            if (isPaused && !_wasPaused)
            {
                // Entrée en pause : on arrête immédiatement les roues,
                // mais on conserve les valeurs cibles pour la reprise.
                _robot.Stop();
                _wasPaused = true;
            }
            else if (!isPaused && _wasPaused)
            {
                // Sortie de pause : on laisse le prochain FixedUpdate réappliquer la vitesse.
                _wasPaused = false;
            }

            // Ne pas traiter les entrées clavier si en pause
            if (isPaused) return;

            if (controlMode == ControlMode.Scenario) return;
            if (controlMode == ControlMode.Keyboard || controlMode == ControlMode.Hybrid)
            {
                if (IsSelectedForControl())
                    HandleKeyboardInput();
                else
                    ReleaseKeyboard();
            }
        }

        private void FixedUpdate()
        {
            // Si en pause, on n'envoie aucune commande (les roues sont déjà à l'arrêt)
            if ((_supervisor ??= Supervisor.Instance) != null && _supervisor.IsPaused)
                return;

            // A robot handed to a client belongs to that client, not to the scenario: a session that asked
            // for ROS control keeps it even when a scenario hands the robot a route. The route is suspended
            // rather than erased - see SuspendScenarioRoute - so the goal stays what it was.
            if (controlMode == ControlMode.ROS)
                SuspendScenarioRoute();

            if (controlMode == ControlMode.Scenario) return;

            if (controlMode == ControlMode.Hybrid && Time.unscaledTime - _lastRosCommandTime > rosCommandTimeout &&
                !_keyboardDriving && showDebugInfo && Time.frameCount % 60 == 0)
                Debug.Log("[RobotInputController] ROS timeout, fallback to keyboard");

            float linear, angular;
            if (!TryResolveCommand(out linear, out angular))
            {
                // Nobody is talking to this robot any more, and a robot nobody talks to stands still: letting
                // go of a key, or a client that stops publishing, has to take the command back rather than
                // leave the wheels turning on the last one.
                if (_driving)
                {
                    _driving = false;
                    _robot.Stop();
                }

                return;
            }

            // Appliquer la vitesse au robot (les valeurs sont conservées)
            _driving = true;
            _targetLinear = linear;
            _targetAngular = angular;
            _robot.SetVelocity(linear, angular);
        }

        /// <summary>
        /// The velocity this robot should be driving at, or false when nothing is asking it to move.
        ///
        /// The hand and the client are read separately because they die differently: a key is released, a
        /// publisher simply falls silent, and a third party that published once should not hold the robot for
        /// the rest of the run. When both are talking the hand wins, which is what an operator reaching for the
        /// keys expects, and it means a message no longer overwrites the keys the moment they are released.
        /// </summary>
        private bool TryResolveCommand(out float linear, out float angular)
        {
            bool clientTalking = Time.unscaledTime - _lastRosCommandTime <= rosCommandTimeout;
            linear = 0f;
            angular = 0f;

            switch (controlMode)
            {
                case ControlMode.Keyboard:
                    if (!_keyboardDriving)
                        return false;
                    linear = _keyLinear;
                    angular = _keyAngular;
                    return true;

                case ControlMode.ROS:
                    if (!clientTalking)
                        return false;
                    linear = _rosLinear;
                    angular = _rosAngular;
                    return true;

                case ControlMode.Hybrid:
                    if (_keyboardDriving)
                    {
                        linear = _keyLinear;
                        angular = _keyAngular;
                        return true;
                    }

                    if (!clientTalking)
                        return false;

                    linear = _rosLinear;
                    angular = _rosAngular;
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the keys pressed in this frame belong to this robot. Every robot of a scenario carries an
        /// input controller and every one of them reads the same keys, so without a selection the arrows drove
        /// the whole fleet at once; the robot the simulation view is on is the one that answers to them.
        /// </summary>
        private bool IsSelectedForControl()
        {
            if (!requireSelection)
                return true;

            _camera ??= FindAnyObjectByType<CameraController>();
            if (_camera == null)
                return true;

            Transform target = _camera.GetCurrentFollowTarget();
            if (target == null)
                return false;

            return target.GetComponentInParent<Robot>() == _robot;
        }

        /// <summary>Drops whatever the keys were asking for, so the robot comes to a stop on the next step.</summary>
        private void ReleaseKeyboard()
        {
            if (!_keyboardDriving && Mathf.Approximately(_targetLinear, 0f) && Mathf.Approximately(_targetAngular, 0f))
                return;

            _keyboardDriving = false;
            _keyLinear = 0f;
            _keyAngular = 0f;
        }

        private void SubscribeToROS()
        {
            // The connector appends a callback to its topic state without deduplicating it, so a second
            // subscription would run every velocity command twice.
            if (_rosSubscribed || _envROS == null || !_envROS.IsInitialized)
                return;

            string prefix = "";
            if (autoDetectPrefix && _envROS != null) prefix = _envROS.Prefix;
            else if (!string.IsNullOrEmpty(customPrefix)) prefix = customPrefix;
            // Joined by the topic table. This line used to concatenate the prefix and the name and rely on
            // the leading slash of `/cmd_vel` to separate them, so a topic configured without one came out
            // as `/myenvcmd_vel`.
            _fullCmdVelTopics.Clear();
            string baseTopic = RobotSNAPTopics.Resolve(RosTopicSlot.CmdVel, cmdVelTopic);
            _fullCmdVelTopics.AddRange(RobotIdentity.StreamNamesFor(this, baseTopic, prefix));
            foreach (string topic in _fullCmdVelTopics)
                _envROS.RegisterSubscriber<RosMessageTypes.Geometry.TwistMsg>(topic, OnRosCommandReceived);
            _rosSubscribed = true;
        }

        private void OnRosCommandReceived(RosMessageTypes.Geometry.TwistMsg msg)
        {
            _lastRosCommandTime = Time.unscaledTime;
            if (controlMode == ControlMode.ROS || controlMode == ControlMode.Hybrid)
            {
                _rosLinear = Mathf.Clamp((float)msg.linear.x, -maxLinearSpeed, maxLinearSpeed);
                // Le signe est repris tel quel : une consigne ROS positive tourne vers la gauche
                // (sens anti-horaire vu de dessus), et c'est la convention que partagent les conduites
                // du projet - le controleur de roues comme la base cinematique. La negation qui vivait
                // ici inversait donc le sens de rotation du robot par rapport a ce que le planificateur
                // demandait, ce qui faisait osciller le lacet et reculer le robot vers son but.
                _rosAngular = Mathf.Clamp((float)msg.angular.z, -maxAngularSpeed, maxAngularSpeed);
            }
        }

        private void HandleKeyboardInput()
        {
            float linear = 0f, angular = 0f;
            if (Input.GetKey(forwardKey)) linear = maxLinearSpeed;
            else if (Input.GetKey(backwardKey)) linear = -maxLinearSpeed;
            if (Input.GetKey(leftKey)) angular = maxAngularSpeed;
            else if (Input.GetKey(rightKey)) angular = -maxAngularSpeed;
            if (Input.GetKeyDown(stopKey)) { linear = 0f; angular = 0f; }
            _keyLinear = Mathf.Clamp(linear, -maxLinearSpeed, maxLinearSpeed);
            _keyAngular = Mathf.Clamp(angular, -maxAngularSpeed, maxAngularSpeed);

            // The hand takes this robot away from the route it was given, so the two never steer it at once.
            _keyboardDriving = Mathf.Abs(_keyLinear) > 0.01f || Mathf.Abs(_keyAngular) > 0.01f;
            if (_keyboardDriving)
                SuspendScenarioRoute();
        }

        /// <summary>
        /// Takes the route of the scenario away from this robot without throwing it away.
        ///
        /// A takeover used to call <see cref="Robot.ClearGoal"/>, which erases the route *and* the position
        /// of the goal: the goal marker of the simulation view disappeared the moment anybody touched the
        /// robot, and the agent panel - which reads "no goal" as "goal reached" - announced a mission that
        /// had not been run. Suspending leaves the goal where the scenario put it, stops the scenario from
        /// steering at the same time as the driver, and lets the route carry on from the point it had
        /// reached when the robot is handed back to the scenario.
        /// </summary>
        private void SuspendScenarioRoute()
        {
            if (!_robot.RouteOwnedByScenario)
                return;

            _robot.SetRouteOwnedByScenario(false);
        }

        public void SetControlMode(ControlMode newMode)
        {
            controlMode = newMode;
            _targetLinear = 0f;
            _targetAngular = 0f;
            _keyLinear = 0f;
            _keyAngular = 0f;
            _rosLinear = 0f;
            _rosAngular = 0f;
            _keyboardDriving = false;
            _driving = false;
            // A mode change also takes the clock out of the client's hands: without this, switching into ROS
            // mode within the timeout of a command published under the previous mode would drive on it.
            _lastRosCommandTime = float.NegativeInfinity;

            // A robot handed to a client - or to the keyboard - stops walking the route the scenario gave
            // it: without this the scenario and the driver would steer it at the same time. The route itself
            // is kept, so the simulation view still shows where this robot was going and handing the robot
            // back to the scenario resumes it where it stopped instead of starting the mission over.
            _robot.SetRouteOwnedByScenario(newMode == ControlMode.Scenario);

            // The subscription used to be created once, in Start, and only when the Inspector already said
            // ROS or Hybrid. A session switched over the bridge therefore drove the robot with a topic
            // nobody was listening to, which is the one thing a Python or ROS2 client does first.
            if (newMode == ControlMode.ROS || newMode == ControlMode.Hybrid)
            {
                _envROS ??= FindAnyObjectByType<EnvROS>();
                SubscribeToROS();
            }

            _robot.Stop();
        }

        public void ToggleControlMode() => SetControlMode((ControlMode)(((int)controlMode + 1) % System.Enum.GetValues(typeof(ControlMode)).Length));

        public void EmergencyStop()
        {
            _targetLinear = 0f;
            _targetAngular = 0f;
            _keyLinear = 0f;
            _keyAngular = 0f;
            _rosLinear = 0f;
            _rosAngular = 0f;
            _keyboardDriving = false;
            _driving = false;
            _lastRosCommandTime = float.NegativeInfinity;
            _robot.Stop();
        }

        public void SendVelocityCommand(float linear, float angular)
        {
            if (controlMode != ControlMode.Scenario)
            {
                _rosLinear = Mathf.Clamp(linear, -maxLinearSpeed, maxLinearSpeed);
                _rosAngular = Mathf.Clamp(angular, -maxAngularSpeed, maxAngularSpeed);
                _lastRosCommandTime = Time.unscaledTime;
            }
        }

        public void EnableScenarioMode() => SetControlMode(ControlMode.Scenario);
        public void DisableScenarioMode() => SetControlMode(ControlMode.Keyboard);
        public string GetModeString() => controlMode.ToString();
    }
}
