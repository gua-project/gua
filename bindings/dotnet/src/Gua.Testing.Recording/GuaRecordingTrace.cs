using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Core;
using Json.Schema;

namespace Gua.Testing.Recording;

/// <summary>Recording remains a separate format. Trace records non-replayable failures separately.</summary>
public static class GuaRecordingTrace
{
    private static readonly Lazy<Json.Schema.JsonSchema> TimedResultSchema = new(() =>
    {
        using var stream = typeof(GuaRecordingTrace).Assembly.GetManifestResourceStream("Gua.Recording.TimedResult.schema.json")!;
        using var reader = new StreamReader(stream);
        return Json.Schema.JsonSchema.FromText(reader.ReadToEnd());
    });
    /// <summary>Attach values-free timing evidence without executing Replay or guessing application times.</summary>
    public static bool AttachTimedResult(GuaTraceSession trace, string stepId, GuaTimedSegmentResult result,
        GuaObservationProfile profile = GuaObservationProfile.Debug) =>
        GuaTraceCapture.JsonAttachment(trace, stepId, "gua.timed-segment-result.v1", () =>
        {
            var requestIds = new HashSet<ulong>();
            if (result.Inputs.Any(input => input.RequestId is { } id && !requestIds.Add(id)))
                throw new InvalidDataException("Duplicate timed input request correlation.");
            if (result.Inputs.Any(input => input.ResultReceivedMilliseconds is { } received &&
                (input.SentMilliseconds is not { } sent || received < sent)))
                throw new InvalidDataException("Invalid client timing evidence.");
            if (result.ApplicationTimingConfirmed)
                for (var i = 0; i < result.Inputs.Count; i++)
                {
                    var input = result.Inputs[i];
                    if (input.HostAppliedMilliseconds is not { } applied || applied < input.ScheduledMilliseconds ||
                        applied - input.ScheduledMilliseconds > result.MaxLatenessMilliseconds ||
                        i > 0 && applied < result.Inputs[i - 1].HostAppliedMilliseconds)
                        throw new InvalidDataException("Invalid application timing confirmation.");
                }
            var json = JsonSerializer.Serialize(new { schemaVersion = 1, result = new
            {
                result.Outcome, result.CleanupSucceeded, result.NeutralConfirmed, result.FailureCode,
                result.Clock, result.SimulationScope, result.MaxLatenessMilliseconds,
                result.ExecutionTimeoutMilliseconds, result.CleanupTimeoutMilliseconds, result.ApplicationTimingConfirmed,
                inputs = result.Inputs.Select(input => new
                {
                    input.Index, input.ScheduledMilliseconds, input.SentMilliseconds,
                    requestId = input.RequestId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    input.ResultReceivedMilliseconds, input.HostAppliedMilliseconds, input.Succeeded, input.ErrorCode,
                }),
            } }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var document = JsonDocument.Parse(json);
            if (!TimedResultSchema.Value.Evaluate(document.RootElement).IsValid)
                throw new InvalidDataException("Invalid timed segment result attachment.");
            return json;
        }, profile);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Attach a validated recording to its related Step. Sensitive steps are masked by Trace;
    /// safe replay references are retained separately. Does not read files or execute Replay.</summary>
    public static bool Attach(GuaTraceSession trace, string stepId, GuaRecording recording,
        GuaObservationProfile profile = GuaObservationProfile.Debug)
    {
        var attached = GuaTraceCapture.JsonAttachment(trace, stepId, "gua.trace.recording.v1", () =>
        {
            GuaRecordingFile.Validate(recording);
            return JsonSerializer.Serialize(new { schemaVersion = 1, recording }, Options);
        }, profile);
        if (!attached) return false;
        return GuaTraceCapture.JsonAttachment(trace, stepId, "gua.recording.references.v1", () =>
            JsonSerializer.Serialize(new { schemaVersion = 1, steps = recording.Steps.Select((step, index) =>
                new { index, requestId = step.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    eventId = step.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture), step.SecretKey }) }, Options), profile);
    }
}
