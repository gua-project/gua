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
        Assert.That(attachments.Select(e => e.Data.GetProperty("schema").GetString()), Does.Contain("gua.semantic-lint.v1").And.Contain("gua.semantic-comparison.v1").And.Contain("gua.trace.recording.v1").And.Contain("gua.diagnostics.v1").And.Contain("gua.environment.v1"));
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
            Directory.CreateDirectory(Path.Combine(evidence, "schema"));
            File.WriteAllText(Path.Combine(evidence, "schema", "diagnostics.json"), diagnostics.GetRawText());
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
        Assert.That(GuaTraceCapture.Environment(trace, step, context.GetVersion(),
            new Dictionary<string, string> { ["caller-approved"] = "public" }, GuaObservationProfile.Player), Is.True);
        Assert.That(GuaTraceCapture.Diagnostics(trace, step, () => context.GetDiagnosticsJson(GuaObservationProfile.Player), GuaObservationProfile.Player), Is.True);
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

    [TestCase("missing")]
    [TestCase("version")]
    [TestCase("ui")]
    [TestCase("request")]
    public async Task MalformedDiagnosticsNeverAdvertisesValidProjection(string fault)
    {
        using var context = new GuaContext(); Publish(context, 0);
        Assert.That(context.EnqueueClick("button"), Is.True);
        var document = System.Text.Json.Nodes.JsonNode.Parse(context.GetDiagnosticsJson())!;
        if (fault == "missing") document.AsObject().Remove("schemaVersion");
        else if (fault == "version") document["schemaVersion"] = 9;
        else if (fault == "ui") document["uiTree"]!["nodes"]![0]!["bounds"]!["x"] = "not-a-number";
        else document["pendingRequests"]![0]!["action"] = "invented";
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, SavePolicy = GuaTraceSavePolicy.OnFailure });
        var step = trace.Mark("Invalid diagnostics"); bool getterFired = false;
        Assert.That(GuaTraceCapture.Diagnostics(trace, step, () => { getterFired = true; return document.ToJsonString(); }, GuaObservationProfile.Debug), Is.False);
        Assert.That(getterFired, Is.True);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Events.Any(e => e.Type == "attachment"), Is.False);
        Assert.That(read.Events.Count(e => e.Type == "capture.failure"), Is.EqualTo(1));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("attachment-failed"));
    }

    [TestCase(GuaObservationProfile.Debug)]
    [TestCase(GuaObservationProfile.Player)]
    public async Task PlayerTraceRejectsLegacyDebugComparisonAndDiagnostics(GuaObservationProfile claimedProfile)
    {
        using var context = new GuaContext(); Publish(context, 0);
        var baseline = GuaSemanticSnapshots.CompareSnapshot(context, "private", Snapshot(update: true));
        Assert.That(File.ReadAllText(baseline.BaselinePath), Does.Contain("PRIVATE-MARKER"));
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, Profile = "player" });
        var step = trace.Mark("Reject legacy Debug data");
        var comparison = GuaSemanticSnapshots.CompareSnapshot(context, "private", new()
        {
            BaselineDirectory = Path.Combine(_root, "baselines"), ArtifactDirectory = Path.Combine(_root, "comparisons"),
            Trace = trace, TraceStepId = step, TraceProfile = claimedProfile
        });
        Assert.That(comparison.Matched, Is.True, "Rejection must preserve explicit comparison behavior.");
        var primary = new ApplicationException("PRIVATE-EXCEPTION");
        var diagnostics = new GuaDiagnosticsSession(context, new()
        {
            TestName = "private", OutputDirectory = Path.Combine(_root, "legacy"),
            Trace = trace, TraceStepId = step, TraceProfile = claimedProfile,
            Environment = new Dictionary<string, string> { ["config"] = "PRIVATE-CONFIG" },
            CallerMetadata = new Dictionary<string, string> { ["source"] = "PRIVATE-METADATA" }
        }).Capture(primary);
        Assert.That(diagnostics.Succeeded, Is.True); Assert.That(diagnostics.PrimaryException, Is.SameAs(primary));
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Where(e => e.Type == "attachment").Select(e => e.Data.GetProperty("schema").GetString()),
            Is.Empty, "Every legacy automatic payload, including environment/version/metadata, is Debug-only.");
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("profile-mismatch"));
        Assert.That(string.Join("", read.Blobs.Values.Select(b => b.GetRawText())),
            Does.Not.Contain("PRIVATE-MARKER").And.Not.Contain("PRIVATE-EXCEPTION").And.Not.Contain("PRIVATE-CONFIG").And.Not.Contain("PRIVATE-METADATA"));
    }

    [TestCase("directory")]
    [TestCase("missing-property")]
    [TestCase("supplement")]
    [TestCase("live-read")]
    [TestCase("bookkeeping")]
    [TestCase("version")]
    public async Task DownstreamDiagnosticsFaultsAreVisibleWithoutReplacingPrimary(string fault)
    {
        using var context = new GuaContext(); Publish(context, 0);
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root });
        var step = trace.Mark("Downstream fault");
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "blocked"); File.WriteAllText(blocked, "actual filesystem fault");
        var primary = new ApplicationException("PRIVATE-EXCEPTION");
        bool supplementFired = false;
        var source = new SingleReadDiagnostics(context, fault == "missing-property" ? () => "{}" :
            fault == "live-read" ? () => throw new IOException("PRIVATE-FAULT") : null,
            fault == "version" ? () => throw new IOException("PRIVATE-FAULT") : null);
        var result = new GuaDiagnosticsSession(source, new()
        {
            TestName = "fault", OutputDirectory = fault == "directory" ? blocked : Path.Combine(_root, "legacy"),
            Trace = trace, TraceStepId = step,
            TextArtifacts = fault is "supplement" or "bookkeeping" ? new Dictionary<string, Func<string>>
                { ["supplement.txt"] = () =>
                    {
                        supplementFired = true;
                        if (fault == "bookkeeping")
                        {
                            var directory = Path.GetFullPath(Directory.GetDirectories(Path.Combine(_root, "legacy", "fault")).Single());
                            Assert.That(directory.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
                            Directory.Delete(directory, true); // Only this fixture's generated legacy capture; simulate unavailable storage.
                        }
                        throw new IOException("PRIVATE-FAULT");
                    } } : new()
        }).Capture(primary);
        Assert.That(source.Reads, Is.EqualTo(1)); Assert.That(result.PrimaryException, Is.SameAs(primary));
        Assert.That(result.CaptureErrors, Is.Not.Empty, "The downstream fault must actually fire.");
        Assert.That(result.Succeeded, Is.EqualTo(fault is "supplement" or "bookkeeping" or "version"));
        Assert.That(supplementFired, Is.EqualTo(fault is "supplement" or "bookkeeping"));
        if (fault == "bookkeeping")
            Assert.That(result.CaptureErrors.Select(e => e.Stage), Does.Contain("error-summary").And.Contain("bookkeeping"), "Both actual bookkeeping faults must fire without replacing primary.");
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        if (fault == "missing-property")
            Assert.That(read.Events.Where(e => e.Type == "attachment").Select(e => e.Data.GetProperty("schema").GetString()), Does.Not.Contain("gua.diagnostics.v1"));
        if (fault == "version")
        {
            Assert.That(source.VersionReads, Is.EqualTo(1), "Cached failure must avoid a second host request.");
            Assert.That(result.CaptureErrors.Select(e => e.Stage), Does.Contain("version"));
        }
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("failed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("diagnostics-failed"));
        Assert.That(read.Events.Any(e => e.Type == "capture.failure" && e.Data.GetProperty("reason").GetString() == "diagnostics-failed"), Is.True);
        Assert.That(read.Events.Count(e => e.Type == "capture.failure" && e.Data.GetProperty("reason").GetString() == "diagnostics-failed"), Is.EqualTo(1));
        Assert.That(read.Events.Count(e => e.Type == "capture.failure"), Is.EqualTo(1), "One fault must not consume two events through different adapters.");
        Assert.That(string.Join("", read.Blobs.Values.Select(b => b.GetRawText())) + string.Join("", read.Events.Select(e => e.Data.GetRawText())),
            Does.Not.Contain("PRIVATE-EXCEPTION").And.Not.Contain("PRIVATE-FAULT"));
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
        Assert.That(singleRead.VersionReads, Is.EqualTo(1), "A second live version read also fails in this fixture.");
        Assert.That(result.PrimaryException, Is.SameAs(primary)); Assert.That(result.Succeeded, Is.True);
        Assert.That(GuaTraceCapture.Environment(trace, step, context.GetVersion(), options.Environment, GuaObservationProfile.Debug), Is.True);
        Assert.Throws<IOException>(() => singleRead.GetVersion(), "The second version read fault is real but capture must avoid it.");
        Assert.That(singleRead.VersionReads, Is.EqualTo(2));
        var failedCapture = new GuaDiagnosticsSession(singleRead, options).Capture(primary);
        Assert.That(singleRead.Reads, Is.EqualTo(2));
        Assert.That(failedCapture.Succeeded, Is.False, "The second-read IOException must actually fire.");
        Assert.That(failedCapture.PrimaryException, Is.SameAs(primary));
        context.Dispose();
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Where(e => e.Type == "attachment").Select(e => e.Data.GetProperty("schema").GetString()),
            Is.EquivalentTo(new[] { "gua.diagnostics.v1", "gua.environment.v1", "gua.environment.v1" }));
        var diagnosticCopy = read.Blobs[read.Events.Single(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "gua.diagnostics.v1").Data.GetProperty("blob").GetString()!];
        Assert.That(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(diagnosticCopy.GetProperty("uiTree").GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(result.ArtifactPath!, "ui-tree.json")))), Is.True);
        var environmentAttachments = read.Events.Where(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "gua.environment.v1").ToArray();
        var environmentCopy = read.Blobs[environmentAttachments[0].Data.GetProperty("blob").GetString()!];
        var explicitCopy = read.Blobs[environmentAttachments[1].Data.GetProperty("blob").GetString()!];
        Assert.That(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(environmentCopy.GetProperty("version").GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(explicitCopy.GetProperty("version").GetRawText())), Is.True, "One schema must have one case-sensitive version shape.");
        Assert.That(environmentCopy.GetProperty("version").TryGetProperty("protocolSchemaVersion", out _), Is.True);
        using var legacyVersion = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.ArtifactPath!, "version.json")));
        Assert.That(legacyVersion.RootElement.TryGetProperty("ProtocolSchemaVersion", out _), Is.True, "Legacy file casing remains unchanged.");
        Assert.That(GuaVersion.Parse(environmentCopy.GetProperty("version").GetRawText()).ProtocolSchemaVersion,
            Is.EqualTo(GuaVersion.Parse(legacyVersion.RootElement.GetRawText()).ProtocolSchemaVersion));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("diagnostics-failed"));
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

    [TestCase(GuaTraceCaptureMode.Recent, "attachment")]
    [TestCase(GuaTraceCaptureMode.Streaming, "attachment")]
    [TestCase(GuaTraceCaptureMode.Recent, "artifact")]
    [TestCase(GuaTraceCaptureMode.Streaming, "artifact")]
    [TestCase(GuaTraceCaptureMode.Streaming, "queue")]
    [TestCase(GuaTraceCaptureMode.Streaming, "queue-items")]
    public async Task AttachmentLimitFailureKeepsCorrelationWithoutExceedingBudgets(GuaTraceCaptureMode mode, string limit)
    {
        await using var trace = new GuaTraceSession(new()
        {
            OutputDirectory = _root, CaptureMode = mode, SavePolicy = GuaTraceSavePolicy.OnFailure,
            MaxAttachmentBytes = limit == "attachment" ? 128 : 4096,
            MaxArtifactBytes = limit == "artifact" ? 1024 : 16384,
            MaxMemoryBytes = limit == "queue" ? 1024 : 16384, MaxQueueItems = limit == "queue-items" ? 1 : 256,
            MaxEventBytes = 512, Secrets = new[] { "SECRET-MARKER" }
        });
        string step;
        var gate = typeof(GuaTraceSession).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(trace)!;
        lock (gate)
        {
            step = trace.BeginStep(GuaTraceStepKind.Mark, "Limit capture");
            if (limit is "queue" or "queue-items")
            {
                for (var i = 0; i < 10 && !trace.Status.DetailStopped; i++)
                    trace.Record(step, "detail", JsonSerializer.SerializeToElement(new { index = i }));
                Assert.That(trace.Status.Issues, Does.Contain(limit == "queue" ? "queue-byte-limit" : "queue-limit"), "The queue fault must actually fire.");
            }
            Assert.That(GuaTraceCapture.JsonAttachment(trace, step, "limit.fixture.v1", () =>
                JsonSerializer.Serialize(new { secret = "SECRET-MARKER", payload = new string('x', 2048) }), GuaObservationProfile.Debug), Is.False);
            Assert.That(trace.Status.DetailStopped, Is.True);
            Assert.That(trace.Record(step, "detail", JsonSerializer.SerializeToElement(new { })), Is.False);
        }
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("attachment-unavailable"));
        Assert.That(read.Blobs, Is.Empty);
        if (limit == "queue")
            Assert.That(read.Manifest.Quality.Issues, Does.Contain("capture-failure:" + step + ":attachment:attachment-unavailable"));
        else if (limit == "queue-items")
        {
            // The writer may take an item while blocked on the gate, freeing a slot but not its byte budget.
            // In either scheduling order, correlation must survive within the same finite queue contract.
            Assert.That(read.Events.Any(e => e.Type == "capture.failure" && e.StepId == step &&
                e.Data.GetProperty("channel").GetString() == "attachment") ||
                read.Manifest.Quality.Issues.Contains("capture-failure:" + step + ":attachment:attachment-unavailable"), Is.True);
        }
        else
        {
            var failure = read.Events.Single(e => e.Type == "capture.failure");
            Assert.That(failure.StepId, Is.EqualTo(step));
            Assert.That(failure.Data.GetProperty("channel").GetString(), Is.EqualTo("attachment"));
        }
        var payloadBytes = Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "manifest.json").Sum(p => new FileInfo(p).Length);
        Assert.That(payloadBytes, Is.LessThanOrEqualTo(limit == "artifact" ? 1024 : 16384));
        Assert.That(new FileInfo(Path.Combine(trace.ArtifactPath, "manifest.json")).Length, Is.LessThanOrEqualTo(65536));
        Assert.That(string.Join("", read.Events.Select(e => e.Data.GetRawText())), Does.Not.Contain("SECRET-MARKER"));
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
        var payload = read.Blobs[read.Events.Single(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "gua.trace.recording.v1").Data.GetProperty("blob").GetString()!];
        Assert.That(payload.GetProperty("recording").GetProperty("steps")[0].GetProperty("redacted").GetBoolean(), Is.True);
        Assert.That(payload.GetRawText(), Does.Not.Contain("private-target"));
        var evidence = System.Environment.GetEnvironmentVariable("GUA_TRACE_INTEGRATION_EVIDENCE");
        if (evidence is not null)
        {
            Directory.CreateDirectory(Path.Combine(evidence, "schema"));
            File.WriteAllText(Path.Combine(evidence, "schema", "recording.json"), payload.GetRawText());
        }
        var reference = read.Blobs[read.Events.Single(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "gua.recording.references.v1").Data.GetProperty("blob").GetString()!];
        Assert.That(reference.GetProperty("steps")[0].GetProperty("secretKey").GetString(), Is.EqualTo("login-password"));
        Assert.That(reference.GetProperty("steps")[0].GetProperty("requestId").GetString(), Is.EqualTo(ulong.MaxValue.ToString()));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("attachment-failed"));
    }

    private sealed class SingleReadDiagnostics(GuaContext context, Func<string>? payload = null, Func<GuaVersion>? versionPayload = null) : IGuaContext
    {
        public int Reads;
        public int VersionReads;
        public string GetDiagnosticsJson() => ++Reads == 1 ? payload?.Invoke() ?? context.GetDiagnosticsJson() : throw new IOException("second live read fired");
        public GuaVersion GetVersion() => ++VersionReads == 1 ? versionPayload?.Invoke() ?? context.GetVersion() : throw new IOException("second version read fired");
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
