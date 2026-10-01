using System.Globalization;
using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

public sealed partial class GuaTraceSession
{
    private sealed class ContextComparer : IEqualityComparer<IGuaContext>
    {
        public bool Equals(IGuaContext? a, IGuaContext? b) => ReferenceEquals(a, b);
        public int GetHashCode(IGuaContext context) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(context);
    }
    private readonly Dictionary<IGuaContext, GuaTraceLifecycle> _lifecycles = new(new ContextComparer());
    private bool _lifecycleClosing;
    private int _nativePending;
    private readonly Dictionary<string, ulong> _nativeSequences = new();
    internal void ReserveNative() { lock (_gate) _nativePending++; }
    internal void ReleaseNative() { lock (_gate) if (_nativePending > 0) _nativePending--; }
    internal bool NativePending { get { lock (_gate) return _nativePending != 0; } }
    internal bool AcceptNativeSequence(string source, ulong sequence)
    {
        lock (_gate)
        {
            if (_nativeSequences.TryGetValue(source, out var previous) && sequence <= previous) return false;
            if (_nativeSequences.Count >= 64 && !_nativeSequences.ContainsKey(source)) { _issues.Add("native-source-limit"); return false; }
            _nativeSequences[source] = sequence; return true;
        }
    }
    private readonly AsyncLocal<string?> _explicitStep = new();

    /// <summary>Associate automatic operations with an explicit Step in this async flow.</summary>
    public IDisposable UseStep(string stepId)
    {
        lock (_gate)
            if (!_byId.ContainsKey(stepId)) throw new ArgumentException("Unknown Trace Step.", nameof(stepId));
        var previous = _explicitStep.Value;
        _explicitStep.Value = stepId;
        return new StepScope(() => _explicitStep.Value = previous);
    }
    private sealed class StepScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
    internal string ActionStep(string label) => _explicitStep.Value ?? BeginStep(GuaTraceStepKind.Action, label);
    /// <summary>Record verification without replacing its original exception or stack.</summary>
    public void Assert(string label, Action assertion,
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFile = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0)
    {
        var step = BeginStep(GuaTraceStepKind.Assertion, label, sourceFile: sourceFile, sourceLine: sourceLine);
        try
        {
            assertion();
            Record(step, "assertion.result", GuaTraceJson.Element(new { truth = "true", expectedState = "observed" }));
            EndStep(step, GuaTraceOutcome.Passed);
        }
        catch (Exception error)
        {
            Record(step, "assertion.result", GuaTraceJson.Element(new { truth = "unknown", failureType = error.GetType().FullName }));
            EndStep(step, error is OperationCanceledException ? GuaTraceOutcome.Interrupted : GuaTraceOutcome.Failed);
            throw;
        }
    }
    internal bool IsEnded(string stepId) { lock (_gate) return _byId.TryGetValue(stepId, out var step) && step.Ended; }
    internal bool IsLateCompletion(string stepId)
    {
        lock (_gate) return _byId.TryGetValue(stepId, out var step) && step.Ended && !step.ClientCompleted &&
            step.Outcome is GuaTraceOutcome.Unknown or GuaTraceOutcome.Interrupted;
    }
    internal bool IsLifecycleStep(string stepId) { lock (_gate) return _byId.TryGetValue(stepId, out var step) && step.Kind == GuaTraceStepKind.Lifecycle; }
    internal void LifecycleIssue(string issue) { lock (_gate) if (!_closed) _issues.Add(issue); }
    internal bool FindRequest(GuaTraceRequest request, out string stepId)
    { lock (_gate) return _requests.TryGetValue(request, out stepId!); }

    /// <summary>Observe Debug diagnostics without consuming completion queues. Call before raw host operations.</summary>
    public GuaTraceLifecycle Watch(IGuaContext context, TimeSpan? pollInterval = null)
    {
        Guard.NotNull(context, nameof(context));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        if (interval < TimeSpan.FromMilliseconds(1)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        lock (_gate)
        {
            if (_lifecycles.TryGetValue(context, out var existing)) return existing;
            if (_closed || _lifecycleClosing) throw new ObjectDisposedException(nameof(GuaTraceSession));
            if (_lifecycles.Count >= 64) throw new InvalidOperationException("Trace source limit.");
            var watcher = new GuaTraceLifecycle(this, context, interval);
            _lifecycles.Add(context, watcher);
            return watcher;
        }
    }
    private async Task StopLifecycleAsync()
    {
        GuaTraceLifecycle[] watchers;
        lock (_gate) { _lifecycleClosing = true; watchers = _lifecycles.Values.ToArray(); }
        await Task.WhenAll(watchers.Select(watcher => watcher.StopAsync(_options.FlushTimeout))).ConfigureAwait(false);
    }
    internal TimeSpan LifecycleTimeout => _options.FlushTimeout;
}

/// <summary>A bounded, read-only native lifecycle observer. Does not control input or host clocks.</summary>
public sealed class GuaTraceLifecycle : IDisposable, IAsyncDisposable
{
    private readonly GuaTraceSession _trace;
    private readonly IGuaContext _context;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private string? _hostSource;
    private ulong _cursor;
    private int _pending;
    private bool _supported;
    private bool _disposed;
    private Task? _shutdown;
    /// <summary>Use a host-confirmed epoch to associate explicit records with the same native request.</summary>
    public GuaTraceRequest Request(ulong sessionEpoch, ulong requestId, ulong? inputOwnerId = null) =>
        new("native:" + _hostSource + ":" + (inputOwnerId.HasValue ? "input:" + inputOwnerId.Value.ToString(CultureInfo.InvariantCulture) : "ui:0"),
            sessionEpoch.ToString(CultureInfo.InvariantCulture), requestId.ToString(CultureInfo.InvariantCulture));

    internal GuaTraceLifecycle(GuaTraceSession trace, IGuaContext context, TimeSpan interval)
    {
        _trace = trace; _context = context;
        // Baseline precedes registration: old requests are outside this collection interval.
        try
        {
            using var document = JsonDocument.Parse(context.GetDiagnosticsJson());
            if (document.RootElement.TryGetProperty("traceLifecycle", out var journal))
            {
                _hostSource = journal.GetProperty("sourceId").GetString();
                _cursor = Number(journal, "lastSequence"); _supported = true;
            }
        }
        catch { trace.LifecycleIssue("native-capture-failed"); }
        if (!_supported) trace.LifecycleIssue("native-lifecycle-not-provided");
        _worker = _supported ? Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                    Capture();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }) : Task.CompletedTask;
    }
    internal void Reserve() { lock (_gate) { _pending++; _trace.ReserveNative(); } }
    internal void Abandon() { lock (_gate) if (_pending > 0) { _pending--; _trace.ReleaseNative(); } }
    private GuaTraceRequest Key(JsonElement record) => new("native:" + _hostSource + ":" +
        record.GetProperty("domain").GetString() + ":" + record.GetProperty("ownerId").GetString(),
        record.GetProperty("sessionEpoch").GetString()!, record.GetProperty("requestId").GetString()!);
    internal void Bind(ulong requestId, string stepId)
    {
        try
        {
            if (!_supported) return;
            using var doc = JsonDocument.Parse(_context.GetDiagnosticsJson());
            lock (_gate)
            {
                var journal = doc.RootElement.GetProperty("traceLifecycle");
                var host = journal.GetProperty("sourceId").GetString();
                if (host != _hostSource) { _trace.LifecycleIssue("native-source-changed"); _hostSource = host; _cursor = 0; }
                foreach (var record in journal.GetProperty("events").EnumerateArray())
                    if (record.GetProperty("domain").GetString() == "ui" && Number(record, "requestId") == requestId &&
                        record.GetProperty("phase").GetString() == "enqueue")
                    { _trace.Correlate(stepId, Key(record)); return; }
                _trace.LifecycleIssue("native-correlation-unavailable");
            }
        }
        catch { _trace.LifecycleIssue("native-capture-failed"); }
        finally { Abandon(); }
    }
    /// <summary>Collect available facts now. Never polls an operation result.</summary>
    public bool Capture()
    {
        if (!_supported) return false;
        lock (_gate) if (_disposed) return false;
        try
        {
            using var document = JsonDocument.Parse(_context.GetDiagnosticsJson());
            lock (_gate)
            {
                if (_disposed || _trace.NativePending) return false;
                var journal = document.RootElement.GetProperty("traceLifecycle");
                var host = journal.GetProperty("sourceId").GetString();
                if (host != _hostSource) { _trace.LifecycleIssue("native-source-changed"); _hostSource = host; _cursor = 0; }
                foreach (var record in journal.GetProperty("events").EnumerateArray())
                {
                    var sequence = Number(record, "sequence");
                    if (sequence <= _cursor) continue;
                    if (sequence != _cursor + 1) _trace.LifecycleIssue("native-lifecycle-gap");
                    _cursor = sequence;
                    if (!_trace.AcceptNativeSequence(_hostSource!, sequence)) continue;
                    var key = Key(record);
                    var known = _trace.FindRequest(key, out var step);
                    var phase = record.GetProperty("phase").GetString()!;
                    if (!known && phase is not ("enqueue" or "release-requested" or "owner-disconnected"))
                    { _trace.LifecycleIssue("native-request-outside-correlation-window"); continue; }
                    if (!known) step = _trace.BeginStep(GuaTraceStepKind.Lifecycle, "native " + record.GetProperty("domain").GetString(), key);
                    var ended = _trace.IsEnded(step);
                    _trace.RecordRequest(key, phase == "completion" && _trace.IsLateCompletion(step) ? "late-completion" : phase, record);
                    if (phase == "owner-disconnected") _trace.EndStep(step, GuaTraceOutcome.Passed);
                    if (phase is "completion" or "cancelled" && !ended)
                    {
                        var result = record.GetProperty("result");
                        var outcome = result.TryGetProperty("succeeded", out var success) && success.ValueKind == JsonValueKind.True
                            ? GuaTraceOutcome.Passed : GuaTraceOutcome.Failed;
                        // Client-bound steps end at the caller result, native-only steps at host result.
                        if (!known || IsNativeStep(step)) _trace.EndStep(step, outcome);
                    }
                }
                if (Number(journal, "lastSequence") > _cursor) _trace.LifecycleIssue("native-lifecycle-gap");
                return true;
            }
        }
        catch { _trace.LifecycleIssue("native-capture-failed"); return false; }
    }
    private bool IsNativeStep(string id) => _trace.IsLifecycleStep(id);
    private static ulong Number(JsonElement value, string name) => ulong.Parse(value.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(StopAsync(_trace.LifecycleTimeout));
    internal Task StopAsync(TimeSpan timeout)
    {
        lock (_gate) return _shutdown ??= StopCoreAsync(timeout);
    }
    private async Task StopCoreAsync(TimeSpan timeout)
    {
        _stop.Cancel();
        var finishing = Task.Run(async () =>
        {
            try { await _worker.ConfigureAwait(false); Capture(); }
            catch { _trace.LifecycleIssue("native-capture-failed"); }
        });
        var winner = await Task.WhenAny(finishing, Task.Delay(timeout)).ConfigureAwait(false);
        // A synchronous context read cannot be forcibly cancelled. Freeze its bounded
        // reader so it cannot append facts after timeout/finalization.
        lock (_gate) _disposed = true;
        if (winner != finishing) _trace.LifecycleIssue("native-stop-timeout");
        else await finishing.ConfigureAwait(false);
    }
}
