using System.Text.Json;
using System.Reflection;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Testing.Recording;
using NUnit.Framework;

namespace Gua.Visual.Tests;

public sealed partial class TimedSegmentTests
{
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

    [Test]
    public void Review_TraceRejectsUnregisteredFailureText()
    {
        var root = Path.Combine(Path.GetTempPath(), "gua-invalid-timed", Guid.NewGuid().ToString("N"));
        try
        {
            using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "segment");
            var invalid = new GuaTimedSegmentResult(GuaSegmentOutcome.Failed, [], false, false, "secret-marker");
            Assert.That(GuaRecordingTrace.AttachTimedResult(trace, step, invalid), Is.False);
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
        host.Health = () => host.CleanupCount == 2 ? "lease-expired-before-release" : null;
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

    private sealed class ThrowingRealtime : IGuaSegmentRealtime
    {
        public double Milliseconds => throw new InvalidOperationException("clock unavailable");
        public Task DelayAsync(TimeSpan delay, CancellationToken token) => throw new InvalidOperationException();
    }
}
