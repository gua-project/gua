using System.Runtime.CompilerServices;
using System.Text.Json;
using Json.Schema;
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
    private readonly GuaTraceLifecycle? _lifecycle;
    private readonly bool _sensitive;
    private bool _reserved;
    private bool _sent;
    private bool _ended;
    private GuaTraceAction(GuaTraceSession trace, string source, string step, GuaTraceLifecycle? lifecycle, bool sensitive)
    { _trace = trace; _source = source; _step = step; _lifecycle = lifecycle; _sensitive = sensitive; }
    internal static GuaTraceAction? Begin(IGuaContext context, GuaActionRequest request, string? selector = null)
    {
        try
        {
            var trace = GuaAssertions.Options.Trace;
            if (trace is null) return null;
            var source = Sources.GetValue(context, _ => new Source()).Id;
            var step = trace.ActionStep(request.Action.ToString());
            GuaTraceLifecycle? lifecycle = null;
            try { lifecycle = trace.Watch(context); } catch { }
            var capture = new GuaTraceAction(trace, source, step, lifecycle, request.Sensitive);
            // Keep result metadata while masking only the input; do not build a buffer containing a marked secret.
            trace.Record(step, selector is null ? "request.sending" : "action.preparing", GuaTraceJson.Element(new { sourceId = source,
                action = request.Action, resolvedId = request.NodeId, selector,
                input = SafeInput(request), profile = request.ObservationProfile, epochStatus = "unconfirmed" }));
            capture._sent = selector is null;
            return capture;
        }
        catch { return null; }
    }
    internal void Resolved(string id, string? selector) => Safe(() =>
        _trace.Record(_step, "selector.resolved", GuaTraceJson.Element(new { selector, resolvedId = id })));
    internal void Sending(GuaActionRequest request, string? selector) => Safe(() =>
    {
        if (!_reserved && _lifecycle is not null) { _lifecycle.Reserve(); _reserved = true; }
        if (_sent) return;
        _sent = true;
        _trace.Record(_step, "request.sending", GuaTraceJson.Element(new { sourceId = _source,
            action = request.Action, resolvedId = request.NodeId, selector,
            input = SafeInput(request), epochStatus = "unconfirmed" }));
    });
    private static object SafeInput(GuaActionRequest request) => request.Sensitive ? "[redacted]" : new
    { request.Value, request.DeltaX, request.DeltaY, request.BoolValue, request.Key, request.Modifiers, request.ScrollUnit };
    internal void Failure(GuaActionException error) => Safe(() => _trace.Record(_step, "caller.failure",
        GuaTraceJson.Element(new { failureType = error.GetType().FullName, error.Kind, error.Error,
            requestId = error.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture) })));
    internal void Accepted(ulong requestId, GuaActionError error) => Safe(() =>
    {
        if (_reserved)
        {
            if (error == GuaActionError.None) _lifecycle!.Bind(requestId, _step);
            else _lifecycle!.Abandon();
            _reserved = false;
        }
        _lifecycle?.Capture();
        _trace.Record(_step, "request.enqueue", GuaTraceJson.Element(new { sourceId = _source,
            requestId = requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), error,
            accepted = error == GuaActionError.None, hostCompletion = "unconfirmed" }));
    });
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
            result.Error, result.NodeId, value = _sensitive || result.Sensitive ? "[redacted]" : result.Value,
            expectedState = "unconfirmed",
        }));
    });
    internal void End(GuaTraceOutcome outcome, string reason) => Safe(() =>
    {
        if (_ended) return;
        _ended = true;
        if (_reserved) { _lifecycle?.Abandon(); _reserved = false; }
        _trace.Record(_step, "caller.result", GuaTraceJson.Element(new { outcome, reason }));
        _trace.EndStep(_step, outcome);
    });
    private static void Safe(Action action) { try { action(); } catch { /* Secondary capture never replaces the operation. */ } }
}

public static partial class GuaTraceCapture
{
    private static readonly Lazy<JsonSchema> UiSchema = new(() => LoadSchema("UiTree"));
    private static readonly Lazy<JsonSchema> WorldSchema = new(() => LoadSchema("WorldTree"));
    private static JsonSchema LoadSchema(string name)
    {
        using var stream = typeof(GuaTraceCapture).Assembly.GetManifestResourceStream($"Gua.Trace.{name}.schema.json")!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    public static string Ui(GuaTraceSession trace, string stepId, GuaContext context,
        string sourceId, string reason, string? expectedSessionEpoch = null) =>
        Tree(trace, stepId, "ui", sourceId, reason,
            () => context.GetUiTreeJson(GuaTraceObservations.Profile(trace)), expectedSessionEpoch);

    public static string World(GuaTraceSession trace, string stepId, IGuaWorldContext context,
        string sourceId, string reason, string? expectedSessionEpoch = null) =>
        Tree(trace, stepId, "world", sourceId, reason,
            () => context is GuaContext local
                ? local.GetWorldObjectTreeJson(GuaTraceObservations.Profile(trace))
                : throw new InvalidOperationException("Use an authorized Tree getter for remote World capture."), expectedSessionEpoch);

    /// <summary>The getter must use the host-authorized profile. UI and World are separate
    /// reads, with independent host references. A failed read never becomes an empty tree.</summary>
    public static string Tree(GuaTraceSession trace, string stepId, string channel, string sourceId,
        string reason, Func<string> getter, string? expectedSessionEpoch = null)
    {
        try
        {
            using var parsed = JsonDocument.Parse(getter());
            var root = parsed.RootElement;
            var schema = channel switch {
                "ui" => UiSchema.Value,
                "world" => WorldSchema.Value,
                _ => throw new JsonException(),
            };
            if (!schema.Evaluate(root).IsValid || root.GetProperty("sessionEpoch").GetUInt64() == 0)
                throw new JsonException();
            // Validate the same transformation that will enter the snapshot buffer.
            // Safe text masking remains available; masked structural values do not.
            using var redacted = JsonDocument.Parse(trace.RedactObservation(root));
            if (!schema.Evaluate(redacted.RootElement).IsValid) throw new JsonException();
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
        // This interface cannot select or confirm a Player profile. Use an authorized getter for Player.
        => Diagnostics(trace, stepId, context.GetDiagnosticsJson, GuaObservationProfile.Debug);

    /// <summary>The caller must authorize the getter's profile. Capture before releasing the context.
    /// Screenshot pixels are omitted even when present in diagnostics.</summary>
    public static bool Diagnostics(GuaTraceSession trace, string stepId, Func<string> getter,
        GuaObservationProfile profile)
    {
        return JsonAttachment(trace, stepId, "gua.diagnostics.v1", () =>
        {
            using var doc = JsonDocument.Parse(getter());
            // Screenshot pixels have a separate policy and cannot inherit semantic sensitive markers.
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                    if (property.Name != "screenshot") property.WriteTo(writer);
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }, profile);
    }
}
