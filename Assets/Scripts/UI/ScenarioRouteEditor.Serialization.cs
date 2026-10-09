using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RobotSNAP.Agents;
using RobotSNAP.Core.Scenario;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Reading a scenario into the wizard and writing it back out: the reference table, the
/// areas, the spawn and goal configs, and the point helpers the two directions share.</summary>
public sealed partial class ScenarioRouteEditor
{

    /// <summary>
    /// Writes every route of the wizard into <paramref name="scenario"/> and returns the crowd routes the
    /// writer had to leave out, named with the reason. A route that carries no agent has nothing to write -
    /// its points would be references to nothing - but the author drew it, so the caller is told rather than
    /// left to find it gone from the file.
    /// </summary>
    public IReadOnlyList<string> WriteToScenario(ScenarioData scenario)
    {
        scenario.Points ??= new Dictionary<string, RefPoint>();
        HashSet<string> previousRouteReferences = CollectRouteReferences(scenario);
        var skippedRoutes = new List<string>();

        // Every robot of the scenario is written as one entry of the list, in the order the editor lists them.
        // The robot a client reaches without naming anybody is robot_1, which is the id the first route carries,
        // so the order of the list is what it always was for a client that knows a single robot.
        var robotConfigs = new List<RobotScenarioConfig>();
        List<RouteDraft> robotRoutes = _routes.Where(route => route.IsRobot).ToList();
        for (int index = 0; index < robotRoutes.Count; index++)
        {
            RouteDraft robot = robotRoutes[index];
            EnsureMinimumPoints(robot);
            NormalizeZones(robot);

            RobotScenarioConfig config = robot.RobotSource ?? new RobotScenarioConfig();
            config.Id = string.IsNullOrWhiteSpace(robot.Id) ? ScenarioData.DefaultRobotId(index) : robot.Id;
            config.Type = string.IsNullOrWhiteSpace(robot.RobotType) ? RobotProfiles.DefaultId : robot.RobotType;
            config.Speed = robot.Speed > 0f ? robot.Speed : RobotProfiles.Find(config.Type).MaxLinearSpeed;
            config.Behavior = string.IsNullOrWhiteSpace(config.Behavior) ? "normal" : config.Behavior;
            // The scalar stays the value the author fixed, and the range - when there is one - is what the run
            // draws from. A route that fixes both values writes nulls here and keeps its old file shape.
            config.SpeedRange = robot.SpeedRange;
            config.StartYawRange = robot.StartYawRange;

            string prefix = $"robot_{index + 1}";
            string startRef = string.IsNullOrWhiteSpace(config.StartRef) ? $"{prefix}_start" : config.StartRef;
            string goalRef = string.IsNullOrWhiteSpace(config.GoalRef) ? $"{prefix}_goal" : config.GoalRef;

            // A robot's start and objectives are areas when the author turned them into ones, exactly like a
            // crowd's: the route holds the shape, and this writes it into the point table the scenario reads.
            WriteReference(scenario, startRef, robot.Points[0], PointArea(robot, 0), robot.StartYaw);
            WriteReference(scenario, goalRef, robot.Points[^1], PointArea(robot, robot.Points.Count - 1));
            config.StartRef = startRef;
            config.GoalRef = goalRef;

            var waypointRefs = new List<string>();
            for (int pointIndex = 1; pointIndex < robot.Points.Count - 1; pointIndex++)
            {
                string reference = config.WaypointRefs != null && pointIndex - 1 < config.WaypointRefs.Count
                    ? config.WaypointRefs[pointIndex - 1]
                    : $"{prefix}_waypoint_{pointIndex}";
                waypointRefs.Add(reference);
                WriteReference(scenario, reference, robot.Points[pointIndex], PointArea(robot, pointIndex));
            }
            config.WaypointRefs = waypointRefs.Count > 0 ? waypointRefs : null;
            robotConfigs.Add(config);
        }

        scenario.Robots = robotConfigs;
        // The single-robot section is only a way to read an old file: writing both would put two different
        // robots in one scenario the next time it is opened.
        scenario.Robot = null;

        var humanConfigs = new List<HumanScenarioConfig>();
        int humanIndex = 1;
        foreach (RouteDraft draft in _routes.Where(route => !route.IsRobot))
        {
            // The author stays master of the count: a route left at zero is reported, never given one quietly.
            if (!CarriesAgents(draft))
            {
                skippedRoutes.Add($"{draft.Id} carries no agents (count {draft.Count}, no count range)");
                continue;
            }

            HumanScenarioConfig config = draft.Source ?? new HumanScenarioConfig();
            config.Id = string.IsNullOrWhiteSpace(config.Id) ? $"human_route_{humanIndex}" : config.Id;
            config.Count = draft.Count;
            config.Speed = draft.Speed;
            config.CountRange = draft.CountRange;
            config.SpeedRange = draft.SpeedRange;
            config.SpawnWindowRange = draft.SpawnWindowRange;
            config.EndBehavior = HumanEndBehaviorParser.ToYamlValue(draft.EndBehavior);
            // Grouping is carried by the route formation now, so the editor never writes a group id back.
            config.Group = null;
            config.Spawn ??= new SpawnConfig();
            config.Spawn.Formation = string.IsNullOrWhiteSpace(draft.Formation) ? "pair" : draft.Formation.Trim().ToLowerInvariant();
            config.Spawn.Spacing = Mathf.Clamp(
                draft.GroupSpacing,
                GroupFormation.MinSpacing(config.Spawn.Formation),
                3f);
            config.Spawn.SpacingRange = draft.SpacingRange;
            config.Spawn.FormationParameter = Mathf.Max(0f, draft.FormationParameter);
            // A route that enters over time says so; one that starts together writes the zero the runtime reads
            // as "everybody at once", so a scenario never has to guess what an absent key meant.
            config.SpawnWindow = Mathf.Max(0f, draft.SpawnWindow);
            // An inherited controller is written as an absent block, so the HumanConfig asset keeps deciding.
            config.MovementController = string.IsNullOrWhiteSpace(draft.MovementController)
                ? null
                : new MovementControllerConfig { Type = draft.MovementController.Trim() };

            if (draft.Source == null || draft.RouteModified)
            {
                EnsureMinimumPoints(draft);
                NormalizeZones(draft);
                config.Spawn ??= new SpawnConfig();
                config.Goal ??= new GoalConfig();
                string safeId = ToFileId(config.Id);

                if (draft.SpawnRandom && IsUsableZone(draft.SpawnZone))
                    WriteZoneSpawn(config.Spawn, draft.SpawnZone);
                else
                {
                    SetSpawnReference(config.Spawn, $"{safeId}_start");
                    WritePoint(scenario, config.Spawn.Reference, draft.Points[0]);
                }

                WriteGoal(scenario, config.Goal, $"{safeId}_goal_1", draft.Points[1], draft.ZoneAt(1));

                var additionalGoals = new List<GoalConfig>();
                for (int goalIndex = 2; goalIndex < draft.Points.Count; goalIndex++)
                {
                    GoalConfig goal = config.Goals != null && goalIndex - 2 < config.Goals.Count
                        ? config.Goals[goalIndex - 2]
                        : new GoalConfig();
                    WriteGoal(scenario, goal, $"{safeId}_goal_{goalIndex}", draft.Points[goalIndex], draft.ZoneAt(goalIndex));
                    additionalGoals.Add(goal);
                }
                config.Goals = additionalGoals.Count > 0 ? additionalGoals : null;
            }

            humanConfigs.Add(config);
            humanIndex++;
        }
        scenario.Humans = humanConfigs;

        HashSet<string> activeRouteReferences = CollectRouteReferences(scenario);
        foreach (string staleReference in previousRouteReferences.Except(activeRouteReferences))
            scenario.Points.Remove(staleReference);

        return skippedRoutes;
    }

    private static Rect? GoalZone(GoalConfig goal) => IsRandomGoal(goal) ? ReadZone(goal?.Zone) : null;

    private static bool TryResolveReference(ScenarioData scenario, string reference, out Vector2 position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(reference) || scenario?.Points == null ||
            !scenario.Points.TryGetValue(reference, out RefPoint point) || point == null)
            return false;
        Vector3 vector = point.ToVector3();
        position = new Vector2(vector.x, vector.z);
        return true;
    }

    private static Vector2 ResolveSpawnPosition(ScenarioData scenario, SpawnConfig spawn)
    {
        if (spawn == null) return Vector2.zero;
        if (TryResolveReference(scenario, spawn.Reference, out Vector2 referenced)) return referenced;
        Vector3 position = spawn.Position?.ToVector3() ?? spawn.Zone?.ToVector3() ?? Vector3.zero;
        return new Vector2(position.x, position.z);
    }

    private static Vector2 ResolveGoalPosition(ScenarioData scenario, GoalConfig goal)
    {
        if (goal == null) return Vector2.zero;
        Debug.Log($"Resolving goal '{goal.Reference}' of type '{goal.Type}' for position.");
        if (TryResolveReference(scenario, goal.Reference, out Vector2 referenced)) return referenced;
        Vector3 position = goal.Position?.ToVector3() ?? goal.Zone?.ToVector3() ?? Vector3.zero;
        Debug.LogWarning($"Goal '{goal.Reference}' has no spatial reference; using position {position}.");
        return new Vector2(position.x, position.z);
    }

    private static bool IsSpatialGoal(GoalConfig goal)
    {
        if (goal == null) return false;
        string type = goal.Type?.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(type) || type == "point" || type == "random";
    }

    private static bool IsRandomGoal(GoalConfig goal) =>
        goal != null && string.Equals(goal.Type?.Trim(), "random", StringComparison.OrdinalIgnoreCase);

    private static bool IsRandomSpawn(SpawnConfig spawn) =>
        string.Equals(spawn?.Type?.Trim(), "random", StringComparison.OrdinalIgnoreCase);

    // ==========================================
    //          POINT AND AREA BOOKKEEPING
    // ==========================================

    /// <summary>
    /// Every mutation of the point list goes through these helpers: <see cref="RouteDraft.PointZones"/> is
    /// parallel to <see cref="RouteDraft.Points"/>, and an area landing on the wrong objective would be a
    /// silent authoring bug rather than a visible one.
    /// </summary>
    private static void AppendPoint(RouteDraft draft, Vector2 point, Rect? zone = null)
    {
        draft.Points.Add(point);
        draft.PointZones.Add(zone);
    }

    private static void InsertPointAt(RouteDraft draft, int index, Vector2 point, Rect? zone = null)
    {
        index = Mathf.Clamp(index, 0, draft.Points.Count);
        draft.Points.Insert(index, point);
        draft.PointZones.Insert(index, zone);
    }

    private static void RemovePointAt(RouteDraft draft, int index)
    {
        if (index < 0 || index >= draft.Points.Count)
            return;

        draft.Points.RemoveAt(index);
        if (index < draft.PointZones.Count)
            draft.PointZones.RemoveAt(index);
    }

    private static void SwapPoints(RouteDraft draft, int first, int second)
    {
        (draft.Points[first], draft.Points[second]) = (draft.Points[second], draft.Points[first]);
        if (first < draft.PointZones.Count && second < draft.PointZones.Count)
        {
            (draft.PointZones[first], draft.PointZones[second]) =
                (draft.PointZones[second], draft.PointZones[first]);
        }
    }

    /// <summary>Brings the area list back to the length of the point list after a load or an undo.</summary>
    private static void NormalizeZones(RouteDraft draft)
    {
        while (draft.PointZones.Count < draft.Points.Count)
            draft.PointZones.Add(null);

        if (draft.PointZones.Count > draft.Points.Count)
            draft.PointZones.RemoveRange(draft.Points.Count, draft.PointZones.Count - draft.Points.Count);
    }

    /// <summary>World XZ rectangle of an authored area, or null when the reference is a plain point.</summary>
    private static Rect? ReadZone(RefPoint reference)
    {
        if (reference == null || !reference.IsBounds)
            return null;

        Bounds bounds = reference.ToBounds();
        if (bounds.size.x <= 0f || bounds.size.z <= 0f)
            return null;

        return Rect.MinMaxRect(
            bounds.min.x, bounds.min.z,
            bounds.max.x, bounds.max.z);
    }

    /// <summary>Writes a world XZ rectangle back as the centre and size a scenario stores.</summary>
    private static RefPoint WriteZone(Rect zone)
    {
        var center = new Vector3(zone.center.x, 0f, zone.center.y);
        var size = new Vector3(zone.width, 0f, zone.height);
        return RefPoint.FromBounds(center, size);
    }

    /// <summary>Normalised rectangle from two corners the author clicked on the map.</summary>
    private static Rect ZoneFromCorners(Vector2 first, Vector2 second) =>
        Rect.MinMaxRect(
            Mathf.Min(first.x, second.x), Mathf.Min(first.y, second.y),
            Mathf.Max(first.x, second.x), Mathf.Max(first.y, second.y));

    /// <summary>A square area around a point, used when an objective is switched to a random arrival.</summary>
    private static Rect DefaultZoneAround(Vector2 center, float size = 4f) =>
        Rect.MinMaxRect(center.x - size * 0.5f, center.y - size * 0.5f,
                        center.x + size * 0.5f, center.y + size * 0.5f);

    /// <summary>An area the runtime can actually sample: a zero-size rectangle would pin the agent again.</summary>
    private static bool IsUsableZone(Rect zone) => zone.width >= 0.5f && zone.height >= 0.5f;

    /// <summary>Builds a positive-size rectangle from the centre and size an author typed.</summary>
    private static Rect ZoneFromCenterAndSize(Vector2 center, Vector2 size)
    {
        float width = Mathf.Max(0.5f, Mathf.Abs(size.x));
        float depth = Mathf.Max(0.5f, Mathf.Abs(size.y));
        return Rect.MinMaxRect(
            center.x - width * 0.5f, center.y - depth * 0.5f,
            center.x + width * 0.5f, center.y + depth * 0.5f);
    }

    /// <summary>
    /// Moves one route point and drags its arrival area along, so an area can never be left behind on the
    /// map by the point it belongs to.
    /// </summary>
    private static void SetPointPosition(RouteDraft draft, int index, Vector2 position)
    {
        if (index < 0 || index >= draft.Points.Count)
            return;

        Vector2 delta = position - draft.Points[index];
        draft.Points[index] = position;
        if (index < draft.PointZones.Count && draft.PointZones[index].HasValue)
        {
            Rect zone = draft.PointZones[index].Value;
            draft.PointZones[index] = new Rect(zone.x + delta.x, zone.y + delta.y, zone.width, zone.height);
        }
    }

    private static void EnsureMinimumPoints(RouteDraft draft)
    {
        if (draft.Points.Count == 0) AppendPoint(draft, Vector2.zero);
        if (draft.Points.Count == 1) AppendPoint(draft, draft.Points[0] + Vector2.right * 2f);
    }

    private static bool HasRepeatedConsecutivePoint(RouteDraft draft)
    {
        for (int index = 1; index < draft.Points.Count; index++)
            if (Vector2.Distance(draft.Points[index - 1], draft.Points[index]) < 0.001f) return true;
        return false;
    }

    private static HashSet<string> CollectRouteReferences(ScenarioData scenario)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        AddReference(references, scenario?.Robot?.StartRef);
        AddReference(references, scenario?.Robot?.GoalRef);
        if (scenario?.Robot?.WaypointRefs != null)
        {
            foreach (string reference in scenario.Robot.WaypointRefs)
                AddReference(references, reference);
        }

        if (scenario?.Humans != null)
        {
            foreach (HumanScenarioConfig human in scenario.Humans.Where(human => human != null))
            {
                AddReference(references, human.Spawn?.Reference);
                AddReference(references, human.Goal?.Reference);
                if (human.Goals == null)
                    continue;
                foreach (GoalConfig goal in human.Goals.Where(goal => goal != null))
                    AddReference(references, goal.Reference);
            }
        }

        return references;
    }

    private static void AddReference(ISet<string> references, string reference)
    {
        if (!string.IsNullOrWhiteSpace(reference))
            references.Add(reference);
    }

    private static string FormatRouteSummary(RouteDraft draft)
    {
        if (draft.Points.Count < 2) return "Incomplete route";
        return $"{draft.Points.Count - 1} objective(s): " +
               string.Join(" → ", draft.Points.Select(point => $"({point.x:0.##}, {point.y:0.##})"));
    }

    private static void WritePoint(ScenarioData scenario, string reference, Vector2 point)
    {
        scenario.Points[reference] = new RefPoint
        {
            X = RoundCoordinate(point.x),
            Y = 0f,
            Z = RoundCoordinate(point.y)
        };
    }

    /// <summary>
    /// One point carrying the heading an agent placed on it faces. Only the start of a robot has one: it is
    /// what the run begins pointing at, and a scenario the author drew without a heading keeps none.
    /// </summary>
    private static void WritePoint(ScenarioData scenario, string reference, Vector2 point, float yaw)
    {
        WritePoint(scenario, reference, point);
        scenario.Points[reference].Yaw = RoundYaw(yaw);
    }

    /// <summary>
    /// Writes one reference of a route as the point or the area the author drew. A reference names one or the
    /// other, never both, and both live in the same point table - which is what lets a robot carry areas too,
    /// even though its scenario block is only a list of references.
    /// </summary>
    private static void WriteReference(ScenarioData scenario, string reference, Vector2 point, Rect? area, float? yaw = null)
    {
        if (area.HasValue)
        {
            RefPoint zone = WriteZone(area.Value);
            if (yaw.HasValue)
                zone.Yaw = RoundYaw(yaw.Value);

            scenario.Points[reference] = zone;
            return;
        }

        if (yaw.HasValue)
            WritePoint(scenario, reference, point, yaw.Value);
        else
            WritePoint(scenario, reference, point);
    }

    /// <summary>The entry of the point table a reference names, or null when the scenario has none.</summary>
    private static RefPoint PointAt(ScenarioData scenario, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || scenario?.Points == null)
            return null;

        return scenario.Points.TryGetValue(reference, out RefPoint point) ? point : null;
    }

    private static float RoundCoordinate(float value) => Mathf.Round(value * 100f) / 100f;

    private static float RoundYaw(float value) => Mathf.Round(value * 10f) / 10f;

    /// <summary>
    /// Writes one objective. A fixed point keeps its named reference in the scenario's point table; an area
    /// becomes a random goal carrying its zone inline, and its reference is dropped so the two can never
    /// disagree about where the agent is going.
    /// </summary>
    private static void WriteGoal(
        ScenarioData scenario,
        GoalConfig goal,
        string fallbackReference,
        Vector2 point,
        Rect? zone)
    {
        if (zone.HasValue)
        {
            goal.Type = "random";
            goal.Zone = WriteZone(zone.Value);
            goal.Position = null;
            goal.Target = null;
            goal.Reference = null;
            return;
        }

        SetGoalReference(goal, fallbackReference);
        WritePoint(scenario, goal.Reference, point);
    }

    /// <summary>Writes the spawn as an area: the runtime draws one walkable point inside it per run.</summary>
    private static void WriteZoneSpawn(SpawnConfig spawn, Rect zone)
    {
        spawn.Type = "random";
        spawn.Zone = WriteZone(zone);
        spawn.Position = null;
        spawn.Reference = null;
    }

    private static void SetSpawnReference(SpawnConfig spawn, string fallbackReference)
    {
        spawn.Type = "point";
        spawn.Reference = string.IsNullOrWhiteSpace(spawn.Reference) ? fallbackReference : spawn.Reference;
        spawn.Position = null;
        spawn.Zone = null;
    }

    private static void SetGoalReference(GoalConfig goal, string fallbackReference)
    {
        goal.Type = "point";
        goal.Reference = string.IsNullOrWhiteSpace(goal.Reference) ? fallbackReference : goal.Reference;
        goal.Position = null;
        goal.Zone = null;
        goal.Target = null;
    }

    private static string ToFileId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "human_route";
        return new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray()).Trim('_');
    }
}
