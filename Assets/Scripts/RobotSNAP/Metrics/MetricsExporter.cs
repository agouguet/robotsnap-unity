using System;
using System.IO;
using Newtonsoft.Json;
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

        public MetricsExportReport(
            string directory,
            string sessionFile,
            string csvFile,
            string indexFile,
            string sessionsFile = null,
            string catalogueFile = null,
            string trajectoriesFolder = null)
        {
            Directory = directory;
            SessionFile = sessionFile;
            CsvFile = csvFile;
            IndexFile = indexFile;
            SessionsFile = sessionsFile;
            CatalogueFile = catalogueFile;
            TrajectoriesFolder = trajectoriesFolder;
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
    /// None of this throws: an export that fails - a read-only folder, a full disk - is reported through the
    /// returned report being empty and through the exception message in <c>LastError</c>, because an episode
    /// that has already been recorded and published must not be lost to a file system problem. A trajectory
    /// that cannot be archived is not lost either: its catalogue line then carries the map in clear, which is
    /// the one case a reader needs the inline copy.
    /// </summary>
    public static class MetricsExporter
    {
        /// <summary>Last failure of an export, or null when the last one succeeded.</summary>
        public static string LastError { get; private set; }

        /// <summary>Folder the export writes to when the caller names none: <c>StreamingAssets/metrics</c>.</summary>
        public static string DefaultRoot
            => Path.Combine(Application.streamingAssetsPath, MetricsContract.ExportFolder);

        /// <summary>
        /// Appends every episode the session has not archived yet: a session line the first time, then one
        /// catalogue line and one trajectory record each. <paramref name="root"/> overrides the folder, which
        /// is how a test writes to a temporary directory instead of the project.
        /// </summary>
        public static MetricsExportReport Export(MetricsStore store, string root = null)
        {
            if (store == null) return default;

            string directory = string.IsNullOrEmpty(root) ? DefaultRoot : root;

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogWarning($"[MetricsExporter] export to '{directory}' failed: {exception.Message}");
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

            archive.EnsureSessionLine(store.SessionId, store.StartedAt);
            if (archive.LastError != null)
            {
                failure ??= archive.LastError;
                fatal ??= archive.LastError;
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
                Path.Combine(directory, TrajectoryArchive.TrajectoriesFolderName));
        }

        /// <summary>
        /// Exports one episode on its own, so a run can be handed over without the session it belongs to. The
        /// file sits next to the session export, is named after the episode and is written whole, so a reader
        /// of a single run never has to open - or be given - the session's file. The index is deliberately not
        /// touched: it describes sessions, an episode is not one, and a reader of an index a previous version
        /// wrote must go on reading the same shape.
        /// </summary>
        public static MetricsExportReport ExportEpisode(MetricsStore store, string episodeId, string root = null)
        {
            if (store == null) return default;

            EpisodeMetrics episode = store.Get(episodeId);
            if (episode == null)
            {
                LastError = $"no episode '{episodeId}' in session {store.SessionId}";
                return default;
            }

            string directory = string.IsNullOrEmpty(root) ? DefaultRoot : root;
            string file = Path.Combine(directory, "episode_" + episode.Id + ".json");

            try
            {
                Directory.CreateDirectory(directory);
                WriteAtomic(file, JsonConvert.SerializeObject(episode, Formatting.Indented));
                LastError = null;
                return new MetricsExportReport(directory, file, string.Empty, string.Empty);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogWarning($"[MetricsExporter] export of '{episodeId}' to '{directory}' failed: {exception.Message}");
                return default;
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
