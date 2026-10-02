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
        if (!host.OrderedApplication || (segment.RequireApplicationTimes || segment.RequireSameTickApplication) && !host.ApplicationTimes ||
            segment.RequireSameTickApplication && !host.SameTickApplication ||
            segment.Clock == GuaSegmentClock.Simulation && string.IsNullOrWhiteSpace(host.SimulationScope))
            throw new NotSupportedException("The host cannot satisfy the requested segment timing capabilities.");
        // Freeze caller-owned plans before any await; resolve secrets before reserving/starting a segment.
        JsonElement?[] secrets;
        try { secrets = segment.Inputs.Select(input => input.Sensitive
            ? secretResolver?.Invoke(input.SecretKey!)?.Clone() : null).ToArray(); }
        catch { throw new InvalidOperationException("Segment secret resolution failed."); }
        if (segment.Inputs.Where((input, i) => input.Sensitive && secrets[i] is null).Any())
            throw new InvalidOperationException("A segment secret could not be resolved.");
        cancellationToken.ThrowIfCancellationRequested();
        realtime ??= new StopwatchRealtime();
        var results = segment.Inputs.Select((input, index) => new GuaTimedInputResult(
            index, input.OffsetMilliseconds, null, null, null, null, null, null)).ToArray();
        host.Begin(segment);
        var origin = realtime.Milliseconds;
        var simulationOrigin = 0.0;
        var lastScheduleTime = 0.0;
        var outcome = GuaSegmentOutcome.Succeeded;
        string? failure = null;
        bool cleanupSucceeded = false, neutral = false;
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
            { outcome = GuaSegmentOutcome.Failed; failure = health; }
            var complete = true;
            for (var i = 0; i < results.Length; i++)
            {
                var result = results[i];
                if (result.RequestId is not { } id)
                {
                    if (result.SentMilliseconds is not null) complete = false; // accepted request/reply unknown
                    continue;
                }
                if (result.ResultReceivedMilliseconds is not null) continue;
                var receipt = host.Poll(id);
                if (receipt is null) { complete = false; continue; }
                results[i] = result with { ResultReceivedMilliseconds = Elapsed(), Succeeded = receipt.Succeeded,
                    ErrorCode = receipt.ErrorCode, HostAppliedMilliseconds = receipt.HostAppliedMilliseconds is { } stamp &&
                        !double.IsNaN(stamp) && !double.IsInfinity(stamp) && stamp >= 0 ? stamp : null };
                if (!receipt.Succeeded && outcome == GuaSegmentOutcome.Succeeded)
                { outcome = GuaSegmentOutcome.Failed; failure = "host-rejected-or-failed"; }
                if ((segment.RequireApplicationTimes || segment.RequireSameTickApplication) && receipt.HostAppliedMilliseconds is null)
                { outcome = GuaSegmentOutcome.Failed; failure = "application-time-unconfirmed"; }
                if (receipt.HostAppliedMilliseconds is { } applied &&
                    (double.IsNaN(applied) || double.IsInfinity(applied) || applied < result.ScheduledMilliseconds ||
                    applied - result.ScheduledMilliseconds > segment.MaxLatenessMilliseconds))
                { outcome = GuaSegmentOutcome.Late; failure = "application-time-violation"; }
            }
            return complete;
        }
        try
        {
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
                PollAll();
            }
            while (outcome == GuaSegmentOutcome.Succeeded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Elapsed() >= segment.ExecutionTimeoutMilliseconds)
                { outcome = GuaSegmentOutcome.TimedOut; failure = "completion-or-boundary-timeout"; break; }
                var completed = PollAll();
                if (completed && ScheduleElapsed() >= segment.DurationMilliseconds) break;
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
            var cleanupOrigin = realtime.Milliseconds;
            try
            {
                var cleanupId = host.ReleaseAll();
                while (realtime.Milliseconds - cleanupOrigin < segment.CleanupTimeoutMilliseconds)
                {
                    var completed = PollAll();
                    var cleanup = cleanupSucceeded ? null : host.Poll(cleanupId);
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
                        while (realtime.Milliseconds - cleanupOrigin < segment.CleanupTimeoutMilliseconds)
                        {
                            var final = host.Poll(cleanupId);
                            if (final is not null) { cleanupSucceeded = final.Succeeded; neutral = cleanupSucceeded && host.IsNeutral; break; }
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
        var applicationConfirmed = applied.All(time => time is not null) && results.All(result =>
            result.HostAppliedMilliseconds >= result.ScheduledMilliseconds &&
            result.HostAppliedMilliseconds - result.ScheduledMilliseconds <= segment.MaxLatenessMilliseconds);
        if (applicationConfirmed)
        {
            for (var i = 1; i < applied.Length; i++)
                if (applied[i] < applied[i - 1] || segment.RequireSameTickApplication &&
                    results[i].ScheduledMilliseconds == results[i - 1].ScheduledMilliseconds && applied[i] != applied[i - 1])
                { outcome = GuaSegmentOutcome.Failed; failure = "application-order-or-tick-violation"; applicationConfirmed = false; }
        }
        return new(outcome, results, cleanupSucceeded, neutral, failure)
        {
            Clock = segment.Clock, SimulationScope = segment.Clock == GuaSegmentClock.Simulation ? host.SimulationScope : null,
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
