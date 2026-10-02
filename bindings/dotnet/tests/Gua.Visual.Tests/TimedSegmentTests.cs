using System.Text.Json;
using System.Diagnostics;
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
    public void FileRoundTripMatchesProtocolAndRejectsWaitsAndBranches()
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-timed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "segment.json");
        try
        {
            GuaTimedSegmentFile.Save(path, Plan());
            Assert.That(GuaTimedSegmentFile.Load(path).Inputs.Count, Is.EqualTo(4));
            var schemaPath = Path.GetFullPath("../../../../../../../protocol/schema/timed-segment-v1.schema.json", TestContext.CurrentContext.TestDirectory);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.That(JsonSchema.FromFile(schemaPath).Evaluate(document.RootElement).IsValid, Is.True);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"target\": \"KeyW\"", "\"waitCondition\": \"visible:x\", \"target\": \"KeyW\""));
            Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Load(path));
            File.WriteAllText(path, """{"schemaVersion":1,"executionTimeoutMilliseconds":100,"cleanupTimeoutMilliseconds":100,"inputs":[{"kind":2,"operation":1,"target":"Space"}]}""");
            Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Load(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private static GuaTimedSegment Plan(long maxLateness = 20) => new(1, 300, maxLateness, 1000, 100,
    [new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Down, "KeyW", LeaseMilliseconds: 5000),
     new(0, GuaGameInputKind.Pointer, GuaGameInputOperation.MoveDelta, "delta:", X: 2),
     new(100, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space"),
     new(300, GuaGameInputKind.Keyboard, GuaGameInputOperation.Up, "KeyW")]);

    [Test]
    public async Task AT_CLOCK_003_004_SlowResultsDoNotDelayJumpOrReleaseAndSameOffsetsKeepOrder()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { ResultDelay = 450 };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: clock);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded));
            Assert.That(result.Inputs.Select(x => x.SentMilliseconds), Is.EqualTo(new double?[] { 0, 0, 100, 300 }));
            Assert.That(host.Sent.Select(x => x.Target), Is.EqualTo(new[] { "KeyW", "delta:", "Space", "KeyW" }));
            Assert.That(result.Inputs.All(x => x.ResultReceivedMilliseconds >= x.SentMilliseconds + 450), Is.True);
            Assert.That(result.Inputs.All(x => x.HostAppliedMilliseconds is null), Is.True);
            Assert.That(result.NeutralConfirmed, Is.True);
        });
    }

    [Test]
    public void AT_CLOCK_005_006_RejectOpenHoldsUnsafeLeaseWaitAndUnsupportedStrictCapabilities()
    {
        var plan = Plan();
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan with { Inputs = plan.Inputs.Take(3).ToArray() }));
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(plan with
        { Inputs = plan.Inputs.Select(x => x.Operation == GuaGameInputOperation.Down ? x with { LeaseMilliseconds = 1000 } : x).ToArray() }));
        var json = """{"schemaVersion":2,"steps":[{"action":"game_input","operation":"press_physical_key","arguments":{"code":"Space"},"relativeMilliseconds":0,"sensitive":false,"waitCondition":"visible:x"}]}""";
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentImport.FromRecording(json, 300, 20, 1000, 100));
        var host = new FakeHost(new FakeRealtime());
        Assert.ThrowsAsync<NotSupportedException>(() => GuaTimedSegmentReplay.ReplayAsync(host, plan with { RequireApplicationTimes = true }));
        Assert.ThrowsAsync<NotSupportedException>(() => GuaTimedSegmentReplay.ReplayAsync(host, plan with { RequireSameTickApplication = true }));
        host.OrderedApplication = false;
        Assert.ThrowsAsync<NotSupportedException>(() => GuaTimedSegmentReplay.ReplayAsync(host, plan));
        Assert.That(host.Began, Is.False);
    }

    [Test]
    public async Task AT_CLOCK_007_ResumeLateStopsUnsentInputsWithoutBurstAndPreservesPartialEvidence()
    {
        var clock = new FakeRealtime { JumpOnce = 400 };
        var host = new FakeHost(clock);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: clock);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Late));
            Assert.That(result.Inputs.Count(x => x.RequestId is not null), Is.EqualTo(2));
            Assert.That(host.Sent.Any(x => x.Target == "Space"), Is.False);
            Assert.That(result.NeutralConfirmed, Is.True);
            Assert.That(result.FailureCode, Is.EqualTo("max-lateness-exceeded"));
        });
    }

    [Test]
    public async Task SendGuardStopsLateOrCancelledInputAfterHostPreflight()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { Preflight = () => clock.Milliseconds += 400 };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Late));
        Assert.That(host.Sent, Is.Empty);
        Assert.That(result.Inputs.All(input => input.SentMilliseconds is null), Is.True);
        Assert.That(result.NeutralConfirmed, Is.True);

        using var cancellation = new CancellationTokenSource();
        clock = new FakeRealtime();
        host = new FakeHost(clock) { Preflight = () => cancellation.Cancel() };
        result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), cancellationToken: cancellation.Token, realtime: clock);
        Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Cancelled));
        Assert.That(host.Sent, Is.Empty);
    }

    [Test]
    public async Task Audit_UnknownDispatchCannotConfirmNeutralWhileOrdinaryWorkMightStillApply()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { ThrowAfterDispatch = true, ResultDelay = 2000 };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: clock);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Failed));
            Assert.That(result.Inputs[0].SentMilliseconds, Is.EqualTo(0));
            Assert.That(result.Inputs[0].RequestId, Is.Null);
            Assert.That(result.NeutralConfirmed, Is.False);
            Assert.That(result.CleanupSucceeded, Is.True);
            Assert.That(host.Ended, Is.True);
        });
    }

    [Test]
    public async Task ApplicationEvidenceMustMatchApplyOrderAndStrictSameOffsetTick()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { ApplicationTimes = true,
            Applied = id => id switch { 1 => 20, 2 => 10, 3 => 100, 4 => 300, _ => null } };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan() with { RequireApplicationTimes = true }, realtime: clock);
        Assert.That(result.FailureCode, Is.EqualTo("application-order-or-tick-violation"));
        Assert.That(result.ApplicationTimingConfirmed, Is.False);
        clock = new FakeRealtime();
        host = new FakeHost(clock) { ApplicationTimes = true, SameTickApplication = true,
            Applied = id => id switch { 1 => 0, 2 => 1, 3 => 100, 4 => 300, _ => null } };
        result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan() with { RequireSameTickApplication = true }, realtime: clock);
        Assert.That(result.FailureCode, Is.EqualTo("application-order-or-tick-violation"));
        Assert.That(result.ApplicationTimingConfirmed, Is.False);
    }

    [Test]
    public async Task CancellationStillCleansUpWithItsIndependentDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeRealtime { OnDelay = () => cancellation.Cancel() };
        var host = new FakeHost(clock);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), cancellationToken: cancellation.Token, realtime: clock);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Cancelled));
            Assert.That(result.Inputs.Count(x => x.RequestId is not null), Is.EqualTo(2));
            Assert.That(result.NeutralConfirmed, Is.True);
            Assert.That(host.Ended, Is.True);
        });
    }

    [Test]
    public async Task AT_CLOCK_002_StoppedSimulationUsesRealtimeExecutionAndCleanupDeadlines()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { SimulationScope = "test-input-subsystem" };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan() with { Clock = GuaSegmentClock.Simulation }, realtime: clock);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.TimedOut));
            Assert.That(result.Inputs.Count(x => x.RequestId is not null), Is.EqualTo(2));
            Assert.That(result.NeutralConfirmed, Is.True);
            Assert.That(clock.Milliseconds, Is.InRange(1000, 1100));
        });
        var unsupported = new FakeHost(new FakeRealtime());
        Assert.ThrowsAsync<NotSupportedException>(() => GuaTimedSegmentReplay.ReplayAsync(unsupported,
            Plan() with { Clock = GuaSegmentClock.Simulation }));
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task AT_CLOCK_006_UnconfirmedReleaseOrPendingOrdinaryResultCannotReportNormalTiming(bool cleanupFails, bool pending)
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock) { CleanupFails = cleanupFails, ResultDelay = pending ? 2000 : 0 };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: clock);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.Not.EqualTo(GuaSegmentOutcome.Succeeded));
            Assert.That(result.NeutralConfirmed, Is.False);
            Assert.That(host.Ended, Is.True);
        });
    }

    [Test]
    public void LegacyConversionPreservesOffsetsAndRejectsInventedTimeAndPlaintext()
    {
        var json = """
        {"schemaVersion":2,"steps":[
          {"action":"game_input","operation":"key_down","arguments":{"code":"KeyW","leaseMs":5000},"relativeMilliseconds":0,"sensitive":false},
          {"action":"game_input","operation":"press_physical_key","arguments":{"code":"Space"},"relativeMilliseconds":100,"sensitive":false},
          {"action":"game_input","operation":"key_up","arguments":{"code":"KeyW"},"relativeMilliseconds":300,"sensitive":false}]}
        """;
        var segment = GuaTimedSegmentImport.FromRecording(json, 300, 20, 1000, 100);
        Assert.That(segment.TimingProvenance, Is.EqualTo("legacy-unknown"));
        Assert.That(segment.Inputs.Select(x => x.OffsetMilliseconds), Is.EqualTo(new long[] { 0, 100, 300 }));
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentImport.FromRecording(json.Replace("\"schemaVersion\":2", "\"schemaVersion\":1"), 300, 20, 1000, 100));
        var sensitive = """{"schemaVersion":2,"steps":[{"action":"game_input","operation":"set_game_input_action","arguments":{"actionId":"chat","value":"secret-marker"},"relativeMilliseconds":0,"sensitive":true,"secretKey":"chat"}]}""";
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentImport.FromRecording(sensitive, 300, 20, 1000, 100));
    }

    [Test]
    public async Task SecretsAreResolvedOnlyForHostAndNeverCopiedToResult()
    {
        var clock = new FakeRealtime();
        var host = new FakeHost(clock);
        var segment = new GuaTimedSegment(1, 1, 20, 100, 100,
            [new(0, GuaGameInputKind.TextInput, GuaGameInputOperation.Set, "", Sensitive: true, SecretKey: "chat")]);
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, segment,
            _ => JsonSerializer.SerializeToElement("secret-marker"), realtime: clock);
        Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain("secret-marker"));
        Assert.That(host.Secrets.Single()!.Value.GetString(), Is.EqualTo("secret-marker"));
        Assert.Throws<InvalidDataException>(() => GuaTimedSegmentFile.Validate(segment with
        { Inputs = [segment.Inputs[0] with { Value = JsonSerializer.SerializeToElement("secret-marker") }] }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RealNativeHostVerifiesConsumeOrderDelayedCompletionLeaseAndOwnerIsolation(bool expireEarly)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard | GuaGameInputCapabilities.Pointer, () => { });
        using var other = runtime.CreateGameInputSession();
        other.Send(GuaGameInputKind.Keyboard, GuaGameInputOperation.Down, "KeyA", lease: TimeSpan.FromSeconds(30));
        Assert.That(runtime.TryConsumeGameInput(out var otherDown), Is.True);
        runtime.CompleteGameInput(otherDown, true);
        var clock = new FakeRealtime();
        var applied = new List<(string Target, double Time)>();
        var delayed = new List<(GuaGameInputRequest Request, double Due)>();
        var host = new GuaRuntimeSegmentHost(runtime, adapterAppliesInOrder: true);
        ulong? owner = null;
        var expired = false;
        clock.OnDelay = () =>
        {
            owner ??= host.OwnerId;
            if (expireEarly && clock.Milliseconds >= 50 && !expired)
            { runtime.TickGameInputLeases(TimeSpan.FromSeconds(6)); expired = true; }
            while (runtime.TryConsumeGameInput(out var request))
            {
                if (request.Kind == GuaGameInputKind.Cleanup || request.Operation == GuaGameInputOperation.Release)
                { runtime.CompleteGameInput(request, true); continue; }
                applied.Add((request.Target, clock.Milliseconds));
                if (request.Operation == GuaGameInputOperation.Down)
                    delayed.Add((request, 450));
                else runtime.CompleteGameInput(request, true);
            }
            foreach (var pending in delayed.Where(x => x.Due <= clock.Milliseconds).ToArray())
            { runtime.CompleteGameInput(pending.Request, true); delayed.Remove(pending); }
        };
        var result = await GuaTimedSegmentReplay.ReplayAsync(host, Plan(), realtime: clock);
        Assert.Multiple(() =>
        {
            Assert.That(owner, Is.Not.EqualTo(other.OwnerId));
            Assert.That(result.Outcome, Is.EqualTo(expireEarly ? GuaSegmentOutcome.Failed : GuaSegmentOutcome.Succeeded));
            Assert.That(result.NeutralConfirmed, Is.EqualTo(!expireEarly));
            Assert.That(JsonDocument.Parse(other.GetStateJson()).RootElement.GetProperty("held").GetArrayLength(), Is.EqualTo(1));
            Assert.That(result.Inputs.All(x => x.HostAppliedMilliseconds is null), Is.True);
        });
        if (expireEarly) Assert.That(result.FailureCode, Is.EqualTo("lease-expired-before-release"));
        else
        {
            Assert.That(applied.Select(x => x.Target), Is.EqualTo(new[] { "KeyW", "delta:", "Space", "KeyW" }));
            Assert.That(applied[2].Time, Is.InRange(100, 102));
            Assert.That(applied[3].Time, Is.InRange(300, 302));
        }
    }

    [Test]
    public void RealHostRejectsMissingCapabilityAndUnconfirmedRiskBeforeAnySegmentOwner()
    {
        using var runtime = new GuaRuntime();
        var host = new GuaRuntimeSegmentHost(runtime, true, GuaObservationProfile.Player);
        Assert.ThrowsAsync<NotSupportedException>(() => GuaTimedSegmentReplay.ReplayAsync(host, Plan()));
        Assert.That(host.OwnerId, Is.Null);
        runtime.EnableGameInput(GuaGameInputCapabilities.Semantic, () => { });
        runtime.PublishGameInputActions("test", [new("danger", "danger", GuaGameInputValueType.Button, RequiresConfirmation: true)]);
        var protectedPlan = new GuaTimedSegment(1, 0, 20, 100, 100,
            [new(0, GuaGameInputKind.Semantic, GuaGameInputOperation.Press, "danger")]);
        var debugHost = new GuaRuntimeSegmentHost(runtime, true);
        Assert.ThrowsAsync<InvalidOperationException>(() => GuaTimedSegmentReplay.ReplayAsync(debugHost, protectedPlan));
        Assert.That(debugHost.OwnerId, Is.Null);
    }

    [Test]
    public void NativeSendGuardRunsAfterMarshallingAndBeforeEnqueue()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard, () => { });
        using var owner = runtime.CreateGameInputSession();
        Assert.Throws<OperationCanceledException>(() => owner.Send(GuaGameInputKind.Keyboard,
            GuaGameInputOperation.Press, "Space", null, null, 0, 0, 0, false, false,
            () => throw new OperationCanceledException()));
        Assert.That(runtime.TryConsumeGameInput(out _), Is.False);
    }

    [Test]
    public async Task RealtimeNativePumpSendsReleaseBeforeSlowStartCompletion()
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.Keyboard | GuaGameInputCapabilities.Pointer, () => { });
        using var pumpCancellation = new CancellationTokenSource();
        var host = new GuaRuntimeSegmentHost(runtime, true);
        var clock = Stopwatch.StartNew();
        var delayed = new List<(GuaGameInputRequest Request, double Due)>();
        var pump = Task.Run(async () =>
        {
            while (!pumpCancellation.IsCancellationRequested)
            {
                while (runtime.TryConsumeGameInput(out var request))
                    if (request.Operation == GuaGameInputOperation.Down)
                        delayed.Add((request, clock.Elapsed.TotalMilliseconds + 800));
                    else runtime.CompleteGameInput(request, true);
                foreach (var item in delayed.Where(item => item.Due <= clock.Elapsed.TotalMilliseconds).ToArray())
                { runtime.CompleteGameInput(item.Request, true); delayed.Remove(item); }
                await Task.Delay(1);
            }
        });
        GuaTimedSegmentResult result;
        try
        {
            result = await GuaReplayer.ReplayTimedSegmentAsync(host, Plan(500) with
            { ExecutionTimeoutMilliseconds = 2000, CleanupTimeoutMilliseconds = 1000 });
        }
        finally { pumpCancellation.Cancel(); await pump; }
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(GuaSegmentOutcome.Succeeded));
            Assert.That(result.NeutralConfirmed, Is.True);
            Assert.That(result.Inputs[2].SentMilliseconds, Is.LessThan(700));
            Assert.That(result.Inputs[3].SentMilliseconds, Is.LessThan(800));
            Assert.That(result.Inputs[0].ResultReceivedMilliseconds, Is.GreaterThan(result.Inputs[3].SentMilliseconds!.Value));
            Assert.That(result.ApplicationTimingConfirmed, Is.False);
        });
    }

    [Test]
    public async Task TraceAttachmentContainsTimingAndCleanupEvidenceWithoutValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-timed-trace", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var stepId = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            var clock = new FakeRealtime();
            var result = await GuaTimedSegmentReplay.ReplayAsync(new FakeHost(clock), Plan(), realtime: clock);
            result = result with { Inputs = result.Inputs.Select((input, i) => i == 0 ? input with { RequestId = ulong.MaxValue } : input).ToArray() };
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, stepId, result), Is.True);
            trace.EndStep(stepId, GuaTraceOutcome.Passed);
            Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Passed), Is.True);
            using var attachment = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(trace.ArtifactPath, "attachments"), "*.json").Single()));
            var inputEvidence = attachment.RootElement.GetProperty("result").GetProperty("inputs")[0];
            Assert.That(inputEvidence.GetProperty("requestId").GetString(), Is.EqualTo("18446744073709551615"));
            Assert.That(inputEvidence.GetProperty("hostAppliedMilliseconds").ValueKind, Is.EqualTo(JsonValueKind.Null));
            var schemaPath = Path.GetFullPath("../../../../../../../protocol/schema/timed-segment-result-v1.schema.json", TestContext.CurrentContext.TestDirectory);
            Assert.That(JsonSchema.FromFile(schemaPath).Evaluate(attachment.RootElement).IsValid, Is.True);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeRealtime : IGuaSegmentRealtime
    {
        public double Milliseconds { get; set; }
        public double JumpOnce { get; set; }
        public Action? OnDelay { get; set; }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Milliseconds += delay.TotalMilliseconds + JumpOnce; JumpOnce = 0;
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
    private sealed class FakeHost(FakeRealtime clock) : IGuaTimedSegmentHost
    {
        private ulong next;
        private readonly Dictionary<ulong, (double Due, bool Cleanup)> pending = new();
        public bool OrderedApplication { get; set; } = true;
        public bool ApplicationTimes { get; set; }
        public bool SameTickApplication { get; set; }
        public Func<ulong, double?>? Applied { get; set; }
        public Action? Preflight { get; set; }
        public bool ThrowAfterDispatch { get; set; }
        public string? SimulationScope { get; set; }
        public double SimulationMilliseconds => 0;
        public Func<string?>? Health { get; set; }
        public Action<ulong, bool>? OnPoll { get; set; }
        public Action? OnRelease { get; set; }
        public Action? OnNeutral { get; set; }
        public string? ExecutionFailureCode => Health?.Invoke();
        public double ResultDelay { get; set; }
        public bool CleanupFails { get; set; }
        public bool Began { get; private set; }
        public bool Ended { get; private set; }
        public List<GuaTimedInput> Sent { get; } = [];
        public List<JsonElement?> Secrets { get; } = [];
        public void Begin(GuaTimedSegment segment) => Began = true;
        public ulong Send(GuaTimedInput input, JsonElement? secret, Action verifySendBoundary)
        {
            Preflight?.Invoke(); verifySendBoundary();
            Sent.Add(input); Secrets.Add(secret); pending[++next] = (clock.Milliseconds + ResultDelay, false);
            if (ThrowAfterDispatch) throw new InvalidOperationException("accepted request, reply lost");
            return next;
        }
        public GuaTimedCompletion? Poll(ulong requestId)
        {
            if (!pending.TryGetValue(requestId, out var request)) return null;
            OnPoll?.Invoke(requestId, request.Cleanup);
            if (request.Due > clock.Milliseconds) return null;
            pending.Remove(requestId);
            return new(!request.Cleanup || !CleanupFails, HostAppliedMilliseconds: request.Cleanup ? null : Applied?.Invoke(requestId));
        }
        public ulong ReleaseAll() { OnRelease?.Invoke(); CleanupCount++; pending[++next] = (clock.Milliseconds, true); return next; }
        public bool IsNeutral { get { OnNeutral?.Invoke(); return !CleanupFails; } }
        public void End() { Ended = true; if (ClearScopeOnEnd) SimulationScope = null; }
        public bool ClearScopeOnEnd { get; set; }
        public int CleanupCount { get; private set; }
    }
}
