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
public sealed class TraceObserveTests
{
    private string _root = null!;
    [SetUp] public void Setup() => _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "trace-observe", Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private GuaTraceOptions Options(string profile = "debug") => new() { OutputDirectory = _root, Profile = profile, SavePolicy = GuaTraceSavePolicy.Always };
    private static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private static JsonElement Received(GuaTraceEvent e) => e.Data.GetProperty("received");

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedSubscribeClosesItsOwningConnection(bool lostReply)
    {
        using var runtime = new GuaRuntime(); int port = Port();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = Task.Run(async () => {
            using var socket = (await (await listener.GetContextAsync()).AcceptWebSocketAsync(null)).WebSocket;
            try {
                while (true) {
                    byte[] bytes = new byte[65536]; var frame = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    using var request = JsonDocument.Parse(bytes.AsMemory(0, frame.Count));
                    bool subscribe = request.RootElement.GetProperty("type").GetString() == "subscribe_observations";
                    if (subscribe && lostReply) continue;
                    var reply = $"{{\"id\":{request.RootElement.GetProperty("id")},\"ok\":true,\"result\":{(subscribe ? "{}" : runtime.GetVersionJson())}}}";
                    await socket.SendAsync(Encoding.UTF8.GetBytes(reply).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                }
            } catch (WebSocketException) { }
        });
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}/", TimeSpan.FromMilliseconds(200));
        Assert.Catch(() => remote.SubscribeObservations());
        // The context is still live; a failed request must already release its host cursor owner.
        await host.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task SnapshotRejectsOlderPublicationWithoutAdvancingIndependentCursor()
    {
        using var context = new GuaContext(); using var owner = context.CreateObserveOwner(GuaObserveSource.World);
        int number = 1; using var value = owner.Property("count", () => GuaValue.Integer(number)); value.Notify();
        string snapshot = context.GetObserveSnapshotTransportJson();
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Mark, "snapshot-sequence");
        using var capture = GuaTraceObservations.Subscribe(trace, step, () => context.SubscribeObservations(), () => snapshot);
        number = 2; value.Notify(); Assert.That(capture.Poll(step), Is.True);
        Assert.That(capture.Snapshot(step, "old-publication"), Is.False);
        snapshot = context.GetObserveSnapshotTransportJson();
        Assert.That(capture.Snapshot(step, "same-publication"), Is.True);
        number = 3; value.Notify(); snapshot = context.GetObserveSnapshotTransportJson();
        Assert.That(capture.Snapshot(step, "ahead-publication"), Is.True);
        Assert.That(capture.Poll(step), Is.True);
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var stale = read.Events.Single(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString() == "old-publication");
        Assert.That(stale.Data.GetProperty("availability").GetString(), Is.EqualTo("stale"));
        Assert.That(stale.Data.TryGetProperty("blob", out _), Is.False);
        Assert.That(read.Events.Count(e => e.Type == "observation.change"), Is.EqualTo(2));
    }

    [TestCase("snapshot-entry")]
    [TestCase("snapshot-catalog")]
    [TestCase("change-kind")]
    [TestCase("change-catalog")]
    [TestCase("snapshot-enum-missing")]
    [TestCase("snapshot-enum-type")]
    [TestCase("snapshot-enum-member")]
    [TestCase("snapshot-empty-enum-type")]
    [TestCase("change-enum-type")]
    public async Task MalformedRemotePayloadNeverRetainsPartialInterval(string defect)
    {
        using var runtime = new GuaRuntime();
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        using var catalog = new GuaEnumCatalog(); catalog.Register("game.Phase", "First", "Second", "Third");
        int number = 1; using var value = owner.Property("count", () => defect.Contains("enum")
            ? defect.Contains("empty") ? GuaValue.FromJson("{\"type\":\"list\",\"elementType\":\"enum\",\"enumType\":\"game.Phase\",\"value\":[]}", catalog)
                : GuaValue.Enum("game.Phase", number == 1 ? "First" : number == 2 ? "Second" : "Third", catalog)
            : GuaValue.Integer(number)); value.Notify();
        int port = Port(); using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = Task.Run(async () => {
            var connection = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var socket = (await connection.AcceptWebSocketAsync(null)).WebSocket;
            using var client = runtime.CreateObserveClient();
            try {
                while (true) {
                    byte[] bytes = new byte[65536]; var received = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    var request = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 0, received.Count))!;
                    var type = request["type"]!.GetValue<string>();
                    string result = type switch {
                        "get_version" => runtime.GetVersionJson(),
                        "subscribe_observations" => client.CommandJson(2),
                        "poll_observations" => client.CommandJson(3, request["subscriptionId"]!.GetValue<ulong>()),
                        "unsubscribe_observations" => client.CommandJson(4, request["subscriptionId"]!.GetValue<ulong>()),
                        _ => throw new InvalidOperationException()
                    };
                    var payload = JsonNode.Parse(result)!;
                    if (type == "subscribe_observations" && defect.StartsWith("snapshot")) {
                        var snapshot = payload["snapshot"]!;
                        if (defect == "snapshot-entry") snapshot["document"]!["entries"]![0] = new JsonObject();
                        else if (defect == "snapshot-catalog") snapshot["catalogs"]![0]!["value"] = new JsonObject { ["enums"] = new JsonArray(new JsonObject()) };
                        else if (defect == "snapshot-enum-missing") snapshot["catalogs"]![0]!.AsObject().Remove("value");
                        else if (defect == "snapshot-enum-member") snapshot["catalogs"]![0]!["value"]!["enums"]![0]!["members"] = new JsonArray("Different");
                        else snapshot["catalogs"]![0]!["value"]!["enums"]![0]!["enumType"] = "game.Other";
                    }
                    if (type == "poll_observations") {
                        // A valid first event cannot be retained before a malformed second event is checked.
                        if (defect == "change-kind") payload["document"]!["events"]![1]!["kind"] = "unsupported";
                        else if (defect == "change-enum-type") payload["catalogs"]![1]!["after"]!["enums"]![0]!["enumType"] = "game.Other";
                        else payload["catalogs"]![1]!["unexpected"] = true;
                    }
                    var reply = new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = true, ["result"] = payload };
                    await socket.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                }
            } catch (WebSocketException) { }
        });
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Mark, "invalid-payload");
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}/", TimeSpan.FromSeconds(2));
        using var capture = GuaTraceObservations.Subscribe(trace, step, remote);
        if (defect.StartsWith("change")) { number = 2; value.Notify(); number = 3; value.Notify(); Assert.That(capture.Poll(step), Is.False); }
        Assert.That(trace.Status.Issues, Does.Contain("observe-failed"));
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Any(e => e.Type == "observation.change"), Is.False);
        if (defect.StartsWith("snapshot")) Assert.That(read.Blobs, Is.Empty);
        capture.Dispose(); remote.Dispose(); await host.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RedactionCannotInvalidateTypedObservePayload(bool structural)
    {
        using var context = new GuaContext(); using var owner = context.CreateObserveOwner(GuaObserveSource.World);
        using var catalog = new GuaEnumCatalog(); catalog.Register("game.Phase", "SecretFirst", "SecretSecond");
        string phase = "SecretFirst"; using var value = owner.Property("phase", () => GuaValue.Enum("game.Phase", phase, catalog)); value.Notify();
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, SavePolicy = GuaTraceSavePolicy.Always,
            Secrets = structural ? new[] { "changed" } : new[] { "SecretFirst", "SecretSecond" } });
        var step = trace.BeginStep(GuaTraceStepKind.Mark, "redaction");
        using var capture = GuaTraceObservations.Subscribe(trace, step, context);
        phase = "SecretSecond"; value.Notify(); Assert.That(capture.Poll(step), Is.False);
        Assert.That(trace.Status.Issues, Does.Contain("observe-failed"));
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Any(e => e.Type == "observation.change"), Is.False);
        if (!structural) {
            Assert.That(read.Blobs, Is.Empty);
            var artifact = string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.That(artifact, Does.Not.Contain("SecretFirst").And.Not.Contain("SecretSecond"));
        }
    }

    [TestCase("rejection")]
    [TestCase("timeout")]
    [TestCase("malformed-success")]
    public async Task FailedUnsubscribeClosesConnectionOwner(string failure)
    {
        using var runtime = new GuaRuntime(); int port = Port();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var host = Task.Run(async () => {
            var connection = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var socket = (await connection.AcceptWebSocketAsync(null)).WebSocket;
            using var client = runtime.CreateObserveClient();
            try {
                while (true) {
                    byte[] bytes = new byte[65536]; var received = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    var request = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 0, received.Count))!;
                    bool unsubscribe = request["type"]!.GetValue<string>() == "unsubscribe_observations";
                    if (unsubscribe && failure == "timeout") continue;
                    var reply = unsubscribe
                        ? failure == "malformed-success"
                            ? new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = true, ["result"] = new JsonObject() }
                            : new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = false, ["error"] = "rejected" }
                        : new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = true,
                            ["result"] = JsonNode.Parse(request["type"]!.GetValue<string>() == "get_version" ? runtime.GetVersionJson() : client.CommandJson(2)) };
                    await socket.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                }
            } catch (WebSocketException) { }
        });
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Mark, "unsubscribe");
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}/", TimeSpan.FromMilliseconds(500));
        var capture = GuaTraceObservations.Subscribe(trace, step, remote);
        capture.Dispose();
        Assert.That(trace.Status.Issues, Does.Contain("observe-unsubscribe-failed"));
        // Disposal must close the owning socket even while the context remains live.
        await host.WaitAsync(TimeSpan.FromSeconds(3));
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RealLocalAndRemotePreserveIntermediateEnumValuesAndIndependentCursor(bool remote)
    {
        using var runtime = new GuaRuntime();
        using var catalog = new GuaEnumCatalog(); catalog.Register("game.Phase", "First", "Second", "Third");
        using var owner = runtime.CreateObserveOwner(GuaObserveSource.World);
        string phase = "First";
        using var registration = owner.Property("phase", () => GuaValue.Enum("game.Phase", phase, catalog));
        registration.Notify();
        using var independent = runtime.SubscribeObservations();
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Action, "transition");
        int port = Port(); Assert.That(runtime.StartInspectorBridge(port), Is.True);
        using var socket = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        using var capture = remote ? GuaTraceObservations.Subscribe(trace, step, socket) :
            GuaTraceObservations.Subscribe(trace, step, () => runtime.SubscribeObservations(), () => runtime.GetObserveSnapshotTransportJson());
        phase = "Second"; registration.Notify(); phase = "Third"; registration.Notify();
        Assert.That(capture.Poll(step, "input-complete"), Is.True);
        Assert.That(capture.Snapshot(step, "main-result"), Is.True);
        Assert.That(independent.PollJson(), Does.Contain("Second").And.Contain("Third"));
        trace.SetPrimaryOutcome(GuaTraceOutcome.Failed);
        phase = "First"; registration.Notify(); Assert.That(capture.Poll(step, "after-cleanup"), Is.True);
        Assert.That(capture.Snapshot(step, "after-cleanup"), Is.True);
        trace.EndStep(step, GuaTraceOutcome.Failed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var changes = read.Events.Where(e => e.Type == "observation.change").ToArray();
        Assert.That(changes.Select(e => Received(e).GetProperty("after").GetProperty("value").GetString()), Is.EqualTo(new[] { "Second", "Third", "First" }));
        Assert.That(changes[0].Data.GetProperty("catalogs").GetProperty("after").GetProperty("enums")[0].GetProperty("members").GetArrayLength(), Is.EqualTo(3));
        Assert.That(Received(changes[0]).GetProperty("sequence").ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(Received(changes[0]).GetProperty("worldFrame").ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("failed"));
        var result = read.Events.Single(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString() == "main-result");
        Assert.That(read.Blobs[result.Data.GetProperty("blob").GetString()!].GetProperty("entries")[0].GetProperty("value").GetProperty("value").GetString(), Is.EqualTo("Third"));
        Assert.That(read.Issues, Is.Empty);
    }

    [Test]
    public async Task GetterFailureRemovalAndReregistrationNeverFabricateNull()
    {
        using var context = new GuaContext(); using var owner = context.CreateObserveOwner(GuaObserveSource.World);
        bool fail = false; using var value = owner.Property("phase", () => fail ? throw new Exception("SECRET_ERROR") : GuaValue.String("First"));
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Action, "lifetime");
        using var capture = GuaTraceObservations.Subscribe(trace, step, context);
        value.Notify(); fail = true; value.Notify(); value.Dispose();
        using var replacement = owner.Property("phase", () => GuaValue.String("New")); replacement.Notify();
        Assert.That(capture.Poll(step), Is.True); trace.EndStep(step, GuaTraceOutcome.Passed);
        await trace.CompleteAsync(GuaTraceOutcome.Failed); var read = GuaTraceReader.Read(trace.ArtifactPath);
        var changes = read.Events.Where(e => e.Type == "observation.change").Select(Received).ToArray();
        Assert.That(changes.Select(e => e.GetProperty("kind").GetString()), Is.EqualTo(new[] { "added", "unavailable", "removed", "unavailable", "added" }));
        Assert.That(changes[0].TryGetProperty("before", out _), Is.False);
        Assert.That(changes[1].TryGetProperty("after", out _), Is.False);
        Assert.That(changes[2].GetProperty("beforeError").GetInt32(), Is.EqualTo(100));
        Assert.That(changes[2].TryGetProperty("before", out _), Is.False);
        Assert.That(changes[3].GetProperty("afterError").GetInt32(), Is.EqualTo(101));
        Assert.That(changes[0].GetProperty("registrationId").GetString(), Is.Not.EqualTo(changes[4].GetProperty("registrationId").GetString()));
        Assert.That(string.Join("", read.Events.Select(e => e.Data.GetRawText())), Does.Not.Contain("SECRET_ERROR"));
    }

    [Test]
    public async Task ObjectRemovalAndSameRuntimeIdRecreationKeepDistinctLifetimes()
    {
        using var c = new GuaContext();
        void World(bool present) {
            c.BeginWorldFrame("test");
            if (present) c.RegisterWorldObject(new("enemy", "enemy", "Enemy", GuaWorldSpace.World2D, new(1, 2)));
            c.EndWorldFrame();
        }
        World(true); using var owner = c.CreateObserveOwner(GuaObserveSource.Object, "enemy");
        using var phase = owner.Observe("phase", () => GuaValue.String("Old")); phase.Notify();
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Action, "respawn");
        using var capture = GuaTraceObservations.Subscribe(trace, step, c);
        World(false); Assert.That(capture.Poll(step), Is.True);
        World(true); using var reborn = c.CreateObserveOwner(GuaObserveSource.Object, "enemy");
        using var replacement = reborn.Observe("phase", () => GuaValue.String("New")); replacement.Notify();
        Assert.That(capture.Poll(step), Is.True); Assert.That(capture.Snapshot(step, "main-result"), Is.True);
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var changes = read.Events.Where(e => e.Type == "observation.change").Select(Received).ToArray();
        Assert.That(changes.Select(e => e.GetProperty("kind").GetString()), Is.EqualTo(new[] { "removed", "unavailable", "added" }));
        Assert.That(changes[0].GetProperty("before").GetProperty("value").GetString(), Is.EqualTo("Old"));
        Assert.That(changes[2].GetProperty("after").GetProperty("value").GetString(), Is.EqualTo("New"));
        Assert.That(changes[0].GetProperty("ownerId").GetString(), Is.Not.EqualTo(changes[2].GetProperty("ownerId").GetString()));
        Assert.That(changes.Select(e => e.GetProperty("runtimeId").GetString()).Distinct(), Is.EqualTo(new[] { "enemy" }));
    }

    [Test]
    public async Task GapResubscribeAndResetRemainMissingAfterFreshSnapshot()
    {
        using var c = new GuaContext(); using var owner = c.CreateObserveOwner(GuaObserveSource.World);
        int number = 0; using var value = owner.Property("number", () => GuaValue.Integer(number)); value.Notify(); c.SetObserveHistoryLimits(1);
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Action, "overflow");
        using var capture = GuaTraceObservations.Subscribe(trace, step, c);
        number = 1; value.Notify(); number = 2; value.Notify();
        Assert.That(capture.Poll(step), Is.False); Assert.That(capture.Snapshot(step, "wait-end"), Is.True);
        Assert.That(capture.Poll(step), Is.False); Assert.That(capture.Resubscribe(step), Is.True);
        number = 3; value.Notify(); Assert.That(capture.Poll(step), Is.True);
        c.Reset(); Assert.That(capture.Poll(step, "first-stale"), Is.False);
        Assert.That(capture.Poll(step, "repeated-stale"), Is.False);
        Assert.That(capture.Snapshot(step, "after-cleanup"), Is.False);
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("observe-gap").And.Contain("observe-stale"));
        Assert.That(read.Events.Count(e => e.Type == "observation.change"), Is.EqualTo(1));
        Assert.That(read.Events.Single(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString() == "repeated-stale")
            .Data.GetProperty("availability").GetString(), Is.EqualTo("stale"));
        Assert.That(read.Events.Where(e => e.Type == "observation" && e.Data.GetProperty("availability").GetString() == "gap").All(e => !e.Data.TryGetProperty("blob", out _)), Is.True);
    }

    [Test]
    public async Task FailedCursorRetainsFailureUntilExplicitResubscribe()
    {
        var c = new GuaContext();
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Mark, "disconnect");
        using var capture = GuaTraceObservations.Subscribe(trace, step, c);
        c.Dispose(); Assert.That(capture.Poll(step, "first-failure"), Is.False);
        Assert.That(capture.Poll(step, "repeated-failure"), Is.False);
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Where(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString()!.Contains("failure"))
            .Select(e => e.Data.GetProperty("availability").GetString()), Is.EqualTo(new[] { "failed", "failed" }));
        Assert.That(read.Manifest.Quality.Issues, Does.Not.Contain("observe-gap"));
    }

    [Test]
    public async Task IdenticalContentsAcrossPublicationsDeduplicateButKeepReadTimesAndFrames()
    {
        using var c = new GuaContext(); using var owner = c.CreateObserveOwner(GuaObserveSource.World);
        using var value = owner.Property("constant", () => GuaValue.Integer(5)); value.Notify();
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Mark, "reads");
        using var capture = GuaTraceObservations.Subscribe(trace, step, c);
        c.BeginWorldFrame("test"); c.EndWorldFrame(); Assert.That(capture.Snapshot(step, "wait-end"), Is.True);
        c.BeginWorldFrame("test"); c.EndWorldFrame(); Assert.That(capture.Snapshot(step, "main-result"), Is.True);
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Blobs.Count, Is.EqualTo(1));
        var observations = read.Events.Where(e => e.Type == "observation").ToArray();
        Assert.That(observations.Select(e => e.Data.GetProperty("observationId").GetString()).Distinct().Count(), Is.EqualTo(3));
        Assert.That(observations.Select(e => e.Data.GetProperty("metadata").GetProperty("publication").GetProperty("worldFrame").GetString()), Is.EqualTo(new[] { "0", "1", "2" }));
    }

    [Test]
    public async Task PlayerProfileSensitiveCatalogsAndMismatchedRemoteCannotLeak()
    {
        using var c = new GuaContext(); using var owner = c.CreateObserveOwner(GuaObserveSource.World);
        using var cat = new GuaEnumCatalog(); cat.Register("game.SECRET_TYPE", "SECRET_VALUE");
        using var secret = owner.Property("secret", () => GuaValue.Enum("game.SECRET_TYPE", "SECRET_VALUE", cat), true, true);
        using var privateValue = owner.Property("private", () => GuaValue.String("PRIVATE_VALUE"));
        using var publicValue = owner.Property("count", () => GuaValue.Integer(5), true); secret.Notify(); privateValue.Notify(); publicValue.Notify();
        await using var trace = new GuaTraceSession(Options("player")); var step = trace.BeginStep(GuaTraceStepKind.Mark, "public");
        using var capture = GuaTraceObservations.Subscribe(trace, step, c); Assert.That(capture.Snapshot(step, "wait-end"), Is.True);
        // Misconfigured custom/Runtime source is rejected before Trace retention.
        using var rejected = GuaTraceObservations.Subscribe(trace, step, () => c.SubscribeObservations(), () => c.GetObserveSnapshotTransportJson());
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var text = string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.That(text, Does.Not.Contain("SECRET").And.Not.Contain("PRIVATE_VALUE"));
        Assert.That(trace.Status.Issues, Does.Contain("observe-failed"));
    }

    [Test]
    public async Task TreePositionFailureAndStaleRemainDistinctWithoutBaselineNormalization()
    {
        using var c = new GuaContext();
        void World(double x) { c.BeginWorldFrame("test"); c.RegisterWorldObject(new("enemy", "enemy", "Enemy", GuaWorldSpace.World2D, new(x, 3))); c.EndWorldFrame(); }
        World(1); await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Action, "move");
        GuaTraceCapture.World(trace, step, c, "game", "before"); World(2);
        GuaTraceCapture.World(trace, step, c, "game", "input-complete");
        GuaTraceCapture.Tree(trace, step, "ui", "game", "wait-end", () => throw new Exception("SECRET_ERROR"));
        GuaTraceCapture.World(trace, step, c, "game", "main-result", "999");
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath); var observations = read.Events.Where(e => e.Type == "observation").ToArray();
        Assert.That(observations.Select(e => e.Data.GetProperty("availability").GetString()), Is.EqualTo(new[] { "available", "available", "failed", "stale" }));
        Assert.That(read.Blobs[observations[0].Data.GetProperty("blob").GetString()!].GetProperty("objects")[0].GetProperty("position").GetProperty("x").GetDouble(), Is.EqualTo(1));
        Assert.That(read.Blobs[observations[1].Data.GetProperty("blob").GetString()!].GetProperty("objects")[0].GetProperty("position").GetProperty("x").GetDouble(), Is.EqualTo(2));
    }

    [Test]
    public async Task ParallelCaptureAndCapacityOverflowRemainBoundedAndKeepReferences()
    {
        using var c = new GuaContext(); using var owner = c.CreateObserveOwner(GuaObserveSource.World);
        int number = 0; using var value = owner.Property("number", () => GuaValue.Integer(number)); value.Notify();
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, MaxSteps = 2, MaxMemoryBytes = 4096, SavePolicy = GuaTraceSavePolicy.Always });
        var step = trace.BeginStep(GuaTraceStepKind.Action, "parallel");
        using var capture = GuaTraceObservations.Subscribe(trace, step, c);
        Parallel.For(0, 5, _ => capture.Snapshot(step, "wait-end"));
        for (int i = 0; i < 30; ++i) { number = i; value.Notify(); capture.Poll(step); }
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.Quality.DetailStopped, Is.True);
        Assert.That(read.Issues, Does.Not.Contain("blob-unavailable"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("observe-storage-gap"));
    }
}
