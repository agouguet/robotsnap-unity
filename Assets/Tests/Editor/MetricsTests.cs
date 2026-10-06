using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RobotSNAP.Agents;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using RobotSNAP.Metrics;
using RobotSNAP.ROS;
using UnityEngine;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// Guards the metrics layer's arithmetic and its document shape. The numbers a benchmark reports are read
    /// long after the run that produced them, so a definition that drifts quietly - a speed averaged over wall
    /// time, a personal space counted per sample instead of per entry - is a result nobody can reproduce. Every
    /// test here pins one of those definitions.
    /// </summary>
    public sealed class MetricsTests
    {
        private static readonly System.Type RosterSlotType =
            typeof(RobotRoster).GetNestedType("Slot", BindingFlags.NonPublic);

        private static readonly FieldInfo RosterSlots =
            typeof(RobotRoster).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<GameObject> _created = new List<GameObject>();
        private ScenarioManager _sessionManager;
        private Supervisor _sessionSupervisor;

        [TearDown]
        public void ResetStore()
        {
            MetricsStore.Instance.Clear();
        }

        /// <summary>
        /// A component added outside play mode does not run its own lifecycle, so the roster a case plants is
        /// cleared here rather than left to the objects that case built.
        /// </summary>
        [TearDown]
        public void DestroyTheSceneBuiltByACase()
        {
            SetRosterCurrent(null);

            // A supervisor added without a configuration makes its own, and that instance is not owned by the
            // scene, so it is read before the object carrying it goes away.
            SimulationConfig config = _sessionSupervisor != null ? _sessionSupervisor.ActiveConfig : null;
            _sessionSupervisor = null;
            _sessionManager = null;

            foreach (GameObject created in _created)
                if (created != null) UnityEngine.Object.DestroyImmediate(created);

            _created.Clear();

            if (config != null && config.name == "DefaultConfig")
                UnityEngine.Object.DestroyImmediate(config);
        }

        private static EpisodeMetrics Synthetic(string id = "ep")
        {
            return new EpisodeMetrics
            {
                Id = id,
                Index = 7,
                Session = "s_test",
                Scenario = "corridor",
                Robot = "robot_1",
                StartedAt = "2026-09-30T10:00:00.0000000Z",
                Outcome = MetricsContract.OutcomeGoal,
                WorldSeconds = 12.0,
                WallSeconds = 1.2,
                Steps = 600,
                PathLengthMetres = 15.0,
                StraightLineMetres = 10.0,
                AverageSpeedMetresPerSecond = 1.25,
                MaxSpeedMetresPerSecond = 1.6,
                MinHumanDistanceMetres = 0.7,
                AverageHumanDistanceMetres = 3.2,
                PersonalSpaceIntrusions = 2,
                PersonalSpaceSeconds = 1.5,
                PersonalSpaceRadiusMetres = 0.5,
                Robots = new List<string> { "robot_1" },
                Trajectories = new Dictionary<string, List<double[]>>
                {
                    ["robot_1"] = new List<double[]> { new[] { 0.0, 0.0, 0.0 } },
                },
            };
        }

        // -- the record's contract ------------------------------------------

        [Test]
        public void TheEpisodeDocumentCarriesTheContractKeys()
        {
            JObject document = JObject.Parse(Synthetic("ep_1").ToJson());

            foreach (string key in new[]
                     {
                         "id", "index", "scenario", "robot", "started_at", "outcome", "world_seconds",
                         "wall_seconds", "steps", "path_length_m", "straight_line_m",
                         "avg_speed_mps", "max_speed_mps", "min_human_distance_m",
                         "avg_human_distance_m", "min_clearance_m", "robot_radius_m",
                         "human_radius_m", "personal_space_intrusions",
                         "personal_space_seconds", "robots", "trajectories",
                     })
            {
                Assert.That(document.ContainsKey(key), Is.True, $"missing contract key '{key}'");
            }

            Assert.That((string)document["id"], Is.EqualTo("ep_1"));
            Assert.That((int)document["index"], Is.EqualTo(7), "the session ordinal travels with the document");
        }

        // -- the session ordinal ---------------------------------------------

        [Test]
        public void TheStoreHandsOutOnePlacePerEpisodeInOrder()
        {
            MetricsStore store = MetricsStore.Instance;

            string first = store.NextEpisodeId(out int firstIndex);
            string second = store.NextEpisodeId(out int secondIndex);

            Assert.That(firstIndex, Is.EqualTo(1), "the first episode of a session is number one");
            Assert.That(secondIndex, Is.EqualTo(2), "and the counter the id is built from is the same one");
            Assert.That(first, Does.EndWith("-0001"));
            Assert.That(second, Does.EndWith("-0002"));

            store.Clear();
            store.NextEpisodeId(out int afterClear);
            Assert.That(afterClear, Is.EqualTo(1), "a new session numbers its episodes from the start again");
        }

        [Test]
        public void ATrajectoryIsATimeAndAPlanarPosition()
        {
            JObject document = JObject.Parse(Synthetic().ToJson());
            JArray point = (JArray)document["trajectories"]["robot_1"][0];

            Assert.That(point.Count, Is.EqualTo(3), "a trajectory point is [t, x, z]");
            Assert.That((double)point[0], Is.EqualTo(0.0));
        }

        [Test]
        public void AnEmptySessionIsAListNotAnError()
        {
            Assert.That(MetricsStore.Instance.Episodes, Is.Empty);
            Assert.That(MetricsStore.Instance.Get("nothing"), Is.Null);
        }

        [Test]
        public void TheStoreKeepsWhatItWasGivenAndClearsToANewSession()
        {
            MetricsStore store = MetricsStore.Instance;
            string before = store.SessionId;
            store.Add(Synthetic("ep_a"));

            Assert.That(store.Count, Is.EqualTo(1));
            Assert.That(store.Get("ep_a"), Is.Not.Null);

            int cleared = store.Clear();

            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(store.Episodes, Is.Empty);
            Assert.That(store.SessionId, Is.Not.EqualTo(before), "a cleared session starts a new identity");
        }

        // -- trajectories ---------------------------------------------------

        [Test]
        public void ATrajectoryStaysBoundedAndKeepsBothEnds()
        {
            var buffer = new TrajectoryBuffer(4);
            for (int step = 0; step < 64; step++)
                buffer.Add(step, step, 0.0);

            Assert.That(buffer.Count, Is.LessThanOrEqualTo(4));
            Assert.That(buffer.Points[0][0], Is.EqualTo(0.0), "the first sample survives every halving");
            Assert.That(buffer.Stride, Is.GreaterThan(1), "beyond the budget the resolution is halved");
        }

        // -- the arithmetic -------------------------------------------------

        [Test]
        public void PathSpeedAndStraightLineFollowTheirDefinitions()
        {
            var accumulator = NewAccumulator();
            accumulator.Sample(0.0, new Vector3(0f, 0f, 0f), null);
            accumulator.Sample(1.0, new Vector3(1f, 0f, 0f), null);
            accumulator.Sample(2.0, new Vector3(1f, 0f, 2f), null);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 2.0, 0.5);

            Assert.That(episode.PathLengthMetres, Is.EqualTo(3.0).Within(1e-6), "1 m then 2 m");
            Assert.That(episode.WorldSeconds, Is.EqualTo(2.0).Within(1e-6));
            Assert.That(episode.AverageSpeedMetresPerSecond, Is.EqualTo(1.5).Within(1e-6));
            Assert.That(episode.Steps, Is.EqualTo(3));
        }

        [Test]
        public void TheStraightLineMetricMeasuresTheGoalNotTheEffort()
        {
            var accumulator = NewAccumulator();
            accumulator.SetGoal(new Vector3(3f, 0f, 4f));
            accumulator.Sample(0.0, Vector3.zero, null);
            accumulator.Sample(5.0, new Vector3(1f, 0f, 1f), null);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 5.0, 0.5);

            Assert.That(episode.StraightLineMetres, Is.EqualTo(5.0).Within(1e-6), "3-4-5 from the start pose");
            Assert.That(episode.PathLengthMetres, Is.LessThan(episode.StraightLineMetres + 1e-6));
        }

        [Test]
        public void AnEpisodeWithNoHumanSaysSoInsteadOfReportingZero()
        {
            var accumulator = NewAccumulator();
            accumulator.Sample(0.0, Vector3.zero, null);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 1.0, 0.1);

            Assert.That(episode.MinHumanDistanceMetres, Is.EqualTo(EpisodeMetrics.NoHumanDistance));
            Assert.That(episode.AverageHumanDistanceMetres, Is.EqualTo(EpisodeMetrics.NoHumanDistance));
        }

        // -- a run is as long as the session, not as long as its first event -----

        /// <summary>
        /// The trajectory of a session is the whole session, including what came after the goal.
        ///
        /// The recorder used to close an episode the instant its robot arrived, collided or left the map, so a
        /// driver who kept going - the ordinary case when a person is at the controls - had the rest of the run
        /// thrown away. Events now name the run and let it continue; only the end of the session closes it.
        /// </summary>
        [Test]
        public void AnEpisodeKeepsTheTrajectoryItDroveAfterTheEvent()
        {
            var accumulator = NewAccumulator();

            accumulator.Sample(0.0, new Vector3(0f, 0f, 0f), null);
            accumulator.Sample(1.0, new Vector3(1f, 0f, 0f), null);

            // The robot arrives at the goal...
            accumulator.LatchOutcome(MetricsContract.OutcomeGoal);

            // ...and is driven on for two more metres, which is the part the old recorder dropped.
            accumulator.Sample(2.0, new Vector3(2f, 0f, 0f), null);
            accumulator.Sample(3.0, new Vector3(3f, 0f, 0f), null);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeStopped, 3.0, 0.4);

            Assert.That(episode.PathLengthMetres, Is.EqualTo(3.0).Within(1e-6),
                "the path covers the whole run, including the metres driven after the goal");
            Assert.That(episode.WorldSeconds, Is.EqualTo(3.0).Within(1e-6));
            Assert.That(episode.Trajectories["robot_1"].Count, Is.EqualTo(4),
                "every sample of the run is kept, not only the ones before the event");
            Assert.That(episode.Outcome, Is.EqualTo(MetricsContract.OutcomeGoal),
                "the run is filed under what it achieved, not under the button that stopped it");
        }

        /// <summary>The first thing that happens names the run; what happens next does not rename it.</summary>
        [Test]
        public void TheFirstEventNamesTheRunAndLaterOnesDoNot()
        {
            var accumulator = NewAccumulator();

            accumulator.LatchOutcome(MetricsContract.OutcomeCollision);
            accumulator.LatchOutcome(MetricsContract.OutcomeGoal);
            accumulator.LatchOutcome(MetricsContract.OutcomeOutOfBounds);
            accumulator.LatchOutcome(null);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeTimeout, 4.0, 0.4);

            Assert.That(episode.Outcome, Is.EqualTo(MetricsContract.OutcomeCollision),
                "the collision came first, so a goal reached afterwards does not erase it");
        }

        /// <summary>A run nothing happened to is still filed under why it stopped.</summary>
        [Test]
        public void ARunWithNoEventIsFiledUnderWhyItStopped()
        {
            var accumulator = NewAccumulator();
            accumulator.Sample(0.0, Vector3.zero, null);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeStopped, 1.0, 0.1);

            Assert.That(episode.Outcome, Is.EqualTo(MetricsContract.OutcomeStopped));
        }

        [Test]
        public void PersonalSpaceCountsEntriesNotSamples()
        {
            var accumulator = NewAccumulator(personalSpaceRadius: 0.5);
            var outside = new List<HumanSample> { new HumanSample(3, new Vector3(1f, 0f, 0f)) };
            var inside = new List<HumanSample> { new HumanSample(3, new Vector3(0.4f, 0f, 0f)) };

            accumulator.Sample(0.0, Vector3.zero, outside);
            accumulator.Sample(1.0, Vector3.zero, inside);
            accumulator.Sample(2.0, Vector3.zero, inside);
            accumulator.Sample(3.0, Vector3.zero, outside);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 3.0, 0.2);

            Assert.That(episode.PersonalSpaceIntrusions, Is.EqualTo(1), "one entry, not one per sample");
            Assert.That(episode.PersonalSpaceSeconds, Is.EqualTo(2.0).Within(1e-6));
            Assert.That(episode.MinHumanDistanceMetres, Is.EqualTo(0.4).Within(1e-6));
            Assert.That(episode.AverageHumanDistanceMetres, Is.EqualTo(0.7).Within(1e-6));
        }

        /// <summary>
        /// A clearance is measured between the bodies, not between the centres.
        ///
        /// Two agents one metre apart with 0.3 m footprints are 0.4 m apart in the only sense a bystander can
        /// see: the metric that read centres called that a comfortable metre and never reported the passage.
        /// The footprints now travel with the sample, so the same geometry reads as what it is - and a pass
        /// that really does leave room still reads as room.
        /// </summary>
        [Test]
        public void TheClearanceIsMeasuredBetweenTheBodiesNotTheCentres()
        {
            var accumulator = NewAccumulator(personalSpaceRadius: 0.5);
            var robots = new List<RobotSample> { new RobotSample("robot_1", Vector3.zero, 0.3f) };
            var broadBodies = new List<HumanSample> { new HumanSample(1, new Vector3(1f, 0f, 0f), 0.3f) };
            var slimBodies = new List<HumanSample> { new HumanSample(1, new Vector3(1f, 0f, 0f), 0.05f) };

            accumulator.Sample(0.0, robots, broadBodies);
            accumulator.Sample(1.0, robots, slimBodies);
            accumulator.Sample(2.0, robots, broadBodies);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 2.0, 0.1);

            Assert.That(episode.MinHumanDistanceMetres, Is.EqualTo(1.0).Within(1e-6),
                "the distance metric still reports centre to centre");
            Assert.That(episode.MinClearanceMetres, Is.EqualTo(0.4).Within(1e-6),
                "the clearance takes both footprints off the centre distance");
            Assert.That(episode.RobotRadiusMetres, Is.EqualTo(0.3).Within(1e-6));
            Assert.That(episode.HumanRadiusMetres, Is.EqualTo(0.3).Within(1e-6),
                "the radius recorded is the one of the human that came closest");
            Assert.That(episode.PersonalSpaceIntrusions, Is.EqualTo(2),
                "0.4 m of air is inside a 0.5 m personal space, and the slim pass is not");
        }

        [Test]
        public void TheHumanTrajectoryIsFiledUnderItsOwnKey()
        {
            var accumulator = NewAccumulator();
            var humans = new List<HumanSample> { new HumanSample(7, new Vector3(2f, 0f, 1f)) };

            accumulator.Sample(0.0, Vector3.zero, humans);
            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 1.0, 0.1);

            Assert.That(episode.Trajectories.ContainsKey("robot_1"), Is.True);
            Assert.That(episode.Trajectories.ContainsKey("human_7"), Is.True);
            Assert.That(episode.Trajectories["human_7"][0][2], Is.EqualTo(1.0).Within(1e-6));
        }

        [Test]
        public void EveryRobotOfTheFleetGetsItsOwnTrackAndTheScalarsStayTheTrackedRobots()
        {
            // The accumulator was built for robot_1, so robot_1's motion is the path the metrics describe;
            // robot_2's path is recorded beside it without ever entering the numbers.
            var accumulator = NewAccumulator();
            var fleet = new List<RobotSample>
            {
                new RobotSample("robot_1", new Vector3(0f, 0f, 0f)),
                new RobotSample("robot_2", new Vector3(0f, 0f, 5f)),
            };
            accumulator.Sample(0.0, fleet, null);

            fleet[0] = new RobotSample("robot_1", new Vector3(1f, 0f, 0f));
            fleet[1] = new RobotSample("robot_2", new Vector3(0f, 0f, -5f));
            accumulator.Sample(1.0, fleet, null);

            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 1.0, 0.1);

            Assert.That(episode.Trajectories.ContainsKey("robot_1"), Is.True);
            Assert.That(episode.Trajectories.ContainsKey("robot_2"), Is.True,
                "a scenario with two robots writes two robot tracks, not one");
            Assert.That(episode.Trajectories["robot_2"][1][2], Is.EqualTo(-5.0).Within(1e-6));
            Assert.That(episode.PathLengthMetres, Is.EqualTo(1.0).Within(1e-6),
                "the path is the tracked robot's 1 m, not the fleet's 11 m");
            Assert.That(episode.Robots, Is.EqualTo(new List<string> { "robot_1", "robot_2" }),
                "the tracked robot opens the list of robots the episode saw");
        }

        [Test]
        public void TheTrackedRobotOpensTheDocumentEvenWhenTheFleetListsItLast()
        {
            var accumulator = NewAccumulator();
            var fleet = new List<RobotSample>
            {
                new RobotSample("robot_2", new Vector3(0f, 0f, 5f)),
                new RobotSample("robot_1", new Vector3(2f, 0f, 0f)),
            };

            accumulator.Sample(0.0, fleet, null);
            EpisodeMetrics episode = accumulator.Finish(MetricsContract.OutcomeGoal, 1.0, 0.1);

            Assert.That(episode.Robots[0], Is.EqualTo("robot_1"),
                "the robot the scalar metrics name is the first one a reader meets");
            Assert.That(episode.Robots, Is.EqualTo(new List<string> { "robot_1", "robot_2" }));
        }

        // -- export ---------------------------------------------------------

        /// <summary>
        /// The export appends instead of rewriting: a session line, a catalogue line and a trajectory record,
        /// and none of the whole-session files. Rewriting the session after every episode is what made a
        /// long campaign pay for its whole history on every finish.
        /// </summary>
        [Test]
        public void TheExportAppendsACatalogueLineAndATrajectoryRecordAndNoSessionFile()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                EpisodeMetrics episode = Synthetic("ep_export");
                store.Add(episode);

                MetricsExportReport report = MetricsExporter.Export(store, root);

                Assert.That(report.Directory, Is.EqualTo(root));
                Assert.That(report.SessionsFile, Is.EqualTo(Path.Combine(root, "sessions.jsonl")));
                Assert.That(report.CatalogueFile, Is.EqualTo(Path.Combine(root, "catalogue.jsonl")));
                Assert.That(File.Exists(report.SessionsFile), Is.True);
                Assert.That(File.Exists(report.CatalogueFile), Is.True);

                Assert.That(episode.TrajectoryRef, Is.Not.Null, "the episode is archived");
                string archive = Path.Combine(
                    root,
                    episode.TrajectoryRef.File.Replace('/', Path.DirectorySeparatorChar));
                Assert.That(File.Exists(archive), Is.True);
                Assert.That(episode.Trajectories, Is.Empty, "the map leaves RAM once the archive holds it");

                Assert.That(
                    File.Exists(Path.Combine(root, MetricsContract.SessionFileName(store.SessionId))),
                    Is.False,
                    "the whole-session JSON is no longer written");
                Assert.That(
                    File.Exists(Path.Combine(root, MetricsContract.SessionCsvFileName(store.SessionId))),
                    Is.False,
                    "the whole-session CSV is no longer written");
                Assert.That(
                    File.Exists(Path.Combine(root, MetricsContract.IndexFileName)),
                    Is.False,
                    "the index is no longer written");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// A session with nothing to record is not announced at all. The session line is what makes the folder
        /// list the session in the analysis tab, so a line written for an empty store is a session a reader can
        /// open and find nothing in - and the write that happens when the application goes away does this to
        /// every run that never filed an episode. Exporting an empty store therefore leaves the folder as it
        /// found it, while still reporting where it would have written.
        /// </summary>
        [Test]
        public void AnExportWithNothingToRecordAnnouncesNoSession()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Clear();
                Assert.That(store.Count, Is.Zero, "the case starts from a session with nothing in it");

                MetricsExportReport report = MetricsExporter.Export(store, root);

                Assert.That(report.Directory, Is.EqualTo(root),
                    "the export still names the folder it was asked for");
                Assert.That(File.Exists(Path.Combine(root, TrajectoryArchive.SessionsFileName)), Is.False,
                    "an empty session leaves no session line behind");
                Assert.That(File.Exists(Path.Combine(root, TrajectoryArchive.CatalogueFileName)), Is.False,
                    "and no catalogue");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// The catalogue is one line per finished episode, appended once and never rewritten, and the line
        /// defers the trajectory to the archive instead of repeating it.
        /// </summary>
        [Test]
        public void TheCatalogueIsAppendedLineByLineAndDefersTheTrajectoryToTheArchive()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(Synthetic("ep_one"));
                store.Add(Synthetic("ep_two"));

                MetricsExporter.Export(store, root);
                MetricsExporter.Export(store, root); // a second export must not rewrite the lines already there

                string[] lines = File.ReadAllLines(Path.Combine(root, "catalogue.jsonl"));
                Assert.That(lines.Length, Is.EqualTo(2), "one line per episode, appended once");

                var first = JObject.Parse(lines[0]);
                var second = JObject.Parse(lines[1]);
                Assert.That((string)first["id"], Is.EqualTo("ep_one"));
                Assert.That(first["trajectories"], Is.Null, "the archived map is not repeated in the line");
                Assert.That((string)first["trajectory_ref"]["file"], Is.EqualTo("trajectories/s_test.rbt"),
                    "the reference is relative to the export root");
                Assert.That((string)first["trajectory_ref"]["encoding"], Is.EqualTo("int16mm"));
                Assert.That((int)first["trajectory_ref"]["agents"], Is.EqualTo(1));
                Assert.That((long)first["trajectory_ref"]["length"], Is.GreaterThan(0));
                Assert.That((long)second["trajectory_ref"]["offset"],
                    Is.GreaterThan((long)first["trajectory_ref"]["offset"]),
                    "the second record follows the first in the same file");

                string[] sessions = File.ReadAllLines(Path.Combine(root, "sessions.jsonl"));
                Assert.That(sessions.Length, Is.EqualTo(1), "a session is announced once, not once per export");
                var session = JObject.Parse(sessions[0]);
                Assert.That((string)session["id"], Is.EqualTo(store.SessionId));
                Assert.That((int)session["schema"], Is.EqualTo(1));
                Assert.That(session["started_at"], Is.Not.Null);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// The archive round-trips every agent of an episode after the trajectory buffer subsampled far more
        /// samples than its capacity, which is the shape a long run really produces.
        /// </summary>
        [Test]
        public void TheArchiveRoundTripsEveryAgentAtMillimetreAndMillisecondPrecision()
        {
            string root = NewExportRoot();
            try
            {
                var robot = new TrajectoryBuffer(8);
                var human = new TrajectoryBuffer(8);
                for (int step = 0; step < 200; step++)
                {
                    robot.Add(step * 0.02, step * 0.05, System.Math.Sin(step * 0.1));
                    human.Add(step * 0.02, 2.0 - step * 0.01, 1.5);
                }

                EpisodeMetrics episode = TrajectoryEpisode("s_roundtrip", "ep_roundtrip");
                episode.TrajectoryStride = robot.Stride;
                episode.Trajectories["robot_1"] = new List<double[]>(robot.Points);
                episode.Trajectories["human_3"] = new List<double[]>(human.Points);

                var archive = new TrajectoryArchive(root);
                TrajectoryRef reference = archive.Append(episode);

                Assert.That(reference, Is.Not.Null, archive.LastError);
                Assert.That(reference.File, Is.EqualTo("trajectories/s_roundtrip.rbt"));
                Assert.That(reference.Agents, Is.EqualTo(2));
                Assert.That(reference.Points, Is.EqualTo(robot.Count + human.Count));
                Assert.That(reference.Offset, Is.EqualTo(12), "the first record follows the 12-byte file header");

                Assert.That(archive.TryRead(reference, out Dictionary<string, List<double[]>> tracks), Is.True);
                Assert.That(tracks.Keys, Is.EquivalentTo(new[] { "robot_1", "human_3" }));
                AssertSamePoints(episode.Trajectories["robot_1"], tracks["robot_1"]);
                AssertSamePoints(episode.Trajectories["human_3"], tracks["human_3"]);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>A record whose bytes are not all there is skipped, never decoded into a wrong path.</summary>
        [Test]
        public void ATruncatedRecordIsRefused()
        {
            string root = NewExportRoot();
            try
            {
                var archive = new TrajectoryArchive(root);
                TrajectoryRef reference = archive.Append(TrajectoryEpisode("s_trunc", "ep_trunc"));
                Assert.That(reference, Is.Not.Null, archive.LastError);

                string path = Path.Combine(
                    root,
                    reference.File.Replace('/', Path.DirectorySeparatorChar));
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                    stream.SetLength(stream.Length - 4);

                Assert.That(archive.TryRead(reference, out _), Is.False, "a short record is not a record");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>The store hands a reader the archived tracks once the inline map has left RAM.</summary>
        [Test]
        public void TheStoreReadsBackTheArchivedTracksAfterTheMapLeftRam()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                EpisodeMetrics episode = TrajectoryEpisode("s_read", "ep_read");
                int expected = episode.Trajectories["robot_1"].Count;
                store.Add(episode);

                MetricsExporter.Export(store, root);

                Assert.That(episode.Trajectories, Is.Empty);
                IReadOnlyDictionary<string, List<double[]>> tracks = store.TracksOf(episode);
                Assert.That(tracks.ContainsKey("robot_1"), Is.True);
                Assert.That(tracks["robot_1"].Count, Is.EqualTo(expected));
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// A root the process cannot write to must not cost the run its trajectories: the export falls back to
        /// the persistent-data folder, says so, and is not treated as a failure. The impossible root is a path
        /// under an ordinary file, which makes <c>Directory.CreateDirectory</c> fail deterministically on every
        /// platform this project builds for.
        /// </summary>
        [Test]
        public void AnUnwritableRootFallsBackToPersistentDataInsteadOfLosingTheTrajectory()
        {
            string blocker = Path.Combine(
                Path.GetTempPath(), "robotsnap_blocker_" + System.Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(blocker, "an ordinary file, so no folder can be made under it");

            string impossible = Path.Combine(blocker, "sub");
            string fallback = Path.Combine(Application.persistentDataPath, "robotsnap", "metrics");
            try
            {
                MetricsStore store = MetricsStore.Instance;
                EpisodeMetrics episode = Synthetic("ep_fallback");
                store.Add(episode);

                MetricsExportReport report = MetricsExporter.Export(store, impossible);

                Assert.That(report.UsedFallback, Is.True, "the export says it did not land where it was asked to");
                Assert.That(report.Directory, Is.EqualTo(fallback), "it landed in the persistent-data fallback");
                Assert.That(MetricsExporter.LastError, Is.Null, "a fallback that worked is not a failure");

                Assert.That(File.Exists(Path.Combine(fallback, "sessions.jsonl")), Is.True);
                Assert.That(File.Exists(Path.Combine(fallback, "catalogue.jsonl")), Is.True);
                Assert.That(episode.TrajectoryRef, Is.Not.Null, "the trajectory was archived, not dropped");
                Assert.That(
                    File.Exists(Path.Combine(
                        fallback, episode.TrajectoryRef.File.Replace('/', Path.DirectorySeparatorChar))),
                    Is.True,
                    "the archive record is under the fallback root");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(fallback);

                // The test created the fallback's parent folder; it is removed too when it holds nothing, so
                // the case leaves the machine as it found it.
                string parent = Path.GetDirectoryName(fallback);
                if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                    Directory.Delete(parent);

                if (File.Exists(blocker))
                    File.Delete(blocker);
            }
        }

        /// <summary>
        /// The marker of an unarchived episode is a null reference, and the exporter must not care why it is
        /// null: an episode a previous call left behind - exactly what a failed archive leaves - is written by
        /// the next call, and the episodes that already have a record are not written again.
        /// </summary>
        [Test]
        public void AnEpisodeLeftUnarchivedByAnEarlierCallIsWrittenByTheNextOne()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(Synthetic("ep_first"));
                MetricsExporter.Export(store, root);

                // The second episode the first export did not reach. A failed archive takes the reference back
                // and keeps the map, so this is the state a retry has to look for.
                EpisodeMetrics leftover = Synthetic("ep_leftover");
                store.Add(leftover);
                leftover.TrajectoryRef = null;

                Assert.That(leftover.Trajectories.Count, Is.GreaterThan(0),
                    "an unarchived episode still holds its map");

                MetricsExporter.Export(store, root);

                Assert.That(leftover.TrajectoryRef, Is.Not.Null, "the next call archives what was left behind");
                Assert.That(leftover.Trajectories, Is.Empty, "the map leaves RAM once the record holds it");
                Assert.That(File.ReadAllLines(Path.Combine(root, "catalogue.jsonl")).Length, Is.EqualTo(2),
                    "each episode gets one line, and the archived one is not appended again");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>A root the tab has not written to is empty, not an error.</summary>
        [Test]
        public void ScanReadsAnAbsentRootAsEmpty()
        {
            string missing = Path.Combine(
                Path.GetTempPath(), "robotsnap_scan_missing_" + System.Guid.NewGuid().ToString("N"));

            Assert.That(TrajectoryArchive.Scan(missing).IsEmpty, Is.True);
            Assert.That(TrajectoryArchive.Scan(null).IsEmpty, Is.True);
            Assert.That(TrajectoryArchive.Scan(string.Empty).IsEmpty, Is.True);
        }

        /// <summary>
        /// The summary is what a delete is decided on, so it has to count the lines a reader would lose and the
        /// bytes those lines point at.
        /// </summary>
        [Test]
        public void ScanCountsSessionsEpisodesAndBytes()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(Synthetic("ep_scan_one"));
                store.Add(Synthetic("ep_scan_two"));
                MetricsExporter.Export(store, root);

                TrajectoryArchive.ArchiveSummary summary = TrajectoryArchive.Scan(root);

                Assert.That(summary.IsEmpty, Is.False);
                Assert.That(summary.Sessions, Is.EqualTo(1), "both episodes share the one session line");
                Assert.That(summary.Episodes, Is.EqualTo(2), "one catalogue line per episode");
                Assert.That(summary.Bytes, Is.GreaterThan(0L), "the archives under trajectories/ are counted");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// Deleting is the one thing that removes an export, and it has to be safe to ask twice: the second
        /// call finds an empty folder and says so rather than failing.
        /// </summary>
        [Test]
        public void DeleteAllRemovesTheRecordsAndIsIdempotent()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(Synthetic("ep_delete"));
                MetricsExporter.Export(store, root);

                Assert.That(TrajectoryArchive.DeleteAll(root), Is.True);
                Assert.That(TrajectoryArchive.Scan(root).IsEmpty, Is.True,
                    "the summary reads empty once the files are gone");
                Assert.That(File.Exists(Path.Combine(root, "catalogue.jsonl")), Is.False);
                Assert.That(File.Exists(Path.Combine(root, "sessions.jsonl")), Is.False);
                Assert.That(
                    Directory.GetFiles(Path.Combine(root, TrajectoryArchive.TrajectoriesFolderName)).Length,
                    Is.EqualTo(0),
                    "no archive is left under trajectories/");

                Assert.That(TrajectoryArchive.DeleteAll(root), Is.True,
                    "a second delete finds nothing and still succeeds");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>A root that was never written to is also a root with nothing to delete.</summary>
        [Test]
        public void DeleteAllForgivesAMissingRoot()
        {
            string missing = Path.Combine(
                Path.GetTempPath(), "robotsnap_delete_missing_" + System.Guid.NewGuid().ToString("N"));

            Assert.That(Directory.Exists(missing), Is.False);
            Assert.That(TrajectoryArchive.DeleteAll(missing), Is.True);
            Assert.That(TrajectoryArchive.DeleteAll(missing), Is.True, "asking twice is the same answer");
        }

        /// <summary>
        /// An episode keeps its reference after its record is deleted, so reading it must be a no-op the
        /// interface can draw - an empty map - rather than an exception or a stale cached read.
        /// </summary>
        [Test]
        public void AnEpisodeWhoseRecordWasDeletedReadsAsNoTracksInsteadOfThrowing()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                EpisodeMetrics episode = TrajectoryEpisode("s_gone", "ep_gone");
                store.Add(episode);
                MetricsExporter.Export(store, root);

                Assert.That(episode.TrajectoryRef, Is.Not.Null);
                Assert.That(store.TracksOf(episode).ContainsKey("robot_1"), Is.True,
                    "the record is readable while it exists");

                Assert.That(TrajectoryArchive.DeleteAll(root), Is.True);
                store.ForgetArchive();
                store.UseArchive(root);

                Assert.That(store.TracksOf(episode), Is.Empty,
                    "a record that is gone from disk reads as no tracks, never an exception");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        // -- the router surface ---------------------------------------------

        [Test]
        public void TheRouterListsAnEmptySessionWithoutRefusing()
        {
            var router = new SimulationCommandRouter();

            CommandResult result = router.Execute("{\"command\":\"metrics_episodes\"}");

            Assert.That(result.Ok, Is.True);
            Assert.That((int)result.Payload["count"], Is.EqualTo(0));
            Assert.That(result.Payload["episodes"], Is.Empty);
        }

        [Test]
        public void TheRouterAnswersOneEpisodeAndAnUnknownOne()
        {
            MetricsStore.Instance.Add(Synthetic("ep_router"));
            var router = new SimulationCommandRouter();

            CommandResult found = router.Execute("{\"command\":\"metrics_episode\",\"id\":\"ep_router\"}");
            CommandResult missing = router.Execute("{\"command\":\"metrics_episode\",\"id\":\"nope\"}");

            Assert.That(found.Ok, Is.True);
            Assert.That((bool)found.Payload["found"], Is.True);
            Assert.That((string)found.Payload["episode"]["id"], Is.EqualTo("ep_router"));

            Assert.That(missing.Ok, Is.True, "a missing id is an answer, not a refusal");
            Assert.That((bool)missing.Payload["found"], Is.False);
        }

        [Test]
        public void TheRouterClearsTheSession()
        {
            MetricsStore.Instance.Add(Synthetic("ep_clear"));
            var router = new SimulationCommandRouter();

            CommandResult result = router.Execute("{\"command\":\"metrics_clear\"}");

            Assert.That(result.Ok, Is.True);
            Assert.That((int)result.Payload["cleared"], Is.EqualTo(1));
            Assert.That(MetricsStore.Instance.Episodes, Is.Empty);
        }

        // -- the mission of a fleet ------------------------------------------

        /// <summary>One robot arriving is not the fleet arriving: the run is not a goal yet.</summary>
        [Test]
        public void OneRobotArrivingDoesNotFileTheFleetAsAGoal()
        {
            Robot primary = NewRobot("robot_1");
            Robot second = NewRobot("robot_2");
            ArriveAtGoal(primary);
            DrivingTowards(second, new Vector3(5f, 0f, 5f));

            RobotRoster roster = Roster(primary, second);

            Assert.That(Classify(roster, primary), Is.Not.EqualTo(MetricsContract.OutcomeGoal),
                "the second robot is still driving, so the fleet has not finished its mission");
        }

        /// <summary>The fleet succeeds only once every one of its robots has arrived.</summary>
        [Test]
        public void TheFleetIsAGoalOnceEveryRobotOfItArrived()
        {
            Robot primary = NewRobot("robot_1");
            Robot second = NewRobot("robot_2");
            ArriveAtGoal(primary);
            ArriveAtGoal(second);

            RobotRoster roster = Roster(primary, second);

            Assert.That(Classify(roster, primary), Is.EqualTo(MetricsContract.OutcomeGoal),
                "every robot reached its goal, which is what the mission asks of the fleet");
        }

        /// <summary>A robot nobody gave a goal never arrives, so it keeps the fleet short of the goal.</summary>
        [Test]
        public void ARobotNobodyGaveAGoalKeepsTheFleetShortOfTheGoal()
        {
            Robot primary = NewRobot("robot_1");
            Robot ungoaled = NewRobot("robot_2");
            ArriveAtGoal(primary);

            RobotRoster roster = Roster(primary, ungoaled);

            Assert.That(Classify(roster, primary), Is.Not.EqualTo(MetricsContract.OutcomeGoal),
                "a robot nobody gave a goal to cannot have arrived");
        }

        /// <summary>A scene with no roster is the single-robot case, and it is measured as it always was.</summary>
        [Test]
        public void ASingleRobotSceneFilesItsOwnArrivalAsTheGoal()
        {
            Robot alone = NewRobot("robot_1");
            ArriveAtGoal(alone);

            Assert.That(Classify(null, alone), Is.EqualTo(MetricsContract.OutcomeGoal),
                "one robot is the whole fleet, so its own arrival is the mission");
        }

        // -- a reset is an episode boundary ----------------------------------

        /// <summary>
        /// A reset puts the clock back to zero under the episode it was running through. The run interrupted
        /// that way is filed with the time it really reached, and the world that follows is a new episode at
        /// the new origin, so the time of the session and the file of the benchmark name the same run.
        /// </summary>
        [Test]
        public void AClockResetUnderARunningEpisodeClosesItAtTheTimeItReached()
        {
            Clock clock = NewClock();
            MetricsRecorder recorder = NewSessionRecorder(clock, "corridor");

            Drive(recorder, clock, 0.0);
            Drive(recorder, clock, 30.0);

            // The reset: the world goes back to zero while the session keeps running.
            Drive(recorder, clock, 0.0);

            Assert.That(MetricsStore.Instance.Count, Is.EqualTo(1), "the interrupted run was closed, once");
            EpisodeMetrics interrupted = MetricsStore.Instance.Episodes[0];
            Assert.That(interrupted.WorldSeconds, Is.EqualTo(30.0).Within(1e-6),
                "the episode is filed where it really was, not at the rewound reading");
            Assert.That(interrupted.Steps, Is.EqualTo(2), "both steps before the reset were recorded");
            Assert.That(interrupted.Outcome, Is.EqualTo(MetricsContract.OutcomeStopped),
                "a reset interrupts the run the way a stop does");

            // The new episode begins at the new origin: five seconds after the reset it is five seconds old.
            Drive(recorder, clock, 5.0);
            Drive(recorder, clock, 0.0);

            Assert.That(MetricsStore.Instance.Count, Is.EqualTo(2), "the second reset closed the second run");
            Assert.That(MetricsStore.Instance.Episodes[1].WorldSeconds, Is.EqualTo(5.0).Within(1e-6),
                "the run after the reset counts from the new zero, not from the old origin");
        }

        /// <summary>
        /// A reset that lands before the episode recorded a single step leaves nothing behind: an accumulator
        /// with no sample is not a run, and filing it would be a row the session never drove.
        /// </summary>
        [Test]
        public void AResetUnderAnEpisodeThatNeverSampledFilesNothing()
        {
            Clock clock = NewClock();
            MetricsRecorder recorder = NewSessionRecorder(clock, "corridor");

            // An episode open at thirty seconds that has taken no step yet, which is the state a reset lands in.
            SetPrivate(clock, "_elapsedSeconds", 30.0);
            Invoke(recorder, "Begin", _sessionManager, clock, "corridor");

            Drive(recorder, clock, 0.0);

            Assert.That(MetricsStore.Instance.Count, Is.EqualTo(0),
                "an episode that never recorded a step is dropped, not filed");
        }

        // -- reading a session back from an archive --------------------------

        /// <summary>
        /// A catalogue line is the episode as it went on the wire, so reading one back has to give the same
        /// episode: the identity, the reference to its record, and the folder that record lives in.
        /// </summary>
        [Test]
        public void ACatalogueRoundTripsTheEpisodesOfASession()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                string session = store.SessionId;
                EpisodeMetrics first = TrajectoryEpisode(session, "ep_round_one");
                first.StartedAt = "2026-09-30T10:00:00.0000000Z";
                store.Add(first);
                store.Add(TrajectoryEpisode(session, "ep_round_two"));
                MetricsExporter.Export(store, root);

                Dictionary<string, List<EpisodeMetrics>> bySession =
                    ArchiveCatalogue.ReadEpisodesBySession(root);

                Assert.That(bySession.ContainsKey(session), Is.True);
                List<EpisodeMetrics> episodes = bySession[session];
                Assert.That(episodes.Select(episode => episode.Id),
                    Is.EqualTo(new[] { "ep_round_one", "ep_round_two" }),
                    "the lines are grouped in the order they were appended, oldest first");
                Assert.That(episodes[0].TrajectoryRef, Is.Not.Null, "an archived episode carries its reference");
                Assert.That(episodes[0].Trajectories, Is.Empty, "the points are left on disk, not read into RAM");
                Assert.That(episodes[0].ArchiveRoot, Is.EqualTo(root),
                    "a re-read episode names the folder its record lives in");
                Assert.That(episodes[0].StartedAt, Is.EqualTo("2026-09-30T10:00:00.0000000Z"),
                    "the instant is read back as it was written, not reformatted by the current culture");

                IReadOnlyList<ArchiveSessionRecord> sessions = ArchiveCatalogue.ReadSessions(root);
                Assert.That(sessions.Count, Is.EqualTo(1));
                Assert.That(sessions[0].Id, Is.EqualTo(session));
                Assert.That(sessions[0].Episodes, Is.EqualTo(2), "the count comes from the catalogue beside it");
                Assert.That(sessions[0].Root, Is.EqualTo(root));
                Assert.That(sessions[0].StartedAt, Is.Not.Null.And.Not.Empty);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// The last line of an append-only file is exactly where an interrupted export lands, so a line that
        /// stops mid-document costs that line and not the episodes written before it.
        /// </summary>
        [Test]
        public void ATruncatedLastCatalogueLineIsSkippedWithoutLosingTheOthers()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                string session = store.SessionId;
                store.Add(TrajectoryEpisode(session, "ep_whole"));
                MetricsExporter.Export(store, root);

                File.AppendAllText(
                    Path.Combine(root, TrajectoryArchive.CatalogueFileName),
                    "{\"id\":\"ep_half\",\"session\":\"s_");

                Dictionary<string, List<EpisodeMetrics>> bySession =
                    ArchiveCatalogue.ReadEpisodesBySession(root);

                Assert.That(bySession[session].Select(episode => episode.Id),
                    Is.EqualTo(new[] { "ep_whole" }),
                    "the half-written line is dropped and the complete one is kept");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>An absent or unnamed root is a root with nothing saved, never an exception.</summary>
        [Test]
        public void AnAbsentArchiveReadsAsNothing()
        {
            string missing = Path.Combine(
                Path.GetTempPath(), "robotsnap_archive_missing_" + System.Guid.NewGuid().ToString("N"));

            Assert.That(ArchiveCatalogue.ReadEpisodesBySession(missing), Is.Empty);
            Assert.That(ArchiveCatalogue.ReadSessions(missing), Is.Empty);
            Assert.That(ArchiveCatalogue.ReadAll(new[] { missing }), Is.Empty);
            Assert.That(ArchiveCatalogue.ReadEpisodesBySession(null), Is.Empty);
            Assert.That(ArchiveCatalogue.ReadSessions(string.Empty), Is.Empty);
            Assert.That(ArchiveCatalogue.ReadAll(null), Is.Empty);
        }

        /// <summary>
        /// The tab watches two roots at once, so a session exported to both has to be listed once and read from
        /// the folder the user picked first; the list is newest first, and a session whose line carries no
        /// readable instant belongs after the dated ones rather than sorting as the oldest. Every session below
        /// has an episode behind it, because a session line with no episode is not a session the tab lists.
        /// </summary>
        [Test]
        public void MergingTwoRootsKeepsOneCopyOfASessionAndSortsNewestFirst()
        {
            string first = NewExportRoot();
            string second = NewExportRoot();
            try
            {
                WriteSessionLine(first, "s_shared", "2026-10-01T09:00:00Z");
                WriteCatalogueLine(first, "{\"id\":\"ep_shared_first\",\"session\":\"s_shared\"}");
                WriteSessionLine(first, "s_older", "2026-09-01T09:00:00Z");
                WriteCatalogueLine(first, "{\"id\":\"ep_older\",\"session\":\"s_older\"}");
                WriteSessionLine(second, "s_shared", "2026-10-01T09:00:00Z");
                WriteCatalogueLine(second, "{\"id\":\"ep_shared_second\",\"session\":\"s_shared\"}");
                WriteSessionLine(second, "s_newest", "2026-10-05T09:00:00Z");
                WriteCatalogueLine(second, "{\"id\":\"ep_newest\",\"session\":\"s_newest\"}");
                WriteSessionLine(second, "s_undated", string.Empty);
                WriteCatalogueLine(second, "{\"id\":\"ep_undated\",\"session\":\"s_undated\"}");

                IReadOnlyList<ArchiveSessionRecord> merged =
                    ArchiveCatalogue.ReadAll(new[] { first, second, first, null, string.Empty });

                Assert.That(merged.Select(record => record.Id),
                    Is.EqualTo(new[] { "s_newest", "s_shared", "s_older", "s_undated" }),
                    "newest first, the duplicated session once, and the undated one last");
                Assert.That(merged.First(record => record.Id == "s_shared").Root, Is.EqualTo(first),
                    "the first root that names a session wins");
                Assert.That(merged.First(record => record.Id == "s_newest").StartedAt,
                    Is.EqualTo("2026-10-05T09:00:00Z"),
                    "a session's start instant keeps the text of its line");
            }
            finally
            {
                DeleteExportRoot(first);
                DeleteExportRoot(second);
            }
        }

        /// <summary>
        /// An episode read back from a folder nothing installed still finds its own record: its runtime root is
        /// what routes the read, not the archive the running session happens to hold.
        /// </summary>
        [Test]
        public void AnEpisodeReadFromAnArchiveReadsItsTracksFromThatRoot()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                string session = store.SessionId;
                EpisodeMetrics written = TrajectoryEpisode(session, "ep_tracks");
                List<double[]> expected = written.Trajectories["robot_1"];
                store.Add(written);
                MetricsExporter.Export(store, root);

                // Nothing points the store at `root` any more, which is the state of a session read back from
                // an earlier run: only the episode's own root says where its record went.
                store.ForgetArchive();

                EpisodeMetrics reread = ArchiveCatalogue.ReadEpisodesBySession(root)[session][0];
                Assert.That(reread.Trajectories, Is.Empty, "the catalogue line carries no map to fall back on");

                IReadOnlyDictionary<string, List<double[]>> tracks = store.TracksOf(reread);

                Assert.That(tracks.ContainsKey("robot_1"), Is.True,
                    "the tracks are read from the root the episode was read back from");
                AssertSamePoints(expected, tracks["robot_1"]);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        // -- editing what the archive holds ----------------------------------

        /// <summary>
        /// A session line with no episode behind it is not a session: the rail must not offer a row a reader
        /// can open and find nothing in.
        /// </summary>
        [Test]
        public void ReadAllHidesASessionTheCatalogueHasNoEpisodeFor()
        {
            string root = NewExportRoot();
            try
            {
                WriteSessionLine(root, "s_empty", "2026-10-01T09:00:00Z");
                WriteSessionLine(root, "s_full", "2026-10-01T10:00:00Z");
                WriteCatalogueLine(root, "{\"id\":\"ep_full\",\"session\":\"s_full\"}");

                IReadOnlyList<ArchiveSessionRecord> sessions = ArchiveCatalogue.ReadAll(new[] { root });

                Assert.That(sessions.Count, Is.EqualTo(1), "only the session with an episode is a session");
                Assert.That(sessions[0].Id, Is.EqualTo("s_full"));
                Assert.That(sessions[0].Episodes, Is.EqualTo(1));

                Assert.That(ArchiveCatalogue.ReadSessions(root).Count, Is.EqualTo(2),
                    "the raw reader still sees both lines - it is the merged view that hides the empty one");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// Purging rewrites the session file only when it has a line to remove, drops only the lines with no
        /// episode behind them, and leaves the lines it keeps byte for byte.
        /// </summary>
        [Test]
        public void PurgeEmptySessionsDropsOnlyTheLinesWithNoEpisode()
        {
            string root = NewExportRoot();
            try
            {
                string kept = "{\"id\":\"s_full\",\"started_at\":\"2026-10-01T10:00:00Z\",\"schema\":1}\n";
                WriteSessionLine(root, "s_empty", "2026-10-01T09:00:00Z");
                File.AppendAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName), kept);
                WriteCatalogueLine(root, "{\"id\":\"ep_full\",\"session\":\"s_full\"}");

                Assert.That(TrajectoryArchive.PurgeEmptySessions(root), Is.EqualTo(1));

                string after = File.ReadAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName));
                Assert.That(after, Is.EqualTo(kept), "the line that stays is the bytes it was");

                Assert.That(TrajectoryArchive.PurgeEmptySessions(root), Is.EqualTo(0),
                    "a second purge has nothing to remove");
                Assert.That(
                    File.ReadAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName)), Is.EqualTo(kept),
                    "a purge with nothing to do writes nothing");

                Assert.That(TrajectoryArchive.PurgeEmptySessions(root + "_missing"), Is.EqualTo(0),
                    "a root that was never written to reads as nothing to do");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// Deleting one episode touches one line: the lines around it come back exactly as they were, including
        /// one an older version wrote in another shape, and the session's archive file is not rewritten.
        /// </summary>
        [Test]
        public void DeleteEpisodeRemovesItsLineAndLeavesTheNeighboursByteForByte()
        {
            string root = NewExportRoot();
            try
            {
                string legacy = "{\"id\": \"ep_legacy\",\"scenario\":\"old_export\",\"session\":\"s_edit\"}";
                WriteCatalogueLine(root, legacy);
                WriteCatalogueLine(root, "{\"id\":\"ep_two\",\"session\":\"s_edit\"}");
                WriteCatalogueLine(root, "{\"id\":\"ep_three\",\"session\":\"s_edit\"}");

                string archive = Path.Combine(root, TrajectoryArchive.TrajectoriesFolderName, "s_edit.rbt");
                Directory.CreateDirectory(Path.GetDirectoryName(archive));
                byte[] record = { 1, 2, 3, 4, 5 };
                File.WriteAllBytes(archive, record);

                Assert.That(TrajectoryArchive.DeleteEpisode(root, "ep_two"), Is.True);

                Assert.That(
                    File.ReadAllText(Path.Combine(root, TrajectoryArchive.CatalogueFileName)),
                    Is.EqualTo(legacy + "\n" + "{\"id\":\"ep_three\",\"session\":\"s_edit\"}\n"),
                    "only the deleted line left, and the rest is what was written");
                Assert.That(File.ReadAllBytes(archive), Is.EqualTo(record),
                    "the trajectory archive is append-only and is never rewritten in place");

                Assert.That(TrajectoryArchive.DeleteEpisode(root, "ep_two"), Is.True,
                    "an id that is already gone is the state the call promises");
                Assert.That(TrajectoryArchive.DeleteEpisode(root, "ep_nobody"), Is.True);
                Assert.That(TrajectoryArchive.DeleteEpisode(root + "_missing", "ep_nobody"), Is.True,
                    "a root that was never written to holds no episode");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// Deleting a session takes its lines, its session line and its archive, and leaves the session beside
        /// it - lines and archive both - exactly as it was.
        /// </summary>
        [Test]
        public void DeleteSessionRemovesItsLinesItsArchiveAndItsSessionLine()
        {
            string root = NewExportRoot();
            try
            {
                string keepSession = "{\"id\":\"s_keep\",\"started_at\":\"2026-10-01T10:00:00Z\",\"schema\":1}\n";
                string keepEpisode = "{\"id\":\"ep_keep\",\"session\":\"s_keep\"}\n";
                WriteSessionLine(root, "s_gone", "2026-10-01T09:00:00Z");
                File.AppendAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName), keepSession);
                WriteCatalogueLine(root, "{\"id\":\"ep_gone\",\"session\":\"s_gone\"}");
                File.AppendAllText(Path.Combine(root, TrajectoryArchive.CatalogueFileName), keepEpisode);

                string folder = Path.Combine(root, TrajectoryArchive.TrajectoriesFolderName);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "s_gone.rbt"), new byte[] { 9, 9, 9 });
                File.WriteAllBytes(Path.Combine(folder, "s_keep.rbt"), new byte[] { 7, 7 });

                Assert.That(TrajectoryArchive.DeleteSession(root, "s_gone"), Is.True);

                Assert.That(
                    File.ReadAllText(Path.Combine(root, TrajectoryArchive.CatalogueFileName)),
                    Is.EqualTo(keepEpisode), "the other session's episode line is untouched");
                Assert.That(
                    File.ReadAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName)),
                    Is.EqualTo(keepSession), "the other session's line is untouched");
                Assert.That(File.Exists(Path.Combine(folder, "s_gone.rbt")), Is.False,
                    "the session's trajectory archive is gone");
                Assert.That(File.Exists(Path.Combine(folder, "s_keep.rbt")), Is.True,
                    "the archive beside it stays");

                Assert.That(TrajectoryArchive.DeleteSession(root, "s_gone"), Is.True,
                    "deleting a session twice is the same answer");
                Assert.That(TrajectoryArchive.DeleteSession(root + "_missing", "s_gone"), Is.True);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// Renaming an episode rewrites its line's <c>name</c> key and nothing else - the id, the scenario and
        /// the lines around it come back untouched - and an emptied name removes the key rather than writing
        /// one nothing gave.
        /// </summary>
        [Test]
        public void RenameEpisodeChangesOnlyTheNameKeyOfItsLine()
        {
            string root = NewExportRoot();
            try
            {
                string other = "{\"id\":\"ep_other\",\"scenario\":\"corridor\"}\n";
                WriteCatalogueLine(root, "{\"id\":\"ep_rename\",\"index\":3,\"scenario\":\"corridor\"}");
                File.AppendAllText(Path.Combine(root, TrajectoryArchive.CatalogueFileName), other);

                Assert.That(TrajectoryArchive.RenameEpisode(root, "ep_rename", "Rue du Port"), Is.True);

                string[] lines = File.ReadAllLines(Path.Combine(root, TrajectoryArchive.CatalogueFileName));
                JObject renamed = JObject.Parse(lines[0]);
                Assert.That((string)renamed["name"], Is.EqualTo("Rue du Port"));
                Assert.That((string)renamed["id"], Is.EqualTo("ep_rename"));
                Assert.That((int)renamed["index"], Is.EqualTo(3));
                Assert.That((string)renamed["scenario"], Is.EqualTo("corridor"));
                Assert.That(lines[1] + "\n", Is.EqualTo(other), "the line beside it is the bytes it was");

                Assert.That(TrajectoryArchive.RenameEpisode(root, "ep_rename", ""), Is.True);
                JObject cleared = JObject.Parse(
                    File.ReadAllLines(Path.Combine(root, TrajectoryArchive.CatalogueFileName))[0]);
                Assert.That(cleared.ContainsKey("name"), Is.False,
                    "an empty name removes the key instead of writing an empty string");

                Assert.That(TrajectoryArchive.RenameEpisode(root, "ep_nobody", "X"), Is.False,
                    "there is nothing to rename an id the catalogue does not hold");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// A session is renamed the same way its episodes are, and the name comes back out of the file the
        /// catalogue reader parses.
        /// </summary>
        [Test]
        public void RenameSessionChangesTheSessionName()
        {
            string root = NewExportRoot();
            try
            {
                string other = "{\"id\":\"s_other\",\"started_at\":\"2026-10-01T10:00:00Z\",\"schema\":1}\n";
                WriteSessionLine(root, "s_named", "2026-10-01T09:00:00Z");
                File.AppendAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName), other);

                Assert.That(TrajectoryArchive.RenameSession(root, "s_named", "Campus run"), Is.True);

                string[] lines = File.ReadAllLines(Path.Combine(root, TrajectoryArchive.SessionsFileName));
                Assert.That((string)JObject.Parse(lines[0])["name"], Is.EqualTo("Campus run"));
                Assert.That(lines[1] + "\n", Is.EqualTo(other), "the line beside it is the bytes it was");
                Assert.That(ArchiveCatalogue.ReadSessions(root)[0].Name, Is.EqualTo("Campus run"),
                    "the name comes back out of the file a reader re-reads");

                Assert.That(TrajectoryArchive.RenameSession(root, "s_named", ""), Is.True);
                Assert.That(
                    JObject.Parse(File.ReadAllLines(Path.Combine(root, TrajectoryArchive.SessionsFileName))[0])
                        .ContainsKey("name"),
                    Is.False);

                Assert.That(TrajectoryArchive.RenameSession(root, "s_nobody", "X"), Is.False);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// The name reaches the file from two directions: the archive only writes one when there is one, and the
        /// exporter hands it the name the store holds. A session nobody named carries no key at all.
        /// </summary>
        [Test]
        public void ASessionLineCarriesTheNameOnlyWhenThereIsOne()
        {
            string root = NewExportRoot();
            try
            {
                var archive = new TrajectoryArchive(root);
                archive.EnsureSessionLine("s_named", "2026-10-01T09:00:00Z", "Campus run");
                archive.EnsureSessionLine("s_bare", "2026-10-01T10:00:00Z");

                string[] lines = File.ReadAllLines(Path.Combine(root, TrajectoryArchive.SessionsFileName));
                Assert.That((string)JObject.Parse(lines[0])["name"], Is.EqualTo("Campus run"));
                Assert.That(JObject.Parse(lines[1]).ContainsKey("name"), Is.False,
                    "a session nobody named carries no name key");
            }
            finally
            {
                DeleteExportRoot(root);
            }

            string export = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.SessionName = "Benchmark 01";
                store.Add(Synthetic("ep_named"));

                MetricsExporter.Export(store, export);

                string line = File.ReadAllText(Path.Combine(export, TrajectoryArchive.SessionsFileName));
                Assert.That((string)JObject.Parse(line)["name"], Is.EqualTo("Benchmark 01"),
                    "the exporter hands the session's name to the line it writes");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(export);
            }
        }

        /// <summary>
        /// The disk carries the name the user wrote and nothing else: an episode nobody named has no name key,
        /// and clearing a name puts it back in that state rather than writing the label the interface computes.
        /// </summary>
        [Test]
        public void AnEpisodeNobodyNamedCarriesNoNameOnDisk()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(TrajectoryEpisode("s_bare", "ep_bare"));
                MetricsExporter.Export(store, root);

                string path = Path.Combine(root, TrajectoryArchive.CatalogueFileName);
                Assert.That(JObject.Parse(File.ReadAllLines(path)[0]).ContainsKey("name"), Is.False,
                    "an un-named episode carries no name key, and never the computed 01_default label");

                Assert.That(TrajectoryArchive.RenameEpisode(root, "ep_bare", "Dock"), Is.True);
                Assert.That((string)JObject.Parse(File.ReadAllLines(path)[0])["name"], Is.EqualTo("Dock"));

                Assert.That(TrajectoryArchive.RenameEpisode(root, "ep_bare", null), Is.True);
                Assert.That(JObject.Parse(File.ReadAllLines(path)[0]).ContainsKey("name"), Is.False,
                    "clearing a name puts the line back where a never-named one is");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// The tab purges a bare session line as it reads the folder, so the file stops naming a session with
        /// nothing in it - and the store forgets the archive it had announced that session to, or the next
        /// export would never write the line again.
        /// </summary>
        [Test]
        public void TheSessionListPurgesABareLineAndForgetsTheArchive()
        {
            string root = NewExportRoot();
            try
            {
                string kept = "{\"id\":\"s_full\",\"started_at\":\"2026-10-01T10:00:00Z\",\"schema\":1}\n";
                WriteSessionLine(root, "s_bare", "2026-10-01T09:00:00Z");
                File.AppendAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName), kept);
                WriteCatalogueLine(root, "{\"id\":\"ep_full\",\"session\":\"s_full\"}");

                MetricsStore.Instance.UseArchive(root);
                Assert.That(MetricsStore.Instance.Archive, Is.Not.Null, "the store is pointed at the folder");

                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });

                Assert.That(session.SavedSessions.Count, Is.EqualTo(1));
                Assert.That(session.SavedSessions[0].Id, Is.EqualTo("s_full"));
                Assert.That(
                    File.ReadAllText(Path.Combine(root, TrajectoryArchive.SessionsFileName)), Is.EqualTo(kept),
                    "the bare line is gone and the line with an episode behind it is untouched");
                Assert.That(MetricsStore.Instance.Archive, Is.Null,
                    "an archive that lost a session line has to announce it afresh next time");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// An episode is exported with its points whether it still holds them or has to read them back: the
        /// single-file export of an archived episode carries its trajectory in clear, and the episode in the
        /// tab is not filled in by the write.
        /// </summary>
        [Test]
        public void AnEpisodeReadBackFromAnArchiveExportsWithItsTrajectoryInClear()
        {
            string archiveRoot = NewExportRoot();
            string exportRoot = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                string session = store.SessionId;
                EpisodeMetrics written = TrajectoryEpisode(session, "ep_clear");
                List<double[]> expected = written.Trajectories["robot_1"];
                store.Add(written);
                MetricsExporter.Export(store, archiveRoot);

                store.ForgetArchive();

                EpisodeMetrics reread = ArchiveCatalogue.ReadEpisodesBySession(archiveRoot)[session][0];
                Assert.That(reread.Trajectories, Is.Empty, "the catalogue line carries no map to fall back on");

                MetricsExportReport report = MetricsExporter.ExportEpisode(store, reread, exportRoot);

                Assert.That(report.Directory, Is.EqualTo(exportRoot));
                Assert.That(report.SessionFile, Is.EqualTo(Path.Combine(exportRoot, "episode_ep_clear.json")));

                JObject document = ParseDocument(File.ReadAllText(report.SessionFile));
                Assert.That(document.ContainsKey("trajectory_ref"), Is.False,
                    "a single-file export carries the points, not a pointer to a file beside it");
                Assert.That((string)document["id"], Is.EqualTo("ep_clear"));

                var points = (JArray)document["trajectories"]["robot_1"];
                Assert.That(points.Count, Is.EqualTo(expected.Count));
                Assert.That((double)points[1][1], Is.EqualTo(expected[1][1]).Within(1e-6));

                Assert.That(reread.Trajectories, Is.Empty,
                    "the export does not fill the episode the tab holds");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(archiveRoot);
                DeleteExportRoot(exportRoot);
            }
        }

        /// <summary>
        /// The session document is the one a reader parses without Unity: the keys the format promises, one
        /// entry per episode, and each entry carrying its trajectory in clear even when it came back from an
        /// archive.
        /// </summary>
        [Test]
        public void ASessionExportIsReadableAndCarriesEveryTrajectoryInClear()
        {
            string archiveRoot = NewExportRoot();
            string exportRoot = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                string session = store.SessionId;
                const string startedAt = "2026-10-01T09:00:00Z";
                store.Add(TrajectoryEpisode(session, "ep_one"));
                store.Add(TrajectoryEpisode(session, "ep_two"));
                MetricsExporter.Export(store, archiveRoot);

                store.ForgetArchive();

                IReadOnlyList<EpisodeMetrics> episodes = ArchiveCatalogue.ReadEpisodesBySession(archiveRoot)[session];
                Assert.That(episodes.Count, Is.EqualTo(2));

                MetricsExportReport report =
                    MetricsExporter.ExportSession(episodes, session, startedAt, exportRoot);

                Assert.That(report.SessionFile, Is.EqualTo(Path.Combine(exportRoot, "session_" + session + ".json")));

                string text = File.ReadAllText(report.SessionFile);
                JObject document = ParseDocument(text);
                Assert.That((string)document["session"], Is.EqualTo(session));
                Assert.That((string)document["started_at"], Is.EqualTo(startedAt));
                Assert.That(text, Does.Contain("\"started_at\": \"2026-10-01T09:00:00Z\""),
                    "the instant keeps the text of the line it came from");
                Assert.That(document.ContainsKey("exported_at"), Is.True);
                Assert.That((int)document["episode_count"], Is.EqualTo(2));

                var written = (JArray)document["episodes"];
                Assert.That(written.Count, Is.EqualTo(2));
                foreach (JToken episode in written)
                {
                    Assert.That(episode["trajectories"]?["robot_1"], Is.Not.Null,
                        "every episode of the file carries its trajectory in clear");
                    Assert.That(((JArray)episode["trajectories"]["robot_1"]).Count, Is.EqualTo(3));
                    Assert.That(episode["trajectory_ref"], Is.Null);
                }
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(archiveRoot);
                DeleteExportRoot(exportRoot);
            }
        }

        // -- helpers --------------------------------------------------------

        private static string NewExportRoot()
            => Path.Combine(Path.GetTempPath(), "robotsnap_metrics_" + System.Guid.NewGuid().ToString("N"));

        /// <summary>
        /// One line of a root's <c>sessions.jsonl</c>, written by hand: the merge cases care about which root
        /// names which session, and going through an export would drag two sessions into the same folder.
        /// </summary>
        private static void WriteSessionLine(string root, string id, string startedAt)
        {
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, TrajectoryArchive.SessionsFileName),
                "{\"id\":\"" + id + "\",\"started_at\":\"" + startedAt + "\",\"schema\":1}\n");
        }

        /// <summary>
        /// One hand-written catalogue line, so a case can lay out the exact bytes - including a shape an older
        /// version wrote - and check that an edit leaves the lines around it untouched.
        /// </summary>
        private static void WriteCatalogueLine(string root, string line)
        {
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, TrajectoryArchive.CatalogueFileName), line + "\n");
        }

        /// <summary>
        /// Parses an exported document the way the archive reader does: a string that looks like an instant
        /// stays the text it is, instead of the parser turning it into a date and printing it back in the
        /// machine's culture - which would make a test read a value the file does not hold.
        /// </summary>
        private static JObject ParseDocument(string text)
            => JsonConvert.DeserializeObject<JObject>(
                text, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });

        private static void DeleteExportRoot(string root)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        /// <summary>An episode carrying two robot tracks, which is what the archive cases are written on.</summary>
        private static EpisodeMetrics TrajectoryEpisode(string session, string id)
        {
            return new EpisodeMetrics
            {
                Id = id,
                Session = session,
                Robot = "robot_1",
                TrajectoryStride = 1,
                Trajectories = new Dictionary<string, List<double[]>>
                {
                    ["robot_1"] = new List<double[]>
                    {
                        new[] { 0.0, 0.0, 0.0 },
                        new[] { 1.0, 1.0, 0.0 },
                        new[] { 2.0, 1.0, 2.0 },
                    },
                },
            };
        }

        /// <summary>Every sample of <paramref name="actual"/> is the same instant and pose as the expected one.</summary>
        private static void AssertSamePoints(List<double[]> expected, List<double[]> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            for (int index = 0; index < expected.Count; index++)
            {
                Assert.That(actual[index][0], Is.EqualTo(expected[index][0]).Within(1e-6), $"t of point {index}");
                Assert.That(actual[index][1], Is.EqualTo(expected[index][1]).Within(1e-6), $"x of point {index}");
                Assert.That(actual[index][2], Is.EqualTo(expected[index][2]).Within(1e-6), $"z of point {index}");
            }
        }

        private static EpisodeAccumulator NewAccumulator(double personalSpaceRadius = 0.5, int capacity = 64)
        {
            return new EpisodeAccumulator(
                "ep_test",
                1,
                "s_test",
                "corridor",
                "robot_1",
                personalSpaceRadius,
                capacity,
                "2026-09-30T10:00:00.0000000Z",
                0.0);
        }

        /// <summary>
        /// The verdict the recorder would file for <paramref name="robot"/> over that roster: the roster is
        /// planted as the current one, the recorder gathers its robots through the very step FixedUpdate
        /// runs, and the classifier the loop calls is read back. A null roster is the single-robot shape.
        /// </summary>
        private string Classify(RobotRoster roster, Robot robot)
        {
            SetRosterCurrent(roster);

            var host = new GameObject("test-metrics-recorder");
            _created.Add(host);
            MetricsRecorder recorder = host.AddComponent<MetricsRecorder>();

            Invoke(recorder, "CollectRobots");
            return (string)Invoke(recorder, "Classify", robot, 0.0);
        }

        private Robot NewRobot(string name)
        {
            var host = new GameObject(name);
            _created.Add(host);
            return host.AddComponent<Robot>();
        }

        /// <summary>Latches a robot as arrived through the same watch a client-steered robot goes through.</summary>
        private static void ArriveAtGoal(Robot robot)
        {
            robot.SetGoal(robot.Position);
            Invoke(robot, "WatchManualArrival");
        }

        private static void DrivingTowards(Robot robot, Vector3 goal) => robot.SetGoal(goal);

        /// <summary>A roster holding the robots given, in the order given, planted as the current one.</summary>
        private RobotRoster Roster(params Robot[] robots)
        {
            var host = new GameObject("test-robot-roster");
            _created.Add(host);
            var roster = host.AddComponent<RobotRoster>();
            var slots = (IList)RosterSlots.GetValue(roster);

            foreach (Robot robot in robots)
                slots.Add(SlotFor(robot));

            SetRosterCurrent(roster);
            return roster;
        }

        private static object SlotFor(Robot robot)
        {
            object slot = System.Activator.CreateInstance(RosterSlotType, nonPublic: true);
            RosterSlotType.GetField("Id").SetValue(slot, robot.gameObject.name);
            RosterSlotType.GetField("TypeId").SetValue(slot, RobotProfiles.DefaultId);
            RosterSlotType.GetField("Robot").SetValue(slot, robot);
            return slot;
        }

        private static void SetRosterCurrent(RobotRoster roster)
        {
            typeof(RobotRoster)
                .GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                .GetSetMethod(true)
                .Invoke(null, new object[] { roster });
        }

        private static object Invoke(object target, string method, params object[] arguments)
        {
            return target.GetType()
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, arguments);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            target.GetType()
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
        }

        /// <summary>A clock of the session, whose elapsed time the cases below move by hand.</summary>
        private Clock NewClock()
        {
            var host = new GameObject("test-clock");
            _created.Add(host);
            return host.AddComponent<Clock>();
        }

        /// <summary>
        /// A recorder watching a session that reads Running: a scenario manager holding a scenario, a
        /// supervisor that is not paused - which is what the session state is derived from - one robot to be
        /// the subject of the episode, and the clock the case moves. The recorder is driven through the very
        /// FixedUpdate the loop runs, because an edit-mode test runs no frame of its own. The session file and
        /// the two detectors are switched off: what is measured here is the clock boundary, not the map.
        /// </summary>
        private MetricsRecorder NewSessionRecorder(Clock clock, string scenario)
        {
            _sessionSupervisor = NewSupervisor();
            _sessionManager = NewRunningManager(scenario);
            Roster(NewRobot("robot_1"));

            var host = new GameObject("test-metrics-recorder");
            _created.Add(host);
            MetricsRecorder recorder = host.AddComponent<MetricsRecorder>();

            SetPrivate(recorder, "_initialized", true);
            SetPrivate(recorder, "_manager", _sessionManager);
            SetPrivate(recorder, "_clock", clock);
            SetPrivate(recorder, "exportOnFinish", false);
            SetPrivate(recorder, "detectCollisions", false);
            SetPrivate(recorder, "detectOutOfBounds", false);
            return recorder;
        }

        /// <summary>The supervisor the session state is derived from; without a clock it is never paused.</summary>
        private Supervisor NewSupervisor()
        {
            var host = new GameObject("test-supervisor");
            _created.Add(host);
            Supervisor supervisor = host.AddComponent<Supervisor>();
            SetPrivate(supervisor, "_clock", null);
            return supervisor;
        }

        /// <summary>A manager whose session reads Running, which is the state a reset happens in.</summary>
        private ScenarioManager NewRunningManager(string scenario)
        {
            var host = new GameObject("test-scenario-manager");
            _created.Add(host);
            ScenarioManager manager = host.AddComponent<ScenarioManager>();
            SetPrivate(manager, "_currentScenarioData", new ScenarioData { Info = new ScenarioInfo { Name = scenario } });
            SetPrivate(manager, "_currentScenarioId", scenario);
            SetPrivate(manager, "_scenarioApplied", true);
            return manager;
        }

        /// <summary>One FixedUpdate of the recorder with the clock standing at that world time.</summary>
        private static void Drive(MetricsRecorder recorder, Clock clock, double seconds)
        {
            SetPrivate(clock, "_elapsedSeconds", seconds);
            Invoke(recorder, "FixedUpdate");
        }
    }
}
