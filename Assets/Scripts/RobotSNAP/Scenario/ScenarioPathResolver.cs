using System;
using System.Collections.Generic;
using System.IO;

namespace RobotSNAP.Core.Scenario
{
    /// <summary>
    /// Resolves scenario and dataset paths without depending on Unity scene state.
    /// </summary>
    public static class ScenarioPathResolver
    {
        /// <summary>
        /// Name of the environment variable that relocates the scenarios folder. It carries the absolute
        /// path of the folder itself, not of a StreamingAssets root.
        ///
        /// A built player copies StreamingAssets next to the executable, so the scenario files it plays are
        /// a snapshot taken at build time: editing the YAML in the project changed nothing the player could
        /// see. Pointing this variable at the project folder is how a build reads the live files. Maps are
        /// untouched and keep coming from StreamingAssets.
        /// </summary>
        public const string ScenariosDirectoryOverrideVariable = "ROBOTSNAP_SCENARIOS_DIR";

        /// <summary>
        /// Reads <see cref="ScenariosDirectoryOverrideVariable"/> and answers the folder it designates, or
        /// false when there is nothing usable: unset, blank, not absolute, or naming a folder that does not
        /// exist. A variable that cannot be honoured falls back to the project-relative default rather than
        /// pointing the loader at a folder that is not there.
        /// </summary>
        public static bool TryResolveScenariosOverride(out string scenariosPath)
        {
            scenariosPath = null;

            // Qualified: this namespace sits next to RobotSNAP.Environment, which would otherwise win the name.
            string configured = System.Environment.GetEnvironmentVariable(ScenariosDirectoryOverrideVariable);
            if (string.IsNullOrWhiteSpace(configured))
                return false;

            if (!Path.IsPathRooted(configured))
                return false;

            if (!Directory.Exists(configured))
                return false;

            scenariosPath = Path.GetFullPath(configured);
            return true;
        }

        public static (string ScenariosPath, string MapsPath) ResolvePaths(
            string streamingAssetsPath,
            string scenariosFolder,
            string datasetFolder)
        {
            TryResolveScenariosOverride(out string overridePath);
            return ResolvePaths(streamingAssetsPath, scenariosFolder, datasetFolder, overridePath);
        }

        /// <summary>
        /// Resolves the two folders with the scenarios override already decided, so a caller - a test, or a
        /// build script - can name the folder without going through the environment.
        /// </summary>
        public static (string ScenariosPath, string MapsPath) ResolvePaths(
            string streamingAssetsPath,
            string scenariosFolder,
            string datasetFolder,
            string scenariosOverridePath)
        {
            if (string.IsNullOrWhiteSpace(streamingAssetsPath))
                throw new ArgumentException("A StreamingAssets path is required.", nameof(streamingAssetsPath));

            string mapsPath = Path.Combine(
                streamingAssetsPath,
                string.IsNullOrWhiteSpace(datasetFolder) ? "Dataset" : datasetFolder);

            string scenariosPath = string.IsNullOrWhiteSpace(scenariosOverridePath)
                ? Path.Combine(streamingAssetsPath, string.IsNullOrWhiteSpace(scenariosFolder) ? "Scenarios" : scenariosFolder)
                : scenariosOverridePath;

            return (scenariosPath, mapsPath);
        }

        public static string FindScenarioFile(string scenariosPath, string scenarioName, IEnumerable<string> validExtensions)
        {
            if (string.IsNullOrWhiteSpace(scenariosPath) || string.IsNullOrWhiteSpace(scenarioName) || validExtensions == null)
                return null;

            foreach (string extension in validExtensions)
            {
                string path = Path.Combine(scenariosPath, scenarioName + extension);
                if (File.Exists(path)) return path;
            }

            return null;
        }
    }
}
