using NUnit.Framework;
using RobotSNAP.Metrics;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// The sentences the Settings tab's Data card states about the saved analysis data, and the elements it
    /// wires. They are pulled out as small static functions on purpose: a count a reader acts on - what a
    /// clear is about to remove, and what it removed - has to be checkable without a dialog, a window or a
    /// folder on the machine.
    /// </summary>
    public sealed class SettingsDataTests
    {
        private const string SettingsTabPath = "Assets/UI/Tabs/Settings/SettingsTab.uxml";

        private static TrajectoryArchive.ArchiveSummary Summary(int sessions, int episodes, long bytes)
            => new TrajectoryArchive.ArchiveSummary(sessions, episodes, bytes);

        [Test]
        public void TheCacheSummaryReadsInCountsAndInBytes()
        {
            Assert.That(SettingsTabController.CacheSummary(Summary(1, 1, 0)),
                Is.EqualTo("1 session, 1 episode, 0.0 MiB on disk"));
            Assert.That(SettingsTabController.CacheSummary(Summary(3, 12, 2L * 1024 * 1024)),
                Is.EqualTo("3 sessions, 12 episodes, 2.0 MiB on disk"));
        }

        [Test]
        public void TheCacheOutcomeStatesWhatWentOrWhyNothingDid()
        {
            Assert.That(SettingsTabController.CacheOutcome(true, "/tmp/metrics", Summary(3, 12, 0)),
                Is.EqualTo("Deleted 3 sessions, 12 episodes, 0.0 MiB on disk under /tmp/metrics"));
            Assert.That(SettingsTabController.CacheOutcome(true, "/tmp/metrics", default),
                Is.EqualTo("No saved data under /tmp/metrics"));
            Assert.That(SettingsTabController.CacheOutcome(false, "/tmp/metrics", Summary(3, 12, 0)),
                Is.EqualTo("Clear failed under /tmp/metrics"));
        }

        [Test]
        public void TheDataSummaryNamesWhatIsSaved()
        {
            Assert.That(SettingsTabController.DataSummaryText(0, default), Is.EqualTo("Nothing saved."));
            Assert.That(SettingsTabController.DataSummaryText(2, Summary(2, 5, 0)),
                Is.EqualTo("2 sessions, 5 episodes, 0.0 MiB on disk"));
        }

        [Test]
        public void TheFoldersLabelJoinsTheRootsItIsGiven()
        {
            Assert.That(SettingsTabController.FoldersLabel(null), Is.EqualTo("(no folder)"));
            Assert.That(SettingsTabController.FoldersLabel(new string[0]), Is.EqualTo("(no folder)"));
            Assert.That(SettingsTabController.FoldersLabel(new[] { "/tmp/a" }), Is.EqualTo("/tmp/a"));
            Assert.That(SettingsTabController.FoldersLabel(new[] { "/tmp/a", "/tmp/b" }),
                Is.EqualTo("/tmp/a and /tmp/b"));
        }

        [Test]
        public void TheDataRootsAreTheExportFolderAndTheFallback()
        {
            var roots = SettingsTabController.DataRoots();

            Assert.That(roots, Does.Contain(AnalysisExportFolder.Last));
            Assert.That(roots, Does.Contain(MetricsExporter.FallbackRoot));
            for (int first = 0; first < roots.Count; first++)
            {
                for (int second = first + 1; second < roots.Count; second++)
                    Assert.That(roots[first], Is.Not.EqualTo(roots[second]), "a root is offered once");
            }
        }

        /// <summary>The template carries the card the controller requires, so the tab cannot build with it missing.</summary>
        [Test]
        public void TheSettingsTemplateCarriesTheDataCard()
        {
            VisualTreeAsset template = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(SettingsTabPath);
            Assert.That(template, Is.Not.Null);

            VisualElement root = template.CloneTree();

            Assert.That(root.Q<VisualElement>("DataSection"), Is.Not.Null);
            Assert.That(root.Q<Label>("DataSummaryLabel"), Is.Not.Null);
            Assert.That(root.Q<Label>("DataStatusLabel"), Is.Not.Null);
            Assert.That(root.Q<Button>("ClearCacheButton"), Is.Not.Null);
        }
    }
}
