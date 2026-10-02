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
