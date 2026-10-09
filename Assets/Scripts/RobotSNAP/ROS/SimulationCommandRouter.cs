using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RobotSNAP.Agents;
using RobotSNAP.Core;
using RobotSNAP.Core.Scenario;
using RobotSNAP.Metrics;
using UnityEngine;

namespace RobotSNAP.ROS
{
    /// <summary>
    /// Answer to one execution of a command: what was asked, whether it happened, and what the application
    /// did about it.
    /// </summary>
    public readonly struct CommandResult
    {
        /// <summary>True when the command ran and reached the state it asked for.</summary>
        public bool Ok { get; }

        /// <summary>Name of the command, lower cased; empty when the body could not be read as a command.</summary>
        public string Command { get; }

        /// <summary>What happened, in one sentence, or the reason the command was refused.</summary>
        public string Message { get; }

        /// <summary>
        /// Ids a crowd command did not find in the scene, so the client can spot a typo instead of waiting for
        /// a human that never moves. Null for every other command, which is what keeps the key out of their
        /// answers.
        /// </summary>
        public IReadOnlyList<int> UnknownIds { get; }

        /// <summary>
        /// Id of the robot a command addressed, when it addressed one: the id named by the "robot" key, or the
        /// primary the command reached without it. Null for every other command, which is what keeps the key
        /// out of their answers.
        /// </summary>
        public string Robot { get; }

        /// <summary>
        /// Extra document a command answers with, or null for the commands that answer with a sentence only.
        /// The metrics commands are the reason it exists: listing a session's episodes is a question whose
        /// answer is a document, and a document squeezed into <see cref="Message"/> would be a message no
        /// client could parse back.
        /// </summary>
        public JObject Payload { get; }

        /// <summary>
        /// Builds a result; a null name or message is stored as an empty string. <paramref name="unknownIds"/>
        /// carries the ids a crowd command could not find and <paramref name="robot"/> the id a robot command
        /// addressed, each left null - and therefore out of the answer - by the commands that have neither.
        /// <paramref name="payload"/> is the document a command answers with instead of a sentence, and is left
        /// null by every command that has none, which is what keeps the key out of their answers.
        /// </summary>
        public CommandResult(
            bool ok,
            string command,
            string message,
            IReadOnlyList<int> unknownIds = null,
            string robot = null,
            JObject payload = null)
        {
            Ok = ok;
            Command = command ?? "";
            Message = message ?? "";
            UnknownIds = unknownIds;
            Robot = robot;
            Payload = payload;
        }
    }

    /// <summary>
    /// Turns one JSON command into one action on the running application, so a client can drive a session -
    /// play, pause, reset, scenario, clock, robot, crowd - without reaching into the Unity scene.
    ///
    /// The body carries a "command" key plus the keys that command needs, for instance
    /// <c>{"command":"set_robot_goal","x":4.5,"z":2.0}</c> or
    /// <c>{"command":"humans","commands":[{"id":3,"vx":1.0,"vz":0.0},{"id":4,"stop":true}]}</c>. The answer
    /// says what actually happened, so the client never has to guess which part of a request was honoured. A
    /// malformed body, a missing key and an unknown command are all answered the same way, as a result with
    /// Ok=false, because a peer on the other end of the connector can do nothing with a thrown exception.
    ///
    /// A robot command may also name the robot it addresses with a "robot" key holding an id such as
    /// <c>robot_2</c>, and reaches the roster's primary when it names none.
    ///
    /// No reference is held between two commands: a scenario load destroys and rebuilds the environments, and
    /// the managers, robot, controller and crowd that live inside them, so every one of them is looked up
    /// again on the call that needs it, the way <see cref="SimulationStatePublisher"/> does. The singletons -
    /// the supervisor and the clock - are read from their instance property, which looks the scene up when the
    /// application has not registered them yet.
    /// </summary>
    public sealed class SimulationCommandRouter
    {
        /// <summary>
        /// Commands this router accepts, in the order they are announced to a client that asked for one of
        /// them that does not exist.
        /// </summary>
        private static readonly string[] AcceptedCommands =
        {
            "play", "pause", "toggle_pause", "reset", "load_scenario", "set_time_scale", "set_pacing",
            "release_pacing", "set_random_seed",
            "stop_simulation", "set_robot_goal", "clear_robot_goal", "stop_robot", "set_control_mode",
            "set_agent_controller", "humans",
            "metrics_episodes", "metrics_episode", "metrics_clear"
        };

        /// <summary>Control modes of the robot input controller, as this topic spells them.</summary>
        private const string AcceptedControlModes = "keyboard, ros, hybrid, scenario";

        /// <summary>Movement controllers of the crowd, as this topic spells them.</summary>
        private const string AcceptedAgentModes = "sfm, external, manual";

        /// <summary>Runs one command. Never throws: a malformed body comes back as Ok=false.</summary>
        public CommandResult Execute(string json)
        {
            try
            {
                return Dispatch(json);
            }
            catch (Exception exception)
            {
                // Some of these commands rebuild the scene while they run, and a peer that is still connected
                // is better served by an answer than by an exception crossing the connector.
                return new CommandResult(false, "", $"command failed: {exception.Message}");
            }
        }

        #region Dispatch

        /// <summary>
        /// Reads the body far enough to know which command it asks for, then hands it to that command. The
        /// reading never throws: the reason a body cannot be used is part of the answer.
        /// </summary>
        private CommandResult Dispatch(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new CommandResult(false, "", "empty command body");

            JObject body = ParseBody(json, out string parseError);
            if (body == null)
                return new CommandResult(false, "", parseError);

            if (!HasValue(body, "command"))
                return new CommandResult(false, "", "missing key 'command'");

            if (!TryGetString(body, "command", out string requested))
                return new CommandResult(false, "", "key 'command' must be a string");

            string command = requested.Trim().ToLowerInvariant();
            if (command.Length == 0)
                return new CommandResult(false, "", "missing key 'command'");

            switch (command)
            {
                case "play": return SetPaused(false, command);
                case "pause": return SetPaused(true, command);
                case "toggle_pause": return TogglePaused(command);
                case "reset": return Reset(body, command);
                case "load_scenario": return LoadScenario(body, command);
                case "stop_simulation": return StopSimulation(command);
                case "set_time_scale": return SetTimeScale(body, command);
                case "set_pacing": return SetPacing(body, command);
                case "release_pacing": return ReleasePacing(command);
                case "set_random_seed": return SetRandomSeed(body, command);
                case "set_robot_goal": return SetRobotGoal(body, command);
                case "clear_robot_goal": return ClearRobotGoal(body, command);
                case "stop_robot": return StopRobot(body, command);
                case "set_control_mode": return SetControlMode(body, command);
                case "set_agent_controller": return SetAgentController(body, command);
                case "humans": return SetHumans(body, command);
                case "metrics_episodes": return ListEpisodes(command);
                case "metrics_episode": return GetEpisode(body, command);
                case "metrics_clear": return ClearEpisodes(command);
                default:
                    return new CommandResult(false, requested,
                        $"unknown command '{requested}'; accepted commands: {string.Join(", ", AcceptedCommands)}");
            }
        }

        #endregion

        #region Play and Pause

        /// <summary>
        /// Play and pause both go through the Supervisor, which is what drives the clock. The answer reports
        /// the state the clock is in afterwards rather than the state that was asked for.
        /// </summary>
        private static CommandResult SetPaused(bool pause, string command)
        {
            Supervisor supervisor = Supervisor.Instance;
            Clock clock = ResolveClock();
            if (supervisor == null || clock == null)
                return new CommandResult(false, command, "no supervisor or clock in the scene");

            if (pause)
                supervisor.Pause();
            else
                supervisor.Resume();

            // The state is read from the supervisor, which is the object that just did the pausing. The clock
            // looked up by ResolveClock is not necessarily the one the supervisor drives when a scene carries
            // more than one, and reading that one made "pause" answer "simulation running".
            return new CommandResult(true, command, supervisor.IsPaused ? "simulation paused" : "simulation running");
        }

        /// <summary>Flips the pause state of the clock and reports where it landed.</summary>
        private static CommandResult TogglePaused(string command)
        {
            Supervisor supervisor = Supervisor.Instance;
            Clock clock = ResolveClock();
            if (supervisor == null || clock == null)
                return new CommandResult(false, command, "no supervisor or clock in the scene");

            supervisor.TogglePause();

            return new CommandResult(true, command, supervisor.IsPaused ? "simulation paused" : "simulation running");
        }

        #endregion

        #region Stop

        /// <summary>
        /// Stops the session the way the red Stop button of the application does. The button does not reach
        /// into the managers itself: it publishes a <see cref="StopSimulationCommand"/>, which the Scenario
        /// Manager answers by pausing the clock, clearing the agents of every environment and keeping the
        /// scenario and the map, which puts the session back in Ready.
        ///
        /// This command publishes that same event rather than doing the work itself, so the button and a
        /// client on the bridge leave the session in exactly the same state. The publish is synchronous, so
        /// the state read back here is already the one the stop reached: the answer describes what the
        /// session became, not what was asked of it.
        /// </summary>
        private static CommandResult StopSimulation(string command)
        {
            ScenarioManager manager = ResolveScenarioManager();
            if (manager == null)
                return new CommandResult(false, command, "no scenario manager in the scene");

            if (!manager.HasScenarioLoaded)
                return new CommandResult(false, command, "no scenario loaded; nothing to stop");

            EventBus.Instance.Publish(new StopSimulationCommand());

            SimulationState state = manager.CurrentState;
            string scenario = manager.CurrentScenarioId;
            string kept = string.IsNullOrEmpty(scenario)
                ? "scenario and map kept, agents cleared"
                : $"scenario '{scenario}' and its map kept, agents cleared";

            return new CommandResult(true, command, $"simulation stopped, state {state}; {kept}");
        }

        #endregion

        #region Scenario and Reset

        /// <summary>
        /// Back to the start of a scenario: the agents of the scenario in the scene are reset and re-applied,
        /// which puts the clock back to the start of the new run once the world is in place. The optional
        /// "scenario" key loads another scenario instead, with the clock started and the scenario applied; the
        /// optional "seed" key is written before the scenario is applied, which is the moment the seed is read.
        /// </summary>
        private static CommandResult Reset(JObject body, string command)
        {
            ScenarioManager manager = ResolveScenarioManager();
            if (manager == null)
                return new CommandResult(false, command, "no scenario manager in the scene");

            if (HasValue(body, "scenario"))
            {
                if (!TryRequireString(body, "scenario", command, out string requested, out CommandResult refusal))
                    return refusal;

                if (!TryResolveScenarioName(manager, requested, out string name, out string available))
                    return new CommandResult(false, command, $"unknown scenario '{requested}'; available scenarios: {available}");

                if (!TryApplySeed(body, command, out CommandResult seedFailure))
                    return seedFailure;

                manager.LoadScenario(name, startClock: true, autoApply: true, resetClock: true);
                bool queued = manager.PendingScenarioId == name;
                if (queued)
                    return new CommandResult(true, command,
                        $"scenario '{name}' queued behind the load in flight; it will be applied with the clock reset");

                return new CommandResult(true, command, $"scenario '{name}' loaded, applied and clock reset");
            }

            if (!manager.HasScenarioLoaded)
                return new CommandResult(false, command, "no scenario loaded; give the 'scenario' key to load one");

            if (!TryApplySeed(body, command, out CommandResult inPlaceSeedFailure))
                return inPlaceSeedFailure;

            // The clock is reset by the manager itself, once the re-applied world is in place; resetting it
            // here as well would put it back twice for one command, which is two episode boundaries for a
            // single reset.
            manager.ResetAndReapply();

            string current = manager.CurrentScenarioId;
            return new CommandResult(true, command, string.IsNullOrEmpty(current)
                ? "current scenario reset and reapplied"
                : $"scenario '{current}' reset and reapplied");
        }

        /// <summary>
        /// Loads a scenario by name and hands it to the Scenario Manager with the two flags the body can set:
        /// the scenario is applied unless "apply" is false, and the clock is started unless "start" is false,
        /// which leaves a scenario loaded, built and paused, exactly as the Scenario Manager does it.
        ///
        /// A scenario being built cannot be swapped under it, so a request that arrives during one is queued
        /// by the manager and started as soon as that one ends. The answer then says so instead of claiming
        /// the scenario was applied: a caller that needs to see the new world waits for the scenario id of
        /// <c>/simulation/state</c> to change, which is what <c>RobotSNAPClient.wait_for_scenario</c> does.
        /// </summary>
        private static CommandResult LoadScenario(JObject body, string command)
        {
            ScenarioManager manager = ResolveScenarioManager();
            if (manager == null)
                return new CommandResult(false, command, "no scenario manager in the scene");

            if (!TryRequireString(body, "scenario", command, out string requested, out CommandResult refusal))
                return refusal;

            if (!TryResolveScenarioName(manager, requested, out string name, out string available))
                return new CommandResult(false, command, $"unknown scenario '{requested}'; available scenarios: {available}");

            if (!TryGetBool(body, "start", true, out bool start))
                return new CommandResult(false, command, "key 'start' must be true or false");

            if (!TryGetBool(body, "apply", true, out bool apply))
                return new CommandResult(false, command, "key 'apply' must be true or false");

            if (!TryApplySeed(body, command, out CommandResult seedFailure))
                return seedFailure;

            manager.LoadScenario(name, startClock: start, autoApply: apply);

            string applied = apply ? "applied" : "loaded, not applied";
            string clockState = start ? "clock started" : "clock left paused";
            if (manager.PendingScenarioId == name)
                return new CommandResult(true, command,
                    $"scenario '{name}' queued behind the load in flight; it will be {applied}, {clockState}");
            return new CommandResult(true, command, $"scenario '{name}' {applied}, {clockState}");
        }

        #endregion

        #region Clock and Seed

        /// <summary>
        /// Sets how fast the simulation runs. Two things have to move together: Unity's own time scale, which
        /// is what <see cref="SimulationConfig.ApplyTimeSettings"/> writes, and the scale the simulation clock
        /// counts its own seconds with. The configuration clamps the value, so the applied one is read back
        /// from it rather than assumed, and the clock is given that same value.
        ///
        /// The configuration is written in place rather than through
        /// <see cref="Supervisor.UpdateConfig"/>, which also raises the config event and makes the scenario
        /// loader drop its scenario and map caches - not something a speed change should do.
        /// </summary>
        private static CommandResult SetTimeScale(JObject body, string command)
        {
            if (!TryRequireFloat(body, "time_scale", command, out float requested, out CommandResult refusal))
                return refusal;

            if (requested <= 0f)
                return new CommandResult(false, command, $"key 'time_scale' must be greater than zero, got {Format(requested)}");

            // Two optional keys ride with the scale, because the scale alone is not what a fast session runs
            // at. The physics step decides how much simulation one step covers, and the catch-up ceiling
            // decides how many steps one drawn frame is allowed to pay for: a session at a hundred times
            // speed that draws at one hertz owes a hundred seconds of simulation per frame, and the ten this
            // project ships with would answer a tenth of it.
            if (!TryOptionalPositiveFloat(body, "fixed_timestep", command, out float? timestep, out refusal))
                return refusal;
            if (!TryOptionalPositiveFloat(body, "maximum_delta_time", command, out float? ceiling, out refusal))
                return refusal;

            Supervisor supervisor = Supervisor.Instance;
            SimulationConfig config = supervisor != null ? supervisor.ActiveConfig : null;
            Clock clock = ResolveClock();

            if (config == null && clock == null)
                return new CommandResult(false, command, "no clock or configuration in the scene");

            float scale = requested;
            float appliedTimestep = 0f;
            float appliedCeiling = 0f;
            if (config != null)
            {
                if (timestep.HasValue)
                    config.FixedTimestep = timestep.Value;

                // A session already held one control period at a time cannot run faster than the period it
                // hands out: past that, a single frame of physics spends more simulation than the period and
                // whatever the client asked its episode to be is over before the gate is looked at again.
                // The step is written first because the ceiling is expressed in it.
                SimulationPacingGate lockstep = ResolvePacingGate();
                float usable = lockstep != null && lockstep.IsLockstep
                    ? SimulationPacingGate.MaxScaleForStep(lockstep.StepSeconds, config.FixedTimestep)
                    : float.PositiveInfinity;
                config.TimeScale = Mathf.Min(requested, usable);

                // Left out, the ceiling follows the scale up to ten seconds: a session that asks for ten times
                // speed may catch up on ten seconds of simulation in one frame, which is what the project
                // already allowed. It does not follow the ask past that, and the reason is the bridge rather
                // than the physics: a frame let loose on a whole minute of simulation runs three thousand
                // physics steps before the next frame, the streams paced on the simulation clock emit a
                // message for each of them, and the connector's outgoing queue takes the session down with it.
                // A client that wants a hundred times speed wants the pacing gate, where a frame carries one
                // control period and not a minute. A caller that knows better can still say so with
                // maximum_delta_time.
                config.MaximumDeltaTime = ceiling ?? Mathf.Clamp(requested, 1f, 10f);
                config.ApplyTimeSettings();
                scale = config.TimeScale;
                appliedTimestep = config.FixedTimestep;
                appliedCeiling = config.MaximumDeltaTime;
            }

            // In lockstep the catch-up ceiling belongs to the gate, which keeps it at one control period.
            SimulationPacingGate pacing = ResolvePacingGate();
            if (pacing != null && pacing.IsLockstep)
            {
                Time.maximumDeltaTime = pacing.CatchUpCeiling;
                appliedCeiling = Time.maximumDeltaTime;
            }

            if (clock != null)
                clock.SetTimeScale(scale);

            string note = scale == requested
                ? ""
                : $" (requested {Format(requested)}, clamped to {Format(scale)}" +
                  (pacing != null && pacing.IsLockstep
                      ? ": one control period of " + Format(pacing.StepSeconds) +
                        " s holds one physics step up to that scale"
                      : "") + ")";

            string extra = config == null
                ? ""
                : $", physics step {Format(appliedTimestep)} s, catch-up ceiling {Format(appliedCeiling)} s";

            return new CommandResult(true, command, $"time scale set to {Format(scale)}{note}{extra}");
        }

        /// <summary>
        /// Chooses how the session is paced: ``free`` running, or one control period per client step. The
        /// second is what makes a control period mean the same number of simulated seconds whatever the
        /// machine delivers and however long the client's policy takes to answer.
        /// </summary>
        private static CommandResult SetPacing(JObject body, string command)
        {
            if (!TryRequireString(body, "mode", command, out string mode, out CommandResult refusal))
                return refusal;

            SimulationPacingGate gate = ResolvePacingGate();
            if (gate == null)
                return new CommandResult(false, command, "no pacing gate in the scene");

            float scale = CurrentScale();

            switch (mode.Trim().ToLowerInvariant())
            {
                case "free":
                    gate.SetFree(scale);
                    // Leaving lockstep gives the frame its catch-up ceiling back.
                    Supervisor.Instance?.ActiveConfig?.ApplyTimeSettings();
                    return new CommandResult(true, command, $"free running at scale {Format(scale)}");

                case "lockstep":
                    if (!TryRequireFloat(body, "step_seconds", command, out float step, out refusal))
                        return refusal;
                    if (step <= 0f)
                        return new CommandResult(false, command,
                            $"key 'step_seconds' must be greater than zero, got {Format(step)}");

                    float usableAtStep = SimulationPacingGate.MaxScaleForStep(step, Time.fixedDeltaTime);
                    float effective = Mathf.Min(scale, usableAtStep);
                    SimulationConfig pacingConfig = Supervisor.Instance != null
                        ? Supervisor.Instance.ActiveConfig
                        : null;
                    if (pacingConfig != null && !Mathf.Approximately(effective, scale))
                    {
                        // The clock and the snapshots carry this number, so it has to be the one the world
                        // actually runs at and not the one that was asked for.
                        pacingConfig.TimeScale = effective;
                        pacingConfig.ApplyTimeSettings();
                    }

                    gate.SetLockstep(step, effective);
                    string clamped = Mathf.Approximately(effective, scale)
                        ? ""
                        : $" (asked {Format(scale)}; a frame of {Format(Time.fixedDeltaTime)} s physics " +
                          $"cannot spend less than {Format(Time.fixedDeltaTime * effective)} s of simulation, " +
                          $"so {Format(step)} s of period holds {Format(effective)} times speed - raise the " +
                          "control period to raise the scale)";
                    return new CommandResult(true, command,
                        $"lockstep, {Format(step)} s of simulation per release at scale {Format(effective)}" +
                        clamped);

                default:
                    return new CommandResult(false, command,
                        $"key 'mode' is 'free' or 'lockstep', got '{mode}'");
            }
        }

        /// <summary>
        /// Spend one period of the lockstep the client asked for. The answer comes back as soon as the world
        /// has been let go, not when the period is over: the client reads the clock itself, and waiting here
        /// would hold the acknowledgement behind a whole period of simulation.
        /// </summary>
        private static CommandResult ReleasePacing(string command)
        {
            SimulationPacingGate gate = ResolvePacingGate();
            if (gate == null)
                return new CommandResult(false, command, "no pacing gate in the scene");

            if (!gate.IsLockstep)
                return new CommandResult(false, command, "the session is not in lockstep pacing");

            gate.Release(CurrentScale());
            return new CommandResult(true, command, $"released {Format(gate.StepSeconds)} s");
        }

        private static SimulationPacingGate ResolvePacingGate()
        {
            return UnityEngine.Object.FindAnyObjectByType<SimulationPacingGate>();
        }

        /// <summary>The scale the session is configured with, or 1 when nothing carries one.</summary>
        private static float CurrentScale()
        {
            Supervisor supervisor = Supervisor.Instance;
            SimulationConfig config = supervisor != null ? supervisor.ActiveConfig : null;
            Clock clock = ResolveClock();
            if (config != null)
                return config.TimeScale;
            return clock != null ? clock.TimeScale : 1f;
        }

        /// <summary>
        /// One optional key that has to be a positive number when it is there at all. Absent is fine and
        /// answers ``null``, so a caller that only wants to change the scale keeps the pacing it had.
        /// </summary>
        private static bool TryOptionalPositiveFloat(
            JObject body, string key, string command, out float? value, out CommandResult failure)
        {
            value = null;
            failure = default;

            if (!HasValue(body, key))
                return true;

            if (!TryGetFloat(body, key, out float parsed))
            {
                failure = new CommandResult(false, command, $"key '{key}' must be a number");
                return false;
            }

            if (parsed <= 0f)
            {
                failure = new CommandResult(false, command, $"key '{key}' must be greater than zero, got {Format(parsed)}");
                return false;
            }

            value = parsed;
            return true;
        }

        /// <summary>
        /// Writes the seed the next scenario application will use. Unity's random state is seeded when a
        /// scenario is applied, so this changes the next run and not the crowd that is already walking; the
        /// answer says so instead of pretending the running session just became reproducible.
        /// </summary>
        private static CommandResult SetRandomSeed(JObject body, string command)
        {
            if (!TryRequireInt(body, "seed", command, out int seed, out CommandResult refusal))
                return refusal;

            if (!ApplySeed(seed))
                return new CommandResult(false, command, "no active configuration to write the seed to");

            return new CommandResult(true, command,
                $"random seed set to {Format(seed)}; it seeds the next scenario application, " +
                "the scenario already in the scene keeps the one it was applied with");
        }

        /// <summary>
        /// Writes a seed into the active configuration, which is what the next scenario application reads
        /// through <see cref="SimulationRuntimeSettings"/>. False when the scene has no configuration.
        /// </summary>
        private static bool ApplySeed(int seed)
        {
            Supervisor supervisor = Supervisor.Instance;
            SimulationConfig config = supervisor != null ? supervisor.ActiveConfig : null;
            if (config == null)
                return false;

            config.RandomSeed = seed;
            return true;
        }

        /// <summary>
        /// Applies the optional "seed" key of the two scenario commands. The seed is written before the
        /// scenario is applied, because applying is the moment the application reads it. True when the key is
        /// absent or was written; false with the reason in <paramref name="failure"/> when it cannot be used.
        /// </summary>
        private static bool TryApplySeed(JObject body, string command, out CommandResult failure)
        {
            failure = default;
            if (!HasValue(body, "seed"))
                return true;

            if (!TryGetInt(body, "seed", out int seed))
            {
                failure = new CommandResult(false, command, "key 'seed' must be a whole number");
                return false;
            }

            if (ApplySeed(seed))
                return true;

            failure = new CommandResult(false, command, "no active configuration to write the seed to");
            return false;
        }

        #endregion

        #region Robot

        /// <summary>
        /// Sends the robot named by the optional "robot" key - the primary when the body names none - to a
        /// point of the scene. Coordinates are Unity world metres on the ground plane; the optional "y" key
        /// puts the goal higher, for a scenario with levels, and the height of the robot is kept otherwise.
        /// </summary>
        private static CommandResult SetRobotGoal(JObject body, string command)
        {
            if (!TryRequireFloat(body, "x", command, out float x, out CommandResult refusal))
                return refusal;

            if (!TryRequireFloat(body, "z", command, out float z, out CommandResult refusalZ))
                return refusalZ;

            Robot robot = ResolveRobot(body, command, out string id, out CommandResult? robotRefusal);
            if (robotRefusal.HasValue)
                return robotRefusal.Value;

            if (robot == null)
                return new CommandResult(false, command, "no robot in the scene");

            float y = TryGetFloat(body, "y", out float requestedY) ? requestedY : robot.RobotTransform.position.y;

            robot.SetGoal(new Vector3(x, y, z));

            return new CommandResult(true, command,
                $"{Label(id)} goal set to ({Format(x)}, {Format(y)}, {Format(z)}) in world metres", null, id);
        }

        /// <summary>Clears the goal and the route of the robot named by the optional "robot" key.</summary>
        private static CommandResult ClearRobotGoal(JObject body, string command)
        {
            Robot robot = ResolveRobot(body, command, out string id, out CommandResult? robotRefusal);
            if (robotRefusal.HasValue)
                return robotRefusal.Value;

            if (robot == null)
                return new CommandResult(false, command, "no robot in the scene");

            robot.ClearGoal();

            return new CommandResult(true, command, $"{Label(id)} goal cleared", null, id);
        }

        /// <summary>
        /// Stops the robot named by the optional "robot" key, and the input controller driving it. The
        /// controller keeps its last target speeds and re-applies them on the next fixed step, so stopping the
        /// robot alone would be undone a frame later; its emergency stop clears those speeds and stops the
        /// robot, and is the public way to do both.
        /// </summary>
        private static CommandResult StopRobot(JObject body, string command)
        {
            Robot robot = ResolveRobot(body, command, out string id, out CommandResult? robotRefusal);
            if (robotRefusal.HasValue)
                return robotRefusal.Value;

            if (robot == null)
                return new CommandResult(false, command, "no robot in the scene");

            robot.Stop();

            RobotInputController controller = ResolveInputController(robot);
            if (controller == null)
                return new CommandResult(true, command,
                    $"{Label(id)} stopped, no robot input controller in the scene", null, id);

            controller.EmergencyStop();

            return new CommandResult(true, command,
                $"{Label(id)} stopped and its input controller zeroed", null, id);
        }

        /// <summary>
        /// Chooses who drives the robot named by the optional "robot" key - the primary when the body names
        /// none: the keyboard, the ROS velocity topic, both, or the scenario. The controller clears its target
        /// speeds and stops the robot when the mode changes.
        /// </summary>
        private static CommandResult SetControlMode(JObject body, string command)
        {
            if (!TryRequireString(body, "mode", command, out string mode, out CommandResult refusal))
                return refusal;

            Robot robot = ResolveRobot(body, command, out string id, out CommandResult? robotRefusal);
            if (robotRefusal.HasValue)
                return robotRefusal.Value;

            RobotInputController controller = ResolveInputController(robot);
            if (controller == null)
                return new CommandResult(false, command, "no robot input controller in the scene");

            RobotInputController.ControlMode controlMode;
            switch (mode.Trim().ToLowerInvariant())
            {
                case "keyboard":
                    controlMode = RobotInputController.ControlMode.Keyboard;
                    break;
                case "ros":
                    controlMode = RobotInputController.ControlMode.ROS;
                    break;
                case "hybrid":
                    controlMode = RobotInputController.ControlMode.Hybrid;
                    break;
                case "scenario":
                    controlMode = RobotInputController.ControlMode.Scenario;
                    break;
                default:
                    return new CommandResult(false, command,
                        $"unknown mode '{mode}'; accepted modes: {AcceptedControlModes}");
            }

            controller.SetControlMode(controlMode);

            return new CommandResult(true, command,
                $"{Label(id)} control mode set to {controlMode.ToString().ToLowerInvariant()}", null, id);
        }

        #endregion

        #region Crowd

        /// <summary>
        /// Hands the whole crowd to one movement controller, or gives it back: "sfm" makes every human walk
        /// with the social force model, and "external" makes them wait for the velocity a client sends on the
        /// crowd topic.
        /// </summary>
        private static CommandResult SetAgentController(JObject body, string command)
        {
            if (!TryRequireString(body, "mode", command, out string mode, out CommandResult refusal))
                return refusal;

            int controllerType;
            string label;
            switch (mode.Trim().ToLowerInvariant())
            {
                case "sfm":
                    controllerType = (int)MovementControllerType.SFM;
                    label = "sfm";
                    break;
                case "external":
                    controllerType = (int)MovementControllerType.External;
                    label = "external";
                    break;
                // A crowd under the manual controller is driven by the keyboard of the machine running the
                // application; a driver that takes one pedestrian with the grab key overrides it for that one.
                case "manual":
                    controllerType = (int)MovementControllerType.Manual;
                    label = "manual";
                    break;
                default:
                    return new CommandResult(false, command,
                        $"unknown mode '{mode}'; accepted modes: {AcceptedAgentModes}");
            }

            int switched = 0;
            foreach (HumanAgent human in FindActiveHumans())
            {
                // The controller lives on the movement component, which a human that has not been built yet
                // does not have.
                HumanMovement movement = human != null ? human.GetMovement() : null;
                if (movement == null)
                    continue;

                movement.SetControllerType(controllerType);
                switched++;
            }

            string message = switched == 1
                ? $"1 human switched to the {label} controller"
                : $"{switched} humans switched to the {label} controller";

            return new CommandResult(true, command, message);
        }

        /// <summary>
        /// Humans being simulated, pooled ones excluded - the same set the state stream publishes as people,
        /// so a count reported here matches the crowd a client is following.
        /// </summary>
        private static HumanAgent[] FindActiveHumans()
        {
            return UnityEngine.Object.FindObjectsByType<HumanAgent>(FindObjectsInactive.Exclude);
        }

        /// <summary>
        /// Drives the crowd in one message: one entry per human, each carrying an id and a velocity in world
        /// metres per second, or "stop": true, which gives the human back to its own controller. The answer
        /// reports what was applied and which ids are not in the scene, because a client cannot see the crowd
        /// it is driving and a typo would otherwise look like a human that never moves.
        /// </summary>
        private static CommandResult SetHumans(JObject body, string command)
        {
            var unknownIds = new List<int>();

            if (!HasValue(body, "commands"))
                return new CommandResult(false, command, "missing key 'commands'", unknownIds);

            JToken commands = body["commands"];
            if (commands.Type != JTokenType.Array)
                return new CommandResult(false, command, "key 'commands' must be an array", unknownIds);

            HumanManager manager = ResolveHumanManager();
            if (manager == null)
                return new CommandResult(false, command, "no human manager in the scene", unknownIds);

            int applied = 0;
            int refused = 0;

            foreach (JToken entry in commands)
            {
                if (!TryReadHumanCommand(entry, out int id, out bool stop, out float vx, out float vz))
                {
                    refused++;
                    continue;
                }

                bool known = stop
                    ? manager.ClearExternalVelocity(id)
                    : manager.SetExternalVelocity(id, new Vector2(vx, vz));

                if (known)
                    applied++;
                else
                    unknownIds.Add(id);
            }

            // Counts are written with the invariant culture, so the answer reads the same in every locale.
            var parts = new List<string>
            {
                applied == 1 ? "1 command applied" : FormattableString.Invariant($"{applied} commands applied"),
                unknownIds.Count == 1
                    ? "1 unknown id"
                    : FormattableString.Invariant($"{unknownIds.Count} unknown ids")
            };

            if (refused > 0)
            {
                parts.Add(refused == 1
                    ? "1 entry ignored"
                    : FormattableString.Invariant($"{refused} entries ignored"));
            }

            string summary = string.Join(", ", parts);
            bool ok = unknownIds.Count == 0 && refused == 0;

            return new CommandResult(ok, command, summary, unknownIds);
        }

        /// <summary>
        /// Reads one crowd entry. A missing velocity reads as zero, which is what a "stop" entry sends, and
        /// "stop" wins over a velocity given in the same entry. An entry whose keys are there but unreadable,
        /// or that carries no id, is refused rather than applied to agent 0.
        /// </summary>
        private static bool TryReadHumanCommand(JToken entry, out int id, out bool stop, out float vx, out float vz)
        {
            id = 0;
            stop = false;
            vx = 0f;
            vz = 0f;

            if (entry == null || entry.Type != JTokenType.Object)
                return false;

            var command = (JObject)entry;
            if (!TryGetInt(command, "id", out id))
                return false;

            if (TryGetBool(command, "stop", false, out bool wantsStop) && wantsStop)
            {
                stop = true;
                return true;
            }

            if (HasValue(command, "stop"))
                return false;

            if (HasValue(command, "vx") && !TryGetFloat(command, "vx", out vx))
                return false;

            if (HasValue(command, "vz") && !TryGetFloat(command, "vz", out vz))
                return false;

            return true;
        }

        #endregion

        #region Scene Lookup

        /// <summary>
        /// Manager that owns the scenarios of the scene. This router is not a MonoBehaviour, so the scene is
        /// searched through FindAnyObjectByType rather than through the instance a component would inherit.
        /// </summary>
        private static ScenarioManager ResolveScenarioManager()
        {
            return UnityEngine.Object.FindAnyObjectByType<ScenarioManager>();
        }

        /// <summary>
        /// Robot a command addresses: the one named by the optional "robot" key when the body carries one,
        /// otherwise the roster's primary. The roster is the source of truth when the scene runs one; a scene
        /// without a roster, or with an empty one, keeps the single robot these commands always resolved, so an
        /// existing client behaves exactly as it did. <paramref name="id"/> is the id the answer echoes, and
        /// <paramref name="refusal"/> carries the answer when the body names an id the roster does not hold.
        /// </summary>
        private static Robot ResolveRobot(JObject body, string command, out string id, out CommandResult? refusal)
        {
            id = null;
            refusal = null;

            RobotRoster roster = RobotRoster.Current;
            if (roster == null || roster.Count == 0)
            {
                Robot single = UnityEngine.Object.FindAnyObjectByType<Robot>();
                id = IdOf(roster, single);
                return single;
            }

            string requested = null;
            if (HasValue(body, "robot"))
            {
                if (!TryGetString(body, "robot", out string named))
                {
                    refusal = new CommandResult(false, command, "key 'robot' must be a string");
                    return null;
                }

                requested = named != null ? named.Trim() : null;
            }

            // Absent, null or blank names nobody, and the roster's primary is exactly the robot those
            // commands reached before the key existed.
            if (string.IsNullOrEmpty(requested))
            {
                Robot primary = roster.Primary;
                id = IdOf(roster, primary);
                return primary;
            }

            if (roster.TryGet(requested, out Robot robot))
            {
                id = IdOf(roster, robot);
                return robot;
            }

            refusal = new CommandResult(false, command,
                $"unknown robot '{requested}'; robots in the scene: {RosterIds(roster)}");
            return null;
        }

        /// <summary>Id a robot is addressed by: the one the roster holds, or the one its identity carries.</summary>
        private static string IdOf(RobotRoster roster, Robot robot)
        {
            if (robot == null)
                return null;

            string id = roster != null ? roster.IdOf(robot) : null;
            if (!string.IsNullOrEmpty(id))
                return id;

            RobotIdentity identity = RobotIdentity.Of(robot);
            return identity != null ? identity.Id : null;
        }

        /// <summary>
        /// Ids the roster carries, comma separated, for the refusal that tells a caller which robots it could
        /// have addressed instead.
        /// </summary>
        private static string RosterIds(RobotRoster roster)
        {
            var robots = new List<Robot>();
            roster.FillRobots(robots);

            var ids = new List<string>(robots.Count);
            foreach (Robot robot in robots)
            {
                string id = roster.IdOf(robot);
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }

            return ids.Count > 0 ? string.Join(", ", ids) : "none";
        }

        /// <summary>How an answer names a robot: its id when it has one, "robot" otherwise.</summary>
        private static string Label(string id)
        {
            return string.IsNullOrEmpty(id) ? "robot" : id;
        }

        /// <summary>
        /// Crowd of the scene, the one that owns the id-to-agent map a velocity command goes through. It is
        /// rebuilt with the environments, so it is looked up on the call that needs it like the others.
        /// </summary>
        private static HumanManager ResolveHumanManager()
        {
            return UnityEngine.Object.FindAnyObjectByType<HumanManager>();
        }

        /// <summary>
        /// Input controller of a robot: the one that lives on the robot, so a command naming a robot drives
        /// that robot's controller instead of whichever one the scene search happens to find first. A scene
        /// that keeps the controller elsewhere falls back to the scene-wide lookup of a single-robot session.
        /// </summary>
        private static RobotInputController ResolveInputController(Robot robot)
        {
            RobotInputController controller = robot != null ? robot.GetComponentInChildren<RobotInputController>() : null;
            return controller != null ? controller : ResolveInputController();
        }

        /// <summary>Input controller of the robot, when the scene gave it one.</summary>
        private static RobotInputController ResolveInputController()
        {
            return UnityEngine.Object.FindAnyObjectByType<RobotInputController>();
        }

        /// <summary>
        /// Clock of the session. Its instance property is a plain static field rather than a lazy lookup, so
        /// the scene is searched when it is empty or when the clock it points at has been destroyed.
        /// </summary>
        private static Clock ResolveClock()
        {
            Clock clock = Clock.Instance;
            return clock != null ? clock : UnityEngine.Object.FindAnyObjectByType<Clock>();
        }

        #endregion

        #region Body Reading

        /// <summary>
        /// Reads a body as a JSON object, or returns null with the reason in <paramref name="error"/>.
        /// </summary>
        internal static JObject ParseBody(string json, out string error)
        {
            error = null;
            try
            {
                JObject body = JsonConvert.DeserializeObject<JObject>(json);
                if (body == null)
                    error = "malformed command body: expected a JSON object";

                return body;
            }
            catch (JsonException exception)
            {
                error = $"malformed command body: {exception.Message}";
                return null;
            }
        }

        /// <summary>True when the key is there and carries something other than null.</summary>
        internal static bool HasValue(JObject body, string key)
        {
            JToken token = body[key];
            return token != null && token.Type != JTokenType.Null;
        }

        /// <summary>String at a key, or false when the key is absent or holds another type.</summary>
        internal static bool TryGetString(JObject body, string key, out string value)
        {
            value = null;
            JToken token = body[key];
            if (token == null || token.Type != JTokenType.String)
                return false;

            value = token.Value<string>();
            return true;
        }

        /// <summary>
        /// Number at a key, or false when the key is absent or holds something else. A number sent as a string
        /// is read too, because a client built on a loosely typed JSON library can send "1.5" for 1.5.
        /// </summary>
        internal static bool TryGetFloat(JObject body, string key, out float value)
        {
            value = 0f;
            JToken token = body[key];
            if (token == null || token.Type == JTokenType.Null)
                return false;

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                value = token.Value<float>();
                return true;
            }

            return token.Type == JTokenType.String
                && float.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Whole number at a key, or false when the key is absent or holds something else.</summary>
        internal static bool TryGetInt(JObject body, string key, out int value)
        {
            value = 0;
            JToken token = body[key];
            if (token == null || token.Type == JTokenType.Null)
                return false;

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }

            return token.Type == JTokenType.String
                && int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Boolean at a key, or <paramref name="fallback"/> when the key is absent. True and false sent as a
        /// string, and 1 and 0, are read as well, so a client that cannot tell the two apart still works.
        /// </summary>
        internal static bool TryGetBool(JObject body, string key, bool fallback, out bool value)
        {
            value = fallback;
            JToken token = body[key];
            if (token == null || token.Type == JTokenType.Null)
                return true;

            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }

            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>() != 0;
                return true;
            }

            if (token.Type == JTokenType.String && bool.TryParse(token.Value<string>(), out bool parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        /// <summary>
        /// String a command cannot run without. The two ways of getting it wrong are told apart, because a
        /// missing key and a key of the wrong type send a client to two different places.
        /// </summary>
        private static bool TryRequireString(JObject body, string key, string command, out string value, out CommandResult failure)
        {
            value = null;
            failure = default;

            if (!HasValue(body, key))
            {
                failure = new CommandResult(false, command, $"missing key '{key}'");
                return false;
            }

            if (!TryGetString(body, key, out value))
            {
                failure = new CommandResult(false, command, $"key '{key}' must be a string");
                return false;
            }

            return true;
        }

        /// <summary>Number a command cannot run without, refused the same way as a required string.</summary>
        private static bool TryRequireFloat(JObject body, string key, string command, out float value, out CommandResult failure)
        {
            value = 0f;
            failure = default;

            if (!HasValue(body, key))
            {
                failure = new CommandResult(false, command, $"missing key '{key}'");
                return false;
            }

            if (!TryGetFloat(body, key, out value))
            {
                failure = new CommandResult(false, command, $"key '{key}' must be a number");
                return false;
            }

            return true;
        }

        /// <summary>Whole number a command cannot run without, refused the same way as a required string.</summary>
        private static bool TryRequireInt(JObject body, string key, string command, out int value, out CommandResult failure)
        {
            value = 0;
            failure = default;

            if (!HasValue(body, key))
            {
                failure = new CommandResult(false, command, $"missing key '{key}'");
                return false;
            }

            if (!TryGetInt(body, key, out value))
            {
                failure = new CommandResult(false, command, $"key '{key}' must be a whole number");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Matches a requested scenario name against the ones the project has, ignoring case, and keeps the
        /// spelling on disk so the loader is asked for a file that exists. False when the name is unknown, with
        /// the available names in <paramref name="available"/> for the answer.
        /// </summary>
        private static bool TryResolveScenarioName(ScenarioManager manager, string requested, out string name, out string available)
        {
            name = null;
            available = "none";

            List<string> known = manager.GetAvailableScenarios();
            if (known == null || known.Count == 0)
                return false;

            available = string.Join(", ", known);

            foreach (string candidate in known)
            {
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                {
                    name = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Number as text, with the same decimal point whatever the machine's locale is.</summary>
        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Whole number as text, with the same sign whatever the machine's locale is.</summary>
        private static string Format(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region Metrics

        /// <summary>
        /// The three questions a client asks about the episodes of the session it is driving. They read the one
        /// store <see cref="MetricsRecorder"/> fills, so the answer a client gets and the list the dashboard
        /// draws are the same episodes in the same order.
        ///
        /// Every one of them answers with a document rather than a sentence - see
        /// <see cref="CommandResult.Payload"/> - because an episode carries a trajectory and a trajectory does
        /// not fit in a message a client could read back. Strings stay empty rather than null on the way out: a
        /// JSON document a client parses is easier to write against when its keys are always there.
        /// </summary>
        private static CommandResult ListEpisodes(string command)
        {
            MetricsStore store = MetricsStore.Instance;
            var episodes = new JArray();
            foreach (EpisodeMetrics episode in store.Episodes)
                episodes.Add(JObject.FromObject(episode));

            var payload = new JObject
            {
                ["session"] = store.SessionId ?? "",
                ["started_at"] = store.StartedAt ?? "",
                ["count"] = store.Count,
                ["episodes"] = episodes,
            };

            string message = store.Count == 0
                ? $"session {store.SessionId} holds no episode yet"
                : $"{store.Count} episode(s) in session {store.SessionId}";
            return new CommandResult(true, command, message, payload: payload);
        }

        /// <summary>
        /// One episode by identifier. A session that does not hold it is answered, not refused: a client asking
        /// about an episode a cleared session dropped is asking a question whose answer is "no", and an answer
        /// of "no" must not look like a broken command.
        /// </summary>
        private static CommandResult GetEpisode(JObject body, string command)
        {
            if (!TryRequireString(body, "id", command, out string id, out CommandResult refusal))
                return refusal;

            EpisodeMetrics episode = MetricsStore.Instance.Get(id);
            var payload = new JObject
            {
                ["id"] = id,
                ["found"] = episode != null,
                ["episode"] = episode == null ? JValue.CreateNull() : JObject.FromObject(episode),
            };

            string message = episode == null
                ? $"no episode '{id}' in session {MetricsStore.Instance.SessionId}"
                : $"episode '{id}' ({episode.Outcome})";
            return new CommandResult(true, command, message, payload: payload);
        }

        /// <summary>
        /// Empties the session store and starts a new one. The export files already on disk are deliberately
        /// left where they are: they are the record of a session that ran, and this command clears the live
        /// list, not the archive.
        /// </summary>
        private static CommandResult ClearEpisodes(string command)
        {
            MetricsStore store = MetricsStore.Instance;
            int cleared = store.Clear();
            var payload = new JObject
            {
                ["cleared"] = cleared,
                ["session"] = store.SessionId ?? "",
            };
            return new CommandResult(
                true,
                command,
                $"cleared {cleared} episode(s); session {store.SessionId} started",
                payload: payload);
        }

        #endregion
    }
}
