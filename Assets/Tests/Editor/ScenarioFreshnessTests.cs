using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using RobotSNAP;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using UnityEngine;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// A scenario rewritten between two runs has to be the one that is played. The Editor enters Play without
    /// reloading the domain or the scene, so every cache filled by one session is still there for the next:
    /// these tests rewrite a YAML file and assert that the reader on the other side hands back the new text
    /// rather than the revision it read the first time.
    ///
    /// An edit-mode test runs in an empty scene and cannot run the lifecycle the game runs - a component
    /// added here does not call its own Awake or Start, except for the <c>[ExecuteAlways]</c> singletons - so
    /// the loader is wired by hand to the folder the test owns.
    /// </summary>
    public sealed class ScenarioFreshnessTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        private string _scenariosDirectory;
        private string _originalOverride;
        private SimulationConfig _ownedConfig;

        [SetUp]
        public void SetUp()
        {
            _originalOverride = System.Environment.GetEnvironmentVariable(
                ScenarioPathResolver.ScenariosDirectoryOverrideVariable);
            _scenariosDirectory = Path.Combine(
                Path.GetTempPath(), "robot_snap_freshness_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scenariosDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject created in _created)
            {
                if (created != null)
                    UnityEngine.Object.DestroyImmediate(created);
            }
            _created.Clear();

            if (_ownedConfig != null)
            {
                UnityEngine.Object.DestroyImmediate(_ownedConfig);
                _ownedConfig = null;
            }

            // The override lives in the process environment: a leaked folder would follow the rest of the run.
            System.Environment.SetEnvironmentVariable(
                ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                _originalOverride);

            if (Directory.Exists(_scenariosDirectory))
                Directory.Delete(_scenariosDirectory, true);
        }

        [Test]
        public void LoadScenario_ReturnsTheRewrittenYaml()
        {
            string scenarioPath = Path.Combine(_scenariosDirectory, "freshness.yaml");
            File.WriteAllText(scenarioPath, ScenarioYaml("Before edit"));
            ScenarioLoader loader = CreateLoader();

            Assert.That(loader.LoadScenario("freshness").Name, Is.EqualTo("Before edit"));

            Rewrite(scenarioPath, ScenarioYaml("After edit"));

            Assert.That(loader.LoadScenario("freshness").Name, Is.EqualTo("After edit"));
        }

        [Test]
        public void GetScenarioInfo_ReturnsTheRewrittenMetadata()
        {
            string scenarioPath = Path.Combine(_scenariosDirectory, "freshness.yaml");
            File.WriteAllText(scenarioPath, ScenarioYaml("Before edit"));
            ScenarioLoader loader = CreateLoader();

            Assert.That(loader.GetScenarioInfo("freshness").Name, Is.EqualTo("Before edit"));

            Rewrite(scenarioPath, ScenarioYaml("After edit"));

            Assert.That(loader.GetScenarioInfo("freshness").Name, Is.EqualTo("After edit"));
        }

        [Test]
        public void ScenarioRepository_DropsItsEntryWhenTheFileIsRewritten()
        {
            string scenarioPath = Path.Combine(_scenariosDirectory, "freshness.yaml");
            File.WriteAllText(scenarioPath, ScenarioYaml("Before edit"));
            var repository = new ScenarioRepository();
            repository.Load("freshness", scenarioPath, cache: true);

            Assert.That(repository.TryGetScenario("freshness", scenarioPath, out _), Is.True);

            Rewrite(scenarioPath, ScenarioYaml("After edit"));

            Assert.That(repository.TryGetScenario("freshness", scenarioPath, out _), Is.False);
            Assert.That(repository.GetInfo("freshness", scenarioPath).Name, Is.EqualTo("After edit"));
        }

        [Test]
        public void ReloadAllScenarioInfos_DescribesTheRewrittenYaml()
        {
            string scenarioPath = Path.Combine(_scenariosDirectory, "freshness.yaml");
            File.WriteAllText(scenarioPath, ScenarioYaml("Before edit"));
            ScenarioDataService service = CreateScenarioDataService();

            service.ReloadAllScenarioInfos();
            Assert.That(service.AllScenarios["freshness"].Name, Is.EqualTo("Before edit"));

            Rewrite(scenarioPath, ScenarioYaml("After edit"));

            service.ReloadAllScenarioInfos();
            Assert.That(service.AllScenarios["freshness"].Name, Is.EqualTo("After edit"));
        }

        [Test]
        public void Start_ReadsTheYamlWrittenBetweenTwoSessions()
        {
            string scenarioPath = Path.Combine(_scenariosDirectory, "freshness.yaml");
            File.WriteAllText(scenarioPath, ScenarioYaml("Before edit"));
            ScenarioDataService service = CreateScenarioDataService();

            InvokePrivate(service, "Start");
            Assert.That(service.AllScenarios["freshness"].Name, Is.EqualTo("Before edit"));

            Rewrite(scenarioPath, ScenarioYaml("After edit"));

            // The next session: the Editor enters Play here without reloading the scene, so this is the same
            // instance, carrying the loaded flag the previous session left behind.
            InvokePrivate(service, "Start");
            Assert.That(service.AllScenarios["freshness"].Name, Is.EqualTo("After edit"));
        }

        [Test]
        public void RefreshPathsFromConfig_ReadsScenariosFromTheEnvironmentFolder()
        {
            System.Environment.SetEnvironmentVariable(
                ScenarioPathResolver.ScenariosDirectoryOverrideVariable,
                _scenariosDirectory);

            var loaderHost = NewObject("ScenarioFreshnessLoader");
            var loader = loaderHost.AddComponent<ScenarioLoader>();

            // A Clock first, so the Supervisor wires the one that is already there instead of creating its own.
            NewObject("ScenarioFreshnessClock").AddComponent<Clock>();

            var supervisorHost = NewObject("ScenarioFreshnessSupervisor");
            var supervisor = supervisorHost.AddComponent<Supervisor>();

            // A Supervisor added without a configuration makes one of its own; this test hands it its own and
            // disposes the one that was made, so no asset instance is left behind.
            SimulationConfig supervisorOwnedConfig = supervisor.ActiveConfig;
            _ownedConfig = ScriptableObject.CreateInstance<SimulationConfig>();
            SetPrivateField(supervisor, "_defaultConfig", _ownedConfig);
            if (supervisorOwnedConfig != null)
                UnityEngine.Object.DestroyImmediate(supervisorOwnedConfig);

            loader.RefreshPathsFromConfig();

            Assert.That(loader.ScenariosPath, Is.EqualTo(Path.GetFullPath(_scenariosDirectory)));
            // Maps are not part of the contract and keep their StreamingAssets location.
            Assert.That(loader.MapsPath, Is.EqualTo(Path.Combine(Application.streamingAssetsPath, "Dataset")));
        }

        /// <summary>A loader wired to the temporary folder, so the file the test rewrites is the one it reads.</summary>
        private ScenarioLoader CreateLoader()
        {
            var loader = NewObject("ScenarioFreshnessLoader").AddComponent<ScenarioLoader>();
            SetPrivateField(loader, "_scenariosPath", _scenariosDirectory);
            SetPrivateField(loader, "_isInitialized", true);
            return loader;
        }

        private ScenarioDataService CreateScenarioDataService()
        {
            ScenarioLoader loader = CreateLoader();
            var manager = loader.gameObject.AddComponent<ScenarioManager>();
            SetPrivateField(manager, "_scenarioLoader", loader);
            var service = loader.gameObject.AddComponent<ScenarioDataService>();
            SetPrivateField(service, "_scenarioManager", manager);
            return service;
        }

        private GameObject NewObject(string name)
        {
            var created = new GameObject(name);
            _created.Add(created);
            return created;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"{target.GetType().Name} has no field {fieldName}");
            field.SetValue(target, value);
        }

        /// <summary>
        /// Replays the lifecycle callback the Editor runs when a session starts - the scene is not reloaded,
        /// so the instance is the same one and only its callbacks come back.
        /// </summary>
        private static void InvokePrivate(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"{target.GetType().Name} has no method {methodName}");
            method.Invoke(target, null);
        }

        /// <summary>
        /// Writes the new text and stamps the file in the future, so the rewrite is visible whatever the
        /// resolution of the filesystem clock is.
        /// </summary>
        private static void Rewrite(string path, string yaml)
        {
            File.WriteAllText(path, yaml);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        }

        private static string ScenarioYaml(string name) => $@"scenario_info:
  name: {name}
  type: Custom
  version: 1.0
points:
  robot_start: {{x: 0, y: 0, z: 0}}
  robot_goal: {{x: 2, y: 0, z: 0}}
robot:
  start: robot_start
  goal: robot_goal
";
    }
}
