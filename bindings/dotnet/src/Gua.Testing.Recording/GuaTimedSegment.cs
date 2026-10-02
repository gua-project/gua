using System.Text.Json;
using Gua.Runtime;

namespace Gua.Testing.Recording;

public enum GuaSegmentClock { Realtime, Simulation }
public enum GuaSegmentOutcome { Succeeded, Failed, Cancelled, TimedOut, Late }

/// <summary>A serializable plan. Values marked sensitive must be supplied through a resolver.</summary>
public sealed record GuaTimedInput(
    long OffsetMilliseconds, GuaGameInputKind Kind, GuaGameInputOperation Operation, string Target,
    JsonElement? Value = null, uint LeaseMilliseconds = 0, double X = 0, double Y = 0,
    int DeviceIndex = 0, bool Sensitive = false, string? SecretKey = null,
    GuaGameInputValueType? SemanticValueType = null);

public sealed record GuaTimedSegment(
    int SchemaVersion, long DurationMilliseconds, long MaxLatenessMilliseconds,
    long ExecutionTimeoutMilliseconds, long CleanupTimeoutMilliseconds, IReadOnlyList<GuaTimedInput> Inputs,
    GuaSegmentClock Clock = GuaSegmentClock.Realtime, bool RequireApplicationTimes = false,
    bool RequireSameTickApplication = false, string TimingProvenance = "planned");

public sealed record GuaTimedInputResult(int Index, long ScheduledMilliseconds, double? SentMilliseconds,
    ulong? RequestId, double? ResultReceivedMilliseconds, double? HostAppliedMilliseconds,
    bool? Succeeded, int? ErrorCode);

public sealed record GuaTimedSegmentResult(GuaSegmentOutcome Outcome, IReadOnlyList<GuaTimedInputResult> Inputs,
    bool CleanupSucceeded, bool NeutralConfirmed, string? FailureCode)
{
    public GuaSegmentClock Clock { get; init; }
    public string? SimulationScope { get; init; }
    public long MaxLatenessMilliseconds { get; init; }
    public long ExecutionTimeoutMilliseconds { get; init; }
    public long CleanupTimeoutMilliseconds { get; init; }
    public bool ApplicationTimingConfirmed { get; init; }
}

public static class GuaTimedSegmentFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Save(string path, GuaTimedSegment segment)
    {
        Validate(segment);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(segment, Options));
    }
    public static GuaTimedSegment Load(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        Require(document.RootElement, "schemaVersion", "durationMilliseconds", "maxLatenessMilliseconds",
            "executionTimeoutMilliseconds", "cleanupTimeoutMilliseconds", "inputs");
        RejectUnknown(document.RootElement, "schemaVersion", "durationMilliseconds", "maxLatenessMilliseconds",
            "executionTimeoutMilliseconds", "cleanupTimeoutMilliseconds", "inputs", "clock", "requireApplicationTimes",
            "requireSameTickApplication", "timingProvenance");
        foreach (var input in document.RootElement.GetProperty("inputs").EnumerateArray())
        {
            Require(input, "offsetMilliseconds", "kind", "operation", "target");
            RejectUnknown(input, "offsetMilliseconds", "kind", "operation", "target", "value", "leaseMilliseconds",
                "x", "y", "deviceIndex", "sensitive", "secretKey", "semanticValueType");
        }
        var segment = JsonSerializer.Deserialize<GuaTimedSegment>(json, Options)
            ?? throw new InvalidDataException("Empty timed segment.");
        Validate(segment);
        return segment;
    }

    public static void Validate(GuaTimedSegment segment)
    {
        if (segment is null) throw new ArgumentNullException(nameof(segment));
        if (segment.SchemaVersion != 1 || !Enum.IsDefined(typeof(GuaSegmentClock), segment.Clock) ||
            segment.TimingProvenance is not ("planned" or "legacy-unknown"))
            throw new InvalidDataException("Unsupported timed segment version, clock or timing provenance.");
        if (segment.DurationMilliseconds < 0 || segment.MaxLatenessMilliseconds < 0 ||
            segment.ExecutionTimeoutMilliseconds <= segment.DurationMilliseconds + (double)segment.MaxLatenessMilliseconds ||
            segment.ExecutionTimeoutMilliseconds > 60000 || segment.CleanupTimeoutMilliseconds is <= 0 or > 60000)
            throw new InvalidDataException("A segment requires finite duration, lateness, execution and cleanup budgets.");
        if (segment.Inputs is null || segment.Inputs.Count is < 1 or > 1000)
            throw new InvalidDataException("A segment requires 1..1000 inputs (bounded completion retention).");
        var held = new HashSet<(GuaGameInputKind, string, int)>();
        long previous = -1;
        foreach (var input in segment.Inputs)
        {
            if (input is null || input.OffsetMilliseconds < previous || input.OffsetMilliseconds < 0 ||
                input.OffsetMilliseconds > segment.DurationMilliseconds)
                throw new InvalidDataException("Offsets must be ordered and inside the segment.");
            previous = input.OffsetMilliseconds;
            if (input.Target is null || (string.IsNullOrWhiteSpace(input.Target) && input.Kind is not (GuaGameInputKind.Cleanup or GuaGameInputKind.TextInput) &&
                !(input.Kind == GuaGameInputKind.Gamepad && input.Operation == GuaGameInputOperation.Reset)) || input.Target.Contains('\0') ||
                input.Target.Contains('\r') || input.Target.Contains('\n') ||
                !Finite(input.X) || !Finite(input.Y) || input.DeviceIndex < 0 || input.DeviceIndex > 3 ||
                (input.Kind != GuaGameInputKind.Gamepad && input.DeviceIndex != 0))
                throw new InvalidDataException("Invalid input target, coordinate or device.");
            if (input.Sensitive ? input.Value is not null || string.IsNullOrWhiteSpace(input.SecretKey) : input.SecretKey is not null)
                throw new InvalidDataException("Sensitive input requires only a secret reference.");
            if (input.Sensitive && (input.Kind is not (GuaGameInputKind.Semantic or GuaGameInputKind.TextInput) || input.Operation != GuaGameInputOperation.Set))
                throw new InvalidDataException("Only semantic Set and text input support sensitive segment values.");
            var allowed = input.Kind switch
            {
                GuaGameInputKind.Semantic => input.Operation is GuaGameInputOperation.Set or GuaGameInputOperation.Press or GuaGameInputOperation.Release,
                GuaGameInputKind.Keyboard => input.Operation is GuaGameInputOperation.Down or GuaGameInputOperation.Up or GuaGameInputOperation.Press,
                GuaGameInputKind.Pointer => input.Operation is GuaGameInputOperation.Down or GuaGameInputOperation.Up or GuaGameInputOperation.MoveAbsolute or GuaGameInputOperation.MoveDelta or GuaGameInputOperation.Wheel,
                GuaGameInputKind.Gamepad => input.Operation is GuaGameInputOperation.Down or GuaGameInputOperation.Up or GuaGameInputOperation.Set or GuaGameInputOperation.Reset,
                GuaGameInputKind.TextInput => input.Operation == GuaGameInputOperation.Set,
                GuaGameInputKind.Cleanup => input.Operation == GuaGameInputOperation.ReleaseAll,
                _ => false,
            };
            if (!allowed) throw new InvalidDataException("Unsupported operation in a timed segment.");
            if (input.SemanticValueType is { } type && (input.Kind != GuaGameInputKind.Semantic ||
                !Enum.IsDefined(typeof(GuaGameInputValueType), type)))
                throw new InvalidDataException("Invalid semantic value type declaration.");
            ValidateRawTarget(input);
            if (!input.Sensitive) ValidatePayload(input, input.Value);
            var key = (input.Kind, input.Target, input.DeviceIndex);
            if (input.Kind == GuaGameInputKind.Cleanup) held.Clear();
            else if (input.Kind == GuaGameInputKind.TextInput || input.Kind == GuaGameInputKind.Semantic &&
                input.SemanticValueType == GuaGameInputValueType.Text) { }
            else if (input.Operation is GuaGameInputOperation.Set or GuaGameInputOperation.Down)
            {
                if (input.LeaseMilliseconds > 60000 || input.LeaseMilliseconds <=
                    segment.ExecutionTimeoutMilliseconds + (double)segment.MaxLatenessMilliseconds)
                    throw new InvalidDataException("Hold lease must exceed the reserved real-time budget plus lateness.");
                held.Add(key);
            }
            else if (input.Operation is GuaGameInputOperation.Release or GuaGameInputOperation.Up) held.Remove(key);
            else if (input.Operation == GuaGameInputOperation.Reset)
                held.RemoveWhere(item => item.Item1 == input.Kind && item.Item3 == input.DeviceIndex);
        }
        if (held.Count != 0) throw new InvalidDataException("Every hold must have an explicit release inside the segment.");
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    internal static void ValidatePayload(GuaTimedInput input, JsonElement? value)
    {
        if ((input.Kind == GuaGameInputKind.TextInput || input.Kind == GuaGameInputKind.Semantic &&
            input.SemanticValueType == GuaGameInputValueType.Text) && input.Operation == GuaGameInputOperation.Set &&
            value?.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Text Set requires a JSON string value.");
        if (input.Kind == GuaGameInputKind.Gamepad && input.Operation == GuaGameInputOperation.Set &&
            (value is not { ValueKind: JsonValueKind.Number } axis || !axis.TryGetDouble(out var number) ||
             !Finite(number) || number < -1 || number > 1))
            throw new InvalidDataException("Gamepad axis Set requires a finite number in [-1,1].");
        if (input.Kind != GuaGameInputKind.Semantic || input.Operation != GuaGameInputOperation.Set) return;
        bool Number(JsonElement? item) => item is { ValueKind: JsonValueKind.Number } element &&
            element.TryGetDouble(out var number) && Finite(number);
        var valid = input.SemanticValueType switch
        {
            GuaGameInputValueType.Button => value?.ValueKind is JsonValueKind.True or JsonValueKind.False,
            GuaGameInputValueType.Axis1D => Number(value),
            GuaGameInputValueType.Vector2 => value is { ValueKind: JsonValueKind.Object } vector &&
                vector.TryGetProperty("x", out var x) && vector.TryGetProperty("y", out var y) && Number(x) && Number(y),
            _ => true,
        };
        if (!valid) throw new InvalidDataException("Semantic Set payload does not match its value type.");
    }
    private static void ValidateRawTarget(GuaTimedInput input)
    {
        bool Is(params string[] targets) => targets.Contains(input.Target);
        var valid = input.Kind switch
        {
            GuaGameInputKind.Keyboard => System.Text.RegularExpressions.Regex.IsMatch(input.Target,
                "^(?:Key[A-Z]|Digit[0-9]|F(?:[1-9]|1[0-9]|2[0-4])|Numpad[0-9]|Backquote|Backslash|Backspace|BracketLeft|BracketRight|CapsLock|Comma|ContextMenu|Delete|End|Enter|Equal|Escape|Home|Insert|MetaLeft|MetaRight|Minus|NumLock|PageDown|PageUp|Pause|Period|Quote|ScrollLock|Semicolon|ShiftLeft|ShiftRight|Slash|Space|Tab|ControlLeft|ControlRight|AltLeft|AltRight|ArrowDown|ArrowLeft|ArrowRight|ArrowUp|PrintScreen|NumpadAdd|NumpadDecimal|NumpadDivide|NumpadEnter|NumpadMultiply|NumpadSubtract)$"),
            GuaGameInputKind.Pointer => input.Operation switch
            {
                GuaGameInputOperation.Down or GuaGameInputOperation.Up => Is("primary", "secondary", "auxiliary", "back", "forward"),
                GuaGameInputOperation.MoveAbsolute => Is("absolute:viewport_pixels") || Is("absolute:viewport_normalized") &&
                    input.X is >= 0 and <= 1 && input.Y is >= 0 and <= 1,
                GuaGameInputOperation.MoveDelta => Is("delta:"),
                GuaGameInputOperation.Wheel => Is("pixels", "lines"), _ => false,
            },
            GuaGameInputKind.Gamepad => input.Operation switch
            {
                GuaGameInputOperation.Down or GuaGameInputOperation.Up => Is("south", "east", "west", "north", "left_shoulder", "right_shoulder", "left_trigger", "right_trigger", "back", "start", "left_stick", "right_stick", "dpad_up", "dpad_down", "dpad_left", "dpad_right"),
                GuaGameInputOperation.Set => Is("left_stick_x", "left_stick_y", "right_stick_x", "right_stick_y"),
                GuaGameInputOperation.Reset => true, _ => false,
            },
            _ => true,
        };
        if (!valid) throw new InvalidDataException("Unsupported fixed raw-input target.");
    }
    private static void RejectUnknown(JsonElement value, params string[] names)
    {
        var seen = new HashSet<string>();
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name) || !seen.Add(property.Name)) throw new InvalidDataException("Unsupported or duplicate timed segment property.");
    }
    private static void Require(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (!value.TryGetProperty(name, out _)) throw new InvalidDataException("Missing required timed segment property.");
    }
}

/// <summary>All host methods are bounded, non-blocking and must preserve owner isolation.
/// OrderedApplication means apply order, not only enqueue order. Application timestamps use
/// the segment clock domain. Poll must never consume another request's result.</summary>
public interface IGuaTimedSegmentHost
{
    bool OrderedApplication { get; }
    bool ApplicationTimes { get; }
    bool SameTickApplication { get; }
    string? SimulationScope { get; }
    double SimulationMilliseconds { get; }
    string? ExecutionFailureCode { get; }
    void Begin(GuaTimedSegment segment);
    /// <summary>Invoke verifySendBoundary exactly once after preflight/marshalling, immediately
    /// before enqueue. A thrown guard prohibits dispatch. An exception after this boundary
    /// without a returned request ID leaves an unknown ordinary request in flight.</summary>
    ulong Send(GuaTimedInput input, JsonElement? secret, Action verifySendBoundary);
    GuaTimedCompletion? Poll(ulong requestId);
    ulong ReleaseAll();
    bool IsNeutral { get; }
    void End();
}
public sealed record GuaTimedCompletion(bool Succeeded, int ErrorCode = 0, double? HostAppliedMilliseconds = null);

/// <summary>Optional dynamic-schema preflight extension. Values include resolved secrets and must
/// only be used for validation/dispatch, never retained in evidence or error text. Reject malformed
/// payloads before creating an owner. The original host interface remains usable.</summary>
public interface IGuaTimedSegmentValueHost : IGuaTimedSegmentHost
{
    void Begin(GuaTimedSegment segment, IReadOnlyList<JsonElement?> resolvedValues);
}

/// <summary>Injectable real-time clock for deterministic tests. Simulation never replaces this deadline clock.</summary>
public interface IGuaSegmentRealtime
{
    double Milliseconds { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
