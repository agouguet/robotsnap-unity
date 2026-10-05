using System;
using System.Collections.Generic;
using RobotSNAP.Metrics;
using UnityEngine;

/// <summary>
/// Where the replay cursor of one episode stands: which run is being replayed, at what instant of its world
/// time, and whether it is playing.
///
/// It is the one thing the trajectory map, the detail panel and the transport strip share, which is what
/// keeps the three of them talking about the same instant. None of them owns the cursor: the views draw
/// whatever this reports and the transport asks it to move, so scrubbing cannot leave a map one second
/// ahead of the numbers beside it.
///
/// The time is the episode's own world time - the seconds the metrics are measured in - and it runs from the
/// first robot sample to the recorded world duration. An episode opens on its complete trajectory, because
/// that is what the tab showed before there was a cursor, and a cursor left at the end is not a prefix: the
/// views fall back to the recorded values, so a run that is never scrubbed reads exactly as it always did.
///
/// Playback advances by the real seconds the caller reports, never by the simulator's, so a reader who set
/// the Unity time scale to watch a run fast still sees the replay advance at one world second per second.
/// </summary>
public sealed class AnalysisEpisodeReplay
{
    /// <summary>How far the step button moves the cursor, in world seconds.</summary>
    public const double StepSeconds = 1.0;

    /// <summary>
    /// How much of each path the map draws behind the cursor while the cursor is inside the episode.
    ///
    /// A replay that drew the whole prefix would bury the map under every metre the run ever covered, and the
    /// part of a moving picture anybody reads is where the agents are now and where they have just been. Five
    /// seconds is long enough to read a direction and short enough to stay legible in a crowd.
    /// </summary>
    public const double TrailSeconds = 5.0;

    /// <summary>Slack that keeps a cursor resting on the end from reading as a prefix.</summary>
    private const double Epsilon = 1e-6;

    private EpisodeMetrics _episode;
    private double _time;
    private bool _playing;

    /// <summary>
    /// The prefix metrics of <see cref="_episode"/>, built on the first read of a cursor and kept until
    /// another run is shown.
    ///
    /// Walking an episode to answer the metrics is not something a frame can afford repeatedly: the walk
    /// covers every robot sample up to the cursor and looks the crowd up at each one, so reading it afresh
    /// once per frame made a long recording cost more and more as its frise advanced. It is built lazily
    /// rather than on <see cref="Show"/> so that selecting an episode never stalls the tab on a run nobody
    /// has scrubbed yet.
    /// </summary>
    private AnalysisPrefixTimeline _prefix;

    /// <summary>Raised when the episode, the instant or the transport state moved.</summary>
    public event Action Changed;

    /// <summary>The run being replayed, or null while none is.</summary>
    public EpisodeMetrics Episode => _episode;

    /// <summary>The episode's world duration, which is the far end of the frise; zero with no episode.</summary>
    public double Duration => _episode != null ? Math.Max(0.0, _episode.WorldSeconds) : 0.0;

    /// <summary>The instant the cursor stands on, in world seconds.</summary>
    public double Time => _time;

    /// <summary>True while the transport is advancing the cursor on its own.</summary>
    public bool IsPlaying => _playing;

    /// <summary>True when there is a run to replay.</summary>
    public bool HasEpisode => _episode != null;

    /// <summary>True when the cursor stands on the beginning of the episode.</summary>
    public bool AtStart => _episode == null || _time <= Epsilon;

    /// <summary>True when the cursor stands on the end - the state an episode opens in.</summary>
    public bool AtEnd => _episode == null || _time >= Duration - Epsilon;

    /// <summary>
    /// True while the cursor sits strictly inside the episode, which is the only state in which the prefix
    /// is a subset: at the end the recorded values are the prefix, and the views say so by using them.
    /// </summary>
    public bool IsScrubbed => _episode != null && _time < Duration - Epsilon;

    /// <summary>
    /// Replays <paramref name="episode"/>, opening on its complete trajectory, or clears the cursor when it
    /// is null. Asking again for the run already being replayed changes nothing: the session re-reports
    /// itself every time an episode ends, and that must not yank a reader's cursor back to the end.
    /// </summary>
    public void Show(EpisodeMetrics episode)
    {
        if (ReferenceEquals(_episode, episode))
            return;

        _episode = episode;
        _time = Duration;
        _playing = false;
        _prefix = null;
        Changed?.Invoke();
    }

    /// <summary>Moves the cursor to <paramref name="seconds"/>, clamped to the episode.</summary>
    public void Seek(double seconds)
    {
        if (_episode == null)
            return;

        double clamped = Math.Max(0.0, Math.Min(Duration, seconds));
        if (Math.Abs(clamped - _time) < 1e-9)
            return;

        _time = clamped;
        Changed?.Invoke();
    }

    /// <summary>Moves the cursor one second forward, never past the end.</summary>
    public void Step() => Seek(_time + StepSeconds);

    /// <summary>Moves the cursor one second back, never before the beginning.</summary>
    public void StepBack() => Seek(_time - StepSeconds);

    /// <summary>Puts the cursor back on the beginning of the episode.</summary>
    public void Rewind() => Seek(0.0);

    /// <summary>
    /// Starts the transport. Asking for play at the end of a run plays it again from the beginning, which is
    /// what a reader who just watched an episode and presses play means.
    /// </summary>
    public void Play()
    {
        if (_episode == null || Duration <= 0.0)
            return;

        if (AtEnd)
            _time = 0.0;

        if (_playing)
            return;

        _playing = true;
        Changed?.Invoke();
    }

    /// <summary>Stops the transport where the cursor stands.</summary>
    public void Pause()
    {
        if (!_playing)
            return;

        _playing = false;
        Changed?.Invoke();
    }

    /// <summary>Play when stopped, stop when playing; this is what the transport's one button does.</summary>
    public void TogglePlay()
    {
        if (_playing)
            Pause();
        else
            Play();
    }

    /// <summary>
    /// Moves the cursor forward by the real seconds the caller measured. The transport stops on the end on
    /// its own, which is what puts the button back to "play" without the reader doing anything.
    /// </summary>
    public void Advance(double seconds)
    {
        if (!_playing || _episode == null || seconds <= 0.0)
            return;

        double next = _time + seconds;
        if (next >= Duration)
        {
            _time = Duration;
            _playing = false;
        }
        else
        {
            _time = next;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// One agent's path up to the cursor: every sample at or before it, cut between the two samples that
    /// bracket it. An unknown key is an empty path, not an error - the map asks for the keys of the episode
    /// it is drawing.
    /// </summary>
    public IReadOnlyList<Vector2> Track(string key)
    {
        if (_episode == null || string.IsNullOrEmpty(key) ||
            !Tracks().TryGetValue(key, out List<double[]> samples))
            return Array.Empty<Vector2>();

        return AnalysisTrackReader.PointsUpTo(samples, CursorSeconds());
    }

    /// <summary>
    /// One agent's path over the last <paramref name="windowSeconds"/> of the episode up to the cursor: a
    /// window that slides with it rather than a line growing from the start.
    ///
    /// This is what the map draws while the cursor is inside the episode. At the end of the frise the whole
    /// trajectory is the honest answer - the run is over, and a reader looking at a finished episode is
    /// comparing whole paths - which is why <see cref="Track"/> stays the complete prefix.
    /// </summary>
    public IReadOnlyList<Vector2> Trail(string key, double windowSeconds = TrailSeconds)
    {
        if (_episode == null || string.IsNullOrEmpty(key) || windowSeconds <= 0.0 ||
            !Tracks().TryGetValue(key, out List<double[]> samples))
            return Array.Empty<Vector2>();

        double cursor = CursorSeconds();
        return AnalysisTrackReader.PointsBetween(samples, cursor - windowSeconds, cursor);
    }

    /// <summary>
    /// The episode's metrics over the prefix the cursor ends. With no episode, or with one that kept no robot
    /// path, the result reports itself as unusable and a caller falls back to the recorded values.
    ///
    /// The reading is a lookup into the timeline the episode walked into once, not a walk of its own: the
    /// cursor moves every frame of a playback, and the answer costs the same at the end of a minute as at its
    /// beginning.
    /// </summary>
    public AnalysisPrefixMetrics Metrics()
    {
        if (_episode == null)
            return default;

        return (_prefix ??= AnalysisPrefixTimeline.Build(_episode)).At(_time);
    }

    /// <summary>
    /// The cursor as an absolute world-second, which is the frame the episodes' samples are written in. The
    /// frise counts from the beginning of the episode, so the robot's first sample is the zero of it.
    /// </summary>
    private double CursorSeconds()
    {
        if (_episode == null)
            return _time;

        IReadOnlyDictionary<string, List<double[]>> tracks = Tracks();
        string robotKey = MetricsContract.RobotTrackKey(_episode.Robot);
        return tracks.TryGetValue(robotKey, out List<double[]> robot)
            ? AnalysisTrackReader.FirstSeconds(robot) + _time
            : _time;
    }

    /// <summary>
    /// The episode's tracks, from the archive when the map has left RAM and from the map while it has not.
    /// The store caches the last record it read, so a frame that asks for every drawn agent pays for one
    /// decode rather than one per agent.
    /// </summary>
    private IReadOnlyDictionary<string, List<double[]>> Tracks()
        => MetricsStore.Instance.TracksOf(_episode);
}
