using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RobotSNAP.Metrics
{
    /// <summary>
    /// Where an export landed, so a caller - a log line, a router answer - can name the files.
    ///
    /// The legacy <see cref="SessionFile"/>, <see cref="CsvFile"/> and <see cref="IndexFile"/> fields are
    /// kept for callers written against the whole-session export, and the new fields name what an append-only
    /// export actually writes.
    /// </summary>
    public readonly struct MetricsExportReport
    {
        public readonly string Directory;
        public readonly string SessionFile;
        public readonly string CsvFile;
        public readonly string IndexFile;
        public readonly string SessionsFile;
        public readonly string CatalogueFile;
        public readonly string TrajectoriesFolder;

        /// <summary>
        /// True when the requested root was refused and the export went to the persistent-data fallback
        /// instead. The export still happened - <see cref="Directory"/> names where - but a caller that
        /// reports the destination has to be able to say it is not the one the user asked for.
        /// </summary>
        public readonly bool UsedFallback;

        public MetricsExportReport(
            string directory,
            string sessionFile,
            string csvFile,
            string indexFile,
            string sessionsFile = null,
            string catalogueFile = null,
            string trajectoriesFolder = null,
            bool usedFallback = false)
        {
            Directory = directory;
            SessionFile = sessionFile;
            CsvFile = csvFile;
            IndexFile = indexFile;
            SessionsFile = sessionsFile;
            CatalogueFile = catalogueFile;
            TrajectoriesFolder = trajectoriesFolder;
            UsedFallback = usedFallback;
        }
    }

    /// <summary>
    /// Appends a session to disk under <c>StreamingAssets/metrics/</c>, in a format Python reads without
    /// Unity and a human reads without Python:
    ///
    ///   <c>sessions.jsonl</c>                  one line per session, written the first time it is exported
    ///   <c>catalogue.jsonl</c>                 one line per finished episode, written once and never rewritten
    ///   <c>trajectories/&lt;session&gt;.rbt</c>   the binary record of each episode's trajectories
    ///
    /// The whole-session file this replaced was rewritten after every finished episode, so a ten-thousand
    /// episode run rewrote tens of terabytes and kept the session in RAM to do it. An append touches only the
    /// episode that just finished, and the episode's map leaves memory the moment the archive holds it: the
    /// store keeps summaries, and a reader that needs a trajectory seeks to it through its catalogue line.
    ///
    /// None of this throws. A folder the process cannot write to is not a failure either: the write falls back
    /// to <see cref="FallbackRoot"/> and the report says so, because a trajectory saved somewhere is worth
    /// more than one saved nowhere. What is left that can fail - a full disk, an archive the file system
    /// refuses - is reported through the returned report being empty and through the exception message in
    /// <c>LastError</c>, because an episode that has already been recorded and published must not be lost to a
    /// file system problem. A trajectory that cannot be archived is not lost either: its catalogue line then
    /// carries the map in clear, which is the one case a reader needs the inline copy.
    /// </summary>
    public static class MetricsExporter
    {
        /// <summary>Last failure of an export, or null when the last one succeeded.</summary>
        public static string LastError { get; private set; }

        /// <summary>Folder the export writes to when the caller names none: <c>StreamingAssets/metrics</c>.</summary>
        public static string DefaultRoot
            => Path.Combine(Application.streamingAssetsPath, MetricsContract.ExportFolder);

        /// <summary>
        /// Where an export goes when the requested root cannot be written: <c>persistentDataPath</c> is the one
        /// folder a build and a workstation both own, so a trajectory is never lost to a read-only project or a
        /// StreamingAssets that a packaged player cannot write.
        /// </summary>
        public static string FallbackRoot
            => Path.Combine(Application.persistentDataPath, "robotsnap", "metrics");

        /// <summary>
        /// Appends every episode the session has not archived yet: a session line the first time, then one
        /// catalogue line and one trajectory record each. <paramref name="root"/> overrides the folder, which
        /// is how a test writes to a temporary directory instead of the project.
        ///
        /// A root the process cannot write to is not the end of the export: the write falls back to
        /// <see cref="FallbackRoot"/> and says so through the report's <c>UsedFallback</c> and a warning,
        /// because a trajectory that exists in the wrong folder is worth more than one that does not exist.
        /// </summary>
        public static MetricsExportReport Export(MetricsStore store, string root = null)
        {
            if (store == null) return default;

            string requested = string.IsNullOrEmpty(root) ? DefaultRoot : root;
            string directory = ResolveRoot(requested, out bool usedFallback);
            if (directory == null)
            {
                LastError = $"export root '{requested}' is not writable and neither is '{FallbackRoot}'";
                Debug.LogWarning($"[MetricsExporter] export to '{requested}' failed: no writable folder");
                return default;
            }

            store.UseArchive(directory);
            TrajectoryArchive archive = store.Archive;
            if (archive == null)
            {
                LastError = "no archive";
                return default;
            }

            // "failure" records the first thing that went wrong, whether or not it could be worked around,
            // because LastError is what a caller reports. "fatal" is only what makes the export itself not
            // have happened - a root nothing could be written to. A trajectory the archive refused is not
            // fatal: its catalogue line carries the map in clear, which is the whole point of the fallback.
            string failure = null;
            string fatal = null;

            // A session with nothing to record is not announced. The session line is what makes a folder list
            // the session in the analysis tab, so one written for an empty store is a session a reader can open
            // and find nothing in - and exporting on quit does exactly this to every run that never filed an
            // episode before the application went away. A store that holds an episode announces its session as
            // before, and for one whose episodes are all archived the call is the no-op it always was.
            if (store.Count > 0)
            {
                archive.EnsureSessionLine(store.SessionId, store.StartedAt, store.SessionName);
                if (archive.LastError != null)
                {
                    failure ??= archive.LastError;
                    fatal ??= archive.LastError;
                }
            }

            foreach (EpisodeMetrics episode in store.Episodes)
            {
                // An episode already carrying a reference is on disk: re-exporting a session appends the new
                // episodes only, so the cost of a session stays linear in its episodes.
                if (episode == null || episode.TrajectoryRef != null)
                    continue;

                TrajectoryRef reference = archive.Append(episode);
                if (reference == null)
                    failure ??= archive.LastError;

                episode.TrajectoryRef = reference;
                archive.AppendCatalogue(episode);

                if (archive.LastError != null)
                {
                    // The line is what makes the record findable, so a record whose line did not land is not
                    // an archive: the reference is taken back and the episode stays in RAM to be retried.
                    failure ??= archive.LastError;
                    fatal ??= archive.LastError;
                    episode.TrajectoryRef = null;
                    continue;
                }

                // The archive holds the points now, so the RAM copy is redundant. A failure above leaves the
                // map in place instead: it is then the only copy, and it is what the line carries in clear.
                if (reference != null && episode.Trajectories != null)
                    episode.Trajectories.Clear();
            }

            LastError = failure;
            if (fatal != null)
                return default;

            return new MetricsExportReport(
                directory,
                string.Empty,
                string.Empty,
                string.Empty,
                Path.Combine(directory, TrajectoryArchive.SessionsFileName),
                Path.Combine(directory, TrajectoryArchive.CatalogueFileName),
                Path.Combine(directory, TrajectoryArchive.TrajectoriesFolderName),
                usedFallback);
        }

        /// <summary>
        /// Exports one episode on its own, so a run can be handed over without the session it belongs to. The
        /// file sits next to the session export, is named after the episode and is written whole, so a reader
        /// of a single run never has to open - or be given - the session's file. The index is deliberately not
        /// touched: it describes sessions, an episode is not one, and a reader of an index a previous version
        /// wrote must go on reading the same shape.
        ///
        /// The document carries the trajectory either way: an episode still in RAM has it inline already, and
        /// one read back from an archive gets its points read out of the record its <c>trajectory_ref</c> names
        /// through <see cref="MetricsStore.TracksOf"/>. The points go into the document and never into the
        /// episode, which stays as it was - a file the reader takes away must not change what the tab holds.
        /// </summary>
        public static MetricsExportReport ExportEpisode(
            MetricsStore store, EpisodeMetrics episode, string root = null)
        {
            if (store == null || episode == null)
            {
                LastError = "no episode to export";
                return default;
            }

            string episodeId = episode.Id;

            string requested = string.IsNullOrEmpty(root) ? DefaultRoot : root;
            string directory = ResolveRoot(requested, out bool usedFallback);
            if (directory == null)
            {
                LastError = $"export root '{requested}' is not writable and neither is '{FallbackRoot}'";
                Debug.LogWarning($"[MetricsExporter] export of '{episodeId}' failed: no writable folder");
                return default;
            }

            string file = Path.Combine(directory, "episode_" + episode.Id + ".json");

            try
            {
                WriteAtomic(file, DocumentOf(store, episode).ToString(Formatting.Indented));
                LastError = null;
                return new MetricsExportReport(
                    directory, file, string.Empty, string.Empty, usedFallback: usedFallback);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogWarning($"[MetricsExporter] export of '{episodeId}' to '{directory}' failed: {exception.Message}");
                return default;
            }
        }

        /// <summary>
        /// Exports one episode named by its id, which is the form a caller that holds only the id - the
        /// episode list's per-row export, a client command - can use. It is the same export as the episode form
        /// above; an id the session does not hold is reported through <see cref="LastError"/> rather than
        /// writing an empty file.
        /// </summary>
        public static MetricsExportReport ExportEpisode(MetricsStore store, string episodeId, string root = null)
        {
            if (store == null)
                return default;

            EpisodeMetrics episode = store.Get(episodeId);
            if (episode == null)
            {
                LastError = $"no episode '{episodeId}' in session {store.SessionId}";
                return default;
            }

            return ExportEpisode(store, episode, root);
        }

        /// <summary>
        /// Exports a whole session as one document, in the shape
        /// <see cref="MetricsStore.ToSessionJson"/> writes and the Python reader already parses:
        /// <c>session</c>, <c>started_at</c>, <c>exported_at</c>, <c>episode_count</c> and <c>episodes</c>.
        ///
        /// It is what hands a session over to another tool: the append-only files beside it are the running
        /// record, this one file is the complete story of a session, and it is written whole rather than
        /// appended to so a reader never sees half of it. Every episode carries its trajectory in clear - the
        /// points of an episode read back from an archive are read out of the record its reference names
        /// through <see cref="MetricsStore.TracksOf"/> - so the file stands alone, with no archive beside it
        /// and no folder this machine happens to have.
        ///
        /// The root follows the same rule as every other export: a folder that cannot be written falls back to
        /// persistent data and says so through <c>UsedFallback</c>.
        /// </summary>
        public static MetricsExportReport ExportSession(
            IReadOnlyList<EpisodeMetrics> episodes, string sessionId, string startedAt, string root = null)
        {
            string requested = string.IsNullOrEmpty(root) ? DefaultRoot : root;
            string directory = ResolveRoot(requested, out bool usedFallback);
            if (directory == null)
            {
                LastError = $"export root '{requested}' is not writable and neither is '{FallbackRoot}'";
                Debug.LogWarning($"[MetricsExporter] export of session '{sessionId}' failed: no writable folder");
                return default;
            }

            string file = Path.Combine(directory, "session_" + sessionId + ".json");

            try
            {
                MetricsStore store = MetricsStore.Instance;
                var documents = new List<JObject>();
                if (episodes != null)
                {
                    foreach (EpisodeMetrics episode in episodes)
                    {
                        if (episode != null)
                            documents.Add(DocumentOf(store, episode));
                    }
                }

                var document = new JObject
                {
                    ["session"] = sessionId,
                    ["started_at"] = startedAt,
                    ["exported_at"] = UtcNowIso(),
                    ["episode_count"] = documents.Count,
                    ["episodes"] = new JArray(documents),
                };

                WriteAtomic(file, document.ToString(Formatting.Indented));
                LastError = null;
                return new MetricsExportReport(
                    directory, file, string.Empty, string.Empty, usedFallback: usedFallback);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogWarning(
                    $"[MetricsExporter] export of session '{sessionId}' to '{directory}' failed: " +
                    exception.Message);
                return default;
            }
        }

        /// <summary>
        /// The episode as the file it is exported to should read: the record as it is, except that an episode
        /// whose points live in an archive gets them read back into the document. A reference is a pointer into
        /// a file beside an export root, which a single-file export does not carry; the points it names are
        /// what the reader of that file needs, so they replace it.
        /// </summary>
        private static JObject DocumentOf(MetricsStore store, EpisodeMetrics episode)
        {
            JObject document = JObject.FromObject(episode);
            if (episode.TrajectoryRef == null)
                return document;

            var map = new JObject();
            foreach (KeyValuePair<string, List<double[]>> track in store.TracksOf(episode))
                map[track.Key] = JArray.FromObject(track.Value);

            document["trajectories"] = map;
            document.Remove("trajectory_ref");
            return document;
        }

        private static string UtcNowIso()
            => DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// The folder an export will really use: the one asked for when a probe proves it writable, and the
        /// persistent-data fallback otherwise. Null means neither can be written, which is the one case an
        /// export does not happen - a caller then reports it instead of silently writing nowhere.
        /// </summary>
        private static string ResolveRoot(string requested, out bool usedFallback)
        {
            usedFallback = false;
            if (IsWritable(requested))
                return requested;

            string fallback = FallbackRoot;
            if (!IsWritable(fallback))
                return null;

            Debug.LogWarning(
                $"[MetricsExporter] export root '{requested}' is not writable; using '{fallback}' instead");
            usedFallback = true;
            return fallback;
        }

        /// <summary>
        /// Whether a folder can actually be written to, decided by doing it: the directory is created and a
        /// probe file is written and removed. A permission bit, a read-only mount and a path that runs through
        /// an ordinary file can all look like a folder from the outside, so the only honest answer comes from a
        /// real write. The probe name is unique so two exports cannot trip over each other's file.
        /// </summary>
        private static bool IsWritable(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            try
            {
                Directory.CreateDirectory(path);
                string probe = Path.Combine(
                    path, "robotsnap-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes through a temporary file and moves it into place, so a reader that opens the destination while
        /// it is being rewritten sees either the previous complete document or the new one, never a half-written
        /// one. The temporary name is derived from the destination, so two exports cannot collide on it.
        /// </summary>
        private static void WriteAtomic(string path, string content)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporary, path);
        }
    }
}
