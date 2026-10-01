using System.Text.Json;
using Gua.Core;
using Gua.Testing;
using Gua.Testing.Recording;
using Gua.Testing.Snapshots;
using NUnit.Framework;

namespace Gua.Visual.Tests;

[NonParallelizable]
public sealed class TraceIntegrationTests
{
    private string _root = null!;
    [SetUp] public void Setup() => _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "trace-integration", Guid.NewGuid().ToString("N"));
    [TearDown] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static void Publish(GuaContext context, float x)
    {
        context.BeginFrame("integration");
        context.RegisterNode("button", "button", "Go", new(x, 0, 10, 10));
        context.RegisterNode(new("private", "button", "PRIVATE-MARKER", new(0, 0, 1, 1), AgentPolicy: new(Exposure: GuaAgentExposure.Private)));
        context.EndFrame();
    }
    private SemanticSnapshotOptions Snapshot(GuaTraceSession? trace = null, string? step = null, bool update = false) => new()
    {
        BaselineDirectory = Path.Combine(_root, "baselines"), ArtifactDirectory = Path.Combine(_root, "comparisons"),
        UpdateBaselines = update, Trace = trace, TraceStepId = step
    };

    [TestCase(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.OnFailure, GuaTraceOutcome.Passed)]
    [TestCase(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.OnFailure, GuaTraceOutcome.Passed)]
    [TestCase(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.Always, GuaTraceOutcome.Passed)]
    [TestCase(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.Always, GuaTraceOutcome.Failed)]
    [TestCase(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.OnFailure, GuaTraceOutcome.Failed)]
    [TestCase(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.OnFailure, GuaTraceOutcome.Interrupted)]
    public async Task ActualLintComparisonRecordingDiagnosticsAndObserveShareTheirStep(
        GuaTraceCaptureMode mode, GuaTraceSavePolicy policy, GuaTraceOutcome primary)
    {
        using var context = new GuaContext(); Publish(context, 0);
        var baseline = GuaSemanticSnapshots.CompareSnapshot(context, "integration", Snapshot(update: true));
        var beforeBaseline = File.ReadAllBytes(baseline.BaselinePath);
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode, SavePolicy = policy, Secrets = new[] { "SECRET-MARKER" } });
        using var scope = GuaAssertionScope.Use(new() { Trace = trace });
        var step = trace.BeginStep(GuaTraceStepKind.Action, "Integrated action");
        using var owner = context.CreateObserveOwner(GuaObserveSource.World);
        int value = 1;
        using var registration = owner.Property("phase", () => GuaValue.Integer(value)); registration.Notify();
        using var independent = context.SubscribeObservations();
        using var observations = GuaTraceObservations.Subscribe(trace, step, context);
        GuaTraceCapture.Ui(trace, step, context, "local", "before");
        var recorder = new GuaRecorder(context);
        using (trace.UseStep(step))
        {
            var action = recorder.ClickAsync(new(Id: "button"), timeout: TimeSpan.FromSeconds(3));
            Assert.That(context.TryConsumeAction(GuaActionType.Click, "button", out var request), Is.True);
            Assert.That(context.EmitActionResult(new(request.RequestId, GuaActionType.Click, true, GuaActionError.None, "button", "", false)), Is.True);
            await action;
        }
        value = 2; registration.Notify(); value = 3; registration.Notify();
        Assert.That(observations.Poll(step, "main-result"), Is.True);
        Publish(context, 80);
        GuaTraceCapture.Ui(trace, step, context, "local", "main-result");
        Assert.That(GuaTraceCapture.Lint(trace, step, GuaSemanticLinter.Analyze(context)), Is.True);
        var comparison = GuaSemanticSnapshots.CompareSnapshot(context, "integration", Snapshot(trace, step));
        Assert.That(comparison.Matched, Is.True, "Default comparison excludes geometry/frame; Trace must retain them.");
        Assert.That(comparison.BaselineUpdated, Is.False);
        Assert.That(File.ReadAllBytes(baseline.BaselinePath), Is.EqualTo(beforeBaseline));
        Assert.That(GuaRecordingTrace.Attach(trace, step, recorder.Recording), Is.True);
        context.AddLog(GuaLogLevel.Info, "SECRET-MARKER safe-log");
        Assert.That(context.EnqueueAction(new(GuaActionType.Click, "button"), out var otherRequest), Is.EqualTo(GuaActionError.None));
        Assert.That(GuaTraceCapture.Diagnostics(trace, step, context), Is.True);
        Assert.That(GuaTraceCapture.Environment(trace, step, context.GetVersion(), new Dictionary<string, string> { ["fixture"] = "integration" }, GuaObservationProfile.Debug), Is.True);
        Assert.That(GuaTraceCapture.JsonAttachment(trace, step, "hostile.fixture.v1", () => JsonSerializer.Serialize(new
        { note = "</script><img src=https://attacker.invalid/pixel onerror=alert(1)>", url = "file:///C:/private", secret = "SECRET-MARKER", code = "window.__hostileExecuted=true" }), GuaObservationProfile.Debug), Is.True);
        // Capture leaves the other request and clock untouched. The caller owns cleanup.
        Assert.That(context.TryConsumeAction(GuaActionType.Click, "button", out var other), Is.True);
        Assert.That(other.RequestId, Is.EqualTo(otherRequest));
        Assert.That(context.EmitActionResult(new(other.RequestId, GuaActionType.Click, true, GuaActionError.None, "button", "", false)), Is.True);
        Assert.That(context.TryPollActionEvent(otherRequest, out _), Is.True);
        using var expected = GuaValue.Integer(3); using var actual = GuaValue.Integer(value);
        Assert.That(trace.Evaluate(step, "phase", "equals", expected, actual, "true", "failure-condition", Array.Empty<string>(), primary), Is.True);
        trace.EndStep(step, primary); trace.SetPrimaryOutcome(primary);
        var cleanup = trace.BeginStep(GuaTraceStepKind.Lifecycle, "Caller cleanup", parentStepId: step);
        GuaTraceCapture.Ui(trace, cleanup, context, "local", "after-cleanup");
        trace.EndStep(cleanup, GuaTraceOutcome.Passed);
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        if (primary == GuaTraceOutcome.Passed && policy == GuaTraceSavePolicy.OnFailure)
        {
            Assert.That(trace.Status.Issues, Is.Empty);
            Assert.That(Directory.Exists(trace.ArtifactPath), Is.False); return;
        }
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo(primary.ToString().ToLowerInvariant()));
        Assert.That(read.Issues, Is.Empty);
        var attachments = read.Events.Where(e => e.Type == "attachment").ToArray();
        Assert.That(attachments.Select(e => e.Data.GetProperty("schema").GetString()), Does.Contain("gua.semantic-lint.v1").And.Contain("gua.semantic-comparison.v1").And.Contain("gua.recording.v1").And.Contain("gua.diagnostics.v1").And.Contain("gua.environment.v1"));
        Assert.That(attachments.Select(e => e.StepId), Is.All.EqualTo(step));
        var diagnostics = read.Blobs[attachments.Single(e => e.Data.GetProperty("schema").GetString() == "gua.diagnostics.v1").Data.GetProperty("blob").GetString()!];
        Assert.That(diagnostics.GetProperty("pendingRequests").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(diagnostics.TryGetProperty("screenshot", out _), Is.False);
        var trees = read.Events.Where(e => e.Type == "observation" && e.Data.GetProperty("channel").GetString() == "ui").ToArray();
        Assert.That(trees.Select(e => read.Blobs[e.Data.GetProperty("blob").GetString()!].GetProperty("nodes")[0].GetProperty("bounds").GetProperty("x").GetSingle()), Does.Contain(0f).And.Contain(80f));
        Assert.That(read.Events.Count(e => e.Type == "observation.change"), Is.EqualTo(2));
        Assert.That(string.Join("", read.Blobs.Values.Select(b => b.GetRawText())), Does.Not.Contain("SECRET-MARKER"));
        var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(_root, "report.html"));
        Assert.That(report.Succeeded, Is.True, report.Error);
        var evidence = System.Environment.GetEnvironmentVariable("GUA_TRACE_INTEGRATION_EVIDENCE");
        if (evidence is not null)
        {
            Directory.CreateDirectory(evidence);
            File.Copy(report.Path!, Path.Combine(evidence, primary.ToString().ToLowerInvariant() + "-" + mode + ".html"), true);
        }
    }

    [Test]
    public async Task ProfileMismatchDoesNotReadPrivateDiagnosticsAndFailureIsRetained()
    {
        using var context = new GuaContext(); Publish(context, 0);
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, Profile = "player" });
        var step = trace.Mark("Player capture");
        int reads = 0;
        Assert.That(GuaTraceCapture.Diagnostics(trace, step, () => { reads++; return context.GetDiagnosticsJson(); }, GuaObservationProfile.Debug), Is.False);
        Assert.That(reads, Is.Zero);
        Assert.That(GuaTraceCapture.Lint(trace, step, GuaSemanticLinter.Analyze(context)), Is.False);
        Assert.That(GuaTraceCapture.Lint(trace, step, GuaSemanticLinter.Analyze(context, new(GuaObservationProfile.Player))), Is.True);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("profile-mismatch"));
        Assert.That(string.Join("", read.Blobs.Values.Select(b => b.GetRawText())), Does.Not.Contain("PRIVATE-MARKER"));
    }

    [Test]
    public async Task CaptureAndReportFailuresPreserveOriginalExceptionIdentityAndStack()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root });
        var primary = new ApplicationException("original");
        Exception? observed = null;
        try
        {
            trace.Assert("OriginalFailure", () =>
            {
                var step = trace.Mark("fault fired");
                bool fired = false;
                Assert.That(GuaTraceCapture.JsonAttachment(trace, step, "hostile.v1", () => { fired = true; throw new IOException("PRIVATE-MARKER"); }, GuaObservationProfile.Debug), Is.False);
                Assert.That(fired, Is.True);
                throw primary;
            });
        }
        catch (Exception error) { observed = error; }
        Assert.That(observed, Is.SameAs(primary)); Assert.That(observed!.StackTrace, Does.Contain(nameof(CaptureAndReportFailuresPreserveOriginalExceptionIdentityAndStack)));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("attachment-failed"));
        Assert.That(read.Events.Single(e => e.Type == "assertion.result").Data.GetProperty("truth").GetString(), Is.EqualTo("unknown"));
        var blocked = Path.Combine(_root, "blocked"); File.WriteAllText(blocked, "actual fault");
        Assert.That(GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(blocked, "report.html")).Succeeded, Is.False);
        Assert.That(observed, Is.SameAs(primary));
    }

    [Test]
    public async Task ExistingDiagnosticsSessionCapturesBeforeTeardownWithoutImportingFiles()
    {
        using var context = new GuaContext(); Publish(context, 0);
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, Secrets = new[] { "SECRET-MARKER" } });
        var step = trace.Mark("failure evidence");
        var primary = new ApplicationException("PRIVATE-EXCEPTION");
        var options = new GuaDiagnosticOptions { TestName = "fixture", OutputDirectory = Path.Combine(_root, "legacy"),
            Trace = trace, TraceStepId = step, Environment = new Dictionary<string, string> { ["fixture"] = "SECRET-MARKER" } };
        var singleRead = new SingleReadDiagnostics(context);
        var result = new GuaDiagnosticsSession(singleRead, options).Capture(primary);
        Assert.That(singleRead.Reads, Is.EqualTo(1), "A second live diagnostics read fails in this fixture.");
        Assert.That(result.PrimaryException, Is.SameAs(primary)); Assert.That(result.Succeeded, Is.True);
        var failedCapture = new GuaDiagnosticsSession(singleRead, options).Capture(primary);
        Assert.That(singleRead.Reads, Is.EqualTo(2));
        Assert.That(failedCapture.Succeeded, Is.False, "The second-read IOException must actually fire.");
        Assert.That(failedCapture.PrimaryException, Is.SameAs(primary));
        context.Dispose();
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Where(e => e.Type == "attachment").Select(e => e.Data.GetProperty("schema").GetString()),
            Is.EquivalentTo(new[] { "gua.diagnostics.v1", "gua.environment.v1" }));
        var diagnosticCopy = read.Blobs[read.Events.Single(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "gua.diagnostics.v1").Data.GetProperty("blob").GetString()!];
        Assert.That(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(diagnosticCopy.GetProperty("uiTree").GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(result.ArtifactPath!, "ui-tree.json")))), Is.True);
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("attachment-failed"));
        Assert.That(string.Join("", read.Blobs.Values.Select(b => b.GetRawText())), Does.Not.Contain("SECRET-MARKER").And.Not.Contain("PRIVATE-EXCEPTION").And.Not.Contain(result.ArtifactPath!));
    }

    [Test]
    public async Task ParallelSessionsAndInterruptedDisposalKeepArtifactsIndependent()
    {
        var paths = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = GuaTraceCaptureMode.Streaming });
            var step = trace.Mark("parallel " + i);
            Assert.That(GuaTraceCapture.JsonAttachment(trace, step, "fixture.v1", () => JsonSerializer.Serialize(new { index = i }), GuaObservationProfile.Debug), Is.True);
            if (i % 2 == 0) await trace.CompleteAsync(GuaTraceOutcome.Failed);
            // Other sessions are disposed with no result; they must retain Interrupted.
            return (trace.ArtifactPath, Index: i);
        }));
        Assert.That(paths.Select(p => p.ArtifactPath).Distinct().Count(), Is.EqualTo(12));
        foreach (var item in paths)
        {
            var read = GuaTraceReader.Read(item.ArtifactPath);
            Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo(item.Index % 2 == 0 ? "failed" : "interrupted"));
            Assert.That(read.Blobs.Single().Value.GetProperty("index").GetInt32(), Is.EqualTo(item.Index));
        }
    }

    [Test]
    public async Task SensitiveRecordingKeepsSafeReferenceAndRejectsInvalidPayload()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root });
        var step = trace.Mark("sensitive recording");
        var recording = new GuaRecording(1, new[] { new GuaRecordingStep(GuaRecordedAction.set_value, 0, 1, 1, true,
            ulong.MaxValue, Target: new(Id: "private-target"), SecretKey: "login-password") });
        Assert.That(GuaRecordingTrace.Attach(trace, step, recording), Is.True);
        Assert.That(GuaRecordingTrace.Attach(trace, step, recording with { SchemaVersion = 9 }), Is.False);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var payload = read.Blobs[read.Events.Single(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "gua.recording.v1").Data.GetProperty("blob").GetString()!];
        Assert.That(payload.GetProperty("steps")[0].GetProperty("redacted").GetBoolean(), Is.True);
        Assert.That(payload.GetRawText(), Does.Not.Contain("private-target"));
        var reference = read.Blobs[read.Events.Single(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "gua.recording.references.v1").Data.GetProperty("blob").GetString()!];
        Assert.That(reference.GetProperty("steps")[0].GetProperty("secretKey").GetString(), Is.EqualTo("login-password"));
        Assert.That(reference.GetProperty("steps")[0].GetProperty("requestId").GetString(), Is.EqualTo(ulong.MaxValue.ToString()));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("attachment-failed"));
    }

    private sealed class SingleReadDiagnostics(GuaContext context) : IGuaContext
    {
        public int Reads;
        public string GetDiagnosticsJson() => ++Reads == 1 ? context.GetDiagnosticsJson() : throw new IOException("second live read fired");
        public GuaVersion GetVersion() => context.GetVersion();
        public string GetUiTreeJson() => context.GetUiTreeJson();
        public GuaNodeState GetNodeState(string id) => context.GetNodeState(id);
        public string FindNodeById(string id) => context.FindNodeById(id);
        public string FindNodeByRole(string role, string? name = null) => context.FindNodeByRole(role, name);
        public string FindNodeByText(string text) => context.FindNodeByText(text);
        public bool EnqueueClick(string id) => context.EnqueueClick(id);
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId) => context.EnqueueAction(request, out requestId);
        public bool TryPollActionEvent(out GuaActionEvent e) => context.TryPollActionEvent(out e);
        public bool TryPollActionEvent(ulong requestId, out GuaActionEvent e) => context.TryPollActionEvent(requestId, out e);
        public bool TryPollEvent(out GuaEvent e) => context.TryPollEvent(out e);
    }
}
