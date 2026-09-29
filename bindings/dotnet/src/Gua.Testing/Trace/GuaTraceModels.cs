using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gua.Testing;

public enum GuaTraceCaptureMode { Recent, Streaming }
public enum GuaTraceSavePolicy { OnFailure, Always }
public enum GuaTraceOutcome { Unknown, Passed, Failed, Interrupted }
public enum GuaTraceStepKind { Action, Assertion, Mark, Lifecycle }

/// <summary>Finite collection limits. The 64 KiB manifest reserve is outside MaxArtifactBytes.</summary>
public sealed class GuaTraceOptions
{
    public string OutputDirectory { get; init; } = Path.Combine("artifacts", "gua-traces");
    public GuaTraceCaptureMode CaptureMode { get; init; } = GuaTraceCaptureMode.Recent;
    public GuaTraceSavePolicy SavePolicy { get; init; } = GuaTraceSavePolicy.OnFailure;
    public int MaxSteps { get; init; } = 100;
    public int MaxMemoryBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxQueueItems { get; init; } = 256;
    public long MaxArtifactBytes { get; init; } = 256L * 1024 * 1024;
    public int MaxAttachmentBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxEventBytes { get; init; } = 256 * 1024;
    public TimeSpan FlushTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string Profile { get; init; } = "debug";
    /// <summary>Additional literal secrets, scrubbed in keys and strings before retention or hashing.</summary>
    public IReadOnlyList<string> Secrets { get; init; } = Array.Empty<string>();
}

/// <summary>Decimal strings preserve native uint64 identity in JavaScript readers.</summary>
public sealed record GuaTraceRequest(string SourceId, string SessionEpoch, string RequestId);
public sealed record GuaTraceHost(string SourceId, string SessionEpoch, string? Frame = null,
    string? Revision = null, string? Timestamp = null, string? ClockId = null);
public sealed record GuaTraceEvent(int SchemaVersion, string TraceId, long Sequence, string EventId,
    string StepId, string Type, double CollectedMilliseconds, JsonElement Data)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record GuaTraceStatus(bool DetailStopped, long EvictedSteps, long DroppedEvents,
    IReadOnlyList<string> Issues)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record GuaTraceManifest(int SchemaVersion, string TraceId, string CaptureMode,
    string SavePolicy, string Profile, string PrimaryOutcome, bool Finalized,
    string CollectionClock, string StartedAt, long LastSequence, GuaTraceStatus Quality)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record GuaTraceReadResult(GuaTraceManifest Manifest, IReadOnlyList<GuaTraceEvent> Events,
    IReadOnlyDictionary<string, JsonElement> Blobs, IReadOnlyList<string> Issues);

internal static class GuaTraceJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    internal static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
}
