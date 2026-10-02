using System.Text.Json;
using Gua.Core;
namespace Gua.Testing;
public static partial class GuaTraceCapture
{
    /// <summary>Attaches only an already received native-validated batch result.
    /// One batch is one non-input step. Storage omission is separate from the
    /// original query's truncation/coverage. This never executes or replays reads.</summary>
    public static bool SpatialBatch(GuaTraceSession trace, string stepId, GuaSpatialDocument received,
        GuaSpatialDocument? advertisement = null)
    {
        try
        {
            if (trace.ObservationProfile != "debug") return false;
            var result = received.ReadBatchResult();
            using var document = JsonDocument.Parse(received.ToJson());
            if (advertisement is not null) advertisement.ReadAdvertisement();
            using var descriptor = JsonDocument.Parse(advertisement?.ToJson() ?? "null");
            var attachment = GuaTraceJson.Element(new {
                result = document.RootElement, advertisement = descriptor.RootElement,
                evidence = "received-live-result", savedRequery = false });
            trace.Record(stepId, "spatial.received", GuaTraceJson.Element(new { result.BatchId,
                live = true, savedRequery = false, storageStatus = "unconfirmed",
                queryTruncated = result.Items.Any(item => item.Result?.Truncated == true),
                states = result.Items.Select(item => new { item.RequestId, item.QueryId, item.State, item.Reason }) }));
            bool stored = trace.Attach(stepId, "gua.spatial.batch.r1", attachment);
            if (stored) trace.Record(stepId, "spatial.storage", GuaTraceJson.Element(new { stored, storageOmitted = false }));
            else trace.RecordCaptureFailure(stepId, "gua.spatial.batch.r1", "spatial-storage-omitted");
            trace.EndStep(stepId, result.Items.All(item => item.State == "completed") ? GuaTraceOutcome.Passed : GuaTraceOutcome.Failed);
            return stored;
        }
        catch { return false; }
    }
}
