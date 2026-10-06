using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RobotSNAP.Metrics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// The analysis tab reads the metrics store and the archives beside it. These tests pin the things a
    /// reader would notice if they drifted: the list follows the store as episodes end, the rail lists only the
    /// sessions that hold something, an episode says which place in the session it took or the name it was
    /// given, the overview counts only the episodes it was handed, a filter narrows the list, a row's glyphs do
    /// only what they say, deleting and renaming reach the folder a record was read from, the headings name
    /// what their panel is about, an episode that held no human says so instead of reporting a zero distance,
    /// every agent of an episode gets its own colour, and a confirmation dialog never acts before the reader
    /// has confirmed it.
    /// </summary>
    public sealed class AnalysisTabTests
    {
        /// <summary>
        /// The store is a session-wide singleton that survives a play session when the project runs with no
        /// domain reload, so a test starts from an empty one instead of inheriting whatever the last run left.
        /// </summary>
        [SetUp]
        public void ResetStoreBefore() => MetricsStore.Instance.Clear();

        [TearDown]
        public void ResetStore() => MetricsStore.Instance.Clear();

        private static EpisodeMetrics Episode(
            string id,
            string outcome = MetricsContract.OutcomeGoal,
            double minHuman = 0.8,
            double meanHuman = 2.4,
            int index = 0,
            string scenario = "corridor",
            int intrusions = 2)
        {
            return new EpisodeMetrics
            {
                Id = id,
                Index = index,
                Session = "s_test",
                Scenario = scenario,
                Robot = "robot_1",
                StartedAt = "2026-09-30T10:00:00.0000000Z",
                Outcome = outcome,
                WorldSeconds = 12.0,
                WallSeconds = 1.2,
                Steps = 600,
                PathLengthMetres = 15.0,
                StraightLineMetres = 10.0,
                AverageSpeedMetresPerSecond = 1.25,
                MaxSpeedMetresPerSecond = 1.6,
                MinHumanDistanceMetres = minHuman,
                AverageHumanDistanceMetres = meanHuman,
                PersonalSpaceIntrusions = intrusions,
                PersonalSpaceSeconds = 1.5,
                PersonalSpaceRadiusMetres = 0.5,
                Trajectories = new Dictionary<string, List<double[]>>
                {
                    ["robot_1"] = new List<double[]> { new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 1.0, 0.5 } },
                    ["human_1"] = new List<double[]> { new[] { 0.0, 2.0, 2.0 } },
                    ["human_2"] = new List<double[]> { new[] { 0.0, -2.0, 2.0 } },
                },
            };
        }

        private static List<VisualElement> Rows(VisualElement list)
            => list.Query<VisualElement>(className: "analysis-episode-row").ToList();

        private static VisualElement RowOf(VisualElement list, string id)
            => Rows(list).FirstOrDefault(row => (string)row.userData == id);

        private static List<string> RowText(VisualElement row)
            => row.Query<Label>().ToList().Select(label => label.text).ToList();

        /// <summary>The value half of the metric row a reader finds by its label.</summary>
        private static string MetricValue(VisualElement root, string label)
        {
            foreach (VisualElement row in root.Query<VisualElement>(className: "analysis-metric-row").ToList())
            {
                List<Label> labels = row.Query<Label>().ToList();
                if (labels.Count >= 2 && labels[0].text == label)
                    return labels[1].text;
            }
            return null;
        }

        /// <summary>
        /// The final half of a metric row - the value the episode ended on, which only a scrubbed row carries.
        /// Null for a row that holds one value, which is every row of an episode read whole.
        /// </summary>
        private static string MetricFinal(VisualElement root, string label)
        {
            foreach (VisualElement row in root.Query<VisualElement>(className: "analysis-metric-row").ToList())
            {
                List<Label> labels = row.Query<Label>().ToList();
                if (labels.Count >= 3 && labels[0].text == label)
                    return labels[2].text;
            }
            return null;
        }

        /// <summary>
        /// One episode a replay can be read through: a robot that drives a straight metre per second for four
        /// seconds, and a human standing 0.4 m off the end of that line. The numbers are the ones the
        /// accumulator would have written, so a prefix can be checked against the value it is heading for -
        /// the closest human is 2.04 m away halfway through and 0.40 m at the end, and the robot only enters
        /// that human's personal space on the last sample.
        /// </summary>
        private static EpisodeMetrics ReplayEpisode()
        {
            return new EpisodeMetrics
            {
                Id = "s_test-0001",
                Index = 1,
                Session = "s_test",
                Scenario = "corridor",
                Robot = "robot_1",
                StartedAt = "2026-09-30T10:00:00.0000000Z",
                Outcome = MetricsContract.OutcomeGoal,
                WorldSeconds = 4.0,
                WallSeconds = 0.4,
                Steps = 5,
                PathLengthMetres = 4.0,
                StraightLineMetres = 4.0,
                AverageSpeedMetresPerSecond = 1.0,
                MaxSpeedMetresPerSecond = 1.0,
                MinHumanDistanceMetres = 0.4,
                AverageHumanDistanceMetres = 2.11263,
                PersonalSpaceIntrusions = 1,
                PersonalSpaceSeconds = 1.0,
                PersonalSpaceRadiusMetres = 0.5,
                Trajectories = new Dictionary<string, List<double[]>>
                {
                    ["robot_1"] = new List<double[]>
                    {
                        new[] { 0.0, 0.0, 0.0 },
                        new[] { 1.0, 1.0, 0.0 },
                        new[] { 2.0, 2.0, 0.0 },
                        new[] { 3.0, 3.0, 0.0 },
                        new[] { 4.0, 4.0, 0.0 },
                    },
                    ["human_1"] = new List<double[]>
                    {
                        new[] { 0.0, 4.0, 0.4 },
                        new[] { 4.0, 4.0, 0.4 },
                    },
                },
            };
        }

        private static List<string> Colours(IReadOnlyList<AnalysisAgentPalette.TrackColour> tracks)
            => tracks.Select(track =>
            {
                var packed = (Color32)track.Colour;
                return $"{packed.r},{packed.g},{packed.b}";
            }).ToList();

        /// <summary>The headline tile of an overview, found by the caption under it.</summary>
        private static VisualElement Tile(VisualElement summaryHost, string caption)
            => summaryHost.Query<VisualElement>(className: "analysis-tile").ToList().FirstOrDefault(tile =>
                tile.Query<Label>(className: "analysis-tile-caption").ToList().Any(label => label.text == caption));

        private static string TileValue(VisualElement summaryHost, string caption)
            => Tile(summaryHost, caption)?.Query<Label>(className: "analysis-tile-value").ToList()[0].text;

        private static List<string> Captions(VisualElement summaryHost, string className)
            => summaryHost.Query<Label>(className: className).ToList().Select(label => label.text).ToList();

        private static AnalysisEpisodeList BoundList(out AnalysisSession session)
        {
            var list = new AnalysisEpisodeList();
            session = NewSession();
            list.Bind(session);
            return list;
        }

        private static List<VisualElement> RailRows(VisualElement rail)
            => rail.Query<VisualElement>(className: "analysis-rail-row").ToList();

        /// <summary>The session id each rail line names, in the order the lines are drawn; null is the running one.</summary>
        private static List<string> RailSessionIds(VisualElement rail)
            => RailRows(rail).Select(row => (string)row.userData).ToList();

        private static AnalysisSessionRail BoundRail(out AnalysisSession session)
        {
            var rail = new AnalysisSessionRail();
            session = NewSession();
            rail.Bind(session);
            return rail;
        }

        /// <summary>
        /// A session over the store that reads no archive. The cases here exercise the live store and any saved
        /// session they write themselves into a temporary folder, never the exports of the machine the suite
        /// happens to run on - the roots are injected empty rather than left to the tab's own defaults.
        /// </summary>
        private static AnalysisSession NewSession(MetricsStore store = null)
            => new AnalysisSession(store, Array.Empty<string>());

        // -- the list follows the store -------------------------------------

        [Test]
        public void TheEpisodeListFillsFromTheStoreWhenAnEpisodeEnds()
        {
            var list = new AnalysisEpisodeList();
            var session = NewSession();
            list.Bind(session);

            Assert.That(Rows(list), Is.Empty, "an empty session is an empty list, never an error");
            Assert.That(list.Query<Label>(className: "analysis-empty-message").ToList(), Is.Not.Empty,
                "an empty session says so instead of leaving a blank panel");

            MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
            Assert.That(session.Poll(), Is.True, "a new episode is a change the session has to notice");

            List<VisualElement> rows = Rows(list);
            Assert.That(rows.Count, Is.EqualTo(1));

            List<string> text = RowText(rows[0]);
            Assert.That(text, Does.Contain("01_corridor"), "the row names the episode by its session ordinal");
            Assert.That(text, Does.Contain("robot_1"), "the row names the robot");
            Assert.That(text, Does.Contain("Reached goal"), "the row names the outcome");
            Assert.That(text, Does.Contain("12.00 s world"), "the row names the simulated duration");
            Assert.That(text.Any(value => value.Contains("2026-09-30")), Is.True, "the row names the instant it started");

            MetricsStore.Instance.Add(Episode("s_test-0002", index: 2));
            Assert.That(session.Poll(), Is.True);
            Assert.That(Rows(list).Count, Is.EqualTo(2), "the newest episode is added, the list grows");
        }

        // -- sessions read back from disk ------------------------------------

        [Test]
        public void TheSessionSwitchesBetweenTheRunningOneAndASavedOne()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(Episode("s_test-0001", index: 1));
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001", "s_saved_2026-0002");

                var session = new AnalysisSession(store, new[] { root });

                Assert.That(session.IsRunningSession, Is.True, "the tab opens on the session running now");
                Assert.That(session.SourceId, Is.EqualTo(store.SessionId));
                Assert.That(session.Episodes.Select(episode => episode.Id),
                    Is.EqualTo(new[] { "s_test-0001" }));
                Assert.That(session.SavedSessions.Select(record => record.Id),
                    Is.EqualTo(new[] { "s_saved_2026" }));

                session.ShowSaved("s_saved_2026");
                Assert.That(session.IsRunningSession, Is.False);
                Assert.That(session.SourceId, Is.EqualTo("s_saved_2026"));
                Assert.That(session.Episodes.Select(episode => episode.Id),
                    Is.EqualTo(new[] { "s_saved_2026-0001", "s_saved_2026-0002" }),
                    "the saved session's episodes read oldest first, as the archive holds them");
                Assert.That(session.Episodes[0].StartedAt, Is.EqualTo("2026-09-30T10:00:00.0000000Z"),
                    "the instant a catalogue line carries comes back exactly as it was written");
                Assert.That(session.Selected.Id, Is.EqualTo("s_saved_2026-0002"),
                    "the newest episode of the saved session is selected first");
                Assert.That(session.Selected.ArchiveRoot, Is.EqualTo(root),
                    "its episodes name the folder they were read from");

                session.Select("s_saved_2026-0001");
                Assert.That(session.Selected.Id, Is.EqualTo("s_saved_2026-0001"));
                session.Select("s_test-0001");
                Assert.That(session.Selected.Id, Is.EqualTo("s_saved_2026-0001"),
                    "an id that belongs to the other source is not in the displayed session");

                session.ShowSaved("s_never_saved");
                Assert.That(session.IsRunningSession, Is.False,
                    "an id the picker never offered leaves the displayed session alone");

                session.ShowRunning();
                Assert.That(session.IsRunningSession, Is.True);
                Assert.That(session.SourceId, Is.EqualTo(store.SessionId));
                Assert.That(session.Selected.Id, Is.EqualTo("s_test-0001"),
                    "coming back to the running session shows its own episode again");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void TheRailOffersTheSavedSessionsAndReadsTheOneThatIsPicked()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001");

                var list = new AnalysisEpisodeList();
                var rail = new AnalysisSessionRail();
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                list.Bind(session);
                rail.Bind(session);

                Assert.That(RailRows(rail).Count, Is.EqualTo(2), "the running session, then the saved one");
                Assert.That(RailRows(rail)[0].userData, Is.Null, "the first line is the session running now");
                Assert.That((string)RailRows(rail)[1].userData, Is.EqualTo("s_saved_2026"));
                Assert.That(RailRows(rail)[1].Query<Label>(className: "analysis-rail-row-count").ToList()[0].text,
                    Is.EqualTo("1 episode"));
                Assert.That(RailRows(rail)[1].Query<Label>(className: "analysis-rail-row-date").ToList()[0].text,
                    Does.Contain("2026-10-01"), "a saved line prints when its session started");

                string pickedId = "not picked";
                rail.SessionPicked += id => pickedId = id;

                rail.SelectSession(1);
                Assert.That(pickedId, Is.EqualTo("s_saved_2026"), "picking a saved line publishes its id");
                Assert.That(session.IsRunningSession, Is.False, "picking a saved line shows that session");
                Assert.That(Rows(list).Count, Is.EqualTo(1));

                VisualElement savedRow = RowOf(list, "s_saved_2026-0001");
                Assert.That(savedRow, Is.Not.Null);
                Assert.That(savedRow.Query<Button>(className: "analysis-row-icon-export").ToList().Count,
                    Is.EqualTo(1),
                    "a row read back from an archive is exported through the same glyph a live row carries");
                Assert.That(savedRow.Query<Button>(className: "analysis-row-icon-delete").ToList().Count,
                    Is.EqualTo(1), "and deleted through it too");

                rail.SelectSession(0);
                Assert.That(pickedId, Is.Null, "the running session is published as null");
                Assert.That(session.IsRunningSession, Is.True,
                    "picking the first line goes back to the running session");
                Assert.That(RowOf(list, "s_test-0001"), Is.Not.Null);
                Assert.That(RailRows(rail)[0].ClassListContains("selected"), Is.True,
                    "the session on screen is the line the rail highlights");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void TheRailAlwaysShowsTheRunningSessionEvenWithNothingSaved()
        {
            MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
            AnalysisSessionRail rail = BoundRail(out _);

            Assert.That(RailRows(rail), Has.Count.EqualTo(1),
                "with nothing saved the rail still carries the session running now");
            Assert.That(RailRows(rail)[0].userData, Is.Null);
            Assert.That(RailRows(rail)[0].ClassListContains("selected"), Is.True,
                "and it is the session the tab has on screen");
            Assert.That(RailRows(rail)[0].Query<Label>(className: "analysis-rail-row-count").ToList()[0].text,
                Is.EqualTo("1 episode"));
        }

        /// <summary>
        /// A session the recorder is still appending to has nothing to show until its first episode ends. The
        /// rail names sessions a reader can open, so a line with no episode behind it - and a line the search
        /// left nothing of - are the same empty column, and it says so rather than leaving a blank one.
        /// </summary>
        [Test]
        public void TheRailHidesTheRunningSessionUntilItHoldsAnEpisode()
        {
            AnalysisSessionRail rail = BoundRail(out AnalysisSession session);

            Assert.That(session.RunningSessionId, Is.Not.Null, "the store still owns a session running now");
            Assert.That(RailRows(rail), Is.Empty, "but it has filed nothing, so there is no line to draw");
            Assert.That(rail.Query<Label>(className: "analysis-rail-empty").ToList(), Is.Not.Empty,
                "an empty rail says why it is empty instead of leaving a blank column");
            Assert.That(rail.Query<Label>(className: "analysis-rail-count").ToList()[0].text,
                Is.EqualTo("0 sessions"));

            MetricsStore.Instance.Add(Episode("s_test-0001", index: 1, scenario: "corridor"));
            Assert.That(session.Poll(), Is.True);

            Assert.That(RailRows(rail), Has.Count.EqualTo(1),
                "the session is listed the moment it has one episode behind it");
            Assert.That(RailRows(rail)[0].userData, Is.Null);
            Assert.That(RailRows(rail)[0].Query<Label>(className: "analysis-rail-row-name").ToList()[0].text,
                Is.EqualTo("corridor"));
        }

        /// <summary>
        /// A rail line carries the same two gestures an episode row does - export it, delete it - and neither
        /// of them is a pick: clicking the glyph of a session the tab is not on must do what the glyph says
        /// rather than move the reader, the way a row's own glyphs do not select the row.
        /// </summary>
        [Test]
        public void TheRailLineCarriesTheSessionGlyphsAndTheyDoNotPickTheLine()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001");

                var rail = new AnalysisSessionRail();
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                rail.Bind(session);

                VisualElement savedRow = RailRows(rail).First(row => (string)row.userData == "s_saved_2026");
                Button export = savedRow.Query<Button>(className: "analysis-row-icon-export").ToList()[0];
                Button delete = savedRow.Query<Button>(className: "analysis-row-icon-delete").ToList()[0];
                Label name = savedRow.Query<Label>(className: "analysis-rail-row-name").ToList()[0];

                Assert.That(export.tooltip, Is.Not.Empty, "an unlabelled glyph says what it does on hover");
                Assert.That(delete.tooltip, Is.Not.Empty);
                Assert.That(delete.ClassListContains("analysis-row-icon-delete"), Is.True,
                    "the delete glyph is the one the project already paints red");
                Assert.That(AnalysisEpisodeList.IsRowAction(export), Is.True);
                Assert.That(AnalysisEpisodeList.IsRowAction(delete), Is.True);
                Assert.That(AnalysisEpisodeList.IsRowAction(name), Is.False);
                Assert.That(AnalysisEpisodeList.IsRowAction(savedRow), Is.False);

                string picked = "not picked";
                string exported = null;
                string deleted = null;
                rail.SessionPicked += id => picked = id;
                rail.SessionExportRequested += id => exported = id;
                rail.SessionDeleteRequested += id => deleted = id;

                rail.ClickRow(savedRow, export);
                rail.ClickRow(savedRow, delete);
                Assert.That(picked, Is.EqualTo("not picked"), "a click on a glyph is not a pick");
                Assert.That(session.IsRunningSession, Is.True,
                    "the tab is still on the session it was on while the glyphs did their own work");

                rail.ClickExport(savedRow);
                rail.ClickDelete(savedRow);
                Assert.That(exported, Is.EqualTo("s_saved_2026"), "exporting a session names that session");
                Assert.That(deleted, Is.EqualTo("s_saved_2026"), "and so does deleting it");

                rail.ClickRow(savedRow, name);
                Assert.That(picked, Is.EqualTo("s_saved_2026"), "a click on the line itself picks that session");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// A session that is running and has been exported exists twice to a reader of the folder: as the store
        /// the recorder is still appending to, and as the catalogue line its export wrote. The picker names each
        /// session once, under the source that can act on it, so the running one is never offered beside its own
        /// saved copy - the copy that carries neither the export nor the delete glyph a live row does.
        /// </summary>
        [Test]
        public void TheRunningSessionIsOfferedOnceEvenAfterItWasExported()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(Episode("s_test-0001", index: 1));

                // The state an export leaves behind: the running session's own line and catalogue entry, filed
                // under the id the store is still using.
                WriteSavedSession(root, store.SessionId, store.SessionId + "-0001");

                var session = new AnalysisSession(store, new[] { root });

                Assert.That(session.SavedSessions, Is.Empty,
                    "the session running now is reached as the running one, not offered a second time");
                Assert.That(session.IsRunningSession, Is.True);
                Assert.That(session.Episodes.Select(episode => episode.Id),
                    Is.EqualTo(new[] { "s_test-0001" }),
                    "the store's copy is the one on screen, which is the one that can be acted on");

                var rail = new AnalysisSessionRail();
                rail.Bind(session);

                Assert.That(RailRows(rail), Has.Count.EqualTo(1),
                    "the running session is not listed a second time beside its own saved copy");
                Assert.That(RailRows(rail)[0].userData, Is.Null);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void PollNoticesASavedSessionWrittenAfterTheSessionWasBuilt()
        {
            string root = NewExportRoot();
            try
            {
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                Assert.That(session.SavedSessions, Is.Empty);
                Assert.That(session.Poll(), Is.False, "nothing has moved on disk or in the store");

                WriteSavedSession(root, "s_late", "s_late-0001");
                Assert.That(session.Poll(), Is.True, "a session saved after the fact is noticed");
                Assert.That(session.SavedSessions.Select(record => record.Id), Is.EqualTo(new[] { "s_late" }));

                Assert.That(session.Poll(), Is.False, "and the archive is read once, not on every frame");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// The episodes of a session are read by id, from whichever side of the archive holds it: the store
        /// while the id names the session running now, the catalogue while it names a saved one, and nothing
        /// at all - never a null a caller has to guard against - for an id neither side holds.
        /// </summary>
        [Test]
        public void EpisodesOfAnswersForTheRunningAndTheSavedSessionAndNothingElse()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                store.Add(Episode("s_test-0001", index: 1));
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001", "s_saved_2026-0002");

                var session = new AnalysisSession(store, new[] { root });

                Assert.That(session.RunningSessionId, Is.EqualTo(store.SessionId));
                Assert.That(session.EpisodesOf(session.RunningSessionId).Select(episode => episode.Id),
                    Is.EqualTo(new[] { "s_test-0001" }),
                    "the running session reads from the store the recorder still appends to");
                Assert.That(session.EpisodesOf("s_saved_2026").Select(episode => episode.Id),
                    Is.EqualTo(new[] { "s_saved_2026-0001", "s_saved_2026-0002" }),
                    "a saved session reads from the catalogue, oldest first");
                Assert.That(session.EpisodesOf("s_never_saved"), Is.Empty);
                Assert.That(session.EpisodesOf(null), Is.Empty);
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void TheRailListsTheRunningSessionFirstThenTheNewestSaved()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore.Instance.Add(Episode("s_test-0001", index: 1, scenario: "corridor"));
                WriteSession(root, "s_old", "2026-09-01T09:00:00Z", ("alpha", MetricsContract.OutcomeGoal));
                WriteSession(root, "s_new", "2026-10-02T09:00:00Z",
                    ("beta", MetricsContract.OutcomeGoal),
                    ("beta", MetricsContract.OutcomeGoal));

                var rail = new AnalysisSessionRail();
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                rail.Bind(session);

                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null, "s_new", "s_old" }),
                    "the session running now comes first, then the saved ones newest first");

                List<VisualElement> rows = RailRows(rail);
                Assert.That(rows[0].Query<Label>(className: "analysis-rail-row-name").ToList()[0].text,
                    Is.EqualTo("corridor"), "a session that ran one scenario is named by that scenario");
                Assert.That(rows[1].Query<Label>(className: "analysis-rail-row-name").ToList()[0].text,
                    Is.EqualTo("beta"));
                Assert.That(rail.Query<Label>(className: "analysis-rail-count").ToList()[0].text,
                    Is.EqualTo("3 sessions"));
                Assert.That(rows[0].ClassListContains("selected"), Is.True,
                    "the running session is the one on screen, so it is the one highlighted");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void TheRailSortsTheSavedSessionsTheWayTheSortAsks()
        {
            string root = NewExportRoot();
            try
            {
                // The session running now always leads, whatever the sort says, so it is given an episode to
                // be listed at all - the order under test is the one the saved lines take behind it.
                MetricsStore.Instance.Add(Episode("s_test-0001", index: 1, scenario: "gamma"));
                WriteSession(root, "s_old", "2026-09-01T09:00:00Z",
                    ("alpha", MetricsContract.OutcomeGoal),
                    ("alpha", MetricsContract.OutcomeCollision),
                    ("alpha", MetricsContract.OutcomeCollision));
                WriteSession(root, "s_new", "2026-10-02T09:00:00Z",
                    ("beta", MetricsContract.OutcomeGoal),
                    ("beta", MetricsContract.OutcomeGoal));

                var rail = new AnalysisSessionRail();
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                rail.Bind(session);

                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null, "s_new", "s_old" }),
                    "the default order is newest first");

                rail.SetSort(AnalysisSessionRail.SortOldest);
                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null, "s_old", "s_new" }),
                    "oldest first reverses the dated ones");

                rail.SetSort(AnalysisSessionRail.SortMostEpisodes);
                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null, "s_old", "s_new" }),
                    "three episodes outrank two");

                rail.SetSort(AnalysisSessionRail.SortBestSuccess);
                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null, "s_new", "s_old" }),
                    "two of two reached the goal where one of three did not");

                rail.SetSort(AnalysisSessionRail.SortScenario);
                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null, "s_old", "s_new" }),
                    "alpha is listed before beta");

                rail.SetSort(AnalysisSessionRail.SortNewest);
                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null, "s_new", "s_old" }));
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void TheRailSearchNarrowsTheSessions()
        {
            string root = NewExportRoot();
            try
            {
                // A line with no episode behind it is not drawn, so the running session is given one to keep
                // the whole point of the search - the lines it filters - in front of it.
                MetricsStore.Instance.Add(Episode("s_test-0001", index: 1, scenario: "corridor"));
                WriteSession(root, "s_alpha", "2026-09-01T09:00:00Z", ("alpha", MetricsContract.OutcomeGoal));
                WriteSession(root, "s_beta", "2026-10-02T09:00:00Z", ("beta", MetricsContract.OutcomeGoal));

                var rail = new AnalysisSessionRail();
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                rail.Bind(session);

                Assert.That(RailRows(rail), Has.Count.EqualTo(3),
                    "the session running now, then the two saved ones");

                rail.SetSearch("s_beta");
                Assert.That(RailSessionIds(rail), Is.EqualTo(new[] { "s_beta" }),
                    "a search keeps only the lines whose id matches");
                Assert.That(rail.Query<Label>(className: "analysis-rail-count").ToList()[0].text,
                    Is.EqualTo("1 session"));

                rail.SetSearch("beta");
                Assert.That(RailSessionIds(rail), Is.EqualTo(new[] { "s_beta" }),
                    "the scenario a session ran is searchable too");

                rail.SetSearch("nothing matches this");
                Assert.That(RailRows(rail), Is.Empty);
                Assert.That(rail.Query<Label>(className: "analysis-rail-empty").ToList(), Is.Not.Empty,
                    "an empty rail says why it is empty instead of leaving a blank column");
                Assert.That(rail.Query<Label>(className: "analysis-rail-count").ToList()[0].text,
                    Is.EqualTo("0 sessions"));

                rail.SetSearch(string.Empty);
                Assert.That(RailRows(rail), Has.Count.EqualTo(3));
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void TheSessionBarSplitsItsWidthByOutcome()
        {
            string root = NewExportRoot();
            try
            {
                WriteSession(root, "s_mix", "2026-10-02T09:00:00Z",
                    ("corridor", MetricsContract.OutcomeGoal),
                    ("corridor", MetricsContract.OutcomeGoal),
                    ("corridor", MetricsContract.OutcomeCollision),
                    ("corridor", MetricsContract.OutcomeTimeout));

                var rail = new AnalysisSessionRail();
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                rail.Bind(session);

                VisualElement row = RailRows(rail).First(element => (string)element.userData == "s_mix");
                List<VisualElement> fills = row.Query<VisualElement>(className: "analysis-bar-fill").ToList();

                Assert.That(fills, Has.Count.EqualTo(3),
                    "only the outcomes that happened get a segment");

                float[] widths = fills.Select(fill => fill.style.width.value.value).ToArray();
                Assert.That(widths[0], Is.EqualTo(50f).Within(0.01f), "two goals of four");
                Assert.That(widths[1], Is.EqualTo(25f).Within(0.01f), "one collision of four");
                Assert.That(widths[2], Is.EqualTo(25f).Within(0.01f), "one timeout of four");

                Assert.That(fills[0].ClassListContains("analysis-outcome-goal"), Is.True);
                Assert.That(fills[1].ClassListContains("analysis-outcome-collision"), Is.True);
                Assert.That(fills[2].ClassListContains("analysis-outcome-timeout"), Is.True);
                Assert.That(row.Query<Label>(className: "analysis-rail-row-tally").ToList()[0].text,
                    Is.EqualTo("2 goal \u00B7 2 other"));
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void PickingASessionLinePublishesItAndShowsItsEpisodes()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001");

                var list = new AnalysisEpisodeList();
                var rail = new AnalysisSessionRail();
                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                list.Bind(session);
                rail.Bind(session);

                var published = new List<string>();
                rail.SessionPicked += id => published.Add(id);

                rail.ClickRow(RailRows(rail)[1]);

                Assert.That(published, Is.EqualTo(new[] { "s_saved_2026" }),
                    "clicking a line publishes the session it names");
                Assert.That(session.IsRunningSession, Is.False);
                Assert.That(session.SourceId, Is.EqualTo("s_saved_2026"));
                Assert.That(RowOf(list, "s_saved_2026-0001"), Is.Not.Null,
                    "and the episode list now shows that session's episodes");
                Assert.That(RailRows(rail)[1].ClassListContains("selected"), Is.True,
                    "and the line that was clicked is the one the rail highlights");
            }
            finally
            {
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// Writes one session into a temporary root the way an export does: a session line, then a catalogue
        /// line and a trajectory record per episode. The root is what a caller then hands to
        /// <see cref="AnalysisSession"/> - a session saved before this one started, which is the whole
        /// situation the rail exists for.
        /// </summary>
        private static void WriteSavedSession(string root, string sessionId, params string[] episodeIds)
        {
            var archive = new TrajectoryArchive(root);
            archive.EnsureSessionLine(sessionId, "2026-10-01T09:00:00Z");

            int index = 0;
            foreach (string episodeId in episodeIds)
                AppendEpisode(archive, sessionId, episodeId, ++index, "corridor", MetricsContract.OutcomeGoal);
        }

        /// <summary>
        /// Writes one session whose start instant, scenarios and outcomes the caller decides, so a test can
        /// build sessions that differ in exactly the way the sort or the results bar is being read.
        /// </summary>
        private static void WriteSession(string root, string sessionId, string startedAt,
            params (string Scenario, string Outcome)[] episodes)
        {
            var archive = new TrajectoryArchive(root);
            archive.EnsureSessionLine(sessionId, startedAt);

            int index = 0;
            foreach ((string scenario, string outcome) in episodes)
                AppendEpisode(archive, sessionId, sessionId + "-" + (++index).ToString("D4"), index, scenario, outcome);
        }

        private static void AppendEpisode(
            TrajectoryArchive archive, string sessionId, string episodeId, int index, string scenario, string outcome)
        {
            EpisodeMetrics episode = ReplayEpisode();
            episode.Id = episodeId;
            episode.Session = sessionId;
            episode.Index = index;
            episode.Scenario = scenario;
            episode.Outcome = outcome;

            episode.TrajectoryRef = archive.Append(episode);
            archive.AppendCatalogue(episode);

            // The archive holds the points now, which is the state every catalogue line describes.
            episode.Trajectories.Clear();
        }

        private static string NewExportRoot()
            => Path.Combine(Path.GetTempPath(), "robotsnap_analysis_" + Guid.NewGuid().ToString("N"));

        private static void DeleteExportRoot(string root)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        /// <summary>The raw text of one root's catalogue line, so a test can say what the folder holds now.</summary>
        private static string Catalogue(string root)
            => ReadIfPresent(Path.Combine(root, TrajectoryArchive.CatalogueFileName));

        /// <summary>The raw text of one root's session line, so a test can say what the folder names now.</summary>
        private static string Sessions(string root)
            => ReadIfPresent(Path.Combine(root, TrajectoryArchive.SessionsFileName));

        private static string ReadIfPresent(string path)
            => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        // -- the session ordinal on the label --------------------------------

        [Test]
        public void AnEpisodeIsLabelledByItsPlaceInTheSession()
        {
            Assert.That(AnalysisFormatting.EpisodeLabel(Episode("a", index: 1, scenario: "default")),
                Is.EqualTo("01_default"));
            Assert.That(AnalysisFormatting.EpisodeLabel(Episode("b", index: 12, scenario: "default")),
                Is.EqualTo("12_default"), "two digits is a format, not a ceiling");
            Assert.That(AnalysisFormatting.EpisodeLabel(Episode("c", index: 0, scenario: "default")),
                Is.EqualTo("default"),
                "an episode with no place to show keeps its scenario rather than pretending to be number zero");
            Assert.That(AnalysisFormatting.EpisodeLabel(Episode("d", index: 3, scenario: "")),
                Is.EqualTo("03_(no scenario)"));
            Assert.That(
                AnalysisFormatting.EpisodeLabel(new EpisodeMetrics { Index = 7, Scenario = "default", Name = "my run" }),
                Is.EqualTo("my run"),
                "a name the reader gave the run wins over the label computed from its place and its scenario");
            Assert.That(
                AnalysisFormatting.EpisodeLabel(new EpisodeMetrics { Index = 7, Scenario = "default", Name = "  " }),
                Is.EqualTo("07_default"),
                "a name left blank is no name, not a blank label");
        }

        // -- clearing the session --------------------------------------------

        [Test]
        public void ClearingTheSessionStartsFromAnEmptyList()
        {
            MetricsStore.Instance.Add(Episode("s_test-0001"));
            MetricsStore.Instance.Add(Episode("s_test-0002"));

            var list = new AnalysisEpisodeList();
            var session = NewSession();
            list.Bind(session);
            Assert.That(Rows(list).Count, Is.EqualTo(2), "the session starts with the episodes the store holds");
            Assert.That(session.Selected, Is.Not.Null);

            MetricsStore.Instance.Clear();
            Assert.That(session.Poll(), Is.True, "a cleared store is a change the session has to notice");

            Assert.That(session.Episodes, Is.Empty);
            Assert.That(session.Selected, Is.Null, "nothing is selected once the session is empty");
            Assert.That(Rows(list), Is.Empty, "the list is empty again, not left holding removed rows");
            Assert.That(list.Query<Label>(className: "analysis-empty-message").ToList(), Is.Not.Empty,
                "an emptied session says so instead of drawing an empty list");
        }

        // -- deleting one episode --------------------------------------------

        [Test]
        public void DeletingOneEpisodeLeavesTheOthers()
        {
            MetricsStore store = MetricsStore.Instance;
            store.Add(Episode("s_test-0001"));
            store.Add(Episode("s_test-0002"));
            store.Add(Episode("s_test-0003"));

            var list = new AnalysisEpisodeList();
            var session = NewSession();
            list.Bind(session);
            session.Select("s_test-0002");

            EpisodeMetrics removed = store.Remove("s_test-0002");
            Assert.That(removed, Is.Not.Null, "the removal reports the episode it dropped");
            Assert.That(removed.Id, Is.EqualTo("s_test-0002"));
            Assert.That(store.Remove("s_test-9999"), Is.Null, "an unknown id removes nothing");

            Assert.That(session.Poll(), Is.True);
            Assert.That(session.Episodes.Select(episode => episode.Id),
                Is.EqualTo(new[] { "s_test-0001", "s_test-0003" }),
                "the two episodes that were not dropped are still there, in order");
            Assert.That(session.Selected.Id, Is.EqualTo("s_test-0003"),
                "deleting the selected episode moves the selection instead of leaving it dangling");
            Assert.That(Rows(list).Count, Is.EqualTo(2));
        }

        /// <summary>
        /// Deleting a run the reader opened from an archive has to reach the folder it was read from: the store
        /// has never held it, so a delete that only touched the session would leave the record on disk and the
        /// episode back on the next tick.
        /// </summary>
        [Test]
        public void DeletingASavedEpisodeTakesItsRecordOffDiskAndLeavesTheOthers()
        {
            string root = NewExportRoot();
            try
            {
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001", "s_saved_2026-0002");

                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                session.ShowSaved("s_saved_2026");

                AnalysisTabController.DeleteEpisode(session, "s_saved_2026-0001");

                Assert.That(session.Episodes.Select(episode => episode.Id),
                    Is.EqualTo(new[] { "s_saved_2026-0002" }),
                    "the run that was not deleted is still in the session on screen");
                Assert.That(Catalogue(root), Does.Not.Contain("s_saved_2026-0001"),
                    "and the one that was is gone from the catalogue the tab reads");
                Assert.That(Catalogue(root), Does.Contain("s_saved_2026-0002"),
                    "which leaves the other lines exactly as they were");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// A session that held one episode and lost it is not a session a reader can open any more, so the tab
        /// goes back to the one the recorder is still appending to rather than staying on nothing.
        /// </summary>
        [Test]
        public void DeletingTheLastEpisodeOfASavedSessionLeavesNothing()
        {
            string root = NewExportRoot();
            try
            {
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001");

                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                session.ShowSaved("s_saved_2026");

                AnalysisTabController.DeleteEpisode(session, "s_saved_2026-0001");

                Assert.That(session.IsRunningSession, Is.True,
                    "the session on screen is gone, so the tab is back on the running one");
                Assert.That(session.SavedSessions, Is.Empty,
                    "and the folder's empty session line went with the episode it had nothing to hold");
                Assert.That(Catalogue(root), Does.Not.Contain("s_saved_2026"));
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        // -- deleting a session ------------------------------------------------

        [Test]
        public void DeletingASavedSessionTakesItOffDiskAndLeavesTheRunningOne()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001", "s_saved_2026-0002");

                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                session.ShowSaved("s_saved_2026");

                AnalysisTabController.DeleteSession(session, "s_saved_2026");

                Assert.That(session.IsRunningSession, Is.True,
                    "the session on screen went away, so the tab is back on the running one");
                Assert.That(session.Episodes.Select(episode => episode.Id),
                    Is.EqualTo(new[] { "s_test-0001" }),
                    "which still holds everything the recorder filed");
                Assert.That(Catalogue(root), Does.Not.Contain("s_saved_2026"),
                    "the session's catalogue lines left the folder with it");
                Assert.That(Sessions(root), Does.Not.Contain("s_saved_2026"),
                    "and so did the line that announced it");
                Assert.That(File.Exists(Path.Combine(
                        root, TrajectoryArchive.TrajectoriesFolderName, "s_saved_2026.rbt")),
                    Is.False, "the trajectory archive went too");

                var rail = new AnalysisSessionRail();
                rail.Bind(session);
                Assert.That(RailSessionIds(rail), Is.EqualTo(new string[] { null }),
                    "the rail lists the running session and nothing else");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        /// <summary>
        /// Deleting the session the recorder owns is the one delete that is two things at once: the same
        /// session's files go from disk, and the list a reader is looking at is emptied, under a new id so the
        /// moments before the delete cannot be confused with the moments after it.
        /// </summary>
        [Test]
        public void DeletingTheRunningSessionEmptiesTheStoreAndStartsANewOne()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                // The recorder files every episode under the id of the session it ran in, which is what the
                // session delete matches its lines on.
                EpisodeMetrics episode = Episode("s_test-0001", index: 1);
                episode.Session = store.SessionId;
                store.Add(episode);
                MetricsExporter.Export(store, root);
                string deletedId = store.SessionId;

                var session = new AnalysisSession(store, new[] { root });
                Assert.That(session.Episodes, Has.Count.EqualTo(1));

                AnalysisTabController.DeleteSession(session, null);

                Assert.That(store.Count, Is.Zero, "the episodes are gone from the session the recorder owns");
                Assert.That(store.SessionId, Is.Not.EqualTo(deletedId),
                    "and the id left behind names the new session, not the one that was deleted");
                Assert.That(session.Episodes, Is.Empty,
                    "the tab shows the new, empty session rather than the one that just went");
                Assert.That(Catalogue(root), Does.Not.Contain("s_test-0001"));
                Assert.That(Sessions(root), Does.Not.Contain(deletedId));
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        // -- exporting one run again -------------------------------------------

        /// <summary>
        /// An episode read back from an archive is handed over as its own file the same way a live one is: its
        /// trajectory sits in the record the episode names, so the export has to read it out of that folder
        /// rather than out of the one the recorder is writing to.
        /// </summary>
        [Test]
        public void ExportingAReplayedEpisodeWritesItsOwnDocument()
        {
            string root = NewExportRoot();
            string target = NewExportRoot();
            try
            {
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001");

                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                session.ShowSaved("s_saved_2026");

                MetricsExportReport report =
                    AnalysisTabController.ExportEpisode(session, "s_saved_2026-0001", target);

                Assert.That(report.Directory, Is.EqualTo(target));
                string file = Path.Combine(target, "episode_s_saved_2026-0001.json");
                Assert.That(File.Exists(file), Is.True, "the run is handed over as its own file");

                string document = File.ReadAllText(file);
                Assert.That(document, Does.Contain("s_saved_2026-0001"));
                Assert.That(document, Does.Contain("\"trajectories\""),
                    "and the points are read out of the archive the episode named, written in clear");
                Assert.That(document, Does.Not.Contain("trajectory_ref"),
                    "a single file carries no pointer to a folder the reader may not have");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
                DeleteExportRoot(target);
            }
        }

        // -- naming a session and a run ----------------------------------------

        [Test]
        public void NamingASavedEpisodeReachesTheArchiveAndTheLabel()
        {
            string root = NewExportRoot();
            try
            {
                WriteSavedSession(root, "s_saved_2026", "s_saved_2026-0001", "s_saved_2026-0002");

                var session = new AnalysisSession(MetricsStore.Instance, new[] { root });
                session.ShowSaved("s_saved_2026");

                AnalysisTabController.RenameEpisode(session, "s_saved_2026-0001", "first run");

                EpisodeMetrics named = session.Episodes.First(episode => episode.Id == "s_saved_2026-0001");
                Assert.That(named.Name, Is.EqualTo("first run"),
                    "the archive reads the run back under the name it was given");
                Assert.That(AnalysisFormatting.EpisodeLabel(named), Is.EqualTo("first run"),
                    "which is what the row prints");
                Assert.That(Catalogue(root), Does.Contain("\"first run\""));

                AnalysisTabController.RenameEpisode(session, "s_saved_2026-0001", null);

                EpisodeMetrics cleared = session.Episodes.First(episode => episode.Id == "s_saved_2026-0001");
                Assert.That(cleared.Name, Is.Null, "an emptied name takes the name away again");
                Assert.That(AnalysisFormatting.EpisodeLabel(cleared), Is.EqualTo("01_corridor"),
                    "and the row falls back to the label computed from the run's place");
                Assert.That(Catalogue(root), Does.Not.Contain("first run"));
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        [Test]
        public void NamingASessionReachesTheStoreTheArchiveAndTheRail()
        {
            string root = NewExportRoot();
            try
            {
                MetricsStore store = MetricsStore.Instance;
                EpisodeMetrics episode = Episode("s_test-0001", index: 1, scenario: "corridor");
                episode.Session = store.SessionId;
                store.Add(episode);
                MetricsExporter.Export(store, root);

                var session = new AnalysisSession(store, new[] { root });
                var rail = new AnalysisSessionRail();
                rail.Bind(session);

                AnalysisTabController.RenameSession(session, "Morning run");

                Assert.That(store.SessionName, Is.EqualTo("Morning run"),
                    "the running session keeps its name where the next export reads it");
                Assert.That(Sessions(root), Does.Contain("\"Morning run\""),
                    "and the folder it was exported to carries it in the session line");
                Assert.That(RailRows(rail)[0].Query<Label>(className: "analysis-rail-row-name").ToList()[0].text,
                    Is.EqualTo("Morning run"), "the rail line names the session the way the reader did");

                AnalysisTabController.RenameSession(session, null);

                Assert.That(store.SessionName, Is.Empty);
                Assert.That(Sessions(root), Does.Not.Contain("Morning run"));
                Assert.That(RailRows(rail)[0].Query<Label>(className: "analysis-rail-row-name").ToList()[0].text,
                    Is.EqualTo("corridor"),
                    "an emptied name falls back to the label computed from the session's episodes");
            }
            finally
            {
                MetricsStore.Instance.ForgetArchive();
                DeleteExportRoot(root);
            }
        }

        // -- the headings that name a panel ------------------------------------

        /// <summary>
        /// Both panels that describe something - the list's session and the map's episode - are headed by its
        /// name at heading size, with the pencil that renames it in place: Enter or the field losing the focus
        /// keeps what was typed, Escape throws it away.
        /// </summary>
        [Test]
        public void ThePanelHeadingsCarryTheNameAndAPencil()
        {
            MetricsStore.Instance.Add(Episode("s_test-0001", index: 1, scenario: "corridor"));
            AnalysisEpisodeList list = BoundList(out _);

            AnalysisEditableTitle heading = list.Query<AnalysisEditableTitle>().ToList()[0];
            Assert.That(heading.ClassListContains("analysis-title"), Is.True);
            Assert.That(heading.Value, Is.EqualTo("corridor"),
                "the episodes panel is headed by the session whose rows it lists");
            Assert.That(heading.Pencil.ClassListContains("analysis-title-pencil"), Is.True);
            Assert.That(heading.Pencil.tooltip, Is.Not.Empty, "an unlabelled glyph says what it does on hover");

            var renames = new List<string>();
            list.SessionRenameRequested += name => renames.Add(name);

            heading.BeginEdit();
            Assert.That(heading.IsEditing, Is.True, "the pencil swaps the label for a field");
            Assert.That(heading.Editor.value, Is.EqualTo("corridor"), "which starts on the name it replaces");

            heading.Editor.value = "Morning benchmark";
            heading.CommitEdit();

            Assert.That(heading.IsEditing, Is.False, "committing puts the label back");
            Assert.That(renames, Is.EqualTo(new[] { "Morning benchmark" }),
                "Enter, or the field losing the focus, reports the name the reader typed");

            heading.BeginEdit();
            heading.Editor.value = "thrown away";
            heading.CancelEdit();
            Assert.That(renames, Is.EqualTo(new[] { "Morning benchmark" }), "Escape commits nothing");
            Assert.That(heading.Value, Is.EqualTo("Morning benchmark"),
                "and puts the previous name back where the field was");

            // The heading over the trajectories is the same control, laid out by the tab in its UXML host.
            var host = new VisualElement();
            AnalysisEditableTitle episodeHeading = AnalysisTabController.BuildEpisodeTitle(host);
            episodeHeading.Show("01_corridor");

            Assert.That(host.Query<Label>(className: "analysis-title-label").ToList()[0].text,
                Is.EqualTo("01_corridor"), "the trajectory panel is headed by the run it draws");
            Assert.That(host.Query<Button>(className: "analysis-title-pencil").ToList().Count,
                Is.EqualTo(1), "with the same pencil beside it");

            string cleared = "not renamed";
            episodeHeading.RenameRequested += name => cleared = name;
            episodeHeading.BeginEdit();
            episodeHeading.Editor.value = string.Empty;
            episodeHeading.CommitEdit();
            Assert.That(cleared, Is.Null, "emptying the field asks for the name to be taken away");
        }

        /// <summary>
        /// The tab finds its tree by name, so an element renamed on one side only - in the UXML or in the
        /// controller - leaves a panel without its button. No compiler catches that, so the two names are
        /// pinned here, together with the buttons the header no longer offers.
        /// </summary>
        [Test]
        public void TheTemplateCarriesEveryElementTheTabAsksForAndNothingItDoesNot()
        {
            string path = Path.Combine(Application.dataPath, "UI/Tabs/Analysis/AnalysisTab.uxml");
            Assert.That(File.Exists(path), Is.True, $"the template is where the tab reads it: {path}");

            string template = File.ReadAllText(path);
            string[] required =
            {
                "AnalysisPage",
                "AnalysisOpenFolderButton",
                "AnalysisExportStatus",
                "AnalysisSessionRailHost",
                "AnalysisEpisodeListHost",
                "AnalysisEpisodeDetailHost",
                "AnalysisSessionSummaryHost",
                "AnalysisEpisodeTitleHost",
                "AnalysisMapPanel",
                "AnalysisDetailColumn",
                "AnalysisTimelineHost",
            };

            foreach (string name in required)
                Assert.That(template, Does.Contain("name=\"" + name + "\""), $"the template carries {name}");

            Assert.That(template, Does.Not.Contain("AnalysisClearButton"),
                "clearing a session is not a header button any more");
            Assert.That(template, Does.Not.Contain("AnalysisDeleteDataButton"));
            Assert.That(template, Does.Contain("Open folder"), "the header opens the folder the sessions go to");
        }

        /// <summary>
        /// The same contract, read the way the tab reads it: the template is instantiated and the elements are
        /// found by the names the controller asks for, so a name that drifted between the two is caught here
        /// rather than as an empty panel on screen.
        /// </summary>
        [Test]
        public void TheTemplateHandsTheTabTheTreeItAsksFor()
        {
            VisualTreeAsset template = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Assets/UI/Tabs/Analysis/AnalysisTab.uxml");
            Assert.That(template, Is.Not.Null, "the template the tab is built from exists");

            VisualElement tree = template.Instantiate();
            Assert.That(tree.Q<Button>("AnalysisOpenFolderButton").text, Is.EqualTo("Open folder"));
            Assert.That(tree.Q<Label>("AnalysisExportStatus"), Is.Not.Null);
            Assert.That(tree.Q<VisualElement>("AnalysisPage"), Is.Not.Null);

            // The episode's heading shares the trajectory panel's heading row with the outcome chip beside it.
            VisualElement episodeTitleHost = tree.Q<VisualElement>("AnalysisEpisodeTitleHost");
            Assert.That(episodeTitleHost, Is.Not.Null);
            Assert.That(episodeTitleHost.parent.ClassListContains("analysis-section-trailing"), Is.True);
            Assert.That(episodeTitleHost.parent.Q<Label>("AnalysisMapOutcome"), Is.Not.Null);

            // The metrics of the run and the overview of the session are stacked in the column that scrolls.
            VisualElement detailHost = tree.Q<VisualElement>("AnalysisEpisodeDetailHost");
            VisualElement summaryHost = tree.Q<VisualElement>("AnalysisSessionSummaryHost");
            ScrollView scroll = detailHost.GetFirstAncestorOfType<ScrollView>();
            Assert.That(scroll, Is.Not.Null, "the detail and the overview live in a column that scrolls");
            Assert.That(summaryHost.GetFirstAncestorOfType<ScrollView>(), Is.SameAs(scroll),
                "and the two are stacked in the same one");
            Assert.That(scroll.parent, Is.SameAs(tree.Q<VisualElement>("AnalysisDetailColumn")));
        }

        // -- the confirmation dialog -----------------------------------------

        [Test]
        public void TheConfirmationDialogDoesNotActUntilConfirmed()
        {
            MetricsStore store = MetricsStore.Instance;
            store.Add(Episode("s_test-0001"));

            var dialog = new ConfirmationDialog(
                "Clear this session?",
                "This cannot be undone.",
                "Clear session",
                alternateText: "Export first");

            int confirmations = 0;
            int alternates = 0;
            dialog.Confirmed += () =>
            {
                confirmations++;
                store.Clear();
            };
            dialog.Alternate += () => alternates++;

            Assert.That(dialog.AlternateButton.text, Is.EqualTo("Export first"),
                "the safer alternative is offered by name");
            Assert.That(dialog.ConfirmButton.ClassListContains("destructive"), Is.True,
                "the destructive button is told apart from the others");
            Assert.That(dialog.CancelButton.ClassListContains("destructive"), Is.False);

            // Escape, a click on the backdrop and the cancel button all run this path.
            dialog.Dismiss();
            Assert.That(confirmations, Is.Zero, "closing the dialog does not confirm anything");
            Assert.That(store.Count, Is.EqualTo(1), "and leaves the store exactly as it was");
            Assert.That(dialog.parent, Is.Null, "a dismissed dialog leaves the tree");

            Assert.That(alternates, Is.Zero, "the alternative only runs when it is asked for");
            dialog.Accept();
            Assert.That(confirmations, Is.EqualTo(1), "confirming runs the destructive path once");
            Assert.That(store.Episodes, Is.Empty);
        }

        // -- an episode with no human does not lie about it -----------------

        [Test]
        public void AnEpisodeWithoutHumansDoesNotClaimADistance()
        {
            Assert.That(AnalysisFormatting.HumanDistance(EpisodeMetrics.NoHumanDistance),
                Is.EqualTo(AnalysisFormatting.NoHumans));
            Assert.That(AnalysisFormatting.HumanDistance(EpisodeMetrics.NoHumanDistance),
                Does.Not.Contain("0.00"), "the no-human sentinel is not a measurement of zero metres");

            MetricsStore.Instance.Add(Episode(
                "s_test-0001",
                minHuman: EpisodeMetrics.NoHumanDistance,
                meanHuman: EpisodeMetrics.NoHumanDistance));

            var detailHost = new VisualElement();
            var detail = new AnalysisEpisodeDetail(detailHost);
            detail.Bind(NewSession());

            Assert.That(MetricValue(detailHost, "Closest human"), Is.EqualTo(AnalysisFormatting.NoHumans));
            Assert.That(MetricValue(detailHost, "Mean nearest human"), Is.EqualTo(AnalysisFormatting.NoHumans));
            Assert.That(MetricValue(detailHost, "Closest human"), Does.Not.Contain("0.00"));

            // The session tile follows the same rule: no human in any episode is not a mean of zero.
            var summaryHost = new VisualElement();
            var summary = new AnalysisSessionSummary(summaryHost);
            AnalysisSession session = NewSession();
            summary.Show(session.Episodes, AnalysisSessionSummary.WholeSession);

            Assert.That(Tile(summaryHost, "Mean closest human"), Is.Not.Null,
                "the session overview carries a closest-human tile");
            Assert.That(TileValue(summaryHost, "Mean closest human"), Is.EqualTo(AnalysisFormatting.NoHumans));
        }

        // -- the episode panel is a reading, not a place for actions ---------

        [Test]
        public void TheEpisodeDetailNamesTheEpisodeAndCarriesNoActionOfItsOwn()
        {
            var host = new VisualElement();
            var detail = new AnalysisEpisodeDetail(host);
            detail.Bind(NewSession());

            Assert.That(host.Query<Button>().ToList(), Is.Empty,
                "export and delete live in the list row, not on the panel that describes a run");
            Assert.That(host.Query<VisualElement>(className: "analysis-empty-message").ToList(), Is.Not.Empty,
                "nothing selected says so");

            MetricsStore.Instance.Add(Episode("s_test-0001", index: 4, scenario: "default"));
            detail.Bind(NewSession());

            Assert.That(host.Query<Label>(className: "analysis-detail-subject").ToList()[0].text,
                Is.EqualTo("Episode 04_default"), "the panel names the run the way the list does");
            Assert.That(MetricValue(host, "Episode id"), Is.EqualTo("s_test-0001"),
                "the stable id is still readable, it is just not the label anymore");
        }

        // -- one colour per agent -------------------------------------------

        [Test]
        public void TheLegendGivesEveryAgentItsOwnColour()
        {
            string[] keys = { "robot_1", "human_1", "human_2", "human_3" };
            IReadOnlyList<AnalysisAgentPalette.TrackColour> tracks = AnalysisAgentPalette.Assign(keys, "robot_1");

            Assert.That(tracks.Count, Is.EqualTo(keys.Length), "every track of the episode is drawn");
            Assert.That(tracks.Select(track => track.Key), Is.Unique, "each agent is named once");
            Assert.That(Colours(tracks), Is.Unique, "each agent draws in its own colour");

            Assert.That(tracks[0].IsRobot, Is.True, "the robot is the first, distinguished entry");
            Assert.That(tracks[0].Colour, Is.EqualTo(AnalysisAgentPalette.RobotColour));
            Assert.That(tracks[0].Label, Is.EqualTo("Robot"));
            Assert.That(Colours(tracks).Skip(1), Does.Not.Contain(Colours(tracks)[0]),
                "no human is drawn in the robot's colour");

            // The colour of a human depends on the agent, not on the order a dictionary enumerated in.
            string[] shuffled = { "human_3", "robot_1", "human_1", "human_2" };
            Assert.That(Colours(AnalysisAgentPalette.Assign(shuffled, "robot_1")), Is.EqualTo(Colours(tracks)));
        }

        [Test]
        public void AMultiRobotFleetIsNotDrawnAsACrowd()
        {
            string[] keys = { "robot_1", "robot_2", "robot_3", "human_1" };
            var fleet = new List<string> { "robot_1", "robot_2", "robot_3" };
            IReadOnlyList<AnalysisAgentPalette.TrackColour> tracks =
                AnalysisAgentPalette.Assign(keys, "robot_1", fleet);

            Assert.That(tracks.Count, Is.EqualTo(keys.Length), "every robot and every human is drawn");
            Assert.That(Colours(tracks), Is.Unique, "no two agents share a colour, fleet or crowd");

            Assert.That(tracks[0].Key, Is.EqualTo("robot_1"), "the followed robot leads the legend");
            Assert.That(tracks[0].IsRobot, Is.True);
            Assert.That(tracks[0].Label, Is.EqualTo("Robot"));

            Assert.That(tracks[1].Key, Is.EqualTo("robot_2"));
            Assert.That(tracks[1].IsRobot, Is.True, "a second robot is a robot, not a crowd member");
            Assert.That(tracks[1].Label, Is.EqualTo("Robot 2"));

            Assert.That(tracks[2].Key, Is.EqualTo("robot_3"));
            Assert.That(tracks[2].IsRobot, Is.True);
            Assert.That(tracks[2].Label, Is.EqualTo("Robot 3"));

            Assert.That(tracks[3].Key, Is.EqualTo("human_1"), "the crowd comes after the fleet");
            Assert.That(tracks[3].IsRobot, Is.False);
            Assert.That(tracks[3].Label, Is.EqualTo("Human 1"));

            // The roster decides what a robot is even when the key alone could be read as a crowd member.
            Assert.That(AnalysisAgentPalette.IsRobotKey("robot_2", fleet), Is.True);
            Assert.That(AnalysisAgentPalette.IsRobotKey("human_1", fleet), Is.False);

            // Ordered by the recorder's roster, not by the order the dictionary enumerated in.
            string[] shuffled = { "human_1", "robot_3", "robot_2", "robot_1" };
            Assert.That(
                AnalysisAgentPalette.Assign(shuffled, "robot_1", fleet)
                    .Select(track => track.Key).ToList(),
                Is.EqualTo(new[] { "robot_1", "robot_2", "robot_3", "human_1" }));
        }

        [Test]
        public void APrefixDoesNotCountASecondRobotAsAHuman()
        {
            var episode = new EpisodeMetrics
            {
                Id = "s_test-0001",
                Robot = "robot_1",
                Robots = new List<string> { "robot_1", "robot_2" },
                WorldSeconds = 4.0,
                PersonalSpaceRadiusMetres = 0.5,
                Trajectories = new Dictionary<string, List<double[]>>
                {
                    ["robot_1"] = new List<double[]>
                    {
                        new[] { 0.0, 0.0, 0.0 },
                        new[] { 1.0, 1.0, 0.0 },
                        new[] { 2.0, 2.0, 0.0 },
                        new[] { 3.0, 3.0, 0.0 },
                        new[] { 4.0, 4.0, 0.0 },
                    },
                    // A second robot running right on top of the first: a person would read as a collision, a
                    // robot must not, because the recorder never measured a robot as a human.
                    ["robot_2"] = new List<double[]>
                    {
                        new[] { 0.0, 0.1, 0.0 },
                        new[] { 4.0, 4.1, 0.0 },
                    },
                },
            };

            AnalysisPrefixMetrics metrics = AnalysisTrackReader.Metrics(episode, 2.0);

            Assert.That(metrics.HasRobot, Is.True);
            Assert.That(metrics.MinHumanDistanceMetres, Is.EqualTo(EpisodeMetrics.NoHumanDistance),
                "with no crowd at all the prefix reports the no-human sentinel, not the second robot");
            Assert.That(metrics.AverageHumanDistanceMetres, Is.EqualTo(EpisodeMetrics.NoHumanDistance));
            Assert.That(metrics.MinClearanceMetres, Is.EqualTo(EpisodeMetrics.NoHumanDistance),
                "and neither is there a clearance to report");
            Assert.That(metrics.PersonalSpaceIntrusions, Is.Zero,
                "a robot passing through another robot's space is not a personal-space intrusion");
        }

        /// <summary>
        /// The frise has to read the same passage the recorder did: the same footprints, the same clearance.
        ///
        /// The trajectory file carries poses and not radii, so the prefix borrows the pair the episode
        /// recorded. Reading it as a bare centre distance would make the numbers beside the cursor disagree
        /// with the numbers the run was scored on - two views of one episode, saying two different things.
        /// </summary>
        [Test]
        public void APrefixMeasuresTheSameClearanceTheRecorderDid()
        {
            var episode = new EpisodeMetrics
            {
                Id = "s_test-0001",
                Robot = "robot_1",
                WorldSeconds = 4.0,
                PersonalSpaceRadiusMetres = 0.5,
                RobotRadiusMetres = 0.3,
                HumanRadiusMetres = 0.3,
                Trajectories = new Dictionary<string, List<double[]>>
                {
                    ["robot_1"] = new List<double[]>
                    {
                        new[] { 0.0, 0.0, 0.0 },
                        new[] { 4.0, 2.0, 0.0 },
                    },
                    ["human_1"] = new List<double[]>
                    {
                        new[] { 0.0, 1.0, 0.0 },
                        new[] { 4.0, 1.0, 0.0 },
                    },
                },
            };

            AnalysisPrefixMetrics metrics = AnalysisTrackReader.Metrics(episode, 4.0);

            Assert.That(metrics.MinHumanDistanceMetres, Is.EqualTo(1.0).Within(1e-6),
                "the human distance stays centre to centre, as the recorder wrote it");
            Assert.That(metrics.MinClearanceMetres, Is.EqualTo(0.4).Within(1e-6),
                "the clearance takes both footprints off, as the recorder did");
            Assert.That(metrics.PersonalSpaceIntrusions, Is.EqualTo(1),
                "0.4 m of air is inside a 0.5 m personal space");

            Assert.That(AnalysisFormatting.Clearance(-0.2), Is.EqualTo("-0.20 m"),
                "bodies that overlap read as a negative gap, not as no measurement");
            Assert.That(AnalysisFormatting.Clearance(EpisodeMetrics.NoHumanDistance),
                Is.EqualTo(AnalysisFormatting.NoHumans));
        }

        // -- the session follows the store ----------------------------------

        [Test]
        public void TheSessionKeepsTheSelectedEpisodeWhileNewOnesArrive()
        {
            MetricsStore.Instance.Add(Episode("s_test-0001"));
            MetricsStore.Instance.Add(Episode("s_test-0002"));
            var session = NewSession();

            Assert.That(session.Selected.Id, Is.EqualTo("s_test-0002"), "the newest episode is selected first");

            session.Select("s_test-0001");
            Assert.That(session.Selected.Id, Is.EqualTo("s_test-0001"));

            MetricsStore.Instance.Add(Episode("s_test-0003"));
            Assert.That(session.Poll(), Is.True);
            Assert.That(session.Selected.Id, Is.EqualTo("s_test-0001"),
                "an episode ending must not pull the reader off the one being examined");

            MetricsStore.Instance.Clear();
            Assert.That(session.Poll(), Is.True, "clearing the store starts a new, empty session");
            Assert.That(session.Selected, Is.Null);
            Assert.That(session.Episodes, Is.Empty);
        }

        // -- which panels the selection asks for ------------------------------

        /// <summary>
        /// With no multi-selection left, the detail panel and the map answer the same question - is there an
        /// episode on screen? - and nothing selected leaves the session overview alone in the column, which is
        /// the reading a reader who has not picked a run yet is after.
        /// </summary>
        [Test]
        public void TheDetailAndTheMapFollowWhetherAnEpisodeIsSelected()
        {
            Assert.That(AnalysisTabController.ShowsEpisodeDetail(true), Is.True,
                "an episode on screen is the run the metric panel and the map describe");
            Assert.That(AnalysisTabController.ShowsTrajectoryMap(true), Is.True);
            Assert.That(AnalysisTabController.ShowsEpisodeDetail(false), Is.False,
                "nothing selected is the overview of the whole session, and only that");
            Assert.That(AnalysisTabController.ShowsTrajectoryMap(false), Is.False,
                "and there is no trajectory to draw for it");
        }

        [Test]
        public void TheOverviewCountsOnlyTheEpisodesItWasHanded()
        {
            MetricsStore store = MetricsStore.Instance;
            store.Add(Episode("s_test-0001", MetricsContract.OutcomeGoal, index: 1, intrusions: 3));
            store.Add(Episode("s_test-0002", MetricsContract.OutcomeCollision, index: 2, intrusions: 1));
            store.Add(Episode("s_test-0003", MetricsContract.OutcomeCollision, index: 3, intrusions: 0));

            var host = new VisualElement();
            var summary = new AnalysisSessionSummary(host);
            summary.Show(new List<EpisodeMetrics> { store.Get("s_test-0002"), store.Get("s_test-0003") },
                AnalysisSessionSummary.SelectionScope(2));

            Assert.That(TileValue(host, "Episodes"), Is.EqualTo("2"));
            Assert.That(TileValue(host, "Success rate"), Is.EqualTo("0 %"), "neither of the two reached the goal");
            Assert.That(TileValue(host, "Personal space intrusions"), Is.EqualTo("1"),
                "the intrusion count is summed over the two episodes, not over the session");
            Assert.That(Captions(host, "analysis-distribution-name"),
                Is.EqualTo(new[] { "Collision" }),
                "the mix lists only the outcomes the handed episodes ended as");
            Assert.That(Captions(host, "analysis-distribution-count"), Is.EqualTo(new[] { "2" }));

            // The overview of the whole session is a different reading, and says so.
            summary.Show(store.Episodes, AnalysisSessionSummary.WholeSession);
            Assert.That(TileValue(host, "Episodes"), Is.EqualTo("3"));
            Assert.That(TileValue(host, "Success rate"), Is.EqualTo("33.3 %"));
            Assert.That(Captions(host, "analysis-section-subtitle")[0],
                Does.Contain("every episode the session ran"));
        }

        // -- the filters ------------------------------------------------------

        [Test]
        public void TheScenarioSearchAndIntrusionFiltersNarrowTheList()
        {
            MetricsStore store = MetricsStore.Instance;
            store.Add(Episode("s_test-0001", MetricsContract.OutcomeGoal, index: 1,
                scenario: "corridor", intrusions: 0));
            store.Add(Episode("s_test-0002", MetricsContract.OutcomeGoal, index: 2,
                scenario: "intersection", intrusions: 4));

            AnalysisEpisodeList list = BoundList(out AnalysisSession session);

            // The scenario criterion is only offered when the session ran more than one scenario.
            Assert.That(list.Query<DropdownField>(className: "analysis-filter-second").ToList()[0].style.display.value,
                Is.EqualTo(DisplayStyle.Flex));

            list.SetScenarioFilter("intersection");
            Assert.That(list.VisibleIds, Is.EqualTo(new[] { "s_test-0002" }));

            list.SetScenarioFilter(AnalysisEpisodeFilter.Any);
            Assert.That(list.VisibleIds.Count, Is.EqualTo(2));

            list.SetSearch("02");
            Assert.That(list.VisibleIds, Is.EqualTo(new[] { "s_test-0002" }),
                "the search reads the label the row shows");

            list.SetSearch(string.Empty);
            list.SetIntrusionsOnly(true);
            Assert.That(list.VisibleIds, Is.EqualTo(new[] { "s_test-0002" }),
                "only the episode whose robot entered a personal space is left");

            list.SetIntrusionsOnly(false);
            Assert.That(session.Episodes.Count, Is.EqualTo(2), "the session itself is untouched by filtering");
        }

        [Test]
        public void ASessionWithOneScenarioDoesNotOfferTheScenarioFilter()
        {
            MetricsStore.Instance.Add(Episode("s_test-0001", index: 1, scenario: "corridor"));
            MetricsStore.Instance.Add(Episode("s_test-0002", index: 2, scenario: "corridor"));

            AnalysisEpisodeList list = BoundList(out _);
            DropdownField scenarioFilter = list.Query<DropdownField>(className: "analysis-filter-second").ToList()[0];

            Assert.That(scenarioFilter.style.display.value, Is.EqualTo(DisplayStyle.None),
                "a criterion whose only answer is 'all of them' is not worth a control");
        }

        // -- a row's glyphs are not a row click -------------------------------

        [Test]
        public void TheRowGlyphsDoNotSelectTheRow()
        {
            MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
            AnalysisEpisodeList list = BoundList(out _);

            VisualElement row = RowOf(list, "s_test-0001");
            Assert.That(row, Is.Not.Null);

            Button export = row.Query<Button>(className: "analysis-row-icon-export").ToList()[0];
            Button delete = row.Query<Button>(className: "analysis-row-icon-delete").ToList()[0];
            Label label = row.Query<Label>(className: "analysis-row-label").ToList()[0];

            Assert.That(export.tooltip, Is.Not.Empty, "an unlabelled glyph says what it does on hover");
            Assert.That(delete.tooltip, Is.Not.Empty);
            Assert.That(AnalysisEpisodeList.IsRowAction(export), Is.True);
            Assert.That(AnalysisEpisodeList.IsRowAction(delete), Is.True);
            Assert.That(AnalysisEpisodeList.IsRowAction(label), Is.False);
            Assert.That(AnalysisEpisodeList.IsRowAction(row), Is.False);

            string selected = null;
            string exported = null;
            string deleted = null;
            list.EpisodeSelected += id => selected = id;
            list.ExportRequested += id => exported = id;
            list.DeleteRequested += id => deleted = id;

            list.ClickRow(row, export);
            list.ClickRow(row, delete);
            Assert.That(selected, Is.Null, "a click on a glyph is not a selection");

            list.ClickExport(row);
            list.ClickDelete(row);
            Assert.That(exported, Is.EqualTo("s_test-0001"), "the export glyph hands over that run");
            Assert.That(deleted, Is.EqualTo("s_test-0001"), "and the delete glyph asks for that one");

            list.ClickRow(row, label);
            Assert.That(selected, Is.EqualTo("s_test-0001"), "a click on the row itself selects that run");
        }

        [Test]
        public void TheRowCarriesBothGlyphsAndNoCheckbox()
        {
            MetricsStore.Instance.Add(Episode("s_test-0001", index: 1));
            MetricsStore.Instance.Add(Episode("s_test-0002", index: 2));
            AnalysisEpisodeList list = BoundList(out _);

            Assert.That(Rows(list).Count, Is.EqualTo(2));
            foreach (VisualElement row in Rows(list))
            {
                Assert.That(row.Query<Button>(className: "analysis-row-icon-export").ToList().Count,
                    Is.EqualTo(1), "every row carries its own export glyph");
                Assert.That(row.Query<Button>(className: "analysis-row-icon-delete").ToList().Count,
                    Is.EqualTo(1), "and its own delete glyph");
                Assert.That(row.Query<Toggle>().ToList(), Is.Empty,
                    "a row is a single selection, so it carries no checkbox");
            }

            // The glyphs live inside the row, not in a panel that describes one run.
            Assert.That(list.Query<VisualElement>(className: "analysis-row-actions").ToList().Count, Is.EqualTo(2));
        }

        // -- the replay cursor -------------------------------------------------

        [Test]
        public void TheCursorReadsTheEpisodeUpToTheInstantItStandsOn()
        {
            var replay = new AnalysisEpisodeReplay();
            replay.Show(ReplayEpisode());

            Assert.That(replay.Time, Is.EqualTo(4.0).Within(1e-6),
                "an episode opens on its complete trajectory, which is what the tab always showed");
            Assert.That(replay.IsScrubbed, Is.False, "and a cursor resting on the end is not a prefix");

            replay.Seek(2.0);
            Assert.That(replay.IsScrubbed, Is.True);

            Assert.That(replay.Track("robot_1").Count, Is.EqualTo(3), "the robot's line stops at the cursor");
            Assert.That(replay.Track("human_1").Count, Is.EqualTo(1),
                "every agent's line stops there, not only the robot's");

            AnalysisPrefixMetrics metrics = replay.Metrics();
            Assert.That(metrics.HasRobot, Is.True);
            Assert.That(metrics.Seconds, Is.EqualTo(2.0).Within(1e-6));
            Assert.That(metrics.PathLengthMetres, Is.EqualTo(2.0).Within(1e-6), "the path is the prefix of the path");
            Assert.That(metrics.AverageSpeedMetresPerSecond, Is.EqualTo(1.0).Within(1e-6));
            Assert.That(metrics.MaxSpeedMetresPerSecond, Is.EqualTo(1.0).Within(1e-6));
            Assert.That(metrics.MinHumanDistanceMetres, Is.EqualTo(Math.Sqrt(4.16)).Within(1e-3),
                "the human is still far away at this instant");
            Assert.That(metrics.PersonalSpaceIntrusions, Is.Zero, "the personal space has not been entered yet");

            // The cut lands between two samples, not only on them.
            replay.Seek(1.5);
            Assert.That(replay.Track("robot_1").Count, Is.EqualTo(3));
            Assert.That(replay.Track("robot_1")[2].x, Is.EqualTo(1.5f).Within(1e-4f),
                "the line is drawn as far as the robot had got, not as far as it was last sampled");
        }

        [Test]
        public void ScrubbingTheDetailReadsThePrefixAndKeepsTheFinalValue()
        {
            MetricsStore.Instance.Add(ReplayEpisode());

            var host = new VisualElement();
            var detail = new AnalysisEpisodeDetail(host);
            var replay = new AnalysisEpisodeReplay();
            detail.SetReplay(replay);
            var session = NewSession();
            detail.Bind(session);
            replay.Show(session.Selected);

            Assert.That(MetricValue(host, "Path length"), Is.EqualTo("4.00 m"),
                "with the cursor at the end the panel reads the recorded values");
            Assert.That(host.Query<Label>(className: "analysis-metric-final").ToList(), Is.Empty,
                "there is nothing to compare while the whole run is on screen");

            replay.Seek(2.0);

            Assert.That(MetricValue(host, "Path length"), Is.EqualTo("2.00 m"));
            Assert.That(MetricFinal(host, "Path length"), Is.EqualTo("final 4.00 m"),
                "the value the run ended on stays beside the prefix");
            Assert.That(MetricValue(host, "World duration"), Is.EqualTo("2.00 s"));
            Assert.That(MetricFinal(host, "World duration"), Is.EqualTo("final 4.00 s"));
            Assert.That(MetricValue(host, "Personal space intrusions"), Is.EqualTo("0"));
            Assert.That(MetricFinal(host, "Personal space intrusions"), Is.EqualTo("final 1"));
            Assert.That(MetricValue(host, "Closest human"), Is.EqualTo("2.04 m"));
            Assert.That(MetricFinal(host, "Closest human"), Is.EqualTo("final 0.40 m"),
                "the closest approach is still ahead of the cursor");
            Assert.That(MetricValue(host, "Episode id"), Is.EqualTo("s_test-0001"),
                "the identity of a run is not something a prefix shortens");
            Assert.That(MetricFinal(host, "Episode id"), Is.Null);
        }

        [Test]
        public void PlaybackAdvancesInRealSecondsAndStopsAtTheEnd()
        {
            var replay = new AnalysisEpisodeReplay();
            replay.Show(ReplayEpisode());
            Assert.That(replay.AtEnd, Is.True);

            replay.Play();
            Assert.That(replay.IsPlaying, Is.True);
            Assert.That(replay.Time, Is.Zero, "playing from the end replays the episode from its beginning");

            replay.Advance(1.5);
            Assert.That(replay.Time, Is.EqualTo(1.5).Within(1e-6),
                "a second of playback is a world second of the episode, whatever the time scale says");
            Assert.That(replay.IsPlaying, Is.True);

            replay.Advance(10.0);
            Assert.That(replay.Time, Is.EqualTo(4.0).Within(1e-6), "the cursor stops at the end of the episode");
            Assert.That(replay.IsPlaying, Is.False, "and the transport is back to play");

            replay.Rewind();
            Assert.That(replay.AtStart, Is.True);
            for (int step = 0; step < 5; step++)
                replay.Step();
            Assert.That(replay.Time, Is.EqualTo(4.0).Within(1e-6), "a step never walks past the end");

            // The step has a left twin: one second back, and it never walks before the start either.
            replay.Seek(2.0);
            replay.StepBack();
            Assert.That(replay.Time, Is.EqualTo(1.0).Within(1e-6), "a step back is a second of the episode");
            replay.Rewind();
            replay.StepBack();
            Assert.That(replay.AtStart, Is.True, "a step back stops at the beginning");
        }

        [Test]
        public void TheFriseSpansTheEpisodeAndReportsWhatTheReaderAsksFor()
        {
            var timeline = new AnalysisTimeline();
            var replay = new AnalysisEpisodeReplay();
            timeline.Bind(replay);
            replay.Show(ReplayEpisode());

            Slider slider = timeline.Q<Slider>(className: "analysis-timeline-slider");
            Assert.That(slider, Is.Not.Null, "the detail view carries a frise");
            Assert.That(slider.lowValue, Is.EqualTo(0f));
            Assert.That(slider.highValue, Is.EqualTo(4f).Within(1e-4f),
                "the frise runs from the beginning to the end of the episode");
            Assert.That(slider.value, Is.EqualTo(4f).Within(1e-4f));
            Assert.That(timeline.Query<Label>(className: "analysis-timeline-clock").ToList()[0].text,
                Is.EqualTo("00:04 / 00:04"), "the instant reads in clear next to the duration");

            double sought = -1.0;
            timeline.SeekRequested += seconds => sought = seconds;
            timeline.ScrubTo(1.0);
            Assert.That(sought, Is.EqualTo(1.0).Within(1e-4), "moving the frise asks for that instant");

            // And the other way round: when the cursor moves, the frise follows it.
            replay.Seek(1.0);
            Assert.That(slider.value, Is.EqualTo(1.0f).Within(1e-4f),
                "the frise stands where the cursor stands, whoever moved it");

            List<string> transport = timeline.Query<Button>(className: "analysis-timeline-button")
                .ToList().Select(button => button.tooltip).ToList();
            Assert.That(transport.Count, Is.EqualTo(4),
                "back to the beginning, play, one second back and one second forward");
            Assert.That(transport, Has.None.Empty, "an unlabelled control says what it does on hover");
            Assert.That(timeline.Query<Button>(className: "analysis-timeline-step").ToList()[0].text,
                Is.EqualTo("+1 s"));
            Assert.That(timeline.Query<Button>(className: "analysis-timeline-step-back").ToList()[0].text,
                Is.EqualTo("-1 s"));

            Assert.That(AnalysisTimeline.Clock(0.0), Is.EqualTo("00:00"));
            Assert.That(AnalysisTimeline.Clock(8.4), Is.EqualTo("00:08"));
            Assert.That(AnalysisTimeline.Clock(125.0), Is.EqualTo("02:05"));
        }

        // -- the map key names the agents, and nothing else --------------------

        /// <summary>
        /// The map resolves its parts by name, so a test that wants the key only has to lay those names out.
        /// The map of a scenario is loaded through the scenario service, which is absent here: the key is
        /// filled from the episode's own tracks either way, which is what this pins.
        /// </summary>
        private static VisualElement MapHost()
        {
            var root = new VisualElement();
            root.Add(new VisualElement { name = "AnalysisMapCanvas" });
            root.Add(new Image { name = "AnalysisMapImage" });
            root.Add(new Label { name = "AnalysisMapPlaceholder" });
            root.Add(new Label { name = "AnalysisMapCaption" });
            root.Add(new Label { name = "AnalysisMapOutcome" });
            root.Add(new VisualElement { name = "AnalysisMapLegendList" });
            root.Add(new VisualElement { name = "AnalysisMapOverlayHost" });
            return root;
        }

        [Test]
        public void TheKeyNamesEachAgentAndCarriesNoPointCount()
        {
            VisualElement root = MapHost();
            var map = new AnalysisEpisodeMap(root);

            map.Show(Episode("s_test-0001"));

            List<string> names = root.Query<Label>(className: "analysis-legend-name").ToList()
                .Select(label => label.text).ToList();
            Assert.That(names, Is.EqualTo(new[] { "Robot", "Human 1", "Human 2" }),
                "the key names the agents the palette drew, in the palette's order");
            Assert.That(names, Has.None.Contains("pts"),
                "how many samples a run kept is not part of who the line belongs to");
            Assert.That(names, Has.None.Contains("-"),
                "a name is a name, not a name and a number glued together");

            map.Dispose();
        }

        // -- the confirmation dialog closes itself -----------------------------

        [Test]
        public void ConfirmingClosesTheDialogOnItsOwn()
        {
            var host = new VisualElement();
            var dialog = new ConfirmationDialog("Delete this episode?", "This cannot be undone.", "Delete episode");
            dialog.Show(host);
            Assert.That(dialog.parent, Is.EqualTo(host));

            dialog.Accept();
            Assert.That(dialog.parent, Is.Null, "the popup closes itself once the reader has confirmed");

            int confirmations = 0;
            int cancellations = 0;
            var second = new ConfirmationDialog("Clear this session?", "This cannot be undone.", "Clear session");
            second.Confirmed += () => confirmations++;
            second.Cancelled += () => cancellations++;
            second.Show(host);
            second.Dismiss();

            Assert.That(confirmations, Is.Zero);
            Assert.That(cancellations, Is.EqualTo(1), "closing without acting is not a confirmation");
            Assert.That(second.parent, Is.Null);
        }

        [Test]
        public void AFinishedExportClosesTheDialogAndAFailedOneStays()
        {
            (string status, bool close) =
                AnalysisTabController.ExportOutcome(new MetricsExportReport("/tmp/out", null, null, null), null);
            Assert.That(close, Is.True, "a finished export closes the popup that asked for it");
            Assert.That(status, Is.EqualTo("Exported to /tmp/out"));

            (status, close) = AnalysisTabController.ExportOutcome(default, "disk is full");
            Assert.That(close, Is.False, "a failed export is the one outcome the reader has to act on");
            Assert.That(status, Is.EqualTo("Export failed: disk is full"));
        }

    }
}
