using System;
using System.Collections.Generic;
using RobotSNAP.Metrics;
using UnityEngine;

/// <summary>
/// One episode's prefix metrics, walked once and then read at any instant.
///
/// <see cref="AnalysisTrackReader.Metrics"/> answers "what do the metrics read with the cursor here" by
/// walking the robot's trajectory up to the cursor. That is the honest definition, but a replay asks the
/// same question once per frame, and walking a prefix that grows with the cursor is what made a long
/// recording slow down towards the end of its frise: the last second of a minute was paid for sixty times
/// over, once per second of playback, and each payment had grown with the run.
///
/// Every number the definition returns is a running quantity over the robot's samples - the path grows a
/// segment at a time, the fastest speed is the largest segment so far, the crowd is measured sample by
/// sample, and an intrusion is one rising edge - so the whole episode can be walked once and the answers
/// kept. Reading an instant is then the entry for the last sample at or before it, plus the one piece of
/// path the samples do not hold: the part between that sample and the cursor, which <see cref="At"/> adds so
/// a scrubbed reading is the same number as the definition produces.
///
/// The values are the episode's own, not the run's: a track that holds no usable sample has no prefix to
/// read, and <see cref="At"/> reports that as an unusable reading rather than as zeroes.
///
/// Nothing here is a MonoBehaviour and nothing here touches a panel: the whole of it is testable without
/// entering Play mode.
/// </summary>
public sealed class AnalysisPrefixTimeline
{
    /// <summary>A run whose prefix can never be read: no episode, or one that kept no robot track.</summary>
    private static readonly AnalysisPrefixTimeline Empty = new AnalysisPrefixTimeline(
        null,
        Array.Empty<double>(),
        Array.Empty<Vector2>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        Array.Empty<int>(),
        Array.Empty<double>(),
        Array.Empty<int>(),
        Array.Empty<double>());

    private readonly IReadOnlyList<double[]> _robot;
    private readonly double[] _times;
    private readonly Vector2[] _points;
    private readonly double[] _path;
    private readonly double[] _maxSpeed;
    private readonly double[] _minHuman;
    private readonly double[] _humanSum;
    private readonly int[] _humanSamples;
    private readonly double[] _minClearance;
    private readonly int[] _intrusions;
    private readonly double[] _personalSeconds;

    private AnalysisPrefixTimeline(
        IReadOnlyList<double[]> robot,
        double[] times,
        Vector2[] points,
        double[] path,
        double[] maxSpeed,
        double[] minHuman,
        double[] humanSum,
        int[] humanSamples,
        double[] minClearance,
        int[] intrusions,
        double[] personalSeconds)
    {
        _robot = robot;
        _times = times;
        _points = points;
        _path = path;
        _maxSpeed = maxSpeed;
        _minHuman = minHuman;
        _humanSum = humanSum;
        _humanSamples = humanSamples;
        _minClearance = minClearance;
        _intrusions = intrusions;
        _personalSeconds = personalSeconds;
    }

    /// <summary>
    /// Builds the timeline of one episode, or the empty one when there is nothing to walk. The walk is the
    /// expensive part - the crowd is looked up for every robot sample - so a caller that reads one instant
    /// should keep the result, which is exactly what <see cref="AnalysisEpisodeReplay"/> does.
    /// </summary>
    public static AnalysisPrefixTimeline Build(EpisodeMetrics episode)
    {
        if (episode == null)
            return Empty;

        IReadOnlyDictionary<string, List<double[]>> tracks = MetricsStore.Instance.TracksOf(episode);
        string robotKey = MetricsContract.RobotTrackKey(episode.Robot);
        if (!tracks.TryGetValue(robotKey, out List<double[]> robot) || robot == null)
            return Empty;

        // Only the crowd counts as humans. The episode files every robot of the fleet under its own roster
        // key, so a second robot must never be measured against as if it were a person - that would make the
        // prefix disagree with the recorder, which never counted a robot as a human either.
        var humans = new List<IReadOnlyList<double[]>>();
        foreach (KeyValuePair<string, List<double[]>> track in tracks)
        {
            if (!AnalysisTrackReader.IsHumanKey(track.Key) || track.Value == null || track.Value.Count == 0)
                continue;
            humans.Add(track.Value);
        }

        return Create(robot, humans, episode);
    }

    /// <summary>
    /// The prefix that ends <paramref name="relativeSeconds"/> after the episode's first robot sample, or an
    /// unusable reading when the track held nothing to read.
    /// </summary>
    public AnalysisPrefixMetrics At(double relativeSeconds)
    {
        if (_robot == null)
            return default;

        double relative = Math.Max(0.0, relativeSeconds);
        double cursor = _times[0] + relative;

        int slot = LastAtOrBefore(cursor);
        if (slot < 0)
            return default;

        // The samples answer everything but the last, partial segment: the path the map draws runs as far as
        // the robot had got, which is between two samples while the frise is being scrubbed. Adding it here is
        // what keeps the reading identical to the definition read whole - including the polyline's own refusal
        // to add a head that has not moved away from the last sample, which is the same thing a still robot
        // between two samples draws.
        double path = _path[slot];
        if (AnalysisTrackReader.TryPositionAt(_robot, cursor, out Vector2 head))
        {
            Vector2 last = _points[slot];
            if ((head - last).sqrMagnitude > 1e-12f)
                path += Vector2.Distance(last, head);
        }

        return new AnalysisPrefixMetrics(
            relative,
            path,
            relative > 0.0 ? path / relative : 0.0,
            _maxSpeed[slot],
            _humanSamples[slot] > 0 ? _minHuman[slot] : EpisodeMetrics.NoHumanDistance,
            _humanSamples[slot] > 0 ? _humanSum[slot] / _humanSamples[slot] : EpisodeMetrics.NoHumanDistance,
            _humanSamples[slot] > 0 ? _minClearance[slot] : EpisodeMetrics.NoHumanDistance,
            _intrusions[slot],
            _personalSeconds[slot]);
    }

    /// <summary>
    /// Walks the robot's samples once, carrying every running quantity the prefix is made of. The values are
    /// the ones <see cref="AnalysisTrackReader.Metrics"/> arrives at, in the same order and with the same
    /// definitions, because both walk the same samples and add up the same segments.
    /// </summary>
    private static AnalysisPrefixTimeline Create(
        IReadOnlyList<double[]> robot,
        IReadOnlyList<IReadOnlyList<double[]>> humans,
        EpisodeMetrics episode)
    {
        int count = 0;
        foreach (double[] sample in robot)
        {
            if (AnalysisTrackReader.IsSample(sample))
                count++;
        }

        if (count == 0)
            return Empty;

        var times = new double[count];
        var points = new Vector2[count];
        var path = new double[count];
        var maxSpeed = new double[count];
        var minHuman = new double[count];
        var humanSum = new double[count];
        var humanSamples = new int[count];
        var minClearance = new double[count];
        var intrusions = new int[count];
        var personalSeconds = new double[count];

        double radius = Math.Max(0.0, episode.PersonalSpaceRadiusMetres);
        // The prefix measures the same quantity the recorder did: the gap left between the two bodies. The
        // trajectory file carries poses and not radii, so it borrows the footprints the episode recorded -
        // the tracked robot's and the closest crowd member's - which is exactly the pair the final numbers
        // were computed with. An episode written before those keys existed reports zeroes and falls back to
        // centre-to-centre, which is what it was recorded as.
        double bodies = Math.Max(0.0, episode.RobotRadiusMetres) + Math.Max(0.0, episode.HumanRadiusMetres);

        double runningPath = 0.0;
        double runningMax = 0.0;
        double runningMinHuman = double.PositiveInfinity;
        double runningMinClearance = double.PositiveInfinity;
        double runningSum = 0.0;
        int runningSamples = 0;
        int runningIntrusions = 0;
        double runningPersonal = 0.0;
        bool inside = false;
        bool hasPrevious = false;
        double previousSeconds = 0.0;
        double[] previousSample = null;
        int slot = 0;

        foreach (double[] sample in robot)
        {
            if (!AnalysisTrackReader.IsSample(sample))
                continue;

            double time = sample[0];
            Vector2 pose = AnalysisTrackReader.Point(sample);

            if (previousSample != null)
            {
                runningPath += Vector2.Distance(AnalysisTrackReader.Point(previousSample), pose);
                runningMax = Math.Max(runningMax, AnalysisTrackReader.SegmentSpeed(previousSample, sample));
            }

            double nearest = NearestHuman(humans, pose, time);
            if (!double.IsPositiveInfinity(nearest))
            {
                if (nearest < runningMinHuman)
                    runningMinHuman = nearest;
                runningSum += nearest;
                runningSamples++;
            }

            double clearance = nearest - bodies;
            if (!double.IsPositiveInfinity(nearest) && clearance < runningMinClearance)
                runningMinClearance = clearance;

            bool isInside = !double.IsPositiveInfinity(nearest) && clearance < radius;
            if (isInside && !inside)
                runningIntrusions++;
            if (isInside && hasPrevious)
                runningPersonal += Math.Max(0.0, time - previousSeconds);

            inside = isInside;
            previousSeconds = time;
            hasPrevious = true;
            previousSample = sample;

            times[slot] = time;
            points[slot] = pose;
            path[slot] = runningPath;
            maxSpeed[slot] = runningMax;
            minHuman[slot] = runningMinHuman;
            humanSum[slot] = runningSum;
            humanSamples[slot] = runningSamples;
            minClearance[slot] = runningMinClearance;
            intrusions[slot] = runningIntrusions;
            personalSeconds[slot] = runningPersonal;
            slot++;
        }

        return new AnalysisPrefixTimeline(
            robot, times, points, path, maxSpeed, minHuman, humanSum, humanSamples, minClearance, intrusions,
            personalSeconds);
    }

    /// <summary>
    /// The slot of the last sample at or before <paramref name="cursor"/>, or -1 before the first one. The
    /// sample times are in order, so the answer is a bisection and the reading costs the same wherever the
    /// cursor stands.
    /// </summary>
    private int LastAtOrBefore(double cursor)
    {
        int low = 0;
        int high = _times.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_times[middle] <= cursor)
                low = middle + 1;
            else
                high = middle;
        }
        return low - 1;
    }

    /// <summary>Distance from one robot pose to the nearest human standing at that instant.</summary>
    private static double NearestHuman(IReadOnlyList<IReadOnlyList<double[]>> humans, Vector2 pose, double time)
    {
        double nearest = double.PositiveInfinity;
        for (int index = 0; index < humans.Count; index++)
        {
            if (!AnalysisTrackReader.TryPositionAt(humans[index], time, out Vector2 position))
                continue;

            double distance = Vector2.Distance(pose, position);
            if (distance < nearest)
                nearest = distance;
        }
        return nearest;
    }
}
