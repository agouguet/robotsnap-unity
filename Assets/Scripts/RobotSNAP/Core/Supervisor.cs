// Scripts/RobotSNAP/Supervisor.cs
using System;
using System.Collections.Generic;
using System.IO;
using RobotSNAP.Metrics;
using RobotSNAP.ROS;
using UnityEngine;
using RobotSNAP.Core;

namespace RobotSNAP
{
    /// <summary>
    /// Supervisor principal - Gère la configuration globale, l'horloge et la persistance.
    /// Ne gère PAS les environnements (délégué à ScenarioManager).
    /// </summary>
    [ExecuteAlways]
    public sealed class Supervisor : MonoBehaviour
    {
        #region Singleton

        private static Supervisor _instance;
        public static Supervisor Instance
        {
            get
            {
                if (_instance == null)
                    _instance = FindAnyObjectByType<Supervisor>();
                return _instance;
            }
        }

        #endregion

        #region Serialized Fields

        [Header("Configuration")]
        [Tooltip("Configuration par défaut (ScriptableObject)")]
        [SerializeField] private SimulationConfig _defaultConfig;

        [Header("References")]
        [Tooltip("Horloge globale de la simulation")]
        [SerializeField] private Clock _clock;

        [Header("Config Persistence")]
        [Tooltip("Sauvegarde automatique à la fermeture")]
        [SerializeField] private bool _autoSaveOnQuit = false;

        [Header("Debug")]
        [Tooltip("Active les logs de débogage")]
        [SerializeField] private bool _logEvents = true;

        #endregion

        #region Private Fields

        private SimulationConfig _runtimeConfig;
        private bool _isInitialized;

        #endregion

        #region Public Properties

        public SimulationConfig ActiveConfig => Application.isPlaying ? _runtimeConfig : _defaultConfig;
        public bool IsInitialized => _isInitialized;

        #endregion

        #region Events

        public event Action<SimulationConfig> OnConfigChanged;
        public event Action OnInitialized;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;

            InitializeDefaultConfig();
            SetupClock();
            ApplyTopicNames();

            if (Application.isPlaying)
                DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (Application.isPlaying)
            {
                _runtimeConfig = _defaultConfig.Clone();
                _runtimeConfig.name = "RuntimeConfig";
                _runtimeConfig.ApplyTimeSettings();
            }

            // The names of the streams are the ones of the configuration in force, and they have to be in
            // place before any publisher registers: a component reads them once, when it builds the name it
            // answers on, and never looks again.
            ApplyTopicNames();

            _isInitialized = true;
            OnInitialized?.Invoke();

            if (_logEvents)
                Debug.Log($"[Supervisor] Initialized. Time scale: {ActiveConfig.TimeScale}");
        }

        private void OnApplicationQuit()
        {
            FlushMetrics();
            if (_autoSaveOnQuit && _runtimeConfig != null)
                SaveDefaultConfig();
        }

        /// <summary>
        /// A pause is where a mobile build is killed without ever seeing a quit, so the same guaranteed write
        /// happens there: an episode that finished a moment earlier is still only in RAM.
        /// </summary>
        private void OnApplicationPause(bool paused)
        {
            if (paused)
                FlushMetrics();
        }

        #endregion

        #region Private Methods

        private void InitializeDefaultConfig()
        {
            if (_defaultConfig == null)
            {
                _defaultConfig = ScriptableObject.CreateInstance<SimulationConfig>();
                _defaultConfig.name = "DefaultConfig";
                Debug.Log("[Supervisor] Created default SimulationConfig");
            }
        }

        /// <summary>
        /// Hands the names of the configuration in force to the one table every stream is built from, and the
        /// environment prefix with them. It is called wherever the configuration changes rather than read on
        /// demand, because a stream is named once, when its publisher registers.
        /// </summary>
        private void ApplyTopicNames()
        {
            SimulationConfig config = ActiveConfig ?? _defaultConfig;
            RobotSNAPTopics.Use(
                config != null ? config.Topics : null,
                config != null ? config.RosPublishFrequency : 0f);
        }

        private void SetupClock()
        {
            if (_clock == null)
            {
                // The lookup and the creation are one decision, taken by the clock itself: asking
                // "is there one, and if not make one" here would race with every other component that
                // asks the same question in the same frame, and two clocks is a session whose time is
                // kept twice. See Clock.EnsureExists.
                _clock = Clock.EnsureExists();
            }
        }

        /// <summary>
        /// Writes every finished episode the session has not archived yet. The recorder exports as each episode
        /// finishes, but an episode that ended between that write and the application going away is still only
        /// in memory, and this is the last chance to put it on disk. The episode in progress is deliberately
        /// not closed: this saves what finished, it does not end the run. Only a real play session writes,
        /// because an editor that is not playing has no run worth saving and writing there would only scatter
        /// test data into the project.
        /// </summary>
        private static void FlushMetrics()
        {
            if (!Application.isPlaying)
                return;

            try
            {
                MetricsExporter.Export(MetricsStore.Instance);
            }
            catch (Exception exception)
            {
                // Shutting down is not a place to throw: the write is best-effort, and a log line is the only
                // other thing it could lose.
                Debug.LogWarning($"[Supervisor] flushing metrics on exit failed: {exception.Message}");
            }
        }

        #endregion

        #region Public API - Configuration

        public void UpdateConfig(SimulationConfig newConfig)
        {
            if (newConfig == null) return;

            var target = Application.isPlaying ? _runtimeConfig : _defaultConfig;
            if (target == null) return;

            newConfig.CopyTo(target);
            target.ApplyTimeSettings();

            ApplyTopicNames();
            OnConfigChanged?.Invoke(target);
            if (_logEvents) Debug.Log("[Supervisor] Configuration updated");
        }

        public void SaveDefaultConfig()
        {
            if (_defaultConfig == null) return;
            var source = Application.isPlaying ? _runtimeConfig : _defaultConfig;
            source?.CopyTo(_defaultConfig);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(_defaultConfig);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
            if (_logEvents) Debug.Log("[Supervisor] Default config saved");
        }

        public void SaveConfigToJson(string fileName)
        {
            var source = Application.isPlaying ? _runtimeConfig : _defaultConfig;
            if (source == null) return;

            ConfigPersistence.Save(source, fileName);
            if (_logEvents) Debug.Log($"[Supervisor] Config saved to JSON: {ConfigPersistence.GetPath(fileName)}");
        }

        public void ResetConfigToDefault()
        {
            var fresh = ScriptableObject.CreateInstance<SimulationConfig>();
            fresh.ResetToDefaults();

            if (Application.isPlaying && _runtimeConfig != null)
            {
                fresh.CopyTo(_runtimeConfig);
                _runtimeConfig.ApplyTimeSettings();
            }
            fresh.CopyTo(_defaultConfig);

            ApplyTopicNames();
            OnConfigChanged?.Invoke(ActiveConfig);
            Debug.Log("[Supervisor] Config reset to default");
        }

        #endregion

        #region Public API - Clock Control

        public void TogglePause() => _clock?.TogglePause();
        public void Pause() => _clock?.Pause();
        public void Resume() => _clock?.Resume();
        public bool IsPaused => _clock != null && _clock.IsPaused;

        #endregion

        #region Editor Utilities

#if UNITY_EDITOR
        [ContextMenu("Config/Save as Default")]
        private void EditorSaveDefaultConfig() => SaveDefaultConfig();

        [ContextMenu("Config/Reset to Default")]
        private void EditorResetConfig() => ResetConfigToDefault();

        [ContextMenu("Config/Save to JSON")]
        private void EditorSaveConfigToJson()
        {
            string fileName = $"config_{DateTime.Now:yyyyMMdd_HHmmss}";
            SaveConfigToJson(fileName);
        }

        [ContextMenu("Debug/Log Status")]
        private void EditorLogStatus()
        {
            var cfg = ActiveConfig;
            Debug.Log($"[Supervisor] Status:\n" +
                      $"  Initialized: {_isInitialized}\n" +
                      $"  Time Scale: {(cfg != null ? cfg.TimeScale : 1f)}\n" +
                      $"  Dataset Path: {(cfg != null ? cfg.DatasetPath : "none")}\n" +
                      $"  Default Scenario: {(cfg != null ? cfg.DefaultScenario : "none")}");
        }
#endif

        #endregion
    }
}
