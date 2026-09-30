using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[TestFixture]
public sealed class LocatorAutoWaitTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(1);

    [TestCase("resolve")]
    [TestCase("actionable")]
    [TestCase("action")]
    public void InvalidValueRegexFailsBeforeLookingForAbsentCandidates(string operation)
    {
        var context = new Fixture { Ids = [], OnQuery = _ => Assert.Fail("Invalid regex must fail before querying.") };
        var query = GuaAssertions.Query(context).ByValue("secret-pattern[", GuaMatchMode.Regex);
        var error = Assert.ThrowsAsync<GuaAssertionException>(async () =>
        {
            if (operation == "resolve") await query.ResolveAsync(Limit, Poll);
            else if (operation == "actionable") await query.WaitForActionableAsync(GuaActionType.Click, Limit, Poll);
            else await query.ClickAsync(Limit, Poll);
        });
        Assert.That(error!.Message, Does.Contain("Invalid Gua value regex"));
        Assert.That(error.ToString(), Does.Not.Contain("secret-pattern"));
        Assert.That(context.QueryCount, Is.Zero);
        Assert.That(context.Sent, Is.Empty);
    }

    [TestCase("role")]
    [TestCase("name")]
    [TestCase("text")]
    [TestCase("scope")]
    [TestCase("visible")]
    [TestCase("enabled")]
    public void ChangedBaseSelectorCannotResolveAStaleId(string field)
    {
        using var native = new GuaContext();
        var original = new GuaNodeDescriptor("same", "button", "Go", new GuaBounds(0, 0, 1, 1), ParentId: "root", Text: "Start");
        void Publish(GuaNodeDescriptor descriptor)
        {
            native.BeginFrame("fixture");
            native.RegisterNode(new GuaNodeDescriptor("root", "panel", "Root", new GuaBounds(0, 0, 1, 1)));
            native.RegisterNode(descriptor);
            native.EndFrame();
        }
        Publish(original);
        var changed = field switch
        {
            "role" => original with { Role = "textbox" },
            "name" => original with { Label = "Other" },
            "text" => original with { Text = "Other" },
            "scope" => original with { ParentId = null },
            "visible" => original with { Visible = false },
            _ => original with { Enabled = false },
        };
        var context = new SwitchingContext(native, () => Publish(changed));
        var query = GuaAssertions.Query(context).ByRole("button", "Go").ByText("Start").Within("root").WhereVisible().WhereEnabled();
        var error = Assert.ThrowsAsync<GuaAssertionException>(() => query.ResolveAsync(TimeSpan.FromMilliseconds(200), Poll));
        Assert.That(error!.Message, Does.Contain("timed out"));
    }

    [Test]
    public async Task ChangedSnapshotCannotReportStaleAmbiguity()
    {
        using var native = new GuaContext();
        void Publish(bool two)
        {
            native.BeginFrame("fixture");
            native.RegisterNode(new GuaNodeDescriptor("one", "button", "One", new GuaBounds(0, 0, 1, 1)));
            if (two) native.RegisterNode(new GuaNodeDescriptor("two", "button", "Two", new GuaBounds(0, 0, 1, 1)));
            native.EndFrame();
        }
        Publish(true);
        var context = new SwitchingContext(native, () => Publish(false));
        var node = await GuaAssertions.Query(context).ByRole("button").ResolveAsync(Limit, Poll);
        Assert.That(node.Id, Is.EqualTo("one"));
    }

    [Test]
    public void InvalidValueRegexDoesNotExposeSelectorContents()
    {
        var context = new Fixture();
        var error = Assert.CatchAsync<Exception>(() => GuaAssertions.Query(context)
            .ByRole("button").ByValue("secret-pattern[", GuaMatchMode.Regex).ClickAsync(Limit, Poll));
        Assert.That(error!.ToString(), Does.Not.Contain("secret-pattern"));
        Assert.That(context.Sent, Is.Empty);
    }

    [Test]
    public void ValueRegexEvaluationUsesBudgetAndRedactsTimeoutInput()
    {
        var context = new Fixture { Value = new string('a', 20000) + "secret-input!" };
        var error = Assert.ThrowsAsync<GuaActionException>(() => GuaAssertions.Query(context)
            .ByRole("button").ByValue("^(a+)+$", GuaMatchMode.Regex)
            .ClickAsync(TimeSpan.FromMilliseconds(40), Poll));
        Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.TimedOut));
        Assert.That(error.ToString(), Does.Not.Contain("secret-input"));
        Assert.That(context.Sent, Is.Empty);
    }

    [Test]
    public void ValuePredicateAndActionabilityUseTheSameSnapshot()
    {
        var context = new Fixture();
        context.OnSnapshot = n => context.Value = n % 2 == 1 ? "target" : "other";
        var error = Assert.ThrowsAsync<GuaActionException>(() => GuaAssertions.Query(context)
            .ByRole("button").ByValue("target").ClickAsync(TimeSpan.FromMilliseconds(100), Poll));
        Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.TimedOut));
        Assert.That(context.Sent, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExpiredQueryDoesNotStartAnotherTransportOrDiagnosticRead(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var context = new Fixture
        {
            OnQuery = _ => { if (cancel) cancellation.Cancel(); else Thread.Sleep(80); },
            OnSnapshot = n => Assert.That(n, Is.EqualTo(1), "Only the pre-query snapshot is allowed before the query expires."),
        };
        var error = Assert.ThrowsAsync<GuaActionException>(() => GuaAssertions.Query(context)
            .ByRole("button").ClickAsync(TimeSpan.FromMilliseconds(50), Poll, cancellation.Token));
        Assert.That(error!.Kind, Is.EqualTo(cancel ? GuaActionFailureKind.Cancelled : GuaActionFailureKind.TimedOut));
        Assert.That(context.Sent, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LateEnqueueRejectionStillHonorsDeadlineAndCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var context = new Fixture
        {
            Rejection = GuaActionError.Disabled,
            OnEnqueue = () => { if (cancel) cancellation.Cancel(); else Thread.Sleep(80); },
        };
        var error = Assert.ThrowsAsync<GuaActionException>(() => GuaAssertions.Query(context)
            .ByRole("button").ClickAsync(TimeSpan.FromMilliseconds(50), Poll, cancellation.Token));
        Assert.That(error!.Kind, Is.EqualTo(cancel ? GuaActionFailureKind.Cancelled : GuaActionFailureKind.TimedOut));
        Assert.That(error.Error, Is.EqualTo(GuaActionError.Disabled));
        Assert.That(error.Message, Does.Contain("phase=enqueue"));
        Assert.That(context.Sent, Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TraceContentionCannotEnqueueAfterCancellationOrDeadline(bool cancel)
    {
        using var trace = new GuaTraceSession(new() { OutputDirectory = Path.Combine(Path.GetTempPath(), "gua-107-trace-tests"), SavePolicy = GuaTraceSavePolicy.OnFailure });
        using var cancellation = new CancellationTokenSource();
        using var snapshotReady = new ManualResetEventSlim();
        var context = new Fixture { OnSnapshot = n => { if (n == 4) snapshotReady.Set(); } };
        var gate = typeof(GuaTraceSession).GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(trace)!;
        GuaActionException? failure = null;
        var thread = new Thread(() =>
        {
            using var scope = GuaAssertionScope.Use(new() { Trace = trace });
            try { GuaAssertions.Query(context).ByRole("button").Click(cancel ? Limit : TimeSpan.FromMilliseconds(100), Poll, cancellation.Token); }
            catch (GuaActionException error) { failure = error; }
        });
        lock (gate)
        {
            thread.Start();
            Assert.That(snapshotReady.Wait(Limit), Is.True);
            Assert.That(SpinWait.SpinUntil(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, Limit), Is.True);
            if (cancel) cancellation.Cancel();
            else Thread.Sleep(150);
        }
        Assert.That(thread.Join(Limit), Is.True);
        Assert.That(failure, Is.Not.Null);
        Assert.That(failure!.Kind, Is.EqualTo(cancel ? GuaActionFailureKind.Cancelled : GuaActionFailureKind.TimedOut));
        Assert.That(failure.RequestId, Is.Zero);
        Assert.That(context.Sent, Is.Empty);
        await trace.CompleteAsync(GuaTraceOutcome.Passed);
    }

    [Test]
    public async Task LocalNativeWaitsForCorrelatedHostCompletion()
    {
        using var context = new GuaContext();
        context.BeginFrame("fixture"); context.EndFrame();
        var operation = GuaAssertions.Query(context).ByRole("button").ClickAsync(Limit, Poll);
        Assert.That(operation.IsCompleted, Is.False);
        context.BeginFrame("fixture");
        context.RegisterNode(new GuaNodeDescriptor("live", "button", "Live", new GuaBounds(0, 0, 1, 1)));
        context.EndFrame();
        GuaActionRequest request = default;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!context.TryConsumeAction(GuaActionType.Click, "live", out request))
        {
            Assert.That(timer.Elapsed, Is.LessThan(Limit));
            await Task.Delay(Poll);
        }
        Assert.That(operation.IsCompleted, Is.False);
        context.EmitActionResult(new GuaActionEvent(request.RequestId, request.Action, true, GuaActionError.None, "live", "", false));
        Assert.That((await operation).RequestId, Is.EqualTo(request.RequestId));
    }

    [Test]
    public async Task WebSocketWaitsForCorrelatedHostCompletion()
    {
        using var runtime = new GuaRuntime();
        runtime.BeginFrame("fixture");
        runtime.EndFrame();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.That(runtime.StartInspectorBridge(port), Is.True);
        using var context = new GuaWebSocketContext($"ws://127.0.0.1:{port}");
        context.WaitUntilAvailable(Limit);
        var operation = GuaAssertions.Query(context).ByRole("button").ClickAsync(Limit, Poll);
        Assert.That(operation.IsCompleted, Is.False);
        runtime.BeginFrame("fixture");
        runtime.RegisterNode(new GuaNodeDescriptor("live", "button", "Live", new GuaBounds(0, 0, 1, 1)));
        runtime.EndFrame();
        GuaActionRequest request = default;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!runtime.TryConsumeAction(GuaActionType.Click, "live", out request))
        {
            Assert.That(timer.Elapsed, Is.LessThan(Limit), "Locator never enqueued the request.");
            await Task.Delay(Poll);
        }
        Assert.That(operation.IsCompleted, Is.False, "Enqueue acceptance must not complete the locator action.");
        runtime.EmitActionResult(request, true);
        var result = await operation;
        Assert.That(result.RequestId, Is.EqualTo(request.RequestId));
        Assert.That(result.NodeId, Is.EqualTo("live"));
        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public async Task WaitsForAppearanceVisibilityEnablementAndActionThenReResolves()
    {
        var context = new Fixture();
        context.OnQuery = n =>
        {
            context.Ids = n == 1 ? [] : [n < 6 ? "old" : "new"];
            context.Visible = n >= 3;
            context.Enabled = n >= 4;
            context.Actions = n >= 5 ? ["click"] : [];
        };
        var result = await GuaAssertions.Query(context).ByRole("button").ClickAsync(Limit, Poll);
        Assert.Multiple(() =>
        {
            Assert.That(context.QueryCount, Is.GreaterThanOrEqualTo(6));
            Assert.That(context.Sent.Single().NodeId, Is.EqualTo("new"));
            Assert.That(result.RequestId, Is.EqualTo(42));
            Assert.That(context.Polled, Is.All.EqualTo(42));
            Assert.That(context.Sent, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ResolveDoesNotRequireActionabilityAndWaitDoesNotEnqueue()
    {
        var context = new Fixture { Visible = false, Enabled = false, Actions = [] };
        await GuaAssertions.Query(context).ByRole("button").ResolveAsync(Limit, Poll);
        context.OnQuery = _ => { context.Visible = true; context.Enabled = true; context.Actions = ["click"]; };
        await GuaAssertions.Query(context).ByRole("button").WaitForActionableAsync(GuaActionType.Click, Limit, Poll);
        Assert.That(context.Sent, Is.Empty);
    }

    [TestCase(1)]
    [TestCase(2)]
    public void AmbiguityFailsImmediatelyIncludingFinalResolution(int ambiguousAt)
    {
        var context = new Fixture();
        context.OnQuery = n => context.Ids = n >= ambiguousAt ? ["one", "two"] : ["one"];
        var error = Assert.ThrowsAsync<GuaAssertionException>(() =>
            GuaAssertions.Query(context).ByRole("button").ClickAsync(Limit, Poll));
        Assert.That(error!.Message, Does.Contain("matched 2").And.Contain("phase=actionability"));
        Assert.That(error.Message, Does.Contain("one (button").And.Contain("two (button").And.Contain("parentId=").And.Contain("Within("));
        Assert.That(context.QueryCount, Is.InRange(ambiguousAt, ambiguousAt + 1));
        Assert.That(context.Sent, Is.Empty);
    }

    [TestCase("missing")]
    [TestCase("hidden")]
    [TestCase("disabled")]
    [TestCase("unsupported")]
    public void UnactionableTimeoutHasSafeDiagnostics(string reason)
    {
        var context = new Fixture
        {
            Ids = reason == "missing" ? [] : ["one"],
            Visible = reason != "hidden", Enabled = reason != "disabled",
            Actions = reason == "unsupported" ? [] : ["set_value"],
        };
        var error = Assert.ThrowsAsync<GuaActionException>(() =>
            GuaAssertions.Query(context).ByRole("button").SetValueAsync("password-secret", sensitive: true,
                timeout: TimeSpan.FromMilliseconds(40), pollInterval: Poll));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.TimedOut));
            Assert.That(error.RequestId, Is.Zero);
            Assert.That(error.Message, Does.Contain("selector=").And.Contain("phase=actionability").And.Contain("frameSequence=").And.Contain("revision="));
            Assert.That(error.ToString(), Does.Not.Contain("password-secret"));
            Assert.That(context.Sent, Is.Empty);
        });
    }

    [Test]
    public void SharedBudgetIncludesSynchronousEnqueue()
    {
        var context = new Fixture { OnEnqueue = () => Thread.Sleep(100) };
        var error = Assert.ThrowsAsync<GuaActionException>(() =>
            GuaAssertions.Query(context).ByRole("button").ClickAsync(TimeSpan.FromMilliseconds(70), Poll));
        Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.TimedOut));
        Assert.That(error.RequestId, Is.EqualTo(42));
        Assert.That(error.Message, Does.Contain("phase=enqueue"));
        Assert.That(context.Polled, Is.Empty);
        Assert.That(context.Sent, Has.Count.EqualTo(1));
    }

    [Test]
    public void SharedBudgetIncludesActionabilityAndFinalResolution()
    {
        var context = new Fixture();
        context.OnQuery = _ => Thread.Sleep(55);
        var error = Assert.ThrowsAsync<GuaActionException>(() =>
            GuaAssertions.Query(context).ByRole("button").ClickAsync(TimeSpan.FromMilliseconds(90), Poll));
        Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.TimedOut));
        Assert.That(context.Sent, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CancellationBeforeEnqueueHasNoSideEffects(bool duringQuery)
    {
        using var cancellation = new CancellationTokenSource();
        var context = new Fixture();
        if (duringQuery) context.OnQuery = _ => cancellation.Cancel();
        else cancellation.Cancel();
        var error = Assert.ThrowsAsync<GuaActionException>(() =>
            GuaAssertions.Query(context).ByRole("button").ClickAsync(Limit, Poll, cancellation.Token));
        Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.Cancelled));
        Assert.That(error.Message, Does.Contain("phase=actionability"));
        Assert.That(context.Sent, Is.Empty);
    }

    [Test]
    public void CancellationAfterAcceptancePreservesRequestCorrelation()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new Fixture { OnEnqueue = cancellation.Cancel };
        var error = Assert.ThrowsAsync<GuaActionException>(() =>
            GuaAssertions.Query(context).ByRole("button").ClickAsync(Limit, Poll, cancellation.Token));
        Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.Cancelled));
        Assert.That(error.RequestId, Is.EqualTo(42));
        Assert.That(error.Message, Does.Contain("phase=enqueue"));
        Assert.That(context.Sent, Has.Count.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void RejectionAndHostFailureAreDistinctAndNeverRetried(bool reject)
    {
        var context = new Fixture { Rejection = reject ? GuaActionError.Disabled : GuaActionError.None, Success = false };
        var error = Assert.ThrowsAsync<GuaActionException>(() =>
            GuaAssertions.Query(context).ByRole("button").ClickAsync(Limit, Poll));
        Assert.That(error!.Kind, Is.EqualTo(reject ? GuaActionFailureKind.Rejected : GuaActionFailureKind.Failed));
        Assert.That(context.Sent, Has.Count.EqualTo(1));
    }

    [Test]
    public void AcceptedWithoutCompletionTimesOutAndPollDelayIsBounded()
    {
        var context = new Fixture { HasCompletion = false };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        // Allow cold JIT/serialization on CI to reach enqueue, while keeping the
        // deadline and elapsed bound well below the deliberately long poll interval.
        var error = Assert.ThrowsAsync<GuaActionException>(() =>
            GuaAssertions.Query(context).ByRole("button").ClickAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)));
        Assert.That(error!.Kind, Is.EqualTo(GuaActionFailureKind.TimedOut));
        Assert.That(error.RequestId, Is.EqualTo(42));
        Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(context.Sent, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AllActionsReturnObservedCompletionWithoutGuessingPostconditions()
    {
        var context = new Fixture { Actions = ["click", "focus", "set_value", "set_checked", "select", "scroll", "press_key"] };
        var query = GuaAssertions.Query(context).ByRole("button");
        await query.ClickAsync();
        query.Focus();
        await query.SetValueAsync("secret", sensitive: true);
        query.SetChecked(true);
        await query.SelectAsync("option");
        query.Scroll(1, 2, 1);
        await query.PressKeyAsync("Enter", 2);
        Assert.That(context.Sent.Select(r => r.Action), Is.EqualTo(Enum.GetValues<GuaActionType>()));
        Assert.That(context.Sent[2].Sensitive, Is.True);
        Assert.That(context.Sent[3].BoolValue, Is.True);
        Assert.That(context.Sent[5].ScrollUnit, Is.EqualTo(1));
        Assert.That(context.Sent[6].Key, Is.EqualTo("Enter"));
    }

    private sealed class SwitchingContext(GuaContext native, Action change) : IGuaContext
    {
        private bool _changed;
        public GuaQueryResult Query(GuaSelector selector)
        {
            var result = native.Query(selector);
            if (!_changed) { _changed = true; change(); }
            return result;
        }
        public string GetUiTreeJson() => native.GetUiTreeJson();
        public GuaNodeState GetNodeState(string id) => native.GetNodeState(id);
        public string FindNodeById(string id) => native.FindNodeById(id);
        public string FindNodeByRole(string role, string? name = null) => native.FindNodeByRole(role, name);
        public string FindNodeByText(string text) => native.FindNodeByText(text);
        public bool EnqueueClick(string id) => native.EnqueueClick(id);
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId) => native.EnqueueAction(request, out requestId);
        public bool TryPollActionEvent(out GuaActionEvent e) => native.TryPollActionEvent(out e);
        public bool TryPollActionEvent(ulong id, out GuaActionEvent e) => native.TryPollActionEvent(id, out e);
        public bool TryPollEvent(out GuaEvent e) => native.TryPollEvent(out e);
    }

    private sealed class Fixture : IGuaContext
    {
        internal string[] Ids = ["one"];
        internal string[] Actions = ["click"];
        internal string Value = "test";
        internal bool Visible = true, Enabled = true, Success = true, HasCompletion = true;
        internal int QueryCount;
        internal Action<int>? OnQuery;
        internal Action<int>? OnSnapshot;
        private int _snapshots;
        internal Action? OnEnqueue;
        internal GuaActionError Rejection;
        internal List<GuaActionRequest> Sent = [];
        internal List<ulong> Polled = [];
        public GuaQueryResult Query(GuaSelector selector)
        {
            OnQuery?.Invoke(++QueryCount);
            return new(true, Ids.Select(id => new GuaNodeQueryMatch(id, "button", "Test", null)).ToArray());
        }
        public string GetUiTreeJson()
        {
            OnSnapshot?.Invoke(++_snapshots);
            return JsonSerializer.Serialize(new
        {
            schemaVersion = 2, sessionEpoch = 1, frameSequence = QueryCount, revision = 1, screen = "fixture",
            nodes = Ids.Select(id => new { id, role = "button", label = "Test", value = Value, visible = Visible, enabled = Enabled,
                bounds = new { x = 0, y = 0, w = 1, h = 1 }, actions = Actions }),
            });
        }
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId)
        {
            Sent.Add(request);
            OnEnqueue?.Invoke();
            requestId = 42;
            return Rejection;
        }
        public bool TryPollActionEvent(ulong requestId, out GuaActionEvent e)
        {
            Polled.Add(requestId);
            var request = Sent.Last();
            e = new(requestId, request.Action, Success, Success ? GuaActionError.None : GuaActionError.Disabled, request.NodeId!, "", request.Sensitive);
            return HasCompletion;
        }
        public bool TryPollActionEvent(out GuaActionEvent e) => throw new AssertionException("Must poll the correlated request only.");
        public bool TryPollEvent(out GuaEvent e) => throw new NotSupportedException();
        public bool EnqueueClick(string id) => throw new NotSupportedException();
        public GuaNodeState GetNodeState(string id) => throw new NotSupportedException();
        public string FindNodeById(string id) => throw new NotSupportedException();
        public string FindNodeByRole(string role, string? name = null) => throw new NotSupportedException();
        public string FindNodeByText(string text) => throw new NotSupportedException();
    }
}
