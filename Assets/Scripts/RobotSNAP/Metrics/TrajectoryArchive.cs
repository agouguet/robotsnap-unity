using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RobotSNAP.Metrics
{
    /// <summary>
    /// Where one episode's trajectory sits inside a session's archive: the file, the byte range the record
    /// occupies in it, and how much is in there. It travels with the catalogue line, so a reader that has the
    /// line can find the bytes without opening - or parsing - the whole archive.
    ///
    /// Every key is spelled out with a <see cref="JsonPropertyAttribute"/> because the Python side is written
    /// against those exact names, and a reader that guesses a spelling reads nothing.
    /// </summary>
    public sealed class TrajectoryRef
    {
        /// <summary>Archive path relative to the export root, with <c>/</c> separators.</summary>
        [JsonProperty("file")] public string File;

        /// <summary>Byte offset of the record's first byte inside the archive.</summary>
        [JsonProperty("offset")] public long Offset;

        /// <summary>Length of the record in bytes, its own length field included.</summary>
        [JsonProperty("length")] public long Length;

        /// <summary>Number of agent tracks the record carries.</summary>
        [JsonProperty("agents")] public int Agents;

        /// <summary>Total number of kept points over every track of the record.</summary>
        [JsonProperty("points")] public long Points;

        /// <summary>How the positions were packed; <see cref="TrajectoryArchive.EncodingName"/>.</summary>
        [JsonProperty("encoding")] public string Encoding = TrajectoryArchive.EncodingName;
    }

    /// <summary>
    /// A session's trajectories as one append-only binary file, beside an append-only catalogue that names
    /// each episode.
    ///
    /// The whole-session export rewrote a file that grew with the session after every finished episode, so a
    /// training campaign paid for every episode again on every episode, and the RAM it held was the session's
    /// whole history. Here one episode is appended once and never touched again: the cost of episode n is the
    /// size of episode n, and a reader that wants one episode seeks to its byte range instead of parsing the
    /// ones before it.
    ///
    /// The archive is deliberately dumb about failure. It cannot guarantee a disk, so a write that fails is
    /// reported and the episode is left in memory instead of being lost: the caller keeps the map inline in
    /// the catalogue line, which is the one thing worse than a missing archive - an archive whose loss
    /// silently took the run with it.
    ///
    /// Positions are packed as millimetre offsets from each track's first point, on <c>int16</c>, which
    /// covers 32.767 m either side of the origin: a trajectory is drawn and not measured from, so a tenth of
    /// a millimetre is already finer than the control loop, and a delta that would overflow is clamped rather
    /// than allowed to wrap into a point on the wrong side of the map.
    /// </summary>
    public sealed class TrajectoryArchive
    {
        /// <summary>Magic written at the head of every archive file.</summary>
        public const string FileMagic = "RSNPTRAJ";

        /// <summary>Magic written at the head of every record.</summary>
        public const string RecordMagic = "REC1";

        /// <summary>Name of the ref's encoding, and of the packing both sides speak.</summary>
        public const string EncodingName = "int16mm";

        /// <summary>Bytes of an archive file header: the magic, the version and the reserved word.</summary>
        public const int FileHeaderLength = 12;

        /// <summary>Name of the append-only line naming every session.</summary>
        public const string SessionsFileName = "sessions.jsonl";

        /// <summary>Name of the append-only line naming every archived episode.</summary>
        public const string CatalogueFileName = "catalogue.jsonl";

        /// <summary>Folder, under the export root, the per-session archives live in.</summary>
        public const string TrajectoriesFolderName = "trajectories";

        /// <summary>
        /// What one export root holds, counted rather than read: enough for a reader to decide whether
        /// deleting it is a decision at all, and to say in one line what it would cost.
        /// </summary>
        public readonly struct ArchiveSummary
        {
            /// <summary>Number of session lines in <c>sessions.jsonl</c>.</summary>
            public readonly int Sessions;

            /// <summary>Number of episode lines in <c>catalogue.jsonl</c>.</summary>
            public readonly int Episodes;

            /// <summary>Bytes of every file under <c>trajectories/</c>.</summary>
            public readonly long Bytes;

            public ArchiveSummary(int sessions, int episodes, long bytes)
            {
                Sessions = sessions;
                Episodes = episodes;
                Bytes = bytes;
            }

            /// <summary>True when there is nothing to delete: no session and no episode.</summary>
            public bool IsEmpty => Episodes == 0 && Sessions == 0;
        }

        private const int Version = 1;
        private const int RecordHeaderLength = 12;
        private const int AgentFixedLength = 26;

        private readonly string _root;
        private readonly HashSet<string> _announcedSessions = new(StringComparer.Ordinal);

        /// <summary>Last write failure, or null when the last operation succeeded.</summary>
        public string LastError { get; private set; }

        /// <summary>The export root this archive is rooted at.</summary>
        public string Root => _root;

        /// <summary>
        /// Opens - and creates when needed - the archive rooted at <paramref name="root"/>. Creating the
        /// folders eagerly is what lets the first append be a plain append; a folder that cannot be made is
        /// reported through <see cref="LastError"/> and through every later operation failing, rather than
        /// thrown at whoever happened to construct this.
        /// </summary>
        public TrajectoryArchive(string root)
        {
            _root = root ?? string.Empty;
            if (_root.Length == 0)
            {
                LastError = "no export root";
                return;
            }

            try
            {
                Directory.CreateDirectory(_root);
                Directory.CreateDirectory(Path.Combine(_root, TrajectoriesFolderName));
                LastError = null;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
            }
        }

        /// <summary>
        /// Appends one record for <paramref name="episode"/> to its session's archive and returns where it
        /// landed, or null when it could not be written - in which case the caller still holds the episode's
        /// map and must put it in the catalogue line.
        /// </summary>
        public TrajectoryRef Append(EpisodeMetrics episode)
        {
            if (episode == null)
            {
                LastError = "no episode";
                return null;
            }

            string session = string.IsNullOrEmpty(episode.Session) ? "session" : episode.Session;
            string relative = TrajectoriesFolderName + "/" + session + ".rbt";

            try
            {
                if (_root.Length == 0)
                    throw new InvalidOperationException("no export root");

                string file = Path.Combine(_root, TrajectoriesFolderName, session + ".rbt");
                byte[] record = BuildRecord(episode, out int agents, out long points);

                long offset;
                using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    if (stream.Length == 0)
                        WriteFileHeader(stream);

                    offset = stream.Length;
                    stream.Write(record, 0, record.Length);
                    stream.Flush();
                }

                LastError = null;
                return new TrajectoryRef
                {
                    File = relative,
                    Offset = offset,
                    Length = record.Length,
                    Agents = agents,
                    Points = points,
                    Encoding = EncodingName,
                };
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogWarning(
                    $"[TrajectoryArchive] appending episode '{episode.Id}' to '{relative}' failed: " +
                    exception.Message);
                return null;
            }
        }

        /// <summary>
        /// Reads the record <paramref name="reference"/> names back into tracks keyed by agent id, each point
        /// <c>{ t, x, z }</c> at the millimetre and millisecond precision the record was written with. A
        /// missing file, a bad magic, a byte range that does not match or a record that ends before its own
        /// length says it should all read as false: a half-written record is skipped, never guessed at.
        /// </summary>
        public bool TryRead(TrajectoryRef reference, out Dictionary<string, List<double[]>> tracks)
        {
            tracks = null;
            if (reference == null || string.IsNullOrEmpty(reference.File))
                return false;

            try
            {
                string path = Path.Combine(
                    _root,
                    reference.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    return false;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (reference.Offset < FileHeaderLength ||
                    reference.Length < RecordHeaderLength ||
                    reference.Offset + reference.Length > stream.Length)
                    return false;

                stream.Seek(reference.Offset, SeekOrigin.Begin);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                byte[] magic = reader.ReadBytes(RecordMagic.Length);
                if (magic.Length != RecordMagic.Length || !Matches(magic, RecordMagic))
                    return false;

                uint recordLength = reader.ReadUInt32();
                if (recordLength != reference.Length)
                    return false;

                long end = reference.Offset + recordLength;
                uint agentCount = reader.ReadUInt32();
                var result = new Dictionary<string, List<double[]>>();

                for (uint agent = 0; agent < agentCount; agent++)
                {
                    if (stream.Position + 2 > end)
                        return false;

                    ushort idLength = reader.ReadUInt16();
                    if (stream.Position + idLength > end)
                        return false;

                    string id = Encoding.UTF8.GetString(reader.ReadBytes(idLength));

                    if (stream.Position + 4 + 4 + 8 + 4 + 4 > end)
                        return false;

                    uint count = reader.ReadUInt32();
                    reader.ReadUInt32(); // stride: metadata for a reader's own bookkeeping, not for the maths
                    double t0 = reader.ReadDouble();
                    int originXmm = reader.ReadInt32();
                    int originZmm = reader.ReadInt32();

                    if (count > int.MaxValue || stream.Position + 8L * count > end)
                        return false;

                    var tMs = new uint[count];
                    for (int index = 0; index < tMs.Length; index++)
                        tMs[index] = reader.ReadUInt32();

                    var dxMm = new short[count];
                    for (int index = 0; index < dxMm.Length; index++)
                        dxMm[index] = reader.ReadInt16();

                    var dzMm = new short[count];
                    for (int index = 0; index < dzMm.Length; index++)
                        dzMm[index] = reader.ReadInt16();

                    var points = new List<double[]>((int)count);
                    for (int index = 0; index < tMs.Length; index++)
                    {
                        points.Add(new[]
                        {
                            t0 + tMs[index] / 1000.0,
                            (originXmm + dxMm[index]) / 1000.0,
                            (originZmm + dzMm[index]) / 1000.0,
                        });
                    }

                    result[id] = points;
                }

                if (stream.Position != end)
                    return false;

                tracks = result;
                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Appends the episode's catalogue line: the document as it goes on the wire, without the heavy
        /// <c>trajectories</c> map when the archive holds it, and with the map left in when the archive could
        /// not be written, so a failed archive costs nothing but bytes.
        /// </summary>
        public void AppendCatalogue(EpisodeMetrics episode)
        {
            if (episode == null)
                return;

            try
            {
                JObject document = JObject.FromObject(episode);
                if (episode.TrajectoryRef != null)
                    document.Remove("trajectories");

                AppendLine(Path.Combine(_root, CatalogueFileName), document.ToString(Formatting.None));
                LastError = null;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogWarning(
                    $"[TrajectoryArchive] writing the catalogue line of '{episode.Id}' failed: " +
                    exception.Message);
            }
        }

        /// <summary>
        /// Appends this session's line to <c>sessions.jsonl</c>, once per session for the life of this
        /// archive. The line is what tells a reader which sessions exist without walking every archive, and a
        /// session exported twice must not appear twice in it.
        ///
        /// <paramref name="name"/> is the name the user gave the session, and it is written only when there is
        /// one: a session nobody renamed carries no <c>name</c> key at all, because an empty string on disk
        /// would read as a name somebody chose and then blanked.
        /// </summary>
        public void EnsureSessionLine(string sessionId, string startedAt, string name = null)
        {
            if (string.IsNullOrEmpty(sessionId) || !_announcedSessions.Add(sessionId))
                return;

            try
            {
                var line = new JObject
                {
                    ["id"] = sessionId,
                    ["started_at"] = startedAt,
                    ["schema"] = 1,
                };
                if (!string.IsNullOrEmpty(name))
                    line["name"] = name;

                AppendLine(Path.Combine(_root, SessionsFileName), line.ToString(Formatting.None));
                LastError = null;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                Debug.LogWarning($"[TrajectoryArchive] writing session '{sessionId}' failed: {exception.Message}");
            }
        }

        /// <summary>
        /// Removes one episode from the catalogue. The session's <c>.rbt</c> archive is left exactly as it is:
        /// its records are addressed by byte range and only ever appended to, so dropping one line never
        /// invalidates the ones around it, and rewriting the file to reclaim the bytes would be the one thing
        /// that could.
        ///
        /// It never throws and is idempotent: an id that is already absent returns true, because "the archive
        /// does not hold this episode" is the state the call promises and the state it finds. A false return
        /// means the catalogue could not be rewritten, so the line may still be there.
        /// </summary>
        public static bool DeleteEpisode(string root, string episodeId)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(episodeId))
                return true;

            try
            {
                return RewriteLines(
                    Path.Combine(root, CatalogueFileName),
                    line => string.Equals(LineId(line), episodeId, StringComparison.Ordinal) ? null : line,
                    out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Removes every trace of one session: its catalogue lines, its session line and its trajectory
        /// archive. Like <see cref="DeleteEpisode"/> it never throws and is idempotent - a session the folder
        /// does not hold returns true - and the lines it leaves behind are rewritten byte for byte, so an
        /// episode an older version wrote survives untouched.
        /// </summary>
        public static bool DeleteSession(string root, string sessionId)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(sessionId))
                return true;

            try
            {
                bool ok = RewriteLines(
                    Path.Combine(root, CatalogueFileName),
                    line => string.Equals(CatalogueSession(line), sessionId, StringComparison.Ordinal)
                        ? null
                        : line,
                    out _);

                ok &= RewriteLines(
                    Path.Combine(root, SessionsFileName),
                    line => string.Equals(LineId(line), sessionId, StringComparison.Ordinal) ? null : line,
                    out _);

                ok &= DeleteFileIfPresent(
                    Path.Combine(root, TrajectoriesFolderName, sessionId + ".rbt"));

                return ok;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Renames one episode by replacing the <c>name</c> key of its catalogue line and nothing else: the
        /// line is parsed, the one key is set or removed, and the line is written back. An empty or null name
        /// removes the key rather than writing an empty string, so a never-named and an un-named episode end up
        /// in the same state and neither carries the computed <c>01_default</c> label the interface shows.
        ///
        /// Returns true when the line was found and now carries the name - including when it already did - and
        /// false when the root or the id is absent, or when the file could not be rewritten. It never throws.
        /// </summary>
        public static bool RenameEpisode(string root, string episodeId, string name)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(episodeId))
                return false;

            bool found = false;
            try
            {
                bool ok = RewriteLines(
                    Path.Combine(root, CatalogueFileName),
                    line =>
                    {
                        if (!string.Equals(LineId(line), episodeId, StringComparison.Ordinal))
                            return line;

                        found = true;
                        JObject document = JObject.Parse(line);
                        ApplyName(document, name);
                        return document.ToString(Formatting.None);
                    },
                    out _);

                return ok && found;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Renames one session by replacing the <c>name</c> key of its <c>sessions.jsonl</c> line. The rules
        /// are the ones <see cref="RenameEpisode"/> follows: only the key changes, an empty name removes it,
        /// and the answer says whether the line was found.
        /// </summary>
        public static bool RenameSession(string root, string sessionId, string name)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(sessionId))
                return false;

            bool found = false;
            try
            {
                bool ok = RewriteLines(
                    Path.Combine(root, SessionsFileName),
                    line =>
                    {
                        if (!string.Equals(LineId(line), sessionId, StringComparison.Ordinal))
                            return line;

                        found = true;
                        JObject document = JObject.Parse(line);
                        ApplyName(document, name);
                        return document.ToString(Formatting.None);
                    },
                    out _);

                return ok && found;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Drops every <c>sessions.jsonl</c> line whose session the catalogue holds no episode for, and
        /// returns how many lines went. A session is a session because it ran something: a line an export
        /// wrote before its first episode landed, or one whose episodes were all deleted since, is a row a
        /// reader can open and find nothing in.
        ///
        /// It never throws, never creates a root, and writes through a temporary file only when there is a
        /// line to remove - so the common case costs one read and no write at all. A line whose id cannot be
        /// read is left alone: nothing proves it is empty, and dropping an unreadable line would be deleting
        /// data that the reader never managed to see.
        /// </summary>
        public static int PurgeEmptySessions(string root)
        {
            if (string.IsNullOrEmpty(root))
                return 0;

            try
            {
                string sessions = Path.Combine(root, SessionsFileName);
                if (!File.Exists(sessions))
                    return 0;

                var live = new HashSet<string>(StringComparer.Ordinal);
                string catalogue = Path.Combine(root, CatalogueFileName);
                if (File.Exists(catalogue))
                {
                    foreach (string line in File.ReadLines(catalogue))
                    {
                        string session = CatalogueSession(line);
                        if (!string.IsNullOrEmpty(session))
                            live.Add(session);
                    }
                }

                bool ok = RewriteLines(sessions, line =>
                {
                    string id = LineId(line);
                    if (string.IsNullOrEmpty(id))
                        return line;

                    return live.Contains(id) ? line : null;
                }, out int removed);

                return ok ? removed : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Counts what a root holds - session lines, episode lines and the bytes of the archives under
        /// <c>trajectories/</c> - so a caller can show the reader what a delete would cost before asking. It
        /// never throws and never guesses: a root that is absent or unreadable reads as the empty summary
        /// rather than as an error, because "there is nothing there" and "I could not look" both lead to the
        /// same harmless decision - do not offer a delete.
        /// </summary>
        public static ArchiveSummary Scan(string root)
        {
            if (string.IsNullOrEmpty(root))
                return new ArchiveSummary(0, 0, 0);

            try
            {
                int sessions = CountLines(Path.Combine(root, SessionsFileName));
                int episodes = CountLines(Path.Combine(root, CatalogueFileName));

                long bytes = 0;
                string folder = Path.Combine(root, TrajectoriesFolderName);
                if (Directory.Exists(folder))
                {
                    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch (Exception)
                        {
                            // A file that vanished between the walk and the read adds nothing, and is not a
                            // reason to fail the whole count.
                        }
                    }
                }

                return new ArchiveSummary(sessions, episodes, bytes);
            }
            catch (Exception)
            {
                return new ArchiveSummary(0, 0, 0);
            }
        }

        /// <summary>
        /// Deletes the saved records under <paramref name="root"/>: every <c>.rbt</c> archive under
        /// <c>trajectories/</c>, the two append-only lines, and any temporary file an interrupted write left
        /// behind. It is the only place in the project that removes an export, which is what makes "nothing
        /// else deletes them" something a reader can check rather than a promise to remember. It never throws
        /// and is idempotent: an absent or already emptied root returns true, because there is nothing left to
        /// remove either way. A false return means at least one file could not be deleted.
        /// </summary>
        public static bool DeleteAll(string root)
        {
            if (string.IsNullOrEmpty(root))
                return true;

            bool deleted = true;
            try
            {
                string folder = Path.Combine(root, TrajectoriesFolderName);
                DeleteMatching(folder, "*.rbt", ref deleted);
                DeleteMatching(folder, "*.tmp", ref deleted);

                TryDelete(Path.Combine(root, CatalogueFileName), ref deleted);
                TryDelete(Path.Combine(root, CatalogueFileName + ".tmp"), ref deleted);
                TryDelete(Path.Combine(root, SessionsFileName), ref deleted);
                TryDelete(Path.Combine(root, SessionsFileName + ".tmp"), ref deleted);

                // The single-episode export writes its temporary beside its destination, at the root itself.
                DeleteMatching(root, "*.tmp", ref deleted);
            }
            catch (Exception)
            {
                deleted = false;
            }

            return deleted;
        }

        private static int CountLines(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return 0;

                int lines = 0;
                foreach (string line in File.ReadLines(path))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lines++;
                }
                return lines;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void DeleteMatching(string folder, string pattern, ref bool deleted)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;

                foreach (string file in Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
                    TryDelete(file, ref deleted);
            }
            catch (Exception)
            {
                deleted = false;
            }
        }

        private static void TryDelete(string path, ref bool deleted)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
                deleted = false;
            }
        }

        /// <summary>Deletes one file and says whether it is gone, forgiving a file that was never there.</summary>
        private static bool DeleteFileIfPresent(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Rewrites an append-only line file, dropping or replacing only the lines <paramref name="transform"/>
        /// answers for. Every line it returns unchanged is written back exactly as it was read - never parsed
        /// and re-printed - so a line an earlier version wrote, with its own key order and spacing, survives an
        /// edit it had nothing to do with. Nothing is written when no line changed, and a file that is not there
        /// is reported as true: there was nothing to rewrite and nothing is left behind either way.
        ///
        /// It never throws. A root or a file the process may not touch reads as false, which the caller reports
        /// instead of letting the exception reach a frame.
        /// </summary>
        private static bool RewriteLines(string path, Func<string, string> transform, out int changed)
        {
            changed = 0;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return true;

                string[] lines = File.ReadAllText(path).Split('\n');
                var rewritten = new List<string>(lines.Length);

                foreach (string line in lines)
                {
                    string replacement = transform(line);
                    if (replacement != line)
                        changed++;
                    if (replacement != null)
                        rewritten.Add(replacement);
                }

                if (changed == 0)
                    return true;

                WriteAtomicText(path, string.Join("\n", rewritten));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Writes through a temporary file and moves it over the destination, so a reader that opens the file
        /// while it is being rewritten sees the previous complete document or the new one, never a half-written
        /// one. The temporary is written whole before the destination is touched, so the two-step move leaves
        /// at worst a moment where the destination is absent - never one where it is half a file.
        /// </summary>
        private static void WriteAtomicText(string path, string content)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));

            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporary, path);
        }

        /// <summary>The <c>id</c> a catalogue or session line names, or null when the line carries none.</summary>
        private static string LineId(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            try
            {
                return (string)JObject.Parse(line)["id"];
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The session a catalogue line belongs to: its own <c>session</c> field, or, for a line written before
        /// that field existed, the id of the episode itself. It is the same rule the catalogue reader groups
        /// by, so a delete and a purge can never disagree with a read about what an episode belongs to.
        /// </summary>
        private static string CatalogueSession(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            try
            {
                JObject document = JObject.Parse(line);
                string session = (string)document["session"];
                return string.IsNullOrEmpty(session) ? (string)document["id"] : session;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Sets a line's <c>name</c>, or removes the key when the name is empty: an empty string on disk would
        /// be a name somebody chose and blanked, which is not the same thing as a name nobody gave.
        /// </summary>
        private static void ApplyName(JObject document, string name)
        {
            if (string.IsNullOrEmpty(name))
                document.Remove("name");
            else
                document["name"] = name;
        }

        private static void WriteFileHeader(Stream stream)
        {
            byte[] magic = Encoding.ASCII.GetBytes(FileMagic);
            stream.Write(magic, 0, magic.Length);
            WriteLittleEndian(stream, (ushort)Version);
            WriteLittleEndian(stream, (ushort)0);
        }

        private static void WriteLittleEndian(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        /// <summary>
        /// The bytes of one record, length field included. The length is computed from the tracks before a
        /// single byte is written, so the field and the buffer cannot disagree - which is the property the
        /// reader relies on to decide a record is complete.
        /// </summary>
        private static byte[] BuildRecord(
            EpisodeMetrics episode,
            out int agentCount,
            out long pointCount)
        {
            var agents = new List<KeyValuePair<string, List<double[]>>>();
            if (episode.Trajectories != null)
            {
                foreach (KeyValuePair<string, List<double[]>> track in episode.Trajectories)
                    agents.Add(track);
            }

            agentCount = agents.Count;
            pointCount = 0;
            long blocks = 0;
            foreach (KeyValuePair<string, List<double[]>> track in agents)
            {
                int samples = CountSamples(track.Value);
                blocks += AgentFixedLength + IdLength(track.Key) + 8L * samples;
                pointCount += samples;
            }

            long recordLength = RecordHeaderLength + blocks;
            var buffer = new MemoryStream((int)Math.Min(recordLength, int.MaxValue));
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes(RecordMagic));
                writer.Write((uint)recordLength);
                writer.Write((uint)agentCount);

                foreach (KeyValuePair<string, List<double[]>> track in agents)
                    WriteAgent(writer, track.Key, track.Value, episode.TrajectoryStride);
            }

            return buffer.ToArray();
        }

        private static void WriteAgent(BinaryWriter writer, string id, List<double[]> points, int stride)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(id ?? string.Empty);
            int idLength = Math.Min(idBytes.Length, ushort.MaxValue);

            writer.Write((ushort)idLength);
            writer.Write(idBytes, 0, idLength);

            var kept = new List<double[]>(points?.Count ?? 0);
            if (points != null)
            {
                foreach (double[] sample in points)
                    if (IsSample(sample))
                        kept.Add(sample);
            }

            writer.Write((uint)kept.Count);
            writer.Write((uint)(stride < 1 ? 1 : stride));

            if (kept.Count == 0)
            {
                // A track that kept nothing still has a name and a length; filling the fixed fields with
                // zeroes keeps the record readable instead of making the reader wonder where the agent went.
                writer.Write(0.0);
                writer.Write(0);
                writer.Write(0);
                return;
            }

            double t0 = kept[0][0];
            int originXmm = (int)Millimetres(kept[0][1]);
            int originZmm = (int)Millimetres(kept[0][2]);

            writer.Write(t0);
            writer.Write(originXmm);
            writer.Write(originZmm);

            foreach (double[] sample in kept)
            {
                long milliseconds = (long)Math.Round(
                    (sample[0] - t0) * 1000.0,
                    MidpointRounding.AwayFromZero);
                if (milliseconds < 0) milliseconds = 0;
                if (milliseconds > uint.MaxValue) milliseconds = uint.MaxValue;
                writer.Write((uint)milliseconds);
            }

            foreach (double[] sample in kept)
                writer.Write(Delta((int)Millimetres(sample[1]), originXmm));

            foreach (double[] sample in kept)
                writer.Write(Delta((int)Millimetres(sample[2]), originZmm));
        }

        private void AppendLine(string path, string line)
        {
            if (_root.Length == 0)
                throw new InvalidOperationException("no export root");

            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static bool Matches(byte[] bytes, string text)
        {
            for (int index = 0; index < bytes.Length; index++)
                if (bytes[index] != (byte)text[index])
                    return false;
            return true;
        }

        private static int IdLength(string id)
            => Math.Min(Encoding.UTF8.GetByteCount(id ?? string.Empty), ushort.MaxValue);

        private static int CountSamples(List<double[]> points)
        {
            if (points == null)
                return 0;

            int count = 0;
            foreach (double[] sample in points)
                if (IsSample(sample))
                    count++;
            return count;
        }

        private static bool IsSample(double[] sample) => sample != null && sample.Length >= 3;

        private static long Millimetres(double metres)
        {
            double millimetres = metres * 1000.0;
            if (millimetres >= int.MaxValue) return int.MaxValue;
            if (millimetres <= int.MinValue) return int.MinValue;
            return (long)Math.Round(millimetres, MidpointRounding.AwayFromZero);
        }

        private static short Delta(int millimetres, int origin)
        {
            long delta = millimetres - (long)origin;
            if (delta > short.MaxValue) return short.MaxValue;
            if (delta < short.MinValue) return short.MinValue;
            return (short)delta;
        }
    }
}
