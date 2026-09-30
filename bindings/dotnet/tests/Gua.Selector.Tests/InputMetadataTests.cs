using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[TestFixture]
public sealed class InputMetadataTests
{
    private const string Schema = """{"type":"object","properties":{"x":{"type":"number","description":"Horizontal movement; positive moves right"},"y":{"type":"number","description":"Forward movement"}},"required":["x","y"],"additionalProperties":false}""";
    private static GuaGameInputActionDescriptor Move => new("move", "Move the player", GuaGameInputValueType.Vector2,
        -1, 1, Holdable: true) { ValueSchemaJson = Schema, ExamplesJson = """[{"x":0.5,"y":0}]""" };
    private static GuaRuntime Runtime()
    {
        var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => {}, GuaGameInputCapabilities.Semantic);
        runtime.PublishGameInputActions("play", [Move]);
        return runtime;
    }

    [Test]
    public void MetadataRoundTripsOnlyOnExplicitV2CallsAndOldRecordSignaturesSurvive()
    {
        using var runtime = Runtime();
        using var old = JsonDocument.Parse(runtime.GetGameInputActionsJson());
        var oldAction = old.RootElement.GetProperty("actions")[0];
        Assert.That(old.RootElement.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(oldAction.TryGetProperty("valueSchema", out _), Is.False);
        Assert.That(oldAction.TryGetProperty("examples", out _), Is.False);
        var v1 = runtime.FindGameInputActions(new(Id: "move"));
        Assert.That(v1.SchemaVersion, Is.EqualTo(1));
        Assert.That(v1.Actions[0].ValueSchema, Is.Null);
        var v2 = runtime.FindGameInputActionsV2(new(Id: "move"));
        Assert.That(v2.SchemaVersion, Is.EqualTo(2));
        Assert.That(v2.Actions[0].ValueSchema!.Value.GetProperty("properties").GetProperty("x").GetProperty("description").GetString(), Does.Contain("moves right"));
        Assert.That(v2.Actions[0].Examples![0].GetProperty("x").GetDouble(), Is.EqualTo(0.5));
        // Existing positional construction and generated Deconstruct signatures remain intact.
        Move.Deconstruct(out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        v1.Actions[0].Deconstruct(out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        var directory = Environment.GetEnvironmentVariable("GUA_INPUT_COMPAT_OUTPUT");
        if (directory is not null) {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "map-v1.json"), runtime.GetGameInputActionsJson());
            File.WriteAllText(Path.Combine(directory, "search-v1.json"), runtime.FindGameInputActionsJson(new(Id: "move")));
            File.WriteAllText(Path.Combine(directory, "map-v2.json"), runtime.GetGameInputActionsJsonV2());
            File.WriteAllText(Path.Combine(directory, "search-v2.json"), runtime.FindGameInputActionsJsonV2(new(Id: "move")));
        }
    }

    [TestCase("null", "[]")]
    [TestCase("{\"type\":\"number\"}", "[]")]
    [TestCase("{\"type\":\"object\",\"$ref\":\"https://example.com\"}", "[]")]
    [TestCase(Schema, "[{\"x\":2,\"y\":0}]")]
    [TestCase(Schema, "[{\"x\":0,\"y\":0,\"z\":0}]")]
    [TestCase(Schema, "[{\"x\":1e999,\"y\":0}]")]
    public void InvalidDeclarationsDoNotReplaceCommittedMap(string schema, string examples)
    {
        using var runtime = Runtime();
        var original = runtime.GetGameInputActionsJsonV2();
        Assert.Throws<ArgumentException>(() => runtime.PublishGameInputActions("bad", [Move with { ValueSchemaJson = schema, ExamplesJson = examples }]));
        Assert.That(runtime.GetGameInputActionsJsonV2(), Is.EqualTo(original));
    }

    [Test]
    public void OptInRangeRetainsDoublePrecisionWhileLegacyFormattingIsUnchanged()
    {
        using var runtime = Runtime();
        runtime.PublishGameInputActions("play", [new("axis", "Move", GuaGameInputValueType.Axis1D, 0.123456789, 0.5)
            { ValueSchemaJson = """{"type":"number","minimum":0.123456789}""", ExamplesJson = "[0.123456789]" }]);
        Assert.That(runtime.GetGameInputActionsJson(), Does.Contain("\"minimum\":0.123457"));
        using var result = JsonDocument.Parse(runtime.GetGameInputActionsJsonV2());
        Assert.That(result.RootElement.GetProperty("actions")[0].GetProperty("range").GetProperty("minimum").GetDouble(), Is.EqualTo(0.123456789));
        var precise = runtime.FindGameInputActionsV2(new(Id: "axis"));
        Assert.That(precise.Actions[0].Range!.Minimum, Is.EqualTo(0.123456789));
    }

    [Test]
    public void CurrentSchemaAndConfirmationAreRecheckedOnHostConsumption()
    {
        using var runtime = Runtime(); using var session = runtime.CreateGameInputSession();
        var requestId = session.Send(GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "move", new { x = 0.5, y = 0 });
        runtime.PublishGameInputActions("play", [Move with { ValueSchemaJson = Schema.Replace("\"number\",\"description\":\"Horizontal", "\"number\",\"maximum\":0.25,\"description\":\"Horizontal"), ExamplesJson = "[]" }]);
        Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
        Assert.That(session.PollResult(requestId).Succeeded, Is.False);
        Assert.Throws<InvalidOperationException>(() => session.Send(GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "move", new { x = 0.5, y = 0 }));
        runtime.PublishGameInputActions("play", [Move with { RequiresConfirmation = true }]);
        Assert.Throws<InvalidOperationException>(() => session.Send(GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "move", new { x = 0, y = 0 }));
        // Release is never prevented by a schema or confirmation requirement.
        Assert.DoesNotThrow(() => session.Send(GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "move"));
    }

    [Test]
    public void MetadataUsesPlayerProjectionAndCannotElevateRuntimeProfile()
    {
        using var runtime = Runtime();
        var before = runtime.FindGameInputActionsV2(new(), GuaObservationProfile.Player).Revision;
        runtime.PublishGameInputActions("play", [Move, Move with { Id = "private_move", Description = "secret-marker", AgentExposure = GuaAgentExposure.Private }]);
        var projected = runtime.FindGameInputActionsV2(new(), GuaObservationProfile.Player);
        Assert.That(projected.Revision, Is.EqualTo(before));
        Assert.That(projected.Count, Is.EqualTo(1));
        Assert.That(runtime.GetGameInputActionsJsonV2(GuaObservationProfile.Player), Does.Not.Contain("secret-marker"));
        runtime.SetObservationProfile(GuaObservationProfile.Player);
        Assert.Throws<InvalidOperationException>(() => runtime.GetGameInputActionsJsonV2(GuaObservationProfile.Debug));
    }

    [Test]
    public async Task ActualWebSocketV1AndV2ReturnDifferentExplicitVersions()
    {
        using var runtime = Runtime();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.That(runtime.StartInspectorBridge(port), Is.True);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(runtime.InspectorBridgeUrl), CancellationToken.None);
        foreach (var (id, type, version) in new[] { (1, "get_game_input_actions", 1), (2, "get_game_input_actions_v2", 2),
            (3, "find_game_input_actions", 1), (4, "find_game_input_actions_v2", 2),
            (7, "get_game_input_actions_v2", 0), (8, "get_game_input_actions", 1),
            (5, "get_game_input_actions_v2", 0), (6, "find_game_input_actions_v2", 0) }) {
            if (id == 5) runtime.EnableGameInput(GuaGameInputCapabilities.None, () => {});
            var request = Encoding.UTF8.GetBytes(id is 7 or 8
                ? JsonSerializer.Serialize(new { id, type, confirmed = true })
                : JsonSerializer.Serialize(new { id, type }));
            await socket.SendAsync(request, WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[16384];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string text;
            do {
                using var bytes = new MemoryStream();
                WebSocketReceiveResult result;
                do {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                    Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Text));
                    bytes.Write(buffer, 0, result.Count);
                    Assert.That(bytes.Length, Is.LessThan(65536));
                } while (!result.EndOfMessage);
                text = Encoding.UTF8.GetString(bytes.ToArray());
                using var message = JsonDocument.Parse(text);
                if (message.RootElement.TryGetProperty("id", out var correlation) && correlation.GetInt32() == id) break;
            } while (true);
            using var response = JsonDocument.Parse(text);
            if (version == 0) {
                Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
                Assert.That(text, Does.Not.Contain("Horizontal movement"));
                continue;
            }
            Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.True);
            var data = response.RootElement.GetProperty("result");
            Assert.That(data.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(version));
            Assert.That(data.GetProperty("actions")[0].TryGetProperty("valueSchema", out _), Is.EqualTo(version == 2));
        }
    }
}
