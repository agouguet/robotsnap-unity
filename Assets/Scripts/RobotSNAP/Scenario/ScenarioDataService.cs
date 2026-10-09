using System;
using System.Collections.Generic;
using System.Linq;
using RobotSNAP.Core.Scenario;
using UnityEngine;

/// <summary>
/// Service central pour l'accès aux données des scénarios (liste, filtrage, tri, scénario chargé).
/// Peut être utilisé par plusieurs contrôleurs UI.
/// </summary>
public class ScenarioDataService : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ScenarioManager _scenarioManager;

    [Header("Debug")]
    [SerializeField] private bool _useDebugData = false;

    // Cache
    private Dictionary<string, ScenarioInfo> _scenarioDictionary = new();
    private string _loadedScenarioId;
    private bool _isLoaded = false;

    // Événements
    public event Action OnScenarioListChanged;
    public event Action<string> OnScenarioLoaded;

    public IReadOnlyDictionary<string, ScenarioInfo> AllScenarios => _scenarioDictionary;
    public string LoadedScenarioId => _loadedScenarioId;
    public ScenarioInfo LoadedScenarioInfo => string.IsNullOrEmpty(_loadedScenarioId) ? null : GetScenarioInfo(_loadedScenarioId);

    private void Awake()
    {
        if (_scenarioManager == null)
            _scenarioManager = FindAnyObjectByType<ScenarioManager>();
    }

    private void Start()
    {
        // A session that starts is a session that has read nothing yet. The flag otherwise survives from the
        // previous one, when the Editor is set not to reload the domain or the scene: a service that still
        // believes it is loaded never looks at the YAML that was written in between.
        _isLoaded = false;

        if (_scenarioManager != null && _scenarioManager.Loader != null)
        {
            _scenarioManager.OnScenarioLoaded += OnScenarioLoadedByManager;
            _scenarioManager.Loader.OnPathsUpdated += OnLoaderPathsUpdated;
        }
        EnsureLoaded();
    }

    private void OnDestroy()
    {
        if (_scenarioManager != null && _scenarioManager.Loader != null)
        {
            _scenarioManager.OnScenarioLoaded -= OnScenarioLoadedByManager;
            _scenarioManager.Loader.OnPathsUpdated -= OnLoaderPathsUpdated;
        }
    }

    private void OnScenarioLoadedByManager(ScenarioData scenario)
    {
         string scenarioId = _scenarioManager.CurrentScenarioId;
        if (!string.IsNullOrEmpty(scenarioId))
        {
            // Ajouter ou mettre à jour le dictionnaire avec les infos du scénario chargé
            if (scenario != null && scenario.Info != null)
            {
                _scenarioDictionary[scenarioId] = scenario.Info;
            }
            _loadedScenarioId = scenarioId;
            OnScenarioLoaded?.Invoke(scenarioId);
            Debug.Log($"[ScenarioDataService] Scenario loaded by manager: {scenarioId}");
        }
    }

    private void OnLoaderPathsUpdated()
    {
        Debug.Log("[ScenarioDataService] Loader paths updated, reloading scenario infos.");
        _isLoaded = false;
        EnsureLoaded();
    }

    public void EnsureLoaded()
    {
        if (_isLoaded) return;
        ReloadAllScenarioInfos();
        // _isLoaded = true;
    }

    /// <summary>
    /// Charge toutes les infos des scénarios disponibles.
    /// </summary>
    public void ReloadAllScenarioInfos()
    {
        // A reload is what the Editor asks for after a scenario file was rewritten. The loader's cache
        // outlives a Play session when the domain and the scene are not reloaded, so it is dropped here:
        // this is what makes the YAML on disk what the list and the next load describe.
        _scenarioManager?.Loader?.ClearScenarioCache();

        _scenarioDictionary.Clear();

        if (_useDebugData)
        {
            var debugList = GenerateDebugScenarioInfos();
            foreach (var info in debugList)
                _scenarioDictionary[info.Name] = info;
            Debug.Log($"[ScenarioDataService] Usage of {_scenarioDictionary.Count} fake scenarios (debug mode)");
            _isLoaded = true;
            OnScenarioListChanged?.Invoke();
            return;
        }

        if (_scenarioManager == null) return;

        var loader = _scenarioManager.Loader;
        if (loader == null || string.IsNullOrEmpty(loader.ScenariosPath))
        {
            Debug.LogWarning("[ScenarioDataService] ScenarioLoader not ready, will retry later.");
            return;
        }

        var fileIds = _scenarioManager.GetAvailableScenarios();
        foreach (var fileId in fileIds)
        {
            var info = _scenarioManager.GetScenarioInfo(fileId);
            Debug.LogWarning($"[ScenarioDataService] Loaded real scenario: {fileId} - {info?.Name}");
            if (info != null)
                _scenarioDictionary[fileId] = info;
        }
        Debug.Log($"[ScenarioDataService] Loaded {_scenarioDictionary.Count} scenarios.");
        _isLoaded = true;
        OnScenarioListChanged?.Invoke();
    }

    /// <summary>
    /// Récupère les infos d'un scénario par son ID.
    /// </summary>
    public ScenarioInfo GetScenarioInfo(string scenarioId)
    {
        _scenarioDictionary.TryGetValue(scenarioId, out var info);
        return info;
    }

    public Color GetColorFromTag(string tag)
    {
        int hash = Mathf.Abs(tag.GetHashCode());
        float hue = (hash % 360) / 360f;
        return Color.HSVToRGB(hue, 0.85f, 0.9f);
    }

    public Texture2D GetScenarioPreviewImage(ScenarioInfo info)
    {
        if (info == null) return GetDefaultImage();
        string previewName = info.PreviewImage;
        if (string.IsNullOrEmpty(previewName))
        {
            return GetDefaultImage();
        }
        string fileNameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(previewName);
        string resourcePath = $"ScenarioPreviews/{fileNameWithoutExt}";
        Texture2D previewTexture = Resources.Load<Texture2D>(resourcePath);
        return previewTexture != null ? previewTexture : GetDefaultImage();
    }

    public Texture2D GetScenarioPreviewImage(string scenarioId)
    {
        return GetScenarioPreviewImage(GetScenarioInfo(scenarioId));
    }

    private Texture2D GetDefaultImage()
    {
        Texture2D defaultTex = Resources.Load<Texture2D>("ScenarioPreviews/default");
        if (defaultTex != null)
            return defaultTex;
        else
            return new Texture2D(1, 1);
    }

    /// <summary>
    /// Filtre et trie les scénarios selon les critères.
    /// </summary>
    public List<(string Id, ScenarioInfo Info)> GetFilteredAndSortedScenarios(string filter, string search, string sortOption)
    {
        Debug.LogWarning(_scenarioDictionary.Count + " scenarios found before filtering.");
        var filtered = _scenarioDictionary
            .Where(entry => FilterMatches(entry, filter, search))
            .Select(entry => (Id: entry.Key, Info: entry.Value))
            .ToList();

        return SortScenarios(filtered, sortOption);
    }

    /// <summary>
    /// Récupère tous les tags uniques pour le filtre.
    /// </summary>
    public List<string> GetAllTags()
    {
        var allTags = new HashSet<string> { "All" };
        foreach (var entry in _scenarioDictionary)
        {
            var info = entry.Value;
            if (info.Tags != null)
            {
                foreach (var tag in info.Tags)
                    allTags.Add(tag);
            }
        }
        return allTags.OrderBy(t => t).ToList();
    }

    /// <summary>
    /// Charge un scénario (via ScenarioManager) et met à jour l'état chargé.
    /// </summary>
    public void LoadScenario(string scenarioId)
    {
        if (string.IsNullOrEmpty(scenarioId)) return;

        _scenarioManager?.LoadScenario(scenarioId, startClock: false, autoApply: false);
        _loadedScenarioId = scenarioId;
        OnScenarioLoaded?.Invoke(scenarioId);
    }

    // Méthodes privées de filtrage/tri

    private bool FilterMatches(KeyValuePair<string, ScenarioInfo> entry, string filter, string search)
    {
        var info = entry.Value;
        if (filter != "All")
        {
            bool tagMatch = info.Tags != null && info.Tags.Contains(filter);
            if (!tagMatch) return false;
        }
        if (!string.IsNullOrEmpty(search))
        {
            string lowerSearch = search.ToLower();
            if (!info.Name.ToLower().Contains(lowerSearch))
                return false;
        }
        return true;
    }

    private List<(string Id, ScenarioInfo Info)> SortScenarios(List<(string Id, ScenarioInfo Info)> list, string sortOption)
    {
        switch (sortOption)
        {
            case "Name (A-Z)": return list.OrderBy(x => x.Info.Name).ToList();
            case "Name (Z-A)": return list.OrderByDescending(x => x.Info.Name).ToList();
            case "Newest": return list.OrderByDescending(x => x.Info.Created).ToList();
            case "Oldest": return list.OrderBy(x => x.Info.Created).ToList();
            default: return list;
        }
    }

    // Données de debug
    private List<ScenarioInfo> GenerateDebugScenarioInfos()
    {
        return new List<ScenarioInfo>
        {
            new ScenarioInfo { Name = "Corridor Crowd", Type = "Crowd", Location = "Office Building 01", Created = "2025-07-27 14:30", Tags = new[] { "crowd", "corridor" } },
            new ScenarioInfo { Name = "Intersection Busy", Type = "Crossing", Location = "Urban Plaza", Created = "2025-07-25 10:12", Tags = new[] { "intersection", "crossing" } },
            new ScenarioInfo { Name = "Narrow Passage", Type = "Navigation", Location = "Office Building 02", Created = "2025-07-24 16:45", Tags = new[] { "navigation", "narrow" } },
            new ScenarioInfo { Name = "Circular Flow", Type = "Crowd", Location = "Town Square", Created = "2025-07-23 09:18", Tags = new[] { "circular", "flow" } },
            new ScenarioInfo { Name = "Frontal Approach", Type = "Interaction", Location = "Office Building 01", Created = "2025-07-22 11:05", Tags = new[] { "frontal", "approach" } },
            new ScenarioInfo { Name = "Corner Turn", Type = "Navigation", Location = "Office Building 02", Created = "2025-07-21 15:22", Tags = new[] { "corner", "turn" } },
            new ScenarioInfo { Name = "Perpendicular Traffic", Type = "Crossing", Location = "Urban Street", Created = "2025-07-20 13:47", Tags = new[] { "perpendicular", "traffic" } },
            new ScenarioInfo { Name = "Dense Crowd", Type = "Crowd", Location = "Main Hall", Created = "2025-07-19 08:33", Tags = new[] { "dense", "crowd" } }
        };
    }
}
