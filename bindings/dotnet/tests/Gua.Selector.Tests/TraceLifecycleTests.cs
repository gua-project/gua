using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[Parallelizable(ParallelScope.All)]
public sealed class TraceLifecycleTests
{
    private static GuaTraceSession Trace() => new(new() { OutputDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
        "trace-lifecycle", Guid.NewGuid().ToString("N")), SavePolicy = GuaTraceSavePolicy.Always });
    private static GuaContext Context()
    {
        var context = new GuaContext();
        context.BeginFrame("test"); context.RegisterNode("button", "button", "Go", new(0, 0, 10, 10)); context.EndFrame();
        return context;
    }
    private static void Complete(GuaContext context, ulong id)
    {
        Assert.That(context.TryConsumeAction(GuaActionType.Click, "button", out var request), Is.True);
        Assert.That(request.RequestId, Is.EqualTo(id));
        Assert.That(context.EmitActionResult(new(id, GuaActionType.Click, true, GuaActionError.None, "button", "", false)), Is.True);
    }
    private static string[] Phases(GuaTraceReadResult read) => read.Events.Where(e => e.Type == "request.phase")
        .Select(e => e.Data.GetProperty("phase").GetString()!).ToArray();

    private sealed class SlowDiagnostics(IGuaContext context) : IGuaContext
    {
        private int _reads;
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        internal bool BlockReads = true;
        internal TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
        internal Func<string, string>? Transform;
        internal Barrier? CaptureBarrier;
        public string GetDiagnosticsJson()
        {
            if (Interlocked.Increment(ref _reads) > 1 && BlockReads) { Entered.Set(); Release.Wait(ReadTimeout); }
            var barrier = Interlocked.Exchange(ref CaptureBarrier, null);
            if (barrier is not null && !barrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Concurrent diagnostic reads did not meet.");
            var json = context.GetDiagnosticsJson();
            return Transform?.Invoke(json) ?? json;
        }
        public string GetUiTreeJson() => context.GetUiTreeJson();
        public GuaNodeState GetNodeState(string id) => context.GetNodeState(id);
        public string FindNodeById(string id) => context.FindNodeById(id);
        public string FindNodeByRole(string role, string? name = null) => context.FindNodeByRole(role, name);
        public string FindNodeByText(string text) => context.FindNodeByText(text);
        public bool EnqueueClick(string id) => context.EnqueueClick(id);
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong id) => context.EnqueueAction(request, out id);
        public bool TryPollActionEvent(out GuaActionEvent e) => context.TryPollActionEvent(out e);
        public bool TryPollActionEvent(ulong id, out GuaActionEvent e) => context.TryPollActionEvent(id, out e);
        public bool TryPollEvent(out GuaEvent e) => context.TryPollEvent(out e);
    }

    [Test]
    public async Task FirstAutomaticActionAfterNativeSourceChangeStillHasOneStep()
    {
        using var context = Context(); var host = "runtime-a";
        var proxy = new SlowDiagnostics(context) { BlockReads = false, Transform = json =>
        {
            using var document = JsonDocument.Parse(json);
            var source = document.RootElement.GetProperty("traceLifecycle").GetProperty("sourceId").GetString()!;
            return json.Replace(source, host);
        } };
        await using var trace = Trace(); trace.Watch(proxy, TimeSpan.FromHours(1));
        using var scope = GuaAssertionScope.Use(new() { Trace = trace });
        host = "runtime-b";
        var pending = GuaActionCompletion.EnqueueAndWaitAsync(proxy, new(GuaActionType.Click, "button"));
        context.TryConsumeAction(GuaActionType.Click, "button", out var request);
        context.EmitActionResult(new(request.RequestId, GuaActionType.Click, true, GuaActionError.None, "button", "", false));
        await pending; await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(1));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("native-source-changed"));
        Assert.That(Phases(read), Does.Contain("completion"));
    }

    [Test]
    public async Task SlowDiagnosticsCannotBlockFinalizationOrAppendAfterItsStopDeadline()
    {
        using var context = Context(); var slow = new SlowDiagnostics(context);
        await using var trace = new GuaTraceSession(new() { OutputDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "trace-lifecycle", Guid.NewGuid().ToString("N")), FlushTimeout = TimeSpan.FromMilliseconds(30), SavePolicy = GuaTraceSavePolicy.Always });
        trace.Mark("main"); var watcher = trace.Watch(slow, TimeSpan.FromHours(1));
        // Establish the in-flight diagnostic read independently of shared pool scheduling.
        var reading = Task.Factory.StartNew(() => watcher.Capture(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.That(slow.Entered.Wait(TimeSpan.FromSeconds(1)), Is.True,
                "Diagnostic read must enter; capture completed=" + reading.IsCompleted + "; issues=" + string.Join(",", trace.Status.Issues));
            var completion = trace.CompleteAsync(GuaTraceOutcome.Failed);
            Assert.That(await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(1))), Is.SameAs(completion));
            await completion;
            Assert.That(trace.Status.Issues, Does.Contain("native-stop-timeout"));
            var dropped = trace.Status.DroppedEvents;
            context.EnqueueAction(new(GuaActionType.Click, "button"), out var id); Complete(context, id);
            slow.Release.Set(); await reading.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(trace.Status.DroppedEvents, Is.EqualTo(dropped));
            Assert.That(context.TryPollActionEvent(id, out _), Is.True);
        }
        finally { slow.Release.Set(); await reading; }
    }

    [Test]
    public async Task RawContextRecordsHostFactsWithoutSourceLocationOrQueueConsumption()
    {
        using var context = Context(); await using var trace = Trace();
        var watcher = trace.Watch(context, TimeSpan.FromHours(1));
        Assert.That(context.EnqueueAction(new(GuaActionType.Click, "button"), out var id), Is.EqualTo(GuaActionError.None));
        watcher.Capture();
        Complete(context, id); watcher.Capture();
        Assert.That(context.TryPollActionEvent(id, out var result), Is.True); Assert.That(result.Succeeded, Is.True);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(Phases(read), Is.EqualTo(new[] { "enqueue", "consume", "completion" }));
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(1));
        Assert.That(read.Events.First().Data.TryGetProperty("source", out _), Is.False);
        Assert.That(read.Issues, Is.Empty);
    }

    [Test]
    public async Task ExplicitAndAutomaticLocatorRecordingShareOneStepAndKeepSelectorAndRuntimeId()
    {
        using var context = Context(); await using var trace = Trace();
        using var scope = GuaAssertionScope.Use(new() { Trace = trace });
        var explicitStep = trace.BeginStep(GuaTraceStepKind.Action, "click Go");
        using var stepScope = trace.UseStep(explicitStep);
        var pending = GuaAssertions.Query(context).ByRole("button", "Go").ClickAsync(timeout: TimeSpan.FromSeconds(2));
        Assert.That(context.TryConsumeAction(GuaActionType.Click, "button", out var request), Is.True);
        context.EmitActionResult(new(request.RequestId, GuaActionType.Click, true, GuaActionError.None, "button", "", false));
        await pending;
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(1));
        Assert.That(read.Events.Select(e => e.StepId).Distinct(), Is.EqualTo(new[] { explicitStep }));
        var resolved = read.Events.Single(e => e.Type == "selector.resolved");
        Assert.That(resolved.Data.GetProperty("resolvedId").GetString(), Is.EqualTo("button"));
        Assert.That(resolved.Data.GetProperty("selector").GetString(), Does.Contain("button"));
        Assert.That(Phases(read), Does.Contain("consume"));
        Assert.That(Phases(read), Does.Contain("completion"));
        Assert.That(Phases(read), Does.Not.Contain("late-completion"));
    }

    [Test]
    public async Task TimeoutRetainsLateCompletionAndDoesNotChangePrimaryOrConsumeOtherRequests()
    {
        using var context = Context(); await using var trace = Trace();
        using var scope = GuaAssertionScope.Use(new() { Trace = trace });
        var failure = Assert.ThrowsAsync<GuaActionException>(() => GuaActionCompletion.EnqueueAndWaitAsync(context,
            new(GuaActionType.Click, "button"), TimeSpan.FromMilliseconds(10)));
        Assert.That(failure!.Kind, Is.EqualTo(GuaActionFailureKind.TimedOut));
        trace.SetPrimaryOutcome(GuaTraceOutcome.Failed);
        Complete(context, failure.RequestId);
        context.EnqueueAction(new(GuaActionType.Click, "button"), out var other);
        Complete(context, other);
        trace.Watch(context).Capture();
        Assert.That(context.TryPollActionEvent(other, out _), Is.True);
        Assert.That(context.TryPollActionEvent(failure.RequestId, out _), Is.True);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.PrimaryOutcome, Is.EqualTo("failed"));
        Assert.That(Phases(read), Does.Contain("late-completion"));
        var late = read.Events.Single(e => e.Type == "request.phase" && e.Data.GetProperty("phase").GetString() == "late-completion");
        Assert.That(read.Events.Count(e => e.StepId == late.StepId && e.Type == "step.end"), Is.EqualTo(1));
    }

    [Test]
    public async Task ParallelContextsSameRequestIdAndEpochAreNotMerged()
    {
        using var a = Context(); using var b = Context(); await using var trace = Trace();
        var wa = trace.Watch(a, TimeSpan.FromHours(1)); var wb = trace.Watch(b, TimeSpan.FromHours(1));
        a.EnqueueAction(new(GuaActionType.Click, "button"), out var first);
        b.EnqueueAction(new(GuaActionType.Click, "button"), out var second);
        Assert.That(first, Is.EqualTo(second)); Complete(a, first); Complete(b, second);
        await Task.WhenAll(Task.Run(() => wa.Capture()), Task.Run(() => wb.Capture()));
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(2));
        Assert.That(read.Events.Where(e => e.Type == "request.phase").Select(e => e.Data.GetProperty("request").GetProperty("sourceId").GetString()).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task ParallelConnectionsObserveOneNativeOperationAndLeaveItsCompletionAvailable()
    {
        using var runtime = new GuaRuntime();
        runtime.BeginFrame("test"); runtime.RegisterNode(new("button", "button", "Go", new(0, 0, 10, 10))); runtime.EndFrame();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.That(runtime.StartInspectorBridge(port), Is.True);
        using var a = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        using var b = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        await using var trace = Trace();
        var wa = trace.Watch(a, TimeSpan.FromHours(1)); var wb = trace.Watch(b, TimeSpan.FromHours(1));
        a.EnqueueAction(new(GuaActionType.Click, "button"), out var id);
        runtime.TryConsumeAction(GuaActionType.Click, "button", out var request); runtime.EmitActionResult(request, true);
        await Task.WhenAll(Task.Run(() => wa.Capture()), Task.Run(() => wb.Capture()));
        Assert.That(a.TryPollActionEvent(id, out var result), Is.True); Assert.That(result.RequestId, Is.EqualTo(id));
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(1));
        Assert.That(Phases(read), Is.EqualTo(new[] { "enqueue", "consume", "completion" }));
    }

    [Test]
    [NonParallelizable]
    public async Task ConcurrentReadersKeepNativePhasesOrderedAndCorrelated()
    {
        // Both readers observe the same real, complete native journal. Synchronize
        // their reads so acceptance/correlation races cannot hide behind serial IO.
        for (var iteration = 0; iteration < 64; iteration++)
        {
            using var context = Context(); await using var trace = Trace();
            var a = new SlowDiagnostics(context) { BlockReads = false };
            var b = new SlowDiagnostics(context) { BlockReads = false };
            var wa = trace.Watch(a, TimeSpan.FromHours(1)); var wb = trace.Watch(b, TimeSpan.FromHours(1));
            Assert.That(context.EnqueueAction(new(GuaActionType.Click, "button"), out var id), Is.EqualTo(GuaActionError.None));
            Complete(context, id);
            using var barrier = new Barrier(2);
            a.CaptureBarrier = b.CaptureBarrier = barrier;
            // Dedicated threads keep the rendezvous independent of the test runner's pool.
            await Task.WhenAll(
                Task.Factory.StartNew(() => wa.Capture(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
                Task.Factory.StartNew(() => wb.Capture(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            Assert.That(context.TryPollActionEvent(id, out var result), Is.True);
            Assert.That(result.RequestId, Is.EqualTo(id));
            await trace.CompleteAsync(GuaTraceOutcome.Passed);
            var read = GuaTraceReader.Read(trace.ArtifactPath);
            Assert.That(Phases(read), Is.EqualTo(new[] { "enqueue", "consume", "completion" }), $"iteration {iteration}");
            Assert.That(read.Events.Count(e => e.Type == "step.begin"), Is.EqualTo(1));
            Assert.That(read.Manifest.Quality.Issues, Is.Empty);
        }
    }

    [Test]
    public async Task BlockedDiagnosticReadDoesNotHoldTheSessionCollectionLock()
    {
        using var context = Context(); var slow = new SlowDiagnostics(context) { ReadTimeout = Timeout.InfiniteTimeSpan };
        await using var trace = Trace();
        var blocked = trace.Watch(slow, TimeSpan.FromHours(1));
        var available = trace.Watch(context, TimeSpan.FromHours(1));
        var reading = Task.Factory.StartNew(() => blocked.Capture(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? collecting = null;
        try
        {
            Assert.That(slow.Entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(context.EnqueueAction(new(GuaActionType.Click, "button"), out var id), Is.EqualTo(GuaActionError.None));
            Complete(context, id);
            collecting = Task.Factory.StartNew(() => { trace.Mark("unblocked"); return available.Capture(); },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.That(await Task.WhenAny(collecting, Task.Delay(TimeSpan.FromSeconds(5))), Is.SameAs(collecting));
            Assert.That(await collecting, Is.True);
            Assert.That(context.TryPollActionEvent(id, out _), Is.True);
        }
        finally { slow.Release.Set(); await reading; if (collecting is not null) await collecting; }
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        Assert.That(Phases(GuaTraceReader.Read(trace.ArtifactPath)), Is.EqualTo(new[] { "enqueue", "consume", "completion" }));
    }

    [Test]
    public async Task PartialResetKeepsRequestEpochSeparateFromHostEpochAndClock()
    {
        using var context = Context(); await using var trace = Trace();
        var watcher = trace.Watch(context, TimeSpan.FromHours(1));
        context.EnqueueAction(new(GuaActionType.Click, "button"), out var id);
        context.Reset(new() { Targets = GuaResetTargets.None, Strict = false });
        Complete(context, id); watcher.Capture();
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        var completion = read.Events.Single(e => e.Type == "request.phase" && e.Data.GetProperty("phase").GetString() == "completion");
        Assert.That(completion.Data.GetProperty("request").GetProperty("sessionEpoch").GetString(), Is.EqualTo("1"));
        Assert.That(completion.Data.GetProperty("result").GetProperty("hostSessionEpoch").GetString(), Is.EqualTo("2"));
        var clocks = read.Events.Where(e => e.Type == "request.phase").Select(e => e.Data.GetProperty("result").GetProperty("hostClockId").GetString()).Distinct();
        Assert.That(clocks.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task EnqueueDoesNotClaimHostCompletionAndOverflowIsVisible()
    {
        using var context = Context(); await using var trace = Trace();
        var watcher = trace.Watch(context, TimeSpan.FromHours(1));
        for (var i = 0; i < 260; i++) { context.EnqueueAction(new(GuaActionType.Click, "button"), out var id); context.CancelAction(id); }
        context.EnqueueAction(new(GuaActionType.Click, "button"), out _);
        watcher.Capture(); await trace.CompleteAsync(GuaTraceOutcome.Unknown);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("native-lifecycle-gap"));
        Assert.That(Phases(read), Does.Not.Contain("completion"));
        Assert.That(read.Manifest.Quality.Issues, Does.Contain("unfinished-steps"));
    }

    [Test]
    public async Task ExplicitEpochKeysSeparateSameRequestAndRepeatedAttempts()
    {
        using var context = Context(); await using var trace = Trace();
        var watcher = trace.Watch(context, TimeSpan.FromHours(1));
        var first = trace.BeginStep(GuaTraceStepKind.Action, "same action", watcher.Request(1, 7));
        var second = trace.BeginStep(GuaTraceStepKind.Action, "same action", watcher.Request(2, 7));
        Assert.That(second, Is.Not.EqualTo(first));
        for (var i = 0; i < 2; i++) { context.EnqueueAction(new(GuaActionType.Click, "button"), out var id); Complete(context, id); }
        watcher.Capture(); trace.EndStep(first, GuaTraceOutcome.Passed); trace.EndStep(second, GuaTraceOutcome.Passed);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        Assert.That(GuaTraceReader.Read(trace.ArtifactPath).Events.Count(e => e.Type == "step.begin"), Is.EqualTo(4));
    }

    [Test]
    public async Task AssertionAndStorageFailurePreserveOriginalExceptionAndStack()
    {
        using var context = Context(); await using var trace = Trace();
        var original = new InvalidOperationException("secret-marker");
        var caught = Assert.Throws<InvalidOperationException>(() => trace.Assert("check", () => throw original));
        Assert.That(caught, Is.SameAs(original)); Assert.That(caught!.StackTrace, Does.Contain(nameof(AssertionAndStorageFailurePreserveOriginalExceptionAndStack)));
        Directory.CreateDirectory(Path.GetDirectoryName(trace.ArtifactPath)!);
        File.WriteAllText(trace.ArtifactPath, "prevent directory creation");
        Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Failed), Is.False);
        Assert.That(trace.Status.Issues, Does.Contain("write-failed"));
        Assert.That(caught, Is.SameAs(original));
    }

    [Test]
    public async Task NativeAndClientBuffersNeverRetainSensitivePayloadOrExceptionMessage()
    {
        using var context = new GuaContext();
        context.BeginFrame("test"); context.RegisterNode("input", "textbox", "Input", new(0, 0, 10, 10)); context.EndFrame();
        await using var trace = Trace();
        using var scope = GuaAssertionScope.Use(new() { Trace = trace });
        const string secret = "TRACE-SECRET-MARKER";
        var pending = GuaActionCompletion.EnqueueAndWaitAsync(context, new(GuaActionType.SetValue, "input", secret, Sensitive: true));
        Assert.That(context.TryConsumeAction(GuaActionType.SetValue, "input", out var request), Is.True);
        context.EmitActionResult(new(request.RequestId, GuaActionType.SetValue, true, GuaActionError.None, "input", secret, true));
        await pending;
        Assert.ThrowsAsync<GuaActionException>(() => GuaActionCompletion.EnqueueAndWaitAsync(context,
            new(GuaActionType.PressKey, "input", Key: secret, Sensitive: true), timeout: TimeSpan.FromMilliseconds(5)));
        Assert.Throws<InvalidOperationException>(() => trace.Assert("verify", () => throw new InvalidOperationException(secret)));
        Assert.That(context.GetDiagnosticsJson(), Does.Not.Contain(secret));
        await trace.CompleteAsync(GuaTraceOutcome.Failed);
        var serialized = string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.That(serialized, Does.Not.Contain(secret));
    }

    [TestCase(GuaGameInputKind.Semantic)]
    [TestCase(GuaGameInputKind.Keyboard)]
    public async Task InputHoldExpiryAndDisconnectRequireObservedReleaseAndLeaveResultsAvailable(GuaGameInputKind kind)
    {
        using var runtime = new GuaRuntime();
        runtime.EnableGameInput(GuaGameInputCapabilities.All, () => { });
        runtime.PublishGameInputActions("test", new[] { new GuaGameInputActionDescriptor("jump", "Jump", GuaGameInputValueType.Button, Holdable: true) });
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Assert.That(runtime.StartInspectorBridge(port), Is.True);
        using var remote = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        await using var trace = Trace(); var watcher = trace.Watch(remote, TimeSpan.FromHours(1));
        using var input = runtime.CreateGameInputSession();
        var id = input.Send(kind, kind == GuaGameInputKind.Semantic ? GuaGameInputOperation.Set : GuaGameInputOperation.Down,
            kind == GuaGameInputKind.Semantic ? "jump" : "Space", value: kind == GuaGameInputKind.Semantic ? true : null,
            lease: TimeSpan.FromMilliseconds(5));
        Assert.That(runtime.TryConsumeGameInput(out var hold), Is.True);
        watcher.Capture();
        runtime.CompleteGameInput(hold, true); watcher.Capture();
        Assert.That(input.PollResult(id).Succeeded, Is.True);
        runtime.TickGameInputLeases(TimeSpan.FromMilliseconds(6)); watcher.Capture();
        await trace.FlushAsync();
        var beforeRelease = Phases(GuaTraceReader.Read(trace.ArtifactPath));
        Assert.That(beforeRelease, Does.Contain("lease-expired"));
        Assert.That(beforeRelease, Does.Not.Contain("release-confirmed"));
        Assert.That(runtime.TryConsumeGameInput(out var release), Is.True); runtime.CompleteGameInput(release, true);
        input.Dispose(); watcher.Capture();
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
        var read = GuaTraceReader.Read(trace.ArtifactPath);
        Assert.That(Phases(read), Does.Contain("hold-pending")); Assert.That(Phases(read), Does.Contain("hold-started"));
        Assert.That(Phases(read), Does.Contain("release-requested")); Assert.That(Phases(read), Does.Contain("release-confirmed"));
        Assert.That(Phases(read), Does.Contain("owner-disconnected"));
    }
}

