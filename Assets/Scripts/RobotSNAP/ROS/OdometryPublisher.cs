using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector.ROSGeometry;
using RobotSNAP.Agents;
using RobotSNAP.Core;

namespace RobotSNAP.ROS
{
    /// <summary>
    /// Publie l'odométrie du robot sur un topic ROS
    /// </summary>
    public class OdometryPublisher : MonoBehaviour
    {
        [Header("ROS Configuration")]
        [SerializeField] private bool autoDetectPrefix = true;
        [SerializeField] private string customPrefix = "";
        [SerializeField] private string topicName = RobotSNAPTopics.Odom;
        [SerializeField] private float publishFrequencyHz = 10f;
        
        [Header("References")]
        [SerializeField] private Rigidbody robotRigidbody;
        [SerializeField] private Transform robotTransform;
        
        [Header("Frame IDs")]
        [SerializeField] private string frameId = "odom";
        [SerializeField] private string childFrameId = "base_link";
        
        [Header("Covariance")]
        [SerializeField] private bool publishCovariance = true;
        [SerializeField] private float positionCovariance = 0.01f;
        [SerializeField] private float orientationCovariance = 0.01f;
        [SerializeField] private float linearVelocityCovariance = 0.01f;
        [SerializeField] private float angularVelocityCovariance = 0.01f;
        
        [SerializeField] private EnvROS _envROS;
        /// <summary>
        /// Every name this odometry answers on: the id of this robot, plus the legacy name when it is the
        /// first robot of the scenario.
        /// </summary>
        private readonly List<string> _fullTopicNames = new List<string>(2);
        private float _publishInterval;
        // The next instant of simulation time this pose is due at, on the same physics grid the scan and the
        // state snapshot publish on, so a client always holds poses that surround a scan stamp.
        private float _nextPublishTime;
        // The robot moves through an ArticulationBody rather than a Rigidbody, so this is where the twist
        // comes from when there is no rigidbody to read.
        private Robot _robot;
        private RosMessageTypes.Nav.OdometryMsg _message;
        private double[] _poseCovariance;
        private double[] _twistCovariance;
        
        private void Start()
        {
            // Find EnvROS
            _envROS ??= FindAnyObjectByType<EnvROS>();
            
            if (_envROS == null)
            {
                Debug.LogError($"[{name}] EnvROS not found in scene!");
                enabled = false;
                return;
            }
            
            // Detect prefix
            string prefix = "";
            if (autoDetectPrefix && _envROS != null)
            {
                prefix = _envROS.Prefix;
            }
            else if (!string.IsNullOrEmpty(customPrefix))
            {
                prefix = customPrefix;
            }
            
            // Build full topic names, with the one rule the whole project joins names with and the identity
            // of the robot this odometry belongs to.
            _fullTopicNames.Clear();
            // The name comes from the configuration in force, and the one this component was authored with
            // is only what a configuration that names nothing falls back to.
            string baseTopic = RobotSNAPTopics.Resolve(RosTopicSlot.Odom, topicName);
            _fullTopicNames.AddRange(RobotIdentity.StreamNamesFor(this, baseTopic, prefix));
            
            // Register publisher
            foreach (string topic in _fullTopicNames)
                _envROS.RegisterPublisher<RosMessageTypes.Nav.OdometryMsg>(topic);
            
            // Get references
            if (robotRigidbody == null)
            {
                robotRigidbody = GetComponent<Rigidbody>();
            }

            if (robotRigidbody == null)
            {
                _robot = GetComponentInParent<Robot>();
                if (_robot == null)
                {
                    Debug.LogWarning(
                        $"[{name}] Neither a Rigidbody nor a Robot to read a velocity from: the odometry " +
                        "twist will stay at zero");
                }
            }
            
            if (robotTransform == null)
            {
                // The pose that is published is the pose of the base of the robot, not of the root of its
                // prefab: the root of every robot of a scenario sits at the origin, because the base is what
                // the scenario moves, so reading it would report one robot at (0,0,0) however far it drove.
                _robot ??= GetComponentInParent<Robot>();
                robotTransform = _robot != null && _robot.RobotTransform != null ? _robot.RobotTransform : transform;
            }
            
            // Initialize covariance matrices
            InitializeCovariance();
            
            // Initialize message
            InitializeMessage();
            
            float rate = RobotSNAPTopics.ResolveFrequency(publishFrequencyHz);
            _publishInterval = 1f / rate;
            _nextPublishTime = NextSlot(Time.fixedTime, _publishInterval);
            
            Debug.Log($"[{name}] Publishing odometry to {string.Join(", ", _fullTopicNames)} at {rate} Hz");
        }

        /// <summary>
        /// Publishes the pose at the rate above, counted in simulated seconds and paced on the physics step.
        ///
        /// The pose has to come from the step it is stamped with, and the same step the scan of that instant
        /// was measured on. Published from the frame instead - which is what InvokeRepeating did - the pose
        /// was the one the last physics step had left behind while its stamp came from the frame's own
        /// moment, and the two grids do not line up once the session runs faster than it draws: at a scale of
        /// ten on a six hertz frame, ten poses of a whole simulated fifth of a second shared one stamp. A
        /// client placing a scan at the pose of its stamp then had no pose around that stamp at all, fell
        /// back on the newest one it held, and turned the lidar cloud by however much the robot had moved in
        /// between - the walls slid round with the robot.
        /// </summary>
        private void FixedUpdate()
        {
            if (_envROS == null || !_envROS.IsInitialized)
                return;

            if (Time.fixedTime < _nextPublishTime)
                return;

            _nextPublishTime = NextSlot(Time.fixedTime, _publishInterval);
            PublishOdometry();
        }

        /// <summary>
        /// The first instant of the shared simulated grid that comes after <paramref name="now"/>: the next
        /// multiple of <paramref name="interval"/> counted from zero.
        ///
        /// The scan of this robot is paced on that same grid, so the pose and the scan of one instant leave
        /// in one physics step and carry one stamp. Paced apart, each stream counted its own interval from
        /// whenever its component woke up, and a client reading a scan held no pose of that instant at all -
        /// only poses from before it - so it placed the lidar cloud with a pose the robot had left, up to a
        /// whole period of travel earlier.
        /// </summary>
        private static float NextSlot(float now, float interval)
        {
            return interval <= 0f
                ? float.PositiveInfinity
                : (Mathf.Floor(now / interval) + 1f) * interval;
        }
        
        private void InitializeCovariance()
        {
            // Covariance pour la pose (6x6 = 36 éléments)
            _poseCovariance = new double[36];
            // Covariance pour le twist (6x6 = 36 éléments)
            _twistCovariance = new double[36];
            
            if (publishCovariance)
            {
                // Position covariance (x, y, z) - indices 0, 7, 14
                _poseCovariance[0] = positionCovariance;
                _poseCovariance[7] = positionCovariance;
                _poseCovariance[14] = positionCovariance;
                
                // Orientation covariance (roll, pitch, yaw) - indices 21, 28, 35
                _poseCovariance[21] = orientationCovariance;
                _poseCovariance[28] = orientationCovariance;
                _poseCovariance[35] = orientationCovariance;
                
                // Linear velocity covariance (x, y, z) - indices 0, 7, 14
                _twistCovariance[0] = linearVelocityCovariance;
                _twistCovariance[7] = linearVelocityCovariance;
                _twistCovariance[14] = linearVelocityCovariance;
                
                // Angular velocity covariance (roll, pitch, yaw) - indices 21, 28, 35
                _twistCovariance[21] = angularVelocityCovariance;
                _twistCovariance[28] = angularVelocityCovariance;
                _twistCovariance[35] = angularVelocityCovariance;
            }
        }
        
        private void InitializeMessage()
        {
            _message = new RosMessageTypes.Nav.OdometryMsg
            {
                header = new RosMessageTypes.Std.HeaderMsg(),
                pose = new RosMessageTypes.Geometry.PoseWithCovarianceMsg(),
                twist = new RosMessageTypes.Geometry.TwistWithCovarianceMsg(),
                // The frame of this robot: the legacy one for the first robot, `robot_2/base_link` for a
                // second one, so two odometries cannot claim the same body.
                child_frame_id = RobotIdentity
                    .FrameIdFor(this, childFrameId, _envROS != null ? _envROS.Prefix : "")
                    .TrimStart('/')
            };
            
            if (publishCovariance)
            {
                _message.pose.covariance = _poseCovariance;
                _message.twist.covariance = _twistCovariance;
            }
        }
        
        private void PublishOdometry()
        {
            if (robotTransform == null || !robotTransform.gameObject.activeSelf)
                return;
            
            // Update header timestamp
            ROSTimeUtils.UpdateHeader(_message.header);
            _message.header.frame_id = RobotIdentity.FrameIdFor(this, frameId, _envROS.Prefix);
            
            // Position and orientation, in the world frame the scenario and the occupancy grid use: the
            // local position this used to publish only agreed with them while the robot happened to be a
            // child of an object sitting at the origin.
            Vector3 position = robotTransform.position;
            Quaternion rotation = robotTransform.rotation;
            
            _message.pose.pose.position = Util.Geometry.GetGeometryPoint(position.To<FLU>());
            _message.pose.pose.orientation = Util.Geometry.GetGeometryQuaternion(rotation.To<FLU>());
            
            // Velocity. This robot moves through an ArticulationBody, not a Rigidbody, so the rigidbody
            // branch used to report a still robot at all times; the agent is the fallback that actually
            // knows how fast it is going.
            if (robotRigidbody != null)
            {
                // Linear velocity in local frame
                Vector3 linearVelocityWorld = robotRigidbody.linearVelocity;
                Vector3 linearVelocityLocal = robotTransform.InverseTransformDirection(linearVelocityWorld);
                _message.twist.twist.linear = Util.Geometry.GetGeometryVector3(linearVelocityLocal.To<FLU>());
                
                // Angular velocity in local frame
                Vector3 angularVelocityWorld = robotRigidbody.angularVelocity;
                Vector3 angularVelocityLocal = robotTransform.InverseTransformDirection(angularVelocityWorld);
                _message.twist.twist.angular = Util.Geometry.GetGeometryVector3(
                    Vector3<FLU>.FromUnityAngularVelocity(angularVelocityLocal));
            }
            else if (_robot != null)
            {
                Vector3 linearVelocityLocal = robotTransform.InverseTransformDirection(_robot.Velocity);
                _message.twist.twist.linear = Util.Geometry.GetGeometryVector3(linearVelocityLocal.To<FLU>());

                Vector3 yawRateWorld = Vector3.up * _robot.AngularSpeed;
                Vector3 angularVelocityLocal = robotTransform.InverseTransformDirection(yawRateWorld);
                _message.twist.twist.angular = Util.Geometry.GetGeometryVector3(
                    Vector3<FLU>.FromUnityAngularVelocity(angularVelocityLocal));
            }
            else
            {
                // Zero velocities if no Rigidbody
                _message.twist.twist.linear = Util.Geometry.GetGeometryVector3(Vector3.zero);
                _message.twist.twist.angular = Util.Geometry.GetGeometryVector3(Vector3.zero);
            }
            
            // Publish, on every name this robot answers on.
            foreach (string topic in _fullTopicNames)
                _envROS.Publish(topic, _message);
        }
        
        [ContextMenu("Test Publish")]
        private void TestPublish()
        {
            PublishOdometry();
            Debug.Log($"[{name}] Test odometry sent to {string.Join(", ", _fullTopicNames)}");
        }
    }
}
