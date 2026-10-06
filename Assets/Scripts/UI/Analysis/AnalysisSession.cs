using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RobotSNAP.Metrics;

/// <summary>
/// What the analysis tab reads: one session of episodes - the one running now, or one read back from disk -
/// the episode the user picked out of it, and one change signal the views subscribe to.
///
/// The tab used to show the store and only the store, so a session vanished from the interface the moment it
/// was cleared even though its export was still on disk. This class is the seam that lets the same views read
/// either source: <see cref="Episodes"/> is always the list of the session being displayed, and the views
/// never need to know which of the two it is. The store stays exactly what it was - the recorder appends to
/// it and the exporter writes from it - and a saved session is read through <see cref="ArchiveCatalogue"/>.
///
/// The store is a plain list the recorder appends to and has no event of its own, so this class watches the
/// two things a change can move - the session identity and the episode count - each time the tab's
/// <c>Update</c> ticks. The files on disk have no event either, so the same tick compares a cheap stamp of
/// the export roots - the length and last-write instant of the two append-only lines - and re-reads the
/// catalogue only once that stamp has moved. Comparing a handful of <c>stat</c> results once a frame is what
/// keeps a growing archive of thousands of episodes off the frame budget, and it is the reason the saved list
/// is not rebuilt on every tick.
///
/// Views subscribe to <see cref="Changed"/> and drop the subscription in their own dispose, so a view that
/// is rebuilt never leaves a dead one behind.
/// </summary>
public sealed class AnalysisSession
{
    /// <summary>Caption the tab carries for the session the recorder is still appending to.</summary>
    public const string RunningSessionLabel = "Running session";

    private readonly MetricsStore _store;

    // The export roots a saved session can be read from, deduplicated and in priority order. Empty when the
    // caller wants a session that reads nothing - which is what a test injects so it never looks at the
    // machine's real exports.
    private readonly List<string> _roots;

    // The episodes of every saved session, keyed by session id, merged over the roots with the first root
    // winning. Refilled only when the stamp moves, never once per frame.
    private readonly Dictionary<string, List<EpisodeMetrics>> _savedEpisodes =
        new Dictionary<string, List<EpisodeMetrics>>(StringComparer.Ordinal);

    private readonly List<ArchiveSessionRecord> _savedSessions = new List<ArchiveSessionRecord>();
    private string _stamp;

    private List<EpisodeMetrics> _episodes = new List<EpisodeMetrics>();
    private string _sessionId;
    private int _count;

    /// <param name="store">The store the running session is read from; the singleton by default.</param>
    /// <param name="archiveRoots">
    /// Folders a saved session can be read from. <c>null</c> means the folders the tab works with - the one
    /// the user last exported to and the exporter's fallback - and an empty sequence means none, which is what
    /// a test passes so it never reads the real exports of the machine it runs on.
    /// </param>
    public AnalysisSession(MetricsStore store = null, IEnumerable<string> archiveRoots = null)
    {
        _store = store ?? MetricsStore.Instance;
        _roots = ResolveRoots(archiveRoots);

        ReloadSavedSessions();

        _sessionId = _store.SessionId;
        _count = _store.Count;
        SourceId = _sessionId;
        RefreshSourceEpisodes();
    }

    /// <summary>Raised after a refresh or a selection change; views redraw from the properties below.</summary>
    public event Action Changed;

    /// <summary>The store the export button writes, so the interface never needs a second reference.</summary>
    public MetricsStore Store => _store;

    /// <summary>
    /// Episodes of the session being displayed, oldest first, whichever source it came from: the store while
    /// the running session is shown, the archive's catalogue while a saved one is.
    /// </summary>
    public IReadOnlyList<EpisodeMetrics> Episodes => _episodes;

    /// <summary>The selected episode, or null while the displayed session holds none.</summary>
    public EpisodeMetrics Selected { get; private set; }

    /// <summary>Sessions already saved on disk, newest first, as the Sessions rail lists them.</summary>
    public IReadOnlyList<ArchiveSessionRecord> SavedSessions => _savedSessions;

    /// <summary>
    /// The identity of the session the recorder is still appending to. The store owns it and starts a new one
    /// whenever it is cleared, so a reader that needs to ask for "the session running now" asks here rather
    /// than caching an id that the next clear would leave pointing at a session of the past.
    /// </summary>
    public string RunningSessionId => _store.SessionId;

    /// <summary>
    /// The episodes of one session, whichever side of the archive it lives on: the store's while
    /// <paramref name="sessionId"/> names the session running now, the catalogue's while it names one already
    /// saved, and an empty list - never null - for an id neither side holds.
    ///
    /// The running session wins the tie on purpose. A session that is running and has been exported exists
    /// under one id on both sides, and the live copy is the one the recorder is still appending to and the
    /// only one the row actions can act on, so it is the one a reader asking for that id is asking for.
    /// </summary>
    public IReadOnlyList<EpisodeMetrics> EpisodesOf(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<EpisodeMetrics>();

        if (string.Equals(sessionId, _store.SessionId, StringComparison.Ordinal))
            return _store.Episodes;

        return _savedEpisodes.TryGetValue(sessionId, out List<EpisodeMetrics> saved)
            ? saved
            : Array.Empty<EpisodeMetrics>();
    }

    /// <summary>
    /// The folders saved sessions are read from, in priority order. The delete button works on the same list,
    /// so a reader can remove every record the tab is able to show them - a folder the tab read but the button
    /// ignored would be data on screen that the interface could not take away again.
    /// </summary>
    public IReadOnlyList<string> ArchiveRoots => _roots;

    /// <summary>Id of the displayed session: the store's while it runs, the archive's while it is read back.</summary>
    public string SourceId { get; private set; }

    /// <summary>True while the episodes come from the store the recorder is appending to.</summary>
    public bool IsRunningSession { get; private set; } = true;

    /// <summary>Shows the session the recorder is still appending to, and picks up whatever it holds now.</summary>
    public void ShowRunning()
    {
        IsRunningSession = true;
        SourceId = _store.SessionId;
        _sessionId = _store.SessionId;
        _count = _store.Count;
        RefreshSourceEpisodes();
        Changed?.Invoke();
    }

    /// <summary>
    /// Shows one saved session, read from the archive that named it. An id the rail did not offer is ignored,
    /// the same way an unknown episode id is, so a stale click cannot put the tab on nothing.
    /// </summary>
    public void ShowSaved(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !HasSavedSession(sessionId))
            return;

        IsRunningSession = false;
        SourceId = sessionId;
        RefreshSourceEpisodes();
        Changed?.Invoke();
    }

    /// <summary>
    /// Picks up a change and returns whether one happened. Called every frame by the tab, the store check is
    /// two scalar comparisons until an episode actually finishes, and the disk check is a stamp comparison
    /// until a file actually moves.
    /// </summary>
    public bool Poll()
    {
        bool changed = false;

        // The store speaks for the session that runs, whether or not that session is the one on screen: a
        // cleared store starts a new session, and the one it just left is a session of the past that belongs
        // in the rail. Only the episodes follow the display - while a saved session is shown, a finished
        // episode is not part of what the reader is looking at.
        bool storeMoved = _store.Count != _count ||
                          !string.Equals(_store.SessionId, _sessionId, StringComparison.Ordinal);
        if (storeMoved)
        {
            bool sessionMoved = !string.Equals(_store.SessionId, _sessionId, StringComparison.Ordinal);
            _sessionId = _store.SessionId;
            _count = _store.Count;

            if (sessionMoved)
                ReloadSavedSessions();

            if (IsRunningSession)
            {
                SourceId = _sessionId;
                RefreshSourceEpisodes();
            }
            changed = true;
        }

        string stamp = ComputeStamp();
        if (!string.Equals(stamp, _stamp, StringComparison.Ordinal))
        {
            ReloadSavedSessions();

            // A saved session that is still being exported - the reader went back to it while it runs - has to
            // follow what was just appended to it, not keep the list the previous read produced.
            if (!IsRunningSession)
                RefreshSourceEpisodes();

            changed = true;
        }

        if (changed)
            Changed?.Invoke();

        return changed;
    }

    /// <summary>
    /// Re-reads everything the tab shows, from both sides at once: the store the running session lives in, and
    /// the export roots a saved one is read from.
    ///
    /// It reads the folder as well as the store because it is what a delete and a rename call, and the files
    /// they touch carry no event of their own - a reader who just deleted a session must not have to wait for
    /// the next tick to see it gone. <see cref="Poll"/> still does the cheap thing on every frame, and only
    /// parses the archive once a stamp actually moved.
    ///
    /// The selection keeps the episode id it was on while that episode is still there, and follows the newest
    /// one otherwise - a cleared store, a deleted episode - so a new episode finishing never pulls the reader
    /// off the one being examined, and a removed one leaves them on its neighbour rather than on nothing.
    /// </summary>
    public void Refresh()
    {
        _sessionId = _store.SessionId;
        _count = _store.Count;

        // Read before the selection follows the episodes, because the rebuilt list is what says whether the
        // session on screen still exists at all.
        ReloadSavedSessions();

        if (IsRunningSession)
            SourceId = _sessionId;

        RefreshSourceEpisodes();

        Changed?.Invoke();
    }

    /// <summary>Selects one episode of the displayed session by id. Unknown ids are ignored rather than cleared.</summary>
    public void Select(string id)
    {
        EpisodeMetrics episode = Find(_episodes, id);
        if (episode == null || ReferenceEquals(episode, Selected))
            return;

        Selected = episode;
        Changed?.Invoke();
    }

    /// <summary>Re-reads the folder stamp and, only when it moved, the sessions and episodes it names.</summary>
    private void ReloadSavedSessions()
    {
        // A session line with no episode behind it is not a session: an export writes the line before the
        // first episode lands and a deleted episode can leave one behind, and both are rows the rail would
        // offer with nothing to open. Purging them here is what keeps the file from growing a list of
        // sessions nobody can read.
        //
        // The archive the store holds remembers which sessions it has already announced, so a line this
        // removed would never be written again by the next export: forgetting the archive is what puts it
        // back in a state where the session is announced afresh.
        bool purged = false;
        foreach (string root in _roots)
            purged |= TrajectoryArchive.PurgeEmptySessions(root) > 0;

        if (purged)
            _store.ForgetArchive();

        // The stamp is taken after the purge, so the write it just made is part of what the next tick compares
        // against instead of looking like a change the folder made on its own.
        _stamp = ComputeStamp();

        _savedSessions.Clear();
        foreach (ArchiveSessionRecord record in ArchiveCatalogue.ReadAll(_roots))
        {
            // The running session is already offered as the first line of the rail. Its saved copy would be
            // the same episodes a second time, and the second one without the row actions only the live copy
            // carries - so the rail names each session once, under the source that can act on it.
            if (!string.Equals(record.Id, _store.SessionId, StringComparison.Ordinal))
                _savedSessions.Add(record);
        }

        _savedEpisodes.Clear();
        foreach (string root in _roots)
        {
            foreach (KeyValuePair<string, List<EpisodeMetrics>> pair in
                     ArchiveCatalogue.ReadEpisodesBySession(root))
            {
                // The first root wins, the same rule ReadAll follows for the session list, so a session
                // exported to the chosen folder and to the fallback is read from the chosen one.
                if (!_savedEpisodes.ContainsKey(pair.Key))
                    _savedEpisodes[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// Rebuilds the displayed episode list from whichever source is shown and keeps the selection on the same
    /// episode id when it is still there.
    /// </summary>
    private void RefreshSourceEpisodes()
    {
        if (IsRunningSession)
        {
            _episodes = new List<EpisodeMetrics>(_store.Episodes);
        }
        else if (_savedEpisodes.TryGetValue(SourceId, out List<EpisodeMetrics> saved))
        {
            // The list is copied because a reader may hold it while the next reload replaces the dictionary's.
            _episodes = new List<EpisodeMetrics>(saved);
        }
        else
        {
            // A session line whose catalogue holds nothing yet is still a session a reader asked to look at.
            _episodes = new List<EpisodeMetrics>();
        }

        EpisodeMetrics kept = Selected != null ? Find(_episodes, Selected.Id) : null;
        Selected = kept ?? (_episodes.Count > 0 ? _episodes[_episodes.Count - 1] : null);
    }

    /// <summary>
    /// Whether the rail currently lists <paramref name="sessionId"/> as a saved session. The running session
    /// is never one of them: it is reached by <see cref="ShowRunning"/>.
    /// </summary>
    public bool HasSavedSession(string sessionId)
    {
        foreach (ArchiveSessionRecord record in _savedSessions)
        {
            if (string.Equals(record.Id, sessionId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static EpisodeMetrics Find(IReadOnlyList<EpisodeMetrics> episodes, string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        for (int index = 0; index < episodes.Count; index++)
        {
            if (string.Equals(episodes[index].Id, id, StringComparison.Ordinal))
                return episodes[index];
        }
        return null;
    }

    /// <summary>
    /// A cheap description of the export roots that changes whenever one of them gains a session or an
    /// episode: the length and last-write instant of the two append-only lines, per root. It is compared once
    /// a frame, and its only job is to decide whether the catalogue is worth reading again - the catalogue
    /// itself is only parsed when this moved.
    /// </summary>
    private string ComputeStamp()
    {
        var builder = new StringBuilder();
        foreach (string root in _roots)
        {
            builder.Append(root).Append('|');
            AppendFileStamp(builder, Path.Combine(root, TrajectoryArchive.CatalogueFileName));
            builder.Append('|');
            AppendFileStamp(builder, Path.Combine(root, TrajectoryArchive.SessionsFileName));
            builder.Append(';');
        }
        return builder.ToString();
    }

    private static void AppendFileStamp(StringBuilder builder, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                builder.Append('-');
                return;
            }

            var info = new FileInfo(path);
            builder.Append(info.Length).Append('@').Append(info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception)
        {
            // A file that vanished between the check and the stat is "unknown", which is a stamp that differs
            // from the last one and therefore re-reads the catalogue once - the safe direction to fail in.
            builder.Append('?');
        }
    }

    /// <summary>
    /// The roots a saved session is read from, deduplicated. Passing none means the two the tab works with;
    /// passing an empty sequence means none, which is how a caller turns reading off.
    /// </summary>
    private static List<string> ResolveRoots(IEnumerable<string> archiveRoots)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string root in archiveRoots ?? DefaultArchiveRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            string normalised = root.Trim();
            if (seen.Add(normalised))
                roots.Add(normalised);
        }

        return roots;
    }

    private static IEnumerable<string> DefaultArchiveRoots()
    {
        yield return AnalysisExportFolder.Last;
        yield return MetricsExporter.FallbackRoot;
    }
}
