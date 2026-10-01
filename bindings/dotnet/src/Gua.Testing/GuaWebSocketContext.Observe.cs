using System.Text.Json;

namespace Gua.Testing;

public sealed partial class GuaWebSocketContext
{
    public string GetObserveSnapshotJson()
    {
        GetVersion().EnsureCompatible(requiredCapabilities: ["observe_v1"]);
        return Raw(new { type = "get_observe_snapshot" });
    }
    public GuaRemoteObserveSubscription SubscribeObservations()
    {
        GetVersion().EnsureCompatible(requiredCapabilities: ["observe_v1"]);
        long generation = 0;
        try {
            using var response = JsonDocument.Parse(Raw(new { type = "subscribe_observations" }, onConnection: g => generation = g));
            return new GuaRemoteObserveSubscription(this, generation, response.RootElement.GetProperty("subscriptionId").GetUInt64(),
                response.RootElement.GetProperty("snapshot").GetRawText());
        } catch (RemoteCommandRejectedException) { throw; } catch {
            // A lost reply may have created a cursor. Close its owning connection.
            requestGate.Wait();
            try {
                if (connectionGeneration == generation) { socket?.Dispose(); socket = null; bufferedActionEvents.Clear(); }
            }
            finally { requestGate.Release(); }
            throw;
        }
    }
    internal string PollObservations(ulong subscription, long generation)
    {
        try { return Raw(new { type = "poll_observations", subscriptionId = subscription }, observeGeneration: generation); }
        catch (RemoteCommandRejectedException) { throw; }
        catch {
            // Poll may have advanced the cursor. Never retry an unknown result.
            requestGate.Wait();
            try {
                if (connectionGeneration == generation) { socket?.Dispose(); socket = null; bufferedActionEvents.Clear(); }
            } finally { requestGate.Release(); }
            throw;
        }
    }
    internal void UnsubscribeObservations(ulong subscription, long generation)
    {
        try {
            using var response = JsonDocument.Parse(Raw(new { type = "unsubscribe_observations", subscriptionId = subscription }, observeGeneration: generation, ignoreStaleObserve: true));
            if (response.RootElement.ValueKind != JsonValueKind.Null) throw new JsonException();
        }
        catch {
            // The token has no retry path. Disconnect its owner even after a rejection.
            requestGate.Wait();
            try {
                if (connectionGeneration == generation) { socket?.Dispose(); socket = null; bufferedActionEvents.Clear(); }
            } finally { requestGate.Release(); }
            throw;
        }
    }
}
/// <summary>Connection-owned cursor. SnapshotJson/PollJson return the transport
/// envelope; inspect document.status and resubscribe after gap/stale_session.
/// A reconnect does not restore this subscription.</summary>
public sealed class GuaRemoteObserveSubscription : IDisposable
{
    private readonly GuaWebSocketContext _context;
    private readonly long _generation;
    private ulong _id;
    public string SnapshotJson { get; }
    internal GuaRemoteObserveSubscription(GuaWebSocketContext context, long generation, ulong id, string snapshot)
    { _context = context; _generation = generation; _id = id; SnapshotJson = snapshot; }
    public string PollJson() { if (_id == 0) throw new ObjectDisposedException(nameof(GuaRemoteObserveSubscription)); return _context.PollObservations(_id, _generation); }
    public void Dispose() { if (_id == 0) return; var id = _id; _id = 0; _context.UnsubscribeObservations(id, _generation); }
}
