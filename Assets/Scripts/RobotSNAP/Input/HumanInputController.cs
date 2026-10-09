using RobotSNAP.Agents;
using RobotSNAP.CameraControl;
using UnityEngine;

namespace RobotSNAP
{
    /// <summary>
    /// Lets a person drive one pedestrian of the running scenario by hand, from the simulation view.
    ///
    /// The pedestrian that walks is the one the camera follows — the agent a click in the scene, or a row of
    /// the agent list, selected — so choosing who to drive needs no second selection of its own. A press of
    /// <see cref="grabKey"/> takes that pedestrian, the WASD or arrow keys walk it in the frame the view looks
    /// down, and a second press gives it back to the scenario.
    ///
    /// It drives the same velocity channel the Python API uses, which is what keeps one contract for the
    /// crowd: a grabbed pedestrian is an external one whose driver happens to be the keyboard in front of it,
    /// and the state snapshot reports it as <c>manual</c> so a reader can tell the two apart. A pedestrian
    /// that is not grabbed is left exactly as the scenario configured it.
    ///
    /// The component installs itself at start-up rather than being wired into the scene: the scene is authored
    /// by an editor tool that knows nothing of it, and a driver that belongs to every run belongs to the
    /// code, not to one document.
    /// </summary>
    public sealed class HumanInputController : MonoBehaviour
    {
        /// <summary>Key that takes the followed pedestrian, and gives it back.</summary>
        [SerializeField] private KeyCode grabKey = KeyCode.G;

        [Header("Keys")]
        [SerializeField] private KeyCode forwardKey = KeyCode.W;
        [SerializeField] private KeyCode backwardKey = KeyCode.S;
        [SerializeField] private KeyCode leftKey = KeyCode.A;
        [SerializeField] private KeyCode rightKey = KeyCode.D;
        [SerializeField] private KeyCode forwardArrowKey = KeyCode.UpArrow;
        [SerializeField] private KeyCode backwardArrowKey = KeyCode.DownArrow;
        [SerializeField] private KeyCode leftArrowKey = KeyCode.LeftArrow;
        [SerializeField] private KeyCode rightArrowKey = KeyCode.RightArrow;

        [Header("Movement")]
        [Tooltip("Walking speed of a manually driven pedestrian, in metres per second.")]
        [SerializeField] private float speed = 1.4f;

        private CameraController _camera;
        private HumanAgent _grabbed;
        private MovementControllerType _controllerBeforeGrab = MovementControllerType.SFM;
        private bool _warnedAboutMissingCamera;

        /// <summary>The pedestrian this controller drives, or null while none is.</summary>
        public HumanAgent Grabbed => _grabbed;

        /// <summary>True while somebody is being driven by hand.</summary>
        public bool IsDriving => _grabbed != null;

        /// <summary>
        /// Puts one driver in the scene of every run, once. A run that already carries one — a scene somebody
        /// wired by hand — is left alone.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindAnyObjectByType<HumanInputController>() != null)
                return;

            var host = new GameObject("HumanInput");
            host.AddComponent<HumanInputController>();
        }

        private void Update()
        {
            if (!Application.isPlaying)
                return;

            if (Input.GetKeyDown(grabKey))
                ToggleGrab();

            // A human destroyed under the grab - a reset, a scenario that puts the crowd back in its pool -
            // reads as null here, and there is nothing left to drive.
            if (_grabbed == null)
                return;

            DriveGrabbed();
        }

        private void OnDestroy() => Release();

        /// <summary>Takes the followed pedestrian, or gives the grabbed one back.</summary>
        private void ToggleGrab()
        {
            if (_grabbed != null)
            {
                Release();
                return;
            }

            HumanAgent candidate = FollowedHuman();
            if (candidate == null)
            {
                Debug.Log("[HumanInput] No pedestrian is followed. Select one in the simulation view, then " +
                          "press the grab key again.");
                return;
            }

            Grab(candidate);
        }

        /// <summary>The pedestrian the camera follows, or null when it follows a robot or nothing.</summary>
        private HumanAgent FollowedHuman()
        {
            CameraController camera = ResolveCamera();
            Transform target = camera != null ? camera.CurrentFollowTarget : null;
            return target != null ? target.GetComponentInParent<HumanAgent>() : null;
        }

        private void Grab(HumanAgent human)
        {
            HumanMovement movement = human.GetMovement();
            if (movement == null)
            {
                Debug.LogWarning("[HumanInput] That pedestrian has no movement component to take.");
                return;
            }

            // What it was is remembered, so releasing puts it back where the scenario - or a Python driver -
            // had it instead of always dropping it onto the social force model.
            _controllerBeforeGrab = movement.ControllerType;
            movement.SetControllerType((int)MovementControllerType.Manual);
            _grabbed = human;
            Debug.Log($"[HumanInput] Pedestrian {human.agentId} taken: WASD or the arrow keys walk it, " +
                      $"{grabKey} gives it back.");
        }

        private void Release()
        {
            HumanAgent human = _grabbed;
            _grabbed = null;
            if (human == null)
                return;

            human.ClearExternalVelocity();
            human.GetMovement()?.SetControllerType((int)_controllerBeforeGrab);
            Debug.Log($"[HumanInput] Pedestrian {human.agentId} released to the scenario.");
        }

        private void DriveGrabbed()
        {
            CameraController camera = ResolveCamera();
            Camera view = camera != null && camera.mainCamera != null ? camera.mainCamera : Camera.main;
            Vector3 viewForward = view != null ? view.transform.forward : Vector3.forward;

            HumanManualInput.GroundBasis(viewForward, out Vector2 forwardOnGround, out Vector2 rightOnGround);

            float forwardAmount = Mathf.Clamp(Axis(forwardKey, backwardKey) + Axis(forwardArrowKey, backwardArrowKey), -1f, 1f);
            float rightAmount = Mathf.Clamp(Axis(rightKey, leftKey) + Axis(rightArrowKey, leftArrowKey), -1f, 1f);

            Vector2 velocity = HumanManualInput.Velocity(
                forwardOnGround, rightOnGround, forwardAmount, rightAmount, speed);

            _grabbed.SetExternalVelocity(velocity);
        }

        private static float Axis(KeyCode positive, KeyCode negative)
        {
            float value = 0f;
            if (Input.GetKey(positive)) value += 1f;
            if (Input.GetKey(negative)) value -= 1f;
            return value;
        }

        private CameraController ResolveCamera()
        {
            if (_camera != null)
                return _camera;

            _camera = FindAnyObjectByType<CameraController>();
            if (_camera == null && !_warnedAboutMissingCamera)
            {
                _warnedAboutMissingCamera = true;
                Debug.LogWarning("[HumanInput] No camera controller in the scene; manual driving stays idle.");
            }

            return _camera;
        }
    }
}
