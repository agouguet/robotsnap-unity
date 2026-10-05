using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RobotSNAP.Metrics
{
    /// <summary>
    /// One finished episode of one robot, in the shape the dashboard, the Python client and the exported files
    /// all read. It is a plain data record on purpose: the accumulator computes it, the store keeps it, the
    /// exporter writes it and nothing here decides anything.
    ///
    /// The JSON key of every field is the contract the Python side is written against, so it is spelled out
    /// with a <see cref="JsonPropertyAttribute"/> rather than left to the field name a refactor could change.
    /// The value of every metric is a physical quantity with its unit in its name, never a bare number whose
    /// meaning a reader has to guess:
    ///
    ///   id                            this episode's identifier, unique within the session
    ///   index                         1-based place of the episode in its session, in the order the episodes
    ///                                 finished. Two episodes of one session share the scenario id, so the
    ///                                 index is what tells them apart on screen; it is display metadata and
    ///                                 never a key - <c>id</c> is the key
    ///   scenario                      id of the scenario that was applied
    ///   robot                         roster id of the robot the episode tracked (<c>robot_1</c>)
    ///   robots                        roster ids of every robot the episode saw, the tracked one first. The
    ///                                 scalar metrics above describe <c>robot</c> only; this list says how
    ///                                 many robot tracks <c>trajectories</c> carries, which is what a scenario
    ///                                 with several robots produces. Optional: an export written before it
    ///                                 existed is still an episode
    ///   started_at                    ISO-8601 UTC instant the episode started
    ///   outcome                       how the episode ended; see <see cref="MetricsContract"/>
    ///   world_seconds                 simulation seconds between the first and last sample, i.e. how far the
    ///                                 world moved - the value an episode time limit is measured in
    ///   wall_seconds                  wall-clock seconds the same episode took, i.e. how long a human waited
    ///   steps                         number of control steps sampled
    ///   path_length_m                 length of the polyline the robot actually travelled, in metres
    ///   straight_line_m               distance from the episode's start pose to its goal, in metres: the
    ///                                 shortest a perfect run could be. Falls back to the end pose when the
    ///                                 robot ended without a goal.
    ///   avg_speed_mps                 <c>path_length_m / world_seconds</c>
    ///   max_speed_mps                 fastest instantaneous speed between two samples
    ///   min_human_distance_m          closest the robot ever came to a human, over the whole episode.
    ///                                 <c>-1</c> when the episode saw no human at all
    ///   avg_human_distance_m          mean over samples of the distance to the nearest human. <c>-1</c> when
    ///                                 the episode saw no human at all
    ///   personal_space_intrusions     number of times the robot entered a human's personal space
    ///   personal_space_seconds        simulated seconds spent with at least one human inside that space
    ///   personal_space_radius_m       the threshold those last two were computed with, so a reader never has
    ///                                 to guess which definition produced them
    ///   trajectory_stride             subsampling factor applied to every trajectory, 1 = nothing dropped
    ///   trajectories                  agent key -> [[t, x, z], ...], t in simulated seconds, x/z in world
    ///                                 metres in the frame the map and the occupancy grid share, so a top-down
    ///                                 view draws the paths on the map without a conversion; see
    ///                                 <see cref="MetricsContract.RobotTrackKey"/>. Points are stored at
    ///                                 millimetre and millisecond precision
    ///   session                       id of the session this episode belongs to
    /// </summary>
    public sealed class EpisodeMetrics
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("index")] public int Index;
        [JsonProperty("scenario")] public string Scenario;
        [JsonProperty("robot")] public string Robot;
        [JsonProperty("robots")] public List<string> Robots = new();
        [JsonProperty("started_at")] public string StartedAt;
        [JsonProperty("outcome")] public string Outcome;
        [JsonProperty("world_seconds")] public double WorldSeconds;
        [JsonProperty("wall_seconds")] public double WallSeconds;
        [JsonProperty("steps")] public int Steps;
        [JsonProperty("path_length_m")] public double PathLengthMetres;
        [JsonProperty("straight_line_m")] public double StraightLineMetres;
        [JsonProperty("avg_speed_mps")] public double AverageSpeedMetresPerSecond;
        [JsonProperty("max_speed_mps")] public double MaxSpeedMetresPerSecond;
        [JsonProperty("min_human_distance_m")] public double MinHumanDistanceMetres;
        [JsonProperty("avg_human_distance_m")] public double AverageHumanDistanceMetres;
        /// <summary>
        /// Smallest gap left between the robot's outline and a crowd member's, in metres, over the episode.
        ///
        /// It is <c>min_human_distance_m</c> minus the two footprint radii, so it is the distance somebody
        /// watching the passage would measure between the bodies; it goes negative when the bodies overlap.
        /// It is the quantity <see cref="PersonalSpaceIntrusions"/> is counted on, so a reader can check the
        /// count instead of trusting it.
        /// </summary>
        [JsonProperty("min_clearance_m")] public double MinClearanceMetres;
        /// <summary>Radius of the tracked robot's footprint, in metres, as the clearance was measured with it.</summary>
        [JsonProperty("robot_radius_m")] public double RobotRadiusMetres;
        /// <summary>Radius of the crowd member that came closest, in metres.</summary>
        [JsonProperty("human_radius_m")] public double HumanRadiusMetres;
        [JsonProperty("personal_space_intrusions")] public int PersonalSpaceIntrusions;
        [JsonProperty("personal_space_seconds")] public double PersonalSpaceSeconds;
        [JsonProperty("personal_space_radius_m")] public double PersonalSpaceRadiusMetres;
        [JsonProperty("trajectory_stride")] public int TrajectoryStride = 1;
        [JsonProperty("trajectories")] public Dictionary<string, List<double[]>> Trajectories = new();
        /// <summary>
        /// Where this episode's trajectory sits in the session archive, or null while it has not been
        /// archived yet - which is what tells the exporter there is still work to do for it.
        ///
        /// When it is set, the archive holds the points and <see cref="Trajectories"/> is emptied, which is
        /// what keeps a ten-thousand-episode session out of RAM. A reader must therefore prefer the inline
        /// map when it is there and fall back to this reference, the same rule the catalogue line follows.
        /// </summary>
        [JsonProperty("trajectory_ref")] public TrajectoryRef TrajectoryRef;
        [JsonProperty("session")] public string Session;

        /// <summary>Sentinel a distance metric carries when the episode held no human to measure against.</summary>
        public const double NoHumanDistance = -1.0;

        /// <summary>The document as it goes on the wire and into the session file.</summary>
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None);
    }
}
