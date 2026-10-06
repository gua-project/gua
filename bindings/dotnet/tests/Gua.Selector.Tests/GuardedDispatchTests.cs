using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[TestFixture, NonParallelizable]
public sealed class GuardedDispatchTests
{
    private static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private static void Frame(GuaRuntime r, string label = "Buy", bool privateNode = false) {
        r.BeginFrame("shop"); r.RegisterNode(new("buy", "button", label, new(0, 0, 20, 20),
            AgentPolicy: privateNode ? new(Exposure: GuaAgentExposure.Private) : null)); r.EndFrame();
    }
    private static void Map(GuaRuntime r, string description = "Jump") => r.PublishGameInputActions("play", [new("jump", description, GuaGameInputValueType.Button)]);
    private static GuaDispatchGuard UiGuard(GuaWebSocketContext c, GuaObservationProfile p = GuaObservationProfile.Debug) {
        var s = c.GetContextStatus(); return new(s.SessionEpoch, p, s.Revision);
    }
    private static GuaDispatchGuard InputGuard(GuaRuntime r, GuaObservationProfile p = GuaObservationProfile.Debug) {
        using var d = JsonDocument.Parse(r.GetGameInputActionsJsonV2(p)); var j = d.RootElement;
        return new(j.GetProperty("sessionEpoch").GetUInt64(), p, j.GetProperty("revision").GetUInt64());
    }
    private static GuaWebSocketContext Bridge(GuaRuntime r) { var port = Port(); Assert.That(r.StartInspectorBridge(port), Is.True); return new($"ws://127.0.0.1:{port}"); }
    [Test]
    public void ClientDeadlineGuardRejectsAfterMarshallingBeforeDispatch() {
        using var r = new GuaRuntime(); Frame(r); using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession();
        var a = s.SendUi(UiGuard(c), new(GuaActionType.Click, "buy"), () => throw new OperationCanceledException());
        Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Rejected)); Assert.That(a.Error, Is.EqualTo("pre-dispatch-rejected"));
        Assert.That(a.RequestId, Is.Null); Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
    }
    [Test]
    public async Task WireRejectsMalformedGuardsAndOtherConnectionCannotPollUiResult() {
        using var r = new GuaRuntime(); Frame(r); using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession();
        var g = UiGuard(c);
        foreach (var metadata in new[] {
            "\"expectedSessionEpoch\":0,\"expectedRevision\":1,\"expectedProfile\":0",
            "\"expectedSessionEpoch\":1,\"expectedRevision\":1.5,\"expectedProfile\":0",
            "\"expectedSessionEpoch\":1,\"expectedRevision\":1,\"expectedProfile\":-1",
            "\"expectedSessionEpoch\":1,\"expectedRevision\":1,\"expectedProfile\":0,\"expectedProfile\":1",
            "\"expectedSessionEpoch\":1,\"expectedRevision\":18446744073709551616,\"expectedProfile\":0",
            "\"value\":{\"expectedSessionEpoch\":1,\"expectedRevision\":1,\"expectedProfile\":0}"
        }) {
            var reply = await Wire(r.InspectorBridgeUrl, "{\"id\":1,\"type\":\"guarded_click_node\",\"nodeId\":\"buy\"," + metadata + "}");
            Assert.That(reply.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
        }
        var a = s.SendUi(g, new(GuaActionType.Click, "buy"));
        Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out var consumed), Is.True); r.EmitActionResult(consumed, true);
        var stolen = await Wire(r.InspectorBridgeUrl, JsonSerializer.Serialize(new {id = 1, type = "guarded_poll_action", requestId = a.RequestId,
            expectedSessionEpoch = g.SessionEpoch, expectedProfile = (int)g.Profile, expectedRevision = g.Revision}));
        Assert.That(stolen.GetProperty("ok").GetBoolean(), Is.True); Assert.That(stolen.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(s.Poll(a).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
    }
    [Test]
    public async Task GuardedUiRequiresTypedVerbPayloadBeforeEnqueue() {
        using var r = new GuaRuntime();
        r.BeginFrame("shop"); r.RegisterNode(new("buy", "checkbox", "Buy", new(0, 0, 20, 20), Checked: true)); r.EndFrame();
        using var c = Bridge(r);
        var g = UiGuard(c);
        foreach (var payload in new[] {
            "\"type\":\"guarded_set_checked\",\"nodeId\":\"buy\"",
            "\"type\":\"guarded_set_checked\",\"nodeId\":\"buy\",\"checked\":null",
            "\"type\":\"guarded_set_checked\",\"nodeId\":\"buy\",\"checked\":\"false\"",
            "\"type\":\"guarded_set_value\",\"nodeId\":\"buy\"",
            "\"type\":\"guarded_set_value\",\"nodeId\":\"buy\",\"value\":42",
            "\"type\":\"guarded_select\",\"nodeId\":\"buy\"",
            "\"type\":\"guarded_select\",\"nodeId\":\"buy\",\"value\":\"\"",
            "\"type\":\"guarded_scroll\",\"nodeId\":\"buy\"",
            "\"type\":\"guarded_scroll\",\"nodeId\":\"buy\",\"deltaX\":0",
            "\"type\":\"guarded_scroll\",\"nodeId\":\"buy\",\"deltaX\":0,\"deltaY\":\"1\"",
            "\"type\":\"guarded_press_key\",\"key\":null",
            "\"type\":\"guarded_click_node\",\"nodeId\":null"
        }) {
            var reply = await Wire(r.InspectorBridgeUrl, "{\"id\":1," + payload +
                $",\"expectedSessionEpoch\":{g.SessionEpoch},\"expectedProfile\":0,\"expectedRevision\":{g.Revision}" + "}");
            Assert.That(reply.GetProperty("ok").GetBoolean(), Is.False, payload);
            Assert.That(reply.GetProperty("error").GetString(), Is.EqualTo("invalid_request"), payload);
            foreach (var action in new[] {GuaActionType.SetChecked, GuaActionType.SetValue, GuaActionType.Select, GuaActionType.Scroll, GuaActionType.PressKey, GuaActionType.Click})
                Assert.That(r.TryConsumeAction(action, "buy", out _), Is.False, payload);
        }
        using var session = c.CreateGuardedDispatchSession();
        var valid = session.SendUi(g, new(GuaActionType.SetChecked, "buy", BoolValue: false));
        Assert.That(valid.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued));
        Assert.That(r.TryConsumeAction(GuaActionType.SetChecked, "buy", out var checkedRequest), Is.True);
        Assert.That(checkedRequest.BoolValue, Is.False); r.EmitActionResult(checkedRequest, true);
        Assert.That(session.Poll(valid).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
    }
    private static async Task<JsonElement> Wire(string url, string command) {
        // Include handshake and snapshot delivery on slower macOS x64 CI runners.
        // This is a bounded one-shot exchange, never a resend after timeout.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var s = new ClientWebSocket();
        await s.ConnectAsync(new(url), timeout.Token); await s.SendAsync(Encoding.UTF8.GetBytes(command).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        while (true) {
            using var d = JsonDocument.Parse(await FaultProxy.Read(s, timeout.Token));
            if (d.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 1) return d.RootElement.Clone();
        }
    }
    [Test]
    public void MissingCapabilityRejectsNewManagedSessionWithoutDispatch() {
        using var r = new GuaRuntime(); Frame(r); using var c = Bridge(r);
        using var proxy = new FaultProxy(r.InspectorBridgeUrl, (_, _) => false,
            (type, reply) => type == "get_version" ? reply.Replace("\"guarded_dispatch_v1\",", "") : reply);
        using var remote = new GuaWebSocketContext(proxy.Url);
        Assert.Throws<GuaCompatibilityException>(() => remote.CreateGuardedDispatchSession());
        Assert.That(proxy.Dispatches, Is.Zero); Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
    }
    [Test]
    public void UiEnqueueGuardsAndConsumeRaceHaveNoSideEffectsAndOwnedOneShotCompletion() {
        using var r = new GuaRuntime(); Frame(r); using var remote = Bridge(r);
        using var session = remote.CreateGuardedDispatchSession(); using var other = remote.CreateGuardedDispatchSession();
        var g = UiGuard(remote); var action = new GuaActionRequest(GuaActionType.Click, "buy");
        foreach (var stale in new[] {g with {SessionEpoch = g.SessionEpoch + 1}, g with {Revision = g.Revision + 1}, g with {Profile = GuaObservationProfile.Player}}) {
            var rejected = session.SendUi(stale, action); Assert.That(rejected.State, Is.EqualTo(GuaRemoteDispatchState.Rejected));
            Assert.That(rejected.Error, Does.Contain("stale_guard")); Assert.That(rejected.RequestId, Is.Null);
            Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
        }
        var attempt = session.SendUi(g, action); Assert.That(attempt.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued));
        Frame(r, "Changed"); Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
        Assert.That(remote.TryPollActionEvent(attempt.RequestId!.Value, out _), Is.False);
        Assert.Throws<ArgumentException>(() => other.Poll(attempt));
        Assert.That(session.Poll(attempt).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
        Assert.That(attempt.Completion!.Value.GetProperty("succeeded").GetBoolean(), Is.False);
        Assert.That(attempt.Completion.Value.GetProperty("error").GetInt32(), Is.EqualTo(-7));
        Assert.That(session.Poll(attempt), Is.SameAs(attempt));
        var legacy = new GuaActionRequest(GuaActionType.Click, "buy");
        Assert.That(remote.EnqueueAction(legacy, out var legacyId), Is.EqualTo(GuaActionError.None));
        Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out var consumed), Is.True); r.EmitActionResult(consumed, true);
        Assert.That(remote.TryPollActionEvent(legacyId, out var e), Is.True); Assert.That(e.RequestId, Is.EqualTo(legacyId));
    }
    [Test]
    public void UiCompletionRetainsCorrelationAndResetCannotReuseOldGuard() {
        using var r = new GuaRuntime(); Frame(r); using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession();
        var guard = UiGuard(c); var a = s.SendUi(guard, new(GuaActionType.Click, "buy"));
        Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out var request), Is.True); r.EmitActionResult(request, true);
        Assert.That(s.Poll(a).Completion!.Value.GetProperty("requestId").GetUInt64(), Is.EqualTo(a.RequestId));
        var pending = s.SendUi(guard, new(GuaActionType.Click, "buy"));
        c.Reset(new() {Strict = false}); Frame(r);
        Assert.That(s.SendUi(guard, new(GuaActionType.Click, "buy")).State, Is.EqualTo(GuaRemoteDispatchState.Rejected));
        Assert.That(s.Poll(pending).State, Is.EqualTo(GuaRemoteDispatchState.Uncertain));
    }
    [Test]
    public void PlayerPrivateAndNonexistentTargetsHaveIdenticalRejection() {
        using var r = new GuaRuntime(); r.SetObservationProfile(GuaObservationProfile.Player); Frame(r, privateNode: true);
        using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession(); var guard = UiGuard(c, GuaObservationProfile.Player);
        var hidden = s.SendUi(guard, new(GuaActionType.Click, "buy")); var missing = s.SendUi(guard, new(GuaActionType.Click, "absent"));
        Assert.That(hidden.State, Is.EqualTo(GuaRemoteDispatchState.Rejected)); Assert.That(hidden.Error, Is.EqualTo(missing.Error));
        Assert.That(hidden.Error, Does.Contain("node_not_found")); Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
    }
    [Test]
    public void PlayerPrivateAndNonexistentInputActionsHaveIdenticalRejection() {
        using var r = new GuaRuntime(); r.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}, GuaGameInputCapabilities.Semantic);
        r.SetObservationProfile(GuaObservationProfile.Player);
        r.PublishGameInputActions("play", [new("jump", "Private", GuaGameInputValueType.Button, AgentExposure: GuaAgentExposure.Private)]);
        using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession(); var g = InputGuard(r, GuaObservationProfile.Player);
        Assert.That(s.GetGameInputActionsJson(), Does.Not.Contain("Private"));
        var hidden = s.SendGameInput(g, new("press_game_input_action", "jump"));
        var missing = s.SendGameInput(g, new("press_game_input_action", "absent"));
        Assert.That(hidden.State, Is.EqualTo(GuaRemoteDispatchState.Rejected)); Assert.That(hidden.Error, Is.EqualTo(missing.Error));
        Assert.That(hidden.Error, Does.Contain("action_not_found")); Assert.That(r.TryConsumeGameInput(out _), Is.False);
    }
    [Test]
    public void SemanticStaleMapAndEpochRejectBeforeHostAndRaceReportsNativeFailure() {
        using var r = new GuaRuntime(); r.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}); Map(r);
        using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession(); var g = InputGuard(r);
        var input = new GuaRemoteGameInputRequest("press_game_input_action", "jump");
        foreach (var stale in new[] {g with {SessionEpoch = g.SessionEpoch + 1}, g with {Revision = g.Revision + 1}, g with {Profile = GuaObservationProfile.Player}}) {
            var a = s.SendGameInput(stale, input); Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Rejected)); Assert.That(a.RequestId, Is.Null);
            Assert.That(r.TryConsumeGameInput(out _), Is.False);
        }
        var pending = s.SendGameInput(g, input); Assert.That(pending.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued));
        Map(r, "Changed"); Assert.That(r.TryConsumeGameInput(out _), Is.False);
        Assert.That(s.Poll(pending).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
        Assert.That(pending.Completion!.Value.GetProperty("succeeded").GetBoolean(), Is.False);
        Assert.That(pending.Completion.Value.GetProperty("errorCode").GetInt32(), Is.EqualTo(-1));
    }
    [Test]
    public void PointerAndGamepadValuesUseTheExistingNativeContract() {
        using var r = new GuaRuntime(); r.EnableGameInput(GuaGameInputCapabilities.Pointer | GuaGameInputCapabilities.Gamepad, () => {});
        using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession(); var g = new GuaDispatchGuard(c.GetContextStatus().SessionEpoch, GuaObservationProfile.Debug, 0);
        foreach (var input in new[] {new GuaRemoteGameInputRequest("pointer_move", "absolute", X: 20, Y: 30),
            new("pointer_move", "delta", X: 2, Y: -3), new("pointer_wheel", "lines", X: 1, Y: -2),
            new("set_gamepad_axis", "left_stick_x", Value: 0.5, DeviceIndex: 2)}) {
            var a = s.SendGameInput(g, input); Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued), a.Error);
            Assert.That(r.TryConsumeGameInput(out var q), Is.True);
            Assert.That(q.X, Is.EqualTo(input.X)); Assert.That(q.Y, Is.EqualTo(input.Y)); Assert.That(q.DeviceIndex, Is.EqualTo(input.DeviceIndex));
            if (input.Command == "set_gamepad_axis") Assert.That(q.ValueJson, Is.EqualTo("0.5"));
            r.CompleteGameInput(q, true); Assert.That(s.Poll(a).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
        }
    }
    [TestCase(false, "succeeded")] [TestCase(true, "succeeded")]
    [TestCase(false, "error")] [TestCase(true, "errorCode")]
    [TestCase(false, "frameSequence")] [TestCase(false, "revision")]
    [TestCase(false, "requestId")] [TestCase(true, "sessionEpoch")]
    public void MalformedRealCompletionPoisonsSessionWithoutRepoll(bool input, string field) {
        using var r = new GuaRuntime(); Frame(r); r.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}); Map(r); using var direct = Bridge(r);
        int faults = 0;
        using var proxy = new FaultProxy(r.InspectorBridgeUrl, (type, _) => {
            if (type == "guarded_click_node") { Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out var q), Is.True); r.EmitActionResult(q, true); }
            if (type == "guarded_press_game_input_action") { Assert.That(r.TryConsumeGameInput(out var q), Is.True); r.CompleteGameInput(q, true); }
            return false;
        }, (type, reply) => {
            if (type != "guarded_poll_action" && type != "guarded_poll_game_input") return reply;
            var root = JsonNode.Parse(reply)!; Assert.That(root["ok"]!.GetValue<bool>(), Is.True);
            Assert.That(root["result"]!.AsObject().Remove(field), Is.True); faults++; return root.ToJsonString();
        });
        using var c = new GuaWebSocketContext(proxy.Url); using var s = c.CreateGuardedDispatchSession();
        var a = input ? s.SendGameInput(InputGuard(r), new("press_game_input_action", "jump")) : s.SendUi(UiGuard(direct), new(GuaActionType.Click, "buy"));
        Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued)); s.Poll(a);
        Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Uncertain)); Assert.That(a.Completion, Is.Null); s.Poll(a);
        Assert.Throws<ObjectDisposedException>(() => s.SendUi(UiGuard(direct), new(GuaActionType.Click, "buy")));
        Assert.That(faults, Is.EqualTo(1)); Assert.That(proxy.Dispatches, Is.EqualTo(1)); Assert.That(proxy.Polls, Is.EqualTo(1));
    }
    [Test]
    public void RawInputOwnerCleanupDoesNotReleaseAnotherOwnerAndCapabilityLossRejectsConsume() {
        using var r = new GuaRuntime(); r.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => {});
        using var c = Bridge(r); using var first = c.CreateGuardedDispatchSession(); using var second = c.CreateGuardedDispatchSession();
        var g = new GuaDispatchGuard(c.GetContextStatus().SessionEpoch, GuaObservationProfile.Debug, 999);
        var a = first.SendGameInput(g, new("key_down", "KeyA")); Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued));
        Assert.That(r.TryConsumeGameInput(out var one), Is.True); r.CompleteGameInput(one, true); Assert.That(first.Poll(a).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
        var b = second.SendGameInput(g, new("key_down", "KeyB")); Assert.That(r.TryConsumeGameInput(out var two), Is.True); r.CompleteGameInput(two, true);
        first.Dispose(); GuaGameInputRequest? cleanup = null;
        Assert.That(SpinWait.SpinUntil(() => r.TryConsumeGameInput(out cleanup!), TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(cleanup!.Kind, Is.EqualTo(GuaGameInputKind.Cleanup)); Assert.That(cleanup.OwnerId, Is.EqualTo(one.OwnerId));
        Assert.That(cleanup.OwnerId, Is.Not.EqualTo(two.OwnerId)); r.CompleteGameInput(cleanup, true);
        Assert.That(second.Poll(b).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
        var denied = second.SendGameInput(g, new("press_physical_key", "KeyC")); r.EnableGameInput(GuaGameInputCapabilities.None, () => {});
        Assert.That(r.TryConsumeGameInput(out _), Is.False); Assert.That(second.Poll(denied).Completion!.Value.GetProperty("errorCode").GetInt32(), Is.EqualTo(-4));
    }
    [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
    public void ActualNativeResponseLossIsTerminalWithoutResendOrRepoll(bool poll, bool gameInput) {
        using var r = new GuaRuntime(); Frame(r); using var direct = Bridge(r);
        if (gameInput) { r.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}); Map(r); }
        var send = gameInput ? "guarded_press_game_input_action" : "guarded_click_node";
        var polling = gameInput ? "guarded_poll_game_input" : "guarded_poll_action";
        using var proxy = new FaultProxy(r.InspectorBridgeUrl, (type, response) => {
            if (type == send) {
                if (gameInput) { Assert.That(r.TryConsumeGameInput(out var q), Is.True); r.CompleteGameInput(q, true); }
                else { Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out var q), Is.True); r.EmitActionResult(q, true); }
            }
            return type == (poll ? polling : send);
        });
        using var c = new GuaWebSocketContext(proxy.Url, TimeSpan.FromMilliseconds(500)); using var s = c.CreateGuardedDispatchSession();
        var a = gameInput ? s.SendGameInput(InputGuard(r), new("press_game_input_action", "jump")) : s.SendUi(UiGuard(direct), new(GuaActionType.Click, "buy"));
        if (poll) { Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued)); s.Poll(a); }
        Assert.That(a.State, Is.EqualTo(GuaRemoteDispatchState.Uncertain)); s.Poll(a);
        Assert.Throws<ObjectDisposedException>(() => s.SendUi(UiGuard(direct), new(GuaActionType.Click, "buy")));
        Assert.That(proxy.Dropped, Is.EqualTo(1)); Assert.That(proxy.Dispatches, Is.EqualTo(1)); Assert.That(proxy.Polls, Is.EqualTo(poll ? 1 : 0));
        Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
        Assert.That(r.TryConsumeGameInput(out _), Is.False);
    }
    private sealed class FaultProxy : IDisposable {
        private readonly HttpListener listener = new(); private readonly CancellationTokenSource cancel = new(TimeSpan.FromSeconds(15)); private readonly Task host;
        public string Url {get;} public int Dropped, Dispatches, Polls;
        public FaultProxy(string upstream, Func<string, string, bool> drop, Func<string, string, string>? rewrite = null) {
            var port = Port(); Url = $"ws://127.0.0.1:{port}/"; listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            host = Task.Run(async () => {
                try { var incoming = await listener.GetContextAsync(); using var downstream = (await incoming.AcceptWebSocketAsync(null)).WebSocket;
                    using var remote = new ClientWebSocket(); await remote.ConnectAsync(new(upstream), cancel.Token);
                    while (!cancel.IsCancellationRequested) {
                        var bytes = await Read(downstream, cancel.Token); using var d = JsonDocument.Parse(bytes); var type = d.RootElement.GetProperty("type").GetString()!;
                        if (type == "guarded_click_node" || type == "guarded_press_game_input_action") Dispatches++;
                        if (type == "guarded_poll_action" || type == "guarded_poll_game_input") Polls++;
                        await remote.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancel.Token);
                        byte[] response;
                        while (true) {
                            response = await Read(remote, cancel.Token); using var envelope = JsonDocument.Parse(response);
                            if (envelope.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == d.RootElement.GetProperty("id").GetInt32()) break;
                        }
                        if (drop(type, Encoding.UTF8.GetString(response))) { Dropped++; downstream.Abort(); break; }
                        if (rewrite != null) response = Encoding.UTF8.GetBytes(rewrite(type, Encoding.UTF8.GetString(response)));
                        await downstream.SendAsync(response.AsMemory(), WebSocketMessageType.Text, true, cancel.Token);
                    }
                } catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            });
        }
        public static async Task<byte[]> Read(WebSocket s, CancellationToken token) {
            using var stream = new MemoryStream(); byte[] b = new byte[8192]; WebSocketReceiveResult result;
            do { result = await s.ReceiveAsync(new ArraySegment<byte>(b), token); if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException(); stream.Write(b, 0, result.Count); } while (!result.EndOfMessage);
            return stream.ToArray();
        }
        public void Dispose() { cancel.Cancel(); listener.Close(); host.GetAwaiter().GetResult(); cancel.Dispose(); }
    }
}
