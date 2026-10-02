using System.Diagnostics;
using System.Text.Json;
using Gua.Core;
namespace Gua.Testing;

public sealed partial class GuaWebSocketContext
{
    public GuaSpatialAdvertisement GetSpatialInfo()
    {
        using var document = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Advertisement, Raw(new { type = "get_spatial_info" }));
        return document.ReadAdvertisement();
    }
    /// <summary>One bounded read batch, pinned to its accepted connection. Never
    /// retries after reconnect. Timeout/cancellation closes the owning connection;
    /// neither implies the physics query did not run. No Recording input is added.</summary>
    public GuaSpatialBatchResult QuerySpatialBatch(GuaSpatialBatch batch, TimeSpan timeout,
        CancellationToken cancellationToken = default, GuaTraceSession? trace = null, string sourceId = "spatial")
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var input = GuaSpatialDocument.FromBatch(batch);
        using var immutable = JsonDocument.Parse(input.ToJson());
        var submitted = input.ReadBatch();
        long generation = 0;
        string? step = null;
        var clock = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var advertisement = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Advertisement,
                Raw(new { type = "get_spatial_info" }, responseTimeout: timeout, onConnection: value => generation = value));
            var preflightRemaining = timeout - clock.Elapsed;
            if (preflightRemaining <= TimeSpan.Zero) throw new TimeoutException("Spatial batch preflight timed out.");
            Raw(new { type = "query_spatial_batch", batch = immutable.RootElement },
                responseTimeout: preflightRemaining, observeGeneration: generation);
            if (trace?.ObservationProfile == "debug")
            {
                try
                {
                    step = trace.BeginStep(GuaTraceStepKind.Lifecycle, "spatial batch",
                        new(sourceId, submitted.Queries[0].SessionEpoch.ToString(), submitted.BatchId.ToString()));
                    trace.Record(step, "spatial.accepted", GuaTraceJson.Element(new { batchId = submitted.BatchId,
                        requestIds = submitted.Queries.Select(q => q.RequestId), queryCount = submitted.Queries.Length,
                        hostCompletion = "unconfirmed", live = true }));
                }
                catch { }
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Spatial batch completion timed out.");
                var wire = Raw(new { type = "poll_spatial_batch", batchId = submitted.BatchId },
                    responseTimeout: remaining, observeGeneration: generation);
                if (wire != "null")
                {
                    using var result = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.BatchResult, wire);
                    var typed = result.ReadBatchResult();
                    if (typed.BatchId != submitted.BatchId || typed.Items.Length != submitted.Queries.Length ||
                        typed.Items.Where((item, index) => item.RequestId != submitted.Queries[index].RequestId || item.QueryId != submitted.Queries[index].QueryId).Any())
                        throw new JsonException("Spatial correlation mismatch.");
                    if (step is not null) GuaTraceCapture.SpatialBatch(trace!, step, result, advertisement);
                    return typed;
                }
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(10, Math.Max(0, (timeout - clock.Elapsed).TotalMilliseconds))));
            }
        }
        catch
        {
            if (step is not null)
            {
                try { trace!.Record(step, "spatial.unconfirmed", GuaTraceJson.Element(new { reason = "caller_interrupted", live = false })); trace.EndStep(step, GuaTraceOutcome.Interrupted); } catch { }
            }
            if (generation != 0)
            {
                requestGate.Wait();
                try { if (connectionGeneration == generation) { socket?.Dispose(); socket = null; bufferedActionEvents.Clear(); } }
                finally { requestGate.Release(); }
            }
            throw;
        }
    }
}
