using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RobotSNAP.Core.Scenario;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>Drives the scenario browser and the three-step scenario creation workflow.</summary>
public class ScenariosTabController : MonoBehaviour
{
    private const string HiddenClass = "scenario-view-hidden";
    private const string StepHiddenClass = "creation-step-hidden";
    private const string ErrorClass = "error";
    private const string TagPlaceholder = "Add a tag…";
    private const string MapPlaceholder = "No environment selected";
    private const string PreviewPlaceholder = "Default cover";

    /// <summary>Room for the worst findings only; the panel is narrow, and the summary line counts them.</summary>
    private const int MaxDryRunFindings = 3;

    [Header("References")]
    [SerializeField] private UIDocument uiDocument;
    [SerializeField, FormerlySerializedAs("_dataService")] private ScenarioDataService dataService;
    [SerializeField, FormerlySerializedAs("_cardTemplate")] private VisualTreeAsset cardTemplate;
    [SerializeField, FormerlySerializedAs("_scenarioLoader")] private ScenarioLoader scenarioLoader;
    [Header("Grid View settings")]
    [SerializeField, FormerlySerializedAs("_columns")] private int columns = 4;
    [SerializeField, FormerlySerializedAs("_cardSpacingPercent")] private float cardSpacingPercent = 0.2f;
    [Header("Debug")]
    [SerializeField, FormerlySerializedAs("_logEvents")] private bool logEvents = true;

    private ScenarioListView _listView;
    private ScenarioDetailsView _detailsView;
    private ScenarioRouteEditor _routeEditor;
    private ScenarioMapRecap _mapRecap;
    private VisualElement _browserView;
    private VisualElement _editorView;
    private readonly VisualElement[] _stepContents = new VisualElement[3];
    private readonly VisualElement[] _stepHeaders = new VisualElement[3];

    private Button _newButton;
    private Button _backButton;
    private Button _cancelButton;
    private Button _previousButton;
    private Button _nextButton;
    private Button _saveButton;
    private Button _addTagButton;
    private Button _openMapBrowserButton;
    private Button _closeMapBrowserButton;
    private Button _confirmMapSelectionButton;
    private Button _openEnvironmentImportButton;
    private Button _browseEnvironmentFileButton;
    private Button _cancelEnvironmentImportButton;
    private Button _confirmEnvironmentImportButton;
    private Button _useDefaultCoverButton;
    private Button _browseCoverImageButton;
    private Button _cancelDeleteScenarioButton;
    private Button _confirmDeleteScenarioButton;

    private Label _editorTitle;
    private Label _editorSubtitle;
    private Label _editorFeedback;
    private Label _nameCounter;
    private Label _descriptionCounter;
    private Label _selectedMapLabel;
    private Label _mapName;
    private Label _mapPointCount;
    private Label _mapMode;
    private Image _environmentImage;
    private Label _environmentPlaceholder;
    private Image _coverPreviewImage;
    private Label _coverImageStatusLabel;
    private TextField _nameField;
    private TextField _descriptionField;
    private TextField _tagInputField;
    private DropdownField _mapDropdown;
    private DropdownField _previewDropdown;
    private DropdownField _tagsDropdown;
    private VisualElement _selectedTagsContainer;

    private VisualElement _mapBrowserOverlay;
    private TextField _mapSearchField;
    private VisualElement _mapDatasetList;
    private VisualElement _mapFileList;
    private Label _mapBrowserFolderLabel;
    private Image _mapBrowserPreviewImage;
    private Label _mapBrowserSelectionLabel;
    private Label _mapBrowserMetadataLabel;

    private VisualElement _environmentImportOverlay;
    private TextField _environmentImportPathField;
    private TextField _environmentImportNameField;
    private FloatField _environmentResolutionField;
    private FloatField _environmentOriginXField;
    private FloatField _environmentOriginZField;
    private Label _environmentImportFeedbackLabel;
    private Image _environmentImportPreviewImage;
    private Label _environmentImportPreviewPlaceholder;
    private Label _environmentImportDimensionsLabel;
    private VisualElement _deleteScenarioOverlay;
    private Label _deleteScenarioNameLabel;

    private FloatField _durationField;

    private Label _summaryName;
    private Label _summaryType;
    private Label _summaryEnvironment;
    private Label _summaryAgents;
    private Label _summaryObjectives;
    private Label _summaryDuration;
    private Label _pedestrianCount;
    private Label _totalAgentCount;
    private Label _summaryRobotConfig;
    private Label _summaryRobotRoute;
    private Label _summaryHumanConfig;
    private Label _summaryHumanRoute;
    private Label _summaryDescription;
    private Label _validationLabel;
    private VisualElement _validationStatusIcon;
    private Label _checkInformation;
    private Label _checkEnvironment;
    private Label _checkRoute;
    private Label _checkHumans;
    private VisualElement _dryRunFindings;

    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _availableMaps = new();
    private bool _initialized;
    private int _currentStep = 1;
    private string _editingScenarioId;
    private ScenarioData _editingScenario;
    private string _pendingDeleteScenarioId;
    private string _loadedMapIdentifier;
    private Texture2D _occupancyTexture;
    private Bounds _occupancyBounds;
    private string _mapBrowserDataset = "All";
    private string _mapBrowserSelection;
    private Texture2D _mapBrowserPreviewTexture;
    private Texture2D _environmentImportPreviewTexture;
    private Texture2D _temporaryCoverTexture;
    private string _coverSourcePath;
    private bool _useDefaultCover = true;

    private void Awake()
    {
        dataService ??= FindAnyObjectByType<ScenarioDataService>();
        scenarioLoader ??= FindAnyObjectByType<ScenarioLoader>();
        MainViewController.OnViewLoaded += OnViewLoaded;
    }

    private void OnDestroy()
    {
        MainViewController.OnViewLoaded -= OnViewLoaded;
        _listView?.Dispose();
        UnregisterEvents();
        ReleaseOccupancyTexture();
        ReleaseTexture(ref _mapBrowserPreviewTexture);
        ReleaseTexture(ref _environmentImportPreviewTexture);
        ReleaseTexture(ref _temporaryCoverTexture);
    }

    private void OnEnable()
    {
        if (_editorView == null)
        {
            _initialized = false;
            return;
        }
        if (_initialized && _editorView.ClassListContains(HiddenClass)) _listView?.Refresh();
    }

    private void OnViewLoaded(string viewName)
    {
        if (viewName == "Scenarios") Initialize();
    }

    private void Initialize()
    {
        if (_initialized)
        {
            _listView?.Refresh();
            return;
        }
        uiDocument ??= GetComponent<UIDocument>();
        if (uiDocument == null)
        {
            Debug.LogError("[ScenariosTabController] UIDocument is missing.");
            return;
        }

        VisualElement root = uiDocument.rootVisualElement;
        var missing = new List<string>();
        _browserView = Require<VisualElement>(root, "ScenarioBrowserView", missing);
        _editorView = Require<VisualElement>(root, "ScenarioEditorView", missing);
        for (int index = 0; index < 3; index++)
        {
            _stepContents[index] = Require<VisualElement>(root, $"Step{index + 1}Content", missing);
            _stepHeaders[index] = Require<VisualElement>(root, $"Step{index + 1}", missing);
        }

        _newButton = Require<Button>(root, "NewScenarioButton", missing);
        _backButton = Require<Button>(root, "BackToScenariosButton", missing);
        _cancelButton = Require<Button>(root, "CancelEditorButton", missing);
        _previousButton = Require<Button>(root, "PreviousStepButton", missing);
        _nextButton = Require<Button>(root, "NextStepButton", missing);
        _saveButton = Require<Button>(root, "SaveEditorButton", missing);
        _addTagButton = Require<Button>(root, "AddTagButton", missing);
        _openMapBrowserButton = Require<Button>(root, "OpenMapBrowserButton", missing);
        _closeMapBrowserButton = Require<Button>(root, "CloseMapBrowserButton", missing);
        _confirmMapSelectionButton = Require<Button>(root, "ConfirmMapSelectionButton", missing);
        _openEnvironmentImportButton = Require<Button>(root, "OpenEnvironmentImportButton", missing);
        _browseEnvironmentFileButton = Require<Button>(root, "BrowseEnvironmentFileButton", missing);
        _cancelEnvironmentImportButton = Require<Button>(root, "CancelEnvironmentImportButton", missing);
        _confirmEnvironmentImportButton = Require<Button>(root, "ConfirmEnvironmentImportButton", missing);
        _useDefaultCoverButton = Require<Button>(root, "UseDefaultCoverButton", missing);
        _browseCoverImageButton = Require<Button>(root, "BrowseCoverImageButton", missing);
        _cancelDeleteScenarioButton = Require<Button>(root, "CancelDeleteScenarioButton", missing);
        _confirmDeleteScenarioButton = Require<Button>(root, "ConfirmDeleteScenarioButton", missing);

        _editorTitle = Require<Label>(root, "EditorTitle", missing);
        _editorSubtitle = Require<Label>(root, "EditorSubtitle", missing);
        _editorFeedback = Require<Label>(root, "EditorFeedbackLabel", missing);
        _nameCounter = Require<Label>(root, "NameCounter", missing);
        _descriptionCounter = Require<Label>(root, "DescriptionCounter", missing);
        _selectedMapLabel = Require<Label>(root, "SelectedMapLabel", missing);
        _mapName = Require<Label>(root, "MapName", missing);
        _mapPointCount = Require<Label>(root, "MapPointCount", missing);
        _mapMode = Require<Label>(root, "MapMode", missing);
        _environmentImage = Require<Image>(root, "EnvironmentImage", missing);
        _environmentPlaceholder = Require<Label>(root, "EnvironmentPlaceholder", missing);
        _coverPreviewImage = Require<Image>(root, "CoverPreviewImage", missing);
        _coverImageStatusLabel = Require<Label>(root, "CoverImageStatusLabel", missing);
        _nameField = Require<TextField>(root, "NameField", missing);
        _descriptionField = Require<TextField>(root, "DescriptionField", missing);
        _tagInputField = Require<TextField>(root, "TagInputField", missing);
        _mapDropdown = Require<DropdownField>(root, "MapDropdown", missing);
        _previewDropdown = Require<DropdownField>(root, "PreviewDropdown", missing);
        _tagsDropdown = Require<DropdownField>(root, "TagsDropdown", missing);
        _selectedTagsContainer = Require<VisualElement>(root, "SelectedTags", missing);

        _mapBrowserOverlay = Require<VisualElement>(root, "MapBrowserOverlay", missing);
        _mapSearchField = Require<TextField>(root, "MapSearchField", missing);
        _mapDatasetList = Require<VisualElement>(root, "MapDatasetList", missing);
        _mapFileList = Require<VisualElement>(root, "MapFileList", missing);
        _mapBrowserFolderLabel = Require<Label>(root, "MapBrowserFolderLabel", missing);
        _mapBrowserPreviewImage = Require<Image>(root, "MapBrowserPreviewImage", missing);
        _mapBrowserSelectionLabel = Require<Label>(root, "MapBrowserSelectionLabel", missing);
        _mapBrowserMetadataLabel = Require<Label>(root, "MapBrowserMetadataLabel", missing);

        _environmentImportOverlay = Require<VisualElement>(root, "EnvironmentImportOverlay", missing);
        _environmentImportPathField = Require<TextField>(root, "EnvironmentImportPathField", missing);
        _environmentImportNameField = Require<TextField>(root, "EnvironmentImportNameField", missing);
        _environmentResolutionField = Require<FloatField>(root, "EnvironmentResolutionField", missing);
        _environmentOriginXField = Require<FloatField>(root, "EnvironmentOriginXField", missing);
        _environmentOriginZField = Require<FloatField>(root, "EnvironmentOriginZField", missing);
        _environmentImportFeedbackLabel = Require<Label>(root, "EnvironmentImportFeedbackLabel", missing);
        _environmentImportPreviewImage = Require<Image>(root, "EnvironmentImportPreviewImage", missing);
        _environmentImportPreviewPlaceholder = Require<Label>(root, "EnvironmentImportPreviewPlaceholder", missing);
        _environmentImportDimensionsLabel = Require<Label>(root, "EnvironmentImportDimensionsLabel", missing);
        _deleteScenarioOverlay = Require<VisualElement>(root, "DeleteScenarioOverlay", missing);
        _deleteScenarioNameLabel = Require<Label>(root, "DeleteScenarioNameLabel", missing);

        _durationField = Require<FloatField>(root, "DurationField", missing);

        _summaryName = Require<Label>(root, "SummaryName", missing);
        _summaryType = Require<Label>(root, "SummaryType", missing);
        _summaryEnvironment = Require<Label>(root, "SummaryEnvironment", missing);
        _summaryAgents = Require<Label>(root, "SummaryAgents", missing);
        _summaryObjectives = Require<Label>(root, "SummaryObjectives", missing);
        _summaryDuration = Require<Label>(root, "SummaryDuration", missing);
        _pedestrianCount = Require<Label>(root, "PedestrianCount", missing);
        _totalAgentCount = Require<Label>(root, "TotalAgentCount", missing);
        _summaryRobotConfig = Require<Label>(root, "SummaryRobotConfig", missing);
        _summaryRobotRoute = Require<Label>(root, "SummaryRobotRoute", missing);
        _summaryHumanConfig = Require<Label>(root, "SummaryHumanConfig", missing);
        _summaryHumanRoute = Require<Label>(root, "SummaryHumanRoute", missing);
        _summaryDescription = Require<Label>(root, "SummaryDescription", missing);
        _validationLabel = Require<Label>(root, "ValidationLabel", missing);
        _validationStatusIcon = Require<VisualElement>(root, "ValidationStatusIcon", missing);
        _checkInformation = Require<Label>(root, "CheckInformation", missing);
        _checkEnvironment = Require<Label>(root, "CheckEnvironment", missing);
        _checkRoute = Require<Label>(root, "CheckRoute", missing);
        _checkHumans = Require<Label>(root, "CheckHumans", missing);
        _dryRunFindings = Require<VisualElement>(root, "DryRunFindings", missing);

        if (missing.Count > 0)
        {
            Debug.LogError($"[ScenariosTabController] Missing UI elements: {string.Join(", ", missing)}");
            return;
        }

        dataService?.EnsureLoaded();
        BuildBrowser(root);
        ConfigureEditor(root);
        RegisterEvents();
        ShowBrowser();
        _initialized = true;
        Log("Scenario UI initialized.");
    }

    private static T Require<T>(VisualElement root, string name, ICollection<string> missing) where T : VisualElement
    {
        T element = root.Q<T>(name);
        if (element == null) missing.Add(name);
        return element;
    }

    private void BuildBrowser(VisualElement root)
    {
        VisualElement browserContainer = root.Q<VisualElement>("ScenarioBrowser");
        VisualElement detailsContainer = root.Q<VisualElement>("ScenarioDetailsContainer");
        if (browserContainer == null || detailsContainer == null)
        {
            Debug.LogError("[ScenariosTabController] Scenario browser containers are missing.");
            return;
        }
        browserContainer.Clear();
        _listView = new ScenarioListView(cardTemplate, columns, cardSpacingPercent);
        _listView.Initialize(dataService);
        _listView.OnScenarioSelected += OnScenarioSelected;
        browserContainer.Add(_listView);
        detailsContainer.Clear();
        _detailsView = new ScenarioDetailsView(dataService) { LogEvents = false };
        detailsContainer.Add(_detailsView);
        var actions = new VisualElement();
        actions.AddToClassList("scenario-details-actions");
        actions.Add(CreateDetailsAction("Edit", OpenSelectedScenario, "secondary"));
        actions.Add(CreateDetailsAction("Delete", RequestDeleteSelectedScenario, "danger"));
        actions.Add(CreateDetailsAction("Use scenario", UseSelectedScenario, "primary"));
        detailsContainer.Add(actions);
    }

    private static Button CreateDetailsAction(string text, Action clicked, string variant)
    {
        var button = new Button(clicked) { text = text };
        button.AddToClassList("action-button");
        button.AddToClassList(variant);
        return button;
    }

    private void ConfigureEditor(VisualElement root)
    {
        _environmentImage.scaleMode = ScaleMode.ScaleToFit;
        _coverPreviewImage.scaleMode = ScaleMode.ScaleAndCrop;
        _mapBrowserPreviewImage.scaleMode = ScaleMode.ScaleToFit;
        _environmentImportPreviewImage.scaleMode = ScaleMode.ScaleToFit;
        RefreshChoiceOptions();
        _routeEditor = new ScenarioRouteEditor(root);
        _mapRecap = new ScenarioMapRecap(root);

        _nameField.RegisterValueChangedCallback(evt =>
        {
            _nameCounter.text = $"{evt.newValue?.Length ?? 0}/60";
            ClearFeedback();
        });
        _descriptionField.RegisterValueChangedCallback(evt => _descriptionCounter.text = $"{evt.newValue?.Length ?? 0}/300");
        _tagsDropdown.RegisterValueChangedCallback(evt =>
        {
            if (!string.IsNullOrWhiteSpace(evt.newValue) && evt.newValue != TagPlaceholder) AddTag(evt.newValue);
            _tagsDropdown.SetValueWithoutNotify(TagPlaceholder);
        });
        _tagInputField.RegisterCallback<KeyDownEvent>(evt =>
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            AddCustomTag();
            evt.StopPropagation();
        });
        _mapDropdown.RegisterValueChangedCallback(_ =>
        {
            UpdateSelectedMapLabel();
            ClearFeedback();
            UpdateEnvironmentPreview();
        });
        _mapSearchField.RegisterValueChangedCallback(_ => RebuildMapFileList());
        _environmentImportPathField.RegisterValueChangedCallback(evt =>
        {
            if (string.IsNullOrWhiteSpace(_environmentImportNameField.value) && File.Exists(evt.newValue))
                _environmentImportNameField.SetValueWithoutNotify(Path.GetFileNameWithoutExtension(evt.newValue));
            _environmentImportFeedbackLabel.text = string.Empty;
            UpdateEnvironmentImportPreview();
        });
        _environmentResolutionField.RegisterValueChangedCallback(_ => UpdateImportDimensionsLabel());
    }

    private void RegisterEvents()
    {
        _newButton.clicked += OpenNewScenario;
        _backButton.clicked += ShowBrowser;
        _cancelButton.clicked += ShowBrowser;
        _previousButton.clicked += GoToPreviousStep;
        _nextButton.clicked += GoToNextStep;
        _saveButton.clicked += SaveScenario;
        _addTagButton.clicked += AddCustomTag;
        _openMapBrowserButton.clicked += OpenMapBrowser;
        _closeMapBrowserButton.clicked += CloseMapBrowser;
        _confirmMapSelectionButton.clicked += ConfirmMapSelection;
        _openEnvironmentImportButton.clicked += OpenEnvironmentImport;
        _browseEnvironmentFileButton.clicked += BrowseEnvironmentFile;
        _cancelEnvironmentImportButton.clicked += CloseEnvironmentImport;
        _confirmEnvironmentImportButton.clicked += ImportEnvironment;
        _useDefaultCoverButton.clicked += UseDefaultCover;
        _browseCoverImageButton.clicked += BrowseCoverImage;
        _cancelDeleteScenarioButton.clicked += CloseDeleteConfirmation;
        _confirmDeleteScenarioButton.clicked += ConfirmDeleteScenario;
    }

    private void UnregisterEvents()
    {
        if (_newButton != null) _newButton.clicked -= OpenNewScenario;
        if (_backButton != null) _backButton.clicked -= ShowBrowser;
        if (_cancelButton != null) _cancelButton.clicked -= ShowBrowser;
        if (_previousButton != null) _previousButton.clicked -= GoToPreviousStep;
        if (_nextButton != null) _nextButton.clicked -= GoToNextStep;
        if (_saveButton != null) _saveButton.clicked -= SaveScenario;
        if (_addTagButton != null) _addTagButton.clicked -= AddCustomTag;
        if (_openMapBrowserButton != null) _openMapBrowserButton.clicked -= OpenMapBrowser;
        if (_closeMapBrowserButton != null) _closeMapBrowserButton.clicked -= CloseMapBrowser;
        if (_confirmMapSelectionButton != null) _confirmMapSelectionButton.clicked -= ConfirmMapSelection;
        if (_openEnvironmentImportButton != null) _openEnvironmentImportButton.clicked -= OpenEnvironmentImport;
        if (_browseEnvironmentFileButton != null) _browseEnvironmentFileButton.clicked -= BrowseEnvironmentFile;
        if (_cancelEnvironmentImportButton != null) _cancelEnvironmentImportButton.clicked -= CloseEnvironmentImport;
        if (_confirmEnvironmentImportButton != null) _confirmEnvironmentImportButton.clicked -= ImportEnvironment;
        if (_useDefaultCoverButton != null) _useDefaultCoverButton.clicked -= UseDefaultCover;
        if (_browseCoverImageButton != null) _browseCoverImageButton.clicked -= BrowseCoverImage;
        if (_cancelDeleteScenarioButton != null) _cancelDeleteScenarioButton.clicked -= CloseDeleteConfirmation;
        if (_confirmDeleteScenarioButton != null) _confirmDeleteScenarioButton.clicked -= ConfirmDeleteScenario;
    }

    private void RefreshChoiceOptions()
    {
        var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scenarioLoader != null)
            foreach (string map in scenarioLoader.GetAvailableMaps())
                if (!string.IsNullOrWhiteSpace(map)) maps.Add(map);
        if (dataService != null)
            foreach (ScenarioInfo info in dataService.AllScenarios.Values)
                if (!string.IsNullOrWhiteSpace(info?.MapImage)) maps.Add(info.MapImage);
        _availableMaps.Clear();
        _availableMaps.AddRange(maps.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        _mapDropdown.choices = new[] { MapPlaceholder }.Concat(_availableMaps).ToList();
        _previewDropdown.choices = new List<string> { PreviewPlaceholder };
        List<string> knownTags = dataService?.GetAllTags()
            .Where(tag => !string.Equals(tag, "All", StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => tag).ToList() ?? new List<string>();
        _tagsDropdown.choices = new[] { TagPlaceholder }.Concat(knownTags).ToList();
        if (_mapDatasetList != null)
        {
            RebuildMapDatasetList();
            RebuildMapFileList();
        }
    }

    /// <summary>
    /// Opens the creation workflow on a blank scenario. Public because the "New Scenario" button of
    /// the scenario popup lands here: creating a scenario is this tab's job, wherever the request
    /// comes from.
    /// </summary>
    public void OpenNewScenario()
    {
        _editingScenarioId = null;
        _editingScenario = null;
        ResetForm();
        ShowEditor();
    }

    private void OpenSelectedScenario()
    {
        string scenarioId = _listView?.SelectedScenarioId;
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            Debug.LogWarning("[ScenariosTabController] Select a scenario before editing it.");
            return;
        }
        _editingScenarioId = scenarioId;
        _editingScenario = scenarioLoader?.LoadScenario(scenarioId);
        ResetForm();
        FillForm(_editingScenario, _listView.SelectedScenarioInfo);
        ShowEditor();
    }

    private void ShowEditor()
    {
        _browserView.AddToClassList(HiddenClass);
        _editorView.RemoveFromClassList(HiddenClass);
        _editorTitle.text = string.IsNullOrEmpty(_editingScenarioId) ? "New scenario" : "Edit scenario";
        _saveButton.text = string.IsNullOrEmpty(_editingScenarioId) ? "Save scenario" : "Save changes";
        SetStep(1);
    }

    private void ShowBrowser()
    {
        CloseMapBrowser();
        CloseEnvironmentImport();
        if (_editorView != null) _editorView.AddToClassList(HiddenClass);
        if (_browserView != null) _browserView.RemoveFromClassList(HiddenClass);
        ClearFeedback();
        _listView?.Refresh();
    }

    private void ResetForm()
    {
        RefreshChoiceOptions();
        _selectedTags.Clear();
        RebuildTagChips();
        _nameField.SetValueWithoutNotify(string.Empty);
        _descriptionField.SetValueWithoutNotify(string.Empty);
        _tagInputField.SetValueWithoutNotify(string.Empty);
        _mapDropdown.SetValueWithoutNotify(MapPlaceholder);
        _previewDropdown.SetValueWithoutNotify(PreviewPlaceholder);
        _tagsDropdown.SetValueWithoutNotify(TagPlaceholder);
        _durationField.SetValueWithoutNotify(0f);
        _nameCounter.text = "0/60";
        _descriptionCounter.text = "0/300";
        _mapName.text = "—";
        _mapPointCount.text = "—";
        _mapMode.text = "—";
        _routeEditor.Reset();
        UseDefaultCover();
        UpdateSelectedMapLabel();
        ClearFeedback();
        UpdateEnvironmentPreview();
    }

    private void FillForm(ScenarioData scenario, ScenarioInfo fallbackInfo)
    {
        ScenarioInfo info = scenario?.Info ?? fallbackInfo;
        if (info != null)
        {
            _nameField.SetValueWithoutNotify(info.Name ?? string.Empty);
            _descriptionField.SetValueWithoutNotify(info.Description ?? string.Empty);
            _nameCounter.text = $"{_nameField.value.Length}/60";
            _descriptionCounter.text = $"{_descriptionField.value.Length}/300";
            EnsureChoice(_mapDropdown, info.MapImage, MapPlaceholder);
            _durationField.SetValueWithoutNotify(Mathf.Max(0f, info.Duration));
            _selectedTags.Clear();
            if (info.Tags != null)
                foreach (string tag in info.Tags)
                    if (!string.IsNullOrWhiteSpace(tag)) _selectedTags.Add(tag.Trim());
            RebuildTagChips();
            if (string.IsNullOrWhiteSpace(info.PreviewImage)) UseDefaultCover();
            else UseExistingCover(info.PreviewImage);
        }
        _routeEditor.Load(scenario);
        UpdateSelectedMapLabel();
        UpdateEnvironmentPreview();
    }

    private static void EnsureChoice(DropdownField dropdown, string value, string fallback)
    {
        string selected = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (!dropdown.choices.Contains(selected)) dropdown.choices = dropdown.choices.Concat(new[] { selected }).ToList();
        dropdown.SetValueWithoutNotify(selected);
    }

    private void GoToNextStep()
    {
        if (_currentStep == 1 && !ValidateStepOne(true)) return;
        if (_currentStep == 2 && !ValidateStepTwo(true)) return;
        if (_currentStep < 3) SetStep(_currentStep + 1);
    }

    private void GoToPreviousStep()
    {
        if (_currentStep > 1) SetStep(_currentStep - 1);
    }

    private void SetStep(int step)
    {
        _currentStep = Mathf.Clamp(step, 1, 3);
        ClearFeedback();
        for (int index = 0; index < 3; index++)
        {
            bool active = index == _currentStep - 1;
            SetVisible(_stepContents[index], active, StepHiddenClass);
            _stepHeaders[index].EnableInClassList("active", active);
            _stepHeaders[index].EnableInClassList("completed", index < _currentStep - 1);
        }
        SetVisible(_previousButton, _currentStep > 1, StepHiddenClass);
        SetVisible(_nextButton, _currentStep < 3, StepHiddenClass);
        SetVisible(_saveButton, _currentStep == 3, StepHiddenClass);
        _editorSubtitle.text = _currentStep switch
        {
            1 => "Define the scenario information and environment.",
            2 => "Configure ordered robot and human routes.",
            _ => "Review all settings before saving."
        };
        if (_currentStep == 3) UpdateValidationSummary();
        else if (_currentStep == 2) _routeEditor.SetMap(_occupancyTexture, _occupancyBounds);
    }

    private static void SetVisible(VisualElement element, bool visible, string hiddenClass)
    {
        element.EnableInClassList(hiddenClass, !visible);
    }

    private bool ValidateStepOne(bool showFeedback)
    {
        string message = null;
        if (string.IsNullOrWhiteSpace(_nameField.value)) message = "Scenario name is required.";
        else if (IsPlaceholder(_mapDropdown.value, MapPlaceholder)) message = "Choose an environment before continuing.";
        else if (_durationField.value < 0f) message = "Mission time limit cannot be negative.";
        if (showFeedback) SetFeedback(message);
        return message == null;
    }

    private bool ValidateStepTwo(bool showFeedback)
    {
        string message = null;
        _routeEditor.Validate(out message);
        if (showFeedback) SetFeedback(message);
        return message == null;
    }

    private void UpdateValidationSummary()
    {
        RefreshMapRecap();
        bool informationValid = ValidateStepOne(false);
        bool routeEditorValid = _routeEditor.Validate(out _);
        bool routeValid = routeEditorValid;
        bool humansValid = routeEditorValid;
        bool environmentValid = !IsPlaceholder(_mapDropdown.value, MapPlaceholder);
        bool allValid = informationValid && routeValid && humansValid && environmentValid;
        int humanTotal = _routeEditor.TotalHumanCount;
        int robotTotal = Mathf.Max(1, _routeEditor.RobotRouteCount);
        _summaryName.text = string.IsNullOrWhiteSpace(_nameField.value) ? "Not set" : _nameField.value.Trim();
        _summaryType.text = string.IsNullOrWhiteSpace(_editingScenario?.Info?.Type) ? "Custom" : _editingScenario.Info.Type;
        _summaryEnvironment.text = environmentValid ? _mapDropdown.value : "Not set";
        _summaryAgents.text = (humanTotal + robotTotal).ToString();
        _summaryObjectives.text = _routeEditor.TotalObjectiveCount.ToString();
        _summaryDuration.text = _durationField.value > 0f ? $"{_durationField.value:0.#} s" : "Unlimited";
        _pedestrianCount.text = humanTotal.ToString();
        _totalAgentCount.text = (humanTotal + robotTotal).ToString();
        _summaryRobotConfig.text = $"{robotTotal} robot(s)";
        _summaryRobotRoute.text = _routeEditor.RobotRouteSummary;
        _summaryHumanConfig.text = humanTotal == 0 ? "No humans" : $"{humanTotal} human(s) across {_routeEditor.HumanRouteCount} route(s)";
        _summaryHumanRoute.text = _routeEditor.HumanRouteSummary;
        _summaryDescription.text = string.IsNullOrWhiteSpace(_descriptionField.value) ? "No description." : _descriptionField.value.Trim();
        SetCheck(_checkInformation, informationValid, "General information complete", "Scenario name is required");
        SetCheck(_checkEnvironment, environmentValid, "Environment selected", "Environment is missing");
        SetCheck(_checkRoute, routeValid, "Agent routes valid", "At least one route is invalid");
        SetCheck(_checkHumans, humansValid, "Human routes valid", "Human route configuration is invalid");
        RefreshDryRunDiscoveries();
        _validationStatusIcon.EnableInClassList(ErrorClass, !allValid);
        _validationLabel.EnableInClassList(ErrorClass, !allValid);
        _validationLabel.text = allValid ? "The scenario is valid and ready to be saved." : "Fix the highlighted settings before saving.";
        _saveButton.SetEnabled(allValid);
    }

    /// <summary>
    /// Estimates what the scenario will actually do before it is saved: path lengths, walking times and
    /// the routes that would not move. Only the problems are listed, healthy routes stay silent.
    /// </summary>
    private void RefreshDryRunDiscoveries()
    {
        if (_dryRunFindings == null || _routeEditor == null)
            return;

        _dryRunFindings.Clear();
        List<ScenarioDryRun.Route> routes = _routeEditor.BuildDryRunRoutes();
        List<DryRunFinding> findings = ScenarioDryRun.Analyse(routes);

        var summary = new Label(ScenarioDryRun.Describe(routes));
        summary.AddToClassList("dry-run-summary");
        _dryRunFindings.Add(summary);

        int shown = 0;
        int healthy = 0;
        foreach (DryRunFinding finding in findings)
        {
            if (finding.Severity == DryRunSeverity.Ok)
            {
                healthy++;
                continue;
            }
            if (shown >= MaxDryRunFindings)
                continue;

            var line = new Label(finding.Message);
            line.AddToClassList("dry-run-finding");
            line.AddToClassList(finding.Severity == DryRunSeverity.Error ? "error" : "warning");
            _dryRunFindings.Add(line);
            shown++;
        }

        if (shown > 0 || healthy == 0)
            return;

        var allGood = new Label($"{healthy} route(s) walk without a warning.");
        allGood.AddToClassList("dry-run-finding");
        allGood.AddToClassList("ok");
        _dryRunFindings.Add(allGood);
    }

    private void RefreshMapRecap()
    {
        if (_mapRecap == null || _routeEditor == null)
            return;
        _mapRecap.SetMap(_occupancyTexture, _occupancyBounds);
        _mapRecap.SetRoutes(_routeEditor.BuildRoutePreviews());
    }

    private static void SetCheck(Label label, bool valid, string validText, string invalidText)
    {
        label.EnableInClassList(ErrorClass, !valid);
        label.text = valid ? $"✓ {validText}" : $"× {invalidText}";
    }

    private void SaveScenario()
    {
        if (!ValidateStepOne(true) || !ValidateStepTwo(true))
        {
            UpdateValidationSummary();
            return;
        }
        if (scenarioLoader == null)
        {
            SetFeedback("Scenario loader is unavailable.");
            return;
        }
        string fileId = string.IsNullOrWhiteSpace(_editingScenarioId) ? GetUniqueFileId(ToFileId(_nameField.value)) : _editingScenarioId;
        if (!PrepareCoverForSave(fileId, out string coverError))
        {
            SetFeedback(coverError);
            return;
        }
        ScenarioData scenario = BuildScenario(out IReadOnlyList<string> skippedRoutes);
        // A drawn route the author left with no agent cannot be written, so the save stops and says so: the
        // route stays on screen with the rest of the work instead of vanishing from the file without a word.
        if (skippedRoutes.Count > 0)
        {
            SetFeedback($"Nothing was saved: {string.Join("; ", skippedRoutes)}. Set a count or remove the route.");
            return;
        }
        if (!scenario.IsValid(out string validationError))
        {
            SetFeedback($"The scenario is invalid: {validationError}");
            return;
        }
        if (!scenarioLoader.ExportScenario(scenario, fileId + ".yaml"))
        {
            SetFeedback("YAML save failed. Check the Console for details.");
            return;
        }
        scenarioLoader.ClearScenarioCache();
        dataService?.ReloadAllScenarioInfos();
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        Log($"Scenario '{scenario.Info.Name}' saved as '{fileId}.yaml'.");
        _editingScenarioId = null;
        _editingScenario = null;
        ShowBrowser();
    }

    private ScenarioData BuildScenario(out IReadOnlyList<string> skippedRoutes)
    {
        ScenarioData scenario = _editingScenario ?? new ScenarioData();
        scenario.Info ??= new ScenarioInfo();
        scenario.Points ??= new Dictionary<string, RefPoint>();
        scenario.Robot ??= new RobotScenarioConfig();
        scenario.Humans ??= new List<HumanScenarioConfig>();
        scenario.Info.Name = _nameField.value.Trim();
        scenario.Info.Type = string.IsNullOrWhiteSpace(scenario.Info.Type) ? "Custom" : scenario.Info.Type;
        scenario.Info.Description = _descriptionField.value?.Trim() ?? string.Empty;
        scenario.Info.Version = string.IsNullOrWhiteSpace(scenario.Info.Version) ? "1.0" : scenario.Info.Version;
        scenario.Info.Author = string.IsNullOrWhiteSpace(scenario.Info.Author) ? "RobotSNAP" : scenario.Info.Author;
        scenario.Info.Created = string.IsNullOrWhiteSpace(scenario.Info.Created) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm") : scenario.Info.Created;
        scenario.Info.Tags = _selectedTags.OrderBy(tag => tag).ToArray();
        scenario.Info.MapImage = _mapDropdown.value;
        scenario.Info.PreviewImage = IsPlaceholder(_previewDropdown.value, PreviewPlaceholder) ? string.Empty : _previewDropdown.value;
        scenario.Info.Duration = Mathf.Max(0f, _durationField.value);
        skippedRoutes = _routeEditor.WriteToScenario(scenario);
        // The label the browser shows is the kind of robot this scenario drives. With several of them the
        // first one speaks for the scenario, which is also the one a single-robot client reaches.
        scenario.Info.RobotType = _routeEditor.PrimaryRobotTypeName;
        return scenario;
    }

    private void AddCustomTag()
    {
        string tag = _tagInputField.value?.Trim();
        if (string.IsNullOrWhiteSpace(tag)) return;
        AddTag(tag);
        _tagInputField.SetValueWithoutNotify(string.Empty);
    }

    private void AddTag(string tag)
    {
        if (_selectedTags.Add(tag.Trim())) RebuildTagChips();
    }

    private void RebuildTagChips()
    {
        _selectedTagsContainer.Clear();
        foreach (string tag in _selectedTags.OrderBy(value => value))
        {
            string capturedTag = tag;
            var chip = new Button(() =>
            {
                _selectedTags.Remove(capturedTag);
                RebuildTagChips();
            }) { text = capturedTag + "  ×", tooltip = "Remove this tag" };
            chip.AddToClassList("selected-tag");
            _selectedTagsContainer.Add(chip);
        }
    }

    private void UpdateSelectedMapLabel()
    {
        bool hasMap = !IsPlaceholder(_mapDropdown.value, MapPlaceholder);
        _selectedMapLabel.text = hasMap ? _mapDropdown.value : "No environment selected";
        _selectedMapLabel.EnableInClassList("has-selection", hasMap);
        _mapName.text = hasMap ? _mapDropdown.value : "—";
    }

    private void UpdateEnvironmentPreview()
    {
        string mapIdentifier = IsPlaceholder(_mapDropdown.value, MapPlaceholder) ? null : _mapDropdown.value;
        if (!string.Equals(_loadedMapIdentifier, mapIdentifier, StringComparison.Ordinal))
        {
            ReleaseOccupancyTexture();
            if (!string.IsNullOrWhiteSpace(mapIdentifier) && scenarioLoader != null &&
                scenarioLoader.LoadMapData(mapIdentifier, out Texture2D texture, out Bounds bounds))
            {
                _occupancyTexture = texture;
                _occupancyBounds = bounds;
                _loadedMapIdentifier = mapIdentifier;
            }
        }
        _environmentImage.image = _occupancyTexture;
        _environmentPlaceholder.EnableInClassList(StepHiddenClass, _occupancyTexture != null);
        _environmentPlaceholder.text = string.IsNullOrWhiteSpace(mapIdentifier)
            ? "Select an environment to preview its occupancy grid."
            : "The occupancy image or its JSON metadata could not be loaded.";
        _mapPointCount.text = _occupancyTexture != null ? $"{_occupancyTexture.width} × {_occupancyTexture.height} px" : "—";
        _mapMode.text = _occupancyTexture != null ? $"{_occupancyBounds.size.x:0.##} × {_occupancyBounds.size.z:0.##} m" : "—";
        _routeEditor?.SetMap(_occupancyTexture, _occupancyBounds);
    }

    private void ReleaseOccupancyTexture()
    {
        ReleaseTexture(ref _occupancyTexture);
        _loadedMapIdentifier = null;
        _occupancyBounds = new Bounds();
    }

    private void OpenMapBrowser()
    {
        RefreshChoiceOptions();
        _mapBrowserSelection = IsPlaceholder(_mapDropdown.value, MapPlaceholder) ? null : _mapDropdown.value;
        _mapSearchField.SetValueWithoutNotify(string.Empty);
        _mapBrowserDataset = DatasetOf(_mapBrowserSelection);
        if (string.IsNullOrWhiteSpace(_mapBrowserDataset)) _mapBrowserDataset = "All";
        RebuildMapDatasetList();
        RebuildMapFileList();
        PreviewMapSelection(_mapBrowserSelection);
        _mapBrowserOverlay.RemoveFromClassList(StepHiddenClass);
    }

    private void CloseMapBrowser()
    {
        _mapBrowserOverlay?.AddToClassList(StepHiddenClass);
        ReleaseTexture(ref _mapBrowserPreviewTexture);
        if (_mapBrowserPreviewImage != null) _mapBrowserPreviewImage.image = null;
    }

    private void RebuildMapDatasetList()
    {
        _mapDatasetList.Clear();
        List<IGrouping<string, string>> datasets = _availableMaps.GroupBy(DatasetOf, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToList();
        AddDatasetButton("All", _availableMaps.Count);
        foreach (IGrouping<string, string> dataset in datasets) AddDatasetButton(dataset.Key, dataset.Count());
    }

    private void AddDatasetButton(string dataset, int count)
    {
        string captured = dataset;
        var button = new Button(() =>
        {
            _mapBrowserDataset = captured;
            RebuildMapDatasetList();
            RebuildMapFileList();
        }) { text = $"{dataset}  ({count})" };
        button.AddToClassList("map-dataset-button");
        button.EnableInClassList("selected", string.Equals(dataset, _mapBrowserDataset, StringComparison.OrdinalIgnoreCase));
        _mapDatasetList.Add(button);
    }

    private void RebuildMapFileList()
    {
        _mapFileList.Clear();
        string search = _mapSearchField.value?.Trim() ?? string.Empty;
        List<string> maps = _availableMaps.Where(map =>
            (string.Equals(_mapBrowserDataset, "All", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(DatasetOf(map), _mapBrowserDataset, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(search) || map.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        _mapBrowserFolderLabel.text = string.Equals(_mapBrowserDataset, "All", StringComparison.OrdinalIgnoreCase)
            ? $"All environments · {maps.Count}"
            : $"{_mapBrowserDataset} · {maps.Count}";
        if (maps.Count == 0)
        {
            var empty = new Label("No matching environment.");
            empty.AddToClassList("map-browser-empty");
            _mapFileList.Add(empty);
            return;
        }
        foreach (string map in maps)
        {
            string captured = map;
            var button = new Button(() =>
            {
                _mapBrowserSelection = captured;
                RebuildMapFileList();
                PreviewMapSelection(captured);
            }) { text = Path.GetFileName(map), tooltip = map };
            button.AddToClassList("map-file-button");
            button.EnableInClassList("selected", string.Equals(map, _mapBrowserSelection, StringComparison.OrdinalIgnoreCase));
            _mapFileList.Add(button);
        }
    }

    private void PreviewMapSelection(string mapIdentifier)
    {
        ReleaseTexture(ref _mapBrowserPreviewTexture);
        _mapBrowserPreviewImage.image = null;
        _confirmMapSelectionButton.SetEnabled(false);
        if (string.IsNullOrWhiteSpace(mapIdentifier))
        {
            _mapBrowserSelectionLabel.text = "Select a map to preview it";
            _mapBrowserMetadataLabel.text = string.Empty;
            return;
        }
        _mapBrowserSelectionLabel.text = mapIdentifier;
        if (scenarioLoader != null && scenarioLoader.LoadMapData(mapIdentifier, out Texture2D texture, out Bounds bounds))
        {
            _mapBrowserPreviewTexture = texture;
            _mapBrowserPreviewImage.image = texture;
            _mapBrowserMetadataLabel.text = $"{texture.width} × {texture.height} px\n{bounds.size.x:0.##} × {bounds.size.z:0.##} m";
            _confirmMapSelectionButton.SetEnabled(true);
        }
        else _mapBrowserMetadataLabel.text = "Preview unavailable: check the image and JSON metadata.";
    }

    private void ConfirmMapSelection()
    {
        if (string.IsNullOrWhiteSpace(_mapBrowserSelection)) return;
        EnsureChoice(_mapDropdown, _mapBrowserSelection, MapPlaceholder);
        UpdateSelectedMapLabel();
        UpdateEnvironmentPreview();
        CloseMapBrowser();
    }

    private static string DatasetOf(string mapIdentifier)
    {
        if (string.IsNullOrWhiteSpace(mapIdentifier)) return string.Empty;
        int separator = mapIdentifier.LastIndexOf('/');
        return separator > 0 ? mapIdentifier.Substring(0, separator) : "Root";
    }

    private void OpenEnvironmentImport()
    {
        _environmentImportPathField.SetValueWithoutNotify(string.Empty);
        _environmentImportNameField.SetValueWithoutNotify(string.Empty);
        _environmentResolutionField.SetValueWithoutNotify(0.05f);
        _environmentOriginXField.SetValueWithoutNotify(0f);
        _environmentOriginZField.SetValueWithoutNotify(0f);
        _environmentImportFeedbackLabel.text = string.Empty;
        UpdateEnvironmentImportPreview();
        _environmentImportOverlay.RemoveFromClassList(StepHiddenClass);
    }

    private void CloseEnvironmentImport()
    {
        _environmentImportOverlay?.AddToClassList(StepHiddenClass);
        ReleaseTexture(ref _environmentImportPreviewTexture);
        if (_environmentImportPreviewImage != null) _environmentImportPreviewImage.image = null;
    }

    private void BrowseEnvironmentFile()
    {
#if UNITY_EDITOR
        string path = EditorUtility.OpenFilePanel("Select an occupancy grid", string.Empty, "png,jpg,jpeg");
        if (!string.IsNullOrWhiteSpace(path)) _environmentImportPathField.value = path;
#else
        _environmentImportFeedbackLabel.text = "Enter the full path to the image.";
#endif
    }

    private void UpdateEnvironmentImportPreview()
    {
        ReleaseTexture(ref _environmentImportPreviewTexture);
        _environmentImportPreviewImage.image = null;
        string path = _environmentImportPathField.value;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _environmentImportPreviewPlaceholder.text = "Choose a PNG, JPG or JPEG file to preview the grid.";
            _environmentImportPreviewPlaceholder.RemoveFromClassList(StepHiddenClass);
            _environmentImportDimensionsLabel.text = "No image selected";
            return;
        }
        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                DestroyTexture(texture);
                _environmentImportPreviewPlaceholder.text = "This image could not be decoded.";
                _environmentImportPreviewPlaceholder.RemoveFromClassList(StepHiddenClass);
                _environmentImportDimensionsLabel.text = "Invalid image";
                return;
            }
            _environmentImportPreviewTexture = texture;
            _environmentImportPreviewImage.image = texture;
            _environmentImportPreviewPlaceholder.AddToClassList(StepHiddenClass);
            UpdateImportDimensionsLabel();
        }
        catch (Exception exception)
        {
            _environmentImportPreviewPlaceholder.text = "Preview failed.";
            _environmentImportPreviewPlaceholder.RemoveFromClassList(StepHiddenClass);
            _environmentImportDimensionsLabel.text = exception.Message;
        }
    }

    private void UpdateImportDimensionsLabel()
    {
        if (_environmentImportPreviewTexture == null) return;
        float resolution = Mathf.Max(0f, _environmentResolutionField.value);
        _environmentImportDimensionsLabel.text =
            $"{_environmentImportPreviewTexture.width} × {_environmentImportPreviewTexture.height} px  ·  " +
            $"{_environmentImportPreviewTexture.width * resolution:0.##} × {_environmentImportPreviewTexture.height * resolution:0.##} m";
    }

    private void ImportEnvironment()
    {
        if (scenarioLoader == null)
        {
            _environmentImportFeedbackLabel.text = "Scenario loader is unavailable.";
            return;
        }
        if (!OccupancyMapImporter.TryImport(
                _environmentImportPathField.value, scenarioLoader.MapsPath, _environmentImportNameField.value,
                _environmentResolutionField.value, _environmentOriginXField.value, _environmentOriginZField.value,
                out string mapIdentifier, out string error))
        {
            _environmentImportFeedbackLabel.text = error;
            return;
        }
        scenarioLoader.ClearMapCache();
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        RefreshChoiceOptions();
        EnsureChoice(_mapDropdown, mapIdentifier, MapPlaceholder);
        UpdateSelectedMapLabel();
        UpdateEnvironmentPreview();
        CloseEnvironmentImport();
        SetFeedback($"Environment '{mapIdentifier}' imported.");
    }

    private void UseDefaultCover()
    {
        _useDefaultCover = true;
        _coverSourcePath = null;
        ReleaseTexture(ref _temporaryCoverTexture);
        _previewDropdown.SetValueWithoutNotify(PreviewPlaceholder);
        _coverPreviewImage.image = Resources.Load<Texture2D>("ScenarioPreviews/default");
        _coverImageStatusLabel.text = "Default 16:9 cover";
        _useDefaultCoverButton.EnableInClassList("active", true);
        _browseCoverImageButton.EnableInClassList("active", false);
    }

    private void UseExistingCover(string previewName)
    {
        _useDefaultCover = false;
        _coverSourcePath = null;
        ReleaseTexture(ref _temporaryCoverTexture);
        EnsureChoice(_previewDropdown, previewName, PreviewPlaceholder);
        string resourceName = Path.GetFileNameWithoutExtension(previewName);
        _coverPreviewImage.image = Resources.Load<Texture2D>($"ScenarioPreviews/{resourceName}") ??
                                   Resources.Load<Texture2D>("ScenarioPreviews/default");
        _coverImageStatusLabel.text = $"Current cover · {previewName}";
        _useDefaultCoverButton.EnableInClassList("active", false);
        _browseCoverImageButton.EnableInClassList("active", true);
    }

    private void BrowseCoverImage()
    {
#if UNITY_EDITOR
        string path = EditorUtility.OpenFilePanel("Select a scenario cover", string.Empty, "png,jpg,jpeg");
        if (string.IsNullOrWhiteSpace(path)) return;
        ReleaseTexture(ref _temporaryCoverTexture);
        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                DestroyTexture(texture);
                SetFeedback("The selected cover image could not be decoded.");
                return;
            }
            _temporaryCoverTexture = texture;
            _coverSourcePath = path;
            _useDefaultCover = false;
            _coverPreviewImage.image = texture;
            _coverImageStatusLabel.text = $"Custom cover · {texture.width} × {texture.height} px";
            _useDefaultCoverButton.EnableInClassList("active", false);
            _browseCoverImageButton.EnableInClassList("active", true);
            ClearFeedback();
        }
        catch (Exception exception)
        {
            SetFeedback($"Cover preview failed: {exception.Message}");
        }
#else
        SetFeedback("Cover import is available in the Unity Editor.");
#endif
    }

    private bool PrepareCoverForSave(string fileId, out string error)
    {
        error = null;
        if (_useDefaultCover)
        {
            _previewDropdown.SetValueWithoutNotify(PreviewPlaceholder);
            return true;
        }
        if (string.IsNullOrWhiteSpace(_coverSourcePath)) return true;
        string destination = Path.Combine(Application.dataPath, "Resources", "ScenarioPreviews");
        if (!ScenarioCoverImporter.TryImport(_coverSourcePath, destination, fileId, out string fileName, out error)) return false;
        EnsureChoice(_previewDropdown, fileName, PreviewPlaceholder);
        return true;
    }

    private void OnScenarioSelected(string scenarioId)
    {
        _detailsView?.ShowScenario(dataService?.GetScenarioInfo(scenarioId));
        Log($"Scenario selected: {scenarioId}");
    }

    private void UseSelectedScenario()
    {
        string scenarioId = _listView?.SelectedScenarioId;
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            Debug.LogWarning("[ScenariosTabController] Select a scenario before using it.");
            return;
        }

        dataService?.LoadScenario(scenarioId);
        FindAnyObjectByType<SidebarController>()?.ShowView("Simulator");
    }

    private void RequestDeleteSelectedScenario()
    {
        string scenarioId = _listView?.SelectedScenarioId;
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            Debug.LogWarning("[ScenariosTabController] Select a scenario before deleting it.");
            return;
        }
        _pendingDeleteScenarioId = scenarioId;
        string displayName = _listView.SelectedScenarioInfo?.Name;
        _deleteScenarioNameLabel.text = string.IsNullOrWhiteSpace(displayName) ? scenarioId : displayName;
        _deleteScenarioOverlay.RemoveFromClassList(HiddenClass);
    }

    private void CloseDeleteConfirmation()
    {
        _pendingDeleteScenarioId = null;
        _deleteScenarioOverlay?.AddToClassList(HiddenClass);
    }

    private void ConfirmDeleteScenario()
    {
        if (string.IsNullOrWhiteSpace(_pendingDeleteScenarioId) || scenarioLoader == null) return;
        string scenarioId = _pendingDeleteScenarioId;
        if (!scenarioLoader.ArchiveScenario(scenarioId, out string archivedPath, out string error))
        {
            _deleteScenarioNameLabel.text = error;
            return;
        }
        CloseDeleteConfirmation();
        dataService?.ReloadAllScenarioInfos();
        _listView?.ClearSelection();
        _detailsView?.ShowEmpty();
        Log($"Scenario archived: {archivedPath}");
    }

    private void SetFeedback(string message) => _editorFeedback.text = message ?? string.Empty;
    private void ClearFeedback()
    {
        if (_editorFeedback != null) _editorFeedback.text = string.Empty;
    }

    private string GetUniqueFileId(string requestedId)
    {
        string baseId = string.IsNullOrWhiteSpace(requestedId) ? "scenario" : requestedId;
        string candidate = baseId;
        int suffix = 2;
        while (scenarioLoader.ScenarioExists(candidate)) candidate = $"{baseId}_{suffix++}";
        return candidate;
    }

    private static string ToFileId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "scenario";
        return new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray()).Trim('_');
    }

    private static bool IsPlaceholder(string value, string placeholder)
    {
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, placeholder, StringComparison.Ordinal);
    }

    private static void ReleaseTexture(ref Texture2D texture)
    {
        if (texture == null) return;
        DestroyTexture(texture);
        texture = null;
    }

    private static void DestroyTexture(Texture2D texture)
    {
        if (texture == null) return;
        if (Application.isPlaying) Destroy(texture);
        else DestroyImmediate(texture);
    }

    private void Log(string message)
    {
        if (logEvents) Debug.Log($"[ScenariosTabController] {message}");
    }
}
