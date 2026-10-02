using System.Diagnostics;
using System.Text.Json;
using System.Net.WebSockets;
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
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var input = GuaSpatialDocument.FromBatch(batch);
        using var immutable = JsonDocument.Parse(input.ToJson());
        var submitted = input.ReadBatch();
        long generation = 0;
        string? step = null;
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        ClientWebSocket? spatialSocket = null;
        using var abort = deadline.Token.Register(() => { try { Volatile.Read(ref spatialSocket)?.Abort(); } catch (ObjectDisposedException) { } });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var advertisement = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Advertisement,
                SpatialRawAsync(new { type = "get_spatial_info" }, timeout, deadline.Token,
                    onConnection: (value, active) => { generation = value; spatialSocket = active; }).GetAwaiter().GetResult());
            var preflightRemaining = timeout - clock.Elapsed;
            if (preflightRemaining <= TimeSpan.Zero) throw new TimeoutException("Spatial batch preflight timed out.");
            SpatialRawAsync(new { type = "query_spatial_batch", batch = immutable.RootElement },
                preflightRemaining, deadline.Token, generation).GetAwaiter().GetResult();
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
                deadline.Token.ThrowIfCancellationRequested();
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Spatial batch completion timed out.");
                var wire = SpatialRawAsync(new { type = "poll_spatial_batch", batchId = submitted.BatchId },
                    remaining, deadline.Token, generation).GetAwaiter().GetResult();
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
                deadline.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(10, Math.Max(0, (timeout - clock.Elapsed).TotalMilliseconds))));
            }
        }
        catch (Exception error)
        {
            if (step is not null)
            {
                try { trace!.Record(step, "spatial.unconfirmed", GuaTraceJson.Element(new { reason = "caller_interrupted", live = false })); trace.EndStep(step, GuaTraceOutcome.Interrupted); } catch { }
            }
            try { spatialSocket?.Abort(); } catch (ObjectDisposedException) { }
            // Abort is immediate even if an unrelated request holds the shared gate.
            // Never wait again during cancellation, and never close a replacement socket.
            if (generation != 0 && requestGate.Wait(0))
            {
                try { if (connectionGeneration == generation && ReferenceEquals(socket, spatialSocket)) { socket?.Dispose(); socket = null; bufferedActionEvents.Clear(); } }
                finally { requestGate.Release(); }
            }
            if (deadline.IsCancellationRequested) {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Spatial batch completion timed out.", error);
            }
            throw;
        }
    }
    // Shares framing/connection state with Raw, while keeping spatial gate,
    // connect, send and receive waits within one cancellable caller deadline.
    private async Task<string> SpatialRawAsync(object command, TimeSpan responseTimeout, CancellationToken cancellation,
        long? generation = null, Action<long, ClientWebSocket>? onConnection = null)
    {
        await requestGate.WaitAsync(cancellation).ConfigureAwait(false);
        ClientWebSocket? active = null;
        try
        {
            if (generation.HasValue && (disposed || socket?.State != WebSocketState.Open || generation.Value != connectionGeneration))
                throw new InvalidOperationException("Spatial batch belongs to an inactive connection.");
            await EnsureConnectedAsync(cancellation).ConfigureAwait(false);
            active = socket!; onConnection?.Invoke(connectionGeneration, active);
            cancellation.ThrowIfCancellationRequested();
            var id = nextId++;
            await active.SendAsync(new ArraySegment<byte>(Envelope(id, command)), WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false);
            while (true) {
                using var document = JsonDocument.Parse(await ReceiveAsync(cancellation, responseTimeout).ConfigureAwait(false));
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                if (!root.GetProperty("ok").GetBoolean()) throw new RemoteCommandRejectedException(root.GetProperty("error").GetString());
                return root.GetProperty("result").GetRawText();
            }
        }
        catch {
            if (active is not null && ReferenceEquals(socket, active)) { active.Dispose(); socket = null; bufferedActionEvents.Clear(); }
            throw;
        }
        finally { requestGate.Release(); }
    }
}
