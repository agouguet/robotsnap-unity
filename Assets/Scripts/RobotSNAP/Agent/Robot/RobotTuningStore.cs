using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RobotSNAP.Agents
{
    /// <summary>
    /// Single filesystem boundary for the tunings a reader saved: one file per robot TYPE id, under
    /// <c>Application.persistentDataPath/RobotTuning</c>.
    ///
    /// It mirrors <c>ConfigPersistence</c>: a directory of its own, a <see cref="DirectoryOverride"/> so a test
    /// writes into a folder of its choosing rather than the one the application's user owns, and every call
    /// wrapping its own failure - a tuning is a convenience, and a name the file system refuses must not take
    /// the running session down with it.
    ///
    /// The key is the TYPE id (<c>jackal</c>) and not the roster id (<c>robot_2</c>): a scenario may place
    /// several robots of one type, and the figures a reader chose for a Jackal are what every Jackal of the
    /// fleet should drive with. Which robot of the list a value was typed on is a detail of the page, not of
    /// the tuning.
    /// </summary>
    public static class RobotTuningStore
    {
        private const string DirectoryName = "RobotTuning";
        private const string FileExtension = ".json";

        /// <summary>
        /// A folder to use in place of the real one, and null - the default - for the real one. It exists for
        /// tests: where the files go is the one thing a test cannot choose for itself.
        /// </summary>
        public static string DirectoryOverride { get; set; }

        /// <summary>Where the tunings are written, honouring <see cref="DirectoryOverride"/>.</summary>
        public static string TuningDirectoryPath => string.IsNullOrEmpty(DirectoryOverride)
            ? Path.Combine(Application.persistentDataPath, DirectoryName)
            : DirectoryOverride;

        /// <summary>Writes the tuning of a type, replacing the one already saved for it.</summary>
        public static void Save(string typeId, RobotTuning tuning)
        {
            if (tuning == null)
                throw new ArgumentNullException(nameof(tuning));

            string path = GetPath(typeId);
            try
            {
                Directory.CreateDirectory(TuningDirectoryPath);
                File.WriteAllText(path, tuning.ToJson());
            }
            catch (Exception e)
            {
                Debug.LogError($"[RobotTuningStore] Failed to save the tuning of '{typeId}': {e.Message}");
            }
        }

        /// <summary>
        /// Reads the tuning of a type. A type nothing was saved for, a file a crash left half-written and a
        /// name the file system refuses all read as false rather than as an error.
        /// </summary>
        public static bool TryLoad(string typeId, out RobotTuning tuning)
        {
            tuning = null;
            if (string.IsNullOrWhiteSpace(typeId))
                return false;

            string path;
            try
            {
                path = GetPath(typeId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!File.Exists(path))
                return false;

            try
            {
                tuning = RobotTuning.FromJson(File.ReadAllText(path));
                return tuning != null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[RobotTuningStore] Failed to read the tuning of '{typeId}': {e.Message}");
                tuning = null;
                return false;
            }
        }

        /// <summary>True when a type carries a saved tuning.</summary>
        public static bool Has(string typeId) => TryLoad(typeId, out _);

        /// <summary>
        /// Removes the saved tuning of a type, so the next build drives the built-in figures again. A tuning
        /// that is not there is reported as not removed rather than as a failure.
        /// </summary>
        public static bool Delete(string typeId)
        {
            string path;
            try
            {
                path = GetPath(typeId);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (!File.Exists(path))
                return false;

            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The path a type's tuning is written to, under the directory in force.</summary>
        public static string GetPath(string typeId)
        {
            if (string.IsNullOrWhiteSpace(typeId))
                throw new ArgumentException("A robot type id is required.", nameof(typeId));

            return Path.Combine(TuningDirectoryPath, FileNameFor(typeId));
        }

        /// <summary>
        /// The file name a type id becomes. A type id is authored by the project and is always a plain word,
        /// but it is user-influenced data once a scenario names it, so anything a path could be built from is
        /// dropped rather than trusted.
        /// </summary>
        private static string FileNameFor(string typeId)
        {
            string trimmed = typeId.Trim();
            var builder = new StringBuilder(trimmed.Length);
            foreach (char character in trimmed)
            {
                if (char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.')
                    builder.Append(character);
            }

            string safe = builder.ToString();
            if (safe.Length == 0 || safe == "." || safe == "..")
                throw new ArgumentException($"'{typeId}' is not a usable robot type id.", nameof(typeId));

            return safe + FileExtension;
        }
    }
}
