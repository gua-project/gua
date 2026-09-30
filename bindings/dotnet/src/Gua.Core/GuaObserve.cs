using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Gua.Core;

public enum GuaObserveSource { Ui = 1, Object = 2, World = 3 }
public sealed class GuaObserveException : InvalidOperationException
{
    public int Code { get; }
    internal GuaObserveException(int code) : base($"Observe error {code}") { Code = code; }
}
internal sealed class ObserveResultHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ObserveResultHandle(nint value) : base(true) { SetHandle(value); }
    protected override bool ReleaseHandle() { Native.gua_observe_result_destroy(handle); return true; }
}
public sealed partial class GuaContext
{
    // Serializes the new handles against Dispose/reset. Getters run on the
    // caller's host thread; no callback crosses the ABI or runs under its lock.
    internal readonly object ObserveGate = new();
    private readonly List<WeakReference<GuaObserveRegistration>> _observeGetters = [];
    internal static void CheckObserve(int status) { if (status != 0) throw new GuaObserveException(status); }
    internal bool ObserveDisposed => _handle == 0;
    internal nint ObserveHandle { get { ThrowIfDisposed(); return _handle; } }
    internal static string ReadObserveResult(nint result)
    {
        using var owned = new ObserveResultHandle(result);
        return GuaValue.Copy(result, Native.gua_observe_result_copy_json);
    }
    public GuaObserveOwner CreateObserveOwner(GuaObserveSource source, string runtimeId = "")
    {
        lock (ObserveGate) {
            using var input = new ValueInputs();
            CheckObserve(Native.gua_observe_create_owner(ObserveHandle, (int)source, input.Text(runtimeId), out var id));
            return new GuaObserveOwner(this, id, source);
        }
    }
    public string GetObserveSnapshotJson(GuaObservationProfile profile = GuaObservationProfile.Debug)
    {
        lock (ObserveGate) { CheckObserve(Native.gua_observe_snapshot(ObserveHandle, (int)profile, out var result)); return ReadObserveResult(result); }
    }
    public GuaObserveSubscription SubscribeObservations(GuaObservationProfile profile = GuaObservationProfile.Debug)
    {
        lock (ObserveGate) {
            CheckObserve(Native.gua_observe_subscribe(ObserveHandle, (int)profile, out var id, out var result));
            try { return new GuaObserveSubscription(this, id, ReadObserveResult(result)); }
            catch { Native.gua_observe_unsubscribe(_handle, id); throw; }
        }
    }
    public void SetObserveHistoryLimits(uint events = 1024, ulong bytes = 8 * 1024 * 1024)
    {
        lock (ObserveGate) CheckObserve(Native.gua_observe_set_limits(ObserveHandle, events, bytes));
    }
    internal void AddObserveGetter(GuaObserveRegistration registration) => _observeGetters.Add(new(registration));
    internal void RemoveObserveGetter(GuaObserveRegistration registration) =>
        _observeGetters.RemoveAll(w => !w.TryGetTarget(out var r) || ReferenceEquals(r, registration));
    private void SampleObservations(bool ui)
    {
        // A getter may explicitly dispose/register/reset; enumerate a copy.
        foreach (var weak in _observeGetters.ToArray())
            if (weak.TryGetTarget(out var r) && ((r.Source == GuaObserveSource.Ui) == ui)) r.Sample(true);
        _observeGetters.RemoveAll(w => !w.TryGetTarget(out var r) || r.Id == 0);
    }
    internal void ReleaseObserve(ulong id, int kind)
    {
        lock (ObserveGate) {
            if (_handle == 0 || id == 0) return;
            if (kind == 0) Native.gua_observe_destroy_owner(_handle, id);
            else if (kind == 1) Native.gua_observe_unregister(_handle, id);
            else Native.gua_observe_unsubscribe(_handle, id);
        }
    }
}
/// <summary>Explicit lifetime token for an already published node, or the World.
/// Dispose it when replacing a game object without an intervening absent frame.</summary>
public sealed class GuaObserveOwner : IDisposable
{
    private readonly GuaContext _context;
    private ulong _id;
    private readonly GuaObserveSource _source;
    internal GuaObserveOwner(GuaContext context, ulong id, GuaObserveSource source) { _context = context; _id = id; _source = source; }
    public GuaObserveRegistration Property(string name, Func<GuaValue> getter, bool allowPlayer = false, bool sensitive = false)
    {
        if (_source != GuaObserveSource.World) throw new GuaObserveException(1);
        return Observe(name, getter, allowPlayer, sensitive);
    }
    /// <summary>The getter returns a fresh owned Value; Gua disposes it after copying.
    /// Exceptions become safe unavailable reasons. No arbitrary exception text is retained.</summary>
    public GuaObserveRegistration Observe(string name, Func<GuaValue> getter, bool allowPlayer = false, bool sensitive = false)
    {
        if (getter is null) throw new ArgumentNullException(nameof(getter));
        lock (_context.ObserveGate) {
            if (_id == 0) throw new ObjectDisposedException(nameof(GuaObserveOwner));
            using var input = new ValueInputs();
            var d = new Native.ObserveRegistration { StructSize = (uint)Marshal.SizeOf<Native.ObserveRegistration>(), Owner = _id,
                Name = input.Text(name), Player = allowPlayer ? 1 : 0, Sensitive = sensitive ? 1 : 0 };
            GuaContext.CheckObserve(Native.gua_observe_register_v1(_context.ObserveHandle, in d, out var id));
            var r = new GuaObserveRegistration(_context, id, _source, getter);
            _context.AddObserveGetter(r); return r;
        }
    }
    public void Dispose() { lock (_context.ObserveGate) { _context.ReleaseObserve(_id, 0); _id = 0; } }
}
public sealed class GuaObserveRegistration : IDisposable
{
    private readonly GuaContext _context;
    private Func<GuaValue>? _getter;
    internal ulong Id { get; private set; }
    internal GuaObserveSource Source { get; }
    internal GuaObserveRegistration(GuaContext context, ulong id, GuaObserveSource source, Func<GuaValue> getter)
    { _context = context; Id = id; Source = source; _getter = getter; }
    public void Notify()
    {
        lock (_context.ObserveGate) { if (Id == 0) throw new ObjectDisposedException(nameof(GuaObserveRegistration)); Sample(false); }
    }
    private void Invalidate() { Id = 0; _getter = null; _context.RemoveObserveGetter(this); }
    internal void Sample(bool stage)
    {
        if (Id == 0 || _getter is null) return;
        if (_context.ObserveDisposed) { Invalidate(); if (!stage) GuaContext.CheckObserve(2); return; }
        int alive = Native.gua_observe_registration_alive(_context.ObserveHandle, Id);
        if (alive == 2) { Invalidate(); if (!stage) GuaContext.CheckObserve(alive); return; }
        GuaContext.CheckObserve(alive);
        GuaValue? value = null; int error = 0;
        try { value = _getter(); if (value is null) error = (int)GuaValueErrorCode.Structure; }
        catch (GuaValueException ex) { error = (int)ex.Code; }
        catch { error = 100; }
        using (value) {
            if (Id == 0) return;
            if (_context.ObserveDisposed) { Invalidate(); return; }
            using var input = new ValueInputs();
            int status = Native.gua_observe_publish(_context.ObserveHandle, Id, input.Use(value?.Handle), error, stage ? 1 : 0);
            if (status == 2) { Invalidate(); return; }
            GuaContext.CheckObserve(status);
        }
    }
    public void Dispose() { lock (_context.ObserveGate) { _context.ReleaseObserve(Id, 1); Invalidate(); } }
}
public sealed class GuaObserveSubscription : IDisposable
{
    private readonly GuaContext _context;
    private ulong _id;
    public string SnapshotJson { get; }
    internal GuaObserveSubscription(GuaContext context, ulong id, string snapshot) { _context = context; _id = id; SnapshotJson = snapshot; }
    /// <summary>Returns changes, a sticky gap, or stale_session. Resubscribe after a gap;
    /// the new Snapshot does not restore any missed intermediate history.</summary>
    public string PollJson()
    {
        lock (_context.ObserveGate) {
            if (_id == 0) throw new ObjectDisposedException(nameof(GuaObserveSubscription));
            GuaContext.CheckObserve(Native.gua_observe_poll(_context.ObserveHandle, _id, out var result));
            return GuaContext.ReadObserveResult(result);
        }
    }
    public void Dispose() { lock (_context.ObserveGate) { _context.ReleaseObserve(_id, 2); _id = 0; } }
}
internal static partial class Native
{
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_registration_alive(nint c, ulong id);
    [StructLayout(LayoutKind.Sequential)]
    internal struct ObserveRegistration { public uint StructSize; public ulong Owner; public ValueText Name; public int Player, Sensitive; }
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_create_owner(nint c, int source, ValueText id, out ulong owner);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_destroy_owner(nint c, ulong owner);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_register_v1(nint c, in ObserveRegistration d, out ulong registration);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_unregister(nint c, ulong id);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_publish(nint c, ulong id, nint value, int error, int stage);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_set_limits(nint c, uint events, ulong bytes);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_snapshot(nint c, int profile, out nint result);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_subscribe(nint c, int profile, out ulong subscription, out nint result);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_poll(nint c, ulong id, out nint result);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_unsubscribe(nint c, ulong id);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_observe_result_copy_json(nint result, byte[]? buffer, int capacity);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_observe_result_destroy(nint result);
}
