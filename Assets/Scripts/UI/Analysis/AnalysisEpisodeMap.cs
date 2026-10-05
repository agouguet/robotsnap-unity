using System;
using System.Collections.Generic;
using RobotSNAP.Core.Scenario;
using RobotSNAP.Metrics;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The top-down view of one episode: the occupancy map of the scenario that ran, the trajectories the
/// agents actually walked, and a key that names the colour each agent was drawn in. It reuses the drawing
/// the scenario editor and its recap already share - the same scale-to-fit image and the same
/// <see cref="OccupancyMapRouteOverlay"/> - so a path drawn here lands in the same place it would in the
/// editor.
///
/// Trajectories arrive as <c>[t, x, z]</c> in the world metres of the map, which is the frame the overlay
/// already projects from, so no conversion happens here.
///
/// While the reader has the replay cursor inside the episode, each agent is drawn as the trail it left over
/// the last few seconds - every agent's, not only the robot's - so the picture beside the numbers is the state
/// the numbers describe without the map disappearing under every metre the run has covered. At the end of the
/// frise the whole trajectories are drawn, which is what a finished run asks for. Both come from the same
/// <see cref="AnalysisEpisodeReplay"/> the frise moves, so a drag and a redraw cannot disagree about what
/// instant they are showing.
/// </summary>
public sealed class AnalysisEpisodeMap : IDisposable
{
    private readonly VisualElement _canvas;
    private readonly Image _image;
    private readonly Label _placeholder;
    private readonly Label _caption;
    private readonly Label _outcome;
    private readonly VisualElement _legendList;
    private readonly OccupancyMapRouteOverlay _overlay;

    private Texture2D _texture;
    private Bounds _bounds;
    private string _loadedScenario;
    private bool _disposed;
    private EpisodeMetrics _episode;

    // The map is redrawn on every tick of the replay, so the lists it draws from are kept and refilled rather
    // than built again each frame. Nothing the overlay is handed is mutated afterwards: it copies the routes
    // into its own list and only reads the points and masks they point at.
    private readonly List<OccupancyMapRouteOverlay.RouteVisual> _routes = new();
    private readonly List<AnalysisAgentPalette.TrackColour> _legend = new();
    private readonly List<AnalysisAgentPalette.TrackColour> _drawnLegend = new();
    private readonly Dictionary<(int Count, bool Whole), List<bool>> _endpointMasks = new();
    private readonly Dictionary<string, List<Vector2>> _wholePaths = new();
    private IReadOnlyList<AnalysisAgentPalette.TrackColour> _tracks;

    private ScenarioDataService _dataService;
    private ScenarioLoader _loader;
    private AnalysisSession _session;
    private AnalysisEpisodeReplay _replay;
    private bool _scenarioTableRequested;

    public AnalysisEpisodeMap(VisualElement root)
    {
        _canvas = root.Q<VisualElement>("AnalysisMapCanvas");
        _image = root.Q<Image>("AnalysisMapImage");
        _placeholder = root.Q<Label>("AnalysisMapPlaceholder");
        _caption = root.Q<Label>("AnalysisMapCaption");
        _outcome = root.Q<Label>("AnalysisMapOutcome");
        _legendList = root.Q<VisualElement>("AnalysisMapLegendList");
        VisualElement overlayHost = root.Q<VisualElement>("AnalysisMapOverlayHost");

        if (_canvas == null || _image == null || _placeholder == null || _caption == null ||
            _outcome == null || _legendList == null || overlayHost == null)
            throw new InvalidOperationException("The analysis map UI is incomplete.");

        _image.scaleMode = ScaleMode.ScaleToFit;
        _overlay = new OccupancyMapRouteOverlay { ShowGrid = true };
        overlayHost.Add(_overlay);
        _canvas.RegisterCallback<GeometryChangedEvent>(_ => Refresh());

        Show(null);
    }

    /// <summary>Follows one session: the map draws whatever episode the session has selected.</summary>
    public void Bind(AnalysisSession session)
    {
        if (_session != null)
            _session.Changed -= OnSessionChanged;

        _session = session;
        if (_session != null)
            _session.Changed += OnSessionChanged;

        OnSessionChanged();
    }

    /// <summary>
    /// Follows the replay cursor, so moving it redraws the lines it shortens. A null cursor is the whole
    /// episode, which is what the map drew before there was one.
    /// </summary>
    public void SetReplay(AnalysisEpisodeReplay replay)
    {
        if (_replay != null)
            _replay.Changed -= OnReplayChanged;

        _replay = replay;
        if (_replay != null)
            _replay.Changed += OnReplayChanged;

        OnReplayChanged();
    }

    /// <summary>Shows one episode, or clears the view when there is none.</summary>
    public void Show(EpisodeMetrics episode)
    {
        if (!ReferenceEquals(_episode, episode))
        {
            // Everything cached describes the run being replaced: the palette, the whole paths and the key
            // that is already on screen. The endpoint masks depend on a length alone, so they stay.
            _tracks = null;
            _wholePaths.Clear();
            _routes.Clear();
            _legend.Clear();
            _drawnLegend.Clear();
        }

        _episode = episode;
        if (episode == null)
        {
            ReleaseTexture();
            _loadedScenario = null;
            _bounds = new Bounds();
            _image.image = null;
            _overlay.SetRoutes(_routes);
            _overlay.SetMap(_bounds, Rect.zero);
            _placeholder.text = "Select an episode to see the map it ran on.";
            _caption.text = string.Empty;
            _outcome.text = string.Empty;
            _legendList.Clear();
            return;
        }

        EnsureMap(episode.Scenario);
        Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        if (_session != null)
            _session.Changed -= OnSessionChanged;
        if (_replay != null)
            _replay.Changed -= OnReplayChanged;
        _session = null;
        _replay = null;
        ReleaseTexture();
    }

    private void OnSessionChanged() => Show(_session != null ? _session.Selected : null);

    private void OnReplayChanged()
    {
        if (!_disposed)
            Refresh();
    }

    /// <summary>
    /// Redraws what is already loaded. Called again on every geometry change, because the scale-to-fit rect
    /// of the map only exists once the canvas has a size.
    /// </summary>
    private void Refresh()
    {
        if (_disposed)
            return;

        _overlay.SetMap(_bounds, GetDisplayedRect());
        _image.image = _texture;
        _placeholder.EnableInClassList("analysis-map-placeholder-hidden", _texture != null);

        if (_episode == null)
            return;

        if (_texture == null)
            _placeholder.text = "The occupancy map of this scenario is not available.";

        _routes.Clear();
        _legend.Clear();
        bool whole = DrawsWholePaths();
        IReadOnlyDictionary<string, List<double[]>> tracks = MetricsStore.Instance.TracksOf(_episode);
        foreach (AnalysisAgentPalette.TrackColour track in Tracks())
        {
            if (!tracks.TryGetValue(track.Key, out List<double[]> samples))
                continue;

            IReadOnlyList<Vector2> points = TrackPoints(track.Key, samples);
            if (points.Count == 0)
                continue;

            _routes.Add(new OccupancyMapRouteOverlay.RouteVisual(
                points, track.Colour, true, track.Label, false, EndpointMask(points.Count, whole)));
            _legend.Add(track);
        }

        _overlay.SetRoutes(_routes);
        RefreshCaption(_legend.Count);
        RefreshLegend(_legend);
    }

    /// <summary>
    /// The palette of the episode being shown. Assigning it enumerates the episode's whole dictionary and
    /// sorts the fleet, which a redraw has no reason to repeat: it only changes when another run is selected.
    /// </summary>
    private IReadOnlyList<AnalysisAgentPalette.TrackColour> Tracks()
        => _tracks ??= AnalysisAgentPalette.Assign(MetricsStore.Instance.TracksOf(_episode).Keys,
            MetricsContract.RobotTrackKey(_episode.Robot), _episode.Robots);

    /// <summary>
    /// Whether the whole trajectories are being drawn rather than the replay's trailing window.
    ///
    /// True with no cursor, and true when the cursor stands on the end of the episode: an episode opens there,
    /// a playback stops there, and a reader looking at a finished run is comparing whole paths. A cursor left
    /// on another run by a selection change is not this episode's cursor either, and the map falls back to the
    /// whole path rather than cutting it at a time that means nothing here.
    /// </summary>
    private bool DrawsWholePaths()
        => _replay == null || !ReferenceEquals(_replay.Episode, _episode) || _replay.AtEnd;

    /// <summary>
    /// The line to draw for one agent: the whole path, or - while the reader has the cursor inside the episode
    /// - the trail it left over the last few seconds up to it.
    /// </summary>
    private IReadOnlyList<Vector2> TrackPoints(string key, List<double[]> samples)
    {
        if (!DrawsWholePaths())
            return _replay.Trail(key);

        // A whole path is what the episode holds, sample for sample, and it does not move while the episode
        // stays selected - so it is built once and drawn from then on. This is the case the end of a long
        // frise lands in, which is where rebuilding every agent's path each frame was most expensive.
        if (!_wholePaths.TryGetValue(key, out List<Vector2> points))
        {
            points = Points(samples);
            _wholePaths[key] = points;
        }
        return points;
    }

    /// <summary>
    /// What the episode was: the scenario, how it ended, how long it lasted and how long the robot drove.
    /// The map's own scale stays in the scale bar the overlay draws.
    /// </summary>
    private void RefreshCaption(int trackCount)
    {
        var parts = new List<string>
        {
            string.IsNullOrEmpty(_episode.Scenario) ? "no scenario" : _episode.Scenario,
            AnalysisFormatting.OutcomeName(_episode.Outcome),
            AnalysisFormatting.Seconds(_episode.WorldSeconds) + " world",
            AnalysisFormatting.Seconds(_episode.WallSeconds) + " wall",
            AnalysisFormatting.Metres(_episode.PathLengthMetres) + " path",
            trackCount == 1 ? "1 track" : trackCount + " tracks",
        };
        if (_texture == null)
            parts.Add("map unavailable");
        _caption.text = string.Join("   |   ", parts);

        _outcome.text = AnalysisFormatting.OutcomeName(_episode.Outcome);
        _outcome.RemoveFromClassList("analysis-outcome-goal");
        _outcome.RemoveFromClassList("analysis-outcome-collision");
        _outcome.RemoveFromClassList("analysis-outcome-out-of-bounds");
        _outcome.RemoveFromClassList("analysis-outcome-timeout");
        _outcome.RemoveFromClassList("analysis-outcome-stopped");
        _outcome.RemoveFromClassList("analysis-outcome-unknown");
        _outcome.AddToClassList(AnalysisFormatting.OutcomeClass(_episode.Outcome));
    }

    /// <summary>
    /// Colour key of the map: one entry per drawn track, in the palette's own order. It names the agent and
    /// shows its colour, nothing else - how many samples a run happened to keep is a property of the
    /// recording, not of who the line belongs to, and reading it off the key only made the name harder to
    /// scan.
    /// </summary>
    private void RefreshLegend(IReadOnlyList<AnalysisAgentPalette.TrackColour> legend)
    {
        // The key only changes when the set of drawn agents does, which a tick of the replay does not: the
        // same crowd keeps the same entry, and rebuilding it would clear and recreate a visual element per
        // agent, every frame, to say the same thing. A colour or a caption can change with it - the palette
        // names the robot the episode followed - so the whole entry is what is compared.
        if (LegendUnchanged(legend))
            return;

        _legendList.Clear();
        _drawnLegend.Clear();
        foreach (AnalysisAgentPalette.TrackColour track in legend)
        {
            var entry = new VisualElement();
            entry.AddToClassList("analysis-legend-entry");

            var swatch = new VisualElement();
            swatch.AddToClassList("analysis-legend-swatch");
            swatch.style.backgroundColor = track.Colour;
            entry.Add(swatch);

            var name = new Label(track.Label);
            name.AddToClassList("analysis-legend-name");
            entry.Add(name);

            _legendList.Add(entry);
            _drawnLegend.Add(track);
        }
    }

    /// <summary>Whether the key on screen already names the same agents, in the same colours.</summary>
    private bool LegendUnchanged(IReadOnlyList<AnalysisAgentPalette.TrackColour> legend)
    {
        if (_legendList.childCount != _drawnLegend.Count || legend.Count != _drawnLegend.Count)
            return false;

        for (int index = 0; index < legend.Count; index++)
        {
            AnalysisAgentPalette.TrackColour drawn = legend[index];
            AnalysisAgentPalette.TrackColour shown = _drawnLegend[index];
            if (drawn.IsRobot != shown.IsRobot ||
                drawn.Colour != shown.Colour ||
                !string.Equals(drawn.Key, shown.Key, StringComparison.Ordinal) ||
                !string.Equals(drawn.Label, shown.Label, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Loads the map of a scenario once. The map identifier lives in the scenario document, so the episode's
    /// scenario id is resolved through the same table the scenario tab browses.
    /// </summary>
    private void EnsureMap(string scenarioId)
    {
        if (string.Equals(_loadedScenario, scenarioId, StringComparison.Ordinal))
            return;

        ReleaseTexture();
        _loadedScenario = scenarioId;
        _bounds = new Bounds();

        if (string.IsNullOrEmpty(scenarioId))
            return;

        _dataService ??= UnityEngine.Object.FindAnyObjectByType<ScenarioDataService>();
        if (_dataService == null)
            return;

        // EnsureLoaded rebuilds the whole scenario table and, in this project, says so in the console for every
        // scenario it finds. Asking once is enough for the map - a missing scenario does not become available
        // by asking again, and the service reloads itself when its paths change.
        if (!_scenarioTableRequested)
        {
            _dataService.EnsureLoaded();
            _scenarioTableRequested = true;
        }

        ScenarioInfo info = _dataService.GetScenarioInfo(scenarioId);
        string mapIdentifier = info != null ? info.MapImage : null;
        if (string.IsNullOrWhiteSpace(mapIdentifier))
            return;

        _loader ??= UnityEngine.Object.FindAnyObjectByType<ScenarioLoader>();
        if (_loader == null)
            return;

        if (_loader.LoadMapData(mapIdentifier, out Texture2D texture, out Bounds bounds))
        {
            _texture = texture;
            _bounds = bounds;
        }
    }

    private Rect GetDisplayedRect() => _texture == null
        ? Rect.zero
        : OccupancyMapLayout.FitRect(_canvas.contentRect, _texture.width, _texture.height);

    /// <summary>Turns an episode's <c>[t, x, z]</c> samples into the overlay's world XZ points.</summary>
    private static List<Vector2> Points(List<double[]> samples)
    {
        var points = new List<Vector2>(samples != null ? samples.Count : 0);
        if (samples == null)
            return points;

        foreach (double[] sample in samples)
        {
            if (sample == null || sample.Length < 3)
                continue;
            points.Add(new Vector2((float)sample[1], (float)sample[2]));
        }
        return points;
    }

    /// <summary>
    /// A trajectory is a polyline, not a route of waypoints, so only its ends get a marker: a dot on every
    /// sample would bury the path under its own dots.
    ///
    /// A trail has one meaningful end, not two: its head is where the agent is, while its tail is wherever the
    /// window happens to have reached and moves backwards behind it. So a trail marks its head only.
    /// </summary>
    private IReadOnlyList<bool> EndpointMask(int count, bool bothEnds)
    {
        // The mask is a function of a length and of which ends to mark, and it is handed to the overlay by
        // reference: one per drawing, and one per agent of that drawing. Keeping the masks means a crowd of
        // the same size does not rebuild its markers every frame.
        var key = (count, bothEnds);
        if (_endpointMasks.TryGetValue(key, out List<bool> cached))
            return cached;

        var mask = new List<bool>(count);
        for (int index = 0; index < count; index++)
            mask.Add(bothEnds ? index == 0 || index == count - 1 : index == count - 1);

        // A trail slides a metre at a time, so its length takes many values over a long replay. The cache is a
        // short-lived one and not a catalogue: past a few hundred shapes it is cheaper to start again.
        if (_endpointMasks.Count >= 512)
            _endpointMasks.Clear();
        _endpointMasks[key] = mask;
        return mask;
    }

    private void ReleaseTexture()
    {
        if (_texture == null)
            return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(_texture);
        else UnityEngine.Object.DestroyImmediate(_texture);
        _texture = null;
    }
}
