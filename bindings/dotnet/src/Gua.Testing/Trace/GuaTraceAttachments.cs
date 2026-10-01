using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

public static partial class GuaTraceCapture
{
    /// <summary>Capture caller-authorized JSON without changing the caller's result.
    /// The profile describes the supplied data, not a request to elevate host access.
    /// A mismatch is rejected before invoking the getter.</summary>
    public static bool JsonAttachment(GuaTraceSession trace, string stepId, string schema,
        Func<string> getter, GuaObservationProfile profile)
    {
        try
        {
            if (!MatchesProfile(trace, profile)) return AttachmentFailure(trace, stepId, "profile-mismatch");
            using var document = JsonDocument.Parse(getter());
            if (trace.Attach(stepId, schema, document.RootElement)) return true;
            return AttachmentFailure(trace, stepId, "attachment-unavailable");
        }
        catch { return AttachmentFailure(trace, stepId, "attachment-failed"); }
    }

    private static bool MatchesProfile(GuaTraceSession trace, GuaObservationProfile profile) =>
        profile is GuaObservationProfile.Debug or GuaObservationProfile.Player &&
        trace.ObservationProfile == (profile == GuaObservationProfile.Player ? "player" : "debug");

    private static bool AttachmentFailure(GuaTraceSession trace, string stepId, string reason)
    {
        trace.ObservationIssue(reason);
        trace.Record(stepId, "capture.failure", GuaTraceJson.Element(new { channel = "attachment", reason }));
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
