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
public sealed class SemanticLintTransportTests
{
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback,0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    [TestCase(GuaObservationProfile.Debug), TestCase(GuaObservationProfile.Player)]
    public void RemoteUsesCoreRulesAndHostProjection(GuaObservationProfile profile) {
        using var runtime = new GuaRuntime(); runtime.SetObservationProfile(profile); runtime.EnableWorldObjectTreeAdapter();
        runtime.BeginFrame("fixture");
        runtime.RegisterNode(new("safe", "button", "", new(0,0,1,1)));
        runtime.RegisterNode(new("SECRET_ID", "button", "", new(0,0,1,1), Visible:false));
        runtime.EndFrame();
        runtime.BeginWorldFrame("fixture");
        runtime.RegisterWorldObject(new("world", "fixture", "World", GuaWorldSpace.World2D, new(0,0), VisibleToPlayer:true, RelatedUiNodeId:"absent"));
        runtime.RegisterWorldObject(new("SECRET_WORLD", "fixture", "Secret", GuaWorldSpace.World2D, new(0,0), RelatedUiNodeId:"SECRET_REFERENCE"));
        runtime.EndWorldFrame();
        int port = Port(); Assert.That(runtime.StartInspectorBridge(port), Is.True);
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        var report = remote.AnalyzeSemanticLint();
        Assert.That(report.Profile, Is.EqualTo(profile));
        Assert.That(report.Findings.Any(f => f.RuleId == "broken-related-ui" && f.TargetId == "world"), Is.True);
        Assert.That(report.Findings.Any(f => f.RuleId == "missing-accessible-name" && f.TargetId == "safe"), Is.True);
        Assert.That(remote.AnalyzeSemanticLint(false).WorldObjectTree, Is.Null);
        if (profile == GuaObservationProfile.Player) Assert.That(report.ToJson(), Does.Not.Contain("SECRET"));
        runtime.BeginFrame("staging"); runtime.RegisterNode(new("unpublished", "button", "", new(0,0,1,1)));
        Assert.That(remote.AnalyzeSemanticLint().ToJson(), Is.EqualTo(report.ToJson()));
    }
    [Test]
    public async Task MalformedCommandsCannotElevateProfileOrCloseConnection() {
        using var runtime = new GuaRuntime(); runtime.SetObservationProfile(GuaObservationProfile.Player);
        int port = Port(); Assert.That(runtime.StartInspectorBridge(port), Is.True);
        using var socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
        foreach (var command in new[] {
            "{\"id\":1,\"type\":\"semantic_lint\",\"profile\":\"SECRET\"}",
            "{\"id\":1,\"type\":\"semantic_lint\",\"includeWorld\":1}",
            "{\"id\":1,\"type\":\"semantic_lint\",\"id\":2}",
            "{\"id\":999999999999999999999,\"type\":\"semantic_lint\"}",
            "{\"id\":1,\"type\":\"semantic_lint\",\"uiTree\":\"SECRET\"}" }) {
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(command)), WebSocketMessageType.Text, true, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true) {
                var bytes = new byte[65536]; var frame = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token);
                using var response = JsonDocument.Parse(Encoding.UTF8.GetString(bytes,0,frame.Count));
                if (!response.RootElement.TryGetProperty("id", out _)) continue;
                Assert.That(response.RootElement.GetProperty("ok").GetBoolean(), Is.False);
                Assert.That(response.RootElement.GetRawText(), Does.Not.Contain("SECRET")); break;
            }
        }
    }
}
