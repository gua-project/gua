using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[TestFixture, NonParallelizable]
public sealed class ObserveTransportTests
{
    private static JsonElement Parse(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    private static JsonElement Document(string json) => Parse(json).GetProperty("document");
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void World(GuaRuntime r, bool second = true, bool visible = true)
    {
        r.BeginWorldFrame("test");
        r.RegisterWorldObject(new("enemy-1", "enemy", "One", GuaWorldSpace.World2D, new(2, 3), VisibleToPlayer: visible));
        if (second) r.RegisterWorldObject(new("enemy-2", "enemy", "Two", GuaWorldSpace.World2D, new(8, 9), VisibleToPlayer: true));
        r.EndWorldFrame();
    }
    [Test]
    public void RuntimeToRealRemoteClientRetainsTypesCatalogsAndIndependentOwners()
    {
        using var r = new GuaRuntime(); r.EnableWorldObjectTreeAdapter(); World(r);
        using var catalog = new GuaEnumCatalog(); catalog.Register("game.Phase", "First", "Second");
        using var one = r.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
        using var two = r.CreateObserveOwner(GuaObserveSource.Object, "enemy-2");
        string phase = "First";
        using var a = one.Observe("phase", () => GuaValue.Enum("game.Phase", phase, catalog));
        using var b = two.Observe("phase", () => GuaValue.String("Other"));
        using var empty = one.Observe("inventory", () => GuaValue.Collection(GuaValueType.Set, GuaValueType.Enum, Array.Empty<GuaValue>(), "game.Phase", catalog));
        World(r);
        int port = Port(); Assert.That(r.StartInspectorBridge(port), Is.True);
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        using var first = remote.SubscribeObservations(); using var second = remote.SubscribeObservations();
        var snapshot = Parse(first.SnapshotJson); var entries = snapshot.GetProperty("document").GetProperty("entries");
        Assert.That(entries.GetArrayLength(), Is.EqualTo(3));
        Assert.That(entries[0].GetProperty("runtimeId").GetString(), Is.EqualTo("enemy-1"));
        Assert.That(entries[1].GetProperty("runtimeId").GetString(), Is.EqualTo("enemy-2"));
        Assert.That(entries[2].GetProperty("value").GetProperty("elementType").GetString(), Is.EqualTo("enum"));
        Assert.That(snapshot.GetProperty("catalogs")[2].GetProperty("value").GetProperty("enums")[0].GetProperty("members").GetArrayLength(), Is.EqualTo(2));
        var world = Parse(r.GetWorldObjectTreeJson());
        Assert.That(world.GetProperty("objects")[0].GetProperty("position").GetProperty("x").GetDouble(), Is.EqualTo(2));
        Assert.That(entries.EnumerateArray().Select(e => e.GetProperty("name").GetString()), Does.Not.Contain("worldPosition"));
        phase = "Second"; World(r);
        Assert.That(Document(first.PollJson()).GetProperty("events")[0].GetProperty("after").GetProperty("value").GetString(), Is.EqualTo("Second"));
        Assert.That(Document(second.PollJson()).GetProperty("events").GetArrayLength(), Is.EqualTo(1));
        Assert.That(Document(first.PollJson()).GetProperty("events").GetArrayLength(), Is.Zero);
        World(r, second: false);
        Assert.That(Document(first.PollJson()).GetProperty("events")[0].GetProperty("kind").GetString(), Is.EqualTo("removed"));
        World(r); using var reborn = r.CreateObserveOwner(GuaObserveSource.Object, "enemy-2");
        using var replacement = reborn.Observe("phase", () => GuaValue.String("New")); World(r);
        Assert.That(remote.GetObserveSnapshotJson(), Does.Contain("New").And.Not.Contain("Other"));
    }
    [Test]
    public void GetterFailureGapAndResetRemainVisibleThroughTransport()
    {
        using var r = new GuaRuntime(); using var owner = r.CreateObserveOwner(GuaObserveSource.World);
        int value = 0; bool fail = false;
        using var item = owner.Property("phase", () => fail ? throw new Exception("SECRET_MARKER") : GuaValue.Integer(value));
        item.Notify(); int port = Port(); Assert.That(r.StartInspectorBridge(port), Is.True);
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}"); using var sub = remote.SubscribeObservations();
        fail = true; item.Notify();
        string changes = sub.PollJson(); Assert.That(changes, Does.Not.Contain("SECRET_MARKER"));
        var failed = Document(changes).GetProperty("events")[0];
        Assert.That(failed.GetProperty("afterError").GetInt32(), Is.EqualTo(100)); Assert.That(failed.TryGetProperty("after", out _), Is.False);
        fail = false; item.Notify(); Assert.That(sub.PollJson(), Does.Contain("recovered"));
        r.SetObserveHistoryLimits(1);
        value = 1; item.Notify(); value = 2; item.Notify();
        Assert.That(Document(sub.PollJson()).GetProperty("status").GetString(), Is.EqualTo("gap"));
        Assert.That(Document(sub.PollJson()).GetProperty("status").GetString(), Is.EqualTo("gap"));
        using var fresh = remote.SubscribeObservations();
        Assert.That(Document(fresh.SnapshotJson).GetProperty("entries")[0].GetProperty("value").GetProperty("value").GetInt32(), Is.EqualTo(2));
        remote.Reset(); Assert.That(Document(fresh.PollJson()).GetProperty("status").GetString(), Is.EqualTo("stale_session"));
    }
    [Test]
    public void PlayerCannotReadPrivateValuesOrTheirCandidatesAndRevocationDropsHistory()
    {
        using var r = new GuaRuntime(); r.SetObservationProfile(GuaObservationProfile.Player); r.EnableWorldObjectTreeAdapter(); World(r);
        using var owner = r.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
        using var cat = new GuaEnumCatalog(); cat.Register("game.SECRET_TYPE", "SECRET_MARKER");
        using var hidden = owner.Observe("secret", () => GuaValue.Enum("game.SECRET_TYPE", "SECRET_MARKER", cat));
        using var sensitive = owner.Observe("sensitive", () => GuaValue.Enum("game.SECRET_TYPE", "SECRET_MARKER", cat), allowPlayer: true, sensitive: true);
        using var visible = owner.Observe("phase", () => GuaValue.String("First"), allowPlayer: true);
        World(r); int port = Port(); Assert.That(r.StartInspectorBridge(port), Is.True);
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}"); using var sub = remote.SubscribeObservations();
        Assert.That(sub.SnapshotJson, Does.Not.Contain("SECRET"));
        Assert.That(Document(sub.SnapshotJson).GetProperty("profile").GetString(), Is.EqualTo("player"));
        World(r, visible: false); string changes = sub.PollJson();
        Assert.That(Document(changes).GetProperty("status").GetString(), Is.EqualTo("gap"));
        Assert.That(changes, Does.Not.Contain("First").And.Not.Contain("SECRET"));
        Assert.That(Document(remote.GetObserveSnapshotJson()).GetProperty("entries").GetArrayLength(), Is.Zero);
    }
    [Test]
    public void TransportOwnersBoundSubscriptionsAndRejectOtherClients()
    {
        using var r = new GuaRuntime();
        using var one = r.CreateObserveClient(); using var two = r.CreateObserveClient(GuaObservationProfile.Player);
        ulong id = Parse(one.CommandJson(2)).GetProperty("subscriptionId").GetUInt64();
        Assert.That(Assert.Throws<GuaObserveException>(() => two.CommandJson(3, id))!.Code, Is.EqualTo(2));
        for (int i = 1; i < 64; i++) one.CommandJson(2);
        Assert.That(Assert.Throws<GuaObserveException>(() => one.CommandJson(2))!.Code, Is.EqualTo(1));
        one.CommandJson(4, id); Assert.DoesNotThrow(() => one.CommandJson(2));
        one.Dispose(); Assert.Throws<ObjectDisposedException>(() => one.CommandJson(3, id));
    }
    [TestCase(false), TestCase(true)]
    public void RuntimeGetterMayDisposeRuntimeWithoutUseAfterFree(bool frame)
    {
        var r = new GuaRuntime(); using var owner = r.CreateObserveOwner(GuaObserveSource.World);
        using var item = owner.Property("dispose", () => { r.Dispose(); return GuaValue.Integer(1); });
        if (frame) { r.BeginWorldFrame("test"); Assert.DoesNotThrow(r.EndWorldFrame); }
        else Assert.DoesNotThrow(item.Notify);
    }
    [Test]
    public async Task WebSocketRejectsMalformedObserveMetadataWithoutLeakingInput()
    {
        using var r = new GuaRuntime(); int port = Port(); Assert.That(r.StartInspectorBridge(port), Is.True);
        using var socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
        foreach (string command in new[] {
            "{\"id\":1,\"type\":\"get_observe_snapshot\",\"profile\":\"SECRET_MARKER\"}",
            "{\"id\":1,\"type\":\"subscribe_observations\",\"id\":2}",
            "{\"id\":1,\"type\":\"poll_observations\",\"subscriptionId\":0}",
            "{\"id\":1,\"type\":\"poll_observations\",\"subscriptionId\":1.00000000000000001}",
            "{\"id\":1,\"type\":\"poll_observations\",\"subscriptionId\":\"SECRET_MARKER\"}" }) {
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(command)), WebSocketMessageType.Text, true, CancellationToken.None);
            while (true) {
                byte[] bytes = new byte[65536]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token);
                string response = Encoding.UTF8.GetString(bytes, 0, result.Count); var root = Parse(response);
                if (!root.TryGetProperty("id", out _)) continue;
                Assert.That(root.GetProperty("ok").GetBoolean(), Is.False); Assert.That(response, Does.Not.Contain("SECRET_MARKER")); break;
            }
        }
    }
    [Test]
    public void ExplicitSubscribeRejectionPreservesExistingConnectionAndTokens()
    {
        using var r = new GuaRuntime(); int port = Port(); Assert.That(r.StartInspectorBridge(port), Is.True);
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        var tokens = Enumerable.Range(0, 64).Select(_ => remote.SubscribeObservations()).ToArray();
        try {
            Assert.Catch<InvalidOperationException>(() => remote.SubscribeObservations());
            Assert.DoesNotThrow(() => tokens[0].PollJson());
            tokens[0].Dispose(); using var replacement = remote.SubscribeObservations();
            Assert.DoesNotThrow(() => replacement.PollJson());
        } finally { foreach (var token in tokens) token.Dispose(); }
    }
    [Test]
    public async Task LostSubscribeReplyDropsOwnerSocketAndAllowsReconnect()
    {
        using var runtime = new GuaRuntime(); int port = Port();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = Task.Run(async () => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var first = await listener.GetContextAsync();
            using var socket = (await first.AcceptWebSocketAsync(null)).WebSocket;
            byte[] bytes = new byte[65536];
            var received = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            var request = Parse(Encoding.UTF8.GetString(bytes, 0, received.Count));
            await socket.SendAsync(Encoding.UTF8.GetBytes($"{{\"id\":{request.GetProperty("id")},\"ok\":true,\"result\":{runtime.GetVersionJson()}}}").AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            received = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            Assert.That(Parse(Encoding.UTF8.GetString(bytes, 0, received.Count)).GetProperty("type").GetString(), Is.EqualTo("subscribe_observations"));
            try { var end = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token); closed.SetResult(end.MessageType == WebSocketMessageType.Close); }
            catch (WebSocketException) { closed.SetResult(true); }
            var next = await listener.GetContextAsync();
            using var again = (await next.AcceptWebSocketAsync(null)).WebSocket;
            received = await again.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            request = Parse(Encoding.UTF8.GetString(bytes, 0, received.Count));
            await again.SendAsync(Encoding.UTF8.GetBytes($"{{\"id\":{request.GetProperty("id")},\"ok\":true,\"result\":{runtime.GetVersionJson()}}}").AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        });
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}/", TimeSpan.FromMilliseconds(300));
        Assert.Catch<OperationCanceledException>(() => remote.SubscribeObservations());
        Assert.That(await closed.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.True);
        Assert.That(remote.GetVersion().Capabilities, Does.Contain("observe_v1"));
        await host;
    }
}
