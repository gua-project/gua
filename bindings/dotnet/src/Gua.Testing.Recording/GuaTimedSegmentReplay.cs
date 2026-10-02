using System.Diagnostics;
using System.Text.Json;

namespace Gua.Testing.Recording;

/// <summary>Additive Replay path. Legacy ReplayAsync keeps its sequential timing semantics.</summary>
public static class GuaTimedSegmentReplay
{
    public static async Task<GuaTimedSegmentResult> ReplayAsync(IGuaTimedSegmentHost host, GuaTimedSegment segment,
        Func<string, JsonElement?>? secretResolver = null, CancellationToken cancellationToken = default,
        IGuaSegmentRealtime? realtime = null)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (segment is null) throw new ArgumentNullException(nameof(segment));
        segment = segment with { Inputs = segment.Inputs?.Select(input => input is null ? null! : input with { Value = input.Value?.Clone() }).ToArray()! };
        GuaTimedSegmentFile.Validate(segment);
        var simulationScope = segment.Clock == GuaSegmentClock.Simulation ? host.SimulationScope : null;
        if (!host.OrderedApplication || (segment.RequireApplicationTimes || segment.RequireSameTickApplication) && !host.ApplicationTimes ||
            segment.RequireSameTickApplication && !host.SameTickApplication ||
            segment.Clock == GuaSegmentClock.Simulation && string.IsNullOrWhiteSpace(simulationScope))
            throw new NotSupportedException("The host cannot satisfy the requested segment timing capabilities.");
        // Freeze caller-owned plans before any await; resolve secrets before reserving/starting a segment.
        JsonElement?[] secrets;
        try { secrets = segment.Inputs.Select(input => input.Sensitive
            ? secretResolver?.Invoke(input.SecretKey!)?.Clone() : null).ToArray(); }
        catch { throw new InvalidOperationException("Segment secret resolution failed."); }
        if (segment.Inputs.Where((input, i) => input.Sensitive && secrets[i] is null).Any())
            throw new InvalidOperationException("A segment secret could not be resolved.");
        for (var i = 0; i < segment.Inputs.Count; i++)
            if (segment.Inputs[i].Sensitive) GuaTimedSegmentFile.ValidatePayload(segment.Inputs[i], secrets[i]);
        cancellationToken.ThrowIfCancellationRequested();
        realtime ??= new StopwatchRealtime();
        var results = segment.Inputs.Select((input, index) => new GuaTimedInputResult(
            index, input.OffsetMilliseconds, null, null, null, null, null, null)).ToArray();
        var origin = 0.0;
        var simulationOrigin = 0.0;
        var lastScheduleTime = 0.0;
        var outcome = GuaSegmentOutcome.Succeeded;
        string? failure = null;
        bool cleanupSucceeded = false, neutral = false;
        var pollCursor = 0;
        double Elapsed() => realtime.Milliseconds - origin;
        double ScheduleElapsed()
        {
            var value = segment.Clock == GuaSegmentClock.Realtime ? Elapsed() : host.SimulationMilliseconds - simulationOrigin;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < lastScheduleTime)
                throw new InvalidOperationException("Segment clock is invalid or changed generation.");
            lastScheduleTime = value;
            return value;
        }
        bool PollAll()
        {
            if (host.ExecutionFailureCode is { } health && outcome == GuaSegmentOutcome.Succeeded)
            { outcome = GuaSegmentOutcome.Failed; failure = health is "lifecycle-source-changed" or
                "lifecycle-evidence-gap" or "lease-expired-before-release" or "lifecycle-or-session-unconfirmed"
                ? health : "host-health-failed"; }
            // A bounded round-robin sweep keeps large pending sets from monopolizing dispatch.
            var polls = 0;
            for (var visited = 0; visited < results.Length && polls < 16; visited++)
            {
                var i = pollCursor;
                pollCursor = (pollCursor + 1) % results.Length;
                var result = results[i];
                if (result.RequestId is not { } id) continue;
                if (result.ResultReceivedMilliseconds is not null) continue;
                polls++;
                var receipt = host.Poll(id);
                if (receipt is null) continue;
                results[i] = result with { ResultReceivedMilliseconds = Elapsed(), Succeeded = receipt.Succeeded,
                    ErrorCode = receipt.ErrorCode, HostAppliedMilliseconds = receipt.HostAppliedMilliseconds is { } stamp &&
                        !double.IsNaN(stamp) && !double.IsInfinity(stamp) && stamp >= 0 ? stamp : null };
                if (!receipt.Succeeded && outcome == GuaSegmentOutcome.Succeeded)
                { outcome = GuaSegmentOutcome.Failed; failure = "host-rejected-or-failed"; }
                if (outcome == GuaSegmentOutcome.Succeeded && (segment.RequireApplicationTimes || segment.RequireSameTickApplication) && receipt.HostAppliedMilliseconds is null)
                { outcome = GuaSegmentOutcome.Failed; failure = "application-time-unconfirmed"; }
                if (outcome == GuaSegmentOutcome.Succeeded && receipt.HostAppliedMilliseconds is { } applied &&
                    (double.IsNaN(applied) || double.IsInfinity(applied) || applied < result.ScheduledMilliseconds ||
                    applied - result.ScheduledMilliseconds > segment.MaxLatenessMilliseconds))
                { outcome = GuaSegmentOutcome.Late; failure = "application-time-violation"; }
            }
            return results.All(result => result.SentMilliseconds is null || result.ResultReceivedMilliseconds is not null);
        }
        if (host is IGuaTimedSegmentValueHost valueHost)
            valueHost.Begin(segment, segment.Inputs.Select((input, i) => input.Sensitive ? secrets[i] : input.Value).ToArray());
        else host.Begin(segment);
        try
        {
            origin = realtime.Milliseconds;
            if (segment.Clock == GuaSegmentClock.Simulation) simulationOrigin = host.SimulationMilliseconds;
            for (var i = 0; i < results.Length && outcome == GuaSegmentOutcome.Succeeded; i++)
            {
                var input = segment.Inputs[i];
                while (ScheduleElapsed() < input.OffsetMilliseconds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Elapsed() >= segment.ExecutionTimeoutMilliseconds)
                    { outcome = GuaSegmentOutcome.TimedOut; failure = "execution-timeout"; break; }
                    PollAll();
                    if (outcome != GuaSegmentOutcome.Succeeded) break;
                    await realtime.DelayAsync(TimeSpan.FromMilliseconds(Math.Min(2,
                        Math.Max(0.01, input.OffsetMilliseconds - ScheduleElapsed()))), cancellationToken).ConfigureAwait(false);
                }
                if (outcome != GuaSegmentOutcome.Succeeded) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (Elapsed() >= segment.ExecutionTimeoutMilliseconds)
                { outcome = GuaSegmentOutcome.TimedOut; failure = "execution-timeout"; break; }
                if (ScheduleElapsed() - input.OffsetMilliseconds > segment.MaxLatenessMilliseconds)
                { outcome = GuaSegmentOutcome.Late; failure = "max-lateness-exceeded"; break; }
                // Send never waits for a prior request's completion.
                var boundaryChecked = false;
                var requestId = host.Send(input, secrets[i], () =>
                {
                    if (boundaryChecked) throw new InvalidOperationException("Host repeated the send boundary guard.");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Elapsed() >= segment.ExecutionTimeoutMilliseconds)
                        throw new SegmentStop(GuaSegmentOutcome.TimedOut, "execution-timeout");
                    if (ScheduleElapsed() - input.OffsetMilliseconds > segment.MaxLatenessMilliseconds)
                        throw new SegmentStop(GuaSegmentOutcome.Late, "max-lateness-exceeded");
                    results[i] = results[i] with { SentMilliseconds = Elapsed() };
                    boundaryChecked = true;
                });
                if (!boundaryChecked) results[i] = results[i] with { SentMilliseconds = Elapsed() };
                if (requestId == 0) throw new InvalidOperationException("Host returned an invalid request ID.");
                results[i] = results[i] with { RequestId = requestId };
                if (!boundaryChecked) throw new InvalidOperationException("Host omitted the send boundary guard.");
                if (ScheduleElapsed() - input.OffsetMilliseconds > segment.MaxLatenessMilliseconds)
                { outcome = GuaSegmentOutcome.Late; failure = "send-exceeded-max-lateness"; break; }
                if (i + 1 == results.Length || segment.Inputs[i + 1].OffsetMilliseconds != input.OffsetMilliseconds)
                    PollAll(); // Do not poll between sends in the same-offset batch.
            }
            while (outcome == GuaSegmentOutcome.Succeeded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Elapsed() >= segment.ExecutionTimeoutMilliseconds)
                { outcome = GuaSegmentOutcome.TimedOut; failure = "completion-or-boundary-timeout"; break; }
                var completed = PollAll();
                if (outcome != GuaSegmentOutcome.Succeeded) break;
                var boundaryReached = completed && ScheduleElapsed() >= segment.DurationMilliseconds;
                cancellationToken.ThrowIfCancellationRequested();
                if (Elapsed() >= segment.ExecutionTimeoutMilliseconds)
                { outcome = GuaSegmentOutcome.TimedOut; failure = "completion-or-boundary-timeout"; break; }
                if (boundaryReached) break;
                await realtime.DelayAsync(TimeSpan.FromMilliseconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { outcome = GuaSegmentOutcome.Cancelled; failure = "caller-cancelled"; }
        catch (SegmentStop stop) { outcome = stop.Outcome; failure = stop.Code; }
        catch
        {
            // Host/transport exceptions may contain sensitive values; return only a fixed safe code.
            outcome = GuaSegmentOutcome.Failed; failure = "host-or-clock-exception";
        }
        finally
        {
            // Safety deadlines and cleanup do not use simulation time or caller cancellation.
            try
            {
                ulong cleanupId;
                double cleanupOrigin;
                try { cleanupOrigin = realtime.Milliseconds; }
                finally { cleanupId = host.ReleaseAll(); } // Still attempt safety cleanup if the clock throws.
                bool WithinCleanupBudget()
                {
                    var elapsed = realtime.Milliseconds - cleanupOrigin;
                    return !double.IsNaN(elapsed) && !double.IsInfinity(elapsed) && elapsed >= 0 &&
                        elapsed < segment.CleanupTimeoutMilliseconds;
                }
                while (WithinCleanupBudget())
                {
                    var completed = PollAll();
                    if (!WithinCleanupBudget()) break;
                    var cleanup = cleanupSucceeded ? null : host.Poll(cleanupId);
                    if (!WithinCleanupBudget()) break;
                    if (cleanup is not null)
                    {
                        cleanupSucceeded = cleanup.Succeeded;
                        if (!cleanupSucceeded) break;
                    }
                    if (completed && cleanupSucceeded)
                    {
                        // All ordinary in-flight work is finished; a final neutralization covers
                        // an adapter that completed cleanup before a delayed ordinary completion.
                        cleanupId = host.ReleaseAll();
                        cleanupSucceeded = false;
                        while (WithinCleanupBudget())
                        {
                            var final = host.Poll(cleanupId);
                            if (!WithinCleanupBudget()) break;
                            if (final is not null)
                            {
                                cleanupSucceeded = final.Succeeded;
                                PollAll(); // Includes lifecycle/epoch health through the final host completion.
                                neutral = cleanupSucceeded && host.IsNeutral && WithinCleanupBudget();
                                break;
                            }
                            await realtime.DelayAsync(TimeSpan.FromMilliseconds(2), CancellationToken.None).ConfigureAwait(false);
                        }
                        break;
                    }
                    await realtime.DelayAsync(TimeSpan.FromMilliseconds(2), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { cleanupSucceeded = false; neutral = false; }
            finally { try { host.End(); } catch { cleanupSucceeded = false; neutral = false; } }
        }
        if ((!neutral || !cleanupSucceeded) && outcome == GuaSegmentOutcome.Succeeded)
        { outcome = GuaSegmentOutcome.Failed; failure = "cleanup-unconfirmed"; }
        var applied = results.Select(result => result.HostAppliedMilliseconds).ToArray();
        var applicationConfirmed = outcome == GuaSegmentOutcome.Succeeded && applied.All(time => time is not null) && results.All(result =>
            result.HostAppliedMilliseconds >= result.ScheduledMilliseconds &&
            result.HostAppliedMilliseconds - result.ScheduledMilliseconds <= segment.MaxLatenessMilliseconds);
        if (applicationConfirmed)
        {
            for (var i = 1; i < applied.Length; i++)
                if (applied[i] < applied[i - 1] || segment.RequireSameTickApplication &&
                    results[i].ScheduledMilliseconds == results[i - 1].ScheduledMilliseconds && applied[i] != applied[i - 1])
                {
                    if (outcome == GuaSegmentOutcome.Succeeded)
                    { outcome = GuaSegmentOutcome.Failed; failure = "application-order-or-tick-violation"; }
                    applicationConfirmed = false;
                }
        }
        return new(outcome, results, cleanupSucceeded, neutral, failure)
        {
            Clock = segment.Clock, SimulationScope = simulationScope,
            MaxLatenessMilliseconds = segment.MaxLatenessMilliseconds, ExecutionTimeoutMilliseconds = segment.ExecutionTimeoutMilliseconds,
            CleanupTimeoutMilliseconds = segment.CleanupTimeoutMilliseconds, ApplicationTimingConfirmed = applicationConfirmed,
        };
    }

    private sealed class StopwatchRealtime : IGuaSegmentRealtime
    {
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        public double Milliseconds => stopwatch.Elapsed.TotalMilliseconds;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }
    private sealed class SegmentStop(GuaSegmentOutcome outcome, string code) : Exception
    {
        internal GuaSegmentOutcome Outcome { get; } = outcome;
        internal string Code { get; } = code;
    }
}
