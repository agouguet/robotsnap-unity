using System.Collections.Generic;
using RobotSNAP;
using RobotSNAP.Agents;
using RobotSNAP.Core.Scenario;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

/// <summary>
/// Wires the Robots tab: the catalogue of robot TYPES a scenario may place on the left, a preview instance of
/// the selected one in the middle, and the figures a reader may change on the right - the speeds of the base,
/// the shape and rate of its lidar, and the radius within which it notices the other agents.
///
/// The list is the catalogue, not the running fleet: the tab is what a reader opens before an experiment to
/// decide how a type drives, and it works with no scenario loaded. A type comes from
/// <see cref="RobotProfiles.All"/> and then from the entries of the project catalogue <see cref="RobotCatalog"/>
/// whose type is not already one of them, so a robot the editor tool catalogued is configurable here even
/// before any scenario names it.
///
/// The page owns one decision the views do not: a tuning is per robot TYPE and not per instance, because a
/// scenario may place several robots of one type and the figures a reader chose for a Jackal are what every
/// Jackal of the fleet should drive with. Saving writes that tuning to disk, and the hook on
/// <see cref="ScenarioManager.OnScenarioApplied"/> lays the saved tunings over the roster of a scenario just
/// applied, so a tuning reaches robots a scenario builds later and survives a restart.
///
/// The preview is an instance of the type's prefab, placed far below the world and stripped of anything that
/// could take part in an experiment: its behaviours are disabled and its physics is off before it is shown, so
/// a robot on display here is a picture and not a participant.
///
/// It initializes lazily, the first time the tab is opened, because that is when the UXML template is
/// instantiated into the UIDocument - the same reason the other tab controllers wait for their own view.
///
/// No object of the scene names this controller: it installs itself after the scene loads, the way the
/// metrics recorder does, so the tab works without a scene edit. The UIDocument is resolved as the one whose
/// tree actually holds the robots page, which keeps the controller inert until its view exists.
/// </summary>
public class RobotsTabController : MonoBehaviour
{
    /// <summary>Where a preview instance is parked: far below the world, so it touches no experiment.</summary>
    private static readonly Vector3 PreviewOrigin = new Vector3(0f, -1000f, 0f);

    /// <summary>Angle the inspection view opens on: a three-quarter shot from above.</summary>
    private const float DefaultPreviewYaw = 35f;
    private const float DefaultPreviewPitch = 18f;

    /// <summary>How far the view may be tilted, in degrees. It stops short of the poles so the orbit never flips.</summary>
    private const float MinPreviewPitch = -15f;
    private const float MaxPreviewPitch = 80f;

    /// <summary>How far the view may be pushed in or pulled out, as a multiple of the framed distance.</summary>
    private const float MinPreviewZoom = 0.4f;
    private const float MaxPreviewZoom = 3f;

    /// <summary>Degrees the view turns per pixel dragged.</summary>
    private const float PreviewOrbitDegreesPerPixel = 0.4f;

    /// <summary>Colours of the little stage the preview stands on, so it is not a robot floating in the void.</summary>
    private static readonly Color PreviewFloorColour = new Color(0.20f, 0.21f, 0.24f, 1f);
    private static readonly Color PreviewBackdropColour = new Color(0.44f, 0.47f, 0.52f, 1f);

    /// <summary>
    /// The detection radius each type shipped with, read from a fresh instance of its prefab the first time it
    /// is seen. The radius lives on the detector rather than on the profile, so this is what lets Reset put
    /// back the figure the prefab carries instead of a value this page wrote. The key is the canonical type id.
    /// </summary>
    private static readonly Dictionary<string, float> AuthoredDetectionRadius = new Dictionary<string, float>();

    [SerializeField] private UIDocument uiDocument;

    private static RobotsTabController _instance;

    private bool _initialized;
    private VisualElement _page;
    private VisualElement _listHost;
    private Label _listEmpty;
    private VisualElement _previewContainer;
    private Label _previewName;
    private FloatField _maxLinearSpeed;
    private FloatField _maxAngularSpeed;
    private FloatField _lidarSpan;
    private FloatField _lidarRange;
    private IntegerField _lidarRays;
    private FloatField _lidarHeight;
    private FloatField _lidarFrequency;
    private FloatField _detectionRadius;
    private Button _saveButton;
    private Button _resetButton;
    private Label _status;

    private Camera _previewCamera;
    private Light _previewLight;
    private RenderTexture _previewTexture;
    private GameObject _previewRoot;
    private GameObject _previewInstance;
    private string _previewTypeId;
    private bool _previewIsPlaceholder;

    /// <summary>The floor and the backdrop the preview stands between, so the panel has a room rather than a void.</summary>
    private GameObject _previewStage;
    private Transform _previewBackdrop;

    /// <summary>Where the inspection view looks, how far out it is, and the angle it has been turned to.</summary>
    private Vector3 _previewCenter;
    private float _previewRadius = 0.5f;
    private float _previewYaw = DefaultPreviewYaw;
    private float _previewPitch = DefaultPreviewPitch;
    private float _previewZoom = 1f;
    private bool _previewDragging;
    private Vector2 _previewLastPointer;

    private RobotCatalog _catalog;
    private bool _catalogRead;
    private ScenarioManager _scenarioManager;
    private List<RobotTypeEntry> _typeEntries = new List<RobotTypeEntry>();
    private string _selectedTypeId;
    private float _nextResolveTime;

    /// <summary>
    /// Puts one controller in the session, after the scene is loaded and only once per play session. The
    /// object carries no scene reference on purpose: it lives on across the scenario changes that tear the
    /// environment down and build it again.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null)
            return;

        var host = new GameObject("RobotSNAP Robots Tab");
        DontDestroyOnLoad(host);
        _instance = host.AddComponent<RobotsTabController>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
    }

    private void OnEnable()
    {
        MainViewController.OnViewLoaded += OnViewLoaded;
        EnsureScenarioSubscription();
        TryInitialize();
    }

    private void OnDisable()
    {
        MainViewController.OnViewLoaded -= OnViewLoaded;
    }

    private void OnDestroy()
    {
        if (_instance == this)
            _instance = null;

        DestroyPreviewInstance();

        if (_previewCamera != null && _previewTexture != null)
        {
            _previewCamera.targetTexture = null;
            _previewTexture.Release();
            Destroy(_previewTexture);
            _previewTexture = null;
        }

        if (_previewStage != null)
        {
            Destroy(_previewStage);
            _previewStage = null;
            _previewBackdrop = null;
        }
    }

    private void Update()
    {
        if (_scenarioManager == null && Time.unscaledTime >= _nextResolveTime)
            EnsureScenarioSubscription();

        TickPreview();
    }

    private void OnViewLoaded(string viewName)
    {
        if (viewName != "Robots")
            return;

        TryInitialize();
        RefreshList();
    }

    // -- the catalogue of types ----------------------------------------------

    /// <summary>
    /// One robot type the tab lists: what the list shows and what the preview builds. A type the catalogue
    /// knows but the profiles do not has no <see cref="Profile"/> - only its id, and a prefab to show.
    /// </summary>
    public sealed class RobotTypeEntry
    {
        public RobotTypeEntry(string id, string displayName, string description, RobotProfile profile)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            Profile = profile;
        }

        /// <summary>Stable identifier of the type, the key a tuning is saved under.</summary>
        public string Id { get; }

        /// <summary>Name a person reads, and the label the list shows.</summary>
        public string DisplayName { get; }

        /// <summary>One line on what this robot is, or empty when only the catalogue knows the type.</summary>
        public string Description { get; }

        /// <summary>The built-in profile of the type, or null for a type only the catalogue files.</summary>
        public RobotProfile Profile { get; }

        /// <summary>True when the type has a built-in profile, so its figures are the ones it shipped with.</summary>
        public bool HasProfile => Profile != null;
    }

    /// <summary>
    /// The types the tab lists, read from the project: the built-in profiles first, then the entries of the
    /// project catalogue whose type is not already one of them. The order is the profile order first, so the
    /// interface lists the robots the application shipped with before the ones a project added.
    /// </summary>
    public static List<RobotTypeEntry> BuildTypeList()
    {
        RobotCatalog catalog = RobotCatalog.Load();
        IReadOnlyList<RobotCatalog.Entry> catalogueEntries = catalog != null ? catalog.Entries : null;
        return BuildTypeList(RobotProfiles.All, catalogueEntries);
    }

    /// <summary>
    /// The merge the tab lists, over the two sources handed in: a profile contributes its own name and
    /// description, and a catalogue entry the profiles do not know contributes its id as its name. A type is
    /// named once however it is spelled, so a catalogue entry that repeats a profile adds nothing.
    /// </summary>
    public static List<RobotTypeEntry> BuildTypeList(
        IReadOnlyList<RobotProfile> profiles,
        IReadOnlyList<RobotCatalog.Entry> catalogueEntries)
    {
        var entries = new List<RobotTypeEntry>();
        var seen = new HashSet<string>();

        if (profiles != null)
        {
            foreach (RobotProfile profile in profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
                    continue;
                if (!seen.Add(Canonical(profile.Id)))
                    continue;
                entries.Add(new RobotTypeEntry(profile.Id, profile.DisplayName, profile.Description, profile));
            }
        }

        if (catalogueEntries != null)
        {
            foreach (RobotCatalog.Entry entry in catalogueEntries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.TypeId))
                    continue;
                if (!seen.Add(Canonical(entry.TypeId)))
                    continue;
                entries.Add(new RobotTypeEntry(entry.TypeId, entry.TypeId, string.Empty, null));
            }
        }

        return entries;
    }

    // -- composition ----------------------------------------------------------

    private void TryInitialize()
    {
        if (_initialized)
            return;

        UIDocument document = ResolveDocument();
        if (document == null)
            return;

        VisualElement root = document.rootVisualElement;
        if (root == null || root.Q<VisualElement>("RobotsPage") == null)
            return; // The tab has not been shown yet, so its template is not in the tree.

        var missing = new List<string>();
        _page = Require<VisualElement>(root, "RobotsPage", missing);
        _listHost = Require<VisualElement>(root, "RobotListHost", missing);
        _listEmpty = Require<Label>(root, "RobotListEmpty", missing);
        _previewContainer = Require<VisualElement>(root, "RobotPreviewContainer", missing);
        _previewName = Require<Label>(root, "RobotPreviewName", missing);
        _maxLinearSpeed = Require<FloatField>(root, "RobotMaxLinearSpeed", missing);
        _maxAngularSpeed = Require<FloatField>(root, "RobotMaxAngularSpeed", missing);
        _lidarSpan = Require<FloatField>(root, "RobotLidarSpan", missing);
        _lidarRange = Require<FloatField>(root, "RobotLidarRange", missing);
        _lidarRays = Require<IntegerField>(root, "RobotLidarRays", missing);
        _lidarHeight = Require<FloatField>(root, "RobotLidarHeight", missing);
        _lidarFrequency = Require<FloatField>(root, "RobotLidarFrequency", missing);
        _detectionRadius = Require<FloatField>(root, "RobotDetectionRadius", missing);
        _saveButton = Require<Button>(root, "RobotSaveButton", missing);
        _resetButton = Require<Button>(root, "RobotResetButton", missing);
        _status = Require<Label>(root, "RobotTuningStatus", missing);
        if (missing.Count > 0)
        {
            Debug.LogError($"[RobotsTabController] Missing UI elements: {string.Join(", ", missing)}");
            return;
        }

        // The style sheet is loaded here rather than referenced by the template, so the UXML carries no guid
        // pointing at an asset the Resources loader would have to find on its own.
        StyleSheet sheet = Resources.Load<StyleSheet>("UI/Tabs/Robots/RobotsTab");
        if (sheet != null && !_page.styleSheets.Contains(sheet))
            _page.styleSheets.Add(sheet);

        EnsurePreviewCamera();

        // The name rides on top of the picture and must not swallow the drag that turns it; the container
        // itself has to be pickable, which is what makes the whole panel the grab surface of the orbit.
        _previewName.pickingMode = PickingMode.Ignore;
        _previewContainer.pickingMode = PickingMode.Position;
        _previewContainer.RegisterCallback<PointerDownEvent>(OnPreviewPointerDown);
        _previewContainer.RegisterCallback<PointerMoveEvent>(OnPreviewPointerMove);
        _previewContainer.RegisterCallback<PointerUpEvent>(OnPreviewPointerUp);
        _previewContainer.RegisterCallback<WheelEvent>(OnPreviewWheel);
        _previewContainer.RegisterCallback<GeometryChangedEvent>(OnPreviewResized);
        // The geometry event covers a resize, but the first layout can land before this callback existed: the
        // periodic pass is the belt to that pair of braces, and it does nothing once the size is settled.
        _previewContainer.schedule.Execute(UpdatePreviewTexture).Every(250);
        _saveButton.clicked += OnSaveClicked;
        _resetButton.clicked += OnResetClicked;

        _initialized = true;
        RefreshList();
    }

    /// <summary>
    /// The UIDocument is authored on the HUD, not on this object, so it is resolved as the one whose tree
    /// actually holds the robots page. That keeps the controller inert until its view exists instead of
    /// failing on a document that happens to be found first.
    /// </summary>
    private UIDocument ResolveDocument()
    {
        if (uiDocument != null)
            return uiDocument;

        uiDocument = GetComponent<UIDocument>();
        if (uiDocument != null)
            return uiDocument;

        foreach (UIDocument candidate in FindObjectsByType<UIDocument>())
        {
            VisualElement candidateRoot = candidate.rootVisualElement;
            if (candidateRoot != null && candidateRoot.Q<VisualElement>("RobotsPage") != null)
            {
                uiDocument = candidate;
                break;
            }
        }

        return uiDocument;
    }

    private static T Require<T>(VisualElement root, string name, ICollection<string> missing) where T : VisualElement
    {
        T element = root.Q<T>(name);
        if (element == null)
            missing.Add(name);
        return element;
    }

    /// <summary>
    /// Finds the scenario manager once and follows it: a scenario that is applied adds, removes or rebuilds
    /// robots, so the saved tunings are laid back over the fleet every time. The tab's own list does not read
    /// the roster - it is a catalogue of types - so this hook is the only thing that watches a scenario.
    /// </summary>
    private void EnsureScenarioSubscription()
    {
        if (_scenarioManager != null)
            return;

        _nextResolveTime = Time.unscaledTime + 1f;
        _scenarioManager = FindAnyObjectByType<ScenarioManager>();
        if (_scenarioManager == null)
            return;

        _scenarioManager.OnScenarioApplied += OnScenarioApplied;

        // A scenario may already be in the scene by the time this controller appears. Laying the saved
        // tunings over the roster now is what makes a restart read a saved tuning even when the first
        // application happened before this object existed.
        if (_scenarioManager.HasScenarioLoaded)
            ApplyStoredTunings();
    }

    private void OnScenarioApplied(ScenarioData scenario)
    {
        ApplyStoredTunings();
        RefreshList();
    }

    private RobotCatalog Catalog
    {
        get
        {
            if (!_catalogRead)
            {
                _catalog = RobotCatalog.Load();
                _catalogRead = true;
            }

            return _catalog;
        }
    }

    // -- the list -------------------------------------------------------------

    private void RefreshList()
    {
        if (!_initialized)
            return;

        _listHost.Clear();
        _typeEntries = BuildTypeList();

        _listEmpty.style.display = _typeEntries.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

        if (_selectedTypeId != null && FindEntry(_selectedTypeId) == null)
            _selectedTypeId = null;

        foreach (RobotTypeEntry entry in _typeEntries)
            _listHost.Add(BuildRow(entry));

        if (_selectedTypeId == null && _typeEntries.Count > 0)
            _selectedTypeId = _typeEntries[0].Id;

        ApplySelection();
    }

    private Button BuildRow(RobotTypeEntry entry)
    {
        var row = new Button { userData = entry };
        row.AddToClassList("robots-list-item");
        if (!string.IsNullOrEmpty(entry.Description))
            row.tooltip = entry.Description;

        var nameLabel = new Label(entry.DisplayName);
        nameLabel.AddToClassList("robots-list-id");
        row.Add(nameLabel);

        var idLabel = new Label(entry.Id);
        idLabel.AddToClassList("robots-list-type");
        row.Add(idLabel);

        row.clicked += () => Select(entry.Id);
        return row;
    }

    private void Select(string typeId)
    {
        _selectedTypeId = typeId;
        ApplySelection();
    }

    private void ApplySelection()
    {
        HighlightSelection();
        EnsurePreviewMatchesSelection();
        LoadFields();
    }

    private void HighlightSelection()
    {
        foreach (VisualElement child in _listHost.Children())
        {
            if (child is Button row && row.userData is RobotTypeEntry entry)
                row.EnableInClassList("is-selected", entry.Id == _selectedTypeId);
        }
    }

    private RobotTypeEntry FindEntry(string typeId)
    {
        if (string.IsNullOrEmpty(typeId))
            return null;

        string wanted = Canonical(typeId);
        foreach (RobotTypeEntry entry in _typeEntries)
        {
            if (Canonical(entry.Id) == wanted)
                return entry;
        }

        return null;
    }

    // -- the figures ----------------------------------------------------------

    private void LoadFields()
    {
        RobotTypeEntry entry = FindEntry(_selectedTypeId);
        if (entry == null)
        {
            SetFieldsEnabled(false);
            _previewName.text = string.Empty;
            return;
        }

        SetFieldsEnabled(true);
        _previewName.text = _previewIsPlaceholder
            ? $"{entry.DisplayName} (no prefab; showing a placeholder)"
            : entry.DisplayName;

        // A type the profiles do not know has no figures of its own: the base profile is the fallback its
        // robot would drive as, so the page shows those rather than nothing.
        RobotProfile profile = entry.Profile ?? RobotProfiles.Default;

        if (RobotTuningStore.TryLoad(entry.Id, out RobotTuning tuning))
        {
            ShowFields(tuning);
            _status.text = $"Showing the saved tuning of {entry.DisplayName}.";
        }
        else
        {
            tuning = RobotTuning.Default(profile, AuthoredRadiusOf(entry));
            ShowFields(tuning);
            _status.text = entry.HasProfile
                ? $"Showing the built-in figures of {entry.DisplayName}."
                : $"The catalogue lists {entry.DisplayName} but the profiles do not; showing the base figures.";
        }
    }

    private void ShowFields(RobotTuning tuning)
    {
        _maxLinearSpeed.SetValueWithoutNotify(tuning.MaxLinearSpeed);
        _maxAngularSpeed.SetValueWithoutNotify(tuning.MaxAngularSpeed);
        _lidarSpan.SetValueWithoutNotify(tuning.LidarSpanDegrees);
        _lidarRange.SetValueWithoutNotify(tuning.LidarRange);
        _lidarRays.SetValueWithoutNotify(tuning.LidarRays);
        _lidarHeight.SetValueWithoutNotify(tuning.LidarHeight);
        _lidarFrequency.SetValueWithoutNotify(tuning.LidarFrequencyHz);
        _detectionRadius.SetValueWithoutNotify(tuning.DetectionRadius);
    }

    private RobotTuning ReadFields() => new RobotTuning
    {
        MaxLinearSpeed = Mathf.Max(0.01f, _maxLinearSpeed.value),
        MaxAngularSpeed = Mathf.Max(0.01f, _maxAngularSpeed.value),
        LidarSpanDegrees = Mathf.Clamp(_lidarSpan.value, 1f, 360f),
        LidarRange = Mathf.Max(0.1f, _lidarRange.value),
        LidarRays = Mathf.Max(2, _lidarRays.value),
        LidarHeight = Mathf.Max(0.05f, _lidarHeight.value),
        LidarFrequencyHz = Mathf.Max(1f, _lidarFrequency.value),
        DetectionRadius = Mathf.Max(0f, _detectionRadius.value)
    };

    private void SetFieldsEnabled(bool enabled)
    {
        _maxLinearSpeed.SetEnabled(enabled);
        _maxAngularSpeed.SetEnabled(enabled);
        _lidarSpan.SetEnabled(enabled);
        _lidarRange.SetEnabled(enabled);
        _lidarRays.SetEnabled(enabled);
        _lidarHeight.SetEnabled(enabled);
        _lidarFrequency.SetEnabled(enabled);
        _detectionRadius.SetEnabled(enabled);
        _saveButton.SetEnabled(enabled);
        _resetButton.SetEnabled(enabled);
    }

    private void OnSaveClicked()
    {
        RobotTypeEntry entry = FindEntry(_selectedTypeId);
        if (entry == null)
            return;

        RobotTuning tuning = ReadFields();
        RobotTuningStore.Save(entry.Id, tuning);
        ShowFields(tuning); // The fields show what was kept, not what was typed.

        int live = ApplyTuningToLiveRobots(entry.Id, tuning);
        _status.text = live > 0
            ? $"Saved the {entry.DisplayName} tuning; applied to {live} robot(s) of the running scenario."
            : $"Saved the {entry.DisplayName} tuning; it applies to every scenario that places this type.";
    }

    private void OnResetClicked()
    {
        RobotTypeEntry entry = FindEntry(_selectedTypeId);
        if (entry == null)
            return;

        RobotTuningStore.Delete(entry.Id);
        RobotTuning defaults = RobotTuning.Default(entry.Profile ?? RobotProfiles.Default, AuthoredRadiusOf(entry));
        ApplyTuningToLiveRobots(entry.Id, defaults);
        ShowFields(defaults);
        _status.text = $"Restored the built-in figures of {entry.DisplayName}.";
    }

    // -- applying a tuning ----------------------------------------------------

    /// <summary>
    /// Lays every saved tuning over the robots of the running roster. Called whenever a scenario is applied,
    /// which is also how a tuning saved in an earlier session reaches a robot the roster has just built.
    /// Returns how many robots were tuned.
    /// </summary>
    public static int ApplyStoredTunings()
    {
        RobotRoster roster = RobotRoster.Current;
        if (roster == null)
            return 0;

        var robots = new List<Robot>();
        roster.FillRobots(robots);

        // Read what each type shipped with before anything is laid over it: once a tuning has changed the
        // detector, the prefab's own figure is gone from the live instance.
        foreach (Robot robot in robots)
            RememberAuthoredRadius(robot);

        int applied = 0;
        foreach (Robot robot in robots)
        {
            string typeId = TypeIdOf(robot);
            if (!RobotTuningStore.TryLoad(typeId, out RobotTuning tuning))
                continue;

            LayOver(robot, RobotProfiles.Find(typeId), tuning);
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Lays one tuning over the robots of the running roster that drive as <paramref name="typeId"/>, so a
    /// reader who saves or resets sees the change on a scenario that is already running. Returns how many
    /// robots it touched.
    /// </summary>
    private static int ApplyTuningToLiveRobots(string typeId, RobotTuning tuning)
    {
        RobotRoster roster = RobotRoster.Current;
        if (roster == null || tuning == null)
            return 0;

        var robots = new List<Robot>();
        roster.FillRobots(robots);

        string wanted = Canonical(typeId);
        int applied = 0;
        foreach (Robot robot in robots)
        {
            if (Canonical(TypeIdOf(robot)) != wanted)
                continue;

            LayOver(robot, RobotProfiles.Find(typeId), tuning);
            applied++;
        }

        return applied;
    }

    /// <summary>Makes one robot drive as its type tuned by these figures, speed, lidar and detection alike.</summary>
    private static void LayOver(Robot robot, RobotProfile baseProfile, RobotTuning tuning)
    {
        if (robot == null || baseProfile == null || tuning == null)
            return;

        robot.ApplyProfile(tuning.ApplyTo(baseProfile));

        AgentDetector detector = DetectorOf(robot);
        if (detector != null)
            detector.ApplyDetectionRadius(tuning.DetectionRadius);
    }

    private static void RememberAuthoredRadius(Robot robot)
    {
        string typeId = TypeIdOf(robot);
        string key = Canonical(typeId);
        if (AuthoredDetectionRadius.ContainsKey(key))
            return;

        AgentDetector detector = DetectorOf(robot);
        AuthoredDetectionRadius[key] = detector != null ? detector.Radius : 5f;
    }

    /// <summary>
    /// The radius the type shipped with, read from a fresh instance of its prefab the first time the page
    /// shows it. Reading it from a preview instance rather than from a running robot is what makes Reset
    /// meaningful with no scenario loaded.
    /// </summary>
    private float AuthoredRadiusOf(RobotTypeEntry entry)
    {
        string key = Canonical(entry.Id);
        if (AuthoredDetectionRadius.TryGetValue(key, out float cached))
            return cached;

        float radius = 5f;
        AgentDetector detector = _previewInstance != null
            ? _previewInstance.GetComponentInChildren<AgentDetector>(true)
            : null;
        if (detector != null)
            radius = detector.Radius;

        AuthoredDetectionRadius[key] = radius;
        return radius;
    }

    /// <summary>The type a robot drives as, whatever profile it is currently wearing.</summary>
    private static string TypeIdOf(Robot robot)
    {
        if (robot == null)
            return RobotProfiles.DefaultId;

        if (robot.Profile != null)
            return robot.Profile.Id;

        RobotIdentity identity = robot.GetComponent<RobotIdentity>();
        return identity != null ? identity.TypeId : RobotProfiles.DefaultId;
    }

    private static AgentDetector DetectorOf(Robot robot)
    {
        if (robot == null)
            return null;

        AgentDetector detector = robot.GetAgentDetector();
        return detector != null ? detector : robot.GetComponentInChildren<AgentDetector>();
    }

    /// <summary>
    /// Compares type names the way a person writes them: case is ignored, and so are spaces, dashes,
    /// underscores and dots, so "Jackal", "jackal" and "jack-al" are one type. The same rule the profiles
    /// look a type up with, so the two lists agree on what a duplicate is.
    /// </summary>
    private static string Canonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var characters = new List<char>(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
                characters.Add(char.ToLowerInvariant(character));
        }

        return new string(characters.ToArray());
    }

    // -- the 3D preview -------------------------------------------------------

    private void EnsurePreviewCamera()
    {
        if (_previewCamera != null)
            return;

        var cameraObject = new GameObject("RobotPreviewCamera");
        cameraObject.transform.SetParent(transform, false);

        _previewCamera = cameraObject.AddComponent<Camera>();
        _previewCamera.clearFlags = CameraClearFlags.SolidColor;
        // The clear colour is the backdrop's own colour, so a sliver of it left around the stage still reads
        // as part of the room instead of as the black the panel used to open onto.
        _previewCamera.backgroundColor = PreviewBackdropColour;
        _previewCamera.depth = -10f;
        _previewCamera.fieldOfView = 45f;
        _previewCamera.nearClipPlane = 0.05f;
        _previewCamera.farClipPlane = 100f;

        // The preview camera must not draw the interface that is drawn over the whole window.
        int uiLayer = LayerMask.NameToLayer("UI");
        _previewCamera.cullingMask = uiLayer >= 0 ? ~(1 << uiLayer) : ~0;
        _previewCamera.enabled = false;

        EnsurePreviewStage();
        EnsurePreviewLight();
    }

    /// <summary>
    /// Builds the room the preview stands in: a wide floor and a backdrop that the orbit keeps behind the
    /// robot. Both live at <see cref="PreviewOrigin"/>, so they are the only things in the camera's view and
    /// no wall or crowd of the world can fall between the reader and the robot.
    ///
    /// The backdrop follows the view rather than being a wall the camera could come round behind, which is
    /// what lets a full turn always show a surface instead of the empty sky behind the stage.
    /// </summary>
    private void EnsurePreviewStage()
    {
        if (_previewStage != null)
            return;

        _previewStage = new GameObject("RobotPreviewStage");
        _previewStage.transform.SetParent(transform, false);
        _previewStage.transform.position = PreviewOrigin;

        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.SetParent(_previewStage.transform, false);
        floor.transform.localScale = new Vector3(8f, 1f, 8f);
        StripCollider(floor);
        Paint(floor, PreviewFloorColour, lit: true);

        GameObject backdrop = GameObject.CreatePrimitive(PrimitiveType.Cube);
        backdrop.name = "Backdrop";
        backdrop.transform.SetParent(_previewStage.transform, false);
        backdrop.transform.localScale = new Vector3(40f, 24f, 0.2f);
        StripCollider(backdrop);
        Paint(backdrop, PreviewBackdropColour, lit: false);
        _previewBackdrop = backdrop.transform;
    }

    /// <summary>Moves the backdrop to the far side of the robot from wherever the view now stands.</summary>
    private void PlacePreviewBackdrop(Vector3 cameraPosition)
    {
        if (_previewBackdrop == null)
            return;

        Vector3 toCamera = cameraPosition - _previewCenter;
        toCamera.y = 0f;
        if (toCamera.sqrMagnitude < 1e-4f)
            return;

        toCamera.Normalize();
        float behind = _previewRadius * 2f + 8f;
        _previewBackdrop.position = _previewCenter - toCamera * behind;
        _previewBackdrop.rotation = Quaternion.LookRotation(toCamera, Vector3.up);
    }

    private static void StripCollider(GameObject primitive)
    {
        Collider collider = primitive.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);
    }

    /// <summary>
    /// Paints a stage primitive with a plain material. The shader is looked up through the pipelines the
    /// project might be on, because a stage that rendered magenta would be worse than no stage at all.
    /// </summary>
    private static void Paint(GameObject primitive, Color colour, bool lit)
    {
        Renderer renderer = primitive.GetComponent<Renderer>();
        if (renderer == null)
            return;

        Shader shader = Shader.Find(lit ? "HDRP/Lit" : "HDRP/Unlit");
        if (shader == null)
            shader = Shader.Find(lit ? "Universal Render Pipeline/Lit" : "Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find(lit ? "Standard" : "Unlit/Color");
        if (shader == null)
            return;

        var material = new Material(shader) { name = lit ? "RobotPreviewFloor" : "RobotPreviewBackdrop" };
        ApplyColour(material, colour);
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    /// <summary>Writes a colour under whichever name the shader in use gives it.</summary>
    private static void ApplyColour(Material material, Color colour)
    {
        string[] names = { "_BaseColor", "_UnlitColor", "_Color" };
        foreach (string name in names)
        {
            if (material.HasProperty(name))
            {
                material.SetColor(name, colour);
                return;
            }
        }
    }

    /// <summary>
    /// A key light for the preview, added only when the scene carries no directional light. A preview parked
    /// a kilometre below the world still catches a directional light, but a scene lit by point lights alone
    /// would leave the robot dark - and a reader opening this tab wants to see the robot.
    /// </summary>
    private void EnsurePreviewLight()
    {
        if (_previewLight != null || HasDirectionalLight())
            return;

        var lightObject = new GameObject("RobotPreviewLight");
        lightObject.transform.SetParent(_previewCamera.transform, false);
        lightObject.transform.localRotation = Quaternion.Euler(45f, 30f, 0f);

        _previewLight = lightObject.AddComponent<Light>();
        _previewLight.type = LightType.Directional;
        _previewLight.intensity = 1f;
        _previewLight.cullingMask = _previewCamera.cullingMask;
    }

    private static bool HasDirectionalLight()
    {
        foreach (Light light in FindObjectsByType<Light>())
        {
            if (light != null && light.enabled && light.type == LightType.Directional)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the instance the middle column shows when the selected type changes. The existing one, if any,
    /// is destroyed first, so a reader never sees two robots stacked on top of each other.
    /// </summary>
    private void EnsurePreviewMatchesSelection()
    {
        if (_selectedTypeId == null)
        {
            DestroyPreviewInstance();
            return;
        }

        if (_previewRoot != null && _previewTypeId == _selectedTypeId)
            return;

        DestroyPreviewInstance();

        RobotTypeEntry entry = FindEntry(_selectedTypeId);
        if (entry == null)
            return;

        _previewRoot = new GameObject($"RobotPreview_{entry.Id}");
        _previewRoot.transform.SetParent(transform, false);
        _previewRoot.transform.position = PreviewOrigin;

        // Built inactive so the prefab's own components get no frame before they are neutralised: a robot
        // shown here must neither drive, nor sense, nor register a publisher.
        _previewRoot.SetActive(false);

        GameObject prefab = Catalog != null ? Catalog.PrefabFor(entry.Id) : null;
        if (prefab != null)
        {
            _previewInstance = Instantiate(prefab, _previewRoot.transform, false);
            _previewIsPlaceholder = false;
        }
        else
        {
            _previewInstance = CreatePlaceholder(entry);
            _previewInstance.transform.SetParent(_previewRoot.transform, false);
            _previewIsPlaceholder = true;
        }

        _previewInstance.transform.localPosition = Vector3.zero;
        _previewInstance.transform.localRotation = Quaternion.identity;

        Neutralize(_previewInstance);
        _previewRoot.SetActive(true);
        _previewTypeId = _selectedTypeId;

        ResetPreviewOrbit();
        FramePreview();
    }

    /// <summary>
    /// A neutral body for a type the catalogue has no prefab for, so the column shows something and its
    /// label can say the type has no prefab rather than leaving the panel empty.
    /// </summary>
    private static GameObject CreatePlaceholder(RobotTypeEntry entry)
    {
        float radius = entry.Profile != null ? entry.Profile.Radius : 0.3f;

        GameObject placeholder = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        placeholder.name = $"Placeholder for {entry.Id}";
        placeholder.transform.localScale = new Vector3(radius * 2f, 0.9f, radius * 2f);
        return placeholder;
    }

    /// <summary>
    /// Takes a preview instance out of the simulation: every behaviour is disabled, and every shape the
    /// physics knows about is switched off, so the body can neither be pushed, seen by a scan, nor driven.
    /// The renderers are left alone - the picture is the one thing this page is for.
    /// </summary>
    private static void Neutralize(GameObject instance)
    {
        if (instance == null)
            return;

        foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour != null)
                behaviour.enabled = false;
        }

        foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
        {
            if (collider != null)
                collider.enabled = false;
        }

        foreach (Rigidbody body in instance.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body == null)
                continue;

            body.isKinematic = true;
            body.detectCollisions = false;
        }

        foreach (ArticulationBody articulation in instance.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (articulation != null)
                articulation.enabled = false;
        }

        foreach (Joint joint in instance.GetComponentsInChildren<Joint>(true))
        {
            if (joint != null)
                Destroy(joint);
        }
    }

    private void DestroyPreviewInstance()
    {
        if (_previewRoot != null)
            Destroy(_previewRoot);

        _previewRoot = null;
        _previewInstance = null;
        _previewTypeId = null;
        _previewIsPlaceholder = false;
    }

    /// <summary>
    /// Frames the preview instance the way a product shot would: a three-quarter view from above, at the
    /// distance that makes the whole robot fill the panel whatever its size.
    /// </summary>
    private void FramePreview()
    {
        if (_previewCamera == null || _previewInstance == null)
            return;

        if (TryGetBounds(_previewInstance, out Bounds bounds))
        {
            _previewCenter = bounds.center;
            _previewRadius = Mathf.Clamp(bounds.extents.magnitude, 0.25f, 3f);
        }
        else
        {
            _previewCenter = _previewRoot != null ? _previewRoot.transform.position + Vector3.up * 0.3f : PreviewOrigin;
            _previewRadius = 0.5f;
        }

        UpdatePreviewCamera();
    }

    /// <summary>
    /// Stands the camera on the orbit the reader has set - the angle they dragged it to and the distance they
    /// wound the wheel to - looking at the middle of the robot. Every turn, every zoom and every new selection
    /// goes through here, so the shot and the drag can never disagree about where the view stands.
    /// </summary>
    private void UpdatePreviewCamera()
    {
        if (_previewCamera == null)
            return;

        float pitch = Mathf.Clamp(_previewPitch, MinPreviewPitch, MaxPreviewPitch);
        float yawRadians = _previewYaw * Mathf.Deg2Rad;
        float pitchRadians = pitch * Mathf.Deg2Rad;
        Vector3 direction = new Vector3(
            Mathf.Cos(pitchRadians) * Mathf.Sin(yawRadians),
            Mathf.Sin(pitchRadians),
            Mathf.Cos(pitchRadians) * Mathf.Cos(yawRadians));

        float framed = _previewRadius / Mathf.Sin(_previewCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float distance = Mathf.Max(framed, _previewRadius * 1.6f) * _previewZoom;

        Vector3 position = _previewCenter + direction * distance;
        _previewCamera.transform.position = position;
        _previewCamera.transform.LookAt(_previewCenter);

        _previewCamera.nearClipPlane = Mathf.Max(0.02f, distance - _previewRadius * 3f);
        _previewCamera.farClipPlane = distance + _previewRadius * 8f + 30f;

        PlacePreviewBackdrop(position);
    }

    /// <summary>Puts the inspection view back on the shot a freshly selected type opens on.</summary>
    private void ResetPreviewOrbit()
    {
        _previewYaw = DefaultPreviewYaw;
        _previewPitch = DefaultPreviewPitch;
        _previewZoom = 1f;
        _previewDragging = false;
    }

    // -- turning the type over by hand --------------------------------------------

    /// <summary>Anywhere on the picture starts a drag: the whole panel is the grab surface of the orbit.</summary>
    private void OnPreviewPointerDown(PointerDownEvent evt)
    {
        if (evt.button != 0 && evt.button != 2)
            return;

        _previewDragging = true;
        _previewLastPointer = evt.position;
        _previewContainer.CapturePointer(evt.pointerId);
        evt.StopPropagation();
    }

    private void OnPreviewPointerMove(PointerMoveEvent evt)
    {
        if (!_previewDragging)
            return;

        Vector2 position = evt.position;
        Vector2 delta = position - _previewLastPointer;
        _previewLastPointer = position;

        _previewYaw += delta.x * PreviewOrbitDegreesPerPixel;
        _previewPitch = Mathf.Clamp(
            _previewPitch - delta.y * PreviewOrbitDegreesPerPixel, MinPreviewPitch, MaxPreviewPitch);
        UpdatePreviewCamera();
        evt.StopPropagation();
    }

    private void OnPreviewPointerUp(PointerUpEvent evt)
    {
        if (!_previewDragging)
            return;

        _previewDragging = false;
        _previewContainer.ReleasePointer(evt.pointerId);
        evt.StopPropagation();
    }

    /// <summary>The wheel pulls the view in and pushes it out along the same orbit.</summary>
    private void OnPreviewWheel(WheelEvent evt)
    {
        _previewZoom = Mathf.Clamp(
            _previewZoom * (1f - evt.delta.y * 0.08f), MinPreviewZoom, MaxPreviewZoom);
        UpdatePreviewCamera();
        evt.StopPropagation();
    }

    private static bool TryGetBounds(GameObject instance, out Bounds bounds)
    {
        bounds = default;
        if (instance == null)
            return false;

        bool any = false;
        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null)
                continue;

            if (!any)
            {
                bounds = renderer.bounds;
                any = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return any;
    }

    private void TickPreview()
    {
        if (!_initialized || _previewCamera == null || _previewContainer == null)
            return;

        bool shown = _previewContainer.panel != null
                     && _previewRoot != null
                     && _previewRoot.activeSelf
                     && _previewInstance != null;
        _previewCamera.enabled = shown;
    }

    private void OnPreviewResized(GeometryChangedEvent evt) => UpdatePreviewTexture();

    private void UpdatePreviewTexture()
    {
        if (_previewCamera == null || _previewContainer == null)
            return;

        float width = _previewContainer.resolvedStyle.width;
        float height = _previewContainer.resolvedStyle.height;
        if (width <= 0f || height <= 0f)
            return;

        int w = Mathf.Max(1, (int)width);
        int h = Mathf.Max(1, (int)height);
        if (_previewTexture != null && _previewTexture.width == w && _previewTexture.height == h)
            return;

        if (_previewTexture != null)
        {
            _previewCamera.targetTexture = null;
            _previewTexture.Release();
            Destroy(_previewTexture);
        }

        _previewTexture = new RenderTexture(w, h, 24) { name = "RobotPreview" };
        _previewTexture.Create();
        _previewCamera.targetTexture = _previewTexture;
        _previewContainer.style.backgroundImage = Background.FromRenderTexture(_previewTexture);

        // The panel changed shape, so the framing that filled it a moment ago may no longer: reframe against
        // the new aspect if an instance is already on display.
        FramePreview();
    }
}
