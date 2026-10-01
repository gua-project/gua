using System.Reflection;
using System.Text.Json;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Visual.Tests;

public sealed class TraceStorageTests
{
    private string _root = null!;
    [SetUp] public void SetUp() => _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "trace-storage", Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static T Field<T>(GuaTraceSession trace, string name) =>
        (T)typeof(GuaTraceSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(trace)!;

    [Test]
    public async Task PublicIncompleteObservationIsRetainedUnderOnFailure(
        [Values(GuaTraceCaptureMode.Recent, GuaTraceCaptureMode.Streaming)] GuaTraceCaptureMode mode,
        [Values("partial", "gap", "stale", "failed", "outsideRetention")] string availability)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode });
        var step = trace.BeginStep(GuaTraceStepKind.Action, "public observation");
        var observation = trace.Observe(step, "world", "wait-end", availability, new("runtime", "1"));
        Assert.That(observation, Is.Not.Empty);
        trace.EndStep(step, GuaTraceOutcome.Passed);
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        Assert.That(Directory.Exists(trace.ArtifactPath), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("observation-" + availability));
        Assert.That(read.Events.Single(e => e.Type == "observation").Data.GetProperty("observationId").GetString(), Is.EqualTo(observation));
    }

    [Test]
    public async Task CompletePublicObservationDoesNotForceOnFailureRetention(
        [Values(GuaTraceCaptureMode.Recent, GuaTraceCaptureMode.Streaming)] GuaTraceCaptureMode mode,
        [Values("available", "absent")] string availability)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode });
        var step = trace.BeginStep(GuaTraceStepKind.Action, "public observation");
        trace.Observe(step, "world", "wait-end", availability, new("runtime", "1"));
        trace.EndStep(step, GuaTraceOutcome.Passed);
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        Assert.That(trace.Status.Issues, Is.Empty);
        Assert.That(Directory.Exists(trace.ArtifactPath), Is.False);
    }

    [TestCase(GuaTraceCaptureMode.Recent, GuaTraceOutcome.Unknown)]
    [TestCase(GuaTraceCaptureMode.Streaming, GuaTraceOutcome.Unknown)]
    [TestCase(GuaTraceCaptureMode.Recent, GuaTraceOutcome.Interrupted)]
    [TestCase(GuaTraceCaptureMode.Streaming, GuaTraceOutcome.Interrupted)]
    public async Task UnknownOrInterruptedStepIsNotDiscardedWithPassedPrimary(GuaTraceCaptureMode mode, GuaTraceOutcome outcome)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode });
        var step = trace.BeginStep(GuaTraceStepKind.Action, "uncertain side effect");
        trace.EndStep(step, outcome);
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("uncertain-step-outcome"));
    }

    [TestCase(GuaTraceCaptureMode.Recent)]
    [TestCase(GuaTraceCaptureMode.Streaming)]
    public async Task UnfinishedStepOutsideWindowStillPreventsSuccessDiscard(GuaTraceCaptureMode mode)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode, MaxSteps = 1 });
        trace.BeginStep(GuaTraceStepKind.Action, "lost completion");
        trace.Mark("completed last step");
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.Quality.EvictedSteps, Is.EqualTo(1));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("unfinished-steps"));
    }

    [Test]
    public async Task StreamingReaderDetectsMissingCompleteRecordEvenWhenLastSequenceMatches()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root,
            CaptureMode = GuaTraceCaptureMode.Streaming, SavePolicy = GuaTraceSavePolicy.Always });
        trace.Mark("first"); trace.Mark("second");
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Failed), Is.True);
        var path = Path.Combine(trace.ArtifactPath, "events.jsonl");
        File.WriteAllLines(path, File.ReadAllLines(path).Where((_, i) => i != 1));
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events, Has.Count.EqualTo(3));
        Assert.That(read.Issues, Does.Contain("sequence-gap"));
    }

    [Test]
    public async Task RecentCheckpointsKeepSharedBlobsAndMarkEvictedParentReference()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, MaxSteps = 2,
            SavePolicy = GuaTraceSavePolicy.Always });
        var parent = trace.Mark("parent");
        trace.Attach(parent, "test", Json(new { shared = true }));
        var child = trace.BeginStep(GuaTraceStepKind.Action, "child", parentStepId: parent);
        trace.Attach(child, "test", Json(new { shared = true }));
        trace.EndStep(child, GuaTraceOutcome.Passed);
        Assert.That(await trace.FlushAsync(), Is.True);
        trace.Mark("evict parent");
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Failed), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Blobs, Has.Count.EqualTo(1));
        Assert.That(Directory.GetFiles(Path.Combine(trace.ArtifactPath, "attachments")), Has.Length.EqualTo(1));
        Assert.That(read.Issues, Does.Contain("step-outside-retention"));
        Assert.That(read.Issues, Does.Not.Contain("sequence-gap"));
    }

    [TestCase(1, 4194304)]
    [TestCase(100000, 256)]
    public async Task ReaderLimitsReturnPrefixAndExplicitQualityIssue(int records, int recordBytes)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, SavePolicy = GuaTraceSavePolicy.Always });
        trace.Mark(new string('x', 512)); trace.Mark("second");
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Failed), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath, maxRecords: records, maxRecordBytes: recordBytes);
        Assert.That(read.Issues, Does.Contain("reader-limit"));
        Assert.That(read.Issues, Does.Contain("sequence-incomplete"));
    }

    [TestCase(GuaTraceCaptureMode.Recent)]
    [TestCase(GuaTraceCaptureMode.Streaming)]
    public async Task PassedPrimaryWithUnfinishedStepIsRetained(GuaTraceCaptureMode mode)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode });
        trace.BeginStep(GuaTraceStepKind.Action, "interrupted action");
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        Assert.That(Directory.Exists(trace.ArtifactPath), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Finalized, Is.True);
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("unfinished-steps"));
        Assert.That(read.Events.Count(e => e.Type == "step.end"), Is.Zero);
    }

    public static IEnumerable<TestCaseData> RetentionCases()
    {
        foreach (var mode in Enum.GetValues<GuaTraceCaptureMode>())
        foreach (var policy in Enum.GetValues<GuaTraceSavePolicy>())
        foreach (var outcome in Enum.GetValues<GuaTraceOutcome>())
            yield return new(mode, policy, outcome);
    }

    [TestCaseSource(nameof(RetentionCases))]
    public async Task MoreThanOneHundredStepsPreserveBlobsInEverySavingCombination(GuaTraceCaptureMode mode,
        GuaTraceSavePolicy policy, GuaTraceOutcome outcome)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode, SavePolicy = policy,
            MaxQueueItems = 1024 });
        for (var i = 0; i < 105; i++)
        {
            var step = trace.BeginStep(GuaTraceStepKind.Action, "action " + i);
            Assert.That(trace.Attach(step, "gua.recording.v1", Json(new { index = i })), Is.True);
            trace.EndStep(step, GuaTraceOutcome.Passed);
        }
        Assert.That(await trace.CompleteAsync(outcome), Is.True);
        if (outcome == GuaTraceOutcome.Passed && policy == GuaTraceSavePolicy.OnFailure)
        {
            Assert.That(Directory.Exists(trace.ArtifactPath), Is.False);
            return;
        }
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo(outcome.ToString().ToLowerInvariant()));
        var count = mode == GuaTraceCaptureMode.Recent ? 100 : 105;
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(count));
        Assert.That(read.Blobs, Has.Count.EqualTo(count));
        Assert.That(read.Manifest.Quality.EvictedSteps, Is.EqualTo(5));
        Assert.That(read.Issues, Is.Empty);
    }

    [TestCase(GuaTraceCaptureMode.Recent, "event")]
    [TestCase(GuaTraceCaptureMode.Streaming, "event")]
    [TestCase(GuaTraceCaptureMode.Recent, "attachment")]
    [TestCase(GuaTraceCaptureMode.Streaming, "attachment")]
    [TestCase(GuaTraceCaptureMode.Recent, "snapshot")]
    [TestCase(GuaTraceCaptureMode.Streaming, "snapshot")]
    public async Task IndividualPayloadLimitStopsDetailsAndKeepsSummary(GuaTraceCaptureMode mode, string payload)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode,
            MaxEventBytes = 512, MaxAttachmentBytes = 128 });
        var step = trace.BeginStep(GuaTraceStepKind.Action, "limit");
        var large = Json(new { text = new string('x', 1024) });
        if (payload == "event") Assert.That(trace.Record(step, "detail", large), Is.False);
        else if (payload == "attachment") Assert.That(trace.Attach(step, "test", large), Is.False);
        else Assert.That(trace.Observe(step, "ui", "before", "available", new("host", "1"), large), Is.Empty);
        Assert.That(trace.Status.DetailStopped, Is.True);
        Assert.That(trace.Record(step, "detail", Json(new { })), Is.False);
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain(payload + "-limit"));
        Assert.That(new FileInfo(Path.Combine(trace.ArtifactPath, "manifest.json")).Length, Is.LessThanOrEqualTo(65536));
        Assert.That(read.Blobs, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingWriterIsBoundedByQueueItemsAndBytes(bool byteLimit)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root,
            CaptureMode = GuaTraceCaptureMode.Streaming, MaxQueueItems = byteLimit ? 256 : 1,
            MaxMemoryBytes = byteLimit ? 1024 : 16384 });
        // The writer takes this lock before writing payloads. Holding it makes saturation deterministic.
        lock (Field<object>(trace, "_gate"))
        {
            var step = trace.BeginStep(GuaTraceStepKind.Action, "queue");
            for (var i = 0; i < 10 && !trace.Status.DetailStopped; i++)
                trace.Record(step, "detail", Json(new { text = new string('x', 200) }));
            Assert.That(trace.Status.Issues, Does.Contain(byteLimit ? "queue-byte-limit" : "queue-limit"));
            Assert.That(Field<long>(trace, "_queuedBytes"), Is.LessThanOrEqualTo(byteLimit ? 1024 : 16384));
        }
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Manifest.Quality.DetailStopped, Is.True);
    }

    [Test]
    public async Task FlushTimeoutIsSecondaryAndLaterFinalizationSavesQuality()
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, FlushTimeout = TimeSpan.FromMilliseconds(100) });
        trace.Mark("before timeout");
        var gate = Field<SemaphoreSlim>(trace, "_flushGate");
        await gate.WaitAsync();
        try { Assert.That(await trace.FlushAsync(), Is.False); }
        finally { gate.Release(); }
        Assert.That(trace.Status.Issues, Does.Contain("flush-timeout"));
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.False);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("passed"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("flush-timeout"));
    }

    [TestCase(GuaTraceCaptureMode.Recent)]
    [TestCase(GuaTraceCaptureMode.Streaming)]
    public async Task SensitivePayloadsAreRedactedBeforeRetentionQueueAndHash(GuaTraceCaptureMode mode)
    {
        await using var trace = new GuaTraceSession(new() { OutputDirectory = _root, CaptureMode = mode,
            SavePolicy = GuaTraceSavePolicy.Always, Profile = "player", Secrets = new[] { "secret-marker" } });
        lock (Field<object>(trace, "_gate"))
        {
            var step = trace.BeginStep(GuaTraceStepKind.Action, "secret-marker");
            trace.Record(step, "exception", Json(new { message = "secret-marker" }));
            trace.Record(step, "arguments", Json(new { value = "secret-marker" }), sensitive: true);
            trace.Observe(step, "ui", "before", "available", new("host", "1"), Json(new { sensitive = true, value = "secret-marker" }));
            trace.Attach(step, "test", Json(new { value = "secret-marker" }));
            trace.EndStep(step, GuaTraceOutcome.Passed);
            // Serialize only retained batches/steps, never the caller-owned configured secret list.
            var retained = typeof(GuaTraceSession).GetField(mode == GuaTraceCaptureMode.Recent ? "_steps" : "_queue",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(trace) as System.Collections.IEnumerable;
            foreach (var item in retained!)
            {
                var text = JsonSerializer.Serialize(item, item!.GetType(), new JsonSerializerOptions { IncludeFields = true });
                Assert.That(text, Does.Not.Contain("secret-marker"));
                // Private Step fields require explicit inspection; Batch exposes its public record properties.
                foreach (var field in item.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    Assert.That(JsonSerializer.Serialize(field.GetValue(item)), Does.Not.Contain("secret-marker"));
                var blobs = item.GetType().GetProperty("Blobs")?.GetValue(item) ??
                    item.GetType().GetField("Blobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item);
                var rawHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Json(new { value = "secret-marker" }).GetRawText()))).ToLowerInvariant();
                foreach (var blob in (Dictionary<string, byte[]>)blobs!)
                {
                    Assert.That(System.Text.Encoding.UTF8.GetString(blob.Value), Does.Not.Contain("secret-marker"));
                    Assert.That(blob.Key, Does.Not.Contain(rawHash), "hash must describe the sanitized bytes");
                }
            }
        }
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Failed), Is.True);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.Profile, Is.EqualTo("player"));
        Assert.That(read.Issues, Is.Empty);
        Assert.That(string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText)), Does.Not.Contain("secret-marker"));
    }
}
