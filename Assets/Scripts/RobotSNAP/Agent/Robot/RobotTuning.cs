using System;
using Newtonsoft.Json;
using UnityEngine;

namespace RobotSNAP.Agents
{
    /// <summary>
    /// What a reader may change about one robot type without editing the prefab: the speeds its base may be
    /// commanded, the shape and rate of its lidar, and the radius within which it notices the other agents.
    ///
    /// It is deliberately a flat record with a JSON key spelled out per field, like the metrics the Python
    /// side reads: a refactor that renames a field must not rename the key a saved tuning was written under,
    /// and the file has to stay readable by whoever opens it.
    ///
    /// A tuning is only the *overrides*: the footprint and the mass still come from the built-in profile of
    /// the type, because those are properties of the imported model rather than choices a user makes here.
    /// <see cref="ApplyTo"/> is what lays these numbers over that profile, and it is the one place a tuned
    /// profile is built - so the page, the save and the reload all describe the same robot.
    /// </summary>
    [Serializable]
    public sealed class RobotTuning
    {
        /// <summary>Highest linear speed the base may be commanded, in m/s.</summary>
        [JsonProperty("max_linear_speed_mps")] public float MaxLinearSpeed = 1f;

        /// <summary>Highest angular speed the base may be commanded, in rad/s.</summary>
        [JsonProperty("max_angular_speed_radps")] public float MaxAngularSpeed = 2f;

        /// <summary>Field of view of the lidar, in degrees. 360 is a full circle, 270 the usual three-quarter.</summary>
        [JsonProperty("lidar_span_deg")] public float LidarSpanDegrees = 360f;

        /// <summary>Range of the lidar, in metres.</summary>
        [JsonProperty("lidar_range_m")] public float LidarRange = 3.5f;

        /// <summary>Number of rays of one scan.</summary>
        [JsonProperty("lidar_rays")] public int LidarRays = 180;

        /// <summary>Height of the laser plane above the ground, in metres.</summary>
        [JsonProperty("lidar_height_m")] public float LidarHeight = 0.79f;

        /// <summary>Publishing rate of the scan, in Hz.</summary>
        [JsonProperty("lidar_frequency_hz")] public float LidarFrequencyHz = 20f;

        /// <summary>Radius, in metres, within which the robot notices the other agents.</summary>
        [JsonProperty("detection_radius_m")] public float DetectionRadius = 5f;

        /// <summary>
        /// The tuning the built-in figures of a type make: what the page shows before anything was saved, and
        /// what the reset button puts back. The detection radius comes from the caller because it is a value
        /// the prefab carries rather than a figure the profile declares.
        /// </summary>
        public static RobotTuning Default(RobotProfile profile, float detectionRadius)
        {
            if (profile == null)
                profile = RobotProfiles.Default;

            return new RobotTuning
            {
                MaxLinearSpeed = profile.MaxLinearSpeed,
                MaxAngularSpeed = profile.MaxAngularSpeed,
                LidarSpanDegrees = profile.LidarSpanDegrees,
                LidarRange = profile.LidarRange,
                LidarRays = profile.LidarRays,
                LidarHeight = profile.LidarHeight,
                LidarFrequencyHz = profile.LidarFrequencyHz,
                DetectionRadius = detectionRadius
            };
        }

        /// <summary>
        /// The profile a type drives as once these overrides are laid over its built-in one: the footprint, the
        /// mass, the id and the name stay the profile's, and the commanded speeds and the lidar become these.
        /// A null profile reads as null, so a caller with no type is not handed a profile it did not ask for.
        /// </summary>
        public RobotProfile ApplyTo(RobotProfile baseProfile)
        {
            if (baseProfile == null)
                return null;

            return new RobotProfile(
                baseProfile.Id,
                baseProfile.DisplayName,
                baseProfile.Description,
                baseProfile.Radius,
                baseProfile.Mass,
                Mathf.Max(0.01f, MaxLinearSpeed),
                Mathf.Max(0.01f, MaxAngularSpeed),
                LidarSpanDegrees,
                LidarRange,
                LidarRays,
                LidarHeight,
                LidarFrequencyHz);
        }

        /// <summary>This tuning as the text of the file it is stored in.</summary>
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

        /// <summary>
        /// The tuning a file holds. A file that does not parse is reported as null rather than thrown, so the
        /// caller can fall back to the built-in figures of the type.
        /// </summary>
        public static RobotTuning FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<RobotTuning>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
