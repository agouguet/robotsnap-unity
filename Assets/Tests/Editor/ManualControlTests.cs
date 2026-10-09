using NUnit.Framework;
using RobotSNAP.Agents;
using UnityEngine;

namespace RobotSNAP.Tests.Editor
{
    /// <summary>
    /// The arithmetic of driving a pedestrian by hand, and the scenario spelling that puts one under the
    /// keyboard. The rule is pure, so what the keys do can be decided without entering Play mode.
    /// </summary>
    public sealed class ManualControlTests
    {
        private const float Tolerance = 0.0001f;

        [Test]
        public void TheBasisOfAViewFacingForwardWalksAlongZWithXToItsRight()
        {
            HumanManualInput.GroundBasis(Vector3.forward, out Vector2 forward, out Vector2 right);

            Assert.That(forward.x, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(forward.y, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(right.x, Is.EqualTo(1f).Within(Tolerance),
                "Facing +z, the right hand points along +x.");
            Assert.That(right.y, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void TheBasisIsFlattenedAndNormalised()
        {
            // A camera looking down and to the side still keeps its heading on the ground plane.
            HumanManualInput.GroundBasis(new Vector3(1f, -1f, 1f), out Vector2 forward, out Vector2 right);

            Assert.That(forward.magnitude, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(right.magnitude, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(Vector2.Dot(forward, right), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void ACameraLookingStraightDownStillHasUsableKeys()
        {
            HumanManualInput.GroundBasis(Vector3.down, out Vector2 forward, out Vector2 right);

            Assert.That(forward, Is.EqualTo(Vector2.up), "The fallback walks along +z.");
            Assert.That(right, Is.EqualTo(Vector2.right), "and keeps +x on its right.");
        }

        [Test]
        public void HoldingForward_WalksAtTheCruiseSpeedAlongTheView()
        {
            HumanManualInput.GroundBasis(Vector3.forward, out Vector2 forward, out Vector2 right);

            Vector2 velocity = HumanManualInput.Velocity(forward, right, 1f, 0f, 1.4f);

            Assert.That(velocity, Is.EqualTo(new Vector2(0f, 1.4f)));
        }

        [Test]
        public void ADiagonalIsNotFasterThanAStraightLine()
        {
            HumanManualInput.GroundBasis(Vector3.forward, out Vector2 forward, out Vector2 right);

            Vector2 velocity = HumanManualInput.Velocity(forward, right, 1f, 1f, 1.4f);

            Assert.That(velocity.magnitude, Is.EqualTo(1.4f).Within(Tolerance));
        }

        [Test]
        public void AReleasedKeyboard_StopsTheWalk()
        {
            HumanManualInput.GroundBasis(Vector3.forward, out Vector2 forward, out Vector2 right);

            Assert.That(HumanManualInput.Velocity(forward, right, 0f, 0f, 1.4f), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void BackingUpWalksBackwards()
        {
            HumanManualInput.GroundBasis(Vector3.forward, out Vector2 forward, out Vector2 right);

            Vector2 velocity = HumanManualInput.Velocity(forward, right, -1f, 0f, 2f);

            Assert.That(velocity, Is.EqualTo(new Vector2(0f, -2f)));
        }

        [Test]
        public void ANegativeSpeedNeverWalksBackwardsOnItsOwn()
        {
            HumanManualInput.GroundBasis(Vector3.forward, out Vector2 forward, out Vector2 right);

            Assert.That(HumanManualInput.Velocity(forward, right, 1f, 0f, -3f), Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void TheManualControllerIsReadFromTheScenario()
        {
            Assert.That(HumanMovementControllerParser.TryParse("manual", out MovementControllerType parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Manual));

            Assert.That(HumanMovementControllerParser.TryParse("keyboard", out parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Manual));

            Assert.That(
                HumanMovementControllerParser.TryParse(
                    HumanMovementControllerParser.ManualDisplayName, out parsed),
                Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Manual));
        }

        [Test]
        public void TheManualControllerRoundTripsThroughItsScenarioSpelling()
        {
            string yaml = HumanMovementControllerParser.ToYamlValue(MovementControllerType.Manual);
            Assert.That(HumanMovementControllerParser.TryParse(yaml, out MovementControllerType parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(MovementControllerType.Manual));
        }

        [Test]
        public void TheManualControllerIsSpelledForTheEditor()
        {
            Assert.That(
                HumanMovementControllerParser.ToDisplayName(MovementControllerType.Manual),
                Is.EqualTo(HumanMovementControllerParser.ManualDisplayName));
        }
    }
}
