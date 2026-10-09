// Scripts/RobotSNAP/Core/Scenario/ScenarioLoader.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;
using VYaml.Serialization;
using Newtonsoft.Json;

namespace RobotSNAP.Core.Scenario
{

    public enum MapAssetKind { None, Image, Prefab, Scene }

    public class MapAsset
    {
        public MapAssetKind Kind;
        public Texture2D Texture;
        public GameObject Prefab;
        public string SceneName;  // Nouveau : nom de la scène à charger
        public Bounds Bounds;
    }


    public sealed class ScenarioLoader : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private bool _enableCaching = true;
        [SerializeField] private bool _logEvents = true;
        [SerializeField] private string[] _validExtensions = { ".yaml", ".yml" };
        [SerializeField] private TextAsset _fallbackYamlAsset;

        private readonly ScenarioRepository _scenarioRepository = new();
        private Dictionary<string, Texture2D> _loadedMaps = new();
        private string _scenariosPath;
        private string _mapsPath;
        private bool _isInitialized;

        public string ScenariosPath => _scenariosPath;
        public string MapsPath => _mapsPath;
        public int CacheSize => _scenarioRepository.CacheSize;

        public event Action<ScenarioData> OnScenarioLoaded;
        public event Action<string> OnScenarioError;
        public event Action OnPathsUpdated;

        private void Awake()
        {
            var supervisor = Supervisor.Instance;
            if (supervisor != null)
            {
                supervisor.OnConfigChanged += OnConfigChanged;
            }
        }

        private void OnDestroy()
        {
            var supervisor = Supervisor.Instance;
            if (supervisor != null)
            {
                supervisor.OnConfigChanged -= OnConfigChanged;
            }
        }

         private void OnConfigChanged(SimulationConfig config)
        {
            Debug.Log("[ScenarioLoader] Config changed, refreshing paths.");
            RefreshPathsFromConfig();
            
            ClearScenarioCache();
            ClearMapCache();
            
            OnPathsUpdated?.Invoke();
        }

        /// <summary>
        /// Met à jour les chemins à partir de la SimulationConfig active.
        /// </summary>
        public void RefreshPathsFromConfig()
        {
            var config = Supervisor.Instance?.ActiveConfig;
            if (config == null)
            {
                Debug.LogError("[ScenarioLoader] No active SimulationConfig found");
                return;
            }

            (_scenariosPath, _mapsPath) = ScenarioPathResolver.ResolvePaths(
                Application.streamingAssetsPath,
                config.ScenariosFolder,
                config.DatasetPath);

            if (ScenarioPathResolver.TryResolveScenariosOverride(out string overridePath))
                Debug.Log($"[ScenarioLoader] {ScenarioPathResolver.ScenariosDirectoryOverrideVariable} redirects the scenarios folder to: {overridePath}");

            Debug.Log($"[ScenarioLoader] Scenarios path set to: {_scenariosPath}");

            EnsureDirectoriesExist();
            _isInitialized = true;
        }

        private void EnsureDirectoriesExist()
        {
            if (!Directory.Exists(_scenariosPath))
            {
                Directory.CreateDirectory(_scenariosPath);
                if (_logEvents) Debug.Log($"[ScenarioLoader] Created scenarios directory: {_scenariosPath}");
            }
            if (!Directory.Exists(_mapsPath))
            {
                Directory.CreateDirectory(_mapsPath);
                if (_logEvents) Debug.Log($"[ScenarioLoader] Created maps directory: {_mapsPath}");
            }
        }

        #region Private Helper Methods

        private string FindScenarioFile(string scenarioName)
        {
            return ScenarioPathResolver.FindScenarioFile(_scenariosPath, scenarioName, _validExtensions);
        }

        private string FindMapFile(string mapName)
        {
            string[] imageExtensions = { ".png", ".jpg", ".jpeg" };
            
            foreach (var ext in imageExtensions)
            {
                string path = Path.Combine(_mapsPath, mapName + ext);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            return null;
        }

        private bool IsSceneExists(string sceneName)
        {
            Debug.Log($"[ScenarioLoader] Checking if scene exists: {sceneName}");
            Debug.Log($"[ScenarioLoader] Scene count in build settings: {SceneManager.sceneCountInBuildSettings}");
            #if UNITY_EDITOR
                foreach (var scene in EditorBuildSettings.scenes)
                {
                    if (scene.enabled)
                    {
                        string name = Path.GetFileNameWithoutExtension(scene.path);
                        if (name == sceneName)
                            return true;
                    }
                }
                return false;
            #else
                for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                {
                    Debug.Log($"[ScenarioLoader] Checking scene index {i} in build settings");
                    string path = SceneUtility.GetScenePathByBuildIndex(i);
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name == sceneName)
                        return true;
                }
                return false;
            #endif
        }

        #endregion

        #region Public API - Loading

        /// <summary>
        /// Charge un scénario par son nom
        /// </summary>
        /// <param name="scenarioName">Nom du scénario (sans extension)</param>
        /// <returns>Le scénario chargé ou null</returns>
        public ScenarioData LoadScenario(string scenarioName)
        {
            if (!_isInitialized)
            {
                OnScenarioError?.Invoke("ScenarioLoader not initialized");
                RefreshPathsFromConfig();
                // return null;
            }

            // Le fichier est cherché avant le cache : le cache doit pouvoir comparer son entrée à la
            // révision qui est sur le disque, et une entrée plus ancienne est ignorée.
            string filePath = FindScenarioFile(scenarioName);
            if (filePath == null)
            {
                string error = $"Scenario not found: {scenarioName} in {_scenariosPath}";
                OnScenarioError?.Invoke(error);
                Debug.LogError($"[ScenarioLoader] {error}");
                return null;
            }

            // Vérifier le cache, pour ce fichier précis
            if (_enableCaching && _scenarioRepository.TryGetScenario(scenarioName, filePath, out var cached))
            {
                if (_logEvents) Debug.Log($"[ScenarioLoader] Returning cached scenario: {scenarioName}");
                return cached;
            }

            Debug.Log($"[ScenarioLoader] Found scenario file: {filePath}");
            try
            {
                // Lire et parser
                var scenario = _scenarioRepository.Load(scenarioName, filePath, _enableCaching);
                
                OnScenarioLoaded?.Invoke(scenario);
                
                if (_logEvents)
                {
                    Debug.Log($"[ScenarioLoader] Loaded scenario: {scenario.Name} v{scenario.Version} from {filePath}");
                }
                
                return scenario;
            }
            catch (Exception e)
            {
                string error = $"Failed to load scenario '{scenarioName}': {e.Message}";
                OnScenarioError?.Invoke(error);
                Debug.LogError($"[ScenarioLoader] {error}");
                return null;
            }
        }
        
        /// <summary>
        /// Charge un scénario depuis un chemin de fichier absolu
        /// </summary>
        /// <param name="filePath">Chemin complet du fichier</param>
        /// <returns>Le scénario chargé ou null</returns>
        public ScenarioData LoadScenarioFromPath(string filePath)
        {
            if (!File.Exists(filePath))
            {
                OnScenarioError?.Invoke($"File not found: {filePath}");
                return null;
            }
            
            string scenarioName = Path.GetFileNameWithoutExtension(filePath);
            
            // Vérifier le cache, pour ce fichier précis : une réécriture du YAML le rend caduc.
            if (_enableCaching && _scenarioRepository.TryGetScenario(scenarioName, filePath, out var cached))
            {
                return cached;
            }
            
            try
            {
                var scenario = _scenarioRepository.Load(scenarioName, filePath, _enableCaching);
                
                OnScenarioLoaded?.Invoke(scenario);
                
                if (_logEvents)
                {
                    Debug.Log($"[ScenarioLoader] Loaded scenario from path: {scenario.Name}");
                }
                
                return scenario;
            }
            catch (Exception e)
            {
                OnScenarioError?.Invoke($"Failed to load from path '{filePath}': {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Charge le scénario depuis un fichier YAML intégré (TextAsset)
        /// </summary>
        /// <param name="asset">TextAsset contenant le YAML</param>
        /// <returns>Le scénario chargé ou null</returns>
        public ScenarioData LoadScenarioFromAsset(TextAsset asset)
        {
            if (asset == null)
            {
                OnScenarioError?.Invoke("Cannot load null TextAsset");
                return null;
            }
            
            try
            {
                byte[] yamlBytes = System.Text.Encoding.UTF8.GetBytes(asset.text);
                var scenario = _scenarioRepository.Parse(yamlBytes, asset.name);
                
                OnScenarioLoaded?.Invoke(scenario);
                
                if (_logEvents)
                {
                    Debug.Log($"[ScenarioLoader] Loaded scenario from asset: {scenario.Name}");
                }
                
                return scenario;
            }
            catch (Exception e)
            {
                OnScenarioError?.Invoke($"Failed to load from asset '{asset.name}': {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Charge le scénario par défaut (fallback)
        /// </summary>
        /// <returns>Le scénario par défaut ou null</returns>
        public ScenarioData LoadFallbackScenario()
        {
            if (_fallbackYamlAsset != null)
            {
                return LoadScenarioFromAsset(_fallbackYamlAsset);
            }
            return null;
        }
        
        /// <summary>
        /// Obtient les informations d'un scénario sans charger toutes les données
        /// </summary>
        /// <param name="scenarioName">Nom du scénario</param>
        /// <returns>Informations du scénario ou null</returns>
        public ScenarioInfo GetScenarioInfo(string scenarioName)
        {
            // Vérifier le cache d'infos
            // Trouver le fichier
            string filePath = FindScenarioFile(scenarioName);
            if (filePath == null)
            {
                return null;
            }
            
            return _scenarioRepository.GetInfo(scenarioName, filePath);
        }

        /// <summary>
        /// Charge une map (texture + bounds) à partir d'un identifiant.
        /// L'identifiant peut être :
        /// - "dataset" (ex: "basic") : une map aléatoire de ce dataset
        /// - "dataset/mapname" (ex: "basic/corner") : la map spécifique
        /// </summary>
        public bool LoadMapData(string mapIdentifier, out Texture2D texture, out Bounds bounds)
        {
            texture = null;
            bounds = new Bounds();

            if (string.IsNullOrWhiteSpace(mapIdentifier) || string.IsNullOrWhiteSpace(_mapsPath))
                return false;

            string basePath = _mapsPath;
            string mapName = mapIdentifier.Replace('\\', '/').Trim('/');

            string imagePath, jsonPath;

            int collectionSeparator = mapName.LastIndexOf('/');
            if (collectionSeparator > 0)
            {
                // Format "collection/mapname". The collection may itself contain folders,
                // for example "human_test/crowd/lobby".
                string collection = mapName.Substring(0, collectionSeparator);
                string map = mapName.Substring(collectionSeparator + 1);
                string collectionPath = Path.Combine(basePath,
                    collection.Replace('/', Path.DirectorySeparatorChar));
                string imageBasePath = Path.Combine(collectionPath, "png", map);
                imagePath = new[] { ".png", ".jpg", ".jpeg" }
                    .Select(extension => imageBasePath + extension)
                    .FirstOrDefault(File.Exists);
                jsonPath = Path.Combine(collectionPath, "json", map + ".json");
            }
            else
            {
                // A root-level image is an exact legacy map. Otherwise the identifier names
                // a collection and keeps the previous random-map behavior.
                string directImageBasePath = Path.Combine(basePath, mapName);
                string directImagePath = new[] { ".png", ".jpg", ".jpeg" }
                    .Select(extension => directImageBasePath + extension)
                    .FirstOrDefault(File.Exists);
                string directJsonPath = Path.Combine(basePath, mapName + ".json");
                if (!string.IsNullOrWhiteSpace(directImagePath) && File.Exists(directJsonPath))
                {
                    imagePath = directImagePath;
                    jsonPath = directJsonPath;
                }
                else
                {
                    // Choose a random map from the requested collection.
                    string dataset = mapName;
                    string pngFolder = Path.Combine(basePath, dataset, "png");
                    if (!Directory.Exists(pngFolder))
                    {
                        Debug.LogError($"[ScenarioLoader] Dataset folder not found: {pngFolder}");
                        return false;
                    }
                    string[] imageFiles = new[] { "*.png", "*.jpg", "*.jpeg" }
                        .SelectMany(pattern => Directory.GetFiles(pngFolder, pattern, SearchOption.TopDirectoryOnly))
                        .ToArray();
                    if (imageFiles.Length == 0)
                    {
                        Debug.LogError($"[ScenarioLoader] No occupancy-grid images in {pngFolder}");
                        return false;
                    }
                    int index = UnityEngine.Random.Range(0, imageFiles.Length);
                    string selected = Path.GetFileNameWithoutExtension(imageFiles[index]);
                    imagePath = imageFiles[index];
                    jsonPath = Path.Combine(basePath, dataset, "json", selected + ".json");
                }
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || !File.Exists(jsonPath))
            {
                Debug.LogError($"[ScenarioLoader] Map files missing for: {mapIdentifier}");
                return false;
            }

            // Charger texture
            byte[] pngData = File.ReadAllBytes(imagePath);
            texture = new Texture2D(2, 2);
            texture.LoadImage(pngData);

            // Charger JSON
            string json = File.ReadAllText(jsonPath);
            var metadata = JsonConvert.DeserializeObject<MapMetadata>(json);
            if (metadata?.bbox?.min == null || metadata.bbox.max == null ||
                metadata.bbox.min.Length < 2 || metadata.bbox.max.Length < 2)
            {
                Debug.LogError($"[ScenarioLoader] Invalid JSON metadata for {mapIdentifier}");
                Destroy(texture);
                texture = null;
                return false;
            }

            float[] min = metadata.bbox.min;
            float[] max = metadata.bbox.max;
            Vector3 center = new Vector3((min[0] + max[0]) / 2f, 0, (min[1] + max[1]) / 2f);
            Vector3 size = new Vector3(max[0] - min[0], 0, max[1] - min[1]);
            bounds = new Bounds(center, size);

            return true;
        }

        public GameObject LoadMapPrefab(string path)
        {
            // Si vous utilisez Resources, le chemin est sans extension
            GameObject prefab = Resources.Load<GameObject>(path);
            if (prefab == null)
                Debug.LogError($"[ScenarioLoader] Prefab not found at Resources path: {path}");
            return prefab;
        }

        // Classe interne pour la désérialisation
        [System.Serializable]
        private class MapMetadata
        {
            public BBox bbox;
        }

        [System.Serializable]
        private class BBox
        {
            public float[] min;
            public float[] max;
        }

        #endregion

        #region Public API - Map Loading

        /// <summary>
        /// Charge une image de map
        /// </summary>
        /// <param name="mapName">Nom de la map (sans extension)</param>
        /// <returns>Texture2D de la map ou null</returns>
        /// <summary>
        /// Charge une carte en essayant d'abord comme Texture2D, puis comme GameObject (Prefab).
        /// </summary>
        public MapAsset LoadMap(string mapName)
        {
            // 1. Tenter de charger comme une Scene (additive)
            if (IsSceneExists(mapName))
            {
                return new MapAsset
                {
                    Kind = MapAssetKind.Scene,
                    SceneName = mapName,
                    Texture = null,
                    Prefab = null,
                    Bounds = new Bounds(Vector3.zero, Vector3.one)
                };
            }

            // 2. Tenter de charger comme un Prefab (Resources)
            GameObject prefab = Resources.Load<GameObject>(mapName);
            if (prefab != null)
            {
                return new MapAsset
                {
                    Kind = MapAssetKind.Prefab,
                    Prefab = prefab,
                    Texture = null,
                    SceneName = null,
                    Bounds = new Bounds(Vector3.zero, Vector3.one)
                };
            }

            // 3. Tenter de charger comme une Image (comportement actuel)
            if (LoadMapData(mapName, out Texture2D texture, out Bounds bounds))
            {
                return new MapAsset
                {
                    Kind = MapAssetKind.Image,
                    Texture = texture,
                    Bounds = bounds,
                    Prefab = null,
                    SceneName = null
                };
            }

            Debug.LogError($"[ScenarioLoader] Map resource not found: {mapName}");
            return new MapAsset { Kind = MapAssetKind.None };
        }
        
        /// <summary>
        /// Charge la map associée à un scénario
        /// </summary>
        /// <param name="scenario">Scénario</param>
        /// <returns>Texture2D de la map ou null</returns>
        // public Texture2D LoadMapForScenario(ScenarioData scenario)
        // {
        //     if (scenario == null || string.IsNullOrEmpty(scenario.MapImage))
        //         return null;
            
        //     return LoadMap(scenario.MapImage);
        // }

        #endregion

        #region Public API - Queries

        /// <summary>
        /// Liste tous les scénarios disponibles
        /// </summary>
        /// <returns>Liste des noms de scénarios (sans extension)</returns>
        public List<string> GetAvailableScenarios()
        {
            Debug.LogWarning($"[ScenarioLoader] GetAvailableScenarios called, scenarios path: {_scenariosPath}" + $", exists: {Directory.Exists(_scenariosPath)}");
            if (!Directory.Exists(_scenariosPath))
            {
                return new List<string>();
            }
            
            var scenarios = new List<string>();
            
            foreach (var ext in _validExtensions)
            {
                string[] files = Directory.GetFiles(_scenariosPath, "*" + ext);
                foreach (var file in files)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!scenarios.Contains(name))
                    {
                        scenarios.Add(name);
                    }
                }
            }

            Debug.LogWarning($"[ScenarioLoader] Found {scenarios.Count} scenarios in {_scenariosPath}");
            
            return scenarios.OrderBy(x => x).ToList();
        }
        
        /// <summary>
        /// Liste toutes les maps disponibles
        /// </summary>
        /// <returns>Liste des noms de maps (sans extension)</returns>
        public List<string> GetAvailableMaps()
        {
            if (!Directory.Exists(_mapsPath))
            {
                return new List<string>();
            }

            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] imageExtensions = { "*.png", "*.jpg", "*.jpeg" };

            // Legacy maps stored directly at the dataset root.
            foreach (string pattern in imageExtensions)
            {
                foreach (string file in Directory.GetFiles(_mapsPath, pattern, SearchOption.TopDirectoryOnly))
                    maps.Add(Path.GetFileNameWithoutExtension(file));
            }

            // Current layout: any nested collection can own png/ and json/ folders.
            foreach (string pngDirectory in Directory.GetDirectories(_mapsPath, "png", SearchOption.AllDirectories))
            {
                DirectoryInfo collectionDirectory = Directory.GetParent(pngDirectory);
                if (collectionDirectory == null)
                    continue;

                string collection = Path.GetRelativePath(_mapsPath, collectionDirectory.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                foreach (string pattern in imageExtensions)
                {
                    foreach (string file in Directory.GetFiles(pngDirectory, pattern, SearchOption.TopDirectoryOnly))
                        maps.Add($"{collection}/{Path.GetFileNameWithoutExtension(file)}");
                }
            }

            return maps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        
        /// <summary>
        /// Vérifie si un scénario existe
        /// </summary>
        public bool ScenarioExists(string scenarioName)
        {
            return FindScenarioFile(scenarioName) != null;
        }

        /// <summary>
        /// Moves a scenario to a recoverable archive folder instead of deleting it permanently.
        /// </summary>
        public bool ArchiveScenario(string scenarioName, out string archivedPath, out string error)
        {
            archivedPath = null;
            error = null;

            string sourcePath = FindScenarioFile(scenarioName);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "The scenario file could not be found.";
                return false;
            }

            try
            {
                string archiveDirectory = Path.Combine(_scenariosPath, "DeletedScenarios");
                Directory.CreateDirectory(archiveDirectory);

                string extension = Path.GetExtension(sourcePath);
                string fileName = Path.GetFileNameWithoutExtension(sourcePath);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string destination = Path.Combine(archiveDirectory, $"{fileName}_{timestamp}{extension}");
                int suffix = 2;
                while (File.Exists(destination))
                    destination = Path.Combine(archiveDirectory, $"{fileName}_{timestamp}_{suffix++}{extension}");

                File.Move(sourcePath, destination);
                if (File.Exists(sourcePath + ".meta"))
                {
                    try
                    {
                        File.Move(sourcePath + ".meta", destination + ".meta");
                    }
                    catch (Exception metaException)
                    {
                        Debug.LogWarning($"[ScenarioLoader] Scenario archived, but its meta file could not be moved: {metaException.Message}");
                    }
                }

                archivedPath = destination;
                ClearScenarioCache();
#if UNITY_EDITOR
                // Only a file inside the project is something the importer has to hear about. A scenario
                // archived outside it - a caller that pointed the loader at its own folder - has nothing for
                // the asset database to pick up, and asking anyway makes the Editor probe paths it cannot
                // stat, which it reports as an internal assert.
                if (IsInsideProjectAssets(destination))
                    AssetDatabase.Refresh();
#endif
                return true;
            }
            catch (Exception exception)
            {
                error = $"Delete failed: {exception.Message}";
                return false;
            }
        }

#if UNITY_EDITOR
        /// <summary>True when a path sits under the <c>Assets</c> folder of this project.</summary>
        private static bool IsInsideProjectAssets(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string assets = Path.GetFullPath(Application.dataPath).TrimEnd('/', '\\');
            string candidate = Path.GetFullPath(path).Replace('\\', '/');
            assets = assets.Replace('\\', '/');
            return candidate.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase);
        }
#endif
        
        /// <summary>
        /// Vérifie si une map existe
        /// </summary>
        public bool MapExists(string mapName)
        {
            return FindMapFile(mapName) != null;
        }

        #endregion

        #region Public API - Export

        /// <summary>
        /// Exporte un scénario en fichier YAML
        /// </summary>
        /// <param name="scenario">Scénario à exporter</param>
        /// <param name="fileName">Nom du fichier (sans extension)</param>
        /// <returns>True si l'export a réussi</returns>
        public bool ExportScenario(ScenarioData scenario, string fileName)
        {
            if (scenario == null)
            {
                OnScenarioError?.Invoke("Cannot export null scenario");
                return false;
            }
            
            EnsureDirectoriesExist();
            
            string fullPath = Path.Combine(_scenariosPath, fileName);
            if (!fullPath.EndsWith(".yaml") && !fullPath.EndsWith(".yml"))
            {
                fullPath += ".yaml";
            }
            
            try
            {
                // byte[] yamlBytes = YamlSerializer.Serialize(scenario);
                byte[] yamlBytes = YamlSerializer.Serialize(scenario).ToArray();
                // byte[] yamlBytes = YamlSerializer.Serialize(scenario).Span.ToArray();
                File.WriteAllBytes(fullPath, yamlBytes);
                
                if (_logEvents)
                {
                    Debug.Log($"[ScenarioLoader] Exported scenario to: {fullPath}");
                }
                
                return true;
            }
            catch (Exception e)
            {
                OnScenarioError?.Invoke($"Failed to export scenario: {e.Message}");
                return false;
            }
        }
        

        #endregion

        #region Public API - Cache Management

        /// <summary>
        /// Vide le cache des scénarios
        /// </summary>
        public void ClearScenarioCache()
        {
            _scenarioRepository.Clear();
            
            if (_logEvents)
            {
                Debug.Log("[ScenarioLoader] Scenario cache cleared");
            }
        }
        
        /// <summary>
        /// Vide le cache des maps
        /// </summary>
        public void ClearMapCache()
        {
            foreach (var texture in _loadedMaps.Values)
            {
                if (texture != null)
                    Destroy(texture);
            }
            _loadedMaps.Clear();
            
            if (_logEvents)
            {
                Debug.Log("[ScenarioLoader] Map cache cleared");
            }
        }
        
        /// <summary>
        /// Vide tous les caches
        /// </summary>
        public void ClearAllCaches()
        {
            ClearScenarioCache();
            ClearMapCache();
        }

        #endregion

        #region Editor Utilities

        #if UNITY_EDITOR
        
        [ContextMenu("Clear All Caches")]
        private void EditorClearCaches() => ClearAllCaches();
        
        [ContextMenu("Log Available Scenarios")]
        private void EditorLogAvailableScenarios()
        {
            var scenarios = GetAvailableScenarios();
            Debug.Log($"[ScenarioLoader] Available scenarios ({scenarios.Count}):\n  {(scenarios.Count > 0 ? string.Join("\n  ", scenarios) : "none")}");
        }
        
        [ContextMenu("Log Available Maps")]
        private void EditorLogAvailableMaps()
        {
            var maps = GetAvailableMaps();
            Debug.Log($"[ScenarioLoader] Available maps ({maps.Count}):\n  {(maps.Count > 0 ? string.Join("\n  ", maps) : "none")}");
        }
        
        #endif

        #endregion

        private Vector3 GetPositionFromRef(ScenarioData scenario, string reference)
        {
            // Debug.Log($"[ScenarioLoader] Resolving position for reference: '{reference}'");
            if (string.IsNullOrEmpty(reference)) return Vector3.zero;
            
            if (scenario.Points != null && scenario.Points.TryGetValue(reference, out var point))
            {
                Debug.Log($"[ScenarioLoader] Found point for reference '{reference}': {point} at {point.ToVector3()}");
                return point.ToVector3();
            }
            
            // Essayer comme ID entier
            if (int.TryParse(reference, out int id) && scenario.Points != null)
            {
                string key = id.ToString();
                if (scenario.Points.TryGetValue(key, out point))
                    return point.ToVector3();
            }
            
            return Vector3.zero;
        }

        /// <summary>
        /// Obtient la position et la rotation à partir d'une référence (point nommé).
        /// </summary>
        public (Vector3 position, Quaternion rotation) GetPositionAndRotation(ScenarioData scenario, string reference)
        {
            if (string.IsNullOrEmpty(reference)) 
                return (Vector3.zero, Quaternion.identity);

            if (scenario.Points != null && scenario.Points.TryGetValue(reference, out var point))
            {
                return (point.ToVector3(), point.Rotation);
            }

            // Fallback : essayer comme ID entier
            if (int.TryParse(reference, out int id) && scenario.Points != null)
            {
                string key = id.ToString();
                if (scenario.Points.TryGetValue(key, out point))
                    return (point.ToVector3(), point.Rotation);
            }

            return (Vector3.zero, Quaternion.identity);
        }

        private Bounds GetBoundsFromRef(ScenarioData scenario, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return new Bounds();
            
            if (scenario.Points != null && scenario.Points.TryGetValue(reference, out var point) && point.IsBounds)
                return point.ToBounds();
            
            return new Bounds();
        }

        /// <summary>
        /// Obtient une position Vector3 à partir d'une référence (point nommé ou zone)
        /// </summary>
        public Vector3 GetPosition(ScenarioData scenario, string reference)
        {
            return GetPositionFromRef(scenario, reference);
        }

        /// <summary>
        /// Obtient une zone Bounds à partir d'une référence (zone nommée)
        /// </summary>
        public Bounds GetBounds(ScenarioData scenario, string reference)
        {
            return GetBoundsFromRef(scenario, reference);
        }
    }
}
