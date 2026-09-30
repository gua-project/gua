using System.Diagnostics;
using Gua.Core;

namespace Gua.Testing;

public enum GuaActionFailureKind
{
    Rejected,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed class GuaActionException : Exception
{
    public GuaActionException(
        GuaActionFailureKind kind,
        ulong requestId,
        GuaActionType action,
        string? nodeId,
        GuaActionError error,
        string message,
        Exception? innerException = null) : base(message, innerException)
    {
        Kind = kind;
        RequestId = requestId;
        Action = action;
        NodeId = nodeId;
        Error = error;
    }

    public GuaActionFailureKind Kind { get; }
    public ulong RequestId { get; }
    public GuaActionType Action { get; }
    public string? NodeId { get; }
    public GuaActionError Error { get; }
}

public static class GuaActionCompletion
{
    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(1);
    public static TimeSpan DefaultPollInterval { get; set; } = TimeSpan.FromMilliseconds(10);

    public static GuaActionEvent EnqueueAndWait(
        IGuaContext context,
        GuaActionRequest request,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null) =>
        EnqueueAndWaitAsync(context, request, timeout, pollInterval).GetAwaiter().GetResult();

    public static Task<GuaActionEvent> EnqueueAndWaitAsync(
        IGuaContext context,
        GuaActionRequest request,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        EnqueueWithBudgetAsync(context, request, timeout ?? DefaultTimeout, pollInterval ?? DefaultPollInterval,
            null, null, null, cancellationToken);

    internal static async Task<GuaActionEvent> EnqueueWithBudgetAsync(
        IGuaContext context, GuaActionRequest request, TimeSpan timeout, TimeSpan pollInterval,
        Stopwatch? budget, string? selector, string? lastState, CancellationToken cancellationToken,
        GuaTraceAction? trace = null)
    {
        Guard.NotNull(context, nameof(context));
        var limit = timeout;
        var interval = pollInterval;
        if (limit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));

        var phase = "enqueue";
        GuaActionException Failure(GuaActionFailureKind kind, ulong id, GuaActionError actionError, string message, Exception? inner = null) =>
            Create(context, kind, id, request.Action, request.NodeId, actionError,
                selector is null ? message : $"{message} selector={selector}, phase={phase}, last state: {lastState}.", inner, snapshotMetadata: selector is null ? null : "");
        if (cancellationToken.IsCancellationRequested)
            throw Failure(GuaActionFailureKind.Cancelled, 0, GuaActionError.None, "Gua action cancelled before enqueue.");
        if (budget is not null && budget.Elapsed >= limit)
            throw Failure(GuaActionFailureKind.TimedOut, 0, GuaActionError.None, "Gua action timed out before enqueue.");
        trace ??= GuaTraceAction.Begin(context, request, selector);
        // Trace recording can block; recheck at the actual send boundary.
        if (cancellationToken.IsCancellationRequested)
        {
            trace?.End(GuaTraceOutcome.Interrupted, "cancelled-before-enqueue");
            throw Failure(GuaActionFailureKind.Cancelled, 0, GuaActionError.None, "Gua action cancelled before enqueue.");
        }
        if (budget is not null && budget.Elapsed >= limit)
        {
            trace?.End(GuaTraceOutcome.Failed, "timeout-before-enqueue");
            throw Failure(GuaActionFailureKind.TimedOut, 0, GuaActionError.None, "Gua action timed out before enqueue.");
        }
        GuaActionError error;
        ulong requestId;
        trace?.Sending(request, selector);
        if (cancellationToken.IsCancellationRequested)
        {
            trace?.End(GuaTraceOutcome.Interrupted, "cancelled-before-enqueue");
            throw Failure(GuaActionFailureKind.Cancelled, 0, GuaActionError.None, "Gua action cancelled before enqueue.");
        }
        if (budget is not null && budget.Elapsed >= limit)
        {
            trace?.End(GuaTraceOutcome.Failed, "timeout-before-enqueue");
            throw Failure(GuaActionFailureKind.TimedOut, 0, GuaActionError.None, "Gua action timed out before enqueue.");
        }
        try { error = context.EnqueueAction(request, out requestId); }
        catch { trace?.End(GuaTraceOutcome.Unknown, "send-exception"); throw; }
        trace?.Accepted(requestId, error);
        if (budget is not null && cancellationToken.IsCancellationRequested)
        {
            trace?.End(GuaTraceOutcome.Interrupted, error == GuaActionError.None ? "cancelled-side-effects-unknown" : "cancelled-after-rejection");
            throw Failure(GuaActionFailureKind.Cancelled, requestId, error, "Gua action cancelled during enqueue.");
        }
        if (budget is not null && budget.Elapsed >= limit)
        {
            trace?.End(GuaTraceOutcome.Unknown, error == GuaActionError.None ? "timeout-side-effects-unknown" : "timeout-after-rejection");
            throw Failure(GuaActionFailureKind.TimedOut, requestId, error, "Gua action timed out during enqueue.");
        }
        if (error != GuaActionError.None)
        {
            trace?.End(GuaTraceOutcome.Failed, "rejected");
            throw Failure(GuaActionFailureKind.Rejected, requestId, error,
                $"Gua action was rejected: requestId={requestId}, action={request.Action}, nodeId='{request.NodeId ?? "<focused>"}', error={error}.");
        }

        var stopwatch = budget ?? Stopwatch.StartNew();
        phase = "completion";
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget is not null && stopwatch.Elapsed >= limit) break;
                if (context.TryPollActionEvent(requestId, out var result))
                {
                    trace?.Completed(result);
                    if (budget is not null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (stopwatch.Elapsed >= limit) break;
                    }
                    trace?.End(result.Succeeded ? GuaTraceOutcome.Passed : GuaTraceOutcome.Failed, "host-result");
                    if (!result.Succeeded)
                    {
                        throw Failure(GuaActionFailureKind.Failed, requestId, result.Error,
                            $"Gua action failed: requestId={requestId}, action={request.Action}, nodeId='{request.NodeId ?? "<focused>"}', error={result.Error}.");
                    }
                    return result;
                }
                if (stopwatch.Elapsed >= limit) break;
                var remaining = limit - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < interval ? remaining : interval, cancellationToken).ConfigureAwait(false);
            }
            while (true);
        }
        catch (OperationCanceledException cancellationError) when (cancellationToken.IsCancellationRequested)
        {
            trace?.End(GuaTraceOutcome.Interrupted, "cancelled-side-effects-unknown");
            throw Failure(GuaActionFailureKind.Cancelled, requestId, GuaActionError.None,
                $"Gua action wait was cancelled: requestId={requestId}, action={request.Action}, nodeId='{request.NodeId ?? "<focused>"}'.", cancellationError);
        }
        catch { trace?.End(GuaTraceOutcome.Unknown, "receive-exception"); throw; }

        trace?.End(GuaTraceOutcome.Unknown, "timeout-side-effects-unknown");
        throw Failure(GuaActionFailureKind.TimedOut, requestId, GuaActionError.None,
            $"Timed out after {limit:g} waiting for Gua action: requestId={requestId}, action={request.Action}, nodeId='{request.NodeId ?? "<focused>"}'.");
    }

    internal static GuaActionException Create(
        IGuaContext context, GuaActionFailureKind kind, ulong requestId, GuaActionType action,
        string? nodeId, GuaActionError error, string message, Exception? inner = null, string? snapshotMetadata = null)
    {
        var suffix = snapshotMetadata ?? GuaAssertions.DescribeSnapshot(context);
        var failure = new GuaActionException(kind, requestId, action, nodeId, error, $"{message} {suffix}", inner);
        var session = GuaAssertions.Options.DiagnosticsSession;
        if (session is not null)
        {
            var result = session.Capture(failure);
            if (result.ArtifactPath is not null)
                failure.Data["GuaDiagnosticsPath"] = result.ArtifactPath;
            if (result.CaptureErrors.Count > 0)
                failure.Data["GuaDiagnosticsCaptureErrors"] = result.CaptureErrors;
        }
        return failure;
    }
}
