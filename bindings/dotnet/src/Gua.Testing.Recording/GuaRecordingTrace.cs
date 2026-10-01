using System.Text.Json;
using System.Text.Json.Serialization;
using Gua.Core;

namespace Gua.Testing.Recording;

/// <summary>Recording remains a separate format. Trace records non-replayable failures separately.</summary>
public static class GuaRecordingTrace
{
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
