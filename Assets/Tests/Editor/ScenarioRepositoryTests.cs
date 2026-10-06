using System.IO;
using System.Text;
using NUnit.Framework;
using RobotSNAP.Core.Scenario;
using UnityEngine;

namespace RobotSNAP.Tests.Editor
{
    public sealed class ScenarioRepositoryTests
    {
        /// <summary>
        /// The scenario every session starts from, so the one file this can be pointed at without pinning a
        /// test to a scenario an author is free to delete or rename.
        /// </summary>
        private static string DefaultScenarioPath =>
            Path.Combine(Application.streamingAssetsPath, "Scenarios", "default.yaml");

        [Test]
        public void Load_ParsesBundledScenarioAndCachesItsMetadata()
        {
            var repository = new ScenarioRepository();

            ScenarioData scenario = repository.Load("default", DefaultScenarioPath, cache: true);
            ScenarioInfo info = repository.GetInfo("default", DefaultScenarioPath);

            Assert.That(scenario, Is.Not.Null);
            // The display name is an author's to write, and the editor rewrites it whenever the scenario is
            // saved - it lower-cases and upper-cases at its own convenience. What this test is about is that
            // the name in the file is parsed and survives the cache, not the wording the author chose.
            Assert.That(scenario.Info.Name, Is.EqualTo("default").IgnoreCase);
            Assert.That(repository.CacheSize, Is.EqualTo(1));
            Assert.That(info, Is.SameAs(scenario.Info));
        }

        [Test]
        public void Clear_RemovesScenarioCache()
        {
            var repository = new ScenarioRepository();
            repository.Load("default", DefaultScenarioPath, cache: true);

            repository.Clear();

            Assert.That(repository.CacheSize, Is.Zero);
            Assert.That(repository.TryGetScenario("default", out _), Is.False);
        }

        [Test]
        public void Parse_LoadsOrderedRobotAndHumanGoals()
        {
            const string yaml = @"scenario_info:
  name: Route test
  map: basic/corner
points:
  robot_start: {x: 0, y: 0, z: 0}
  robot_mid: {x: 1, y: 0, z: 0}
  robot_goal: {x: 2, y: 0, z: 0}
  human_start: {x: 0, y: 0, z: 1}
  human_goal_1: {x: 1, y: 0, z: 1}
  human_goal_2: {x: 2, y: 0, z: 1}
robot:
  start: robot_start
  waypoints: [robot_mid]
  goal: robot_goal
humans:
  - id: independent_route
    count: 1
    spawn: {type: point, ref: human_start}
    goal: {type: point, ref: human_goal_1}
    goals:
      - {type: point, ref: human_goal_2}
";
            var repository = new ScenarioRepository();

            ScenarioData scenario = repository.Parse(Encoding.UTF8.GetBytes(yaml), "route-test");

            Assert.That(scenario.Robot.WaypointRefs, Is.EqualTo(new[] { "robot_mid" }));
            Assert.That(scenario.Humans[0].Goal.Reference, Is.EqualTo("human_goal_1"));
            Assert.That(scenario.Humans[0].Goals[0].Reference, Is.EqualTo("human_goal_2"));
        }
    }
}
