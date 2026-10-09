using System.Collections.Generic;
using UnityEngine;
using RobotSNAP.Agents;
using RobotSNAP.Agents.Movement.Interfaces;

namespace RobotSNAP.Agents.Movement.Controllers
{
    /// <summary>
    /// Controller of a human that walks an episode again instead of deciding its movement.
    ///
    /// The crowd of a recorded run is the one thing a benchmark wants to hold fixed: replaying an episode
    /// with the social force model would let the people choose a second set of paths, and a robot solution
    /// would then be measured against a different situation than the one it was tuned on. This controller
    /// borrows the movement of the recording - the <c>[t, x, z]</c> samples the metrics layer kept - and
    /// steers the agent towards where it stood at the replay instant, so the crowd behaves exactly as it did.
    ///
    /// It is the same shape as <see cref="ExternalControlController"/>: it owns no goal and no local
    /// behaviour, it only returns the velocity that walks the body towards a point somebody else chose, and
    /// the physics of the agent still resolves its collisions. What differs is the source of that point: an
    /// external driver refreshes it a frame at a time, and this one reads it back out of a track.
    ///
    /// The instant is the episode's own world time and every replayed human reads the same one, so the crowd
    /// moves together - see <see cref="EpisodeReplayDirector.CurrentSeconds"/>, which is the clock this
    /// controller reads unless a test hands it another one.
    /// </summary>
    public sealed class RecordedTrajectoryController : IMovementController
    {
        /// <summary>
        /// How far ahead on the track the agent aims, in seconds. Aiming at the point it stood on a moment
        /// later turns the recording from a set of teleport targets into a direction: the gap to that point,
        /// divided by this lead, is the velocity the recorded body was walking with. A lead too short makes
        /// the agent chase the samples, one too long cuts the corners of the route.
        /// </summary>
        public const double LookAheadSeconds = 0.35;

        private HumanConfig _config;
        private IReadOnlyList<double[]> _track;
        private System.Func<double> _clock;

        public RecordedTrajectoryController(HumanConfig config = null, System.Func<double> clock = null)
        {
            _config = config;
            _clock = clock ?? DefaultClock;
        }

        /// <summary>
        /// The instant of the replay, in the episode's world seconds. The director owns the one clock and
        /// publishes it here, so a controller that was not handed its own time source - which is every one of
        /// them in the running session - walks the same instant as the rest of the crowd.
        /// </summary>
        private static double DefaultClock() => EpisodeReplayDirector.CurrentSeconds;

        /// <summary>
        /// The recorded walk this human repeats, keyed the way the episode filed it, or null while it has
        /// none. Handing over a new track is not a restart: the next step simply aims along the new one.
        /// </summary>
        public IReadOnlyList<double[]> Track => _track;

        /// <summary>Points the controller at another recording, or at none with a null.</summary>
        public void SetTrack(IReadOnlyList<double[]> samples) => _track = samples;

        public Vector2 ComputeVelocity(
            Vector2 currentPosition,
            Vector2 currentVelocity,
            Vector2 goalPosition,
            IReadOnlyList<Vector2> neighbors,
            IReadOnlyList<Vector2> neighborVelocities,
            IReadOnlyList<Vector2> staticObstacles,
            RobotObservation robot,
            float deltaTime,
            float cruiseSpeedOverride = 0f)
        {
            float maxSpeed = _config != null ? Mathf.Max(0.1f, _config.maxSpeed) : 1.4f;
            double seconds = _clock != null ? _clock() : 0.0;
            return DesiredVelocity(_track, currentPosition, seconds, maxSpeed);
        }

        /// <summary>Keeps the recording: there is no per-step state to drop.</summary>
        public void Reset() { }

        public float GetConfidence() => _track != null && _track.Count > 0 ? 1f : 0f;

        public void UpdateParameters(HumanConfig config) => _config = config;

        /// <summary>
        /// The velocity that walks a body from <paramref name="currentPosition"/> towards where the recording
        /// put it at <paramref name="seconds"/>, clamped to <paramref name="maxSpeed"/>.
        ///
        /// A pure function of the track, the pose and the instant, so the rule can be read and tested without
        /// a scene or a physics step. It answers zero - the human stands still - before the track's first
        /// sample and after its last one, because a recording says nothing about those instants and inventing
        /// a pose for them would put a body somewhere the run never had one. Inside the track it interpolates
        /// between the two samples that bracket the lead, aims at that point, and turns the gap into a
        /// velocity by dividing by the lead: a body already on the recording walks at the recorded speed, one
        /// left behind catches up, and neither ever exceeds the configured limit.
        /// </summary>
        public static Vector2 DesiredVelocity(
            IReadOnlyList<double[]> track,
            Vector2 currentPosition,
            double seconds,
            float maxSpeed)
        {
            if (track == null || track.Count == 0 || !IsInsideTrack(track, seconds))
                return Vector2.zero;

            if (!AnalysisTrackReader.TryPositionAt(track, seconds + LookAheadSeconds, out Vector2 target))
                return Vector2.zero;

            Vector2 delta = target - currentPosition;
            if (delta.sqrMagnitude < 1e-8f)
                return Vector2.zero;

            Vector2 velocity = delta / (float)LookAheadSeconds;
            return Vector2.ClampMagnitude(velocity, Mathf.Max(0.01f, maxSpeed));
        }

        /// <summary>
        /// Whether the recording says anything about <paramref name="seconds"/>: the cursor stands inside the
        /// span from the track's first sample to its last one. The clock of a replay is anchored on the first
        /// sample of the episode, so an agent whose own track starts later is honestly standing still until
        /// its part of the run begins, and one whose track ended earlier has stopped.
        ///
        /// The two bounds are read with indexed scans rather than the reader's <c>FirstSeconds</c> and
        /// <c>LastSeconds</c>, which walk the list through a boxed enumerator: this runs once per human per
        /// physics step, so it has to stay allocation-free, while the reader's forms are free to be convenient
        /// for the map. The interpolation itself is the reader's own, as everywhere else.
        /// </summary>
        public static bool IsInsideTrack(IReadOnlyList<double[]> track, double seconds)
        {
            if (track == null || track.Count == 0)
                return false;

            double first = FirstSampleSeconds(track);
            if (double.IsNaN(first))
                return false;

            return seconds >= first && seconds <= LastSampleSeconds(track);
        }

        /// <summary>The world-second of a track's first usable sample, or NaN for one that holds none.</summary>
        private static double FirstSampleSeconds(IReadOnlyList<double[]> track)
        {
            for (int index = 0; index < track.Count; index++)
            {
                if (AnalysisTrackReader.IsSample(track[index]))
                    return track[index][0];
            }
            return double.NaN;
        }

        /// <summary>The world-second of a track's last usable sample, or NaN for one that holds none.</summary>
        private static double LastSampleSeconds(IReadOnlyList<double[]> track)
        {
            for (int index = track.Count - 1; index >= 0; index--)
            {
                if (AnalysisTrackReader.IsSample(track[index]))
                    return track[index][0];
            }
            return double.NaN;
        }
    }
}
