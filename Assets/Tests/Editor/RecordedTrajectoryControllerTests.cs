using System.Collections.Generic;
using NUnit.Framework;
using RobotSNAP.Agents;
using RobotSNAP.Agents.Movement.Controllers;
using RobotSNAP.Agents.Movement.Interfaces;
using UnityEngine;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// The arithmetic of walking a human along a recorded track: it aims at where the recording put the body a
    /// moment later, turns the gap into a velocity, and never exceeds the configured speed. The rules are pure
    /// functions of the samples and the instant, so what a replayed crowd does can be decided without entering
    /// Play mode and without a scene.
    /// </summary>
    public sealed class RecordedTrajectoryControllerTests
    {
        private const float Tolerance = 0.0001f;

        /// <summary>
        /// A body walking one metre per second along +X, from <c>(0, 0)</c> at the first sample: sample
        /// <c>t</c> stands at <c>x = t - FirstSample</c>. The first sample is one world-second in, so the
        /// instants before it are a real "the track has not started" case and not an artefact of a zero origin.
        /// </summary>
        private const double FirstSample = 1.0;
        private const int LastSample = 5;

        private static List<double[]> Walker()
        {
            var samples = new List<double[]>();
            for (int second = (int)FirstSample; second <= LastSample; second++)
                samples.Add(new[] { (double)second, second - FirstSample, 0.0 });

            return samples;
        }

        private HumanConfig _config;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<HumanConfig>();
            _config.maxSpeed = 2f;
        }

        [TearDown]
        public void TearDown()
        {
            if (_config != null)
                Object.DestroyImmediate(_config);

            _config = null;
        }

        /// <summary>Builds a controller whose clock answers a fixed instant, which pins the rule without a session.</summary>
        private RecordedTrajectoryController ControllerAt(double seconds, IReadOnlyList<double[]> track = null)
        {
            var controller = new RecordedTrajectoryController(_config, () => seconds);
            controller.SetTrack(track ?? Walker());
            return controller;
        }

        private Vector2 Compute(RecordedTrajectoryController controller, Vector2 position) => controller.ComputeVelocity(
            position,
            Vector2.zero,
            Vector2.zero,
            null,
            null,
            null,
            RobotObservation.None,
            0.02f);

        [Test]
        public void ABodyOnTheRecording_WalksAtTheRecordedSpeed()
        {
            // At three world-seconds the recording put the body at x = 2, walking +X at 1 m/s.
            Vector2 velocity = Compute(ControllerAt(3.0), new Vector2(2f, 0f));

            Assert.That(velocity.x, Is.EqualTo(1f).Within(Tolerance), "the recorded speed is one metre per second");
            Assert.That(velocity.y, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void ABodyBehindTheRecording_WalksTowardsIt()
        {
            Vector2 velocity = Compute(ControllerAt(3.0), new Vector2(1f, 0f));

            Assert.That(velocity.x, Is.GreaterThan(0f), "the body walks in the direction of the recording");
            Assert.That(velocity.y, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void CatchUpSpeed_IsClampedToTheConfiguredMaximum()
        {
            // Ten metres behind, the raw catch-up would be many metres per second; the limit is 2.
            Vector2 velocity = Compute(ControllerAt(3.0), new Vector2(-8f, 0f));

            Assert.That(velocity.magnitude, Is.EqualTo(_config.maxSpeed).Within(Tolerance));
        }

        [Test]
        public void BeforeTheFirstSample_TheHumanStandsStill()
        {
            Vector2 velocity = Compute(ControllerAt(FirstSample - 0.5), new Vector2(0f, 0f));

            Assert.That(velocity, Is.EqualTo(Vector2.zero),
                "a recording says nothing about the instants before it starts");
        }

        [Test]
        public void AfterTheLastSample_TheHumanStandsStill()
        {
            Vector2 velocity = Compute(ControllerAt(LastSample + 0.5), new Vector2(4f, 0f));

            Assert.That(velocity, Is.EqualTo(Vector2.zero),
                "a recording says nothing about the instants after it ends");
        }

        [Test]
        public void BetweenTwoSamples_TheHumanFollowsTheInterpolation()
        {
            // At 3.5 seconds the recording put the body at x = 2.5, so a body standing there walks at 1 m/s.
            Vector2 velocity = Compute(ControllerAt(3.5), new Vector2(2.5f, 0f));

            Assert.That(velocity.x, Is.EqualTo(1f).Within(Tolerance), "the aim interpolates between samples");
            Assert.That(velocity.y, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void ADiagonalTrack_AimsAtBothAxes()
        {
            var track = new List<double[]>
            {
                new[] { 1.0, 1.0, 1.0 },
                new[] { 3.0, 3.0, 3.0 }
            };

            Vector2 velocity = Compute(ControllerAt(2.0, track), new Vector2(2f, 2f));

            Assert.That(velocity.x, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(velocity.y, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void WithoutATrack_TheHumanStandsStill()
        {
            var controller = new RecordedTrajectoryController(_config, () => 3.0);

            Assert.That(Compute(controller, new Vector2(2f, 0f)), Is.EqualTo(Vector2.zero));
            Assert.That(controller.GetConfidence(), Is.EqualTo(0f));
        }

        [Test]
        public void HandingOverATrack_IsWhatMakesTheControllerWalk()
        {
            var controller = new RecordedTrajectoryController(_config, () => 3.0);
            Assert.That(controller.GetConfidence(), Is.EqualTo(0f));

            controller.SetTrack(Walker());

            Assert.That(controller.GetConfidence(), Is.EqualTo(1f));
            Assert.That(Compute(controller, new Vector2(2f, 0f)).x, Is.EqualTo(1f).Within(Tolerance));

            controller.SetTrack(null);
            Assert.That(Compute(controller, new Vector2(2f, 0f)), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void IsInsideTrack_IsTrueOnlyWithinTheRecordedSpan()
        {
            IReadOnlyList<double[]> track = Walker();

            Assert.That(RecordedTrajectoryController.IsInsideTrack(track, FirstSample - 0.01), Is.False);
            Assert.That(RecordedTrajectoryController.IsInsideTrack(track, FirstSample), Is.True);
            Assert.That(RecordedTrajectoryController.IsInsideTrack(track, 3.0), Is.True);
            Assert.That(RecordedTrajectoryController.IsInsideTrack(track, LastSample), Is.True);
            Assert.That(RecordedTrajectoryController.IsInsideTrack(track, LastSample + 0.01), Is.False);
            Assert.That(RecordedTrajectoryController.IsInsideTrack(null, 3.0), Is.False);
        }

        [Test]
        public void TheParsedValue_ReachesTheControllerThroughTheScenarioSpelling()
        {
            // The scenario writes "replay"; the parser has to read it back as the replay controller.
            Assert.That(HumanMovementControllerParser.TryParse("replay", out MovementControllerType parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Replay));
            Assert.That(HumanMovementControllerParser.TryParse("recorded", out parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Replay));
            Assert.That(HumanMovementControllerParser.TryParse("playback", out parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Replay));
            Assert.That(HumanMovementControllerParser.ToYamlValue(MovementControllerType.Replay), Is.EqualTo("Replay"));
            Assert.That(
                HumanMovementControllerParser.TryParse(
                    HumanMovementControllerParser.ToDisplayName(MovementControllerType.Replay),
                    out parsed),
                Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Replay));
        }
    }
}
