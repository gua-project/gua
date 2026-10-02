using System.Runtime.InteropServices;
using Gua.Core;
namespace Gua.Runtime;

public sealed partial class GuaRuntime
{
    /// <summary>Explicit trusted Testing/Debug opt-in at this runtime's current
    /// epoch. Shares the engine scheduler; clients cannot provide grants. The
    /// runtime retains the native scheduler until disabled or disposed.</summary>
    public void BindSpatial(GuaSpatialHost host, ulong provider, GuaSpatialOwnerGrants grants)
    {
        lock (Observations.ObserveGate)
        {
            using var document = GuaSpatialDocument.FromOwner(grants);
            using var refs = new SpatialReferences();
            CheckSpatial(Native.gua_runtime_bind_spatial(Handle, host.Handle, provider, document.Use(refs)));
        }
    }
    public void DisableSpatial()
    {
        lock (Observations.ObserveGate) CheckSpatial(Native.gua_runtime_disable_spatial(Handle, 0, 0, 0));
    }
    public GuaRuntimeSpatialClient CreateSpatialClient()
    {
        lock (Observations.ObserveGate)
        {
            var id = Native.gua_runtime_create_spatial_client(Handle);
            if (id == 0) throw new InvalidOperationException("Spatial client unsupported or unavailable.");
            return new(this, Observations, id);
        }
    }
    internal static void CheckSpatial(int status)
    {
        if (status != 0) throw new GuaSpatialException(new Gua.Core.Native.SpatialError { Code = status });
    }
}
/// <summary>Connection owner with one-shot results. Dispose at disconnect.</summary>
public sealed class GuaRuntimeSpatialClient : IDisposable
{
    readonly GuaRuntime runtime;
    readonly GuaContext observations;
    ulong id;
    internal GuaRuntimeSpatialClient(GuaRuntime runtime, GuaContext observations, ulong id)
    { this.runtime = runtime; this.observations = observations; this.id = id; }
    public GuaSpatialDocument Describe() => Command(1)!;
    public void Enqueue(GuaSpatialBatch batch) { using var input = GuaSpatialDocument.FromBatch(batch); Command(2, input)?.Dispose(); }
    public GuaSpatialDocument? Poll(ulong batchId) => Command(3, batchId: batchId);
    public void Cancel(ulong batchId) => Command(4, batchId: batchId)?.Dispose();
    GuaSpatialDocument? Command(int operation, GuaSpatialDocument? batch = null, ulong batchId = 0)
    {
        lock (observations.ObserveGate)
        {
            if (id == 0) throw new ObjectDisposedException(nameof(GuaRuntimeSpatialClient));
            using var refs = new SpatialReferences();
            int status = Native.gua_runtime_spatial_command(runtime.Handle, id, operation, batch?.Use(refs) ?? 0, batchId, out var result);
            if (status == (int)GuaSpatialErrorCode.NotReady) return null;
            GuaRuntime.CheckSpatial(status);
            return result == 0 ? null : GuaSpatialDocument.Owned(result);
        }
    }
    public void Dispose()
    {
        lock (observations.ObserveGate)
        {
            if (id == 0) return;
            if (!observations.ObserveDisposed) Native.gua_runtime_release_spatial_client(runtime.Handle, id);
            id = 0;
        }
    }
}
internal static partial class Native
{
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_bind_spatial(nint runtime, SpatialHostHandle host, ulong provider, nint grants);
    [DllImport(Library, EntryPoint = "gua_runtime_bind_spatial", CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_disable_spatial(nint runtime, nint host, ulong provider, nint grants);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong gua_runtime_create_spatial_client(nint runtime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_runtime_release_spatial_client(nint runtime, ulong client);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_runtime_spatial_command(nint runtime, ulong client, int operation, nint batch, ulong batchId, out nint result);
}
