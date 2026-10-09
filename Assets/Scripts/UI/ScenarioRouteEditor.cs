using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RobotSNAP.Agents;
using RobotSNAP.Core.Scenario;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Owns the multi-route editing state for the scenario wizard.
/// One human route can represent one or several humans sharing the same ordered goals.
/// The map is the primary editing surface: points are selected and dragged directly on it.
///
/// The type is one class split across files by responsibility: this file keeps the state and
/// the construction of the tab, the neighbours keep the route list, the map surface, the form
/// panels, the walkable-grid navigation, the undo history and the scenario serialization.
/// </summary>
public sealed partial class ScenarioRouteEditor
{
    private const string HiddenClass = "route-editor-hidden";
    private const float PointHitRadius = 16f;
    private const float RouteHitRadius = 9f;
    private const float DragThreshold = 2f;
    private const float FieldEditCoalesceSeconds = 1.5f;
    private const float PointLabelWidth = 78f;
    private const int UndoLimit = 60;

    /// <summary>
    /// Cell budget of the walkable grid sampled from the occupancy image. Taken from the runtime planner, so
    /// the editor and the simulation can never drift apart on the sampling of the map.
    /// </summary>
    private const int NavigationResolution = ScenarioNavigation.Resolution;

    /// <summary>
    /// Coarser grid used while a point is dragged: replanning every mouse move has to stay cheap, and the
    /// exact geometry is rebuilt on the fine grid as soon as the point is released.
    /// </summary>
    private const int InteractiveNavigationResolution = 150;

    /// <summary>
    /// Body radius kept clear from walls, taken from the runtime planner, so the drawn path is exactly one the
    /// simulated agent walks.
    /// </summary>
    private const float NavigationAgentRadius = ScenarioNavigation.DefaultAgentRadius;

    private sealed class RouteDraft
    {
        public string Id;
        public bool IsRobot;
        /// <summary>Type of a robot route, an id of <see cref="RobotProfiles"/>; meaningless for a human.</summary>
        public string RobotType = RobotProfiles.DefaultId;
        /// <summary>Heading of the robot on its first point, in degrees; meaningless for a human.</summary>
        public float StartYaw;
        public int Count;
        public float Speed = 1f;
        public HumanEndBehavior EndBehavior = HumanEndBehavior.Stay;
        public string Group;
        public string MovementController;
        public string Formation = "pair";
        public float GroupSpacing = 1.5f;
        public float FormationParameter;
        /// <summary>Seconds the agents of this route take to enter the run; 0 starts them all together.</summary>
        public float SpawnWindow;
        // The values this route lets the run draw instead of fixing. Null means "fixed", which is what a
        // scenario authored before ranges existed carries and what an untouched route keeps writing.
        public ScenarioRange CountRange;
        public ScenarioRange SpeedRange;
        public ScenarioRange StartYawRange;
        public ScenarioRange SpacingRange;
        public ScenarioRange SpawnWindowRange;
        public readonly List<Vector2> Points = new();
        /// <summary>True when the whole route is scattered at spawn instead of starting on its first point.</summary>
        public bool SpawnRandom;
        /// <summary>Area the route is scattered in, meaningful when <see cref="SpawnRandom"/> is set.</summary>
        public Rect SpawnZone;
        /// <summary>Arrival area of each point, parallel to <see cref="Points"/>; a null entry is a fixed point.</summary>
        public readonly List<Rect?> PointZones = new();
        public HumanScenarioConfig Source;
        /// <summary>The robot entry a route was read from, so saving keeps the fields the editor does not show.</summary>
        public RobotScenarioConfig RobotSource;
        public bool RouteModified;
        public bool HasNonSpatialGoal;

        /// <summary>Arrival area of one point, or null when that point is fixed.</summary>
        public Rect? ZoneAt(int index) =>
            index >= 0 && index < PointZones.Count ? PointZones[index] : null;
    }

    private sealed class RouteSnapshot
    {
        public string Id;
        public bool IsRobot;
        public string RobotType;
        public float StartYaw;
        public int Count;
        public float Speed;
        public HumanEndBehavior EndBehavior;
        public string Group;
        public string MovementController;
        public string Formation;
        public float GroupSpacing;
        public float FormationParameter;
        public float SpawnWindow;
        public ScenarioRange CountRange;
        public ScenarioRange SpeedRange;
        public ScenarioRange StartYawRange;
        public ScenarioRange SpacingRange;
        public ScenarioRange SpawnWindowRange;
        public List<Vector2> Points;
        public bool SpawnRandom;
        public Rect SpawnZone;
        public List<Rect?> PointZones;
        public HumanScenarioConfig Source;
        public RobotScenarioConfig RobotSource;
        public bool RouteModified;
        public bool HasNonSpatialGoal;
    }

    private sealed class EditorSnapshot
    {
        public List<RouteSnapshot> Routes;
        public int ActiveRouteIndex;
        public int PendingPointIndex;
    }

    /// <summary>Drawable geometry of one route, cached until its points or the map change.</summary>
    private sealed class PlannedGeometry
    {
        public int Epoch;
        public int Signature;
        public bool Interactive;
        public bool Looping;
        public List<Vector2> Points;
        public int FallbackSegments;
        public int CrossingSegments;

        /// <summary>Index in <see cref="Points"/> where the loop return leg starts, or -1 when there is none.</summary>
        public int LoopReturnStart = -1;
    }

    private struct CoordinateFields
    {
        public FloatField X;
        public FloatField Z;
    }

    /// <summary>The four numeric fields of one area, so the spawn and arrival editors share one code path.</summary>
    private sealed class ZoneFields
    {
        public FloatField CenterX;
        public FloatField CenterZ;
        public FloatField SizeX;
        public FloatField SizeZ;
    }

    private static readonly Color RobotColor = new(0.22f, 0.78f, 0.45f);
    private static readonly Color[] HumanColors =
    {
        new(0.22f, 0.57f, 0.92f),
        new(0.66f, 0.38f, 0.91f),
        new(0.94f, 0.49f, 0.27f),
        new(0.20f, 0.76f, 0.76f),
        new(0.89f, 0.36f, 0.60f)
    };

    private static readonly string[] EndBehaviorChoices =
    {
        HumanEndBehaviorParser.ToDisplayName(HumanEndBehavior.Stay),
        HumanEndBehaviorParser.ToDisplayName(HumanEndBehavior.Disappear),
        HumanEndBehaviorParser.ToDisplayName(HumanEndBehavior.Loop)
    };

    /// <summary>First entry keeps the controller of the HumanConfig asset; the rest pin it for the group.</summary>
    private const string InheritControllerChoice = "Inherit from config";

    /// <summary>
    /// Share of an area that has to be walkable. Below this the area is a drawing mistake rather than a tight
    /// spot: the runtime projects every agent it cannot place, so the crowd lands on one strip.
    /// </summary>
    private const float MinimumAreaCoverage = 0.15f;

    private static readonly string[] MovementControllerChoices =
    {
        InheritControllerChoice,
        HumanMovementControllerParser.ToDisplayName(MovementControllerType.SFM),
        HumanMovementControllerParser.ToDisplayName(MovementControllerType.External),
        HumanMovementControllerParser.ToDisplayName(MovementControllerType.Manual)
    };

    private static readonly string[] FormationChoices =
    {
        "Pair",
        "Row",
        "Column",
        "Wedge",
        "Cluster",
        "Independent"
    };

    private readonly List<RouteDraft> _routes = new();
    private readonly List<EditorSnapshot> _undoStack = new();
    private readonly List<EditorSnapshot> _redoStack = new();
    /// <summary>Route copied with Ctrl+C, waiting to be pasted as a new route.</summary>
    private RouteSnapshot _routeClipboard;
    private readonly List<VisualElement> _pointRows = new();
    private readonly Dictionary<int, CoordinateFields> _pointFields = new();
    /// <summary>The four area fields of the points that are areas, keyed by point index (0 is the start).</summary>
    private readonly Dictionary<int, ZoneFields> _pointZoneFields = new();
    /// <summary>The size badge of the points that are areas, so typing a number updates it live.</summary>
    private readonly Dictionary<int, Label> _pointAreaLabels = new();
    private readonly VisualElement _routeList;
    private readonly Button _addHumanRouteButton;
    private readonly Button _addRobotRouteButton;
    private readonly Button _duplicateHumanRouteButton;
    private readonly Button _removeHumanRouteButton;
    private readonly Button _setRouteStartButton;
    private readonly Button _addRouteObjectiveButton;
    private readonly Button _toggleMapGridButton;
    private readonly VisualElement _humanRouteSettings;
    private readonly VisualElement _robotRouteSettings;
    /// <summary>Type of the active robot route, one per robot, written in the scenario as <c>robots[i].type</c>.</summary>
    private readonly DropdownField _robotTypeDropdown;
    private readonly FloatField _robotSpeedField;
    private readonly FloatField _startYawField;
    private readonly IntegerField _humanCountField;
    private readonly FloatField _humanSpeedField;
    private readonly DropdownField _endBehaviorDropdown;
    private readonly DropdownField _movementControllerDropdown;
    private readonly DropdownField _formationDropdown;
    private readonly FloatField _groupSpacingField;
    private readonly Label _groupSpacingLabel;
    private readonly Label _groupSpacingHelp;
    private readonly FloatField _formationParameterField;
    private readonly Label _formationParameterLabel;
    private readonly Label _formationPreviewLabel;
    private readonly Button _sectionGlobalHeader;
    private readonly Button _sectionGroupHeader;
    private readonly Button _sectionBehaviourHeader;
    private readonly Button _sectionDepartureHeader;
    private readonly FloatField _spawnWindowField;
    /// <summary>Mode and bounds of every value this route may let the run draw, one control per value.</summary>
    private readonly ScenarioRangeControl _robotSpeedRange;
    private readonly ScenarioRangeControl _startYawRange;
    private readonly ScenarioRangeControl _humanCountRange;
    private readonly ScenarioRangeControl _humanSpeedRange;
    private readonly ScenarioRangeControl _groupSpacingRange;
    private readonly ScenarioRangeControl _spawnWindowRange;
    private readonly VisualElement _sectionGlobalContent;
    private readonly VisualElement _sectionGroupContent;
    private readonly VisualElement _sectionBehaviourContent;
    private readonly VisualElement _sectionDepartureContent;
    private readonly VisualElement _routePointsContainer;
    private readonly VisualElement _canvas;
    private readonly Image _mapImage;
    private readonly Label _mapPlaceholder;
    private readonly Label _instructionLabel;
    private readonly Label _cursorCoordinatesLabel;
    private readonly Label _activeRouteLabel;
    private readonly Label _gridScaleLabel;
    private readonly Label _mapPathStatusLabel;
    private readonly OccupancyMapRouteOverlay _overlay;
    private readonly VisualElement _pointLabelLayer;
    private readonly List<OccupancyMapRouteOverlay.FormationPreview> _formationPreviews = new();
    private readonly List<OccupancyMapRouteOverlay.ZoneVisual> _zoneVisuals = new();
    private readonly Dictionary<int, PlannedGeometry> _plannedGeometry = new();

    private Texture2D _mapTexture;
    private Bounds _mapBounds;
    private OccupancyGrid _navigationGrid;
    private OccupancyGrid _interactiveGrid;
    private OccupancyGrid _occupancyGrid;
    private int _navigationGridEpoch;
    private int _activeRouteIndex;
    private int _pendingPointIndex;
    private bool _updatingFields;

    // Disclosure state of the step-2 sections: the essentials stay on screen, the rest is one click away.
    private bool _globalExpanded = true;
    private bool _groupExpanded = true;
    private bool _behaviourExpanded;
    private bool _departureExpanded;

    /// <summary>Index of the point whose area the next two map clicks are drawing, or -1 when none is.</summary>
    private int _zonePickIndex = -1;
    private bool _zoneFirstCornerPlaced;
    private Vector2 _zoneFirstCorner;

    private bool _zoneDragActive;
    /// <summary>Index of the point whose area a drag is moving; 0 is the start of the route.</summary>
    private int _zoneDragIndex;
    /// <summary>World point the press landed on: the drag measures its delta from there instead of from the
    /// previous frame, so a snapped centre keeps following the pointer instead of sticking to its grid step.</summary>
    private Vector2 _zoneDragGrab;
    /// <summary>Area of the dragged point when the press happened, so the applied delta stays absolute.</summary>
    private Rect _zoneDragStartArea;
    private Vector2 _zoneCursorWorld;
    private bool _showGrid = true;

    private bool _dragging;
    private bool _dragActive;
    private int _dragRouteIndex = -1;
    private int _dragPointIndex = -1;
    private Vector2 _dragOrigin;
    private Vector2 _dragStartWorld;
    private EditorSnapshot _dragUndoSnapshot;
    private bool _skipNextDragUndo;

    private string _lastEditKey;
    private float _lastEditTime = float.NegativeInfinity;

    public ScenarioRouteEditor(VisualElement root)
    {
        _routeList = root.Q<VisualElement>("RouteSelectorList");
        _addHumanRouteButton = root.Q<Button>("AddHumanRouteButton");
        _addRobotRouteButton = root.Q<Button>("AddRobotRouteButton");
        _duplicateHumanRouteButton = root.Q<Button>("DuplicateHumanRouteButton");
        _removeHumanRouteButton = root.Q<Button>("RemoveHumanRouteButton");
        _setRouteStartButton = root.Q<Button>("SetRouteStartButton");
        _addRouteObjectiveButton = root.Q<Button>("AddRouteObjectiveButton");
        _toggleMapGridButton = root.Q<Button>("ToggleMapGridButton");
        _humanRouteSettings = root.Q<VisualElement>("HumanRouteSettings");
        _robotRouteSettings = root.Q<VisualElement>("RobotRouteSettings");
        _robotTypeDropdown = root.Q<DropdownField>("RobotTypeDropdown");
        _robotSpeedField = root.Q<FloatField>("RobotSpeedField");
        _startYawField = root.Q<FloatField>("StartYawField");
        _humanCountField = root.Q<IntegerField>("HumanCountField");
        _humanSpeedField = root.Q<FloatField>("HumanSpeedField");
        _endBehaviorDropdown = root.Q<DropdownField>("EndBehaviorDropdown");
        _movementControllerDropdown = root.Q<DropdownField>("MovementControllerDropdown");
        _formationDropdown = root.Q<DropdownField>("FormationDropdown");
        _groupSpacingField = root.Q<FloatField>("GroupSpacingField");
        _groupSpacingLabel = root.Q<Label>("GroupSpacingLabel");
        _groupSpacingHelp = root.Q<Label>("GroupSpacingHelp");
        _formationParameterField = root.Q<FloatField>("FormationParameterField");
        _formationParameterLabel = root.Q<Label>("FormationParameterLabel");
        _formationPreviewLabel = root.Q<Label>("FormationPreviewLabel");
        _sectionGlobalHeader = root.Q<Button>("SectionGlobalHeader");
        _sectionGroupHeader = root.Q<Button>("SectionGroupHeader");
        _sectionBehaviourHeader = root.Q<Button>("SectionBehaviourHeader");
        _sectionDepartureHeader = root.Q<Button>("SectionDepartureHeader");
        _sectionGlobalContent = root.Q<VisualElement>("SectionGlobalContent");
        _sectionGroupContent = root.Q<VisualElement>("SectionGroupContent");
        _sectionBehaviourContent = root.Q<VisualElement>("SectionBehaviourContent");
        _sectionDepartureContent = root.Q<VisualElement>("SectionDepartureContent");
        _spawnWindowField = root.Q<FloatField>("SpawnWindowField");
        _routePointsContainer = root.Q<VisualElement>("RoutePointsContainer");
        _canvas = root.Q<VisualElement>("AgentsEnvironmentCanvas");
        _mapImage = root.Q<Image>("AgentsEnvironmentImage");
        _mapPlaceholder = root.Q<Label>("AgentsMapPlaceholder");
        _instructionLabel = root.Q<Label>("PlacementInstructionLabel");
        _cursorCoordinatesLabel = root.Q<Label>("MapCursorCoordinatesLabel");
        _activeRouteLabel = root.Q<Label>("ActiveRouteLabel");
        _gridScaleLabel = root.Q<Label>("GridScaleLabel");
        _mapPathStatusLabel = root.Q<Label>("MapPathStatusLabel");
        VisualElement overlayHost = root.Q<VisualElement>("RouteOverlayHost");

        if (new VisualElement[]
            {
                _routeList, _addHumanRouteButton, _duplicateHumanRouteButton, _removeHumanRouteButton,
                _setRouteStartButton, _addRouteObjectiveButton, _toggleMapGridButton,
                _humanRouteSettings, _humanCountField, _humanSpeedField,
                _robotRouteSettings,
                _endBehaviorDropdown, _formationDropdown, _groupSpacingField,
                _groupSpacingLabel, _groupSpacingHelp,
                _movementControllerDropdown,
                _formationParameterField, _formationParameterLabel,
                _formationPreviewLabel,
                _spawnWindowField, _sectionDepartureHeader, _sectionDepartureContent,
                _sectionGlobalHeader, _sectionGroupHeader, _sectionBehaviourHeader,
                _sectionGlobalContent, _sectionGroupContent, _sectionBehaviourContent,
                _routePointsContainer, _canvas, _mapImage, _mapPlaceholder, _instructionLabel,
                _cursorCoordinatesLabel, _activeRouteLabel, _gridScaleLabel, _mapPathStatusLabel,
                overlayHost
            }.Any(element => element == null))
        {
            throw new InvalidOperationException("The scenario route editor UI is incomplete.");
        }

        // One range control per value the run may draw. They are built here rather than in the UXML so the tab
        // keeps its shape, and each sits right under the value it replaces, which is hidden while a range is
        // being edited - the author then sees exactly one setting per value, never both at once.
        VisualElement robotSettings = _robotSpeedField.parent.parent;
        VisualElement humanSettings = _humanCountField.parent.parent.parent;
        VisualElement groupSettings = _groupSpacingField.parent.parent;
        VisualElement departureSettings = _spawnWindowField.parent.parent;
        VisualElement humanCountRow = _humanCountField.parent.parent;

        _robotSpeedRange = new ScenarioRangeControl(
            robotSettings, _robotSpeedField.parent, _robotSpeedField.parent,
            "Robot speed draw", "Drawn each run between these two speeds, in m/s.", 0.01f, 10f, 1f, 2f);
        _startYawRange = new ScenarioRangeControl(
            robotSettings, _startYawField.parent, _startYawField.parent,
            "Robot start orientation draw", "Drawn each run between these two headings, in degrees.", -360f, 360f, -20f, 20f);
        _humanCountRange = new ScenarioRangeControl(
            humanSettings, humanCountRow, _humanCountField.parent,
            "Agent count draw", "Drawn each run between these two counts; both bounds are included.", 0f, 200f, 1f, 5f);
        _humanSpeedRange = new ScenarioRangeControl(
            humanSettings, _humanCountRange.Row, _humanSpeedField.parent,
            "Human speed draw", "Drawn each run between these two walking speeds, in m/s.", 0.01f, 10f, 0.8f, 1.6f);
        _groupSpacingRange = new ScenarioRangeControl(
            groupSettings, _groupSpacingField.parent, _groupSpacingField.parent,
            "Spacing draw",
            "Drawn each run between these two spacings, in metres; the minimum the selected formation can hold still applies.",
            0.4f, 3f, 0.6f, 1.5f);
        _spawnWindowRange = new ScenarioRangeControl(
            departureSettings, _spawnWindowField.parent, _spawnWindowField.parent,
            "Departure window draw", "Drawn each run between these two windows, in seconds.", 0f, 60f, 0f, 5f);

        _overlay = new OccupancyMapRouteOverlay();
        overlayHost.Add(_overlay);

        _pointLabelLayer = new VisualElement { pickingMode = PickingMode.Ignore };
        _pointLabelLayer.AddToClassList("route-point-layer");
        overlayHost.Add(_pointLabelLayer);

        _mapImage.scaleMode = ScaleMode.ScaleToFit;
        _canvas.focusable = true;
        _canvas.AddManipulator(new OccupancyMapPlacementManipulator(
            OnMapPointerDown,
            OnMapPointerMove,
            OnMapPointerLeave,
            OnMapPointerUp));
        _canvas.RegisterCallback<KeyDownEvent>(OnCanvasKeyDown);
        _canvas.RegisterCallback<GeometryChangedEvent>(_ => RefreshOverlay());

        _addHumanRouteButton.clicked += AddHumanRoute;
        if (_addRobotRouteButton != null)
            _addRobotRouteButton.clicked += AddRobotRoute;
        _duplicateHumanRouteButton.clicked += DuplicateActiveRoute;
        _removeHumanRouteButton.clicked += RemoveActiveRoute;
        _setRouteStartButton.clicked += SelectStartForPlacement;
        _addRouteObjectiveButton.clicked += AddObjective;
        _toggleMapGridButton.clicked += ToggleGrid;

        // The type of a robot is the one setting that changes how it behaves rather than where it goes, so it
        // belongs to the route of that robot and follows the selection like every other field of the panel.
        _robotTypeDropdown.choices = RobotProfiles.DisplayNames().ToList();
        _robotTypeDropdown.RegisterValueChangedCallback(evt =>
        {
            if (_updatingFields) return;
            RouteDraft robot = ActiveRobotRoute;
            if (robot == null || string.Equals(evt.newValue, DisplayNameOf(robot.RobotType), StringComparison.Ordinal))
                return;

            PushUndo($"robot-type:{_activeRouteIndex}");
            robot.RobotType = RobotProfiles.Find(evt.newValue).Id;
            // A type carries its own speed; taking it saves the author from typing the figure of the vendor
            // twice, and the field stays editable for a scenario that wants to drive it slower.
            robot.Speed = RobotProfiles.Find(evt.newValue).MaxLinearSpeed;
            robot.RouteModified = true;
            RefreshRouteList();
            RefreshActiveRoute();
        });
        _robotSpeedField.RegisterValueChangedCallback(evt =>
        {
            if (_updatingFields) return;
            RouteDraft robot = ActiveRobotRoute;
            if (robot == null) return;

            float value = Mathf.Max(0.01f, evt.newValue);
            if (!Mathf.Approximately(value, evt.newValue)) _robotSpeedField.SetValueWithoutNotify(value);
            PushUndo($"robot-speed:{_activeRouteIndex}");
            robot.Speed = value;
            robot.RouteModified = true;
        });
        _startYawField.RegisterValueChangedCallback(evt =>
        {
            if (_updatingFields) return;
            RouteDraft robot = ActiveRobotRoute;
            if (robot == null || robot.Points.Count == 0) return;

            PushUndo($"robot-yaw:{_activeRouteIndex}");
            // The orientation of the first point is what the robot faces at spawn, so it is stored on that
            // point and written back with the rest of the route.
            robot.StartYaw = evt.newValue;
        });

        _humanCountField.RegisterValueChangedCallback(evt =>
        {
            int value = Mathf.Max(0, evt.newValue);
            if (value != evt.newValue) _humanCountField.SetValueWithoutNotify(value);
            if (_updatingFields) return;
            PushUndo($"human-count:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.Count = value);
            RefreshRouteList();
        });
        _humanSpeedField.RegisterValueChangedCallback(evt =>
        {
            float value = Mathf.Max(0.01f, evt.newValue);
            if (!Mathf.Approximately(value, evt.newValue)) _humanSpeedField.SetValueWithoutNotify(value);
            if (_updatingFields) return;
            PushUndo($"human-speed:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.Speed = value);
        });
        _endBehaviorDropdown.RegisterValueChangedCallback(evt =>
        {
            if (_updatingFields) return;
            PushUndo($"human-end-behavior:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.EndBehavior = ParseEndBehaviorChoice(evt.newValue));
        });
        _movementControllerDropdown.choices = new List<string>(MovementControllerChoices);
        _movementControllerDropdown.RegisterValueChangedCallback(evt =>
        {
            if (_updatingFields) return;
            PushUndo($"human-controller:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.MovementController = ParseMovementControllerChoice(evt.newValue));
            RefreshFormationPreview();
        });
        _formationDropdown.RegisterValueChangedCallback(evt =>
        {
            if (_updatingFields) return;
            PushUndo($"human-formation:{_activeRouteIndex}");
            UpdateHumanDraft(draft =>
            {
                draft.Formation = FormationFromDisplay(evt.newValue);
                // A single file needs more room than a loose cluster: keep the spacing legal.
                draft.GroupSpacing = Mathf.Clamp(
                    draft.GroupSpacing,
                    GroupFormation.MinSpacing(draft.Formation),
                    3f);
            });
            RefreshActiveRoute();
            RefreshFormationPreview();
        });
        _groupSpacingField.RegisterValueChangedCallback(evt =>
        {
            float floor = ActiveRoute != null
                ? GroupFormation.MinSpacing(ActiveRoute.Formation)
                : 0.4f;
            float value = Mathf.Clamp(evt.newValue, floor, 3f);
            if (!Mathf.Approximately(value, evt.newValue)) _groupSpacingField.SetValueWithoutNotify(value);
            if (_updatingFields) return;
            PushUndo($"human-group-spacing:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.GroupSpacing = value);
            RefreshFormationPreview();
        });
        _spawnWindowField.RegisterValueChangedCallback(evt =>
        {
            // A negative window is not a window: the field floors at zero and says so, rather than carrying a
            // value the runtime would have to interpret.
            float value = Mathf.Max(0f, evt.newValue);
            if (!Mathf.Approximately(value, evt.newValue)) _spawnWindowField.SetValueWithoutNotify(value);
            if (_updatingFields) return;
            PushUndo($"human-spawn-window:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.SpawnWindow = value);
        });
        // The range controls only carry the mode and the two bounds. They say what the run draws; the fixed
        // field above keeps saying what it uses when no range is set, so nothing here ever overwrites it.
        _robotSpeedRange.Changed += () =>
        {
            RouteDraft robot = ActiveRobotRoute;
            if (_updatingFields || robot == null) return;
            PushUndo($"robot-speed-range:{_activeRouteIndex}");
            robot.SpeedRange = _robotSpeedRange.ToRange();
            robot.RouteModified = true;
            RefreshRouteList();
        };
        _startYawRange.Changed += () =>
        {
            RouteDraft robot = ActiveRobotRoute;
            if (_updatingFields || robot == null) return;
            PushUndo($"robot-yaw-range:{_activeRouteIndex}");
            robot.StartYawRange = _startYawRange.ToRange();
            robot.RouteModified = true;
        };
        _humanCountRange.Changed += () =>
        {
            if (_updatingFields || ActiveRoute == null || ActiveRoute.IsRobot) return;
            PushUndo($"human-count-range:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.CountRange = _humanCountRange.ToRange());
            RefreshRouteList();
        };
        _humanSpeedRange.Changed += () =>
        {
            if (_updatingFields || ActiveRoute == null || ActiveRoute.IsRobot) return;
            PushUndo($"human-speed-range:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.SpeedRange = _humanSpeedRange.ToRange());
        };
        _groupSpacingRange.Changed += () =>
        {
            if (_updatingFields || ActiveRoute == null || ActiveRoute.IsRobot) return;
            PushUndo($"human-spacing-range:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.SpacingRange = _groupSpacingRange.ToRange());
            RefreshFormationPreview();
        };
        _spawnWindowRange.Changed += () =>
        {
            if (_updatingFields || ActiveRoute == null || ActiveRoute.IsRobot) return;
            PushUndo($"human-window-range:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.SpawnWindowRange = _spawnWindowRange.ToRange());
        };
        _formationParameterField.RegisterValueChangedCallback(evt =>
        {
            if (_updatingFields) return;
            PushUndo($"human-formation-parameter:{_activeRouteIndex}");
            UpdateHumanDraft(draft => draft.FormationParameter = Mathf.Max(0f, evt.newValue));
            RefreshFormationPreview();
        });

        // Step 2 shows the essentials and keeps the rest one click away.
        _sectionGlobalHeader.userData = _sectionGlobalHeader.text;
        _sectionGroupHeader.userData = _sectionGroupHeader.text;
        _sectionBehaviourHeader.userData = _sectionBehaviourHeader.text;
        _sectionDepartureHeader.userData = _sectionDepartureHeader.text;
        _sectionGlobalHeader.clicked += () => { _globalExpanded = !_globalExpanded; ApplySectionState(); };
        _sectionGroupHeader.clicked += () => { _groupExpanded = !_groupExpanded; ApplySectionState(); };
        _sectionBehaviourHeader.clicked += () => { _behaviourExpanded = !_behaviourExpanded; ApplySectionState(); };
        _sectionDepartureHeader.clicked += () => { _departureExpanded = !_departureExpanded; ApplySectionState(); };
        ApplySectionState();
    }
}
