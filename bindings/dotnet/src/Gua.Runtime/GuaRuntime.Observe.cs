using System.Runtime.InteropServices;
using Gua.Core;

namespace Gua.Runtime;

public sealed partial class GuaRuntime
{
    private GuaContext? _observations;
    private GuaContext Observations { get { ThrowIfDisposed(); return _observations ??= new GuaContext(Native.gua_runtime_borrow_context(_handle)); } }
    public GuaObserveOwner CreateObserveOwner(GuaObserveSource source, string runtimeId = "") => Observations.CreateObserveOwner(source, runtimeId);
    public string GetObserveSnapshotJson(GuaObservationProfile profile = GuaObservationProfile.Debug) => Observations.GetObserveSnapshotJson(profile);
    public string GetObserveSnapshotTransportJson(GuaObservationProfile profile = GuaObservationProfile.Debug) => Observations.GetObserveSnapshotTransportJson(profile);
    public GuaObserveSubscription SubscribeObservations(GuaObservationProfile profile = GuaObservationProfile.Debug) => Observations.SubscribeObservations(profile);
    public void SetObserveHistoryLimits(uint events = 1024, ulong bytes = 8 * 1024 * 1024) => Observations.SetObserveHistoryLimits(events, bytes);
    public GuaRuntimeObserveClient CreateObserveClient(GuaObservationProfile profile = GuaObservationProfile.Debug)
    {
        var observations = Observations;
        lock (observations.ObserveGate) {
            var id = Native.gua_runtime_create_observe_client(Handle, (int)profile);
            if (id == 0) throw new InvalidOperationException("Observe client creation failed.");
            return new GuaRuntimeObserveClient(this, observations, id);
        }
    }
}
/// <summary>Transport-owned, fixed-profile subscriptions. Dispose on disconnect.</summary>
public sealed class GuaRuntimeObserveClient : IDisposable
{
    private readonly GuaRuntime runtime;
    private readonly GuaContext observations;
    private ulong id;
    internal GuaRuntimeObserveClient(GuaRuntime runtime, GuaContext observations, ulong id) { this.runtime = runtime; this.observations = observations; this.id = id; }
    public string CommandJson(int command, ulong subscriptionId = 0)
    {
        lock (observations.ObserveGate) {
            if (id == 0) throw new ObjectDisposedException(nameof(GuaRuntimeObserveClient));
            GuaContext.CheckObserve(Native.gua_runtime_observe_command(runtime.Handle, id, command, subscriptionId, out var subscription, out var result));
            using var owned = new ObserveResultHandle(result);
            if (command == 4) return "null";
            string json = GuaValue.Copy(result, Gua.Core.Native.gua_observe_result_copy_transport_json);
            return command == 2 ? "{\"subscriptionId\":" + subscription + ",\"snapshot\":" + json + "}" : json;
        }
    }
    public void Dispose()
    {
        lock (observations.ObserveGate) {
            if (id == 0) return;
            if (!observations.ObserveDisposed) Native.gua_runtime_release_observe_client(runtime.Handle, id);
            id = 0;
        }
    }
}
internal static partial class Native
{
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gua_runtime_borrow_context(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gua_runtime_create_observe_client(nint runtime, int profile);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_release_observe_client(nint runtime, ulong client);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_observe_command(nint runtime, ulong client, int command, ulong subscription, out ulong id, out nint result);
}
