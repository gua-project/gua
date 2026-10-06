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
        using var d = JsonDocument.Parse(c.GetObserveSnapshotJson()); var observed = d.RootElement.GetProperty("document");
        return new(observed.GetProperty("sessionEpoch").GetUInt64(), p, observed.GetProperty("uiRevision").GetUInt64(), observed.GetProperty("sourceId").GetString()!);
    }
    private static GuaDispatchGuard InputGuard(GuaRuntime r, GuaObservationProfile p = GuaObservationProfile.Debug) {
        using var d = JsonDocument.Parse(r.GetGameInputActionsJsonV2(p)); var j = d.RootElement;
        using var source = JsonDocument.Parse(r.GetObserveSnapshotJson(p));
        return new(j.GetProperty("sessionEpoch").GetUInt64(), p, j.GetProperty("revision").GetUInt64(), source.RootElement.GetProperty("sourceId").GetString()!);
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
            Assert.That(reply.GetProperty("ok").GetBoolean(), Is.False, metadata);
            Assert.That(reply.GetProperty("error").GetString(), Is.EqualTo("invalid_guard"), metadata);
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
    [TestCase(false)] [TestCase(true)]
    public async Task GuardedTransportIdMustBeCorrelatableBeforeDispatch(bool input) {
        using var r = new GuaRuntime(); Frame(r); r.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}); Map(r);
        using var c = Bridge(r); var g = input ? InputGuard(r) : UiGuard(c);
        foreach (var id in new[] {"", "\"id\":null,", "\"id\":\"1\",", "\"id\":1.5,", "\"id\":0,", "\"id\":-1,", "\"id\":2147483648,"}) {
            var payload = input ? "\"type\":\"guarded_press_game_input_action\",\"actionId\":\"jump\"" : "\"type\":\"guarded_click_node\",\"nodeId\":\"buy\"";
            var reply = await Wire(r.InspectorBridgeUrl, "{" + id + payload +
                $",\"expectedSessionEpoch\":{g.SessionEpoch},\"expectedProfile\":0,\"expectedRevision\":{g.Revision}" + "}", 0);
            Assert.That(reply.GetProperty("ok").GetBoolean(), Is.False, id);
            Assert.That(reply.GetProperty("error").GetString(), Is.EqualTo("invalid_request"), id);
            Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False);
            Assert.That(r.TryConsumeGameInput(out _), Is.False);
        }
    }
    [Test]
    public async Task GuardedInputRequiredFieldsCannotDefaultIntoHostInput() {
        using var r = new GuaRuntime(); Frame(r); r.EnableGameInput(GuaGameInputCapabilities.Semantic | GuaGameInputCapabilities.Keyboard |
            GuaGameInputCapabilities.Pointer | GuaGameInputCapabilities.Gamepad | GuaGameInputCapabilities.Text, () => {}); Map(r);
        using var c = Bridge(r); var g = InputGuard(r);
        var payloads = new[] {
            "{\"type\":\"guarded_press_game_input_action\",\"actionId\":null}",
            "{\"type\":\"guarded_set_game_input_action\",\"actionId\":\"jump\",\"value\":null}",
            "{\"type\":\"guarded_release_game_input_action\",\"actionId\":null}",
            "{\"type\":\"guarded_key_down\",\"code\":null}", "{\"type\":\"guarded_key_up\",\"code\":null}",
            "{\"type\":\"guarded_press_physical_key\",\"code\":null}",
            "{\"type\":\"guarded_pointer_move\",\"mode\":\"absolute\",\"coordinateSpace\":\"viewport_pixels\",\"x\":null,\"y\":10}",
            "{\"type\":\"guarded_pointer_move\",\"mode\":\"absolute\",\"coordinateSpace\":\"viewport_pixels\",\"x\":\"20\",\"y\":10}",
            "{\"type\":\"guarded_pointer_move\",\"mode\":\"delta\",\"x\":2}",
            "{\"type\":\"guarded_pointer_button_down\",\"button\":null}", "{\"type\":\"guarded_pointer_button_up\",\"button\":null}",
            "{\"type\":\"guarded_pointer_wheel\",\"deltaY\":null}", "{\"type\":\"guarded_pointer_wheel\"}",
            "{\"type\":\"guarded_gamepad_button_down\",\"button\":null}", "{\"type\":\"guarded_gamepad_button_up\",\"button\":null}",
            "{\"type\":\"guarded_set_gamepad_axis\",\"axis\":\"left_stick_x\"}", "{\"type\":\"guarded_set_gamepad_axis\",\"axis\":\"left_stick_x\",\"value\":\"0\"}",
            "{\"type\":\"guarded_text_input\",\"text\":null}",
            "{\"type\":\"guarded_key_down\",\"code\":\"Space\",\"leaseMs\":null}",
            "{\"type\":\"guarded_gamepad_button_down\",\"button\":\"south\",\"gamepadIndex\":null}",
            "{\"type\":\"guarded_set_game_input_action\",\"actionId\":\"jump\",\"value\":true,\"confirmed\":\"true\"}",
            "{\"type\":\"guarded_text_input\",\"text\":\"a\",\"sensitive\":null}"
        };
        foreach (var payload in payloads) {
            var fields = JsonNode.Parse(payload)!.AsObject(); fields["id"] = 1; fields["expectedSessionEpoch"] = g.SessionEpoch;
            fields["expectedProfile"] = 0; fields["expectedRevision"] = g.Revision;
            var reply = await Wire(r.InspectorBridgeUrl, fields.ToJsonString());
            Assert.That(reply.GetProperty("ok").GetBoolean(), Is.False, payload);
            Assert.That(reply.GetProperty("error").GetString(), Is.EqualTo("invalid_request"), payload);
            Assert.That(r.TryConsumeGameInput(out _), Is.False, payload);
        }
    }
    [Test]
    public async Task EmptyTextRetainsLegacyWireAndManagedGuardedCompletion() {
        using var r = new GuaRuntime(); r.EnableGameInput(GuaGameInputCapabilities.Text, () => {});
        using var c = Bridge(r); var g = UiGuard(c) with { Revision = 999 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new(r.InspectorBridgeUrl), timeout.Token);
        async Task<JsonElement> Exchange(Dictionary<string, object?> fields) {
            var json = JsonSerializer.Serialize(fields); await socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            return await ReadReply(socket, json, timeout.Token);
        }
        foreach (var guarded in new[] {false, true}) {
            var fields = new Dictionary<string, object?> { ["id"] = 1, ["type"] = guarded ? "guarded_text_input" : "text_input", ["text"] = "" };
            if (guarded) { fields["expectedSessionEpoch"] = g.SessionEpoch; fields["expectedProfile"] = 0; fields["expectedRevision"] = g.Revision; }
            var receipt = await Exchange(fields); Assert.That(receipt.GetProperty("ok").GetBoolean(), Is.True, receipt.ToString());
            var id = receipt.GetProperty("result").GetProperty("requestId").GetUInt64();
            Assert.That(r.TryConsumeGameInput(out var request), Is.True); Assert.That(request.RequestId, Is.EqualTo(id));
            Assert.That(request.ValueJson, Is.EqualTo("\"\"")); r.CompleteGameInput(request, true);
            fields.Remove("text"); fields["requestId"] = id; fields["type"] = guarded ? "guarded_poll_game_input" : "poll_game_input";
            var result = (await Exchange(fields)).GetProperty("result");
            Assert.That(result.GetProperty("requestId").GetUInt64(), Is.EqualTo(id));
            Assert.That(result.GetProperty("completed").GetBoolean(), Is.True); Assert.That(result.GetProperty("succeeded").GetBoolean(), Is.True);
        }
        using var session = c.CreateGuardedDispatchSession();
        var attempt = session.SendGameInput(g, new("text_input", "")); Assert.That(attempt.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued), attempt.Error);
        Assert.That(r.TryConsumeGameInput(out var managed), Is.True); Assert.That(managed.ValueJson, Is.EqualTo("\"\""));
        r.CompleteGameInput(managed, true); Assert.That(session.Poll(attempt).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
    }
    [TestCase(false)] [TestCase(true)]
    public async Task EscapedNulTextRetainsLegacyAndGuardedNativeValues(bool semantic) {
        using var r = new GuaRuntime(); r.EnableGameInput(GuaGameInputCapabilities.Text | GuaGameInputCapabilities.Semantic, () => {});
        r.PublishGameInputActions("play", [new("message", "Message", GuaGameInputValueType.Text)]);
        using var c = Bridge(r); var g = semantic ? InputGuard(r) : UiGuard(c) with {Revision=999};
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new(r.InspectorBridgeUrl), timeout.Token);
        foreach (var guarded in new[] {false, true}) {
            var verb = semantic ? "set_game_input_action" : "text_input";
            var fields = new Dictionary<string, object?> { ["id"] = 1, ["type"] = guarded ? "guarded_" + verb : verb, [semantic ? "value" : "text"] = "a\0b" };
            if (semantic) fields["actionId"] = "message";
            if (guarded) { fields["expectedSessionEpoch"] = g.SessionEpoch; fields["expectedProfile"] = 0; fields["expectedRevision"] = g.Revision; }
            var receipt = await Exchange(socket, fields, timeout.Token); Assert.That(receipt.GetProperty("ok").GetBoolean(), Is.True, receipt.ToString());
            var id = receipt.GetProperty("result").GetProperty("requestId").GetUInt64();
            Assert.That(r.TryConsumeGameInput(out var request), Is.True); Assert.That(request.RequestId, Is.EqualTo(id));
            using var value = JsonDocument.Parse(request.ValueJson); Assert.That(value.RootElement.GetString(), Is.EqualTo("a\0b"));
            r.CompleteGameInput(request, true);
            fields.Remove(semantic ? "value" : "text"); fields.Remove("actionId"); fields["requestId"] = id;
            fields["type"] = guarded ? "guarded_poll_game_input" : "poll_game_input";
            var result = (await Exchange(socket, fields, timeout.Token)).GetProperty("result");
            Assert.That(result.GetProperty("requestId").GetUInt64(), Is.EqualTo(id)); Assert.That(result.GetProperty("succeeded").GetBoolean(), Is.True);
        }
        using var session = c.CreateGuardedDispatchSession();
        var attempt = session.SendGameInput(g, semantic ? new("set_game_input_action", "message", Value: "a\0b") : new("text_input", "a\0b"));
        Assert.That(attempt.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued), attempt.Error);
        Assert.That(r.TryConsumeGameInput(out var managed), Is.True);
        using var managedValue = JsonDocument.Parse(managed.ValueJson); Assert.That(managedValue.RootElement.GetString(), Is.EqualTo("a\0b"));
        r.CompleteGameInput(managed, true); Assert.That(session.Poll(attempt).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
    }
    [TestCase(2147483648u)] [TestCase(uint.MaxValue)]
    public void GuardedPressKeyPreservesUnsignedModifierBits(uint modifiers) {
        using var r = new GuaRuntime(); r.BeginFrame("input"); r.RegisterNode(new("entry", "textbox", "Entry", new(0, 0, 20, 20))); r.EndFrame();
        using var c = Bridge(r); using var session = c.CreateGuardedDispatchSession();
        var attempt = session.SendUi(UiGuard(c), new(GuaActionType.PressKey, "entry", Key: "Enter", Modifiers: modifiers));
        Assert.That(attempt.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued), attempt.Error);
        Assert.That(r.TryConsumeAction(GuaActionType.PressKey, "entry", out var request), Is.True);
        Assert.That(request.Modifiers, Is.EqualTo(modifiers)); r.EmitActionResult(request, true);
        Assert.That(session.Poll(attempt).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
    }
    [Test]
    public async Task GuardedTextAcceptsOpaqueReplayReferenceLikeLegacyWire() {
        using var r = new GuaRuntime(); r.EnableGameInput(GuaGameInputCapabilities.Text, () => {});
        using var c = Bridge(r); var g = UiGuard(c) with {Revision=999};
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new(r.InspectorBridgeUrl), timeout.Token);
        foreach (var guarded in new[] {false, true}) {
            var fields = new Dictionary<string, object?> { ["id"] = 1, ["type"] = guarded ? "guarded_text_input" : "text_input", ["text"] = "safe fixture text", ["secretKey"] = "opaque-test-reference" };
            if (guarded) { fields["expectedSessionEpoch"] = g.SessionEpoch; fields["expectedProfile"] = 0; fields["expectedRevision"] = g.Revision; }
            var receipt = await Exchange(socket, fields, timeout.Token); Assert.That(receipt.GetProperty("ok").GetBoolean(), Is.True, receipt.ToString());
            var id = receipt.GetProperty("result").GetProperty("requestId").GetUInt64();
            Assert.That(r.TryConsumeGameInput(out var request), Is.True);
            using var value = JsonDocument.Parse(request.ValueJson); Assert.That(value.RootElement.GetString(), Is.EqualTo("safe fixture text"));
            r.CompleteGameInput(request, true); fields.Remove("text"); fields.Remove("secretKey"); fields["requestId"] = id;
            fields["type"] = guarded ? "guarded_poll_game_input" : "poll_game_input";
            Assert.That((await Exchange(socket, fields, timeout.Token)).GetProperty("result").GetProperty("succeeded").GetBoolean(), Is.True);
        }
        using var session = c.CreateGuardedDispatchSession();
        var attempt = session.SendGameInput(g, new("text_input", "safe fixture text", SecretKey: "opaque-test-reference"));
        Assert.That(attempt.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued), attempt.Error);
        Assert.That(r.TryConsumeGameInput(out var managed), Is.True);
        using var managedValue = JsonDocument.Parse(managed.ValueJson); Assert.That(managedValue.RootElement.GetString(), Is.EqualTo("safe fixture text"));
        r.CompleteGameInput(managed, true); Assert.That(session.Poll(attempt).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
        foreach (var invalid in new object?[] {"", null, 1}) {
            var rejected = await Exchange(socket, new {id=1,type="guarded_text_input",text="fixture",secretKey=invalid,
                expectedSessionEpoch=g.SessionEpoch,expectedProfile=0,expectedRevision=g.Revision}, timeout.Token);
            Assert.That(rejected.GetProperty("ok").GetBoolean(), Is.False); Assert.That(rejected.GetProperty("error").GetString(), Is.EqualTo("invalid_request"));
            Assert.That(r.TryConsumeGameInput(out _), Is.False);
        }
    }
    private static async Task<JsonElement> Exchange(ClientWebSocket socket, object command, CancellationToken token) {
        var json = JsonSerializer.Serialize(command); await socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, token);
        return await ReadReply(socket, json, token);
    }
    private static async Task<JsonElement> Wire(string url, string command, int expectedId = 1) {
        // Include handshake and snapshot delivery on slower macOS x64 CI runners.
        // This is a bounded one-shot exchange, never a resend after timeout.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var s = new ClientWebSocket();
        await s.ConnectAsync(new(url), timeout.Token); await s.SendAsync(Encoding.UTF8.GetBytes(command).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        return await ReadReply(s, command, timeout.Token, expectedId);
    }
    private static async Task<JsonElement> ReadReply(ClientWebSocket s, string command, CancellationToken token, int expectedId = 1) {
        while (true) {
            using var d = JsonDocument.Parse(await FaultProxy.Read(s, token));
            if (d.RootElement.TryGetProperty("id", out var id)) {
                Assert.That(id.GetInt32(), Is.EqualTo(expectedId), $"Uncorrelated reply for {command}: {d.RootElement}");
                return d.RootElement.Clone();
            }
        }
    }
    [TestCase(false, false)] [TestCase(true, false)] [TestCase(true, true)]
    public async Task PollRequiresOriginalRevisionAndKeepsResultForCorrectGuard(bool input, bool raw) {
        using var r = new GuaRuntime(); Frame(r); r.EnableGameInput(GuaGameInputCapabilities.Semantic | GuaGameInputCapabilities.Keyboard, () => {}); Map(r);
        using var c = Bridge(r); var g = input ? InputGuard(r) : UiGuard(c); if (raw) g = g with { Revision = 999 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new(r.InspectorBridgeUrl), timeout.Token);
        async Task<JsonElement> Exchange(object message) {
            var json = JsonSerializer.Serialize(message); await socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            return await ReadReply(socket, json, timeout.Token);
        }
        var command = new Dictionary<string, object?> { ["id"] = 1, ["type"] = input ? raw ? "guarded_press_physical_key" : "guarded_press_game_input_action" : "guarded_click_node",
            ["expectedSessionEpoch"] = g.SessionEpoch, ["expectedProfile"] = 0, ["expectedRevision"] = g.Revision };
        command[input ? raw ? "code" : "actionId" : "nodeId"] = input ? raw ? "Space" : "jump" : "buy";
        var receipt = await Exchange(command); Assert.That(receipt.GetProperty("ok").GetBoolean(), Is.True);
        var requestId = receipt.GetProperty("result").GetProperty("requestId").GetUInt64();
        if (input) { Assert.That(r.TryConsumeGameInput(out var q), Is.True); r.CompleteGameInput(q, true); }
        else { Assert.That(r.TryConsumeAction(GuaActionType.Click, "buy", out var q), Is.True); r.EmitActionResult(q, true); }
        if (input) {
            var legacy = await Exchange(new {id=1,type="poll_game_input",requestId});
            Assert.That(legacy.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(legacy.GetProperty("error").GetString(), Is.EqualTo("guarded_poll_required"));
        }
        command.Clear(); command["id"] = 1; command["type"] = input ? "guarded_poll_game_input" : "guarded_poll_action";
        command["requestId"] = requestId; command["expectedSessionEpoch"] = g.SessionEpoch; command["expectedProfile"] = 0; command["expectedRevision"] = g.Revision + 1;
        var wrong = await Exchange(command); Assert.That(wrong.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(wrong.GetProperty("error").GetString(), Is.EqualTo("stale_guard"));
        command["expectedRevision"] = g.Revision;
        var correct = await Exchange(command); Assert.That(correct.GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(correct.GetProperty("result").GetProperty("requestId").GetUInt64(), Is.EqualTo(requestId));
        Assert.That(correct.GetProperty("result").GetProperty("succeeded").GetBoolean(), Is.True);
    }
    [Test]
    public void SameCounterDifferentHostCannotReceiveObservedUiOrInput() {
        using var a = new GuaRuntime(); using var b = new GuaRuntime(); Frame(a); Frame(b);
        a.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}); b.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}); Map(a); Map(b);
        using var directA = Bridge(a); using var directB = Bridge(b);
        var observed = UiGuard(directA); var foreignInput = InputGuard(a); var targetGuard = UiGuard(directB);
        Assert.That(targetGuard.SessionEpoch, Is.EqualTo(observed.SessionEpoch)); Assert.That(targetGuard.Revision, Is.EqualTo(observed.Revision));
        Assert.That(InputGuard(b).Revision, Is.EqualTo(foreignInput.Revision)); Assert.That(targetGuard.SourceId, Is.Not.EqualTo(observed.SourceId));
        string target = a.InspectorBridgeUrl;
        using var proxy = new FaultProxy(target, (_, _) => false, route: () => target);
        using var original = new GuaWebSocketContext(proxy.Url); Assert.That(UiGuard(original).SourceId, Is.EqualTo(observed.SourceId));
        target = b.InspectorBridgeUrl;
        Assert.That(Assert.Throws<InvalidOperationException>(() => original.CreateGuardedDispatchSession(observed.SourceId))!.Message, Is.EqualTo("source-mismatch"));
        using var changed = new GuaWebSocketContext(proxy.Url); using var session = changed.CreateGuardedDispatchSession(targetGuard.SourceId);
        Assert.That(session.SendUi(observed, new(GuaActionType.Click, "buy")).Error, Is.EqualTo("source-mismatch"));
        Assert.That(session.SendGameInput(foreignInput, new("press_game_input_action", "jump")).Error, Is.EqualTo("source-mismatch"));
        Assert.That(proxy.Dispatches, Is.Zero); Assert.That(b.TryConsumeAction(GuaActionType.Click, "buy", out _), Is.False); Assert.That(b.TryConsumeGameInput(out _), Is.False);
        var accepted = session.SendUi(targetGuard, new(GuaActionType.Click, "buy"));
        Assert.That(accepted.State, Is.EqualTo(GuaRemoteDispatchState.Enqueued));
        Assert.That(b.TryConsumeAction(GuaActionType.Click, "buy", out var valid), Is.True); b.EmitActionResult(valid, true);
        Assert.That(session.Poll(accepted).State, Is.EqualTo(GuaRemoteDispatchState.Completed));
        Assert.That(proxy.Dispatches, Is.EqualTo(1));
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
        using var c = Bridge(r); using var s = c.CreateGuardedDispatchSession(); var g = UiGuard(c) with { Revision = 0 };
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
        var g = UiGuard(c) with { Revision = 999 };
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
        private readonly HttpListener listener = new(); private readonly CancellationTokenSource cancel = new(TimeSpan.FromSeconds(30)); private readonly Task host;
        public string Url {get;} public int Dropped, Dispatches, Polls;
        public FaultProxy(string upstream, Func<string, string, bool> drop, Func<string, string, string>? rewrite = null, Func<string>? route = null) {
            var port = Port(); Url = $"ws://127.0.0.1:{port}/"; listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
            host = Task.Run(async () => {
                var clients = new List<Task>();
                try { while (!cancel.IsCancellationRequested) {
                    var incoming = await listener.GetContextAsync().WaitAsync(cancel.Token); var target = route?.Invoke() ?? upstream;
                    clients.Add(Task.Run(async () => {
                    try { using var downstream = (await incoming.AcceptWebSocketAsync(null)).WebSocket;
                    using var remote = new ClientWebSocket(); await remote.ConnectAsync(new(target), cancel.Token);
                    while (!cancel.IsCancellationRequested) {
                        var bytes = await Read(downstream, cancel.Token); using var d = JsonDocument.Parse(bytes); var type = d.RootElement.GetProperty("type").GetString()!;
                        if (type == "guarded_click_node" || type == "guarded_press_game_input_action") Interlocked.Increment(ref Dispatches);
                        if (type == "guarded_poll_action" || type == "guarded_poll_game_input") Interlocked.Increment(ref Polls);
                        await remote.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancel.Token);
                        byte[] response;
                        while (true) {
                            response = await Read(remote, cancel.Token); using var envelope = JsonDocument.Parse(response);
                            if (envelope.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == d.RootElement.GetProperty("id").GetInt32()) break;
                        }
                        if (drop(type, Encoding.UTF8.GetString(response))) { Interlocked.Increment(ref Dropped); downstream.Abort(); break; }
                        if (rewrite != null) response = Encoding.UTF8.GetBytes(rewrite(type, Encoding.UTF8.GetString(response)));
                        await downstream.SendAsync(response.AsMemory(), WebSocketMessageType.Text, true, cancel.Token);
                    }
                    } catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
                    }));
                }
                } catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
                finally { await Task.WhenAll(clients); }
            });
        }
        public static async Task<byte[]> Read(WebSocket s, CancellationToken token) {
            using var stream = new MemoryStream(); byte[] b = new byte[8192]; WebSocketReceiveResult result;
            do { result = await s.ReceiveAsync(new ArraySegment<byte>(b), token); if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException(); stream.Write(b, 0, result.Count); } while (!result.EndOfMessage);
            return stream.ToArray();
        }
        public void Dispose() {
            cancel.Cancel();
            // Join accepts/relays before closing HttpListener response streams.
            // Linux may have already disposed a peer stream during disconnect.
            try { host.GetAwaiter().GetResult(); }
            finally {
                try { listener.Close(); } catch (ObjectDisposedException) { }
                finally { cancel.Dispose(); }
            }
        }
    }
}
