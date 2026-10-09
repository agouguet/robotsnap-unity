using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RobotSNAP.Agents;
using RobotSNAP.Core.Scenario;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The route list of the wizard: the drafts it holds, and the operations that create,
/// load, save, duplicate, select and summarize them. One route is one row of the editor.</summary>
public sealed partial class ScenarioRouteEditor
{

    public int TotalHumanCount => _routes.Where(route => !route.IsRobot).Sum(route => Mathf.Max(0, route.Count));
    public int TotalObjectiveCount => _routes
        .Where(route => route.IsRobot || CarriesAgents(route))
        .Sum(route => Mathf.Max(0, route.Points.Count - 1));
    public int HumanRouteCount => _routes.Count(route => !route.IsRobot && CarriesAgents(route));

    /// <summary>
    /// True when a route puts agents in the run: a fixed count above zero, or a count the run draws. A route
    /// whose fixed count is zero but whose range is 1 to 5 does carry agents, and dropping it because of the
    /// fixed zero would silently discard the randomization the author asked for.
    /// </summary>
    private static bool CarriesAgents(RouteDraft route) => route.Count > 0 || route.CountRange != null;

    public string RobotRouteSummary
    {
        get
        {
            List<RouteDraft> robots = _routes.Where(route => route.IsRobot).ToList();
            if (robots.Count == 0)
                return "No robot route";

            // Several robots are named by their types, because that is the question the summary answers:
            // what drives this scenario, not how many lists entries it has.
            string types = string.Join(", ", robots
                .Select(route => RobotProfiles.Find(route.RobotType).DisplayName)
                .Distinct());

            return robots.Count == 1
                ? FormatRouteSummary(robots[0])
                : $"{robots.Count} robots · {types}";
        }
    }

    public string HumanRouteSummary
    {
        get
        {
            List<RouteDraft> humans = _routes.Where(route => !route.IsRobot && CarriesAgents(route)).ToList();
            return humans.Count == 0
                ? "No human route"
                : $"{humans.Count} route(s), {humans.Sum(route => route.Points.Count - 1)} objective(s)";
        }
    }

    /// <summary>
    /// Readable type of the robot the scenario drives first: the one a client reaches without naming anybody,
    /// and the label the browser shows for the scenario as a whole.
    /// </summary>
    public string PrimaryRobotTypeName
    {
        get
        {
            RouteDraft first = _routes.FirstOrDefault(route => route.IsRobot);
            return first == null ? RobotProfiles.Default.DisplayName : RobotProfiles.Find(first.RobotType).DisplayName;
        }
    }

    /// <summary>
    /// Every route as a drawable visual, for read-only previews such as the validation step recap.
    /// All routes are marked active so the recap draws them with the full opacity used while editing.
    /// </summary>
    public List<OccupancyMapRouteOverlay.RouteVisual> BuildRoutePreviews()
        => BuildRouteVisuals(markAllActive: true);

    /// <summary>
    /// A blank scenario: the one robot every scenario drives, and no crowd.
    ///
    /// The crowd used to be laid down here and put back whenever the last route was removed, which made a
    /// scenario of pure robot navigation impossible to write: the author had to add a route they did not
    /// want, and could not delete it. A scenario with no human route is a scenario like any other - the
    /// runtime spawns nobody when the list is empty - so the editor now leaves the crowd to whoever asks
    /// for one, with the "Human route" button.
    /// </summary>
    public void Reset()
    {
        _routes.Clear();
        _plannedGeometry.Clear();
        _routes.Add(CreateRobotDraft(RobotProfiles.DefaultId, 0));
        _activeRouteIndex = 0;
        _pendingPointIndex = 0;
        ResetHistory();
        RebuildRouteList();
        RefreshActiveRoute();
    }

    /// <summary>
    /// A robot route to start from, laid down as a short straight leg so the map already shows something to
    /// drag when the step opens.
    /// </summary>
    private static RouteDraft CreateRobotDraft(string typeId, int index)
    {
        RobotProfile profile = RobotProfiles.Find(typeId);
        var draft = new RouteDraft
        {
            Id = ScenarioData.DefaultRobotId(index),
            IsRobot = true,
            RobotType = profile.Id,
            Speed = profile.MaxLinearSpeed,
            RouteModified = true
        };

        Vector2 start = new Vector2(0f, index * 1.5f);
        AppendPoint(draft, start);
        AppendPoint(draft, start + Vector2.right * 2f);
        return draft;
    }

    public void Load(ScenarioData scenario)
    {
        Reset();
        if (scenario == null)
            return;

        List<RobotScenarioConfig> robots = scenario.NormalizedRobots();
        _routes.Clear();
        for (int index = 0; index < robots.Count; index++)
            _routes.Add(CreateRobotDraftFrom(scenario, robots[index], index));
        if (_routes.Count == 0)
            _routes.Add(CreateRobotDraft(RobotProfiles.DefaultId, 0));

        if (scenario.Humans != null)
        {
            foreach (HumanScenarioConfig human in scenario.Humans.Where(human => human != null))
            {
                RouteDraft draft = CreateHumanDraft(scenario, human, _routes.Count);
                draft.Id = MakeUniqueRouteId(draft.Id);
                _routes.Add(draft);
            }
        }

        _activeRouteIndex = 0;
        _pendingPointIndex = 0;
        ResetHistory();
        RebuildRouteList();
        RefreshActiveRoute();
    }

    /// <summary>
    /// One robot of a stored scenario, as the editor draws it: its declared type, its start, its waypoints and
    /// its goal. The single <c>robot</c> section of an older file arrives here as the one-entry list of the
    /// same call, so a scenario written before several robots existed opens unchanged.
    /// </summary>
    private static RouteDraft CreateRobotDraftFrom(ScenarioData scenario, RobotScenarioConfig config, int index)
    {
        RobotProfile profile = RobotProfiles.Find(config.Type);
        var draft = new RouteDraft
        {
            Id = string.IsNullOrWhiteSpace(config.Id) ? ScenarioData.DefaultRobotId(index) : config.Id.Trim(),
            IsRobot = true,
            RobotType = profile.Id,
            Speed = config.Speed > 0f ? config.Speed : profile.MaxLinearSpeed,
            SpeedRange = config.SpeedRange?.Clone(),
            StartYawRange = config.StartYawRange?.Clone(),
            RobotSource = config,
            RouteModified = false
        };

        if (TryResolveReference(scenario, config.StartRef, out Vector2 start))
        {
            AppendPoint(draft, start);
            Rect? startArea = ReadZone(PointAt(scenario, config.StartRef));
            draft.SpawnRandom = startArea.HasValue;
            if (startArea.HasValue)
                draft.SpawnZone = startArea.Value;
        }
        if (config.WaypointRefs != null)
        {
            foreach (string waypointRef in config.WaypointRefs)
            {
                if (!TryResolveReference(scenario, waypointRef, out Vector2 waypoint))
                    continue;

                AppendPoint(draft, waypoint);
                NormalizeZones(draft);
                draft.PointZones[draft.Points.Count - 1] = ReadZone(PointAt(scenario, waypointRef));
            }
        }
        if (TryResolveReference(scenario, config.GoalRef, out Vector2 goal))
        {
            AppendPoint(draft, goal);
            NormalizeZones(draft);
            draft.PointZones[draft.Points.Count - 1] = ReadZone(PointAt(scenario, config.GoalRef));
        }

        draft.StartYaw = TryReadYaw(scenario, config.StartRef);
        EnsureMinimumPoints(draft);
        return draft;
    }

    /// <summary>Heading stored with a point, or zero when the scenario names none.</summary>
    private static float TryReadYaw(ScenarioData scenario, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || scenario?.Points == null ||
            !scenario.Points.TryGetValue(reference, out RefPoint point) || point == null)
            return 0f;

        return point.Yaw ?? 0f;
    }

    /// <summary>Adds a crowd route to the scenario and selects it.</summary>
    public void AddHumanRoute()
    {
        PushUndo();
        RouteDraft draft = CreateHumanDraft(NextHumanRouteNumber());
        _routes.Add(draft);
        _activeRouteIndex = _routes.Count - 1;
        _pendingPointIndex = 0;
        RebuildRouteList();
        RefreshActiveRoute();
    }

    /// <summary>
    /// Adds a robot. A second robot is a second route like any other: it appears in the same list, carries its
    /// own type, its own start and its own objectives, and is saved as another entry of the scenario's
    /// <c>robots</c> list. The new one is laid down a little to the side of the last, because two robots on
    /// the same point would spend the run pushing each other apart.
    /// </summary>
    public void AddRobotRoute()
    {
        PushUndo();

        var draft = new RouteDraft
        {
            Id = NextRobotRouteId(),
            IsRobot = true,
            RobotType = RobotProfiles.DefaultId,
            RouteModified = true
        };

        Vector2 anchor = Vector2.zero;
        RouteDraft last = _routes.LastOrDefault(route => route.IsRobot && route.Points.Count > 0);
        if (last != null)
            anchor = last.Points[0];

        AppendPoint(draft, anchor + new Vector2(0f, 1.5f));
        AppendPoint(draft, anchor + new Vector2(0f, 1.5f) + Vector2.right * 2f);
        _routes.Add(draft);
        _activeRouteIndex = _routes.Count - 1;
        _pendingPointIndex = 0;
        RebuildRouteList();
        RefreshActiveRoute();
    }

    /// <summary>
    /// Adds a copy of the active route. A crowd is built by tuning one route and repeating it — the same start
    /// area crossed from another side, a second flow through the same corridor — so the copy has to carry the
    /// whole route: points, areas, formation, end behaviour and speed.
    /// </summary>
    public void DuplicateActiveRoute()
    {
        RouteDraft active = ActiveRoute;
        if (active == null)
            return;

        InsertRouteCopy(CaptureRoute(active));
    }

    /// <summary>Keeps the active route on the editor clipboard; Ctrl+V turns it into a new route.</summary>
    private void CopyActiveRoute()
    {
        RouteDraft active = ActiveRoute;
        if (active == null)
            return;

        _routeClipboard = CaptureRoute(active);
        _instructionLabel.text = $"{active.Id} copied. Ctrl+V adds a copy.";
    }

    private void PasteRoute()
    {
        if (_routeClipboard == null)
        {
            _instructionLabel.text = "Nothing to paste: copy a route with Ctrl+C first.";
            return;
        }

        InsertRouteCopy(_routeClipboard);
    }

    private void InsertRouteCopy(RouteSnapshot source)
    {
        if (source == null)
            return;

        PushUndo();
        RouteDraft copy = CreateDraftFromSnapshot(source);
        copy.Id = NextRouteCopyId(source.Id);
        // The copy starts as a new route, on its own: saving it as a fresh entry is the point of duplicating,
        // and a group inherited from the original would silently make both routes walk as one formation.
        copy.Group = null;
        copy.RouteModified = true;
        copy.Source = null;
        _routes.Add(copy);
        _activeRouteIndex = _routes.Count - 1;
        _pendingPointIndex = 0;
        RebuildRouteList();
        RefreshActiveRoute();
    }

    /// <summary>
    /// Name of the copy: "human_route_1" gives "human_route_1_copy", then "_copy2". A copy of a copy keeps one
    /// suffix instead of stacking them, and the new id is checked against the routes already in the editor.
    /// </summary>
    private string NextRouteCopyId(string sourceId)
    {
        string root = Regex.Replace(string.IsNullOrWhiteSpace(sourceId) ? "human_route" : sourceId.Trim(),
            "_copy\\d*$", string.Empty, RegexOptions.IgnoreCase);
        string candidate = root + "_copy";
        int suffix = 2;
        while (_routes.Any(route => string.Equals(route.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{root}_copy{suffix}";
            suffix++;
        }

        return candidate;
    }

    /// <summary>
    /// Removes the route the editor is on. The last robot stays - a scenario drives one, and an empty
    /// scenario has nothing to open - and every crowd route can go, down to none at all.
    /// </summary>
    public void RemoveActiveRoute()
    {
        RouteDraft active = ActiveRoute;
        if (active == null)
            return;

        // The scenario always drives at least one robot, so the last one stays and only its route is reset.
        if (active.IsRobot && RobotRouteCount <= 1)
        {
            _instructionLabel.text = "A scenario drives at least one robot: clear its points instead of removing it.";
            return;
        }

        PushUndo();
        _routes.RemoveAt(_activeRouteIndex);
        _activeRouteIndex = Mathf.Clamp(_activeRouteIndex - 1, 0, _routes.Count - 1);
        _pendingPointIndex = 0;
        RebuildRouteList();
        RefreshActiveRoute();
    }

    private void AddObjective()
    {
        RouteDraft active = ActiveRoute;
        if (active == null)
            return;

        PushUndo();
        Vector2 anchor = active.Points.Count > 0 ? active.Points[^1] : Vector2.zero;
        AppendPoint(active, anchor + Vector2.right);
        active.RouteModified = true;
        active.HasNonSpatialGoal = false;
        _pendingPointIndex = active.Points.Count - 1;
        RebuildPointRows();
        SetPlacementInstruction();
        RefreshOverlay();
    }

    private void SelectStartForPlacement()
    {
        _pendingPointIndex = 0;
        UpdatePointSelectionHighlight();
        SetPlacementInstruction();
        RefreshOverlay();
    }

    private void ToggleGrid()
    {
        _showGrid = !_showGrid;
        _toggleMapGridButton.EnableInClassList("active", _showGrid);
        _overlay.ShowGrid = _showGrid;
        UpdateGridScaleLabel();
    }

    private void SelectRoute(int index)
    {
        if (index < 0 || index >= _routes.Count || index == _activeRouteIndex)
            return;
        _activeRouteIndex = index;
        _pendingPointIndex = 0;
        RefreshActiveRoute();
    }

    private void RefreshActiveRoute()
    {
        RouteDraft active = ActiveRoute;
        if (active == null)
            return;

        _activeRouteLabel.text = active.Id;
        // A scenario can hold several robots but never none: the last robot route is the one the wizard
        // guarantees, so it cannot be removed, exactly like the last human route is restored instead.
        _removeHumanRouteButton.SetEnabled(!active.IsRobot || RobotRouteCount > 1);
        _duplicateHumanRouteButton.SetEnabled(true);
        _humanRouteSettings.EnableInClassList(HiddenClass, active.IsRobot);
        _robotRouteSettings.EnableInClassList(HiddenClass, !active.IsRobot);
        _pendingPointIndex = Mathf.Clamp(_pendingPointIndex, 0, Mathf.Max(0, active.Points.Count - 1));

        _updatingFields = true;
        if (active.IsRobot)
        {
            _robotTypeDropdown.SetValueWithoutNotify(DisplayNameOf(active.RobotType));
            _robotSpeedField.SetValueWithoutNotify(active.Speed);
            _startYawField.SetValueWithoutNotify(active.StartYaw);
            _robotSpeedRange.SetFrom(
                active.SpeedRange,
                Mathf.Max(0.01f, active.Speed * 0.8f),
                Mathf.Max(0.02f, active.Speed * 1.2f));
            _startYawRange.SetFrom(active.StartYawRange, active.StartYaw - 20f, active.StartYaw + 20f);
        }
        else
        {
            _humanCountField.SetValueWithoutNotify(active.Count);
            _humanSpeedField.SetValueWithoutNotify(active.Speed);
            _humanCountRange.SetFrom(active.CountRange, Mathf.Max(0, active.Count), Mathf.Max(1, active.Count) + 2);
            _humanSpeedRange.SetFrom(
                active.SpeedRange,
                Mathf.Max(0.01f, active.Speed * 0.8f),
                Mathf.Max(0.02f, active.Speed * 1.2f));
            _groupSpacingRange.SetFrom(
                active.SpacingRange,
                Mathf.Max(0.4f, active.GroupSpacing * 0.8f),
                Mathf.Max(0.45f, active.GroupSpacing * 1.2f));
            _spawnWindowRange.SetFrom(active.SpawnWindowRange, 0f, Mathf.Max(1f, active.SpawnWindow * 2f));
            SetEndBehaviorChoices();
            _endBehaviorDropdown.SetValueWithoutNotify(HumanEndBehaviorParser.ToDisplayName(active.EndBehavior));
            _movementControllerDropdown.SetValueWithoutNotify(MovementControllerToDisplay(active.MovementController));
            SetFormationChoices();
            _formationDropdown.SetValueWithoutNotify(FormationToDisplay(active.Formation));
            _groupSpacingField.SetValueWithoutNotify(active.GroupSpacing);
            _spawnWindowField.SetValueWithoutNotify(active.SpawnWindow);
            UpdateFormationParameterField(active);
        }
        _updatingFields = false;

        RefreshRouteListSelection();
        RebuildPointRows();
        SetPlacementInstruction();
        RefreshOverlay();
    }

    private void RebuildRouteList()
    {
        _routeList.Clear();
        for (int index = 0; index < _routes.Count; index++)
        {
            int capturedIndex = index;
            RouteDraft route = _routes[index];

            var row = new VisualElement();
            row.AddToClassList("route-selector-row");
            row.EnableInClassList("selected", index == _activeRouteIndex);

            var swatch = new VisualElement();
            swatch.AddToClassList("route-selector-swatch");
            swatch.style.backgroundColor = RouteColor(index);

            var name = new Label(DescribeRoute(route));
            name.AddToClassList("route-selector-name");

            row.Add(swatch);
            row.Add(name);
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                SelectRoute(capturedIndex);
                evt.StopPropagation();
            });
            _routeList.Add(row);
        }
    }

    /// <summary>Recomputes the sentence describing the active formation, then the map that draws its slots.</summary>
    private void RefreshFormationPreview()
    {
        UpdateFormationPreviewLabel();
        // The map draws the formation slots, so a formation or spacing change must repaint it
        // immediately instead of waiting for the next point move.
        RefreshOverlay();
    }

    private void RefreshRouteList()
    {
        for (int index = 0; index < _routeList.childCount && index < _routes.Count; index++)
        {
            if (_routeList[index].childCount < 2)
                continue;
            var name = _routeList[index][1] as Label;
            if (name != null)
                name.text = DescribeRoute(_routes[index]);
        }
    }

    private void RefreshRouteListSelection()
    {
        for (int index = 0; index < _routeList.childCount && index < _routes.Count; index++)
            _routeList[index].EnableInClassList("selected", index == _activeRouteIndex);
    }

    /// <summary>Routes that share a group id walk as one formation, which the spawn preview has to respect.</summary>
    private List<RouteDraft> RoutesOfGroup(string groupId) => _routes
        .Where(route => !route.IsRobot &&
                        string.Equals(route.Group?.Trim(), groupId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    private static string DescribeRoute(RouteDraft route)
    {
        string drawn = RandomizationLabel(route);
        if (route.IsRobot)
            return drawn.Length == 0
                ? $"{route.Id} · {RobotProfiles.Find(route.RobotType).DisplayName}"
                : $"{route.Id} · {RobotProfiles.Find(route.RobotType).DisplayName} · {drawn}";
        int agents = Mathf.Max(0, route.Count);
        int objectives = Mathf.Max(0, route.Points.Count - 1);
        int areas = 0;
        for (int index = 1; index < route.Points.Count; index++)
            if (route.ZoneAt(index).HasValue)
                areas++;

        var parts = new List<string>(4)
        {
            route.Id,
            // A route whose count is drawn does not carry one number of agents, so the list shows the span
            // rather than the nominal fixed value the run may never use.
            route.CountRange != null
                ? $"{Mathf.RoundToInt(route.CountRange.Min)}-{Mathf.RoundToInt(route.CountRange.Max)} agents"
                : $"{agents} agent{(agents == 1 ? string.Empty : "s")}",
            $"{objectives} objective{(objectives == 1 ? string.Empty : "s")}"
        };
        if (areas > 0)
            parts.Add($"{areas} area{(areas == 1 ? string.Empty : "s")}");
        // A route that enters over a window does not look like one that starts on the same instant, and the
        // list is where an author sees at a glance which of their scenarios are spread out.
        if (route.SpawnWindow > 0f)
            parts.Add($"enters over {route.SpawnWindow:0.#} s");
        // A route whose values are drawn does not play the same twice, which the list says at a glance.
        if (drawn.Length > 0)
            parts.Add(drawn);

        return string.Join(" · ", parts);
    }

    /// <summary>The values this route lets the run draw, named for the route list, or an empty string.</summary>
    private static string RandomizationLabel(RouteDraft route)
    {
        var names = new List<string>();
        if (route.CountRange != null) names.Add("count");
        if (route.SpeedRange != null) names.Add("speed");
        if (route.StartYawRange != null) names.Add("heading");
        if (route.SpacingRange != null) names.Add("spacing");
        if (route.SpawnWindowRange != null) names.Add("window");
        return names.Count == 0 ? string.Empty : $"random {string.Join("/", names)}";
    }

    private RouteDraft ActiveRoute => _activeRouteIndex >= 0 && _activeRouteIndex < _routes.Count
        ? _routes[_activeRouteIndex]
        : null;

    /// <summary>The active route when it belongs to a robot, so a robot field never writes into a crowd.</summary>
    private RouteDraft ActiveRobotRoute
    {
        get
        {
            RouteDraft active = ActiveRoute;
            return active != null && active.IsRobot ? active : null;
        }
    }

    /// <summary>Number of robots the scenario drives, which is the number of robot routes.</summary>
    public int RobotRouteCount => _routes.Count(route => route.IsRobot);

    /// <summary>The id a new robot route takes: <c>robot_2</c> after <c>robot_1</c>, never twice.</summary>
    private string NextRobotRouteId()
    {
        int number = 1;
        while (_routes.Any(route => string.Equals(route.Id, ScenarioData.DefaultRobotId(number - 1), StringComparison.OrdinalIgnoreCase)))
            number++;
        return ScenarioData.DefaultRobotId(number - 1);
    }

    /// <summary>Label of a robot type as the dropdown shows it.</summary>
    private static string DisplayNameOf(string typeId) => RobotProfiles.Find(typeId).DisplayName;

    private int NextHumanRouteNumber()
    {
        int number = 1;
        while (_routes.Any(route => string.Equals(route.Id, $"Human route {number}", StringComparison.OrdinalIgnoreCase)))
            number++;
        return number;
    }

    private string MakeUniqueRouteId(string preferred)
    {
        string baseName = string.IsNullOrWhiteSpace(preferred) ? $"Human route {NextHumanRouteNumber()}" : preferred;
        string candidate = baseName;
        int suffix = 2;
        while (_routes.Any(route => string.Equals(route.Id, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{baseName} ({suffix++})";
        return candidate;
    }

    private static RouteDraft CreateHumanDraft(int index)
    {
        // A route the author just drew carries agents. The count and the speed come from the model's own
        // defaults, so the wizard cannot disagree with what HumanScenarioConfig documents: a draft born at
        // zero used to be dropped on save, which is the route that disappeared.
        var defaults = new HumanScenarioConfig();
        var draft = new RouteDraft
        {
            Id = $"Human route {index}",
            Count = defaults.Count,
            Speed = defaults.Speed,
            RouteModified = true
        };
        AppendPoint(draft, new Vector2(2f, 0f));
        AppendPoint(draft, Vector2.zero);
        return draft;
    }

    private static RouteDraft CreateHumanDraft(ScenarioData scenario, HumanScenarioConfig human, int index)
    {
        var draft = new RouteDraft
        {
            Id = string.IsNullOrWhiteSpace(human.Id) ? $"Human route {index}" : human.Id,
            Count = Mathf.Max(0, human.Count),
            Speed = Mathf.Max(0.01f, human.Speed),
            CountRange = human.CountRange?.Clone(),
            SpeedRange = human.SpeedRange?.Clone(),
            SpawnWindowRange = human.SpawnWindowRange?.Clone(),
            SpacingRange = human.Spawn?.SpacingRange?.Clone(),
            EndBehavior = HumanEndBehaviorParser.Parse(human.EndBehavior),
            // A group id inherited from an older YAML has no representation in the editor any more,
            // so it is dropped here instead of being carried over and silently re-emitted.
            Group = null,
            MovementController = human.MovementController?.Type,
            Formation = string.IsNullOrWhiteSpace(human.Spawn?.Formation) ? "pair" : human.Spawn.Formation.Trim().ToLowerInvariant(),
            GroupSpacing = human.Spawn != null ? Mathf.Max(0.4f, human.Spawn.Spacing) : 1.5f,
            FormationParameter = human.Spawn != null ? Mathf.Max(0f, human.Spawn.FormationParameter) : 0f,
            // A draft always carries a number, so a document that never wrote a window is read as the zero the
            // field shows. Saving the scenario then writes that zero, which is a decision the author can see.
            SpawnWindow = human.SpawnWindowSeconds,
            SpawnRandom = IsRandomSpawn(human.Spawn),
            SpawnZone = ReadZone(human.Spawn?.Zone) ?? default,
            Source = human,
            RouteModified = false
        };

        // The start of the route stays the point the author sees; the area around it is what the runtime scatters
        // the group in, so a zone-shaped spawn falls back to its centre here.
        AppendPoint(draft, ResolveSpawnPosition(scenario, human.Spawn));
        if (IsSpatialGoal(human.Goal))
            AppendPoint(draft, ResolveGoalPosition(scenario, human.Goal), GoalZone(human.Goal));
        else
            draft.HasNonSpatialGoal = human.Goal != null;
        if (human.Goals != null)
        {
            foreach (GoalConfig goal in human.Goals.Where(IsSpatialGoal))
                AppendPoint(draft, ResolveGoalPosition(scenario, goal), GoalZone(goal));
        }
        if (draft.Points.Count < 2)
            AppendPoint(draft, draft.Points[0] + Vector2.right * 2f);
        NormalizeZones(draft);
        return draft;
    }
}
