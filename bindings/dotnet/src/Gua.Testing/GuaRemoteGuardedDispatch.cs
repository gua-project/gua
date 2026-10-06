using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

public sealed record GuaDispatchGuard(ulong SessionEpoch, GuaObservationProfile Profile, ulong Revision);
public enum GuaRemoteDispatchState { Rejected, Enqueued, Completed, Uncertain }
/// <summary>An uncertain receipt or poll is terminal and never retried. Enqueued is not execution.</summary>
public sealed class GuaRemoteDispatchAttempt
{
    internal GuaRemoteDispatchAttempt(GuaDispatchGuard guard, bool ui, string? nodeId)
    { Guard = guard; IsUi = ui; NodeId = nodeId; }
    internal bool IsUi { get; }
    internal string? NodeId { get; }
    internal GuaActionType? UiAction { get; set; }
    internal GuaRemoteGuardedSession? Owner { get; set; }
    public GuaDispatchGuard Guard { get; }
    public ulong? RequestId { get; internal set; }
    public GuaRemoteDispatchState State { get; internal set; }
    public string? Error { get; internal set; }
    public JsonElement? Completion { get; internal set; }
}

/// <summary>A dedicated connection owns each session, its UI requests and held game inputs.
/// Dispose disconnects that owner. An uncertain send/poll poisons the session.</summary>
public sealed class GuaRemoteGuardedSession : IDisposable
{
    private readonly GuaWebSocketContext context;
    private readonly long generation;
    private bool closed;
    private readonly object gate = new();
    internal GuaRemoteGuardedSession(GuaWebSocketContext context, long generation)
    { this.context = context; this.generation = generation; }
    /// <summary>Profile-projected Action Map with its sessionEpoch and revision for semantic guards.</summary>
    public string GetGameInputActionsJson()
    {
        lock (gate) {
            if (closed) throw new ObjectDisposedException(nameof(GuaRemoteGuardedSession));
            return context.GuardedRequest(new { type = "get_game_input_actions" }, generation);
        }
    }
    public GuaRemoteDispatchAttempt SendUi(GuaDispatchGuard guard, GuaActionRequest request, Action? beforeDispatch = null)
    {
        var type = request.Action switch {
            GuaActionType.Click => "click_node", GuaActionType.Focus => "focus_node",
            GuaActionType.SetValue => "set_value", GuaActionType.SetChecked => "set_checked",
            GuaActionType.Select => "select", GuaActionType.Scroll => "scroll",
            GuaActionType.PressKey => "press_key", _ => throw new ArgumentOutOfRangeException(nameof(request)) };
        var attempt = Send(guard, true, request.NodeId, new Dictionary<string, object?> {
            ["type"] = "guarded_" + type, ["nodeId"] = request.NodeId, ["value"] = request.Value,
            ["deltaX"] = request.DeltaX, ["deltaY"] = request.DeltaY, ["checked"] = request.BoolValue,
            ["key"] = request.Key, ["modifiers"] = request.Modifiers, ["sensitive"] = request.Sensitive,
            ["scrollUnit"] = request.ScrollUnit }, beforeDispatch);
        attempt.UiAction = request.Action;
        return attempt;
    }
    /// <summary>Narrow existing input verbs only; no reset, cleanup or general command tunnel.</summary>
    public GuaRemoteDispatchAttempt SendGameInput(GuaDispatchGuard guard, GuaRemoteGameInputRequest request, Action? beforeDispatch = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var fields = new Dictionary<string, object?> { ["type"] = "guarded_" + request.Command,
            ["value"] = request.Value, ["leaseMs"] = request.LeaseMs, ["x"] = request.X, ["y"] = request.Y,
            ["gamepadIndex"] = request.DeviceIndex, ["sensitive"] = request.Sensitive, ["confirmed"] = request.Confirmed };
        switch (request.Command) {
            case "press_game_input_action": case "set_game_input_action": case "release_game_input_action":
                fields["actionId"] = request.Target; break;
            case "key_down": case "key_up": case "press_physical_key": fields["code"] = request.Target; break;
            case "pointer_button_down": case "pointer_button_up":
            case "gamepad_button_down": case "gamepad_button_up": fields["button"] = request.Target; break;
            case "set_gamepad_axis": fields["axis"] = request.Target; break;
            case "pointer_wheel": fields["wheelUnit"] = request.Target; break;
            case "pointer_move": fields["mode"] = request.Target;
                if (request.Target != "delta") fields["coordinateSpace"] = request.CoordinateSpace;
                break;
            case "text_input": fields["text"] = request.Target; break;
            default: throw new ArgumentException("Unsupported guarded input verb.", nameof(request));
        }
        return Send(guard, false, null, fields, beforeDispatch);
    }
    private GuaRemoteDispatchAttempt Send(GuaDispatchGuard guard, bool ui, string? nodeId, Dictionary<string, object?> fields, Action? beforeDispatch)
    {
        if (guard is null || guard.SessionEpoch == 0 || (guard.Profile != GuaObservationProfile.Debug && guard.Profile != GuaObservationProfile.Player))
            throw new ArgumentException("A nonzero epoch and valid authoritative profile are required.", nameof(guard));
        fields["expectedSessionEpoch"] = guard.SessionEpoch; fields["expectedProfile"] = (int)guard.Profile;
        fields["expectedRevision"] = guard.Revision;
        // Serialize before dispatch: local marshalling errors cannot be uncertain host execution.
        using var command = JsonDocument.Parse(JsonSerializer.Serialize(fields));
        lock (gate) {
            if (closed) throw new ObjectDisposedException(nameof(GuaRemoteGuardedSession));
            var attempt = new GuaRemoteDispatchAttempt(guard, ui, nodeId) { Owner = this };
            try { beforeDispatch?.Invoke(); }
            catch { attempt.State = GuaRemoteDispatchState.Rejected; attempt.Error = "pre-dispatch-rejected"; return attempt; }
            try {
                using var receipt = JsonDocument.Parse(context.GuardedRequest(command.RootElement, generation));
                var id = receipt.RootElement.GetProperty("requestId").GetUInt64();
                if (id == 0) throw new JsonException();
                attempt.RequestId = id; attempt.State = GuaRemoteDispatchState.Enqueued;
            } catch (GuaRemoteDispatchRejectedException error) {
                attempt.State = GuaRemoteDispatchState.Rejected; attempt.Error = error.Message;
            } catch {
                attempt.State = GuaRemoteDispatchState.Uncertain; attempt.Error = "dispatch-unconfirmed";
                Close();
            }
            return attempt;
        }
    }
    public GuaRemoteDispatchAttempt Poll(GuaRemoteDispatchAttempt attempt)
    {
        if (attempt is null || attempt.Owner != this) throw new ArgumentException("Request belongs to another session.", nameof(attempt));
        lock (gate) {
            if (attempt.State != GuaRemoteDispatchState.Enqueued) return attempt;
            if (closed) throw new ObjectDisposedException(nameof(GuaRemoteGuardedSession));
            try {
                using var reply = JsonDocument.Parse(context.GuardedRequest(new {
                    type = attempt.IsUi ? "guarded_poll_action" : "guarded_poll_game_input", requestId = attempt.RequestId,
                    expectedSessionEpoch = attempt.Guard.SessionEpoch, expectedProfile = (int)attempt.Guard.Profile,
                    expectedRevision = attempt.Guard.Revision }, generation));
                var root = reply.RootElement;
                if (root.ValueKind == JsonValueKind.Null) {
                    if (!attempt.IsUi) throw new JsonException();
                    return attempt;
                }
                if (root.GetProperty("sessionEpoch").GetUInt64() != attempt.Guard.SessionEpoch) throw new JsonException();
                if (!attempt.IsUi && !root.GetProperty("completed").GetBoolean()) return attempt;
                if (root.GetProperty("requestId").GetUInt64() != attempt.RequestId ||
                    root.GetProperty("sessionEpoch").GetUInt64() != attempt.Guard.SessionEpoch ||
                    (attempt.IsUi && (root.GetProperty("nodeId").GetString() != (attempt.NodeId ?? "") ||
                        root.GetProperty("action").GetInt32() != (int)attempt.UiAction!))) throw new JsonException();
                _ = root.GetProperty("succeeded").GetBoolean();
                _ = root.GetProperty(attempt.IsUi ? "error" : "errorCode").GetInt32();
                if (attempt.IsUi) {
                    _ = root.GetProperty("frameSequence").GetUInt64();
                    _ = root.GetProperty("revision").GetUInt64();
                    _ = root.GetProperty("sensitive").GetBoolean();
                    if (root.GetProperty("value").ValueKind != JsonValueKind.String) throw new JsonException();
                }
                attempt.Completion = root.Clone(); attempt.State = GuaRemoteDispatchState.Completed;
            } catch (GuaRemoteDispatchRejectedException error) {
                // Rejection after enqueue does not prove nonexecution.
                attempt.State = GuaRemoteDispatchState.Uncertain; attempt.Error = error.Message; Close();
            } catch {
                attempt.State = GuaRemoteDispatchState.Uncertain; attempt.Error = "completion-unconfirmed"; Close();
            }
            return attempt;
        }
    }
    private void Close() { closed = true; context.Dispose(); }
    public void Dispose() { lock (gate) Close(); }
}

public sealed record GuaRemoteGameInputRequest(string Command, string Target, object? Value = null,
    uint LeaseMs = 5000, double X = 0, double Y = 0, int DeviceIndex = 0, bool Sensitive = false,
    bool Confirmed = false, string CoordinateSpace = "viewport_pixels");
internal sealed class GuaRemoteDispatchRejectedException(string? message) : InvalidOperationException(message);

public sealed partial class GuaWebSocketContext
{
    public GuaRemoteGuardedSession CreateGuardedDispatchSession()
    {
        var owned = new GuaWebSocketContext(uri.AbsoluteUri, requestTimeout);
        try {
            owned.GetVersion().EnsureCompatible(requiredCapabilities: ["guarded_dispatch_v1"]);
            return new GuaRemoteGuardedSession(owned, owned.connectionGeneration);
        } catch { owned.Dispose(); throw; }
    }
    internal string GuardedRequest(object command, long generation)
    {
        try { return Raw(command, observeGeneration: generation); }
        catch (RemoteCommandRejectedException error) { throw new GuaRemoteDispatchRejectedException(error.Message); }
    }
}
