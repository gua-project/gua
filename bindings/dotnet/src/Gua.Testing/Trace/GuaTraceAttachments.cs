using System.Text.Json;
using Json.Schema;
using Gua.Core;

namespace Gua.Testing;

public static partial class GuaTraceCapture
{
    /// <summary>Capture caller-authorized JSON without changing the caller's result.
    /// The profile describes the supplied data, not a request to elevate host access.
    /// A mismatch is rejected before invoking the getter.</summary>
    public static bool JsonAttachment(GuaTraceSession trace, string stepId, string schema,
        Func<string> getter, GuaObservationProfile profile)
        => JsonAttachment(trace, stepId, schema, getter, profile, reason => AttachmentFailure(trace, stepId, reason));

    internal static bool JsonAttachment(GuaTraceSession trace, string stepId, string schema,
        Func<string> getter, GuaObservationProfile profile, Action<string> failure)
    {
        try
        {
            if (!MatchesProfile(trace, profile)) { failure("profile-mismatch"); return false; }
            using var document = JsonDocument.Parse(getter());
            if (!ValidTraceProjection(trace, schema, document.RootElement)) { failure("attachment-invalid"); return false; }
            if (trace.Attach(stepId, schema, document.RootElement)) return true;
            failure("attachment-unavailable"); return false;
        }
        catch { failure("attachment-failed"); return false; }
    }

    private static readonly Lazy<Json.Schema.JsonSchema> DiagnosticsProjection = new(() => LoadSchema("trace-diagnostics"));
    private static readonly Lazy<Json.Schema.JsonSchema> RecordingProjection = new(() => LoadSchema("trace-recording"));
    private static bool ValidTraceProjection(GuaTraceSession trace, string schema, JsonElement content)
    {
        var contract = schema switch { "gua.diagnostics.v1" => DiagnosticsProjection.Value,
            "gua.trace.recording.v1" => RecordingProjection.Value, _ => null };
        if (contract is null) return true;
        using var redacted = JsonDocument.Parse(trace.RedactObservation(content));
        return contract.Evaluate(redacted.RootElement).IsValid;
    }

    private static bool MatchesProfile(GuaTraceSession trace, GuaObservationProfile profile) =>
        profile is GuaObservationProfile.Debug or GuaObservationProfile.Player &&
        trace.ObservationProfile == (profile == GuaObservationProfile.Player ? "player" : "debug");

    private static bool AttachmentFailure(GuaTraceSession trace, string stepId, string reason)
    {
        trace.RecordCaptureFailure(stepId, "attachment", reason);
        return false;
    }

    /// <summary>Attach an already explicitly executed lint report. Never runs lint.</summary>
    public static bool Lint(GuaTraceSession trace, string stepId, GuaSemanticLintReport report) =>
        JsonAttachment(trace, stepId, "gua.semantic-lint.v1", () =>
            GuaSemanticLintReport.Parse(report.ToJson()).ToJson(), report?.Profile ?? GuaObservationProfile.Debug);

    /// <summary>Version and environment supplied by the caller; no machine/environment enumeration.</summary>
    public static bool Environment(GuaTraceSession trace, string stepId, GuaVersion version,
        IReadOnlyDictionary<string, string> environment, GuaObservationProfile profile) =>
        JsonAttachment(trace, stepId, "gua.environment.v1", () =>
            JsonSerializer.Serialize(new { version, environment }, GuaTraceJson.Options), profile);
}
