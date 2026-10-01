using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[TestFixture, NonParallelizable]
public sealed class TraceExternalRunnerTests
{
    private static IEnumerable<TestCaseData> Cases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "trace-external-runner.json")));
        foreach (var test in fixture.RootElement.GetProperty("cases").EnumerateArray())
            foreach (var remote in new[] { false, true })
                foreach (var mode in Enum.GetValues<GuaTraceCaptureMode>())
                    yield return new TestCaseData(remote, mode, test.GetProperty("truth").GetString()!,
                        Enum.Parse<GuaTraceOutcome>(test.GetProperty("primary").GetString()!, true),
                        Enum.Parse<GuaTraceOutcome>(test.GetProperty("cleanup").GetString()!, true))
                        .SetName($"ExternalRunner/{(remote ? "websocket" : "local")}/{mode}/{test.GetProperty("name").GetString()}");
    }

    [TestCaseSource(nameof(Cases))]
    public async Task ExternalRunnerContractAcrossNativeLifecycleAndObserve(bool remote, GuaTraceCaptureMode mode,
        string truth, GuaTraceOutcome primary, GuaTraceOutcome cleanupOutcome)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "external-runner", Guid.NewGuid().ToString("N"));
        try
        {
            using var local = remote ? null : new GuaContext();
            using var runtime = remote ? new GuaRuntime() : null;
            int port = 0;
            if (local is not null) {
                local.BeginFrame("runner"); local.RegisterNode("button", "button", "Go", new(0, 0, 10, 10)); local.EndFrame();
            } else {
                runtime!.BeginFrame("runner"); runtime.RegisterNode(new("button", "button", "Go", new(0, 0, 10, 10))); runtime.EndFrame();
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                Assert.That(runtime.StartInspectorBridge(port), Is.True);
            }
            using var socket = remote ? new GuaWebSocketContext($"ws://127.0.0.1:{port}") : null;
            IGuaContext context = (IGuaContext?)local ?? socket!;
            using var owner = local is not null ? local.CreateObserveOwner(GuaObserveSource.World) : runtime!.CreateObserveOwner(GuaObserveSource.World);
            int phase = 1;
            using var registration = owner.Property("phase", () => GuaValue.Integer(phase)); registration.Notify();
            using var independent = local is not null ? local.SubscribeObservations() : runtime!.SubscribeObservations();
            await using var trace = new GuaTraceSession(new() { OutputDirectory = root, CaptureMode = mode,
                SavePolicy = GuaTraceSavePolicy.Always, Secrets = new[] { "KNOWN-SECRET" } });
            using var scope = GuaAssertionScope.Use(new() { Trace = trace });
            var lifecycle = trace.Watch(context, TimeSpan.FromHours(1));
            var step = trace.BeginStep(GuaTraceStepKind.Action, "External transition");
            using var observations = local is not null ? GuaTraceObservations.Subscribe(trace, step, local) : GuaTraceObservations.Subscribe(trace, step, socket!);
            using (trace.UseStep(step))
            {
                var pending = GuaAssertions.Query(context).ByRole("button", "Go").ClickAsync(timeout: TimeSpan.FromSeconds(3));
                if (local is not null) {
                    Assert.That(local.TryConsumeAction(GuaActionType.Click, "button", out var request), Is.True);
                    Assert.That(local.EmitActionResult(new(request.RequestId, GuaActionType.Click, true, GuaActionError.None, "button", "", false)), Is.True);
                } else {
                    Assert.That(runtime!.TryConsumeAction(GuaActionType.Click, "button", out var request), Is.True);
                    runtime.EmitActionResult(request, true);
                }
                await pending;
            }
            Assert.That(lifecycle.Capture(), Is.True);
            phase = 2; registration.Notify(); phase = 3; registration.Notify();
            Assert.That(observations.Poll(step, "input-complete"), Is.True);
            Assert.That(observations.Snapshot(step, "main-result"), Is.True);
            // Explicit observation references are issued by Observe; no observation ID is inferred.
            using var publication = JsonDocument.Parse(local is not null ? local.GetObserveSnapshotTransportJson() : runtime!.GetObserveSnapshotTransportJson());
            var published = publication.RootElement.GetProperty("document");
            var observation = trace.Observe(step, "runner-publication", "main-result", "available",
                new("runner", published.GetProperty("sessionEpoch").GetRawText()), published);
            using var expected = GuaValue.Integer(3); using var actual = GuaValue.Integer(phase);
            Assert.That(trace.Evaluate(step, "phase", "equals", truth == "unknown" ? null : expected,
                truth == "unknown" ? null : actual, truth, "failure-condition", new[] { observation }, primary), Is.True);
            using var name = GuaValue.String("Future goal");
            using var secret = GuaValue.String("SENSITIVE-VALUE");
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "trace-external-runner.json")));
            Assert.That(trace.Annotate(step, fixture.RootElement.GetProperty("annotation").GetString()!, name), Is.True);
            Assert.That(trace.Annotate(step, "external-playtest.private", secret, sensitive: true), Is.True);
            using var decision = JsonDocument.Parse("""{"request":{"observations":[],"summary":"published facts"},"proposal":"reset","decision":"adopted","reason":"fixture cleanup","credential":"KNOWN-SECRET","private":{"sensitive":true,"value":"PRIVATE-VALUE"},"exception":{"mask":true,"message":"PRIVATE-EXCEPTION"}}""");
            Assert.That(trace.Attach(step, fixture.RootElement.GetProperty("schema").GetString()!, decision.RootElement), Is.True);
            Assert.That(trace.SetPrimaryOutcome(primary), Is.True);
            var cleanup = trace.BeginStep(GuaTraceStepKind.Lifecycle, "Runner cleanup", parentStepId: step);
            phase = 1; registration.Notify();
            Assert.That(observations.Poll(cleanup, "after-cleanup"), Is.True);
            Assert.That(observations.Snapshot(cleanup, "after-cleanup"), Is.True);
            trace.EndStep(cleanup, cleanupOutcome);
            Assert.That(await trace.FlushAsync(), Is.True);
            var checkpoint = GuaTraceReader.Read(trace.ArtifactPath);
            var nativeRequest = checkpoint.Events.First(e => e.Type == "request.phase").Data.GetProperty("request");
            var key = new GuaTraceRequest(nativeRequest.GetProperty("sourceId").GetString()!,
                nativeRequest.GetProperty("sessionEpoch").GetString()!, nativeRequest.GetProperty("requestId").GetString()!);
            Assert.That(trace.BeginStep(GuaTraceStepKind.Action, "duplicate explicit request", key), Is.EqualTo(step));
            Assert.That(await trace.CompleteAsync(cleanupOutcome), Is.True);
            var read = GuaTraceReader.Read(trace.ArtifactPath);
            var evaluation = read.Events.Single(e => e.Type == "assertion.evaluation");
            Assert.Multiple(() => {
                Assert.That(read.Issues, Is.Empty);
                Assert.That(read.Manifest.Quality.Issues, Is.Empty);
                Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo(primary.ToString().ToLowerInvariant()));
                Assert.That(read.Events.Count(e => e.Type == "step.begin" && e.Data.GetProperty("kind").GetString() == "action"), Is.EqualTo(1));
                Assert.That(read.Events.Where(e => e.Type == "request.phase").Select(e => e.Data.GetProperty("phase").GetString()), Is.EqualTo(new[] { "enqueue", "consume", "completion" }));
                Assert.That(read.Events.Single(e => e.StepId == cleanup && e.Type == "step.end").Data.GetProperty("outcome").GetString(), Is.EqualTo(cleanupOutcome.ToString().ToLowerInvariant()));
                Assert.That(evaluation.Data.GetProperty("truth").GetString(), Is.EqualTo(truth));
                Assert.That(evaluation.Data.GetProperty("callerOutcome").GetString(), Is.EqualTo(primary.ToString().ToLowerInvariant()));
                Assert.That(evaluation.Data.GetProperty("target").GetString(), Is.EqualTo("phase"));
                Assert.That(evaluation.Data.GetProperty("operator").GetString(), Is.EqualTo("equals"));
                Assert.That(evaluation.Data.GetProperty("role").GetString(), Is.EqualTo("failure-condition"));
                Assert.That(evaluation.CollectedMilliseconds, Is.GreaterThanOrEqualTo(0));
                Assert.That(evaluation.Data.TryGetProperty("expected", out _), Is.EqualTo(truth != "unknown"));
                Assert.That(evaluation.Data.TryGetProperty("actual", out _), Is.EqualTo(truth != "unknown"));
                Assert.That(evaluation.Data.GetProperty("observations")[0].GetString(), Is.EqualTo(observation));
                Assert.That(read.Events.Where(e => e.Type == "observation.change").Select(e => e.Data.GetProperty("received").GetProperty("after").GetProperty("value").GetInt32()), Is.EqualTo(new[] { 2, 3, 1 }));
                Assert.That(independent.PollJson(), Does.Contain("\"value\":2").And.Contain("\"value\":3"));
            });
            var attached = read.Events.Single(e => e.Type == "attachment");
            var path = attached.Data.GetProperty("blob").GetString()!;
            Assert.That(read.Blobs[path].TryGetProperty("usage", out _), Is.False);
            Assert.That(Path.GetFileNameWithoutExtension(path), Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(trace.ArtifactPath, path)))).ToLowerInvariant()));
            var text = string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.That(text, Does.Not.Contain("KNOWN-SECRET").And.Not.Contain("SENSITIVE-VALUE").And.Not.Contain("PRIVATE-VALUE").And.Not.Contain("PRIVATE-EXCEPTION"));
            Assert.That(text, Does.Contain("external-playtest.future.goal").And.Contain("external-playtest.future.decision.v1"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
