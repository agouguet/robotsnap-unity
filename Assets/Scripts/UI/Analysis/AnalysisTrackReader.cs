using System;
using System.Collections.Generic;
using RobotSNAP.Metrics;
using UnityEngine;

/// <summary>
/// The arithmetic of one episode read up to one instant: where every agent stood by then, and what the
/// social metrics of that prefix are.
///
/// It exists because the replay cursor has to answer the same questions the recorder answered for a whole
/// episode, and it has to answer them from the same data - the <c>[t, x, z]</c> samples of
/// <see cref="EpisodeMetrics.Trajectories"/> - with the same definitions. The robot's path length is the
/// length of the drawn polyline, the closest human is the closest one on a sample, and an intrusion is one
/// rising edge into a personal space, exactly as <see cref="EpisodeAccumulator"/> counts them. A prefix
/// that used a different definition would make the comparison the detail panel offers meaningless.
///
/// The samples are the subsampled ones the episode kept, so a prefix is only as precise as the line the map
/// draws, and the buffer keeps the first and last sample through every halving, which is what makes the cut
/// at the cursor land on the path anyway.
///
/// Every lookup here is a bisection and not a walk from the first sample: the samples are in time order, and
/// a replay asks where an agent stood once per frame for every agent it draws, so a walk would make the last
/// second of a long recording cost more than its first. A reader that asks the same question every frame -
/// which is what the replay cursor does - keeps an <see cref="AnalysisPrefixTimeline"/> instead, so the walk
/// over the episode happens once and not once per frame.
///
/// Nothing here allocates beyond the point list it returns, and nothing here is a MonoBehaviour: the whole
/// of it is testable without entering Play mode.
/// </summary>
public static class AnalysisTrackReader
{
    /// <summary>The world-second a track starts at, or zero for a track that holds nothing usable.</summary>
    public static double FirstSeconds(IReadOnlyList<double[]> samples)
    {
        if (samples == null)
            return 0.0;

        foreach (double[] sample in samples)
        {
            if (IsSample(sample))
                return sample[0];
        }
        return 0.0;
    }

    /// <summary>The world-second a track ends at, or zero for a track that holds nothing usable.</summary>
    public static double LastSeconds(IReadOnlyList<double[]> samples)
    {
        if (samples == null)
            return 0.0;

        for (int index = samples.Count - 1; index >= 0; index--)
        {
            if (IsSample(samples[index]))
                return samples[index][0];
        }
        return 0.0;
    }

    /// <summary>
    /// Index of the first usable sample whose time is at or after <paramref name="seconds"/>, or
    /// <c>Count</c> when every sample precedes it.
    ///
    /// The samples are in time order, so the answer is a bisection. The one row a bisection cannot decide is
    /// a row that is not a sample at all - the recorder never writes one, and the walk this replaced skipped
    /// it - so a probe that lands on one falls back to the walk and answers exactly what the walk answered.
    /// </summary>
    public static int FirstSampleAtOrAfter(IReadOnlyList<double[]> samples, double seconds)
    {
        if (samples == null)
            return 0;

        int low = 0;
        int high = samples.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (!IsSample(samples[middle]))
                return FirstSampleAtOrAfterScan(samples, seconds);

            if (samples[middle][0] < seconds)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>
    /// Index of the first usable sample whose time is strictly after <paramref name="seconds"/>, or
    /// <c>Count</c> when every sample is at or before it. The sibling of
    /// <see cref="FirstSampleAtOrAfter"/>, and a bisection for the same reason.
    /// </summary>
    public static int FirstSampleAfter(IReadOnlyList<double[]> samples, double seconds)
    {
        if (samples == null)
            return 0;

        int low = 0;
        int high = samples.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (!IsSample(samples[middle]))
                return FirstSampleAfterScan(samples, seconds);

            if (samples[middle][0] > seconds)
                high = middle;
            else
                low = middle + 1;
        }
        return low;
    }

    /// <summary>
    /// Where the agent stood at <paramref name="seconds"/>, interpolated between the two samples that
    /// bracket it. Before its first sample it stands where it started and after its last one where it
    /// stopped, which is what keeps a human that walks only part of the episode from dropping out of the
    /// distance metrics for the rest of it.
    /// </summary>
    public static bool TryPositionAt(IReadOnlyList<double[]> samples, double seconds, out Vector2 position)
    {
        position = default;
        if (samples == null)
            return false;

        int after = FirstSampleAfter(samples, seconds);
        int before = PreviousSampleIndex(samples, after);
        if (before < 0)
        {
            // No sample at or before the instant, so the agent had not started yet: it stands where its first
            // sample put it. With nothing after either, the track holds nothing usable at all.
            if (after >= samples.Count)
                return false;

            position = Point(samples[after]);
            return true;
        }

        if (after >= samples.Count)
        {
            // The agent has stopped: it stands where its last sample put it.
            position = Point(samples[before]);
            return true;
        }

        double[] next = samples[after];
        double[] previous = samples[before];
        double span = next[0] - previous[0];
        double blend = span > 0.0 ? (seconds - previous[0]) / span : 0.0;
        position = Vector2.Lerp(Point(previous), Point(next), (float)blend);
        return true;
    }

    /// <summary>
    /// The polyline of one agent up to <paramref name="seconds"/>: every sample at or before the cursor,
    /// plus the point the agent had reached between the last of them and the next one. A track with nothing
    /// before the cursor is empty, not a line starting where the agent eventually went.
    /// </summary>
    public static List<Vector2> PointsUpTo(IReadOnlyList<double[]> samples, double seconds)
    {
        var points = new List<Vector2>();
        if (samples == null)
            return points;

        int after = FirstSampleAfter(samples, seconds);
        bool cut = after < samples.Count;
        for (int index = 0; index < after; index++)
        {
            if (IsSample(samples[index]))
                points.Add(Point(samples[index]));
        }

        if (cut && points.Count > 0 && TryPositionAt(samples, seconds, out Vector2 head))
        {
            Vector2 last = points[points.Count - 1];
            if ((head - last).sqrMagnitude > 1e-12f)
                points.Add(head);
        }
        return points;
    }

    /// <summary>
    /// The polyline of one agent between two instants: the point it had already reached at the left edge,
    /// every sample inside the window, and the point it had reached at the right one.
    ///
    /// This is what a moving replay draws. A window that slides along the path keeps the picture readable
    /// however long the run is, where a line growing from the start would end up burying the map under every
    /// metre the run ever covered. A track that had not started by the right edge is empty, and one that had
    /// already stopped is a single point - the agent standing where it stopped - rather than a line to nowhere.
    /// </summary>
    public static List<Vector2> PointsBetween(
        IReadOnlyList<double[]> samples,
        double fromSeconds,
        double toSeconds)
    {
        var points = new List<Vector2>();
        if (samples == null || toSeconds < fromSeconds)
            return points;

        int start = FirstSampleAtOrAfter(samples, fromSeconds);
        int end = FirstSampleAfter(samples, toSeconds);

        var inside = new List<Vector2>();
        for (int index = start; index < end; index++)
        {
            if (IsSample(samples[index]))
                inside.Add(Point(samples[index]));
        }

        // Where the agent stood at the left edge, so the line is a window onto its path and not a segment that
        // restarts at every sample boundary as the window slides past it.
        if (HasSampleAtOrBefore(samples, fromSeconds) &&
            TryPositionAt(samples, fromSeconds, out Vector2 entry) &&
            (inside.Count == 0 || (inside[0] - entry).sqrMagnitude > 1e-12f))
        {
            points.Add(entry);
        }

        points.AddRange(inside);

        if (points.Count > 0 && TryPositionAt(samples, toSeconds, out Vector2 head))
        {
            Vector2 last = points[points.Count - 1];
            if ((head - last).sqrMagnitude > 1e-12f)
                points.Add(head);
        }

        return points;
    }

    /// <summary>
    /// Whether the agent had any sample by <paramref name="seconds"/>. The samples are in time order, which is
    /// the order every reader here walks them in, so the first one past the instant decides it - and the
    /// sample before that one, if there is one, is the one that had already been written.
    /// </summary>
    private static bool HasSampleAtOrBefore(IReadOnlyList<double[]> samples, double seconds)
        => PreviousSampleIndex(samples, FirstSampleAfter(samples, seconds)) >= 0;

    /// <summary>
    /// The episode's own metrics over the prefix that ends <paramref name="relativeSeconds"/> after its first
    /// robot sample. The returned value's <see cref="AnalysisPrefixMetrics.HasRobot"/> is false when the
    /// episode kept no robot path - an old or truncated record - so the caller can fall back to the recorded
    /// numbers rather than report an empty prefix as a zero.
    ///
    /// This is the definition read whole: it walks the prefix every time it is called. A reader that moves a
    /// cursor - the replay - keeps an <see cref="AnalysisPrefixTimeline"/> and asks that instead, so the same
    /// numbers are not recomputed once per frame.
    /// </summary>
    public static AnalysisPrefixMetrics Metrics(EpisodeMetrics episode, double relativeSeconds)
    {
        if (episode == null)
            return default;

        IReadOnlyDictionary<string, List<double[]>> tracks = MetricsStore.Instance.TracksOf(episode);
        string robotKey = MetricsContract.RobotTrackKey(episode.Robot);
        if (!tracks.TryGetValue(robotKey, out List<double[]> robot) || robot == null)
            return default;

        double start = FirstSeconds(robot);
        double relative = Math.Max(0.0, relativeSeconds);
        double cursor = start + relative;

        List<Vector2> path = PointsUpTo(robot, cursor);
        if (path.Count == 0)
            return default;

        double pathLength = 0.0;
        for (int index = 1; index < path.Count; index++)
            pathLength += Vector2.Distance(path[index - 1], path[index]);

        // Only the crowd counts as humans. The episode files every robot of the fleet under its own roster
        // key, so a second robot must never be measured against as if it were a person - that would make the
        // prefix disagree with the recorder, which never counted a robot as a human either.
        var humans = new List<IReadOnlyList<double[]>>();
        foreach (KeyValuePair<string, List<double[]>> track in tracks)
        {
            if (!IsHumanKey(track.Key) || track.Value == null || track.Value.Count == 0)
                continue;
            humans.Add(track.Value);
        }

        double radius = Math.Max(0.0, episode.PersonalSpaceRadiusMetres);
        // The prefix measures the same quantity the recorder did: the gap left between the two bodies. The
        // trajectory file carries poses and not radii, so it borrows the footprints the episode recorded -
        // the tracked robot's and the closest crowd member's - which is exactly the pair the final numbers
        // were computed with. An episode written before those keys existed reports zeroes and falls back to
        // centre-to-centre, which is what it was recorded as.
        double bodies = Math.Max(0.0, episode.RobotRadiusMetres) + Math.Max(0.0, episode.HumanRadiusMetres);
        double minHuman = double.PositiveInfinity;
        double minClearance = double.PositiveInfinity;
        double humanSum = 0.0;
        int humanSamples = 0;
        int intrusions = 0;
        double personalSeconds = 0.0;
        bool inside = false;
        bool hasPrevious = false;
        double previousSeconds = 0.0;

        foreach (double[] sample in robot)
        {
            if (!IsSample(sample))
                continue;

            double time = sample[0];
            if (time > cursor)
                break;

            Vector2 pose = Point(sample);
            double nearest = NearestHuman(humans, pose, time);
            if (!double.IsPositiveInfinity(nearest))
            {
                if (nearest < minHuman)
                    minHuman = nearest;
                humanSum += nearest;
                humanSamples++;
            }

            double clearance = nearest - bodies;
            if (!double.IsPositiveInfinity(nearest) && clearance < minClearance)
                minClearance = clearance;

            bool isInside = !double.IsPositiveInfinity(nearest) && clearance < radius;
            if (isInside && !inside)
                intrusions++;
            if (isInside && hasPrevious)
                personalSeconds += Math.Max(0.0, time - previousSeconds);

            inside = isInside;
            previousSeconds = time;
            hasPrevious = true;
        }

        return new AnalysisPrefixMetrics(
            relative,
            pathLength,
            relative > 0.0 ? pathLength / relative : 0.0,
            MaxSpeed(robot, cursor),
            humanSamples > 0 ? minHuman : EpisodeMetrics.NoHumanDistance,
            humanSamples > 0 ? humanSum / humanSamples : EpisodeMetrics.NoHumanDistance,
            humanSamples > 0 ? minClearance : EpisodeMetrics.NoHumanDistance,
            intrusions,
            personalSeconds);
    }

    /// <summary>
    /// Fastest <c>distance / elapsed</c> between two consecutive samples of the prefix, the definition the
    /// accumulator's maximum speed uses. Two samples that share an instant divide by nothing, so they are
    /// skipped rather than allowed to invent a spike.
    ///
    /// The quantity is a walk over the prefix - every pair of neighbours in it - so this cannot be a lookup;
    /// the bisection only spares it the samples past the cursor, which is where a long recording used to pay
    /// for work the cursor had already left behind. A reader that asks every frame keeps an
    /// <see cref="AnalysisPrefixTimeline"/>, where the same maximum is carried along as the walk is done once.
    /// </summary>
    public static double MaxSpeed(IReadOnlyList<double[]> robot, double cursor)
    {
        double max = 0.0;
        double[] previous = null;
        int end = FirstSampleAfter(robot, cursor);
        for (int index = 0; index < end; index++)
        {
            double[] sample = robot[index];
            if (!IsSample(sample))
                continue;

            if (previous != null)
                max = Math.Max(max, SegmentSpeed(previous, sample));
            previous = sample;
        }
        return max;
    }

    /// <summary>Distance from one robot pose to the nearest human standing at that instant.</summary>
    private static double NearestHuman(IReadOnlyList<IReadOnlyList<double[]>> humans, Vector2 pose, double time)
    {
        double nearest = double.PositiveInfinity;
        for (int index = 0; index < humans.Count; index++)
        {
            if (!TryPositionAt(humans[index], time, out Vector2 position))
                continue;

            double distance = Vector2.Distance(pose, position);
            if (distance < nearest)
                nearest = distance;
        }
        return nearest;
    }

    /// <summary>Whether a row of a trajectory holds a <c>[t, x, z]</c> sample and not padding.</summary>
    public static bool IsSample(double[] sample) => sample != null && sample.Length >= 3;

    /// <summary>
    /// Speed between two consecutive samples of one agent: the distance between them over the time between
    /// them, or zero for two samples that share an instant and so have nothing to divide by.
    /// </summary>
    public static double SegmentSpeed(double[] from, double[] to)
    {
        double elapsed = to[0] - from[0];
        return elapsed > 0.0 ? Vector2.Distance(Point(from), Point(to)) / elapsed : 0.0;
    }

    /// <summary>
    /// The last usable sample before <paramref name="exclusiveEnd"/>, or -1 when the track holds none. The
    /// rows before a cursor are almost always samples, so this is one step; the walk is only for the padding a
    /// malformed record could hold.
    /// </summary>
    private static int PreviousSampleIndex(IReadOnlyList<double[]> samples, int exclusiveEnd)
    {
        int index = Math.Min(exclusiveEnd, samples.Count) - 1;
        for (; index >= 0; index--)
        {
            if (IsSample(samples[index]))
                return index;
        }
        return -1;
    }

    /// <summary>The walk <see cref="FirstSampleAtOrAfter"/> falls back to on a track holding a non-sample row.</summary>
    private static int FirstSampleAtOrAfterScan(IReadOnlyList<double[]> samples, double seconds)
    {
        for (int index = 0; index < samples.Count; index++)
        {
            if (IsSample(samples[index]) && samples[index][0] >= seconds)
                return index;
        }
        return samples.Count;
    }

    /// <summary>The walk <see cref="FirstSampleAfter"/> falls back to on a track holding a non-sample row.</summary>
    private static int FirstSampleAfterScan(IReadOnlyList<double[]> samples, double seconds)
    {
        for (int index = 0; index < samples.Count; index++)
        {
            if (IsSample(samples[index]) && samples[index][0] > seconds)
                return index;
        }
        return samples.Count;
    }

    /// <summary>
    /// Whether a trajectory key names a human. Every crowd member is written as <c>human_&lt;id&gt;</c> and
    /// every robot under its roster id, so the prefix is the whole test - and the one that keeps a second
    /// robot out of the human metrics.
    /// </summary>
    public static bool IsHumanKey(string trackKey)
        => !string.IsNullOrEmpty(trackKey) &&
           trackKey.StartsWith("human_", StringComparison.Ordinal);

    /// <summary>The world XZ point of one sample, which is the frame every map here draws in.</summary>
    public static Vector2 Point(double[] sample) => new Vector2((float)sample[1], (float)sample[2]);
}
