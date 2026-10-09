using System;
using System.IO;
using NUnit.Framework;
using RobotSNAP.Core.Scenario;

namespace RobotSNAP.Tests.Editor
{
    public sealed class ScenarioPathResolverTests
    {
        private string _originalOverride;

        [SetUp]
        public void SetUp()
        {
            // The override lives in the process environment, so every test that writes it has to hand the
            // previous value back: a leaked folder would follow the rest of the run.
            _originalOverride = System.Environment.GetEnvironmentVariable(
                ScenarioPathResolver.ScenariosDirectoryOverrideVariable);
        }

        [TearDown]
        public void TearDown()
        {
            System.Environment.SetEnvironmentVariable(
                ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                _originalOverride);
        }

        [Test]
        public void ResolvePaths_UsesDefaultsForEmptyFolders()
        {
            var paths = ScenarioPathResolver.ResolvePaths("/streaming", "", null);

            Assert.That(paths.ScenariosPath, Is.EqualTo(Path.Combine("/streaming", "Scenarios")));
            Assert.That(paths.MapsPath, Is.EqualTo(Path.Combine("/streaming", "Dataset")));
        }

        [Test]
        public void ResolvePaths_ReadsScenariosFromTheEnvironmentFolderWhenItExists()
        {
            string overrideDirectory = Path.Combine(Path.GetTempPath(), "robot_snap_scenarios_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(overrideDirectory);
            try
            {
                System.Environment.SetEnvironmentVariable(
                    ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                    overrideDirectory);

                var paths = ScenarioPathResolver.ResolvePaths("/streaming", "Scenarios", "Dataset");

                Assert.That(paths.ScenariosPath, Is.EqualTo(Path.GetFullPath(overrideDirectory)));
                // The maps folder is not part of the contract and keeps its StreamingAssets location.
                Assert.That(paths.MapsPath, Is.EqualTo(Path.Combine("/streaming", "Dataset")));
            }
            finally
            {
                Directory.Delete(overrideDirectory, true);
            }
        }

        [Test]
        public void ResolvePaths_IgnoresTheEnvironmentFolderWhenItDoesNotExist()
        {
            string missingDirectory = Path.Combine(Path.GetTempPath(), "robot_snap_missing_" + Guid.NewGuid().ToString("N"));
            System.Environment.SetEnvironmentVariable(
                ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                missingDirectory);

            var paths = ScenarioPathResolver.ResolvePaths("/streaming", "Scenarios", "Dataset");

            Assert.That(paths.ScenariosPath, Is.EqualTo(Path.Combine("/streaming", "Scenarios")));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("relative/scenarios")]
        public void ResolvePaths_IgnoresAnEnvironmentFolderThatNamesNothingUsable(string configured)
        {
            System.Environment.SetEnvironmentVariable(
                ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                configured);

            var paths = ScenarioPathResolver.ResolvePaths("/streaming", "", null);

            Assert.That(paths.ScenariosPath, Is.EqualTo(Path.Combine("/streaming", "Scenarios")));
            Assert.That(paths.MapsPath, Is.EqualTo(Path.Combine("/streaming", "Dataset")));
        }

        [Test]
        public void TryResolveScenariosOverride_ReportsWhetherTheFolderCanBeUsed()
        {
            string overrideDirectory = Path.Combine(Path.GetTempPath(), "robot_snap_scenarios_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(overrideDirectory);
            try
            {
                System.Environment.SetEnvironmentVariable(
                    ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                    overrideDirectory);
                Assert.That(ScenarioPathResolver.TryResolveScenariosOverride(out string resolved), Is.True);
                Assert.That(resolved, Is.EqualTo(Path.GetFullPath(overrideDirectory)));

                System.Environment.SetEnvironmentVariable(
                    ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                    null);
                Assert.That(ScenarioPathResolver.TryResolveScenariosOverride(out string unset), Is.False);
                Assert.That(unset, Is.Null);
            }
            finally
            {
                Directory.Delete(overrideDirectory, true);
            }
        }

        [Test]
        public void FindScenarioFile_UsesExtensionsInProvidedOrder()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string yamlPath = Path.Combine(directory, "example.yaml");
                string ymlPath = Path.Combine(directory, "example.yml");
                File.WriteAllText(yamlPath, "yaml");
                File.WriteAllText(ymlPath, "yml");

                string path = ScenarioPathResolver.FindScenarioFile(directory, "example", new[] { ".yml", ".yaml" });

                Assert.That(path, Is.EqualTo(ymlPath));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
