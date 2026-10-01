using System.Globalization;
using System.Text.Json;
using Json.Schema;
using Gua.Core;

namespace Gua.Testing;

/// <summary>Opt-in, independently owned Observe cursor. Call Poll at observed operation
/// boundaries; it records only published, received changes, never infers causality.
/// No background polling or completion-queue consumption.</summary>
public sealed class GuaTraceObservations : IDisposable
{
    private static readonly Lazy<(JsonSchema Schema, EvaluationOptions Options)> Contract = new(() => {
        var options = new EvaluationOptions();
        JsonSchema? transport = null;
        foreach (var name in new[] { "value-v1", "enum-catalog-v1", "observe-v1", "observe-transport-v1" }) {
            using var stream = typeof(GuaTraceObservations).Assembly.GetManifestResourceStream($"Gua.Trace.{name}.schema.json")!;
            using var reader = new StreamReader(stream);
            var schema = JsonSchema.FromText(reader.ReadToEnd());
            options.SchemaRegistry.Register(schema);
            transport = schema;
        }
        return (transport!, options);
    });
    private readonly object _gate = new();
    private readonly GuaTraceSession _trace;
    private readonly Func<(IDisposable Token, string Snapshot, Func<string> Poll)> _subscribe;
    private readonly Func<string> _snapshot;
    private IDisposable? _token;
    private Func<string>? _poll;
    private string _source = "unconfirmed", _epoch = "0", _interval = "";
    private ulong _sequence;
    private bool _broken, _disposed;
    private string _brokenAvailability = "gap";

    private GuaTraceObservations(GuaTraceSession trace,
        Func<(IDisposable, string, Func<string>)> subscribe, Func<string> snapshot)
    { _trace = trace; _subscribe = subscribe; _snapshot = snapshot; }

    public static GuaTraceObservations Subscribe(GuaTraceSession trace, string stepId, GuaContext context,
        string reason = "before") => Subscribe(trace, stepId,
            () => context.SubscribeObservations(Profile(trace)),
            () => context.GetObserveSnapshotTransportJson(Profile(trace)), reason);

    /// <summary>Also supports a Gua.Runtime host via its SubscribeObservations and
    /// GetObserveSnapshotTransportJson methods. The returned profile must match the Trace.</summary>
    public static GuaTraceObservations Subscribe(GuaTraceSession trace, string stepId,
        Func<GuaObserveSubscription> subscribe, Func<string> snapshot, string reason = "before")
    {
        var capture = new GuaTraceObservations(trace, () => {
            var token = subscribe();
            return (token, token.SnapshotTransportJson, token.PollTransportJson);
        }, snapshot);
        capture.Resubscribe(stepId, reason);
        return capture;
    }

    public static GuaTraceObservations Subscribe(GuaTraceSession trace, string stepId,
        GuaWebSocketContext context, string reason = "before")
    {
        var capture = new GuaTraceObservations(trace, () => {
            var token = context.SubscribeObservations();
            return (token, token.SnapshotJson, token.PollJson);
        }, context.GetObserveSnapshotJson);
        capture.Resubscribe(stepId, reason);
        return capture;
    }

    internal static GuaObservationProfile Profile(GuaTraceSession trace) =>
        trace.ObservationProfile == "player" ? GuaObservationProfile.Player : GuaObservationProfile.Debug;

    /// <summary>A new atomic Snapshot+cursor starts a new interval. A previous missing
    /// interval remains missing; the new Snapshot cannot recover its history.</summary>
    public bool Resubscribe(string stepId, string reason = "resynchronized")
    {
        lock (_gate)
        {
            if (_disposed) return false;
            Release();
            try
            {
                var subscription = _subscribe();
                _token = subscription.Token; _poll = subscription.Poll;
                using var parsed = JsonDocument.Parse(subscription.Snapshot);
                var (document, catalogs) = Envelope(parsed.RootElement, "snapshot");
                _source = document.GetProperty("sourceId").GetString()!;
                _epoch = Decimal(document, "sessionEpoch");
                _sequence = document.GetProperty("sequence").GetUInt64();
                _interval = Guid.NewGuid().ToString("N"); _broken = false; _brokenAvailability = "gap";
                return SaveSnapshot(stepId, reason, document, catalogs, "subscription-start");
            }
            catch { Release(); return Break(stepId, reason, "failed"); }
        }
    }

    public bool Poll(string stepId, string reason = "intermediate")
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_broken || _poll is null) return Missing(stepId, reason, _brokenAvailability);
            try
            {
                using var parsed = JsonDocument.Parse(_poll());
                var (document, catalogs) = Envelope(parsed.RootElement, "changes");
                var status = document.GetProperty("status").GetString();
                if (status != "ok")
                {
                    return Break(stepId, reason, status == "stale_session" ? "stale" : "gap", document);
                }
                if (document.GetProperty("sourceId").GetString() != _source || Decimal(document, "sessionEpoch") != _epoch)
                { return Break(stepId, reason, "stale", document); }
                var events = document.GetProperty("events");
                var next = _sequence;
                // Validate the entire received interval before claiming its continuity.
                foreach (var change in events.EnumerateArray())
                {
                    if (next == ulong.MaxValue || change.GetProperty("sequence").GetUInt64() != next + 1 ||
                        change.GetProperty("sourceId").GetString() != _source || Decimal(change, "sessionEpoch") != _epoch ||
                        change.GetProperty("profile").GetString() != _trace.ObservationProfile)
                    { return Break(stepId, reason, "gap", document); }
                    ++next;
                }
                if (next != document.GetProperty("sequence").GetUInt64())
                { return Break(stepId, reason, "gap", document); }
                var saved = true; var index = 0;
                foreach (var change in events.EnumerateArray())
                {
                    var clean = Normalize(change);
                    saved &= _trace.Record(stepId, "observation.change", GuaTraceJson.Element(new {
                        channel = "observe", reason, intervalId = _interval, continuity = "continuous",
                        change = change.GetProperty("kind").GetString(), host = Host(change),
                        received = clean, catalogs = catalogs[index++] }));
                }
                _sequence = next;
                if (!saved) { _broken = true; _brokenAvailability = "gap"; _trace.ObservationIssue("observe-storage-gap"); }
                return saved;
            }
            catch { Release(); return Break(stepId, reason, "failed"); }
        }
    }

    /// <summary>New read time of the latest published values, independent of the
    /// subscription cursor. No continuity or freshness beyond publication is inferred.</summary>
    public bool Snapshot(string stepId, string reason)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            try
            {
                using var parsed = JsonDocument.Parse(_snapshot());
                var (document, catalogs) = Envelope(parsed.RootElement, "snapshot");
                if (document.GetProperty("sourceId").GetString() != _source || Decimal(document, "sessionEpoch") != _epoch)
                    return Missing(stepId, reason, "stale", document);
                return SaveSnapshot(stepId, reason, document, catalogs, "unverified");
            }
            catch { return Missing(stepId, reason, "failed"); }
        }
    }

    private bool SaveSnapshot(string stepId, string reason, JsonElement document, JsonElement catalogs, string continuity)
    {
        var content = GuaTraceJson.Element(new { entries = Normalize(document.GetProperty("entries")), catalogs });
        var metadata = Metadata(document);
        var id = _trace.ObserveWithMetadata(stepId, "observe", reason, "available", Host(document), content,
            continuity, metadata: GuaTraceJson.Element(new { intervalId = _interval, publication = metadata }));
        if (id.Length == 0) { _broken = true; _brokenAvailability = "gap"; _trace.ObservationIssue("observe-storage-gap"); }
        return id.Length != 0;
    }

    private bool Break(string stepId, string reason, string availability, JsonElement? document = null)
    {
        _broken = true; _brokenAvailability = availability;
        return Missing(stepId, reason, availability, document);
    }

    private bool Missing(string stepId, string reason, string availability, JsonElement? document = null)
    {
        _trace.ObservationIssue("observe-" + availability);
        _trace.ObserveWithMetadata(stepId, "observe", reason, availability,
            document.HasValue ? Host(document.Value) : new(_source, _epoch), continuity: "unverified",
            metadata: GuaTraceJson.Element(new { intervalId = _interval,
                publication = document.HasValue ? (JsonElement?)Metadata(document.Value) : null }));
        return false;
    }

    private (JsonElement Document, JsonElement Catalogs) Envelope(JsonElement root, string kind)
    {
        var contract = Contract.Value;
        if (!contract.Schema.Evaluate(root, contract.Options).IsValid ||
            !_trace.ObservationRedactionIsUnchanged(root) ||
            !_trace.ObservationRedactionIsUnchanged(Normalize(root)))
            throw new JsonException();
        var doc = root.GetProperty("document"); var catalogs = root.GetProperty("catalogs");
        if (doc.GetProperty("schemaVersion").GetInt32() != 1 || doc.GetProperty("kind").GetString() != kind ||
            doc.GetProperty("profile").GetString() != _trace.ObservationProfile ||
            string.IsNullOrEmpty(doc.GetProperty("sourceId").GetString()) || doc.GetProperty("sessionEpoch").GetUInt64() == 0)
            throw new JsonException();
        var items = doc.GetProperty(kind == "snapshot" ? "entries" : "events");
        if (items.ValueKind != JsonValueKind.Array || catalogs.ValueKind != JsonValueKind.Array || items.GetArrayLength() != catalogs.GetArrayLength())
            throw new JsonException();
        var index = 0;
        foreach (var item in items.EnumerateArray()) {
            var paired = catalogs[index++];
            foreach (var field in new[] { "value", "before", "after" }) {
                var hasValue = item.TryGetProperty(field, out var value);
                var hasCatalog = paired.TryGetProperty(field, out var catalog);
                var isEnum = hasValue && value.TryGetProperty("enumType", out _);
                if (hasCatalog != isEnum) throw new JsonException();
                if (!isEnum) continue;
                var definition = catalog.GetProperty("enums")[0];
                if (definition.GetProperty("enumType").GetString() != value.GetProperty("enumType").GetString())
                    throw new JsonException();
                var members = definition.GetProperty("members").EnumerateArray().Select(m => m.GetString()).ToHashSet(StringComparer.Ordinal);
                var payload = value.GetProperty("value");
                if (payload.ValueKind == JsonValueKind.Array) {
                    if (payload.EnumerateArray().Any(m => !members.Contains(m.GetString()))) throw new JsonException();
                } else if (!members.Contains(payload.GetString())) throw new JsonException();
            }
        }
        // Require host references before any part of the interval is stored.
        _ = Metadata(doc);
        return (doc, catalogs);
    }

    private static string Decimal(JsonElement value, string key) => value.GetProperty(key).GetUInt64().ToString(CultureInfo.InvariantCulture);
    private static GuaTraceHost Host(JsonElement value) => new(value.GetProperty("sourceId").GetString()!,
        Decimal(value, "sessionEpoch"), Revision: Decimal(value, "revision"));
    private static JsonElement Metadata(JsonElement value) => GuaTraceJson.Element(new {
        sourceId = value.GetProperty("sourceId").GetString(), sessionEpoch = Decimal(value, "sessionEpoch"),
        sequence = Decimal(value, "sequence"), revision = Decimal(value, "revision"),
        uiFrame = Decimal(value, "uiFrame"), uiRevision = Decimal(value, "uiRevision"),
        worldFrame = Decimal(value, "worldFrame"), worldRevision = Decimal(value, "worldRevision") });

    // Convert only protocol identity/clock fields. Common Value integer payloads remain numbers.
    private static JsonElement Normalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(value, writer);
        using var parsed = JsonDocument.Parse(stream.ToArray()); return parsed.RootElement.Clone();
        static void Write(JsonElement value, Utf8JsonWriter writer)
        {
            if (value.ValueKind == JsonValueKind.Array) {
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); return;
            }
            if (value.ValueKind != JsonValueKind.Object) { value.WriteTo(writer); return; }
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject()) {
                writer.WritePropertyName(property.Name);
                if (property.Name is "sessionEpoch" or "sequence" or "revision" or "uiFrame" or "uiRevision" or
                    "worldFrame" or "worldRevision" or "ownerId" or "registrationId")
                    writer.WriteStringValue(property.Value.GetUInt64().ToString(CultureInfo.InvariantCulture));
                else Write(property.Value, writer);
            }
            writer.WriteEndObject();
        }
    }

    private void Release()
    {
        var token = _token; _token = null; _poll = null;
        try { token?.Dispose(); } catch { _trace.ObservationIssue("observe-unsubscribe-failed"); }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; Release(); } }
}
