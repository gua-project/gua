using System.Runtime.CompilerServices;
using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

/// <summary>Opt-in integration observes the caller's results; it never reads the completion queue.</summary>
internal sealed class GuaTraceAction
{
    private sealed class Source { internal string Id { get; } = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<IGuaContext, Source> Sources = new();
    private readonly GuaTraceSession _trace;
    private readonly string _source;
    private readonly string _step;
    private bool _ended;
    private GuaTraceAction(GuaTraceSession trace, string source, string step) { _trace = trace; _source = source; _step = step; }
    internal static GuaTraceAction? Begin(IGuaContext context, GuaActionRequest request)
    {
        try
        {
            var trace = GuaAssertions.Options.Trace;
            if (trace is null) return null;
            var source = Sources.GetValue(context, _ => new Source()).Id;
            var step = trace.BeginStep(GuaTraceStepKind.Action, request.Action.ToString());
            // Keep result metadata while masking only the input; do not build a buffer containing a marked secret.
            trace.Record(step, "request.sending", GuaTraceJson.Element(new { sourceId = source,
                action = request.Action, resolvedId = request.NodeId,
                value = request.Sensitive ? "[redacted]" : request.Value,
                request.DeltaX, request.DeltaY, request.BoolValue, request.Key, request.Modifiers,
                request.ScrollUnit, profile = request.ObservationProfile, epochStatus = "unconfirmed" }));
            return new(trace, source, step);
        }
        catch { return null; }
    }
    internal void Accepted(ulong requestId, GuaActionError error) => Safe(() =>
        _trace.Record(_step, "request.enqueue", GuaTraceJson.Element(new { sourceId = _source,
            requestId = requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), error,
            accepted = error == GuaActionError.None, hostCompletion = "unconfirmed" })));
    internal void Completed(GuaActionEvent result) => Safe(() =>
    {
        if (result.SessionEpoch.HasValue)
            _trace.Correlate(_step, new(_source, result.SessionEpoch.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _trace.Record(_step, "request.completion", GuaTraceJson.Element(new
        {
            sourceId = _source, requestId = result.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sessionEpoch = result.SessionEpoch?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            frame = result.FrameSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            revision = result.Revision?.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Succeeded,
            result.Error, result.NodeId, value = result.Sensitive ? "[redacted]" : result.Value,
            expectedState = "unconfirmed",
        }));
    });
    internal void End(GuaTraceOutcome outcome, string reason) => Safe(() =>
    {
        if (_ended) return;
        _ended = true;
        _trace.Record(_step, "caller.result", GuaTraceJson.Element(new { outcome, reason }));
        _trace.EndStep(_step, outcome);
    });
    private static void Safe(Action action) { try { action(); } catch { /* Secondary capture never replaces the operation. */ } }
}

public static class GuaTraceCapture
{
    public static string Ui(GuaTraceSession trace, string stepId, GuaContext context,
        string sourceId, string reason, string? expectedSessionEpoch = null) =>
        Tree(trace, stepId, "ui", sourceId, reason,
            () => context.GetUiTreeJson(GuaTraceObservations.Profile(trace)), expectedSessionEpoch);

    public static string World(GuaTraceSession trace, string stepId, IGuaWorldContext context,
        string sourceId, string reason, string? expectedSessionEpoch = null) =>
        Tree(trace, stepId, "world", sourceId, reason,
            () => context.GetWorldObjectTreeJson(GuaTraceObservations.Profile(trace)), expectedSessionEpoch);

    /// <summary>The getter must use the host-authorized profile. UI and World are separate
    /// reads, with independent host references. A failed read never becomes an empty tree.</summary>
    public static string Tree(GuaTraceSession trace, string stepId, string channel, string sourceId,
        string reason, Func<string> getter, string? expectedSessionEpoch = null)
    {
        try
        {
            using var parsed = JsonDocument.Parse(getter());
            var root = parsed.RootElement;
            string Number(string key) => root.GetProperty(key).GetUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var host = new GuaTraceHost(sourceId, Number("sessionEpoch"), Number("frameSequence"), Number("revision"));
            if (expectedSessionEpoch is not null && expectedSessionEpoch != host.SessionEpoch)
            {
                trace.ObservationIssue("tree-stale");
                return trace.Observe(stepId, channel, reason, "stale", host);
            }
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var field in root.EnumerateObject())
                    if (field.Name is not ("sessionEpoch" or "frameSequence" or "revision")) field.WriteTo(writer);
                writer.WriteEndObject();
            }
            using var content = JsonDocument.Parse(stream.ToArray());
            return trace.Observe(stepId, channel, reason, "available", host, content.RootElement);
        }
        catch
        {
            trace.ObservationIssue("tree-failed");
            return trace.Observe(stepId, channel, reason, "failed", new(sourceId, expectedSessionEpoch ?? "0"));
        }
    }

    /// <summary>Reads only diagnostics exposed by the supplied context. Screenshots are deliberately omitted.</summary>
    public static bool Diagnostics(GuaTraceSession trace, string stepId, IGuaContext context)
    {
        try
        {
            using var doc = JsonDocument.Parse(context.GetDiagnosticsJson());
            // Screenshot pixels have a separate policy and cannot inherit semantic sensitive markers.
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                    if (property.Name != "screenshot") property.WriteTo(writer);
                writer.WriteEndObject();
            }
            using var safe = JsonDocument.Parse(buffer.ToArray());
            return trace.Attach(stepId, "gua.diagnostics.v1", safe.RootElement);
        }
        catch { trace.Record(stepId, "capture.failure", GuaTraceJson.Element(new { channel = "diagnostics", reason = "unavailable" })); return false; }
    }
}
