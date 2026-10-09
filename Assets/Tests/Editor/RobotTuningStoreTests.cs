using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RobotSNAP.Agents;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// The round trip of a saved tuning and the figures it turns a type into.
    ///
    /// The store is exercised against a folder of its own rather than the one the application's user owns -
    /// which is what <see cref="RobotTuningStore.DirectoryOverride"/> is for - and every call below goes
    /// through the real methods: a test that re-implemented the path it checks would prove nothing about the
    /// one the application takes.
    /// </summary>
    public sealed class RobotTuningStoreTests
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "robotsnap-tuning-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            RobotTuningStore.DirectoryOverride = _folder;
        }

        [TearDown]
        public void TearDown()
        {
            RobotTuningStore.DirectoryOverride = null;

            if (Directory.Exists(_folder))
                Directory.Delete(_folder, true);
        }

        // -- the round trip --------------------------------------------------------------------

        [Test]
        public void Save_ThenLoad_RoundTripsEveryFigure()
        {
            var tuning = new RobotTuning
            {
                MaxLinearSpeed = 1.75f,
                MaxAngularSpeed = 3.5f,
                LidarSpanDegrees = 270f,
                LidarRange = 12.5f,
                LidarRays = 241,
                LidarHeight = 0.42f,
                LidarFrequencyHz = 17f,
                DetectionRadius = 8.5f
            };

            RobotTuningStore.Save("jackal", tuning);

            Assert.That(RobotTuningStore.TryLoad("jackal", out RobotTuning loaded), Is.True);
            Assert.That(loaded.MaxLinearSpeed, Is.EqualTo(1.75f).Within(1e-4f));
            Assert.That(loaded.MaxAngularSpeed, Is.EqualTo(3.5f).Within(1e-4f));
            Assert.That(loaded.LidarSpanDegrees, Is.EqualTo(270f).Within(1e-4f));
            Assert.That(loaded.LidarRange, Is.EqualTo(12.5f).Within(1e-4f));
            Assert.That(loaded.LidarRays, Is.EqualTo(241));
            Assert.That(loaded.LidarHeight, Is.EqualTo(0.42f).Within(1e-4f));
            Assert.That(loaded.LidarFrequencyHz, Is.EqualTo(17f).Within(1e-4f));
            Assert.That(loaded.DetectionRadius, Is.EqualTo(8.5f).Within(1e-4f));
        }

        [Test]
        public void Save_ReplacesTheTuningAlreadySavedForAType()
        {
            RobotTuningStore.Save("kuri", new RobotTuning { MaxLinearSpeed = 1f });
            RobotTuningStore.Save("kuri", new RobotTuning { MaxLinearSpeed = 2f });

            Assert.That(RobotTuningStore.TryLoad("kuri", out RobotTuning loaded), Is.True);
            Assert.That(loaded.MaxLinearSpeed, Is.EqualTo(2f).Within(1e-4f));
        }

        // -- what a missing or broken file reads as --------------------------------------------

        [Test]
        public void TryLoad_IsFalseForATypeNothingWasSavedFor()
        {
            Assert.That(RobotTuningStore.TryLoad("ginger", out RobotTuning loaded), Is.False);
            Assert.That(loaded, Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void TryLoad_IsFalseForAnEmptyTypeId(string typeId)
        {
            Assert.That(RobotTuningStore.TryLoad(typeId, out _), Is.False);
        }

        [Test]
        public void TryLoad_IsFalseForAFileThatDoesNotParse()
        {
            File.WriteAllText(RobotTuningStore.GetPath("bibus"), "{ this is not json");

            Assert.That(RobotTuningStore.TryLoad("bibus", out RobotTuning loaded), Is.False);
            Assert.That(loaded, Is.Null);
        }

        [Test]
        public void Delete_RemovesTheStoredTuningAndReportsWhetherItWasThere()
        {
            RobotTuningStore.Save("jackal", new RobotTuning());

            Assert.That(RobotTuningStore.Delete("jackal"), Is.True);
            Assert.That(RobotTuningStore.Has("jackal"), Is.False);
            Assert.That(RobotTuningStore.Delete("jackal"), Is.False);
        }

        // -- where the file lands --------------------------------------------------------------

        [Test]
        public void GetPath_LivesUnderTheOverrideDirectory()
        {
            string path = RobotTuningStore.GetPath("freight");

            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(_folder));
            Assert.That(Path.GetFileName(path), Is.EqualTo("freight.json"));
        }

        [Test]
        public void GetPath_DropsAnythingAPathCouldBeBuiltFrom()
        {
            string path = RobotTuningStore.GetPath("a/b");

            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(_folder));
            Assert.That(Path.GetFileName(path), Is.EqualTo("ab.json"));
        }

        [Test]
        public void GetPath_RefusesATypeIdWithNothingUsableInIt()
        {
            Assert.Throws<ArgumentException>(() => RobotTuningStore.GetPath("  "));
            Assert.Throws<ArgumentException>(() => RobotTuningStore.GetPath(".."));
        }

        [Test]
        public void Save_WritesTheDocumentedKeys()
        {
            RobotTuningStore.Save("jackal", new RobotTuning { MaxLinearSpeed = 1.5f });

            JObject saved = JObject.Parse(File.ReadAllText(RobotTuningStore.GetPath("jackal")));
            Assert.That(saved["max_linear_speed_mps"], Is.Not.Null);
            Assert.That(saved["max_angular_speed_radps"], Is.Not.Null);
            Assert.That(saved["lidar_span_deg"], Is.Not.Null);
            Assert.That(saved["lidar_range_m"], Is.Not.Null);
            Assert.That(saved["lidar_rays"], Is.Not.Null);
            Assert.That(saved["lidar_height_m"], Is.Not.Null);
            Assert.That(saved["lidar_frequency_hz"], Is.Not.Null);
            Assert.That(saved["detection_radius_m"], Is.Not.Null);
        }

        // -- what a tuning turns a type into ---------------------------------------------------

        [Test]
        public void Default_ReproducesTheBuiltInFiguresOfAType()
        {
            RobotProfile profile = RobotProfiles.Find("jackal");

            RobotTuning tuning = RobotTuning.Default(profile, detectionRadius: 6f);

            Assert.That(tuning.MaxLinearSpeed, Is.EqualTo(profile.MaxLinearSpeed).Within(1e-4f));
            Assert.That(tuning.MaxAngularSpeed, Is.EqualTo(profile.MaxAngularSpeed).Within(1e-4f));
            Assert.That(tuning.LidarSpanDegrees, Is.EqualTo(profile.LidarSpanDegrees).Within(1e-4f));
            Assert.That(tuning.LidarRange, Is.EqualTo(profile.LidarRange).Within(1e-4f));
            Assert.That(tuning.LidarRays, Is.EqualTo(profile.LidarRays));
            Assert.That(tuning.LidarHeight, Is.EqualTo(profile.LidarHeight).Within(1e-4f));
            Assert.That(tuning.LidarFrequencyHz, Is.EqualTo(profile.LidarFrequencyHz).Within(1e-4f));
            Assert.That(tuning.DetectionRadius, Is.EqualTo(6f).Within(1e-4f));
        }

        [Test]
        public void ApplyTo_KeepsTheTypeAndOverridesTheFigures()
        {
            RobotProfile builtIn = RobotProfiles.Find("jackal");
            var tuning = new RobotTuning
            {
                MaxLinearSpeed = 3.25f,
                MaxAngularSpeed = 1.5f,
                LidarSpanDegrees = 180f,
                LidarRange = 20f,
                LidarRays = 64,
                LidarHeight = 1.1f,
                LidarFrequencyHz = 30f
            };

            RobotProfile tuned = tuning.ApplyTo(builtIn);

            Assert.That(tuned.Id, Is.EqualTo(builtIn.Id));
            Assert.That(tuned.DisplayName, Is.EqualTo(builtIn.DisplayName));
            Assert.That(tuned.Radius, Is.EqualTo(builtIn.Radius).Within(1e-4f));
            Assert.That(tuned.Mass, Is.EqualTo(builtIn.Mass).Within(1e-4f));
            Assert.That(tuned.MaxLinearSpeed, Is.EqualTo(3.25f).Within(1e-4f));
            Assert.That(tuned.MaxAngularSpeed, Is.EqualTo(1.5f).Within(1e-4f));
            Assert.That(tuned.LidarSpanDegrees, Is.EqualTo(180f).Within(1e-4f));
            Assert.That(tuned.LidarRange, Is.EqualTo(20f).Within(1e-4f));
            Assert.That(tuned.LidarRays, Is.EqualTo(64));
            Assert.That(tuned.LidarHeight, Is.EqualTo(1.1f).Within(1e-4f));
            Assert.That(tuned.LidarFrequencyHz, Is.EqualTo(30f).Within(1e-4f));
        }

        [Test]
        public void ApplyTo_ReadsANullProfileAsNull()
        {
            Assert.That(new RobotTuning().ApplyTo(null), Is.Null);
        }
    }

    /// <summary>
    /// The list the Robots tab shows: the built-in profiles, then the entries of the project catalogue the
    /// profiles do not already know. The merge is the one thing about the module that reads without Unity, so
    /// it is the one thing tested here - the list, its selection and its preview need a live panel.
    /// </summary>
    public sealed class RobotTypeCatalogueTests
    {
        [Test]
        public void KeepsTheProfileOrderAndAppendsWhatTheCatalogueAdds()
        {
            var profiles = new List<RobotProfile> { RobotProfiles.Find("jackal"), RobotProfiles.Find("freight") };
            var entries = new List<RobotCatalog.Entry>
            {
                Entry("jackal"),
                Entry("husky"),
                Entry("Jack-al")
            };

            List<RobotsTabController.RobotTypeEntry> types = RobotsTabController.BuildTypeList(profiles, entries);

            Assert.That(types.Count, Is.EqualTo(3));
            Assert.That(types[0].Id, Is.EqualTo("jackal"));
            Assert.That(types[1].Id, Is.EqualTo("freight"));
            Assert.That(types[2].Id, Is.EqualTo("husky"));
        }

        [Test]
        public void AProfileNamesTheTypeAndTheCatalogueOnlyIdNamesTheOther()
        {
            var profiles = new List<RobotProfile> { RobotProfiles.Find("kuri") };
            var entries = new List<RobotCatalog.Entry> { Entry("turtlebot4") };

            List<RobotsTabController.RobotTypeEntry> types = RobotsTabController.BuildTypeList(profiles, entries);

            Assert.That(types[0].HasProfile, Is.True);
            Assert.That(types[0].DisplayName, Is.EqualTo("Kuri"));

            Assert.That(types[1].HasProfile, Is.False);
            Assert.That(types[1].Profile, Is.Null);
            Assert.That(types[1].DisplayName, Is.EqualTo("turtlebot4"));
        }

        [Test]
        public void AMissingSourceContributesNothing()
        {
            Assert.That(RobotsTabController.BuildTypeList(null, null), Is.Empty);
        }

        [Test]
        public void EveryBuiltInTypeIsNamedOnceHoweverTheCatalogueSpellsIt()
        {
            List<RobotsTabController.RobotTypeEntry> types = RobotsTabController.BuildTypeList(RobotProfiles.All, null);

            Assert.That(types.Count, Is.EqualTo(RobotProfiles.All.Count));
            foreach (RobotsTabController.RobotTypeEntry type in types)
                Assert.That(type.HasProfile, Is.True);
        }

        [Test]
        public void TheProjectCatalogueNeverHidesABuiltInType()
        {
            // Reads the asset the project ships, so it fails if the catalogue stops agreeing with the profiles.
            List<RobotsTabController.RobotTypeEntry> types = RobotsTabController.BuildTypeList();

            Assert.That(types.Count, Is.GreaterThanOrEqualTo(RobotProfiles.All.Count));
            foreach (RobotProfile profile in RobotProfiles.All)
                Assert.That(types.Exists(type => type.Id == profile.Id), Is.True, profile.Id);
        }

        private static RobotCatalog.Entry Entry(string typeId) => new RobotCatalog.Entry { TypeId = typeId };
    }
}
