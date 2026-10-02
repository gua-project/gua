using System.Text.Json;
using Gua.Runtime;

namespace Gua.Testing.Recording;

public static class GuaTimedSegmentImport
{
    /// <summary>Explicit conversion of a game-input-only v2 Recording. Offsets are scheduling
    /// intentions with unknown provenance, never evidence of original host application time.
    /// UI v1/v2, conditions and coordinate fallback require the existing condition-based Replay.</summary>
    public static GuaTimedSegment FromRecording(string json, long durationMilliseconds, long maxLatenessMilliseconds,
        long executionTimeoutMilliseconds, long cleanupTimeoutMilliseconds)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2)
            throw new InvalidDataException("Timed conversion requires game-input-only Recording v2.");
        var inputs = new List<GuaTimedInput>();
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            if (step.GetProperty("action").GetString() != "game_input" || step.TryGetProperty("waitCondition", out _) ||
                step.TryGetProperty("target", out _) || step.TryGetProperty("coordinateFallback", out _))
                throw new InvalidDataException("Timed conversion rejects UI, conditions and coordinate fallback.");
            var offset = step.GetProperty("relativeMilliseconds").GetInt64();
            var sensitive = step.GetProperty("sensitive").GetBoolean();
            var args = step.GetProperty("arguments");
            if (sensitive && (step.TryGetProperty("value", out _) || args.TryGetProperty("value", out _) || args.TryGetProperty("text", out _)))
                throw new InvalidDataException("Sensitive legacy input contains plaintext.");
            var secret = step.TryGetProperty("secretKey", out var secretKey) ? secretKey.GetString() : null;
            var lease = args.TryGetProperty("leaseMs", out var leaseMs) ? leaseMs.GetUInt32() : 0;
            var device = args.TryGetProperty("gamepadIndex", out var index) ? index.GetInt32() : 0;
            var operation = step.GetProperty("operation").GetString();
            string Text(string key) => args.GetProperty(key).GetString() ?? throw new InvalidDataException("Missing input target.");
            double Number(string key) => args.GetProperty(key).GetDouble();
            JsonElement? Value(string key) => args.TryGetProperty(key, out var value) ? value.Clone() : null;
            var mapped = operation switch
            {
                "press_game_input_action" => new GuaTimedInput(offset, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, Text("actionId")),
                "set_game_input_action" => new(offset, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, Text("actionId"), Value("value"), lease),
                "release_game_input_action" => new(offset, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, Text("actionId")),
                "key_down" => new(offset, GuaGameInputKind.Keyboard, GuaGameInputOperation.Down, Text("code"), LeaseMilliseconds: lease),
                "key_up" => new(offset, GuaGameInputKind.Keyboard, GuaGameInputOperation.Up, Text("code")),
                "press_physical_key" => new(offset, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, Text("code")),
                "pointer_button_down" => new(offset, GuaGameInputKind.Pointer, GuaGameInputOperation.Down, Text("button"), LeaseMilliseconds: lease),
                "pointer_button_up" => new(offset, GuaGameInputKind.Pointer, GuaGameInputOperation.Up, Text("button")),
                "pointer_move" when Text("mode") == "delta" => new(offset, GuaGameInputKind.Pointer, GuaGameInputOperation.MoveDelta, "delta:", X: Number("x"), Y: Number("y")),
                "pointer_move" when Text("mode") == "absolute" => new(offset, GuaGameInputKind.Pointer, GuaGameInputOperation.MoveAbsolute,
                    "absolute:" + Text("coordinateSpace"), X: Number("x"), Y: Number("y")),
                "pointer_wheel" => new(offset, GuaGameInputKind.Pointer, GuaGameInputOperation.Wheel,
                    args.TryGetProperty("wheelUnit", out var unit) ? unit.GetString()! : "lines", X: Number("deltaX"), Y: Number("deltaY")),
                "gamepad_button_down" => new(offset, GuaGameInputKind.Gamepad, GuaGameInputOperation.Down, Text("button"), LeaseMilliseconds: lease, DeviceIndex: device),
                "gamepad_button_up" => new(offset, GuaGameInputKind.Gamepad, GuaGameInputOperation.Up, Text("button"), DeviceIndex: device),
                "set_gamepad_axis" => new(offset, GuaGameInputKind.Gamepad, GuaGameInputOperation.Set, Text("axis"), Value("value"), lease, DeviceIndex: device),
                "reset_gamepad" => new(offset, GuaGameInputKind.Gamepad, GuaGameInputOperation.Reset, "", DeviceIndex: device),
                "text_input" => new(offset, GuaGameInputKind.TextInput, GuaGameInputOperation.Set, "", Value("text")),
                "release_all_game_inputs" => new(offset, GuaGameInputKind.Cleanup, GuaGameInputOperation.ReleaseAll, ""),
                _ => throw new InvalidDataException("Unsupported legacy input operation."),
            };
            inputs.Add(mapped with { Sensitive = sensitive, SecretKey = secret });
        }
        var segment = new GuaTimedSegment(1, durationMilliseconds, maxLatenessMilliseconds, executionTimeoutMilliseconds,
            cleanupTimeoutMilliseconds, inputs, TimingProvenance: "legacy-unknown");
        GuaTimedSegmentFile.Validate(segment);
        return segment;
    }
}
