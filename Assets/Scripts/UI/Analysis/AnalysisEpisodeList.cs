using System;
using System.Collections.Generic;
using RobotSNAP.Metrics;
using UnityEngine.UIElements;

/// <summary>
/// The episode list of the analysis session: one row per finished episode, newest first, with the filters that
/// decide which of them a reader is looking at.
///
/// A row carries the two things a reader does to one run - export it, delete it - and a click on the row is
/// the single selection the panels beside it read: show me this one. The actions live in the row rather than
/// in the panel that describes the run, because the action belongs to the episode the reader is pointing at,
/// and a benchmark is scanned by pointing at rows. A click that landed on an action is neither; see
/// <see cref="IsRowAction"/>.
///
/// The heading names the session the rows belong to and carries the pencil that renames it, so what the list
/// belongs to is read in the list rather than only in the rail beside it.
///
/// The list owns its subscription to the session and drops it in <see cref="Dispose"/>, so it is rebuilt from
/// the store every time an episode ends and never keeps a stale handler behind.
/// </summary>
public sealed class AnalysisEpisodeList : VisualElement
{
    /// <summary>Raised with the id of the episode the reader put the focus on - a row click.</summary>
    public event Action<string> EpisodeSelected;

    /// <summary>Raised with the id of an episode the reader asked to export.</summary>
    public event Action<string> ExportRequested;

    /// <summary>Raised with the id of an episode the reader asked to delete.</summary>
    public event Action<string> DeleteRequested;

    /// <summary>
    /// Raised with the name the reader committed for the session the rows belong to, or <c>null</c> when they
    /// emptied the field. Where that name goes belongs to the tab, not to the list.
    /// </summary>
    public event Action<string> SessionRenameRequested;

    /// <summary>Choice that clears a criterion, and the caption the scenario filter carries when it is alone.</summary>
    private const string AllOutcomes = "All outcomes";
    private const string AllScenarios = "All scenarios";

    /// <summary>Outcome vocabularies the filter offers, in the order the project reads them.</summary>
    private static readonly (string Label, string Value)[] OutcomeChoices =
    {
        (AllOutcomes, AnalysisEpisodeFilter.Any),
        ("Reached goal", MetricsContract.OutcomeGoal),
        ("Collision", MetricsContract.OutcomeCollision),
        ("Out of bounds", MetricsContract.OutcomeOutOfBounds),
        ("Timed out", MetricsContract.OutcomeTimeout),
        ("Stopped", MetricsContract.OutcomeStopped),
    };

    private Label _count;
    private AnalysisEditableTitle _title;
    private DropdownField _outcomeFilter;
    private DropdownField _scenarioFilter;
    private TextField _search;
    private Toggle _intrusionsOnly;
    private readonly VisualElement _rows;

    private readonly AnalysisEpisodeFilter _filter = new AnalysisEpisodeFilter();
    private readonly List<EpisodeMetrics> _visible = new List<EpisodeMetrics>();

    private AnalysisSession _session;

    public AnalysisEpisodeList()
    {
        AddToClassList("analysis-list");

        Add(BuildHeader());
        Add(BuildFilters());

        var scroll = new ScrollView();
        scroll.AddToClassList("analysis-list-scroll");
        _rows = new VisualElement();
        _rows.AddToClassList("analysis-list-rows");
        scroll.Add(_rows);
        Add(scroll);
    }

    /// <summary>The ids of the rows the filters currently show.</summary>
    public IReadOnlyList<string> VisibleIds
    {
        get
        {
            var ids = new List<string>(_visible.Count);
            foreach (EpisodeMetrics episode in _visible)
                ids.Add(episode.Id);
            return ids;
        }
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

    /// <summary>Detaches from the session; a disposed list never draws again.</summary>
    public void Dispose()
    {
        if (_session != null)
            _session.Changed -= OnSessionChanged;
        _session = null;
    }

    /// <summary>
    /// Whether a click that landed on <paramref name="target"/> is one of a row's own actions - its export
    /// glyph or its delete glyph - rather than a click on the row itself. An action does only what its
    /// tooltip says: exporting a run has never also meant "select it", and deleting one leaves the reader
    /// looking at whatever the list now holds instead of at the run that just left. The walk stops at the row
    /// it started in, so a glyph of the Sessions rail is judged the same way as one of these rows.
    /// </summary>
    public static bool IsRowAction(VisualElement target)
    {
        for (VisualElement element = target; element != null; element = element.parent)
        {
            if (element.ClassListContains("analysis-row-action"))
                return true;
            if (element.ClassListContains("analysis-episode-row") ||
                element.ClassListContains("analysis-rail-row"))
                return false;
        }
        return false;
    }

    /// <summary>Sets the outcome criterion, as if the reader had picked it in the dropdown.</summary>
    public void SetOutcomeFilter(string outcome)
    {
        for (int index = 0; index < OutcomeChoices.Length; index++)
        {
            if (string.Equals(OutcomeChoices[index].Value, outcome ?? AnalysisEpisodeFilter.Any,
                    StringComparison.Ordinal))
            {
                _outcomeFilter.index = index;
                ApplyFilters();
                return;
            }
        }
    }

    /// <summary>Sets the scenario criterion, as if the reader had picked it in the dropdown.</summary>
    public void SetScenarioFilter(string scenario)
    {
        string wanted = string.IsNullOrEmpty(scenario) ? AllScenarios : scenario;
        if (_scenarioFilter.choices != null && _scenarioFilter.choices.Contains(wanted))
        {
            _scenarioFilter.value = wanted;
            ApplyFilters();
        }
    }

    /// <summary>Sets the label search, as if the reader had typed it.</summary>
    public void SetSearch(string text)
    {
        _search.value = text ?? string.Empty;
        ApplyFilters();
    }

    /// <summary>Sets the personal-space criterion, as if the reader had ticked it.</summary>
    public void SetIntrusionsOnly(bool value)
    {
        _intrusionsOnly.value = value;
        ApplyFilters();
    }

    // -- construction ---------------------------------------------------------

    private VisualElement BuildHeader()
    {
        var header = new VisualElement();
        header.AddToClassList("analysis-list-header");

        // The session names itself in the heading of the panel that lists it, at the size of a heading, with
        // the pencil that renames it one click away.
        _title = new AnalysisEditableTitle();
        _title.AddToClassList("analysis-list-name");
        _title.RenameRequested += name => SessionRenameRequested?.Invoke(name);
        header.Add(_title);

        var caption = new VisualElement();
        caption.AddToClassList("analysis-list-caption");

        var title = new Label("Episodes");
        title.AddToClassList("analysis-list-title");
        caption.Add(title);

        _count = new Label("0");
        _count.AddToClassList("analysis-list-count");
        caption.Add(_count);
        header.Add(caption);

        return header;
    }

    private VisualElement BuildFilters()
    {
        var filters = new VisualElement();
        filters.AddToClassList("analysis-list-filters");

        var row = new VisualElement();
        row.AddToClassList("analysis-filter-row");

        var outcomeChoices = new List<string>(OutcomeChoices.Length);
        foreach (var (label, _) in OutcomeChoices)
            outcomeChoices.Add(label);

        _outcomeFilter = new DropdownField { choices = outcomeChoices, value = AllOutcomes };
        _outcomeFilter.AddToClassList("analysis-filter-field");
        _outcomeFilter.tooltip = "Show only the episodes that ended this way";
        _outcomeFilter.RegisterValueChangedCallback(_ => ApplyFilters());
        row.Add(_outcomeFilter);

        _scenarioFilter = new DropdownField { choices = new List<string> { AllScenarios }, value = AllScenarios };
        _scenarioFilter.AddToClassList("analysis-filter-field");
        _scenarioFilter.AddToClassList("analysis-filter-second");
        _scenarioFilter.tooltip = "Show only the episodes that ran this scenario";
        _scenarioFilter.RegisterValueChangedCallback(_ => ApplyFilters());
        _scenarioFilter.style.display = DisplayStyle.None;
        row.Add(_scenarioFilter);

        filters.Add(row);

        _search = new TextField();
        _search.AddToClassList("analysis-filter-field");
        _search.AddToClassList("analysis-filter-search");
        _search.textEdition.placeholder = "Search episodes";
        _search.RegisterValueChangedCallback(_ => ApplyFilters());
        // The label is typed at, not committed: filtering on every keystroke is what makes the field useful.
        _search.RegisterCallback<KeyUpEvent>(_ => ApplyFilters());
        filters.Add(_search);

        _intrusionsOnly = new Toggle("Personal space only");
        _intrusionsOnly.AddToClassList("analysis-filter-intrusions");
        _intrusionsOnly.tooltip =
            "Show only the episodes where the robot came within a human's personal-space disc (0.5 m by " +
            "default) at least once. A run that keeps its distance from the crowd legitimately has none, so " +
            "this filter can empty the list - clear it to see those runs again.";
        _intrusionsOnly.RegisterValueChangedCallback(_ => ApplyFilters());
        filters.Add(_intrusionsOnly);
        return filters;
    }

    // -- data -----------------------------------------------------------------

    private void OnSessionChanged()
    {
        if (_session == null)
            return;

        IReadOnlyList<EpisodeMetrics> episodes = _session.Episodes;
        RefreshTitle();
        RebuildScenarioChoices(episodes);
        ReadFilterFromControls();
        RefreshFiltered();
    }

    /// <summary>
    /// Names the heading after the session the rows belong to: the name the user gave it when there is one,
    /// and the label computed from its episodes otherwise - the same two the rail line prints.
    /// </summary>
    private void RefreshTitle()
    {
        if (_title == null)
            return;

        string name = null;
        if (_session.IsRunningSession)
        {
            name = _session.Store?.SessionName;
        }
        else
        {
            foreach (ArchiveSessionRecord record in _session.SavedSessions)
            {
                if (string.Equals(record.Id, _session.SourceId, StringComparison.Ordinal))
                {
                    name = record.Name;
                    break;
                }
            }
        }

        _title.Show(AnalysisFormatting.SessionLabel(name, _session.Episodes));
    }

    private void ApplyFilters()
    {
        ReadFilterFromControls();
        RefreshFiltered();
    }

    /// <summary>
    /// Reads the criteria off the controls. They are the source of truth, so a caller that set one of them -
    /// a click on the dropdown, a keystroke in the search, a test - gets the same answer as the reader who
    /// used it, whether or not the control announced the change.
    /// </summary>
    private void ReadFilterFromControls()
    {
        _filter.Outcome = OutcomeValue(_outcomeFilter.index);
        _filter.Scenario = _scenarioFilter.value == AllScenarios
            ? AnalysisEpisodeFilter.Any
            : _scenarioFilter.value;
        _filter.Search = _search.value;
        _filter.IntrusionsOnly = _intrusionsOnly.value;
    }

    private void RefreshFiltered()
    {
        if (_session == null)
            return;

        _visible.Clear();
        _visible.AddRange(_filter.Apply(_session.Episodes));
        RebuildRows();
        RefreshChrome();
    }

    private static string OutcomeValue(int index)
        => index >= 0 && index < OutcomeChoices.Length
            ? OutcomeChoices[index].Value
            : AnalysisEpisodeFilter.Any;

    /// <summary>
    /// Rebuilds the scenario choices from the session. The criterion is only offered when it can split the
    /// session in two - a dropdown whose only answer is "all of them" is a control that does nothing.
    /// </summary>
    private void RebuildScenarioChoices(IReadOnlyList<EpisodeMetrics> episodes)
    {
        var scenarios = new List<string>();
        foreach (EpisodeMetrics episode in episodes)
        {
            string scenario = episode.Scenario;
            if (!string.IsNullOrEmpty(scenario) && !scenarios.Contains(scenario))
                scenarios.Add(scenario);
        }
        scenarios.Sort(StringComparer.Ordinal);

        var choices = new List<string> { AllScenarios };
        choices.AddRange(scenarios);

        bool same = _scenarioFilter.choices != null && _scenarioFilter.choices.Count == choices.Count;
        if (same)
        {
            for (int index = 0; index < choices.Count; index++)
            {
                if (_scenarioFilter.choices[index] != choices[index])
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            string current = _scenarioFilter.value;
            _scenarioFilter.choices = choices;
            _scenarioFilter.SetValueWithoutNotify(choices.Contains(current) ? current : AllScenarios);
        }

        _scenarioFilter.style.display = scenarios.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void RebuildRows()
    {
        _rows.Clear();

        if (_visible.Count == 0)
        {
            bool anyEpisode = _session != null && _session.Episodes.Count > 0;
            string message;
            if (anyEpisode)
                message = "No episode matches these filters.";
            else if (_session != null && !_session.IsRunningSession)
                message = "This saved session holds no episode.";
            else
                message = "No episode yet. Run a scenario; an episode appears here as soon as it ends.";

            var empty = new Label(message);
            empty.AddToClassList("analysis-empty-message");
            _rows.Add(empty);
            return;
        }

        // Newest first: the run just finished is the one a reader is looking for.
        for (int index = _visible.Count - 1; index >= 0; index--)
            _rows.Add(BuildRow(_visible[index]));
    }

    private VisualElement BuildRow(EpisodeMetrics episode)
    {
        string id = episode.Id;

        var row = new VisualElement();
        row.AddToClassList("analysis-episode-row");
        row.userData = id;
        row.EnableInClassList("selected", IsFocused(id));

        var top = new VisualElement();
        top.AddToClassList("analysis-row-top");

        var label = new Label(AnalysisFormatting.EpisodeLabel(episode));
        label.AddToClassList("analysis-row-label");
        top.Add(label);

        var outcome = new Label(AnalysisFormatting.OutcomeName(episode.Outcome));
        outcome.AddToClassList("analysis-row-outcome");
        outcome.AddToClassList(AnalysisFormatting.OutcomeClass(episode.Outcome));
        top.Add(outcome);

        // Every row carries the two glyphs, whichever side of the archive the episode lives on: an episode
        // read back from a folder is exported and deleted through the same two files the running one uses.
        var actions = new VisualElement();
        actions.AddToClassList("analysis-row-actions");
        actions.AddToClassList("analysis-row-action");
        actions.Add(BuildIcon("analysis-row-icon-export", "Export this episode",
            () => ClickExport(row)));
        actions.Add(BuildIcon("analysis-row-icon-delete", "Delete this episode",
            () => ClickDelete(row)));
        top.Add(actions);
        row.Add(top);

        var meta = new VisualElement();
        meta.AddToClassList("analysis-row-meta");
        meta.Add(MetaLabel(string.IsNullOrEmpty(episode.Robot) ? "robot" : episode.Robot));
        meta.Add(MetaLabel(AnalysisFormatting.Seconds(episode.WorldSeconds) + " world"));
        meta.Add(MetaLabel(AnalysisFormatting.Timestamp(episode.StartedAt)));
        row.Add(meta);

        row.RegisterCallback<ClickEvent>(evt =>
            ClickRow(row, evt.target as VisualElement));
        return row;
    }

    /// <summary>
    /// What a click on <paramref name="row"/> does, given what it landed on. A click on one of the row's own
    /// actions is not a selection: exporting a run is not asking to look at it, and deleting one is not asking
    /// to look at what just left. Split out from the callback so the rule can be read - and tested - without a
    /// live event system dispatching a real click.
    /// </summary>
    public void ClickRow(VisualElement row, VisualElement target)
    {
        if (row == null || IsRowAction(target))
            return;

        if (row.userData is string id && !string.IsNullOrEmpty(id))
            Focus(id);
    }

    /// <summary>
    /// What the export glyph of one row does: hands the episode it names to whoever owns the export. Split out
    /// from the callback so the rule can be read - and tested - without a live event system dispatching a real
    /// click.
    /// </summary>
    public void ClickExport(VisualElement row)
    {
        if (row?.userData is string id && !string.IsNullOrEmpty(id))
            ExportRequested?.Invoke(id);
    }

    /// <summary>What the delete glyph of one row does: asks for the episode it names to be removed.</summary>
    public void ClickDelete(VisualElement row)
    {
        if (row?.userData is string id && !string.IsNullOrEmpty(id))
            DeleteRequested?.Invoke(id);
    }

    private static Button BuildIcon(string iconClass, string tooltip, Action clicked)
    {
        var button = new Button(clicked) { text = string.Empty };
        button.AddToClassList("analysis-row-icon");
        button.AddToClassList(iconClass);
        button.tooltip = tooltip;
        return button;
    }

    /// <summary>A click on the row is a single selection: this run, and only this run.</summary>
    private void Focus(string id)
    {
        RefreshChrome();
        EpisodeSelected?.Invoke(id);
    }

    private bool IsFocused(string id)
        => _session?.Selected != null && string.Equals(_session.Selected.Id, id, StringComparison.Ordinal);

    /// <summary>The count, and which row the list points at.</summary>
    private void RefreshChrome()
    {
        int total = _session != null ? _session.Episodes.Count : 0;
        bool filtered = _filter.IsActive;

        _count.text = filtered
            ? $"{_visible.Count} of {total}"
            : total == 1 ? "1 episode" : total + " episodes";

        string focusedId = _session?.Selected?.Id;
        foreach (VisualElement row in _rows.Children())
        {
            if (row.userData is not string id)
                continue;

            row.EnableInClassList("selected", string.Equals(id, focusedId, StringComparison.Ordinal));
        }
    }

    private static Label MetaLabel(string text)
    {
        var label = new Label(text);
        label.AddToClassList("analysis-row-meta-item");
        return label;
    }
}
