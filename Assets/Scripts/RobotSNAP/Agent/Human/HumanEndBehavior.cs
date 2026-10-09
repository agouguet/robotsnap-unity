using System;

namespace RobotSNAP.Agents
{
    /// <summary>What an agent does once it reached the last point of its route.</summary>
    public enum HumanEndBehavior
    {
        /// <summary>Stop where the route ended and stay there.</summary>
        Stay,

        /// <summary>Disappear (return to the pool) once the route is complete.</summary>
        Disappear,

        /// <summary>Walk the route again from its first point, forever.</summary>
        Loop
    }

    /// <summary>YAML values of <see cref="HumanEndBehavior"/>, with a few readable synonyms.</summary>
    public static class HumanEndBehaviorParser
    {
        public const string StayValue = "stay";
        public const string DisappearValue = "disappear";
        public const string LoopValue = "loop";

        public static HumanEndBehavior Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return HumanEndBehavior.Stay;

            switch (value.Trim().ToLowerInvariant())
            {
                case "disappear":
                case "despawn":
                case "remove":
                case "leave":
                    return HumanEndBehavior.Disappear;
                case "loop":
                case "loop route":
                case "cycle":
                case "repeat":
                    return HumanEndBehavior.Loop;
                case "stay":
                case "stay in place":
                case "idle":
                case "wait":
                    return HumanEndBehavior.Stay;
                default:
                    return HumanEndBehavior.Stay;
            }
        }

        public static string ToYamlValue(HumanEndBehavior behavior)
        {
            switch (behavior)
            {
                case HumanEndBehavior.Disappear: return DisappearValue;
                case HumanEndBehavior.Loop: return LoopValue;
                default: return StayValue;
            }
        }

        /// <summary>Display labels used by the scenario editor, in enum order.</summary>
        public static string ToDisplayName(HumanEndBehavior behavior)
        {
            switch (behavior)
            {
                case HumanEndBehavior.Disappear: return "Disappear";
                case HumanEndBehavior.Loop: return "Loop route";
                default: return "Stay in place";
            }
        }
    }

    /// <summary>
    /// Movement controller requested by a scenario entry, with the readable synonyms the YAML may carry.
    /// An unknown or empty value means "keep the controller of the HumanConfig asset", which is why the
    /// parser returns a nullable: the editor writes nothing rather than pinning a default.
    /// </summary>
    public static class HumanMovementControllerParser
    {
        public const string SfmValue = "SFM";
        public const string ExternalValue = "External";
        public const string ManualValue = "Manual";
        public const string ReplayValue = "Replay";

        public const string SfmDisplayName = "SFM (social forces)";
        public const string ExternalDisplayName = "External (Python API)";
        public const string ManualDisplayName = "Manual (keyboard)";
        public const string ReplayDisplayName = "Replay (recorded)";

        // Labels the editor wrote into scenarios while the ONNX and hybrid controllers still existed.
        private const string LegacyOnnxDisplayName = "ONNX (learned)";
        private const string LegacyHybridDisplayName = "Hybrid (SFM + ONNX)";

        public static bool TryParse(string value, out MovementControllerType controller)
        {
            controller = MovementControllerType.SFM;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // The editor stores the display label back into the scenario, so it has to read it too.
            string text = value.Trim();
            if (string.Equals(text, SfmDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                controller = MovementControllerType.SFM;
                return true;
            }
            if (string.Equals(text, ExternalDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                controller = MovementControllerType.External;
                return true;
            }
            if (string.Equals(text, ManualDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                controller = MovementControllerType.Manual;
                return true;
            }
            if (string.Equals(text, ReplayDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                controller = MovementControllerType.Replay;
                return true;
            }
            if (string.Equals(text, LegacyOnnxDisplayName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, LegacyHybridDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                controller = MovementControllerType.SFM;
                return true;
            }

            switch (text.ToLowerInvariant())
            {
                case "sfm":
                case "social force":
                case "socialforce":
                case "social forces":
                    controller = MovementControllerType.SFM;
                    return true;
                case "external":
                case "external control":
                case "python":
                case "python api":
                case "api":
                case "ros":
                case "remote":
                    controller = MovementControllerType.External;
                    return true;
                case "manual":
                case "keyboard":
                case "by hand":
                case "operator":
                    controller = MovementControllerType.Manual;
                    return true;
                case "replay":
                case "recorded":
                case "playback":
                    controller = MovementControllerType.Replay;
                    return true;

                // Historic values of the removed ONNX and hybrid controllers. Scenarios already saved on
                // disk may still carry them, so they keep loading and fall back to the SFM controller.
                case "onnx":
                case "onnx_prediction":
                case "onnxprediction":
                case "neural":
                case "prediction":
                case "model":
                case "hybrid":
                case "mixed":
                case "adaptive":
                    controller = MovementControllerType.SFM;
                    return true;
                default:
                    return false;
            }
        }

        public static string ToYamlValue(MovementControllerType controller)
        {
            switch (controller)
            {
                case MovementControllerType.Manual: return ManualValue;
                case MovementControllerType.External: return ExternalValue;
                case MovementControllerType.Replay: return ReplayValue;
                default: return SfmValue;
            }
        }

        /// <summary>Display labels of the scenario editor, where the first entry means "inherit".</summary>
        public static string ToDisplayName(MovementControllerType controller)
        {
            switch (controller)
            {
                case MovementControllerType.Manual: return ManualDisplayName;
                case MovementControllerType.External: return ExternalDisplayName;
                case MovementControllerType.Replay: return ReplayDisplayName;
                default: return SfmDisplayName;
            }
        }
    }
}
