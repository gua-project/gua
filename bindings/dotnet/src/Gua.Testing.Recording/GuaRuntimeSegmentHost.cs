using System.Globalization;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;

namespace Gua.Testing.Recording;

/// <summary>Local runtime game-input path. The adapter must explicitly attest synchronous FIFO
/// application (as in the built-in Unity/Godot input pumps). The runtime alone is not that proof.</summary>
public sealed class GuaRuntimeSegmentHost : IGuaTimedSegmentValueHost
{
    private readonly GuaRuntime runtime;
    private readonly GuaObservationProfile profile;
    private readonly Func<GuaGameInputAction, bool>? confirm;
    private GuaGameInputSession? session;
    private readonly Dictionary<string, string> confirmedActions = new();
    private ulong sequence;
    private string source = "";
    private string? health;
    private long epoch;
    private ulong actionRevision;

    public GuaRuntimeSegmentHost(GuaRuntime runtime, bool adapterAppliesInOrder,
        GuaObservationProfile profile = GuaObservationProfile.Debug, Func<GuaGameInputAction, bool>? confirmation = null)
    { this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime)); OrderedApplication = adapterAppliesInOrder;
      this.profile = profile; confirm = confirmation; }
    public bool OrderedApplication { get; }
    public bool ApplicationTimes => false;
    public bool SameTickApplication => false;
    public string? SimulationScope => null;
    public double SimulationMilliseconds => throw new NotSupportedException("This host supports realtime only.");
    public ulong? OwnerId => session?.OwnerId;

    public void Begin(GuaTimedSegment segment) => BeginCore(segment, null);
    public void Begin(GuaTimedSegment segment, IReadOnlyList<JsonElement?> resolvedValues) => BeginCore(segment, resolvedValues);
    private void BeginCore(GuaTimedSegment segment, IReadOnlyList<JsonElement?>? resolvedValues)
    {
        lock (confirmedActions)
        {
            if (session is not null) throw new InvalidOperationException("This host already has an active segment.");
            if (resolvedValues is not null && resolvedValues.Count != segment.Inputs.Count)
                throw new ArgumentException("Resolved value count does not match the segment.");
            if (profile is not (GuaObservationProfile.Debug or GuaObservationProfile.Player) ||
                runtime.ObservationProfile == GuaObservationProfile.Player && profile != GuaObservationProfile.Player)
                throw new NotSupportedException("Segment profile exceeds the runtime observation ceiling.");
            confirmedActions.Clear();
            var capabilities = runtime.GetGameInputCapabilities(profile);
            foreach (var input in segment.Inputs)
                if (Required(input.Kind) is var needed && needed != GuaGameInputCapabilities.None && (capabilities & needed) != needed)
                    throw new NotSupportedException("Input capability is not initialized/authorized.");
            if (!runtime.SupportsGuardedGameInput) throw new NotSupportedException("Native guarded input is unavailable.");
            using var startTree = JsonDocument.Parse(runtime.GetUiTreeJson());
            epoch = startTree.RootElement.GetProperty("sessionEpoch").GetInt64();
            actionRevision = segment.Inputs.Any(input => input.Kind == GuaGameInputKind.Semantic && input.Operation != GuaGameInputOperation.Release)
                ? runtime.FindGameInputActionsV2(new(Limit: 1), profile).Revision : 0;
            for (var i = 0; i < segment.Inputs.Count; i++)
            {
                var input = segment.Inputs[i];
                var confirmed = false;
                var required = Required(input.Kind);
                if (required != GuaGameInputCapabilities.None && (capabilities & required) != required) throw new NotSupportedException("Input capability is not initialized/authorized.");
                if (input.Kind == GuaGameInputKind.Semantic && input.Operation != GuaGameInputOperation.Release)
                {
                    var action = Find(input.Target);
                    if (input.SemanticValueType is { } type && action.ValueType != (type switch
                        { GuaGameInputValueType.Button => "button", GuaGameInputValueType.Axis1D => "axis1d", GuaGameInputValueType.Vector2 => "vector2", _ => "text" }))
                        throw new InvalidOperationException("Semantic value type declaration does not match the action.");
                    if (input.Operation == GuaGameInputOperation.Set && action.ValueType == "text" && input.SemanticValueType != GuaGameInputValueType.Text)
                        throw new InvalidOperationException("Text Set requires an explicit stateless Text value type.");
                    if (input.Operation == GuaGameInputOperation.Set && action.ValueType == "button" && !action.Holdable)
                        throw new InvalidOperationException("Segment Set requires a holdable action.");
                    if (input.Operation == GuaGameInputOperation.Press && action.ValueType != "button")
                        throw new InvalidOperationException("Segment Press requires a button action.");
                    if (action.RequiresConfirmation && (confirm is null || !confirm(action)))
                        throw new InvalidOperationException("Fresh confirmation was declined or unavailable.");
                    confirmed = action.RequiresConfirmation;
                    confirmedActions[input.Target] = JsonSerializer.Serialize(action);
                }
                if (!input.Sensitive || resolvedValues is not null)
                {
                    try
                    {
                        runtime.ValidateGameInput(profile, checked((ulong)epoch), actionRevision, input.Kind, input.Operation,
                            input.Target, resolvedValues is null ? input.Value : resolvedValues[i],
                            input.LeaseMilliseconds == 0 ? null : TimeSpan.FromMilliseconds(input.LeaseMilliseconds),
                            input.X, input.Y, input.DeviceIndex, input.Sensitive, confirmed);
                    }
                    catch { throw new InvalidDataException("Input payload violates the current native action contract."); }
                }
            }
            using var tree = JsonDocument.Parse(runtime.GetUiTreeJson());
            if (epoch != tree.RootElement.GetProperty("sessionEpoch").GetInt64())
                throw new InvalidOperationException("Segment session changed during preflight.");
            health = null;
            using var diagnostics = JsonDocument.Parse(runtime.GetDiagnosticsJson());
            var journal = diagnostics.RootElement.GetProperty("traceLifecycle");
            source = journal.GetProperty("sourceId").GetString()!;
            sequence = Ulong(journal.GetProperty("lastSequence"));
            session = runtime.CreateGameInputSession(profile);
        }
    }

    public ulong Send(GuaTimedInput input, JsonElement? secret, Action verifySendBoundary)
    {
        var owner = session ?? throw new InvalidOperationException("No active segment.");
        CheckEpoch();
        if (Required(input.Kind) != GuaGameInputCapabilities.None && (runtime.GetGameInputCapabilities(profile) & Required(input.Kind)) == 0)
            throw new NotSupportedException("Input capability was revoked.");
        var confirmed = false;
        if (input.Kind == GuaGameInputKind.Semantic && input.Operation != GuaGameInputOperation.Release)
        {
            var current = Find(input.Target);
            if (!confirmedActions.TryGetValue(input.Target, out var expected) || expected != JsonSerializer.Serialize(current))
                throw new InvalidOperationException("Action map changed since segment preflight.");
            confirmed = current.RequiresConfirmation;
        }
        return owner.SendGuarded(checked((ulong)epoch), actionRevision, input.Kind, input.Operation, input.Target, input.Sensitive ? secret : input.Value,
            input.LeaseMilliseconds == 0 ? null : TimeSpan.FromMilliseconds(input.LeaseMilliseconds),
            input.X, input.Y, input.DeviceIndex, input.Sensitive, confirmed, verifySendBoundary);
    }
    public GuaTimedCompletion? Poll(ulong requestId)
    {
        var result = session!.PollResult(requestId);
        return result.Completed ? new(result.Succeeded == true, result.ErrorCode ?? 0) : null;
    }
    public ulong ReleaseAll() => session!.Send(GuaGameInputKind.Cleanup, GuaGameInputOperation.ReleaseAll, "");
    public bool IsNeutral
    {
        get { using var document = JsonDocument.Parse(session!.GetStateJson()); return document.RootElement.GetProperty("held").GetArrayLength() == 0; }
    }
    public string? ExecutionFailureCode
    {
        get
        {
            try
            {
                CheckEpoch();
                using var document = JsonDocument.Parse(runtime.GetDiagnosticsJson());
                var journal = document.RootElement.GetProperty("traceLifecycle");
                if (journal.GetProperty("sourceId").GetString() != source) return health = "lifecycle-source-changed";
                var latest = Ulong(journal.GetProperty("lastSequence"));
                var facts = journal.GetProperty("events").EnumerateArray().Where(item => Ulong(item.GetProperty("sequence")) > sequence).ToArray();
                if (latest > sequence && (facts.Length == 0 || Ulong(facts[0].GetProperty("sequence")) != sequence + 1))
                    health = "lifecycle-evidence-gap";
                foreach (var fact in facts)
                    if (Ulong(fact.GetProperty("ownerId")) == session!.OwnerId && fact.GetProperty("phase").GetString() == "lease-expired")
                        health = "lease-expired-before-release";
                sequence = latest;
                return health;
            }
            catch { return health = "lifecycle-or-session-unconfirmed"; }
        }
    }
    public void End()
    {
        lock (confirmedActions) { session?.Dispose(); session = null; confirmedActions.Clear(); }
    }
    private void CheckEpoch()
    {
        using var document = JsonDocument.Parse(runtime.GetUiTreeJson());
        if (document.RootElement.GetProperty("sessionEpoch").GetInt64() != epoch)
            throw new InvalidOperationException("Segment session changed.");
    }
    private GuaGameInputAction Find(string target)
    {
        var result = runtime.FindGameInputActionsV2(new(Id: target), profile);
        if (result.Revision != actionRevision || result.SessionEpoch != checked((ulong)epoch))
            throw new InvalidOperationException("Action map or session changed since segment preflight.");
        var action = result.Actions.SingleOrDefault();
        return action is { Active: true } ? action : throw new InvalidOperationException("Action is no longer active/published.");
    }
    private static ulong Ulong(JsonElement value) => ulong.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    private static GuaGameInputCapabilities Required(GuaGameInputKind kind) => kind switch
    {
        GuaGameInputKind.Semantic => GuaGameInputCapabilities.Semantic,
        GuaGameInputKind.Keyboard => GuaGameInputCapabilities.Keyboard,
        GuaGameInputKind.Pointer => GuaGameInputCapabilities.Pointer,
        GuaGameInputKind.Gamepad => GuaGameInputCapabilities.Gamepad,
        GuaGameInputKind.TextInput => GuaGameInputCapabilities.Text,
        GuaGameInputKind.Cleanup => GuaGameInputCapabilities.None,
        _ => throw new NotSupportedException("Unsupported segment input kind."),
    };
}
