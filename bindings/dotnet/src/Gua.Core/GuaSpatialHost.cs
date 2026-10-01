using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Gua.Core;

/// <summary>Finite host scheduling limits. Queue depth includes retained results.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GuaSpatialHostOptions
{
    internal uint StructSize;
    public uint MaxProviders, MaxOwners, MaxQueueDepth, MaxQueriesPerBatch, MaxHitsPerQuery;
    public double QueryDeadlineMs, MaxBatchWorkMs;
}
internal sealed class SpatialHostHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SpatialHostHandle(nint p) : base(true) => SetHandle(p);
    protected override bool ReleaseHandle() { Native.gua_spatial_host_destroy(handle); return true; }
}
/// <summary>
/// Trusted host-only registration and physics-boundary polling over the native C ABI.
/// Owner grants must come from host session authorization, never client input.
/// This class neither owns an engine nor advertises a transport capability.
/// </summary>
public sealed class GuaSpatialHost : IDisposable
{
    private readonly SpatialHostHandle _handle;
    public GuaSpatialHost(GuaSpatialHostOptions options, string clockId)
    {
        options.StructSize = (uint)Marshal.SizeOf<GuaSpatialHostOptions>();
        using var input = new ValueInputs();
        Check(Native.gua_spatial_host_create(in options, input.Text(clockId), out var p, out var e), e);
        _handle = new(p);
    }
    private static void Check(int status, Native.SpatialError e) { if (status != 0) throw new GuaSpatialException(e); }
    public ulong Register(GuaSpatialDocument registration)
    {
        using var refs = new SpatialReferences();
        Check(Native.gua_spatial_host_register(_handle, registration.Use(refs), out var p, out var e), e); return p;
    }
    public void Unregister(ulong provider) => Check(Native.gua_spatial_host_unregister(_handle, provider, out var e), e);
    public ulong OpenOwner(GuaSpatialDocument grants)
    {
        using var refs = new SpatialReferences();
        Check(Native.gua_spatial_host_open_owner(_handle, grants.Use(refs), out var o, out var e), e); return o;
    }
    public void SetOwner(ulong owner, GuaSpatialDocument grants)
    {
        using var refs = new SpatialReferences();
        Check(Native.gua_spatial_host_set_owner(_handle, owner, grants.Use(refs), out var e), e);
    }
    public void CloseOwner(ulong owner) => Check(Native.gua_spatial_host_close_owner(_handle, owner, out var e), e);
    public void Enqueue(ulong owner, GuaSpatialDocument batch)
    {
        using var refs = new SpatialReferences();
        Check(Native.gua_spatial_host_enqueue(_handle, owner, batch.Use(refs), out var e), e);
    }
    public void Cancel(ulong owner, ulong batchId) => Check(Native.gua_spatial_host_cancel(_handle, owner, batchId, out var e), e);
    public GuaSpatialDocument Describe(ulong owner, ulong provider)
    {
        Check(Native.gua_spatial_host_describe(_handle, owner, provider, out var p, out var e), e); return GuaSpatialDocument.Owned(p);
    }
    public GuaSpatialDocument? Poll(ulong owner, ulong batchId)
    {
        int status = Native.gua_spatial_host_poll(_handle, owner, batchId, out var p, out var e);
        if (status == (int)GuaSpatialErrorCode.NotReady) return null;
        Check(status, e); return GuaSpatialDocument.Owned(p);
    }
    /// <summary>Call only inside a safe physics read interval; hold it until End.
    /// No blocking wait, callback or native mutex spans engine work.</summary>
    public ulong? Begin(ulong provider, GuaSpatialDocument boundary)
    {
        using var refs = new SpatialReferences();
        int status = Native.gua_spatial_host_begin(_handle, provider, boundary.Use(refs), out var lease, out var e);
        if (status == (int)GuaSpatialErrorCode.NotReady) return null;
        Check(status, e); return lease;
    }
    public GuaSpatialDocument? Take(ulong lease)
    {
        int status = Native.gua_spatial_host_take(_handle, lease, out var p, out var e);
        if (status == (int)GuaSpatialErrorCode.NotReady) return null;
        Check(status, e); return GuaSpatialDocument.Owned(p);
    }
    public void Complete(ulong lease, GuaSpatialDocument execution)
    {
        using var refs = new SpatialReferences();
        Check(Native.gua_spatial_host_complete(_handle, lease, execution.Use(refs), out var e), e);
    }
    public void End(ulong lease) => Check(Native.gua_spatial_host_end(_handle, lease, out var e), e);
    public void Dispose() => _handle.Dispose();
}
internal static partial class Native
{
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_create(in GuaSpatialHostOptions options, ValueText clock, out nint host, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_spatial_host_destroy(nint host);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_register(SpatialHostHandle host, nint doc, out ulong provider, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_unregister(SpatialHostHandle host, ulong provider, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_open_owner(SpatialHostHandle host, nint doc, out ulong owner, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_set_owner(SpatialHostHandle host, ulong owner, nint doc, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_close_owner(SpatialHostHandle host, ulong owner, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_enqueue(SpatialHostHandle host, ulong owner, nint doc, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_cancel(SpatialHostHandle host, ulong owner, ulong batch, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_describe(SpatialHostHandle host, ulong owner, ulong provider, out nint doc, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_poll(SpatialHostHandle host, ulong owner, ulong batch, out nint doc, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_begin(SpatialHostHandle host, ulong provider, nint doc, out ulong lease, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_take(SpatialHostHandle host, ulong lease, out nint doc, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_complete(SpatialHostHandle host, ulong lease, nint doc, out SpatialError e);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_host_end(SpatialHostHandle host, ulong lease, out SpatialError e);
}
