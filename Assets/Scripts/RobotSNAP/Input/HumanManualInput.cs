using UnityEngine;

namespace RobotSNAP
{
    /// <summary>
    /// The arithmetic of driving one pedestrian by hand: where the keys point, in the frame the view looks
    /// down, and how fast that walks.
    ///
    /// It is separated from the component that reads the keyboard for the same reason the trajectory reader
    /// is separated from the map that draws it: a rule is what a test can hold, and a rule that lives inside
    /// an <c>Update</c> can only be observed by entering Play mode. Nothing here reads <see cref="Input"/>,
    /// allocates, or touches a scene.
    /// </summary>
    public static class HumanManualInput
    {
        /// <summary>
        /// The ground-plane basis a view looking along <paramref name="forward"/> drives in: the direction the
        /// camera faces and the direction to its right, both flattened onto x/z and normalised.
        ///
        /// A camera looking straight down has no forward on the ground, and the fallback points the walk along
        /// +z with +x to its right, which keeps the keys meaningful instead of dead.
        /// </summary>
        public static void GroundBasis(Vector3 forward, out Vector2 forwardOnGround, out Vector2 rightOnGround)
        {
            Vector2 flat = new Vector2(forward.x, forward.z);
            if (flat.sqrMagnitude < 1e-6f)
            {
                forwardOnGround = Vector2.up;   // (0, 1) on x/z, which is +z
                rightOnGround = Vector2.right;  // (1, 0) on x/z, which is +x
                return;
            }

            forwardOnGround = flat.normalized;
            rightOnGround = new Vector2(forwardOnGround.y, -forwardOnGround.x);
        }

        /// <summary>
        /// World velocity, in metres per second, of a pedestrian whose forward key is pressed
        /// <paramref name="forwardAmount"/> and whose strafe key is pressed <paramref name="rightAmount"/>.
        ///
        /// A diagonal is normalised so it is not faster than a straight line, a released keyboard returns
        /// zero so the walk stops, and the speed is the caller's own cruise speed.
        /// </summary>
        public static Vector2 Velocity(
            Vector2 forwardOnGround,
            Vector2 rightOnGround,
            float forwardAmount,
            float rightAmount,
            float speed)
        {
            Vector2 direction = forwardOnGround * forwardAmount + rightOnGround * rightAmount;
            float magnitude = direction.magnitude;
            if (magnitude < 1e-4f)
                return Vector2.zero;

            return direction / magnitude * Mathf.Max(0f, speed);
        }
    }
}
