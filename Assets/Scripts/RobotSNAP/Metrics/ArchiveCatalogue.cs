using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RobotSNAP.Metrics
{
    /// <summary>
    /// One session as an export root remembers it: the identity and start instant written to
    /// <c>sessions.jsonl</c>, how many episodes the catalogue beside it holds, and the root the two live in.
    ///
    /// The root travels with the record on purpose. The analysis tab shows sessions from more than one folder -
    /// the folder the user last exported to and the fallback an unwritable one was redirected to - and a
    /// reader that only had the id would have to guess which folder an episode's trajectory sits in.
    /// </summary>
    public sealed class ArchiveSessionRecord
    {
        /// <summary>Identifier of the session, the same value every episode's <c>session</c> field carries.</summary>
        public string Id;

        /// <summary>ISO-8601 UTC instant the session started, or null when the line did not carry one.</summary>
        public string StartedAt;

        /// <summary>Name the user gave the session, or null when the line did not carry one.</summary>
        public string Name;

        /// <summary>Number of episodes the catalogue holds for this session.</summary>
        public int Episodes;

        /// <summary>Export root the sessions and catalogue lines were read from.</summary>
        public string Root;
    }

    /// <summary>
    /// Reads what an export root has already saved: the sessions it announced and the episodes its catalogue
    /// recorded. The analysis tab uses this to show the sessions of earlier runs beside the one running now,
    /// which is the whole point of the disk format - a session that was exported is a session a reader can
    /// come back to.
    ///
    /// It is a reader and only a reader: it never writes, never creates a folder and never throws. An absent
    /// root, a line a crash left half-written, a field a future schema renamed - each of those reads as
    /// nothing rather than as an error, because a tab that cannot draw is worse than a tab that draws fewer
    /// rows, and the last line of an append-only file is exactly where a crash lands.
    ///
    /// Every line is parsed on its own, so one unreadable line costs that line and not the ones around it: an
    /// export interrupted mid-write must not hide the thousand episodes that were written before it.
    /// </summary>
    public static class ArchiveCatalogue
    {
        /// <summary>
        /// How every line of the archive is parsed. Dates are read as the text they are, never as a
        /// <see cref="DateTime"/>: Newtonsoft turns a string that looks like an instant into a date by default
        /// and then prints it back in the current culture, which would rewrite <c>2026-10-05T13:45:59Z</c> as
        /// <c>10/05/2026 13:45:59</c>. That text is what a reader sorts on and what every other tool that
        /// reads the file expects, so it has to come back out unchanged.
        /// </summary>
        private static readonly JsonSerializerSettings LineSettings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
        };

        /// <summary>One line of <c>sessions.jsonl</c>, read without touching the text of its instant.</summary>
        private sealed class SessionLine
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("started_at")] public string StartedAt;
            [JsonProperty("name")] public string Name;
        }

        /// <summary>
        /// Every episode the root's <c>catalogue.jsonl</c> holds, grouped by session id and ordered as the
        /// file orders them - that is, oldest first, because an export only ever appends.
        ///
        /// An episode carries the root it was read from in its runtime <c>ArchiveRoot</c>, so the trajectories
        /// of a replayed run are read from the folder the run was exported to rather than from whatever
        /// archive the live store happens to have installed.
        /// </summary>
        public static Dictionary<string, List<EpisodeMetrics>> ReadEpisodesBySession(string root)
        {
            var bySession = new Dictionary<string, List<EpisodeMetrics>>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(root))
                return bySession;

            string path = Path.Combine(root, TrajectoryArchive.CatalogueFileName);
            try
            {
                if (!File.Exists(path))
                    return bySession;

                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    EpisodeMetrics episode = null;
                    try
                    {
                        episode = JsonConvert.DeserializeObject<EpisodeMetrics>(line, LineSettings);
                    }
                    catch (Exception)
                    {
                        // A line an interrupted write left truncated, or one a future schema wrote in another
                        // shape, is skipped on its own. The episodes around it are still a session's record.
                        continue;
                    }

                    if (episode == null)
                        continue;

                    episode.ArchiveRoot = root;

                    // A line written before the session field existed still belongs to the session that wrote
                    // it, which is what its own id names - grouping it under an empty key would merge two
                    // sessions that merely predate the field.
                    string session = string.IsNullOrEmpty(episode.Session) ? episode.Id : episode.Session;
                    if (session == null)
                        session = string.Empty;

                    if (!bySession.TryGetValue(session, out List<EpisodeMetrics> episodes))
                    {
                        episodes = new List<EpisodeMetrics>();
                        bySession[session] = episodes;
                    }
                    episodes.Add(episode);
                }
            }
            catch (Exception)
            {
                // A file that vanished between the existence check and the read, or a folder the process may
                // not list, reads as whatever had been gathered so far instead of failing the caller.
            }

            return bySession;
        }

        /// <summary>
        /// The sessions the root's <c>sessions.jsonl</c> names, each with the number of episodes the catalogue
        /// holds for it, oldest line first. A root that was never written to reads as the empty list.
        ///
        /// Reading a double line defensively - the writer announces a session once, but a file that was copied
        /// or merged by hand could carry it twice - keeps one session from appearing twice in the picker.
        /// </summary>
        public static IReadOnlyList<ArchiveSessionRecord> ReadSessions(string root)
        {
            var sessions = new List<ArchiveSessionRecord>();
            if (string.IsNullOrWhiteSpace(root))
                return sessions;

            Dictionary<string, List<EpisodeMetrics>> bySession = ReadEpisodesBySession(root);
            string path = Path.Combine(root, TrajectoryArchive.SessionsFileName);
            var announced = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                if (!File.Exists(path))
                    return sessions;

                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    string id;
                    string startedAt;
                    string name;
                    try
                    {
                        SessionLine parsed = JsonConvert.DeserializeObject<SessionLine>(line, LineSettings);
                        id = parsed?.Id;
                        startedAt = parsed?.StartedAt;
                        name = parsed?.Name;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(id) || !announced.Add(id))
                        continue;

                    sessions.Add(new ArchiveSessionRecord
                    {
                        Id = id,
                        StartedAt = startedAt,
                        Name = name,
                        Episodes = bySession.TryGetValue(id, out List<EpisodeMetrics> episodes)
                            ? episodes.Count
                            : 0,
                        Root = root,
                    });
                }
            }
            catch (Exception)
            {
            }

            return sessions;
        }

        /// <summary>
        /// The sessions of several roots as one list, newest first. The first root that names a session wins -
        /// the user's chosen folder is asked before the fallback - so a session exported to both places is
        /// listed once and read from the folder the user picked.
        ///
        /// Null and empty roots, and a root repeated in the list, are ignored rather than read twice. A session
        /// whose start instant is missing or unreadable sorts after the dated ones instead of sorting as the
        /// oldest, which a zero date would make it look like.
        /// </summary>
        public static IReadOnlyList<ArchiveSessionRecord> ReadAll(IEnumerable<string> roots)
        {
            var merged = new List<ArchiveSessionRecord>();
            var seenSessions = new HashSet<string>(StringComparer.Ordinal);
            var seenRoots = new HashSet<string>(StringComparer.Ordinal);

            if (roots != null)
            {
                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root) || !seenRoots.Add(root))
                        continue;

                    foreach (ArchiveSessionRecord record in ReadSessions(root))
                    {
                        if (record == null || string.IsNullOrEmpty(record.Id))
                            continue;

                        // A session line with no episode behind it is not a session: the file may still name
                        // it - an export writes the line before the first episode lands - but a rail that
                        // listed it would offer a session with nothing to open. Filtering before the
                        // duplicate check is deliberate, so a bare line in the folder the user picked does
                        // not hide the same session's episodes in the fallback.
                        if (record.Episodes == 0)
                            continue;

                        if (!seenSessions.Add(record.Id))
                            continue;

                        merged.Add(record);
                    }
                }
            }

            return merged
                .Select((record, index) => (record, index))
                .OrderByDescending(pair => StartInstant(pair.record.StartedAt) ?? DateTime.MinValue)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.record)
                .ToList();
        }

        /// <summary>The instant a session line names, or null when it names none a reader can place in time.</summary>
        private static DateTime? StartInstant(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso))
                return null;

            return DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
                ? parsed
                : null;
        }
    }
}
