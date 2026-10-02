using System.Text.Json;
using Gua.Core;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Visual.Tests;

public sealed class TraceTests
{
    private string _root = null!;
    [SetUp] public void SetUp() => _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "trace-tests", Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static JsonElement Json(string value) { using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private GuaTraceOptions Options(GuaTraceCaptureMode mode = GuaTraceCaptureMode.Recent,
        GuaTraceSavePolicy policy = GuaTraceSavePolicy.Always) => new() { OutputDirectory = _root, CaptureMode = mode, SavePolicy = policy };

    [TestCase("ui")]
    [TestCase("world")]
    public async Task MetadataOnlyTreeIsFailedWithoutCreatingAnEmptySnapshot(string channel)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root });
        var step = trace.BeginStep(GuaTraceStepKind.Action, "capture tree");
        GuaTraceCapture.Tree(trace, step, channel, "runtime", "main-result",
            () => "{\"sessionEpoch\":1,\"frameSequence\":0,\"revision\":0}");
        trace.EndStep(step, GuaTraceOutcome.Passed);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        Assert.That(Directory.Exists(trace.ArtifactPath), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var observation = read.Events.Single(e => e.Type == "observation");
        Assert.That(observation.Data.GetProperty("availability").GetString(), Is.EqualTo("failed"));
        Assert.That(observation.Data.TryGetProperty("blob", out _), Is.False);
        Assert.That(read.Blobs, Is.Empty);
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("tree-failed"));
    }

    [TestCase("ui", "button", false)]
    [TestCase("world", "world2d", false)]
    [TestCase("ui", "SecretLabel", true)]
    [TestCase("world", "SecretLabel", true)]
    public async Task RedactedTreeMustStillFollowItsChannelSchema(string channel, string secret, bool available)
    {
        var tree = channel == "ui"
            ? "{\"schemaVersion\":2,\"sessionEpoch\":1,\"frameSequence\":0,\"revision\":0,\"screen\":\"s\",\"nodes\":[{\"id\":\"n\",\"role\":\"button\",\"label\":\"SecretLabel\",\"visible\":true,\"enabled\":true,\"bounds\":{},\"actions\":[\"click\"]}]}"
            : "{\"schemaVersion\":1,\"sessionEpoch\":1,\"frameSequence\":0,\"revision\":0,\"scene\":\"s\",\"objects\":[{\"id\":\"o\",\"kind\":\"actor\",\"label\":\"SecretLabel\",\"space\":\"world2d\",\"position\":{\"x\":1},\"visibleToPlayer\":true,\"active\":true,\"agentExposure\":\"auto\",\"state\":{}}]}";
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, SavePolicy = GuaTraceSavePolicy.Always, Secrets = new[] { secret } });
        var step = trace.BeginStep(GuaTraceStepKind.Assertion, "redacted-tree");
        GuaTraceCapture.Tree(trace, step, channel, "game", "capture", () => tree);
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var observation = read.Events.Single(e => e.Type == "observation");
        Assert.That(observation.Data.GetProperty("availability").GetString(), Is.EqualTo(available ? "available" : "failed"));
        Assert.That(read.Blobs.Count, Is.EqualTo(available ? 1 : 0));
        if (available) Assert.That(read.Blobs.Single().Value.GetRawText(), Does.Contain("[redacted]").And.Not.Contain(secret));
        else {
            Assert.That(observation.Data.TryGetProperty("blob", out _), Is.False);
            Assert.That(read.Manifest.Quality.Issues, Does.Contain("tree-failed"));
        }
    }

    [TestCase("ui", 2, "screen", "nodes")]
    [TestCase("world", 1, "scene", "objects")]
    public async Task TreeEnvelopeRejectsInvalidStructureAndPreservesGenuinelyEmptyTrees(string channel,
        int version, string label, string items)
    {
        await using var trace = new GuaTraceSession(Options());
        var step = trace.BeginStep(GuaTraceStepKind.Action, "tree envelope");
        object Metadata() => new { sessionEpoch = 1, frameSequence = 0, revision = 0 };
        string Tree(int schemaVersion, object? name, object? collection) {
            var data = JsonSerializer.SerializeToElement(Metadata()).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
            data["schemaVersion"] = schemaVersion; data[label] = name!; data[items] = collection!;
            return JsonSerializer.Serialize(data);
        }
        foreach (var invalid in new[] { Tree(version + 1, "test", Array.Empty<object>()), Tree(version, null, Array.Empty<object>()),
            Tree(version, "", Array.Empty<object>()), Tree(version, "test", null), Tree(version, "test", new { }),
            Tree(version, "test", new object?[] { null }), Tree(version, "test", new[] { 1 }) })
            GuaTraceCapture.Tree(trace, step, channel, "runtime", "invalid", () => invalid);
        GuaTraceCapture.Tree(trace, step, channel, "runtime", "empty", () => Tree(version, "test", Array.Empty<object>()));
        trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Where(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString() == "invalid")
            .Select(e => e.Data.GetProperty("availability").GetString()), Is.All.EqualTo("failed"));
        var empty = read.Events.Single(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString() == "empty");
        Assert.That(empty.Data.GetProperty("availability").GetString(), Is.EqualTo("available"));
        Assert.That(read.Blobs[empty.Data.GetProperty("blob").GetString()!].GetProperty(items).GetArrayLength(), Is.Zero);
        Assert.That(empty.Data.GetProperty("host").GetProperty("frame").GetString(), Is.EqualTo("0"));
    }

    [TestCase("ui")]
    [TestCase("world")]
    public async Task TreeItemsFollowTheFullChannelSchema(string channel)
    {
        var itemJson = channel == "ui"
            ? "{\"id\":\"n\",\"role\":\"button\",\"visible\":true,\"enabled\":true,\"bounds\":{},\"actions\":[\"click\"]}"
            : "{\"id\":\"o\",\"kind\":\"actor\",\"space\":\"world2d\",\"position\":{\"x\":1},\"visibleToPlayer\":true,\"active\":true,\"agentExposure\":\"auto\",\"state\":{}}";
        var valid = System.Text.Json.Nodes.JsonNode.Parse(itemJson)!.AsObject();
        var invalid = new List<System.Text.Json.Nodes.JsonNode> { new System.Text.Json.Nodes.JsonObject() };
        foreach (var field in valid.Select(p => p.Key)) {
            var missing = System.Text.Json.Nodes.JsonNode.Parse(itemJson)!.AsObject(); missing.Remove(field); invalid.Add(missing);
            var wrongType = System.Text.Json.Nodes.JsonNode.Parse(itemJson)!.AsObject(); wrongType[field] = null; invalid.Add(wrongType);
        }
        var nested = System.Text.Json.Nodes.JsonNode.Parse(itemJson)!.AsObject();
        nested[channel == "ui" ? "bounds" : "position"] = System.Text.Json.Nodes.JsonNode.Parse(channel == "ui" ? "{\"w\":-1}" : "{\"z\":1}"); invalid.Add(nested);
        await using var trace = new GuaTraceSession(Options()); var step = trace.BeginStep(GuaTraceStepKind.Assertion, "items");
        string Tree(System.Text.Json.Nodes.JsonNode item) => channel == "ui"
            ? $"{{\"schemaVersion\":2,\"sessionEpoch\":1,\"frameSequence\":0,\"revision\":0,\"screen\":\"s\",\"nodes\":[{item.ToJsonString()}]}}"
            : $"{{\"schemaVersion\":1,\"sessionEpoch\":1,\"frameSequence\":0,\"revision\":0,\"scene\":\"s\",\"objects\":[{item.ToJsonString()}]}}";
        foreach (var item in invalid) GuaTraceCapture.Tree(trace, step, channel, "game", "invalid", () => Tree(item));
        GuaTraceCapture.Tree(trace, step, channel, "game", "valid", () => Tree(valid));
        await trace.CompleteAsync(GuaTraceOutcome.Passed); var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Where(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString() == "invalid")
            .Select(e => e.Data.GetProperty("availability").GetString()), Is.All.EqualTo("failed"));
        Assert.That(read.Events.Single(e => e.Type == "observation" && e.Data.GetProperty("reason").GetString() == "valid")
            .Data.GetProperty("availability").GetString(), Is.EqualTo("available"));
        Assert.That(read.Blobs.Count, Is.EqualTo(1));
    }

    [TestCase("player")]
    [TestCase("debug")]
    public async Task RemoteWorldCaptureRequiresAnAuthorizedGetter(string profile)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, Profile = profile });
        using var remote = new GuaWebSocketContext("ws://127.0.0.1:1", TimeSpan.FromMilliseconds(50));
        var step = trace.BeginStep(GuaTraceStepKind.Assertion, "remote");
        GuaTraceCapture.World(trace, step, remote, "remote", "capture");
        await trace.CompleteAsync(GuaTraceOutcome.Passed); var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Single(e => e.Type == "observation").Data.GetProperty("availability").GetString(), Is.EqualTo("failed"));
        Assert.That(read.Blobs, Is.Empty);
    }

    private sealed class FixedProfileWorld : IGuaWorldContext
    {
        public int Reads;
        public string GetWorldObjectTreeJson(GuaObservationProfile profile = GuaObservationProfile.Debug)
        { Reads++; return "{\"schemaVersion\":1,\"sessionEpoch\":1,\"frameSequence\":0,\"revision\":0,\"scene\":\"debug\",\"objects\":[]}"; }
        public GuaWorldTree GetWorldObjectTree(GuaObservationProfile profile = GuaObservationProfile.Debug) => throw new NotSupportedException();
        public GuaWorldQueryResult QueryWorldObjects(GuaWorldSelector selector, GuaObservationProfile profile = GuaObservationProfile.Debug) => throw new NotSupportedException();
        public Task<GuaWorldObject> WaitForWorldObjectAsync(GuaWorldSelector selector, TimeSpan? timeout = null,
            GuaObservationProfile profile = GuaObservationProfile.Debug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Test]
    public async Task FixedProfileWorldIsRejectedBeforeReadingEvenAValidTree()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, Profile = "player" });
        var context = new FixedProfileWorld(); var step = trace.BeginStep(GuaTraceStepKind.Assertion, "profile");
        GuaTraceCapture.World(trace, step, context, "remote", "capture");
        Assert.That(context.Reads, Is.Zero);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Blobs, Is.Empty);
    }

    [TestCase(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.OnFailure)]
    [TestCase(GuaTraceCaptureMode.Recent, GuaTraceSavePolicy.Always)]
    [TestCase(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.OnFailure)]
    [TestCase(GuaTraceCaptureMode.Streaming, GuaTraceSavePolicy.Always)]
    public async Task SavingPoliciesCoverPassedFailedAndInterrupted(GuaTraceCaptureMode mode, GuaTraceSavePolicy policy)
    {
        foreach (var outcome in new[] { GuaTraceOutcome.Passed, GuaTraceOutcome.Failed, GuaTraceOutcome.Interrupted, GuaTraceOutcome.Unknown })
        {
            await using var trace = new GuaTraceSession(Options(mode, policy));
            trace.Mark("checkpoint");
            Assert.That(await trace.CompleteAsync(outcome), Is.True);
            if (outcome == GuaTraceOutcome.Passed && policy == GuaTraceSavePolicy.OnFailure)
                Assert.That(Directory.Exists(trace.ArtifactPath), Is.False);
            else
            {
                var read = GuaTraceReader.Read(trace.ArtifactPath);
                Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo(outcome.ToString().ToLowerInvariant()));
                Assert.That(read.Manifest.Finalized, Is.True);
                Assert.That(read.Issues, Is.Empty);
                Assert.That(read.Events, Has.Count.EqualTo(2));
            }
        }
    }
    [Test]
    public async Task RecentRetainsOneHundredUserStepsAndAllPhasesAndReferences()
    {
        await using var trace = new GuaTraceSession(Options());
        for (var i = 0; i < 105; i++)
        {
            var step = trace.BeginStep(GuaTraceStepKind.Action, "action " + i);
            trace.Observe(step, "ui", "before", "available", new("runtime", "1", i.ToString()), Json("{\"bounds\":{\"x\":" + i + "}}"));
            for (var j = 0; j < 5; j++) trace.Record(step, "native.phase", Json("{}"));
            trace.EndStep(step, GuaTraceOutcome.Passed);
        }
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(100));
        Assert.That(read.Events.Count(e => e.Type == "native.phase"), Is.EqualTo(500));
        Assert.That(read.Blobs.Count, Is.EqualTo(100));
        Assert.That(read.Manifest.Quality.EvictedSteps, Is.EqualTo(5));
        Assert.That(read.Issues, Is.Empty);
    }
    [Test]
    public async Task IdenticalSanitizedBlobsKeepDistinctObservationTimesAndHostClocks()
    {
        await using var trace = new GuaTraceSession(Options());
        var step = trace.BeginStep(GuaTraceStepKind.Action, "inspect");
        var a = trace.Observe(step, "ui", "before", "available", new("ui", "1", "2", "1", "10", "host-ui"), Json("{\"sensitive\":true,\"value\":\"SECRET-A\"}"));
        var b = trace.Observe(step, "ui", "main-result", "available", new("ui", "1", "3", "1", "10", "host-ui"), Json("{\"sensitive\":true,\"value\":\"SECRET-B\"}"));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(a, Is.Not.EqualTo(b)); Assert.That(read.Blobs.Count, Is.EqualTo(1));
        var observations = read.Events.Where(e => e.Type == "observation").ToArray();
        Assert.That(observations[0].Data.GetProperty("host").GetProperty("frame").GetString(), Is.EqualTo("2"));
        Assert.That(observations[1].Data.GetProperty("host").GetProperty("frame").GetString(), Is.EqualTo("3"));
        Assert.That(string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText)), Does.Not.Contain("SECRET-"));
    }
    [Test]
    public async Task CorrelationDistinguishesSourcesEpochsAndLateResultsWithoutRewritingPrimary()
    {
        await using var trace = new GuaTraceSession(Options());
        var request = new GuaTraceRequest("a", "1", "18446744073709551615");
        var step = trace.BeginStep(GuaTraceStepKind.Action, "press", request);
        Assert.That(trace.BeginStep(GuaTraceStepKind.Action, "native", request), Is.EqualTo(step));
        var otherRequest = request with { SourceId = "b" };
        var resetRequest = request with { SessionEpoch = "2" };
        var other = trace.BeginStep(GuaTraceStepKind.Action, "other", otherRequest);
        var reset = trace.BeginStep(GuaTraceStepKind.Action, "reset", resetRequest);
        Assert.That(new[] { step, other, reset }.Distinct().Count(), Is.EqualTo(3));
        Assert.That(trace.RecordRequest(otherRequest, "completion", Json("{\"succeeded\":false}")), Is.True);
        Assert.That(trace.RecordRequest(resetRequest, "completion", Json("{\"succeeded\":true}")), Is.True);
        trace.EndStep(other, GuaTraceOutcome.Failed);
        trace.EndStep(reset, GuaTraceOutcome.Passed);
        trace.EndStep(step, GuaTraceOutcome.Unknown);
        trace.SetPrimaryOutcome(GuaTraceOutcome.Failed);
        Assert.That(trace.RecordRequest(request, "late-completion", Json("{\"released\":true}")), Is.True);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("failed"));
        Assert.That(read.Events.Count(e => e.StepId == step && e.Type == "step.end"), Is.EqualTo(1));
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(3));
        foreach (var (expectedStep, expectedRequest) in new[] { (step, request), (other, otherRequest), (reset, resetRequest) })
        {
            var phase = read.Events.Single(e => e.StepId == expectedStep && e.Type == "request.phase");
            var key = phase.Data.GetProperty("request");
            Assert.That(key.GetProperty("sourceId").GetString(), Is.EqualTo(expectedRequest.SourceId));
            Assert.That(key.GetProperty("sessionEpoch").GetString(), Is.EqualTo(expectedRequest.SessionEpoch));
            Assert.That(key.GetProperty("requestId").GetString(), Is.EqualTo(expectedRequest.RequestId));
        }
        Assert.That(read.Events.Single(e => e.StepId == step && e.Type == "step.end").Data.GetProperty("outcome").GetString(), Is.EqualTo("unknown"));
        Assert.That(read.Events.Last().StepId, Is.EqualTo(step));
        Assert.That(read.Events.Last().Type, Is.EqualTo("request.phase"));
    }
    [Test]
    public async Task RetainedEvaluationReportsEvictedObservationRatherThanInventingEmptyState()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, MaxSteps = 1, SavePolicy = GuaTraceSavePolicy.Always });
        var old = trace.BeginStep(GuaTraceStepKind.Action, "before");
        var observation = trace.Observe(old, "ui", "before", "available", new("host", "1"), Json("{}"));
        var step = trace.BeginStep(GuaTraceStepKind.Assertion, "evaluate");
        trace.Record(step, "assertion.evaluation", Json("{\"observations\":[\"" + observation + "\"]}"));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Issues, Does.Contain("observation-outside-retention"));
    }
    [Test]
    public async Task InterruptedAndIncompleteTailExposeOnlyCompleteRecords()
    {
        var trace = new GuaTraceSession(Options(GuaTraceCaptureMode.Streaming));
        trace.Mark("complete record");
        Assert.That(await trace.FlushAsync(), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Issues, Does.Contain("unfinalized"));
        await trace.DisposeAsync();
        File.AppendAllText(Path.Combine(trace.ArtifactPath, "events.jsonl"), "{\"partial\":");
        read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events, Has.Count.EqualTo(2));
        Assert.That(read.Issues, Does.Contain("incomplete-tail"));
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("interrupted"));
    }
    [Test]
    public async Task ByteLimitStopsDetailsAndReservesFinalSummaryEvenWhenCallerPassed()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, MaxMemoryBytes = 1024 });
        var step = trace.BeginStep(GuaTraceStepKind.Mark, "limit");
        for (var i = 0; i < 20; i++) trace.Record(step, "detail", Json("{\"text\":\"" + new string('x', 200) + "\"}"));
        Assert.That(trace.Status.DetailStopped, Is.True);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("retained-byte-limit"));
        Assert.That(read.Manifest.Quality.DroppedEvents, Is.GreaterThan(0));
    }
    [Test]
    public async Task SavingFailureIsSecondaryAndDoesNotLeakFilesystemErrors(
        [Values(GuaTraceCaptureMode.Recent, GuaTraceCaptureMode.Streaming)] GuaTraceCaptureMode mode,
        [Values(GuaTraceOutcome.Passed, GuaTraceOutcome.Failed, GuaTraceOutcome.Interrupted)] GuaTraceOutcome primary)
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "file"); File.WriteAllText(blocker, "secret-marker");
        await using var trace = new GuaTraceSession(new() { OutputDirectory = blocker, CaptureMode = mode, SavePolicy = GuaTraceSavePolicy.Always });
        trace.Mark("failed write");
        Assert.That(trace.SetPrimaryOutcome(primary), Is.True);
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.False);
        Assert.That(trace.Status.Issues, Does.Contain("write-failed"));
        Assert.That(trace.SetPrimaryOutcome(GuaTraceOutcome.Failed), Is.False);
        Assert.That(File.Exists(blocker), Is.True, "the real file/directory collision must remain present");
        Assert.That(File.Exists(Path.Combine(trace.ArtifactPath, "manifest.json")), Is.False);
        Assert.Throws<DirectoryNotFoundException>(() => GuaTraceReader.Read(trace.ArtifactPath));
        Assert.That(string.Join("", trace.Status.Issues), Does.Not.Contain(blocker));
    }
    [Test]
    public async Task ReaderRejectsTraversalAndTamperedBlobAndFutureVersion()
    {
        await using var trace = new GuaTraceSession(Options());
        var step = trace.BeginStep(GuaTraceStepKind.Mark, "attachment");
        trace.Attach(step, "unknown.schema", Json("{\"x\":1}"));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var blob = Directory.GetFiles(Path.Combine(trace.ArtifactPath, "attachments")).Single();
        File.WriteAllText(blob, "{\"x\":2}");
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Issues, Does.Contain("blob-hash-mismatch"));
        var events = Path.Combine(trace.ArtifactPath, "events.jsonl");
        File.WriteAllText(events, File.ReadAllText(events).Replace("attachments/" + Path.GetFileName(blob), "../outside.json"));
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Issues, Does.Contain("blob-unavailable"));
        var manifest = Path.Combine(trace.ArtifactPath, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"schemaVersion\":1", "\"schemaVersion\":2"));
        Assert.Throws<InvalidDataException>(() => GuaTraceReader.Read(trace.ArtifactPath));
    }
    [Test]
    public async Task SecretsAndSourcePathsAreRemovedBeforeStorage()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, SavePolicy = GuaTraceSavePolicy.Always, Secrets = new[] { "secret-marker" } });
        var step = trace.BeginStep(GuaTraceStepKind.Mark, "secret-marker", sourceFile: "C:\\private\\Tests.cs", sourceLine: 12);
        trace.Record(step, "exception", Json("{\"message\":\"secret-marker\"}"));
        trace.Attach(step, "schema", Json("{\"unknown\":\"secret-marker\"}"));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var text = string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.That(text, Does.Not.Contain("secret-marker").And.Not.Contain("private"));
        Assert.That(text, Does.Contain("Tests.cs"));
    }

    [Test]
    public async Task PackagedViewerCreatesSelfContainedSafeReportWithoutSourceCheckout()
    {
        await using var trace = new GuaTraceSession(Options());
        trace.Mark("</script><img src=https://attacker.invalid onerror=alert(1)>");
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(_root, "report.html"));
        Assert.That(report.Succeeded, Is.True, report.Error);
        var html = File.ReadAllText(report.Path!);
        Assert.That(html, Does.Not.Contain("</script><img"));
        Assert.That(html, Does.Contain("connect-src 'none'").And.Contain("sha256-"));
        Assert.That(html, Does.Contain("gua-trace-data"));
        Assert.That(GuaTraceReport.WriteHtml(trace.ArtifactPath, report.Path!).Succeeded, Is.False, "must not overwrite artifacts");
    }

    private sealed class FakeContext : IGuaContext
    {
        internal readonly Queue<GuaActionEvent> Events = new();
        internal int Enqueued, Polls;
        internal Exception? Error;
        public string GetUiTreeJson() => "{\"nodes\":[]}";
        public GuaNodeState GetNodeState(string id) => default;
        public string FindNodeById(string id) => id;
        public string FindNodeByRole(string role, string? name = null) => "n";
        public string FindNodeByText(string text) => "n";
        public bool EnqueueClick(string id) => true;
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
        { Enqueued++; requestId = 7; if (Error is not null) throw Error; return GuaActionError.None; }
        public bool TryPollActionEvent(out GuaActionEvent e) => throw new AssertionException("Trace must not poll another queue");
        public bool TryPollActionEvent(ulong requestId, out GuaActionEvent e)
        { Assert.That(requestId, Is.EqualTo(7)); Polls++; if (Events.Count == 0) { e = default; return false; } e = Events.Dequeue(); return true; }
        public bool TryPollEvent(out GuaEvent e) => throw new AssertionException("Trace must not poll another queue");
    }
    [Test]
    public async Task CompletingAndDisposingConcurrentlyNeverThrows()
    {
        for (var i = 0; i < 100; i++)
        {
            var trace = new GuaTraceSession(Options());
            trace.Mark("race");
            var complete = trace.CompleteAsync(GuaTraceOutcome.Failed);
            var dispose = trace.DisposeAsync().AsTask();
            await Task.WhenAll(complete, dispose);
            Assert.That(complete.Result, Is.True);
        }
    }
    [TestCase(GuaTraceCaptureMode.Recent)]
    [TestCase(GuaTraceCaptureMode.Streaming)]
    public async Task ArtifactByteLimitIncludesActualLineEndings(GuaTraceCaptureMode mode)
    {
        const int limit = 100000;
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode,
            SavePolicy = GuaTraceSavePolicy.Always, MaxSteps = 10000, MaxArtifactBytes = limit, MaxQueueItems = 65536 });
        while (!trace.Status.DetailStopped) trace.Mark("x");
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        Assert.That(new FileInfo(Path.Combine(trace.ArtifactPath, "events.jsonl")).Length, Is.LessThanOrEqualTo(limit));
    }
    [Test]
    public async Task InvalidUtf8AndMissingDataAreRejectedBeforeReport()
    {
        await using var trace = new GuaTraceSession(Options()); trace.Mark("marker");
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var path = Path.Combine(trace.ArtifactPath, "events.jsonl");
        var bytes = File.ReadAllBytes(path);
        var index = System.Text.Encoding.UTF8.GetString(bytes).IndexOf("marker", StringComparison.Ordinal);
        bytes[index] = 255; File.WriteAllBytes(path, bytes);
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Issues, Does.Contain("invalid-record"));
        var e = Json("{\"schemaVersion\":1,\"traceId\":\"" + trace.TraceId + "\",\"sequence\":1,\"eventId\":\"e1\",\"stepId\":\"" + new string('a', 32) + "\",\"type\":\"mark\",\"collectedMilliseconds\":1}");
        File.WriteAllText(path, e.GetRawText() + "\n");
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Issues, Does.Contain("invalid-record"));
    }
    [Test]
    public async Task UnknownEnvelopeFieldsSurviveReaderAndReport()
    {
        await using var trace = new GuaTraceSession(Options()); trace.Mark("extensions");
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        foreach (var name in new[] { "manifest.json", "events.jsonl" })
        {
            var path = Path.Combine(trace.ArtifactPath, name);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\":1", "\"futureField\":\"future-marker\",\"schemaVersion\":1"));
        }
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(JsonSerializer.Serialize(read.Manifest), Does.Contain("future-marker"));
        Assert.That(JsonSerializer.Serialize(read.Events[0]), Does.Contain("future-marker"));
    }
    [Test]
    public async Task ReaderRejectsReversedCollectionTime()
    {
        await using var trace = new GuaTraceSession(Options()); trace.Mark("time");
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var lines = read.Events.Select((e, i) => JsonSerializer.Serialize(e with { CollectedMilliseconds = i == 0 ? 10 : 5 }, options));
        File.WriteAllText(Path.Combine(trace.ArtifactPath, "events.jsonl"), string.Join("\n", lines) + "\n");
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Issues, Does.Contain("invalid-record"));
    }
    [Test]
    public async Task AutomaticActionRecordingPreservesCompletionAndOriginalException()
    {
        await using var trace = new GuaTraceSession(Options());
        using var scope = GuaAssertionScope.Use(new() { Trace = trace });
        var context = new FakeContext();
        // Caller-known sensitive input remains protected even if a legacy host omits the marker.
        context.Events.Enqueue(new(7, GuaActionType.SetValue, true, GuaActionError.None, "n", "secret-marker", false, 1, 2, 3));
        var result = await GuaActionCompletion.EnqueueAndWaitAsync(context, new(GuaActionType.SetValue, "n", "secret-marker", Sensitive: true));
        Assert.That(result.Succeeded, Is.True); Assert.That(context.Polls, Is.EqualTo(1));
        var original = new InvalidOperationException("original"); context.Error = original;
        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => GuaActionCompletion.EnqueueAndWaitAsync(context, new(GuaActionType.Click, "n")));
        Assert.That(thrown, Is.SameAs(original));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(2));
        Assert.That(File.ReadAllText(Path.Combine(trace.ArtifactPath, "events.jsonl")), Does.Not.Contain("secret-marker"));
    }
}
