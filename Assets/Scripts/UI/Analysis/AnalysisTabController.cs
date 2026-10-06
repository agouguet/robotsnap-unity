using System;
using System.Collections.Generic;
using System.IO;
using RobotSNAP.Metrics;
using UnityEngine;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Wires the Analysis tab: the sessions rail, the episode list, the trajectory map and the panel beside it -
/// the metrics of the episode the list points at, under the overview of the session it belongs to - all
/// reading the session's <see cref="MetricsStore"/> and the archives it reads back.
///
/// The tab owns what the views only report as intents: choosing the export folder, asking before a delete, and
/// where a renamed title goes. Keeping that here is what lets the rail and the list stay views of the data
/// instead of owners of a policy.
///
/// It initializes lazily, the first time the tab is opened, because that is when the UXML template is
/// instantiated into the UIDocument - the same reason the other tab controllers wait for their own view.
/// Once built it watches the store from <c>Update</c> so an episode that ends while the tab is open shows up
/// without the reader doing anything.
/// </summary>
public class AnalysisTabController : MonoBehaviour
{
    [SerializeField] private UIDocument uiDocument;

    private bool _initialized;
    private VisualElement _page;
    private AnalysisSession _session;
    private AnalysisEpisodeList _list;
    private AnalysisSessionRail _rail;
    private AnalysisEpisodeMap _map;
    private AnalysisSessionSummary _summary;
    private AnalysisEpisodeDetail _detail;
    private AnalysisEpisodeReplay _replay;
    private AnalysisTimeline _timeline;
    private AnalysisEditableTitle _episodeTitle;
    private VisualElement _mapPanel;
    private VisualElement _detailColumn;
    private VisualElement _timelineHost;
    private VisualElement _detailHost;
    private VisualElement _summaryHost;
    private Button _openFolderButton;
    private Label _status;
    private string _statusSource;

    private void OnEnable()
    {
        MainViewController.OnViewLoaded += OnViewLoaded;
        TryInitialize();
    }

    private void OnDisable()
    {
        MainViewController.OnViewLoaded -= OnViewLoaded;
    }

    private void OnDestroy()
    {
        Detach();
    }

    /// <summary>
    /// A finished episode is a pull of two comparisons, so polling every frame costs nothing and spares the
    /// metrics layer an event it does not have.
    /// </summary>
    private void Update()
    {
        // The tab is torn down and rebuilt on its own schedule, and Unity can tick a frame while the view it
        // was built on is gone. Polling nothing is not a failure - there is simply nothing to poll yet.
        if (_initialized && _session != null)
            _session.Poll();

        // Playback is measured in real seconds and never in the simulator's. Replaying an episode is a
        // reading of the record, not a run of the world, so the time scale - which the reader may have set to
        // study a live run - has nothing to say about how fast the record is read back.
        _replay?.Advance(Time.unscaledDeltaTime);
    }

    private void OnViewLoaded(string viewName)
    {
        if (viewName != "Analysis")
            return;

        TryInitialize();
        _session?.Refresh();
    }

    private void TryInitialize()
    {
        if (_initialized)
            return;

        UIDocument document = ResolveDocument();
        if (document == null)
            return;

        VisualElement root = document.rootVisualElement;
        if (root == null || root.Q<VisualElement>("AnalysisPage") == null)
            return; // The tab has not been shown yet, so its template is not in the tree.

        var missing = new List<string>();
        _page = Require<VisualElement>(root, "AnalysisPage", missing);
        _openFolderButton = Require<Button>(root, "AnalysisOpenFolderButton", missing);
        _status = Require<Label>(root, "AnalysisExportStatus", missing);
        VisualElement railHost = Require<VisualElement>(root, "AnalysisSessionRailHost", missing);
        VisualElement listHost = Require<VisualElement>(root, "AnalysisEpisodeListHost", missing);
        VisualElement detailHost = Require<VisualElement>(root, "AnalysisEpisodeDetailHost", missing);
        VisualElement summaryHost = Require<VisualElement>(root, "AnalysisSessionSummaryHost", missing);
        VisualElement episodeTitleHost = Require<VisualElement>(root, "AnalysisEpisodeTitleHost", missing);
        _mapPanel = Require<VisualElement>(root, "AnalysisMapPanel", missing);
        _detailColumn = Require<VisualElement>(root, "AnalysisDetailColumn", missing);
        VisualElement timelineHost = Require<VisualElement>(root, "AnalysisTimelineHost", missing);
        if (missing.Count > 0)
        {
            Debug.LogError($"[AnalysisTabController] Missing UI elements: {string.Join(", ", missing)}");
            return;
        }

        _session = new AnalysisSession();

        _detailHost = detailHost;
        _summaryHost = summaryHost;
        _timelineHost = timelineHost;

        _map = new AnalysisEpisodeMap(root);
        _rail = new AnalysisSessionRail();
        railHost.Add(_rail);
        _list = new AnalysisEpisodeList();
        listHost.Add(_list);
        _detail = new AnalysisEpisodeDetail(detailHost);
        _summary = new AnalysisSessionSummary(summaryHost);
        _episodeTitle = BuildEpisodeTitle(episodeTitleHost);

        // The cursor the map, the numbers and the frise all read: one object, so the three of them can only
        // ever be talking about the same instant.
        _replay = new AnalysisEpisodeReplay();
        _timeline = new AnalysisTimeline();
        _timelineHost.Add(_timeline);

        _rail.SessionPicked += OnSessionPicked;
        _rail.SessionExportRequested += OnExportSessionRequested;
        _rail.SessionDeleteRequested += OnDeleteSessionRequested;
        _list.EpisodeSelected += OnEpisodeSelected;
        _list.ExportRequested += OnExportEpisodeRequested;
        _list.DeleteRequested += OnDeleteEpisodeRequested;
        _list.SessionRenameRequested += OnSessionRenameRequested;
        _episodeTitle.RenameRequested += OnEpisodeRenameRequested;
        _session.Changed += RefreshView;
        _map.Bind(_session);
        _map.SetReplay(_replay);
        _rail.Bind(_session);
        _list.Bind(_session);
        _detail.Bind(_session);
        _detail.SetReplay(_replay);
        _timeline.Bind(_replay);
        _timeline.SeekRequested += OnSeekRequested;
        _timeline.StepRequested += OnStepRequested;
        _timeline.StepBackRequested += OnStepBackRequested;
        _timeline.RewindRequested += OnRewindRequested;
        _timeline.TogglePlayRequested += OnTogglePlayRequested;
        RefreshView();

        _openFolderButton.clicked += OnOpenFolderClicked;
        _initialized = true;
    }

    /// <summary>
    /// The UIDocument is authored on the HUD, not on this object, so it is resolved as the one whose tree
    /// actually holds the analysis page. That keeps the controller inert until its view exists instead of
    /// failing on a document that happens to be found first.
    /// </summary>
    private UIDocument ResolveDocument()
    {
        if (uiDocument != null)
            return uiDocument;

        uiDocument = GetComponent<UIDocument>();
        if (uiDocument != null)
            return uiDocument;

        foreach (UIDocument candidate in FindObjectsByType<UIDocument>())
        {
            VisualElement candidateRoot = candidate.rootVisualElement;
            if (candidateRoot != null && candidateRoot.Q<VisualElement>("AnalysisPage") != null)
            {
                uiDocument = candidate;
                break;
            }
        }

        return uiDocument;
    }

    private static T Require<T>(VisualElement root, string name, ICollection<string> missing) where T : VisualElement
    {
        T element = root.Q<T>(name);
        if (element == null)
            missing.Add(name);
        return element;
    }

    private void OnEpisodeSelected(string id)
    {
        _session.Select(id);
    }

    /// <summary>
    /// Reads one line of the Sessions rail. A null id is the session the recorder is still appending to, and
    /// any other id is a session read back from the archive - the same two destinations the dropdown this rail
    /// replaced offered, asked for by id rather than by the caption the line happens to print.
    /// </summary>
    private void OnSessionPicked(string sessionId)
    {
        if (_session == null)
            return;

        if (sessionId == null)
            _session.ShowRunning();
        else
            _session.ShowSaved(sessionId);
    }

    /// <summary>Names the session on screen after what the reader typed in the episodes heading.</summary>
    private void OnSessionRenameRequested(string name)
    {
        RenameSession(_session, name);
    }

    /// <summary>Names the episode the trajectory panel is about after what the reader typed in its heading.</summary>
    private void OnEpisodeRenameRequested(string name)
    {
        RenameEpisode(_session, _session?.Selected?.Id, name);
    }

    /// <summary>
    /// Puts the right-hand column in the shape the selection asks for: the episode the list points at, metric
    /// by metric, beside its trajectory and under the frise that replays it - and, whatever the selection is,
    /// the overview of the whole session beneath it.
    ///
    /// The overview is not scoped to the episode on screen: the per-outcome counts are what a reader follows
    /// across a session, and an average over one run would be that run rather than the session. When no
    /// episode is selected there is no trajectory to draw, so the column takes the band and the overview is
    /// what is left to read.
    /// </summary>
    private void RefreshView()
    {
        if (_session == null || _summary == null)
            return;

        RefreshSourceChrome();

        EpisodeMetrics selected = _session.Selected;
        bool hasEpisode = selected != null;

        _detailHost.style.display = hasEpisode ? DisplayStyle.Flex : DisplayStyle.None;
        _summaryHost.style.display = DisplayStyle.Flex;
        _timelineHost.style.display = hasEpisode ? DisplayStyle.Flex : DisplayStyle.None;
        _mapPanel.style.display = hasEpisode ? DisplayStyle.Flex : DisplayStyle.None;
        // With the map gone the detail column is all that is left beside the list, so it takes the band.
        _detailColumn.EnableInClassList("analysis-detail-column-wide", !hasEpisode);
        // And in a band that wide the overview reads across rather than down: the tiles answer "how well" and
        // the outcome mix answers "how did it end", which are two columns of the same reading, not a stack.
        _summaryHost.EnableInClassList("analysis-summary-wide", !hasEpisode);

        _episodeTitle?.Show(hasEpisode ? AnalysisFormatting.EpisodeLabel(selected) : null);

        // No cursor outside the one-episode view: a frise would be replaying a run the panels beside it are
        // not describing.
        _replay.Show(hasEpisode ? selected : null);
        _summary.Show(_session.Episodes, AnalysisSessionSummary.WholeSession);
    }

    /// <summary>
    /// Makes the status line follow the source on screen: the running session by name, and a saved one by when
    /// it started and how many episodes it holds. It is only rewritten when the source actually changes, so
    /// the path "Open folder" just left there survives a click on a row.
    /// </summary>
    private void RefreshSourceChrome()
    {
        _openFolderButton.tooltip = "Open the folder the sessions are written to";

        string source = SourceStatus(_session);
        if (string.Equals(source, _statusSource, StringComparison.Ordinal))
            return;

        _statusSource = source;
        _status.text = source;
    }

    /// <summary>
    /// What the status line says about the session being displayed: the running one by name, and a saved one
    /// by when it started and how many episodes it holds - the same caption the rail offers it under.
    /// </summary>
    public static string SourceStatus(AnalysisSession session)
    {
        if (session == null)
            return string.Empty;

        if (session.IsRunningSession)
            return AnalysisSession.RunningSessionLabel;

        foreach (ArchiveSessionRecord record in session.SavedSessions)
        {
            if (string.Equals(record.Id, session.SourceId, StringComparison.Ordinal))
                return "Saved session " + AnalysisSessionRail.SavedSessionLabel(record);
        }

        return "Saved session " + session.SourceId;
    }

    /// <summary>
    /// Which of the two right-hand panels the tab shows. With no multi-selection left, both questions are the
    /// same one: is there an episode selected at all? An episode is the whole of what the trajectory map and
    /// the metric panel can describe, and nothing selected leaves the session overview alone in the column.
    /// </summary>
    public static bool ShowsEpisodeDetail(bool hasEpisode) => hasEpisode;

    /// <summary>Whether the band carries a trajectory map, which it does exactly while an episode is selected.</summary>
    public static bool ShowsTrajectoryMap(bool hasEpisode) => hasEpisode;

    // -- replay ---------------------------------------------------------------

    /// <summary>
    /// The transport's five intents. Taking hold of the frise and stepping it stop playback, because a reader
    /// who moves the cursor is reading one instant rather than watching the run; play is what starts it again.
    /// </summary>
    private void OnSeekRequested(double seconds)
    {
        _replay?.Pause();
        _replay?.Seek(seconds);
    }

    private void OnStepRequested()
    {
        _replay?.Pause();
        _replay?.Step();
    }

    private void OnStepBackRequested()
    {
        _replay?.Pause();
        _replay?.StepBack();
    }

    private void OnRewindRequested()
    {
        _replay?.Pause();
        _replay?.Rewind();
    }

    private void OnTogglePlayRequested() => _replay?.TogglePlay();

    // -- the export folder ----------------------------------------------------

    /// <summary>
    /// Opens the folder the sessions are written to in the file manager of the machine, and names it in the
    /// status line either way: the path is what a reader copies out of the tab when nothing opens - a missing
    /// file manager, a remote session, a folder this build is not allowed to show.
    /// </summary>
    private void OnOpenFolderClicked()
    {
        string path = AnalysisExportFolder.Last;
        try
        {
            // The folder is where the next export goes, so a reader who opens it before ever exporting gets
            // the folder that export would create rather than an error about it not being there yet.
            Directory.CreateDirectory(path);
        }
        catch (Exception)
        {
            // A folder that cannot be made is still worth naming: the status line is the one thing this
            // button can always leave behind.
        }

        _status.text = path;
#if UNITY_EDITOR
        EditorUtility.RevealInFinder(path);
#else
        Application.OpenURL("file://" + path);
#endif
    }

    /// <summary>
    /// Runs <paramref name="export"/> with a folder the user picked. In the editor that is the native folder
    /// picker; a player has none without a plugin, so with no dialog already open it asks with a field
    /// carrying the last folder, and with one open it writes to that last folder - two stacked modals would
    /// be worse than a documented fallback.
    /// </summary>
    private void ChooseExportFolder(Action<string> export, ConfirmationDialog parent = null)
    {
#if UNITY_EDITOR
        string folder = EditorUtility.SaveFolderPanel(
            "Choose the folder the metrics are exported to",
            AnalysisExportFolder.Last,
            string.Empty);

        if (string.IsNullOrEmpty(folder))
            return; // Closing the picker is a decision, not a failure.

        AnalysisExportFolder.Last = folder;
        export(folder);
#else
        if (parent != null)
        {
            export(AnalysisExportFolder.Last);
            return;
        }

        ShowExportFolderDialog(export);
#endif
    }

    /// <summary>
    /// The outline of a finished export, or the reason it did not happen. The write itself belongs to
    /// <see cref="MetricsExporter"/>; this only reports its outcome.
    /// </summary>
    private static string Describe(MetricsExportReport report, string subject)
    {
        if (string.IsNullOrEmpty(report.Directory))
            return "Export failed: " + (MetricsExporter.LastError ?? "unknown error");

        return report.UsedFallback
            ? $"Exported {subject} to {report.Directory} (the requested folder was not writable)"
            : $"Exported {subject} to {report.Directory}";
    }

    /// <summary>Exports one episode on its own, to the folder the user picks.</summary>
    private void OnExportEpisodeRequested(string id)
    {
        if (_session == null)
            return;

        ChooseExportFolder(folder =>
        {
            MetricsExportReport report = ExportEpisode(_session, id, folder);
            _status.text = Describe(report, "episode " + id);
        });
    }

    /// <summary>Exports one whole session - the running one when the rail publishes no id - to a picked folder.</summary>
    private void OnExportSessionRequested(string sessionId)
    {
        if (_session == null)
            return;

        ChooseExportFolder(folder =>
        {
            MetricsExportReport report = ExportSession(_session, sessionId, folder);
            IReadOnlyList<EpisodeMetrics> episodes = sessionId == null
                ? _session.EpisodesOf(_session.RunningSessionId)
                : _session.EpisodesOf(sessionId);
            int count = episodes.Count;
            _status.text = Describe(report, $"{count} episode{(count == 1 ? "" : "s")}");
        });
    }

    // -- the actions a card carries -------------------------------------------

    /// <summary>
    /// Writes one session to disk as one document. A null id is the session running now; a saved one is read
    /// from the record the archive named it under, because that record is where its start instant lives.
    /// </summary>
    public static MetricsExportReport ExportSession(AnalysisSession session, string sessionId, string folder)
    {
        if (session == null)
            return default;

        if (string.IsNullOrEmpty(sessionId) ||
            string.Equals(sessionId, session.Store.SessionId, StringComparison.Ordinal))
        {
            return MetricsExporter.ExportSession(
                session.EpisodesOf(session.RunningSessionId),
                session.Store.SessionId,
                session.Store.StartedAt,
                folder);
        }

        ArchiveSessionRecord record = RecordOf(session, sessionId);
        if (record == null)
            return default;

        return MetricsExporter.ExportSession(session.EpisodesOf(record.Id), record.Id, record.StartedAt, folder);
    }

    /// <summary>
    /// Exports one episode of the session on screen. The episode is handed over as the object it is - the
    /// store's copy or the one read back from an archive - because the trajectory of a replayed run is read
    /// through the archive the episode names.
    /// </summary>
    public static MetricsExportReport ExportEpisode(AnalysisSession session, string episodeId, string folder)
    {
        if (session == null)
            return default;

        EpisodeMetrics episode = FindEpisode(session, episodeId);
        return episode != null
            ? MetricsExporter.ExportEpisode(session.Store, episode, folder)
            : MetricsExporter.ExportEpisode(session.Store, episodeId, folder);
    }

    /// <summary>
    /// Deletes one episode, from everywhere the tab can see it: the store while it is the recorder's, and
    /// every folder that holds its record - the one it was read back from when the episode names it, and the
    /// folders the tab reads while it is one the recorder is still appending to.
    /// </summary>
    public static void DeleteEpisode(AnalysisSession session, string episodeId)
    {
        if (session == null || string.IsNullOrEmpty(episodeId))
            return;

        EpisodeMetrics episode = FindEpisode(session, episodeId);
        if (episode == null)
            return;

        // Removing an id the store does not hold is not a failure: it is an episode read back from an archive.
        session.Store.Remove(episodeId);
        foreach (string root in RootsOf(session, episode))
            TrajectoryArchive.DeleteEpisode(root, episodeId);

        session.Refresh();
        FallBackToRunning(session);
    }

    /// <summary>
    /// Deletes a whole session and everything it filed: its catalogue lines, its session line and its
    /// trajectory archive. A null id means the session running now, which is emptied as well - the id it
    /// leaves behind is a new session rather than the one whose files just went. When the session on screen is
    /// the one that disappeared, the tab goes back to the running session rather than staying on nothing.
    /// </summary>
    public static void DeleteSession(AnalysisSession session, string sessionId)
    {
        if (session == null)
            return;

        bool running = string.IsNullOrEmpty(sessionId) ||
                       string.Equals(sessionId, session.Store.SessionId, StringComparison.Ordinal);

        if (running)
        {
            foreach (string root in session.ArchiveRoots)
                TrajectoryArchive.DeleteSession(root, session.Store.SessionId);

            session.Store.Clear();
            session.Store.ForgetArchive();
            session.Refresh();
            return;
        }

        foreach (string root in RootsOfSession(session, sessionId))
            TrajectoryArchive.DeleteSession(root, sessionId);

        session.Refresh();
        FallBackToRunning(session);
    }

    /// <summary>
    /// Names the session on screen, or takes the name away again when <paramref name="name"/> is empty. The
    /// running session keeps its name in the store - it has no document yet - and the folders it was exported
    /// to carry it in their session line; a session read back from an archive is renamed in the one folder
    /// that named it.
    /// </summary>
    public static void RenameSession(AnalysisSession session, string name)
    {
        if (session == null)
            return;

        string value = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (session.IsRunningSession)
        {
            session.Store.SessionName = value ?? string.Empty;
            foreach (string root in session.ArchiveRoots)
                TrajectoryArchive.RenameSession(root, session.Store.SessionId, value);
        }
        else
        {
            ArchiveSessionRecord record = RecordOf(session, session.SourceId);
            if (record != null)
                TrajectoryArchive.RenameSession(record.Root, record.Id, value);
        }

        session.Refresh();
    }

    /// <summary>
    /// Names one episode, or takes its name away again when <paramref name="name"/> is empty. The name always
    /// lands on the copy the tab holds; it also goes to the archive the episode was read from, and - for a run
    /// the recorder is still appending to - to the folders it may already have been exported to, where the
    /// catalogue line is what a later reader would otherwise open under the old name. A run that has not been
    /// archived yet keeps the name in memory, and the next export writes it.
    /// </summary>
    public static void RenameEpisode(AnalysisSession session, string episodeId, string name)
    {
        if (session == null)
            return;

        EpisodeMetrics episode = FindEpisode(session, episodeId);
        if (episode == null)
            return;

        string value = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        episode.Name = value;

        foreach (string root in RootsOf(session, episode))
            TrajectoryArchive.RenameEpisode(root, episode.Id, value);

        session.Refresh();
    }

    /// <summary>
    /// Builds the episode heading the trajectory panel heads with: the run's name, and the pencil that renames
    /// it. Public and static so that heading can be laid out - and read - without a live UIDocument.
    /// </summary>
    public static AnalysisEditableTitle BuildEpisodeTitle(VisualElement host)
    {
        var title = new AnalysisEditableTitle();
        title.AddToClassList("analysis-episode-name");
        host?.Add(title);
        return title;
    }

    /// <summary>The episode of the session on screen whose id this is, or null when it is not one of them.</summary>
    private static EpisodeMetrics FindEpisode(AnalysisSession session, string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        foreach (EpisodeMetrics episode in session.Episodes)
        {
            if (string.Equals(episode.Id, id, StringComparison.Ordinal))
                return episode;
        }
        return null;
    }

    /// <summary>The session record an id names, or null when the archive the tab reads does not hold it.</summary>
    private static ArchiveSessionRecord RecordOf(AnalysisSession session, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        foreach (ArchiveSessionRecord record in session.SavedSessions)
        {
            if (string.Equals(record.Id, sessionId, StringComparison.Ordinal))
                return record;
        }
        return null;
    }

    /// <summary>
    /// Goes back to the session the recorder is still appending to when the one on screen has just
    /// disappeared - a session that was deleted, or one whose last episode went and took its line with it.
    /// Staying on a session the rail no longer offers would leave the panels describing nothing.
    /// </summary>
    private static void FallBackToRunning(AnalysisSession session)
    {
        if (!session.IsRunningSession && !session.HasSavedSession(session.SourceId))
            session.ShowRunning();
    }

    /// <summary>
    /// The folders an episode's record can live in. One read back from an archive names its own folder, and
    /// that folder wins: the tab may read several, and only the one that wrote the record is where it sits.
    /// An episode the recorder still holds has no folder of its own yet, so every folder the tab reads is
    /// asked - the one the reader last exported to, and the fallback an unwritable one was redirected to.
    /// </summary>
    private static IEnumerable<string> RootsOf(AnalysisSession session, EpisodeMetrics episode)
    {
        if (!string.IsNullOrEmpty(episode.ArchiveRoot))
        {
            yield return episode.ArchiveRoot;
            yield break;
        }

        foreach (string root in session.ArchiveRoots)
            yield return root;
    }

    /// <summary>
    /// The folders a session's files can live in: the one its record names while the tab still lists it, and
    /// every folder the tab reads when the record went with an earlier delete.
    /// </summary>
    private static IEnumerable<string> RootsOfSession(AnalysisSession session, string sessionId)
    {
        ArchiveSessionRecord record = RecordOf(session, sessionId);
        if (record != null)
        {
            yield return record.Root;
            yield break;
        }

        foreach (string root in session.ArchiveRoots)
            yield return root;
    }

    // -- destructive actions --------------------------------------------------

    /// <summary>
    /// Deletes one episode, after a confirmation that says what goes and where from. The episode is looked up
    /// again on confirmation rather than captured: the reader may have moved to another session while the
    /// question was up, so the answer is about the session they are looking at.
    /// </summary>
    private void OnDeleteEpisodeRequested(string id)
    {
        EpisodeMetrics episode = _session == null ? null : FindEpisode(_session, id);
        if (episode == null)
            return;

        ConfirmationDialog dialog = new ConfirmationDialog(
            "Delete this episode?",
            $"This removes episode {AnalysisFormatting.EpisodeLabel(episode)} from the session and deletes its " +
            "record and trajectory from the folder it was saved in. It cannot be undone. Export first if you " +
            "want to keep it.",
            "Delete episode",
            alternateText: "Export first");

        dialog.Alternate += () => ExportBeforeDestructiveAction(
            dialog, folder => ExportEpisode(_session, id, folder));
        dialog.Confirmed += () => DeleteEpisode(_session, id);

        dialog.Show(_page);
    }

    /// <summary>
    /// Deletes one whole session, after a confirmation that says in counts what leaves the disk. A null id is
    /// the session running now, whose episodes are dropped from the store as well - the two halves of the same
    /// session a reader asked to be rid of.
    /// </summary>
    private void OnDeleteSessionRequested(string sessionId)
    {
        if (_session == null)
            return;

        bool running = sessionId == null;
        int episodes = running
            ? _session.EpisodesOf(_session.RunningSessionId).Count
            : _session.EpisodesOf(sessionId).Count;

        string subject = running ? "the session running now" : "this session";
        ConfirmationDialog dialog = new ConfirmationDialog(
            "Delete this session?",
            $"This deletes {subject} and all {episodes} of its episode{(episodes == 1 ? "" : "s")} from disk, " +
            "trajectories included. It cannot be undone." +
            (running ? " The episode list is emptied as well." : string.Empty),
            "Delete session",
            alternateText: "Export first",
            destructive: true);

        dialog.Alternate += () => ExportBeforeDestructiveAction(
            dialog, folder => ExportSession(_session, running ? null : sessionId, folder));
        dialog.Confirmed += () => DeleteSession(_session, sessionId);

        dialog.Show(_page);
    }

    /// <summary>
    /// The "export first" of a destructive dialog: writes the records the user asked for, leaves the dialog
    /// open while it writes them, and closes it once the write is done. A failure is the outcome the reader
    /// has to act on, so it stays in front of them inside the dialog; a finished export is an answer, and the
    /// question that asked for it has been answered - leaving the dialog up would only ask it again.
    /// </summary>
    private void ExportBeforeDestructiveAction(ConfirmationDialog dialog, Func<string, MetricsExportReport> export)
    {
        ChooseExportFolder(
            folder =>
            {
                (string status, bool close) = ExportOutcome(export(folder), MetricsExporter.LastError);
                if (!close)
                {
                    dialog.SetStatus(status, error: true);
                    return;
                }

                _status.text = status;
                dialog.Dismiss();
            },
            dialog);
    }

    /// <summary>
    /// What one answered export from a dialog leaves behind: the line that reports it, and whether the dialog
    /// that asked for it is done. A failed write is the one outcome the reader has to act on, so it stays in
    /// the dialog where they are already looking; a finished export is an answer, and the question has been
    /// answered. Split out from the handler so the rule can be read - and tested - without the editor's folder
    /// picker standing in the way.
    /// </summary>
    public static (string Status, bool CloseDialog) ExportOutcome(MetricsExportReport report, string error)
        => string.IsNullOrEmpty(report.Directory)
            ? ("Export failed: " + (string.IsNullOrEmpty(error) ? "unknown error" : error), false)
            : ("Exported to " + report.Directory, true);

#if !UNITY_EDITOR
    /// <summary>
    /// Asks for the export folder when no native picker exists. This is the editor's picker replaced by a
    /// field, not a different feature: the folder the user types is the folder every later export uses.
    /// </summary>
    private void ShowExportFolderDialog(Action<string> export)
    {
        ConfirmationDialog dialog = new ConfirmationDialog(
            "Export metrics",
            "Choose the folder the exported files are written to.",
            "Export",
            destructive: false);

        var field = new TextField("Folder") { value = AnalysisExportFolder.Last };
        field.AddToClassList("confirmation-field");
        dialog.Body.Add(field);

        dialog.Confirmed += () =>
        {
            string folder = field.value?.Trim();
            if (string.IsNullOrEmpty(folder))
            {
                dialog.SetStatus("Enter a folder path.", error: true);
                return;
            }

            AnalysisExportFolder.Last = folder;
            export(folder);
        };

        dialog.Show(_page);
    }
#endif

    private void Detach()
    {
        if (_openFolderButton != null)
            _openFolderButton.clicked -= OnOpenFolderClicked;
        if (_rail != null)
        {
            _rail.SessionPicked -= OnSessionPicked;
            _rail.SessionExportRequested -= OnExportSessionRequested;
            _rail.SessionDeleteRequested -= OnDeleteSessionRequested;
        }
        if (_list != null)
        {
            _list.EpisodeSelected -= OnEpisodeSelected;
            _list.ExportRequested -= OnExportEpisodeRequested;
            _list.DeleteRequested -= OnDeleteEpisodeRequested;
            _list.SessionRenameRequested -= OnSessionRenameRequested;
        }
        if (_episodeTitle != null)
            _episodeTitle.RenameRequested -= OnEpisodeRenameRequested;
        if (_session != null)
            _session.Changed -= RefreshView;

        if (_timeline != null)
        {
            _timeline.SeekRequested -= OnSeekRequested;
            _timeline.StepRequested -= OnStepRequested;
            _timeline.StepBackRequested -= OnStepBackRequested;
            _timeline.RewindRequested -= OnRewindRequested;
            _timeline.TogglePlayRequested -= OnTogglePlayRequested;
        }

        _list?.Dispose();
        _rail?.Dispose();
        _map?.Dispose();
        _detail?.Dispose();
        _timeline?.Dispose();

        _list = null;
        _rail = null;
        _map = null;
        _summary = null;
        _detail = null;
        _replay = null;
        _timeline = null;
        _episodeTitle = null;
        _mapPanel = null;
        _detailColumn = null;
        _timelineHost = null;
        _detailHost = null;
        _summaryHost = null;
        _openFolderButton = null;
        _status = null;
        _session = null;
        _page = null;
        _initialized = false;
    }
}
