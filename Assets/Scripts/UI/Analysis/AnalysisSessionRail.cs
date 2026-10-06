using System;
using System.Collections.Generic;
using System.Globalization;
using RobotSNAP.Metrics;
using UnityEngine.UIElements;

/// <summary>
/// The Sessions rail of the analysis tab: the column of sessions on its left edge, each line naming one
/// session and carrying the shape of its results.
///
/// It exists as a column rather than as the dropdown it replaced because a session is something a reader
/// compares, not something they are told about one at a time. The bar on every line is the whole point: a
/// reader who has run a benchmark six times sees, before opening any of them, which run reached the goal and
/// which one spent its episodes in collisions. A dropdown can only ever show the one name it currently holds.
///
/// The session running now is always the first line and always drawn selected while it is the one on screen -
/// it is where the recorder is still appending, and it is the session the tab has always opened on. The
/// sessions already saved follow it, ordered by the sort the reader picked, so a run from last week is one
/// line away rather than one folder away.
///
/// Like the episode list beside it, it is a view over <see cref="AnalysisSession"/> and nothing else: it
/// subscribes to <see cref="AnalysisSession.Changed"/> in <see cref="Bind"/>, drops that subscription in
/// <see cref="Dispose"/>, and redraws from the session every time either side moves. Every count and every
/// bar segment comes from <see cref="AnalysisSession.EpisodesOf"/>, so a line and the episodes behind it can
/// never disagree about what a session holds.
/// </summary>
public sealed class AnalysisSessionRail : VisualElement
{
    /// <summary>
    /// The sort the rail offers, newest first by default. They are consts because the tab and the tests name
    /// them, and a mode spelled differently in two places is a mode that silently does nothing.
    /// </summary>
    public const string SortNewest = "Newest first";
    public const string SortOldest = "Oldest first";
    public const string SortMostEpisodes = "Most episodes";
    public const string SortBestSuccess = "Best success";
    public const string SortScenario = "Scenario A-Z";

    /// <summary>The sorts in the order the dropdown offers them, the default first.</summary>
    private static readonly string[] SortChoices =
    {
        SortNewest, SortOldest, SortMostEpisodes, SortBestSuccess, SortScenario,
    };

    /// <summary>
    /// Order the bar lays its segments out in: the project's own outcome vocabulary, ending on "unknown", so
    /// the same session always paints the same way and two sessions can be read against each other.
    /// </summary>
    private static readonly string[] OutcomeOrder =
    {
        MetricsContract.OutcomeGoal,
        MetricsContract.OutcomeCollision,
        MetricsContract.OutcomeOutOfBounds,
        MetricsContract.OutcomeTimeout,
        MetricsContract.OutcomeStopped,
        MetricsContract.OutcomeUnknown,
    };

    /// <summary>Raised with the id of the session a line names, or null for the session running now.</summary>
    public event Action<string> SessionPicked;

    /// <summary>Raised with the id of the session a line asks to export, or null for the one running now.</summary>
    public event Action<string> SessionExportRequested;

    /// <summary>Raised with the id of the session a line asks to delete, or null for the one running now.</summary>
    public event Action<string> SessionDeleteRequested;

    private Label _countLabel;
    private TextField _search;
    private DropdownField _sort;
    private VisualElement _rows;

    // Parallel to the drawn lines: null for the running session, then the id of the saved one each line
    // names. The label alone cannot carry this - two sessions can start in the same second and hold the same
    // number of episodes - so the line's own caption is never what a pick resolves through.
    private readonly List<string> _sessionIds = new List<string>();

    private AnalysisSession _session;

    public AnalysisSessionRail()
    {
        AddToClassList("analysis-rail");

        Add(BuildHeader());
        Add(BuildControls());

        var scroll = new ScrollView();
        scroll.AddToClassList("analysis-rail-scroll");
        _rows = new VisualElement();
        _rows.AddToClassList("analysis-rail-rows");
        scroll.Add(_rows);
        Add(scroll);
    }

    /// <summary>Follows one session, from now on and immediately.</summary>
    public void Bind(AnalysisSession session)
    {
        if (_session != null)
            _session.Changed -= OnSessionChanged;

        _session = session;
        if (_session != null)
            _session.Changed += OnSessionChanged;

        OnSessionChanged();
    }

    /// <summary>Detaches from the session; a disposed rail never draws again.</summary>
    public void Dispose()
    {
        if (_session != null)
            _session.Changed -= OnSessionChanged;
        _session = null;
    }

    /// <summary>
    /// How the rail names one saved session: when it started, in the same short form the rows print, and how
    /// many episodes it holds. It is the caption the tab's status line reuses for the session on screen, so a
    /// reader reads the same words in the rail and in the header.
    /// </summary>
    public static string SavedSessionLabel(ArchiveSessionRecord record)
    {
        if (record == null)
            return AnalysisFormatting.Unavailable;

        int episodes = record.Episodes;
        return $"{AnalysisFormatting.Timestamp(record.StartedAt)} " +
               $"({episodes} episode{(episodes == 1 ? "" : "s")})";
    }

    /// <summary>
    /// Picks one line of the rail by its place in the list the reader sees: index 0 is the session running
    /// now, then the saved ones in the order the sort put them. Split out from the click so the choice can be
    /// made - by a test, or by any caller - without a live event system dispatching a real click.
    ///
    /// The choice is published first: that is what the tab's controller listens to, and it applies the switch
    /// itself. A caller that owns no controller - a test - still needs the session to move, so the switch also
    /// happens here when nothing else did it: the same bypass the dropdown this rail replaced offered through
    /// its own <c>SelectSession</c>, and the guard is what keeps a listening tab from switching twice.
    /// </summary>
    public void SelectSession(int index)
    {
        if (_session == null || index < 0 || index >= _sessionIds.Count)
            return;

        string sessionId = _sessionIds[index];
        SessionPicked?.Invoke(sessionId);

        if (sessionId == null)
        {
            if (!_session.IsRunningSession)
                _session.ShowRunning();
        }
        else if (_session.IsRunningSession ||
                 !string.Equals(_session.SourceId, sessionId, StringComparison.Ordinal))
        {
            _session.ShowSaved(sessionId);
        }
    }

    /// <summary>
    /// What a click on one line does: pick the session it names, unless the click landed on one of the line's
    /// own actions. Exporting a session or deleting it is not asking to look at it, and the two glyphs say
    /// only what their tooltips say. Split out from the callback so the rule can be read - and tested -
    /// without a live event system dispatching a real click.
    /// </summary>
    public void ClickRow(VisualElement row, VisualElement target = null)
    {
        if (row == null || _rows == null || AnalysisEpisodeList.IsRowAction(target))
            return;

        int index = _rows.IndexOf(row);
        if (index >= 0)
            SelectSession(index);
    }

    /// <summary>
    /// What the export glyph of one line does: hands the session it names - null for the one running now - to
    /// whoever owns the export. Split out from the callback so the rule can be read, and tested, without a
    /// live event system dispatching a real click.
    /// </summary>
    public void ClickExport(VisualElement row)
    {
        if (RowIndex(row) is int index)
            SessionExportRequested?.Invoke(_sessionIds[index]);
    }

    /// <summary>What the delete glyph of one line does: asks for the session it names to be removed.</summary>
    public void ClickDelete(VisualElement row)
    {
        if (RowIndex(row) is int index)
            SessionDeleteRequested?.Invoke(_sessionIds[index]);
    }

    /// <summary>The place of one drawn line among the lines, or null when it is not one of them.</summary>
    private int? RowIndex(VisualElement row)
    {
        if (row == null || _rows == null)
            return null;

        int index = _rows.IndexOf(row);
        return index >= 0 && index < _sessionIds.Count ? index : (int?)null;
    }

    /// <summary>Sets the sort, as if the reader had picked it in the dropdown.</summary>
    public void SetSort(string mode)
    {
        string wanted = string.IsNullOrEmpty(mode) ? SortNewest : mode;
        if (_sort != null && Array.IndexOf(SortChoices, wanted) >= 0)
            _sort.SetValueWithoutNotify(wanted);

        Rebuild();
    }

    /// <summary>Sets the search, as if the reader had typed it.</summary>
    public void SetSearch(string text)
    {
        if (_search != null)
            _search.SetValueWithoutNotify(text ?? string.Empty);

        Rebuild();
    }

    // -- construction ---------------------------------------------------------

    private VisualElement BuildHeader()
    {
        var header = new VisualElement();
        header.AddToClassList("analysis-rail-header");

        var title = new Label("Sessions");
        title.AddToClassList("analysis-rail-title");
        header.Add(title);

        _countLabel = new Label("0 sessions");
        _countLabel.AddToClassList("analysis-rail-count");
        header.Add(_countLabel);

        return header;
    }

    private VisualElement BuildControls()
    {
        var controls = new VisualElement();
        controls.AddToClassList("analysis-rail-controls");

        _search = new TextField();
        _search.AddToClassList("analysis-rail-field");
        _search.AddToClassList("analysis-rail-search");
        _search.textEdition.placeholder = "Search sessions";
        _search.tooltip = "Show only the sessions whose name, scenario or id holds this text";
        _search.RegisterValueChangedCallback(_ => Rebuild());
        // The label is typed at, not committed: filtering on every keystroke is what makes the field useful.
        _search.RegisterCallback<KeyUpEvent>(_ => Rebuild());
        controls.Add(_search);

        _sort = new DropdownField { choices = new List<string>(SortChoices), value = SortNewest };
        _sort.AddToClassList("analysis-rail-field");
        _sort.AddToClassList("analysis-rail-sort");
        _sort.tooltip = "Order the saved sessions; the session running now always comes first";
        _sort.RegisterValueChangedCallback(_ => Rebuild());
        controls.Add(_sort);

        return controls;
    }

    // -- data -----------------------------------------------------------------

    /// <summary>
    /// One line's worth of a session: what it is called, when it started, and the numbers its bar and its
    /// counts are drawn from. It is built fresh from <see cref="AnalysisSession.EpisodesOf"/> on every redraw
    /// and kept nowhere, so a line cannot outlive the episodes it describes.
    /// </summary>
    private sealed class Entry
    {
        /// <summary>Id of the saved session, or null for the one running now.</summary>
        public string Id;

        /// <summary>The label computed from the session's episodes, which is what the Scenario A-Z sort reads.</summary>
        public string Name;

        /// <summary>What the line prints: the name the user gave the session, or <see cref="Name"/>.</summary>
        public string DisplayName;

        public string StartedAt;
        public int Episodes;
        public int Goals;
        public int Others;

        /// <summary>Outcome to count, one pair per outcome that happened, in the canonical order.</summary>
        public readonly List<KeyValuePair<string, int>> Segments = new List<KeyValuePair<string, int>>();

        /// <summary>The text a search looks through: the id, the name and every scenario the session ran.</summary>
        public string Search;

        /// <summary>The start instant as a comparable value; the minimum when the line carried none.</summary>
        public DateTime SortInstant;
    }

    private void OnSessionChanged() => Rebuild();

    private void Rebuild()
    {
        if (_rows == null)
            return;

        _rows.Clear();
        _sessionIds.Clear();

        if (_session == null)
        {
            RefreshCount(0);
            return;
        }

        // The running session is the session the reader is in, so it keeps the first line whatever the sort
        // says. Only the saved ones are ordered.
        Entry running = BuildEntry(
            null,
            _session.Store?.SessionName,
            _session.Store?.StartedAt,
            _session.EpisodesOf(_session.RunningSessionId));
        var saved = new List<Entry>();
        foreach (ArchiveSessionRecord record in _session.SavedSessions)
            saved.Add(BuildEntry(record.Id, record.Name, record.StartedAt, _session.EpisodesOf(record.Id)));

        saved.Sort(CompareBy(_sort?.value ?? SortNewest));

        string query = _search?.value;

        int displayed = 0;
        AddLine(running, query, ref displayed);
        foreach (Entry entry in saved)
            AddLine(entry, query, ref displayed);

        if (displayed == 0)
        {
            var empty = new Label(string.IsNullOrWhiteSpace(query)
                ? "No session has run yet."
                : "No session matches this search.");
            empty.AddToClassList("analysis-rail-empty");
            _rows.Add(empty);
        }

        RefreshCount(displayed);
    }

    private void AddLine(Entry entry, string query, ref int displayed)
    {
        // A session with no episode behind it is not a session a reader can open: an export writes its line
        // before the first episode lands, and the session running now is empty until its first run ends. Both
        // are rows that would open on nothing, so neither is drawn - the rail says it has nothing to show
        // instead, the same way it does for a search that matched none.
        if (entry.Episodes == 0 || !Matches(entry, query))
            return;

        _rows.Add(BuildRow(entry));
        _sessionIds.Add(entry.Id);
        displayed++;
    }

    private void RefreshCount(int displayed)
        => _countLabel.text = displayed == 1 ? "1 session" : displayed + " sessions";

    private Entry BuildEntry(
        string id, string sessionName, string startedAt, IReadOnlyList<EpisodeMetrics> episodes)
    {
        var scenarios = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int goals = 0;

        foreach (EpisodeMetrics episode in episodes)
        {
            string scenario = episode?.Scenario;
            if (!string.IsNullOrEmpty(scenario) && !scenarios.Contains(scenario))
                scenarios.Add(scenario);

            string outcome = OutcomeKey(episode?.Outcome);
            if (outcome == MetricsContract.OutcomeGoal)
                goals++;

            counts[outcome] = counts.TryGetValue(outcome, out int seen) ? seen + 1 : 1;
        }

        string computed = AnalysisFormatting.ComputedSessionLabel(episodes);
        string display = AnalysisFormatting.SessionLabel(sessionName, episodes);

        string search = (id ?? "running") + " " + display;
        if (scenarios.Count > 0)
            search += " " + string.Join(" ", scenarios);

        var entry = new Entry
        {
            Id = id,
            Name = computed,
            DisplayName = display,
            StartedAt = startedAt,
            Episodes = episodes.Count,
            Goals = goals,
            Others = episodes.Count - goals,
            SortInstant = Instant(startedAt),
            Search = search,
        };

        foreach (string outcome in OutcomeOrder)
        {
            if (counts.TryGetValue(outcome, out int count) && count > 0)
                entry.Segments.Add(new KeyValuePair<string, int>(outcome, count));
        }

        return entry;
    }

    /// <summary>
    /// The outcome bucket an episode falls in. Anything the project vocabulary does not name is "unknown"
    /// rather than a bucket of its own, so the bar never grows a segment nobody can label.
    /// </summary>
    private static string OutcomeKey(string outcome)
        => string.IsNullOrEmpty(outcome) || Array.IndexOf(OutcomeOrder, outcome) < 0
            ? MetricsContract.OutcomeUnknown
            : outcome;

    /// <summary>The instant a session line names, or the minimum when it names none a reader can place.</summary>
    private static DateTime Instant(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
            return DateTime.MinValue;

        return DateTime.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static Comparison<Entry> CompareBy(string mode)
    {
        switch (mode)
        {
            case SortOldest:
                return (a, b) => ThenOldest(a.SortInstant.CompareTo(b.SortInstant), a, b);

            case SortMostEpisodes:
                return (a, b) => ThenNewest(b.Episodes.CompareTo(a.Episodes), a, b);

            case SortBestSuccess:
                return (a, b) => ThenNewest(Success(b).CompareTo(Success(a)), a, b);

            case SortScenario:
                return (a, b) => ThenNewest(string.CompareOrdinal(a.Name, b.Name), a, b);

            default:
                return (a, b) => ThenNewest(0, a, b);
        }
    }

    /// <summary>Ties between two equal-enough sessions break on the most recent one, as the default order does.</summary>
    private static int ThenNewest(int primary, Entry a, Entry b)
    {
        if (primary != 0)
            return primary;

        int byDate = b.SortInstant.CompareTo(a.SortInstant);
        return byDate != 0 ? byDate : string.CompareOrdinal(a.Id, b.Id);
    }

    private static int ThenOldest(int primary, Entry a, Entry b)
    {
        if (primary != 0)
            return primary;

        int byDate = a.SortInstant.CompareTo(b.SortInstant);
        return byDate != 0 ? byDate : string.CompareOrdinal(a.Id, b.Id);
    }

    private static double Success(Entry entry) => entry.Episodes > 0 ? (double)entry.Goals / entry.Episodes : 0.0;

    private static bool Matches(Entry entry, string query)
        => string.IsNullOrWhiteSpace(query) ||
           (entry.Search ?? string.Empty).IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;

    // -- drawing --------------------------------------------------------------

    private VisualElement BuildRow(Entry entry)
    {
        var row = new VisualElement();
        row.AddToClassList("analysis-rail-row");
        // The line's identity travels with it: null is the running session, never an empty id.
        row.userData = entry.Id;
        row.EnableInClassList("selected", IsDisplayed(entry));

        var top = new VisualElement();
        top.AddToClassList("analysis-rail-row-top");

        var name = new Label(entry.DisplayName);
        name.AddToClassList("analysis-rail-row-name");
        top.Add(name);

        // The same two glyphs an episode row carries, for the same two things a reader does to a session:
        // hand it over as a file, or remove it. They sit in the line's top corner so the counters below keep
        // the width to themselves.
        var actions = new VisualElement();
        actions.AddToClassList("analysis-row-actions");
        actions.AddToClassList("analysis-row-action");
        actions.Add(BuildIcon("analysis-row-icon-export", "Export this session",
            () => ClickExport(row)));
        actions.Add(BuildIcon("analysis-row-icon-delete", "Delete this session and all of its episodes",
            () => ClickDelete(row)));
        top.Add(actions);
        row.Add(top);

        var date = new Label(AnalysisFormatting.Timestamp(entry.StartedAt));
        date.AddToClassList("analysis-rail-row-date");
        row.Add(date);

        var count = new Label(entry.Episodes == 1 ? "1 episode" : entry.Episodes + " episodes");
        count.AddToClassList("analysis-rail-row-count");
        row.Add(count);

        var tally = new Label($"{entry.Goals} goal \u00B7 {entry.Others} other");
        tally.AddToClassList("analysis-rail-row-tally");
        row.Add(tally);

        row.Add(BuildBar(entry));

        row.RegisterCallback<ClickEvent>(evt => ClickRow(row, evt.target as VisualElement));
        return row;
    }

    /// <summary>One of the line's own glyphs: an unlabelled icon button that only does what its tooltip says.</summary>
    private static Button BuildIcon(string iconClass, string tooltip, Action clicked)
    {
        var button = new Button(clicked) { text = string.Empty };
        button.AddToClassList("analysis-row-icon");
        button.AddToClassList(iconClass);
        button.tooltip = tooltip;
        return button;
    }

    /// <summary>
    /// The results bar of one session: the classes the project already colours with - the track, the fill,
    /// and <see cref="AnalysisFormatting.OutcomeClass"/> - one segment per outcome that happened, each as wide
    /// as its share of the session's episodes. Only outcomes with at least one episode get a segment, so an
    /// all-goal session paints one green bar rather than a bar with slivers of every other colour.
    /// </summary>
    private static VisualElement BuildBar(Entry entry)
    {
        var track = new VisualElement();
        track.AddToClassList("analysis-bar-track");
        track.AddToClassList("analysis-rail-bar");

        int total = entry.Episodes;
        foreach (KeyValuePair<string, int> segment in entry.Segments)
        {
            var fill = new VisualElement();
            fill.AddToClassList("analysis-bar-fill");
            fill.AddToClassList(AnalysisFormatting.OutcomeClass(segment.Key));
            fill.style.width = Length.Percent(total > 0 ? (float)(100.0 * segment.Value / total) : 0f);
            track.Add(fill);
        }

        return track;
    }

    /// <summary>
    /// Whether the line names the session the tab currently has on screen. The running session is on screen
    /// whenever the displayed one is the running one, and a saved line when its id is the displayed source.
    /// </summary>
    private bool IsDisplayed(Entry entry)
    {
        if (_session == null)
            return false;

        return _session.IsRunningSession
            ? entry.Id == null
            : string.Equals(entry.Id, _session.SourceId, StringComparison.Ordinal);
    }
}
