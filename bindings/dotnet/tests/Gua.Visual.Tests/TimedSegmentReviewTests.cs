using System.Text.Json;
using System.Reflection;
using Json.Schema;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Testing.Recording;
using NUnit.Framework;

namespace Gua.Visual.Tests;

public sealed partial class TimedSegmentTests
{
    [Test]
    public async Task Review_TraceSerializesTheValidatedResultSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-result-snapshot", Guid.NewGuid().ToString("N"));
        var initial = new GuaTimedInputResult(0, 0, 0, 1, 1, null, true, 0);
        var changing = new ChangingResults(initial, initial with { ScheduledMilliseconds = 50 });
        var result = new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded, changing, true, true, null)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 };
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, result), Is.True);
            trace.EndStep(step, GuaTraceOutcome.Passed);
            Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
            using var attachment = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(trace.ArtifactPath, "attachments"), "*.json").Single()));
            Assert.That(attachment.RootElement.GetProperty("result").GetProperty("inputs")[0].GetProperty("scheduledMilliseconds").GetInt64(), Is.Zero);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ChangingResults(GuaTimedInputResult initial, GuaTimedInputResult replacement) : IReadOnlyList<GuaTimedInputResult>
    {
        private int reads;
        public int Count => 1;
        public GuaTimedInputResult this[int index] => initial;
        public IEnumerator<GuaTimedInputResult> GetEnumerator() { yield return reads++ == 0 ? initial : replacement; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Review_ValueHostPreflightExceptionsCannotExposeResolvedSecrets(int exceptionKind)
    {
        var input = new GuaTimedInput(0, GuaGameInputKind.TextInput, GuaGameInputOperation.Set, "", Sensitive: true, SecretKey: "chat");
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [input]);
        var host = new ThrowingValueHost(exceptionKind);
        var error = Assert.CatchAsync(() => GuaTimedSegmentReplay.ReplayAsync(host, plan,
            _ => JsonSerializer.SerializeToElement("secret-marker-preflight"), realtime: new FakeRealtime()));
        Assert.That(error!.ToString(), Does.Not.Contain("secret-marker-preflight"));
        Assert.That(error.InnerException, Is.Null);
        Assert.That(host.Sent, Is.False);
        Assert.That(error.GetType(), Is.EqualTo(exceptionKind == 0 ? typeof(InvalidDataException) :
            exceptionKind == 1 ? typeof(NotSupportedException) : typeof(InvalidOperationException)));
    }

    [TestCase(" ", false)]
    [TestCase("\t\r\n", false)]
    [TestCase("\u0085\u00a0", false)]
    [TestCase("\u2007\u2028\u2029\u202f\u3000", false)]
    [TestCase(" chat ", true)]
    public void Review_SecretReferenceWhitespaceMatchesManagedValidation(string key, bool valid)
    {
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100,
            [new(0, GuaGameInputKind.TextInput, GuaGameInputOperation.Set, "", Sensitive: true, SecretKey: key)]);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var path = Path.GetFullPath("../../../../../../../protocol/schema/timed-segment-v1.schema.json", TestContext.CurrentContext.TestDirectory);
        Assert.That(JsonSchema.FromFile(path).Evaluate(document.RootElement).IsValid, Is.EqualTo(valid));
        if (valid) Assert.DoesNotThrow(() => GuaTimedSegmentFile.Validate(plan));
        else Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan));
    }

    [Test]
    public void Review_SaveSerializesTheValidatedInputSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "gua-timed-snapshot-" + Guid.NewGuid().ToString("N") + ".json");
        var input = new GuaTimedInput(0, GuaGameInputKind.TextInput, GuaGameInputOperation.Set, "", Sensitive: true, SecretKey: "chat");
        var changing = new ChangingInputs(input, input with { Value = JsonSerializer.SerializeToElement("secret-marker-save") });
        try
        {
            GuaTimedSegmentFile.Save(path, new(1, 0, 20, 100, 100, changing));
            Assert.That(File.ReadAllText(path), Does.Not.Contain("secret-marker-save"));
            Assert.That(GuaTimedSegmentFile.Load(path).Inputs.Single(), Is.EqualTo(input));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class ChangingInputs(GuaTimedInput initial, GuaTimedInput replacement) : IReadOnlyList<GuaTimedInput>
    {
        private int reads;
        public int Count => 1;
        public GuaTimedInput this[int index] => reads == 0 ? initial : replacement;
        public IEnumerator<GuaTimedInput> GetEnumerator() { yield return reads++ == 0 ? initial : replacement; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingValueHost(int exceptionKind) : IGuaTimedSegmentValueHost
    {
        public bool OrderedApplication => true;
        public bool ApplicationTimes => false;
        public bool SameTickApplication => false;
        public string? SimulationScope => null;
        public double SimulationMilliseconds => 0;
        public string? ExecutionFailureCode => null;
        public bool Sent { get; private set; }
        public void Begin(GuaTimedSegment segment) => throw new InvalidOperationException();
        public void Begin(GuaTimedSegment segment, IReadOnlyList<JsonElement?> values)
        {
            var message = values[0]!.Value.GetString();
            throw exceptionKind == 0 ? new InvalidDataException(message) : exceptionKind == 1 ?
                new NotSupportedException(message) : new InvalidOperationException(message);
        }
        public ulong Send(GuaTimedInput input, JsonElement? secret, Action guard) { Sent = true; return 1; }
        public GuaTimedCompletion? Poll(ulong id) => null;
        public ulong ReleaseAll() => 1;
        public bool IsNeutral => true;
        public void End() { }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Review_PartialApplyStampsStillProveOrder(bool betweenKnown)
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { Applied = id => id == 1 ? 20 : id == (betweenKnown ? 3ul : 2ul) ? 10 : null };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100,
            Enumerable.Range(0, 3).Select(_ => new GuaTimedInput(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")).ToArray());
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Failed));
        Assert.That(result.FailureCode, Is.EqualTo("application-order-or-tick-violation"));
        Assert.That(result.ApplicationTimingConfirmed, Is.False);
        var forged = result with { Outcome = GuaSegmentOutcome.Succeeded, FailureCode = null };
        AssertResultAttachment(forged, false);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Review_CompletedOrdinaryWorkNeedsOnlyOneFinalRelease(bool nearDeadline)
    {
        var clock = new FakeRealtime();
        var releases = 0;
        var host = new FakeHost(clock) { OnRelease = () =>
            { releases++; if (releases > 1) throw new InvalidOperationException("redundant cleanup"); if (nearDeadline) clock.Milliseconds += 99; } };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded)); Assert.That(releases, Is.EqualTo(1));
        Assert.That(result.CleanupSucceeded, Is.True); Assert.That(result.NeutralConfirmed, Is.True);
    }

    [Test]
    public async Task Review_OutstandingOrdinaryWorkRetainsSecondSafetyRelease()
    {
        using var cancel = new CancellationTokenSource();
        var clock = new FakeRealtime(); clock.OnDelay = () => cancel.Cancel();
        var host = new FakeHost(clock) { ResultDelay = 5 };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, cancellationToken: cancel.Token, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Cancelled)); Assert.That(host.CleanupCount, Is.EqualTo(2));
        Assert.That(result.NeutralConfirmed, Is.True);
    }

    [TestCase(GuaSegmentOutcome.Succeeded, GuaSegmentClock.Realtime, 5, 4, false)]
    [TestCase(GuaSegmentOutcome.Succeeded, GuaSegmentClock.Realtime, 0, 50, false)]
    [TestCase(GuaSegmentOutcome.Succeeded, GuaSegmentClock.Realtime, 0, 20, true)]
    [TestCase(GuaSegmentOutcome.Succeeded, GuaSegmentClock.Realtime, 0, null, true)]
    [TestCase(GuaSegmentOutcome.Cancelled, GuaSegmentClock.Realtime, 0, 50, true)]
    [TestCase(GuaSegmentOutcome.Succeeded, GuaSegmentClock.Simulation, 50, 70, true)]
    public void Review_KnownApplyEvidenceCannotContradictSuccessfulOutcome(GuaSegmentOutcome outcome,
        GuaSegmentClock clock, long scheduled, double? applied, bool valid) =>
        AssertResultAttachment(new GuaTimedSegmentResult(outcome,
            [new(0, scheduled, clock == GuaSegmentClock.Realtime ? scheduled : 0, 1,
                clock == GuaSegmentClock.Realtime ? Math.Max(scheduled, applied ?? scheduled) + 1 : 1, applied, true, 0)],
            true, true, outcome == GuaSegmentOutcome.Succeeded ? null : "caller-cancelled")
            { Clock = clock, SimulationScope = clock == GuaSegmentClock.Simulation ? "controlled" : null,
              MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, valid);

    [TestCase(GuaSegmentOutcome.Failed, false)]
    [TestCase(GuaSegmentOutcome.Cancelled, false)]
    [TestCase(GuaSegmentOutcome.TimedOut, false)]
    [TestCase(GuaSegmentOutcome.Late, false)]
    [TestCase(GuaSegmentOutcome.Failed, true)]
    [TestCase(GuaSegmentOutcome.Cancelled, true)]
    [TestCase(GuaSegmentOutcome.TimedOut, true)]
    [TestCase(GuaSegmentOutcome.Late, true)]
    public void Review_ConfirmedNeutralityRequiresSuccessfulCleanup(GuaSegmentOutcome outcome, bool cleanup)
    {
        var code = outcome switch { GuaSegmentOutcome.Cancelled => "caller-cancelled", GuaSegmentOutcome.TimedOut => "execution-timeout",
            GuaSegmentOutcome.Late => "max-lateness-exceeded", _ => "host-health-failed" };
        AssertResultAttachment(new GuaTimedSegmentResult(outcome, [new(0, 0, 0, 1, 1, null, true, 0)], cleanup, true, code)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, cleanup);
    }

    [TestCase(GuaSegmentClock.Realtime, 0, false)]
    [TestCase(GuaSegmentClock.Realtime, 50, true)]
    [TestCase(GuaSegmentClock.Realtime, 70, true)]
    [TestCase(GuaSegmentClock.Realtime, 71, false)]
    [TestCase(GuaSegmentClock.Simulation, 0, true)]
    public void Review_SendEvidenceRespectsItsScheduleClock(GuaSegmentClock clock, double sent, bool valid) =>
        AssertResultAttachment(new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded,
            [new(0, 50, sent, 1, sent + 1, null, true, 0)], true, true, null)
            { Clock = clock, SimulationScope = clock == GuaSegmentClock.Simulation ? "controlled" : null,
              MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, valid);

    [TestCase(100, 101, false)]
    [TestCase(0, 100, false)]
    [TestCase(0, 99, true)]
    public void Review_SuccessfulEvidenceMustPrecedeExecutionDeadline(double sent, double received, bool valid) =>
        AssertResultAttachment(new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded,
            [new(0, 0, sent, 1, received, null, true, 0)], true, true, null)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, valid);

    [TestCase(999, 1, false)]
    [TestCase(0, 0, false)]
    [TestCase(1, 0, false)]
    [TestCase(0, 1, true)]
    public void Review_ResultIndexesFollowOriginalArray(int first, int second, bool valid) =>
        AssertResultAttachment(new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded,
            [new(first, 0, 0, 1, 1, null, true, 0), new(second, 0, 0, 2, 1, null, true, 0)], true, true, null)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, valid);

    [TestCase("18446744073709551615", true)]
    [TestCase("18446744073709551616", false)]
    [TestCase("99999999999999999999", false)]
    [TestCase("18446744073709551615\n", false)]
    [TestCase("0", false)]
    [TestCase("9999999999999999999", true)]
    public void Review_RequestIdSchemaMatchesNativeUInt64(string requestId, bool valid)
    {
        var schema = Path.GetFullPath("../../../../../../../protocol/schema/timed-segment-result-v1.schema.json", TestContext.CurrentContext.TestDirectory);
        var node = System.Text.Json.Nodes.JsonNode.Parse("{\"schemaVersion\":1,\"result\":{\"outcome\":0,\"cleanupSucceeded\":true,\"neutralConfirmed\":true,\"failureCode\":null,\"clock\":0,\"simulationScope\":null,\"maxLatenessMilliseconds\":20,\"executionTimeoutMilliseconds\":100,\"cleanupTimeoutMilliseconds\":100,\"applicationTimingConfirmed\":false,\"inputs\":[{\"index\":0,\"scheduledMilliseconds\":0,\"sentMilliseconds\":0,\"requestId\":\"1\",\"resultReceivedMilliseconds\":1,\"hostAppliedMilliseconds\":null,\"succeeded\":true,\"errorCode\":0}]}}")!;
        node["result"]!["inputs"]![0]!["requestId"] = requestId;
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.That(Json.Schema.JsonSchema.FromFile(schema).Evaluate(document.RootElement).IsValid, Is.EqualTo(valid));
    }

    [TestCase("applied-only", false)]
    [TestCase("receipt-no-id", false)]
    [TestCase("receipt-no-status", false)]
    [TestCase("receipt-no-error", false)]
    [TestCase("status-only", false)]
    [TestCase("id-no-send", false)]
    [TestCase("unknown-send", true)]
    [TestCase("pending", true)]
    [TestCase("complete", true)]
    public void Review_PartialEvidencePreservesRequestLifecycle(string shape, bool valid)
    {
        var input = new GuaTimedInputResult(0, 0, 0, 1, 1, null, false, 1);
        input = shape switch
        {
            "applied-only" => new(0, 0, null, null, null, 1, null, null),
            "receipt-no-id" => input with { RequestId = null },
            "receipt-no-status" => input with { Succeeded = null },
            "receipt-no-error" => input with { ErrorCode = null },
            "status-only" => new(0, 0, null, null, null, null, false, 1),
            "id-no-send" => new(0, 0, null, 1, null, null, null, null),
            "unknown-send" => new(0, 0, 0, null, null, null, null, null),
            "pending" => new(0, 0, 0, 1, null, null, null, null), _ => input,
        };
        AssertResultAttachment(new GuaTimedSegmentResult(GuaSegmentOutcome.Cancelled, [input], false, false, "caller-cancelled")
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, valid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Review_PlannedSemanticReleaseRejectsChangedMap(bool afterEnqueue)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new("move", "move", GuaGameInputValueType.Axis1D)]);
        var plan = new GuaTimedSegment(1, 1, 20, 100, 100,
            [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "move", JsonSerializer.SerializeToElement(0.5), 5000),
             new(1, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "move")]);
        var host = new GuaRuntimeSegmentHost(runtime, true); host.Begin(plan);
        try
        {
            host.Send(plan.Inputs[0], null, () => { });
            Assert.That(runtime.TryConsumeGameInput(out var hold), Is.True); runtime.CompleteGameInput(hold, true);
            ulong id = 0;
            if (afterEnqueue) id = host.Send(plan.Inputs[1], null, () => { });
            runtime.PublishGameInputActions("test", [new("move", "replacement", GuaGameInputValueType.Axis1D)]);
            if (!afterEnqueue) Assert.Throws<InvalidOperationException>(() => host.Send(plan.Inputs[1], null, () => { }));
            else { Assert.That(runtime.TryConsumeGameInput(out _), Is.False); Assert.That(host.Poll(id)!.Succeeded, Is.False); }
            var cleanup = host.ReleaseAll();
            while (runtime.TryConsumeGameInput(out var request)) runtime.CompleteGameInput(request, true);
            Assert.That(host.Poll(cleanup)!.Succeeded, Is.True); Assert.That(host.IsNeutral, Is.True);
        }
        finally { host.End(); }
    }

    [Test]
    public void Review_ReleaseOnlyPlanUsesCurrentRevision()
    {
        using var runtime = new GuaRuntime(); runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new("move", "move", GuaGameInputValueType.Axis1D)]);
        var input = new GuaTimedInput(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "move");
        var host = new GuaRuntimeSegmentHost(runtime, true); host.Begin(new(1, 0, 20, 100, 100, [input]));
        try
        {
            var id = host.Send(input, null, () => { });
            Assert.That(runtime.TryConsumeGameInput(out var request), Is.True); runtime.CompleteGameInput(request, true);
            Assert.That(host.Poll(id)!.Succeeded, Is.True);
        }
        finally { host.End(); }
    }

    [TestCase(GuaGameInputKind.Gamepad, false)]
    [TestCase(GuaGameInputKind.TextInput, false)]
    [TestCase(GuaGameInputKind.Cleanup, false)]
    [TestCase(GuaGameInputKind.Gamepad, true)]
    [TestCase(GuaGameInputKind.TextInput, true)]
    [TestCase(GuaGameInputKind.Cleanup, true)]
    public void Review_TargetlessOperationsRequireAnEmptyTarget(GuaGameInputKind kind, bool valid)
    {
        var input = new GuaTimedInput(0, kind, kind == GuaGameInputKind.Gamepad ? GuaGameInputOperation.Reset :
            kind == GuaGameInputKind.TextInput ? GuaGameInputOperation.Set : GuaGameInputOperation.ReleaseAll,
            valid ? "" : "ignored-private-data", kind == GuaGameInputKind.TextInput ? JsonSerializer.SerializeToElement("text") : null);
        AssertTimedFileAndSchema(new(1, 0, 20, 100, 100, [input]), valid);
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase(" ", false)]
    [TestCase("controlled-subsystem", true)]
    public void Review_SimulationEvidenceRequiresScope(string? scope, bool accepted) =>
        AssertResultAttachment(new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded,
            [new(0, 0, 0, 1, 1, null, true, 0)], true, true, null)
            { Clock = GuaSegmentClock.Simulation, SimulationScope = scope, MaxLatenessMilliseconds = 20,
              ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, accepted);

    [TestCase(GuaSegmentOutcome.Cancelled, "host-rejected-or-failed", false)]
    [TestCase(GuaSegmentOutcome.Cancelled, "caller-cancelled", true)]
    [TestCase(GuaSegmentOutcome.TimedOut, null, false)]
    [TestCase(GuaSegmentOutcome.TimedOut, "execution-timeout", true)]
    [TestCase(GuaSegmentOutcome.TimedOut, "completion-or-boundary-timeout", true)]
    [TestCase(GuaSegmentOutcome.Late, "caller-cancelled", false)]
    [TestCase(GuaSegmentOutcome.Late, "max-lateness-exceeded", true)]
    [TestCase(GuaSegmentOutcome.Late, "send-exceeded-max-lateness", true)]
    [TestCase(GuaSegmentOutcome.Late, "application-time-violation", true)]
    [TestCase(GuaSegmentOutcome.Failed, null, false)]
    [TestCase(GuaSegmentOutcome.Failed, "caller-cancelled", false)]
    [TestCase(GuaSegmentOutcome.Failed, "host-health-failed", true)]
    public void Review_OutcomeMustMatchFailureCode(GuaSegmentOutcome outcome, string? code, bool accepted) =>
        AssertResultAttachment(new GuaTimedSegmentResult(outcome, [new(0, 0, 0, 1, 1, null, false, 1)], false, false, code)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 }, accepted);

    private static void AssertResultAttachment(GuaTimedSegmentResult result, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-result-contract", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, result), Is.EqualTo(accepted));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Review_PreCancelledReplayAvoidsCapabilitiesAndSecrets(bool unavailable)
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var host = new FakeHost(new()) { OrderedApplication = !unavailable };
        var resolved = false;
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100,
            [new(0, GuaGameInputKind.TextInput, GuaGameInputOperation.Set, "", Sensitive: true, SecretKey: "vault")]);
        Assert.ThrowsAsync<OperationCanceledException>(async () => await GuaTimedSegmentReplay.ReplayAsync(host, plan,
            _ => { resolved = true; throw new InvalidOperationException(); }, cancel.Token));
        Assert.That(resolved, Is.False); Assert.That(host.Began, Is.False);
    }

    [TestCase("{\"x\":0,\"x\":100,\"y\":0}", false)]
    [TestCase("{\"x\":0,\"\\u0078\":100,\"y\":0}", false)]
    [TestCase("{\"extra\":{\"x\":0},\"x\":100,\"y\":0}", false)]
    [TestCase("{\"extra\":1,\"x\":0.5,\"y\":0}", true)]
    public void Review_VectorParsingCannotBypassNativeRange(string json, bool valid)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new("vector", "vector", GuaGameInputValueType.Vector2, -1, 1)]);
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        var map = runtime.FindGameInputActionsV2(new(Limit: 1));
        using var owner = runtime.CreateGameInputSession();
        if (valid) Assert.DoesNotThrow(() => owner.SendGuarded(map.SessionEpoch, map.Revision, GuaGameInputKind.Semantic,
            GuaGameInputOperation.Set, "vector", value, TimeSpan.FromSeconds(5), 0, 0, 0, false, false, () => { }));
        else Assert.Throws<InvalidOperationException>(() => owner.SendGuarded(map.SessionEpoch, map.Revision, GuaGameInputKind.Semantic,
            GuaGameInputOperation.Set, "vector", value, TimeSpan.FromSeconds(5), 0, 0, 0, false, false, () => { }));
        if (json.Contains("100") && !json.Contains("extra"))
        {
            var plan = new GuaTimedSegment(1, 1, 20, 100, 100,
                [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "vector", value, 5000, SemanticValueType: GuaGameInputValueType.Vector2),
                 new(1, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "vector")]);
            Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan));
        }
    }

    [TestCase("Bad ID", false)]
    [TestCase("Upper", false)]
    [TestCase("é", false)]
    [TestCase("long", false)]
    [TestCase("limit", true)]
    [TestCase("move.forward-1", true)]
    public void Review_SemanticIdGrammarMatchesSchemaAndFile(string target, bool valid)
    {
        if (target == "long") target = new string('a', 128);
        if (target == "limit") target = new string('a', 127);
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100,
            [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, target)]);
        AssertTimedFileAndSchema(plan, valid);
    }

    [TestCase(false, false, 41, false)]
    [TestCase(true, false, 41, false)]
    [TestCase(false, true, 41, false)]
    [TestCase(true, true, 41, false)]
    [TestCase(false, true, 40, true)]
    [TestCase(true, true, 40, true)]
    [TestCase(false, false, 0, true)]
    public void Review_TextLimitCountsUnicodeCodePoints(bool semantic, bool astral, int count, bool valid)
    {
        var text = string.Concat(Enumerable.Repeat(astral ? "\U0001F600" : "a", count));
        var input = new GuaTimedInput(0, semantic ? GuaGameInputKind.Semantic : GuaGameInputKind.TextInput,
            GuaGameInputOperation.Set, semantic ? "text" : "", JsonSerializer.SerializeToElement(text),
            SemanticValueType: semantic ? GuaGameInputValueType.Text : null);
        AssertTimedFileAndSchema(new(1, 0, 20, 100, 100, [input]), valid);
    }

    [TestCase(GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]
    [TestCase(GuaGameInputKind.Keyboard, GuaGameInputOperation.Down, "KeyW")]
    [TestCase(GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "jump")]
    [TestCase(GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "move")]
    [TestCase(GuaGameInputKind.Pointer, GuaGameInputOperation.MoveDelta, "delta:")]
    [TestCase(GuaGameInputKind.Gamepad, GuaGameInputOperation.Reset, "")]
    [TestCase(GuaGameInputKind.Cleanup, GuaGameInputOperation.ReleaseAll, "")]
    public void Review_ValuelessOperationsRejectIgnoredPayloads(GuaGameInputKind kind, GuaGameInputOperation operation, string target)
    {
        var input = new GuaTimedInput(0, kind, operation, target, JsonSerializer.SerializeToElement("ignored-private-data"));
        var inputs = operation == GuaGameInputOperation.Down ? new[] { input, input with { OffsetMilliseconds = 1,
            Operation = GuaGameInputOperation.Up, Value = null } } : new[] { input };
        AssertTimedFileAndSchema(new(1, 1, 20, 100, 100, inputs), false);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Review_TraceRequestCorrelationMustBeUnique(bool duplicate)
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-unique-correlation", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            var first = new GuaTimedInputResult(0, 0, 0, 1, 1, null, true, 0);
            var result = new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded,
                [first, first with { Index = 1, RequestId = duplicate ? 1ul : 2ul }], true, true, null)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 };
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, result), Is.EqualTo(!duplicate));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AssertTimedFileAndSchema(GuaTimedSegment plan, bool valid)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var schema = Path.GetFullPath("../../../../../../../protocol/schema/timed-segment-v1.schema.json", TestContext.CurrentContext.TestDirectory);
        Assert.That(Json.Schema.JsonSchema.FromFile(schema).Evaluate(document.RootElement).IsValid, Is.EqualTo(valid));
        if (valid) Assert.DoesNotThrow(() => GuaTimedSegmentFile.Validate(plan));
        else Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan));
        var root = Path.Combine(Path.GetTempPath(), "gua-file-contract", Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(root, "segment.json");
            if (valid) Assert.DoesNotThrow(() => GuaTimedSegmentFile.Save(file, plan));
            else Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Save(file, plan));
            Directory.CreateDirectory(root);
            File.WriteAllText(file, document.RootElement.GetRawText());
            if (valid) Assert.DoesNotThrow(() => GuaTimedSegmentFile.Load(file));
            else Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Load(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task Review_NativeResetBetweenFinalCompletionAndNeutralReadCannotConfirmBoundary()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
        var context = (GuaContext)typeof(GuaRuntime).GetField("_observations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        var clock = new FakeRealtime();
        clock.OnDelay = () => { while (runtime.TryConsumeGameInput(out var request)) runtime.CompleteGameInput(request, true); };
        var inner = new GuaRuntimeSegmentHost(runtime, true);
        var host = new NeutralBoundaryHost(inner, () => Assert.That(context.Reset().Result, Is.EqualTo(GuaResetResult.Succeeded)));
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Failed));
        Assert.That(result.FailureCode, Is.EqualTo("lifecycle-or-session-unconfirmed"));
        Assert.That(result.NeutralConfirmed, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Review_DirectAndLoadedHoldPlansUseTheEffectiveDefaultLease(bool omitInFile)
    {
        var plan = new GuaTimedSegment(1, 10, 20, 100, 100,
            [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Down, "KeyW"),
             new(10, GuaGameInputKind.Keyboard, GuaGameInputOperation.Up, "KeyW")]);
        var root = Path.Combine(Path.GetTempPath(), "gua-default-lease", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "segment.json");
            GuaTimedSegmentFile.Save(path, plan);
            if (omitInFile)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
                node["inputs"]![0]!.AsObject().Remove("leaseMilliseconds");
                File.WriteAllText(path, node.ToJsonString());
            }
            plan = GuaTimedSegmentFile.Load(path);
            using var runtime = new GuaRuntime();
            runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
            var clock = new FakeRealtime();
            clock.OnDelay = () =>
            {
                while (runtime.TryConsumeGameInput(out var request))
                {
                    if (request.Operation == GuaGameInputOperation.Down) Assert.That(request.LeaseMs, Is.EqualTo(5000));
                    runtime.CompleteGameInput(request, true);
                }
            };
            var result = await GuaTimedSegmentReplay.ReplayAsync(new GuaRuntimeSegmentHost(runtime, true), plan, realtime: clock);
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded));
            Assert.That(result.NeutralConfirmed, Is.True);
            Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan with { ExecutionTimeoutMilliseconds = 5500 }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(GuaSegmentOutcome.Succeeded)]
    [TestCase(GuaSegmentOutcome.Cancelled)]
    [TestCase(GuaSegmentOutcome.TimedOut)]
    public async Task Review_HealthMustRemainValidAfterTheFinalNeutralRead(GuaSegmentOutcome terminal)
    {
        using var cancel = new CancellationTokenSource();
        var clock = new FakeRealtime();
        if (terminal == GuaSegmentOutcome.Cancelled) clock.OnDelay = () => cancel.Cancel();
        var changed = false;
        var host = new FakeHost(clock) { ResultDelay = terminal == GuaSegmentOutcome.Succeeded ? 0 : terminal == GuaSegmentOutcome.Cancelled ? 5 : 150,
            OnNeutral = () => changed = true, Health = () => changed ? "lifecycle-or-session-unconfirmed" : null };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, cancellationToken: cancel.Token, realtime: clock);
        Assert.That(changed, Is.True);
        Assert.That(result.Outcome, Is.EqualTo(terminal == GuaSegmentOutcome.Succeeded ? GuaSegmentOutcome.Failed : terminal));
        Assert.That(result.FailureCode, Is.EqualTo(terminal == GuaSegmentOutcome.Succeeded ? "lifecycle-or-session-unconfirmed" :
            terminal == GuaSegmentOutcome.Cancelled ? "caller-cancelled" : "completion-or-boundary-timeout"));
        Assert.That(result.NeutralConfirmed, Is.False);
    }

    [TestCase("device")]
    [TestCase("type")]
    [TestCase("coordinate")]
    [TestCase("lease")]
    public void Review_ProtocolAndFileAgreeOnCommonInputMetadata(string invalid)
    {
        var input = invalid == "coordinate" ? new GuaTimedInput(0, GuaGameInputKind.Pointer,
            GuaGameInputOperation.MoveAbsolute, "absolute:viewport_normalized", X: 2) :
            new GuaTimedInput(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space",
                LeaseMilliseconds: invalid == "lease" ? 60001u : 0,
                DeviceIndex: invalid == "device" ? 1 : 0,
                SemanticValueType: invalid == "type" ? GuaGameInputValueType.Button : null);
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [input]);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var path = Path.GetFullPath("../../../../../../../protocol/schema/timed-segment-v1.schema.json", TestContext.CurrentContext.TestDirectory);
        Assert.That(Json.Schema.JsonSchema.FromFile(path).Evaluate(document.RootElement).IsValid, Is.False);
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan));
    }

    [Test]
    public async Task Review_ReadyThousandInputResultsDoNotSpendBudgetSleepingBetweenChunks()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock);
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100,
            Enumerable.Range(0, 1000).Select(_ => new GuaTimedInput(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")).ToArray());
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded));
        Assert.That(clock.Milliseconds, Is.Zero);
        Assert.That(result.NeutralConfirmed, Is.True);
    }

    [TestCase(257)]
    [TestCase(1000)]
    public async Task Review_NativeLargeBatchKeepsOwnerHealthEvidenceAfterJournalRollsOver(int count)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
        var clock = new FakeRealtime();
        clock.OnDelay = () => { while (runtime.TryConsumeGameInput(out var request)) runtime.CompleteGameInput(request, true); };
        var host = new GuaRuntimeSegmentHost(runtime, true);
        var plan = new GuaTimedSegment(1, 0, 20, 1000, 500,
            Enumerable.Range(0, count).Select(_ => new GuaTimedInput(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")).ToArray());
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded));
        Assert.That(result.Inputs.All(input => input.Succeeded == true), Is.True);
        Assert.That(result.NeutralConfirmed, Is.True);
        using var diagnostics = JsonDocument.Parse(runtime.GetDiagnosticsJson());
        Assert.That(diagnostics.RootElement.GetProperty("traceLifecycle").GetProperty("events").GetArrayLength(), Is.EqualTo(256));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Review_OwnerLeaseEvidenceSurvivesUnrelatedJournalTraffic(bool expired)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard | GuaGameInputCapabilities.Pointer, () => { });
        var host = new GuaRuntimeSegmentHost(runtime, true);
        host.Begin(Plan());
        try
        {
            if (expired)
            {
                host.Send(Plan().Inputs[0], null, () => { });
                Assert.That(runtime.TryConsumeGameInput(out var held), Is.True);
                runtime.CompleteGameInput(held, true);
                runtime.TickGameInputLeases(TimeSpan.FromSeconds(6));
            }
            using var other = runtime.CreateGameInputSession();
            for (var i = 0; i < 300; i++)
            {
                other.Send(GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space");
                while (runtime.TryConsumeGameInput(out var request)) runtime.CompleteGameInput(request, true);
            }
            Assert.That(host.ExecutionFailureCode, Is.EqualTo(expired ? "lease-expired-before-release" : null));
        }
        finally { host.End(); }
    }

    [TestCase("cleanup")]
    [TestCase("neutral")]
    [TestCase("pending")]
    [TestCase("failed-input")]
    [TestCase("failure-code")]
    [TestCase("request-id")]
    [TestCase("receipt-time")]
    public void Review_TraceSuccessRequiresSuccessfulCompletionsAndConfirmedCleanup(string invalid)
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-success-invariant", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            var input = invalid == "pending" ? new GuaTimedInputResult(0, 0, null, null, null, null, null, null) :
                new GuaTimedInputResult(0, 0, invalid == "receipt-time" ? 5 : 0, invalid == "request-id" ? 0ul : 1ul,
                    1, null, invalid != "failed-input", 0);
            var result = new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded, [input], invalid != "cleanup", invalid != "neutral",
                invalid == "failure-code" ? "host-health-failed" : null)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 };
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, result), Is.False);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(1, "true", true)]
    [TestCase(1, "null", false)]
    [TestCase(1, "missing", false)]
    [TestCase(1, "123", false)]
    [TestCase(2, "0.5", true)]
    [TestCase(2, "null", false)]
    [TestCase(2, "missing", false)]
    [TestCase(2, "\"0.5\"", false)]
    [TestCase(3, "{\"x\":0,\"y\":1}", true)]
    [TestCase(3, "{\"x\":0}", false)]
    [TestCase(3, "missing", false)]
    [TestCase(3, "{\"x\":0,\"y\":\"1\"}", false)]
    public void Review_ProtocolSchemaMatchesDeclaredSemanticSetPayloadTypes(int type, string json, bool valid)
    {
        var input = new GuaTimedInput(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "value",
            json == "missing" ? null : JsonSerializer.Deserialize<JsonElement>(json), 5000, SemanticValueType: (GuaGameInputValueType)type);
        var plan = new GuaTimedSegment(1, 1, 20, 100, 100,
            [input, new(1, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "value")]);
        var path = Path.GetFullPath("../../../../../../../protocol/schema/timed-segment-v1.schema.json", TestContext.CurrentContext.TestDirectory);
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        if (json == "missing") node["inputs"]![0]!.AsObject().Remove("value");
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.That(Json.Schema.JsonSchema.FromFile(path).Evaluate(document.RootElement).IsValid, Is.EqualTo(valid));
        if (valid) Assert.DoesNotThrow(() => GuaTimedSegmentFile.Validate(plan));
        else Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan));
    }

    [TestCase("missing")]
    [TestCase("late")]
    [TestCase("reordered")]
    public void Review_TraceTimingConfirmationRequiresCompleteOrderedInBudgetApplicationEvidence(string invalid)
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-timing-evidence", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            double? first = invalid == "missing" ? null : invalid == "late" ? 50 : 20;
            var result = new GuaTimedSegmentResult(GuaSegmentOutcome.Succeeded,
                [new(0, 0, 0, 1, 1, first, true, 0), new(1, 0, 0, 2, 1, 10, true, 0)], true, true, null)
            { ApplicationTimingConfirmed = true, MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 };
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, result), Is.False);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(GuaSegmentOutcome.Succeeded, true)]
    [TestCase(GuaSegmentOutcome.Failed, false)]
    [TestCase(GuaSegmentOutcome.Cancelled, false)]
    [TestCase(GuaSegmentOutcome.TimedOut, false)]
    [TestCase(GuaSegmentOutcome.Late, false)]
    public void Review_TraceTimingConfirmationRequiresSuccessfulOutcome(GuaSegmentOutcome outcome, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-timing-invariant", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            var result = new GuaTimedSegmentResult(outcome, [new(0, 0, 0, 1, 1, 0, true, 0)], true, true, null)
            { ApplicationTimingConfirmed = true, MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 };
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, result), Is.EqualTo(accepted));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Review_CompletionPollCannotOverrunExecutionDeadlineOrCancellation(bool cancelled)
    {
        using var cancel = new CancellationTokenSource();
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { ResultDelay = 2 };
        host.OnPoll = (_, cleanup) => { if (!cleanup && clock.Milliseconds >= 2) { if (cancelled) cancel.Cancel(); else clock.Milliseconds += 20; } };
        var plan = new GuaTimedSegment(1, 0, 1, 10, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, cancellationToken: cancel.Token, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(cancelled ? GuaSegmentOutcome.Cancelled : GuaSegmentOutcome.TimedOut));
        Assert.That(result.Inputs[0].Succeeded, Is.True);
        Assert.That(result.NeutralConfirmed, Is.True);
    }

    [Test]
    public async Task Review_SameOffsetThousandInputBatchDoesNotRescanPendingResultsDuringDispatch()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { ResultDelay = 50 };
        var pollsBeforeFinalSend = 0;
        host.OnPoll = (_, cleanup) => { if (!cleanup) { clock.Milliseconds += 0.01; if (host.Sent.Count < 1000) pollsBeforeFinalSend++; } };
        var plan = new GuaTimedSegment(1, 0, 20, 1000, 100,
            Enumerable.Range(0, 1000).Select(_ => new GuaTimedInput(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")).ToArray());
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded));
        Assert.That(host.Sent.Count, Is.EqualTo(1000));
        Assert.That(pollsBeforeFinalSend, Is.Zero);
        Assert.That(result.Inputs.All(input => input.SentMilliseconds == 0), Is.True);
    }

    [Test]
    public async Task Review_ReleaseDispatchUsesTheReservedCleanupBudget()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { OnRelease = () => clock.Milliseconds += 110 };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Failed));
        Assert.That(result.FailureCode, Is.EqualTo("cleanup-unconfirmed"));
        Assert.That(result.NeutralConfirmed, Is.False);
        Assert.That(host.CleanupCount, Is.EqualTo(1));
    }

    [Test]
    public void Review_NativePreflightHasNoOwnerQueueCorrelationOrTraceEffects()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
        using var tree = JsonDocument.Parse(runtime.GetUiTreeJson());
        var epoch = tree.RootElement.GetProperty("sessionEpoch").GetUInt64();
        var before = runtime.GetDiagnosticsJson();
        runtime.ValidateGameInput(GuaObservationProfile.Debug, epoch, 0, GuaGameInputKind.Keyboard,
            GuaGameInputOperation.Press, "Space", null, null, 0, 0, 0, false, false);
        Assert.That(runtime.GetDiagnosticsJson(), Is.EqualTo(before));
        Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
        using var owner = runtime.CreateGameInputSession();
        Assert.That(owner.OwnerId, Is.EqualTo(1));
        var id = owner.SendGuarded(epoch, 0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press,
            "Space", null, null, 0, 0, 0, false, false, () => { });
        Assert.That(id, Is.EqualTo(1));
    }

    [TestCase("secret-marker", false)]
    [TestCase("host-health-failed", true)]
    public void Review_TraceRejectsUnregisteredFailureText(string code, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-invalid-timed", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            var invalid = new GuaTimedSegmentResult(GuaSegmentOutcome.Failed,
                [new(0, 0, 0, 1, 1, null, false, 1)], false, false, code)
            { MaxLatenessMilliseconds = 20, ExecutionTimeoutMilliseconds = 100, CleanupTimeoutMilliseconds = 100 };
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, invalid), Is.EqualTo(accepted));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void Review_GuardedInputCannotTruncateItsNativeValueBuffer()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Text, () => { });
        using var owner = runtime.CreateGameInputSession();
        using var tree = JsonDocument.Parse(runtime.GetUiTreeJson());
        var epoch = tree.RootElement.GetProperty("sessionEpoch").GetUInt64();
        Assert.Throws<InvalidOperationException>(() => owner.SendGuarded(epoch, 0, GuaGameInputKind.TextInput,
            GuaGameInputOperation.Set, "", new string('x', 600), null, 0, 0, 0, false, false, () => { }));
        Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
    }
    [TestCase(GuaGameInputValueType.Button, "true")]
    [TestCase(GuaGameInputValueType.Axis1D, "0.2")]
    [TestCase(GuaGameInputValueType.Vector2, "{\"x\":0.2,\"y\":0.5}")]
    public void Review_ValidTypedSemanticValuesStillDispatch(GuaGameInputValueType type, string value)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new GuaGameInputActionDescriptor("value", "value", type,
            Minimum: type == GuaGameInputValueType.Button ? null : -1, Maximum: type == GuaGameInputValueType.Button ? null : 1, Holdable: true)
            { ValueSchemaJson = type == GuaGameInputValueType.Axis1D ? "{\"type\":\"number\",\"minimum\":-1,\"maximum\":0.25}" : null }]);
        var plan = new GuaTimedSegment(1, 1, 20, 100, 100,
            [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "value", JsonSerializer.Deserialize<JsonElement>(value), 5000, SemanticValueType: type),
             new(1, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "value")]);
        GuaTimedSegmentFile.Validate(plan);
        var host = new GuaRuntimeSegmentHost(runtime, true);
        host.Begin(plan);
        try
        {
            foreach (var input in plan.Inputs)
            {
                host.Send(input, null, () => { });
                Assert.That(runtime.TryConsumeGameInput(out var request), Is.True);
                runtime.CompleteGameInput(request, true);
            }
            Assert.That(host.IsNeutral, Is.True);
        }
        finally { host.End(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Review_ResolvedSemanticSecretSchemaIsCheckedBeforeAnyDispatch(bool valid)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic | GuaGameInputCapabilities.Keyboard, () => { });
        runtime.PublishGameInputActions("test", [new GuaGameInputActionDescriptor("chat", "chat", GuaGameInputValueType.Text)
            { ValueSchemaJson = "{\"type\":\"string\",\"minLength\":1,\"maxLength\":2}" }]);
        var clock = new FakeRealtime();
        clock.OnDelay = () => { while (runtime.TryConsumeGameInput(out var request)) runtime.CompleteGameInput(request, true); };
        var host = new GuaRuntimeSegmentHost(runtime, true);
        var plan = new GuaTimedSegment(1, 10, 20, 100, 100,
            [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space"),
             new(10, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "chat", Sensitive: true, SecretKey: "chat", SemanticValueType: GuaGameInputValueType.Text)]);
        if (valid)
        {
            var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, _ => JsonSerializer.SerializeToElement("ok"), realtime: clock);
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded));
            Assert.That(result.NeutralConfirmed, Is.True);
        }
        else
        {
            var error = Assert.ThrowsAsync<InvalidDataException>(() => GuaTimedSegmentReplay.ReplayAsync(host, plan,
                _ => JsonSerializer.SerializeToElement("secret-marker"), realtime: clock));
            Assert.That(error!.Message, Does.Not.Contain("secret-marker"));
            Assert.That(host.OwnerId, Is.Null);
            Assert.That(clock.Milliseconds, Is.Zero);
            Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
        }
    }
    [TestCase(GuaGameInputValueType.Button, null, false)]
    [TestCase(GuaGameInputValueType.Axis1D, null, false)]
    [TestCase(GuaGameInputValueType.Axis1D, "2", false)]
    [TestCase(GuaGameInputValueType.Vector2, "{\"x\":0}", false)]
    [TestCase(GuaGameInputValueType.Axis1D, "0.5", true)]
    public void Review_SemanticPayloadRangeAndSchemaAreCheckedBeforeOwner(GuaGameInputValueType type, string? value, bool metadata)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new GuaGameInputActionDescriptor("value", "value", type,
            Minimum: type is GuaGameInputValueType.Axis1D or GuaGameInputValueType.Vector2 ? -1 : null,
            Maximum: type is GuaGameInputValueType.Axis1D or GuaGameInputValueType.Vector2 ? 1 : null, Holdable: true)
            { ValueSchemaJson = metadata ? "{\"type\":\"number\",\"minimum\":-1,\"maximum\":0.25}" : null }]);
        var plan = new GuaTimedSegment(1, 1, 20, 100, 100,
            [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "value", value is null ? null : JsonSerializer.Deserialize<JsonElement>(value), 5000, SemanticValueType: type),
             new(1, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "value")]);
        var host = new GuaRuntimeSegmentHost(runtime, true);
        Assert.Throws<InvalidDataException>(() => host.Begin(plan));
        Assert.That(host.OwnerId, Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Review_TerminalOutcomesCannotConfirmOtherwiseValidApplicationTimes(bool cancelled)
    {
        using var cancel = new CancellationTokenSource();
        var clock = new FakeRealtime { OnDelay = () => { if (cancelled) cancel.Cancel(); } };
        var host = new FakeHost(clock) { ApplicationTimes = true, ResultDelay = cancelled ? 5 : 150, Applied = _ => 0 };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")], RequireApplicationTimes: true);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, cancellationToken: cancel.Token, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(cancelled ? GuaSegmentOutcome.Cancelled : GuaSegmentOutcome.TimedOut));
        Assert.That(result.ApplicationTimingConfirmed, Is.False);
    }

    [Test]
    public async Task Review_ArbitraryHostHealthTextCannotEnterTimingEvidence()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { Health = () => "secret-marker-host-error" };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Failed));
        Assert.That(result.FailureCode, Is.EqualTo("host-health-failed"));
        Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain("secret-marker"));
    }
    [TestCase(GuaGameInputKind.TextInput, "", null)]
    [TestCase(GuaGameInputKind.TextInput, "", "123")]
    [TestCase(GuaGameInputKind.Gamepad, "left_stick_x", null)]
    [TestCase(GuaGameInputKind.Gamepad, "left_stick_x", "\"0.5\"")]
    [TestCase(GuaGameInputKind.Gamepad, "left_stick_x", "1.5")]
    public void Review_InvalidRawPayloadsAreRejectedBeforeStart(GuaGameInputKind kind, string target, string? json)
    {
        var input = new GuaTimedInput(0, kind, GuaGameInputOperation.Set, target,
            json is null ? null : JsonSerializer.Deserialize<JsonElement>(json), 5000);
        var inputs = kind == GuaGameInputKind.Gamepad ? new[] { input,
            new GuaTimedInput(1, kind, GuaGameInputOperation.Reset, "") } : new[] { input };
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(new(1, 1, 20, 100, 100, inputs)));
    }

    [Test]
    public void Review_PlayerRuntimeRejectsDebugSegmentBeforeOwnerCreation()
    {
        using var runtime = new GuaRuntime();
        runtime.SetObservationProfile(GuaObservationProfile.Player);
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard | GuaGameInputCapabilities.Pointer, () => { });
        var host = new GuaRuntimeSegmentHost(runtime, true);
        Assert.ThrowsAsync<NotSupportedException>(() => GuaTimedSegmentReplay.ReplayAsync(host, Plan()));
        Assert.That(host.OwnerId, Is.Null);
    }

    [Test]
    public void Review_GuardedInputRejectsProfileCeilingChangeAtConsume()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
        var host = new GuaRuntimeSegmentHost(runtime, true);
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        host.Begin(plan);
        try
        {
            var id = host.Send(plan.Inputs[0], null, () => { });
            runtime.SetObservationProfile(GuaObservationProfile.Player);
            Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
            Assert.That(host.Poll(id)?.Succeeded, Is.False);
        }
        finally { host.End(); }
    }

    [Test]
    public void Review_ResolvedRawTextPayloadIsValidatedBeforeOwnerCreation()
    {
        var host = new FakeHost(new FakeRealtime());
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100,
            [new(0, GuaGameInputKind.TextInput, GuaGameInputOperation.Set, "", Sensitive: true, SecretKey: "text")]);
        Assert.ThrowsAsync<InvalidDataException>(() => GuaTimedSegmentReplay.ReplayAsync(host, plan,
            _ => JsonSerializer.SerializeToElement(123)));
        Assert.That(host.Began, Is.False);
    }
    [Test]
    public void Review_ImportUsesEffectiveLegacyWheelAndLeaseDefaults()
    {
        const string json = """{"schemaVersion":2,"steps":[{"action":"game_input","operation":"key_down","arguments":{"code":"KeyW"},"relativeMilliseconds":0,"sensitive":false},{"action":"game_input","operation":"pointer_wheel","arguments":{"deltaX":0,"deltaY":20},"relativeMilliseconds":100,"sensitive":false},{"action":"game_input","operation":"key_up","arguments":{"code":"KeyW"},"relativeMilliseconds":300,"sensitive":false}]}""";
        var segment = GuaTimedSegmentImport.FromRecording(json, 300, 20, 1000, 100);
        Assert.That(segment.Inputs[0].LeaseMilliseconds, Is.EqualTo(5000));
        Assert.That(segment.Inputs[1].Target, Is.EqualTo("pixels"));
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentImport.FromRecording(json, 300, 20, 5500, 100));
    }

    [TestCase(GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "NotAKey")]
    [TestCase(GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space\n")]
    [TestCase(GuaGameInputKind.Pointer, GuaGameInputOperation.Up, "unknown")]
    [TestCase(GuaGameInputKind.Gamepad, GuaGameInputOperation.Up, "left_stick_x")]
    public void Review_InvalidRawTargetsAreRejectedBeforeStart(GuaGameInputKind kind, GuaGameInputOperation op, string target)
    {
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, kind, op, target)]);
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan));
    }

    [Test]
    public void Review_NativeHostAcceptsNonHoldableAxisAndStatelessSemanticText()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new("move", "move", GuaGameInputValueType.Axis1D), new("chat", "chat", GuaGameInputValueType.Text)]);
        var plan = new GuaTimedSegment(1, 1, 20, 100, 100,
            [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "move", JsonSerializer.SerializeToElement(0.5), 5000),
             new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Set, "chat", Sensitive: true, SecretKey: "chat", SemanticValueType: GuaGameInputValueType.Text),
             new(1, GuaGameInputKind.Semantic, GuaGameInputOperation.Release, "move")]);
        GuaTimedSegmentFile.Validate(plan);
        var host = new GuaRuntimeSegmentHost(runtime, true);
        host.Begin(plan);
        try
        {
            host.Send(plan.Inputs[0], null, () => { });
            host.Send(plan.Inputs[1], JsonSerializer.SerializeToElement("secret"), () => { });
            Assert.That(runtime.TryConsumeGameInput(out var axis), Is.True);
            runtime.CompleteGameInput(axis, true);
            Assert.That(runtime.TryConsumeGameInput(out var text), Is.True);
            runtime.CompleteGameInput(text, true);
            Assert.That(host.IsNeutral, Is.False);
            host.Send(plan.Inputs[2], null, () => { });
            Assert.That(runtime.TryConsumeGameInput(out var release), Is.True);
            runtime.CompleteGameInput(release, true);
            Assert.That(host.IsNeutral, Is.True); // Text did not create a hold.
        }
        finally { host.End(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Review_GuardedDispatchRejectsChangedEpochAtEnqueueAndConsume(bool afterEnqueue)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
        var context = (GuaContext)typeof(GuaRuntime).GetField("_observations", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")]);
        var host = new GuaRuntimeSegmentHost(runtime, true);
        host.Begin(plan);
        try
        {
            if (afterEnqueue)
            {
                var id = host.Send(plan.Inputs[0], null, () => { });
                Assert.That(context.Reset().Result, Is.EqualTo(GuaResetResult.Succeeded));
                Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
                Assert.That(host.Poll(id), Is.Null); // Reset discards prior pending results by existing contract.
                Assert.That(host.ExecutionFailureCode, Is.EqualTo("lifecycle-or-session-unconfirmed"));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => host.Send(plan.Inputs[0], null, () => context.Reset()));
                Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
            }
        }
        finally { host.End(); }
    }

    [Test]
    public void Review_ConfirmationCannotAuthorizeReplacementDescriptorAtConsume()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new("danger", "approved", GuaGameInputValueType.Button, RequiresConfirmation: true)]);
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "danger")]);
        var host = new GuaRuntimeSegmentHost(runtime, true, confirmation: _ => true);
        host.Begin(plan);
        try
        {
            var id = host.Send(plan.Inputs[0], null, () => { });
            runtime.PublishGameInputActions("test", [new("danger", "replacement", GuaGameInputValueType.Button, RequiresConfirmation: true)]);
            Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
            Assert.That(host.Poll(id)?.Succeeded, Is.False);
        }
        finally { host.End(); }
    }

    [Test]
    public async Task Review_CleanupRechecksFinalHealthAndPreservesSimulationScope()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { ClearScopeOnEnd = true, SimulationScope = "input-only" };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100, [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space")], Clock: GuaSegmentClock.Simulation);
        var changed = false;
        host.OnNeutral = () => changed = true;
        host.Health = () => changed ? "lease-expired-before-release" : null;
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Failed));
        Assert.That(result.FailureCode, Is.EqualTo("lease-expired-before-release"));
        Assert.That(result.SimulationScope, Is.EqualTo("input-only"));
        Assert.That(host.Ended, Is.True);
    }

    [Test]
    public async Task Review_ClockFailureAfterBeginStillReleasesAndEndsHost()
    {
        var host = new FakeHost(new FakeRealtime());
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: new ThrowingRealtime());
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Failed));
        Assert.That(host.CleanupCount, Is.EqualTo(1));
        Assert.That(host.Ended, Is.True);
    }

    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public async Task Review_CleanupApplicationEvidenceCannotReplaceTerminalOutcome(bool cancelled, bool late)
    {
        using var cancel = new CancellationTokenSource();
        var clock = new FakeRealtime { OnDelay = () => { if (cancelled) cancel.Cancel(); } };
        var host = new FakeHost(clock) { ApplicationTimes = true, ResultDelay = cancelled ? 5 : 150,
            Applied = id => late ? (id == 1 ? 100 : 50) : (id == 1 ? 20 : 10) };
        var plan = new GuaTimedSegment(1, 0, 20, 100, 100,
            [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space"), new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "KeyA")], RequireApplicationTimes: true);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, plan, cancellationToken: cancel.Token, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(cancelled ? GuaSegmentOutcome.Cancelled : GuaSegmentOutcome.TimedOut));
        Assert.That(result.FailureCode, Is.EqualTo(cancelled ? "caller-cancelled" : "completion-or-boundary-timeout"));
        Assert.That(result.ApplicationTimingConfirmed, Is.False);
    }

    [Test]
    public void Review_TraceRejectsConstructibleInvalidTimedResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-invalid-timed", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            var invalid = new GuaTimedSegmentResult((GuaSegmentOutcome)99, [], true, true, null);
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, invalid), Is.False);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class NeutralBoundaryHost(GuaRuntimeSegmentHost inner, Action beforeNeutral) : IGuaTimedSegmentValueHost
    {
        public bool OrderedApplication => inner.OrderedApplication;
        public bool ApplicationTimes => inner.ApplicationTimes;
        public bool SameTickApplication => inner.SameTickApplication;
        public string? SimulationScope => inner.SimulationScope;
        public double SimulationMilliseconds => inner.SimulationMilliseconds;
        public string? ExecutionFailureCode => inner.ExecutionFailureCode;
        public void Begin(GuaTimedSegment segment) => inner.Begin(segment);
        public void Begin(GuaTimedSegment segment, IReadOnlyList<JsonElement?> values) => inner.Begin(segment, values);
        public ulong Send(GuaTimedInput input, JsonElement? secret, Action guard) => inner.Send(input, secret, guard);
        public GuaTimedCompletion? Poll(ulong id) => inner.Poll(id);
        public ulong ReleaseAll() => inner.ReleaseAll();
        public bool IsNeutral { get { beforeNeutral(); return inner.IsNeutral; } }
        public void End() => inner.End();
    }

    private sealed class ThrowingRealtime : IGuaSegmentRealtime
    {
        public double Milliseconds => throw new InvalidOperationException("clock unavailable");
        public Task DelayAsync(TimeSpan delay, CancellationToken token) => throw new InvalidOperationException();
    }
}
