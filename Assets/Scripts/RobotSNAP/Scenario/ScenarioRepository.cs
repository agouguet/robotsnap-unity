using System;
using System.Collections.Generic;
using System.IO;
using VYaml.Serialization;

namespace RobotSNAP.Core.Scenario
{
    /// <summary>
    /// Filesystem and YAML boundary for scenario documents and their metadata cache.
    ///
    /// An entry is only served for the exact revision of the file it was read from: each one remembers the
    /// file's last write time, and a file rewritten since makes it stale. The Editor runs Play sessions
    /// without reloading the domain or the scene, so this repository outlives the session that filled it -
    /// without that comparison a scenario edited between two runs was answered from the previous run.
    /// </summary>
    public sealed class ScenarioRepository
    {
        private readonly Dictionary<string, ScenarioData> _scenarios = new();
        private readonly Dictionary<string, ScenarioInfo> _infos = new();
        private readonly Dictionary<string, DateTime> _scenarioWriteTimesUtc = new();
        private readonly Dictionary<string, DateTime> _infoWriteTimesUtc = new();

        public int CacheSize => _scenarios.Count;

        /// <summary>Answers from the cache without checking the file, for a caller that holds no path.</summary>
        public bool TryGetScenario(string id, out ScenarioData scenario)
        {
            return TryGetScenario(id, null, out scenario);
        }

        /// <summary>
        /// Answers the cached document when it is still the one on disk. A file that has been rewritten
        /// drops both of its entries and is read again by whoever asked.
        /// </summary>
        public bool TryGetScenario(string id, string filePath, out ScenarioData scenario)
        {
            scenario = null;

            if (!_scenarios.TryGetValue(id, out ScenarioData cached))
                return false;

            if (!IsStillCurrent(id, filePath, _scenarioWriteTimesUtc))
            {
                Drop(id);
                return false;
            }

            scenario = cached;
            return true;
        }

        public ScenarioData Load(string id, string filePath, bool cache)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A scenario file path is required.", nameof(filePath));

            DateTime writeTimeUtc = File.GetLastWriteTimeUtc(filePath);
            ScenarioData scenario = Parse(File.ReadAllBytes(filePath), filePath);
            if (cache)
            {
                _scenarios[id] = scenario;
                _scenarioWriteTimesUtc[id] = writeTimeUtc;
                if (scenario.Info != null)
                {
                    _infos[id] = scenario.Info;
                    _infoWriteTimesUtc[id] = writeTimeUtc;
                }
            }
            return scenario;
        }

        public ScenarioData Parse(byte[] yamlBytes, string sourceName)
        {
            try
            {
                ScenarioData scenario = YamlSerializer.Deserialize<ScenarioData>(yamlBytes);
                if (scenario == null) throw new InvalidDataException("Deserialization returned null");
                if (!scenario.IsValid(out string validationError))
                    throw new InvalidDataException($"Validation failed: {validationError}");
                return scenario;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"Failed to parse YAML from {sourceName}: {exception.Message}", exception);
            }
        }

        public ScenarioInfo GetInfo(string id, string filePath)
        {
            if (_infos.TryGetValue(id, out ScenarioInfo cached))
            {
                if (IsStillCurrent(id, filePath, _infoWriteTimesUtc))
                    return cached;

                RemoveInfo(id);
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

            try
            {
                DateTime writeTimeUtc = File.GetLastWriteTimeUtc(filePath);
                ScenarioData scenario = YamlSerializer.Deserialize<ScenarioData>(File.ReadAllBytes(filePath));
                if (scenario?.Info == null) return null;
                _infos[id] = scenario.Info;
                _infoWriteTimesUtc[id] = writeTimeUtc;
                return scenario.Info;
            }
            catch
            {
                return null;
            }
        }

        public void Clear()
        {
            _scenarios.Clear();
            _infos.Clear();
            _scenarioWriteTimesUtc.Clear();
            _infoWriteTimesUtc.Clear();
        }

        /// <summary>
        /// True when the cache still describes the file. A caller with no path - a document parsed from a
        /// TextAsset rather than read from disk - has nothing to compare against and keeps its entry.
        /// </summary>
        private static bool IsStillCurrent(string id, string filePath, Dictionary<string, DateTime> writeTimesUtc)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return true;

            if (!writeTimesUtc.TryGetValue(id, out DateTime cachedWriteTimeUtc))
                return false;

            return File.Exists(filePath) && File.GetLastWriteTimeUtc(filePath) == cachedWriteTimeUtc;
        }

        private void Drop(string id)
        {
            _scenarios.Remove(id);
            _scenarioWriteTimesUtc.Remove(id);
            RemoveInfo(id);
        }

        private void RemoveInfo(string id)
        {
            _infos.Remove(id);
            _infoWriteTimesUtc.Remove(id);
        }
    }
}
