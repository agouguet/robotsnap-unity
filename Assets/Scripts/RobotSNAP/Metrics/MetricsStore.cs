using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;

namespace RobotSNAP.Metrics
{
    /// <summary>
    /// Every episode of one session, in the order they finished, together with the identity of the session
    /// itself. It is the single thing the dashboard, the router commands and the exporter read, so all three
    /// see the same episodes and a client that clears the store clears it for the interface at the same time.
    ///
    /// A session starts empty and stays valid while it is empty: <see cref="Episodes"/> is an empty list, never
    /// null and never an exception, which is what lets a freshly started client ask for the episode list
    /// without waiting for a scenario to run. <see cref="Clear"/> keeps that promise about the *next* session
    /// as well - it starts a new one, with a new identifier, rather than leaving a session with no identity.
    ///
    /// The store is a singleton because it has to outlive the environment: loading a scenario destroys and
    /// rebuilds the GameObjects, the recorder with them, and an episode list that lived on a destroyed object
    /// would lose the session's history every time a map is loaded.
    /// </summary>
    public sealed class MetricsStore
    {
        private static MetricsStore _instance;

        private readonly List<EpisodeMetrics> _episodes = new();
        private int _sequence;

        // The archive the session's trajectories are read from, and a one-entry cache of the last episode
        // read out of it. The Analysis tab asks for an episode's tracks once per frame per drawn agent, and a
        // read is a seek plus a decode: keeping the last one means a redraw of the same run pays for it once.
        private TrajectoryArchive _archive;
        private EpisodeMetrics _cachedTrackEpisode;
        private IReadOnlyDictionary<string, List<double[]>> _cachedTracks;

        private static readonly IReadOnlyDictionary<string, List<double[]>> NoTracks =
            new Dictionary<string, List<double[]>>();

        private MetricsStore()
        {
            StartSession();
        }

        /// <summary>The session's store, created on first use and never replaced by a scene change.</summary>
        public static MetricsStore Instance => _instance ??= new MetricsStore();

        /// <summary>Identifier of the current session, unique per run of the application.</summary>
        public string SessionId { get; private set; }

        /// <summary>ISO-8601 UTC instant the current session started.</summary>
        public string StartedAt { get; private set; }

        /// <summary>Episodes of the current session, oldest first. Empty - never null - before the first one.</summary>
        public IReadOnlyList<EpisodeMetrics> Episodes => _episodes;

        /// <summary>Number of episodes the current session holds.</summary>
        public int Count => _episodes.Count;

        /// <summary>The archive the session appends to and reads from, or null before one is installed.</summary>
        public TrajectoryArchive Archive => _archive;

        /// <summary>
        /// Points the session at the archive under <paramref name="root"/>. The exporter calls this with the
        /// folder it just wrote to, so a reader that follows the store follows the export wherever it landed.
        ///
        /// The archive is reused while the folder stays the same, because it remembers which sessions it has
        /// already announced: a recorder that exports after every episode must not append the session line
        /// again each time it passes the same folder.
        /// </summary>
        public void UseArchive(string root)
        {
            if (!string.IsNullOrEmpty(root) && _archive != null &&
                string.Equals(_archive.Root, root, StringComparison.Ordinal))
                return;

            _archive = string.IsNullOrEmpty(root) ? null : new TrajectoryArchive(root);
            _cachedTrackEpisode = null;
            _cachedTracks = null;
        }

        /// <summary>
        /// The tracks of one episode, keyed by agent id, whichever copy holds them: the inline map while the
        /// episode has not been archived, and otherwise the record the archive holds. An episode with neither
        /// reads as an empty map, never null and never a throw, because the interface asks this every frame.
        /// </summary>
        public IReadOnlyDictionary<string, List<double[]>> TracksOf(EpisodeMetrics episode)
        {
            if (episode == null)
                return NoTracks;

            if (episode.Trajectories != null && episode.Trajectories.Count > 0)
                return episode.Trajectories;

            if (episode.TrajectoryRef == null)
                return NoTracks;

            if (ReferenceEquals(_cachedTrackEpisode, episode) && _cachedTracks != null)
                return _cachedTracks;

            // An episode carrying a reference but no installed archive is one exported by a previous call in
            // this process; the default folder is where that export would have gone without a named root.
            TrajectoryArchive archive = _archive ??= new TrajectoryArchive(MetricsExporter.DefaultRoot);
            if (!archive.TryRead(episode.TrajectoryRef, out Dictionary<string, List<double[]>> tracks))
                return NoTracks;

            _cachedTrackEpisode = episode;
            _cachedTracks = tracks;
            return tracks;
        }

        /// <summary>Appends one finished episode. Returns it, so a caller can chain the export.</summary>
        public EpisodeMetrics Add(EpisodeMetrics episode)
        {
            if (episode != null)
                _episodes.Add(episode);
            return episode;
        }

        /// <summary>The episode whose <c>id</c> is <paramref name="id"/>, or null when the session holds none.</summary>
        public EpisodeMetrics Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            for (int index = 0; index < _episodes.Count; index++)
            {
                if (string.Equals(_episodes[index].Id, id, StringComparison.Ordinal))
                    return _episodes[index];
            }
            return null;
        }

        /// <summary>
        /// Drops one episode and returns it, or null when the session holds no episode with that id. The
        /// session's identity is left alone: removing one episode of a session is not starting another, so
        /// the ids of the episodes that stay keep naming the session they ran in.
        /// </summary>
        public EpisodeMetrics Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            for (int index = 0; index < _episodes.Count; index++)
            {
                if (!string.Equals(_episodes[index].Id, id, StringComparison.Ordinal))
                    continue;

                EpisodeMetrics removed = _episodes[index];
                _episodes.RemoveAt(index);
                return removed;
            }
            return null;
        }

        /// <summary>
        /// Drops every episode and starts a new session. Returns how many episodes were dropped, so an answer
        /// to a client can say what it actually cleared. Export files already on disk are left alone: they are
        /// the record of a session that happened, and a store that deleted them would make "clear" mean
        /// "erase history".
        /// </summary>
        public int Clear()
        {
            int removed = _episodes.Count;
            _episodes.Clear();
            _sequence = 0;
            StartSession();
            return removed;
        }

        /// <summary>
        /// Allocates the next episode's place in this session: the <paramref name="index"/> it is shown by and
        /// the identifier derived from the session, so two sessions' files cannot collide and a reader can tell
        /// at a glance which session an id belongs to.
        ///
        /// The two come from the same counter on purpose. An episode numbered 3 that is only the second of its
        /// session would be a number a reader cannot trust, and this counter is the only thing that knows how
        /// many episodes the session has already handed out.
        /// </summary>
        public string NextEpisodeId(out int index)
        {
            _sequence++;
            index = _sequence;
            return SessionId + "-" + _sequence.ToString("D4", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The whole session as one JSON document: its identity, when it started, how many episodes it holds
        /// and the episodes themselves. This is what <see cref="MetricsExporter"/> writes, and what a Python
        /// reader parses without Unity in the loop.
        /// </summary>
        public string ToSessionJson()
        {
            var document = new Dictionary<string, object>
            {
                { "session", SessionId },
                { "started_at", StartedAt },
                { "exported_at", UtcNowIso() },
                { "episode_count", _episodes.Count },
                { "episodes", _episodes },
            };
            return JsonConvert.SerializeObject(document, Formatting.Indented);
        }

        /// <summary>
        /// The episode table as CSV, one row per episode, without the trajectories - a trajectory is a column
        /// no spreadsheet can hold, and the JSON file is where it lives. The header is written even when the
        /// session is empty, so a reader always gets a table it can parse.
        /// </summary>
        public string ToCsv()
        {
            var builder = new StringBuilder();
            builder.AppendLine(
                "id,index,scenario,robot,started_at,outcome,world_seconds,wall_seconds,steps," +
                "path_length_m,straight_line_m,avg_speed_mps,max_speed_mps," +
                "min_human_distance_m,avg_human_distance_m,min_clearance_m,robot_radius_m,human_radius_m," +
                "personal_space_intrusions,personal_space_seconds,personal_space_radius_m,session");

            foreach (EpisodeMetrics episode in _episodes)
            {
                builder.Append(Csv(episode.Id)).Append(',')
                    .Append(episode.Index.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Csv(episode.Scenario)).Append(',')
                    .Append(Csv(episode.Robot)).Append(',')
                    .Append(Csv(episode.StartedAt)).Append(',')
                    .Append(Csv(episode.Outcome)).Append(',')
                    .Append(Number(episode.WorldSeconds)).Append(',')
                    .Append(Number(episode.WallSeconds)).Append(',')
                    .Append(episode.Steps.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Number(episode.PathLengthMetres)).Append(',')
                    .Append(Number(episode.StraightLineMetres)).Append(',')
                    .Append(Number(episode.AverageSpeedMetresPerSecond)).Append(',')
                    .Append(Number(episode.MaxSpeedMetresPerSecond)).Append(',')
                    .Append(Number(episode.MinHumanDistanceMetres)).Append(',')
                    .Append(Number(episode.AverageHumanDistanceMetres)).Append(',')
                    .Append(Number(episode.MinClearanceMetres)).Append(',')
                    .Append(Number(episode.RobotRadiusMetres)).Append(',')
                    .Append(Number(episode.HumanRadiusMetres)).Append(',')
                    .Append(episode.PersonalSpaceIntrusions.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Number(episode.PersonalSpaceSeconds)).Append(',')
                    .Append(Number(episode.PersonalSpaceRadiusMetres)).Append(',')
                    .Append(Csv(episode.Session))
                    .AppendLine();
            }

            return builder.ToString();
        }

        private void StartSession()
        {
            StartedAt = UtcNowIso();
            SessionId = "s_" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
                        + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string UtcNowIso()
            => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        private static string Number(double value)
            => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Csv(string value)
        {
            string text = value ?? string.Empty;
            if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
                return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
