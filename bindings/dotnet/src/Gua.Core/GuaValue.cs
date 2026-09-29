using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Gua.Core;

public enum GuaValueType { Bool = 1, Integer, Number, String, Enum, List, Set }
public enum GuaValueErrorCode { Ok, Structure, ForbiddenType, Range, NonFinite, Unicode, EnumUnknown, EnumConflict, EnumMember, ElementType, Duplicate, Internal }
public sealed class GuaValueException : ArgumentException
{
    public GuaValueErrorCode Code { get; }
    public string Path { get; }
    internal GuaValueException(GuaValueErrorCode code, string path) : base($"Value error {code} at {path}") { Code = code; Path = path; }
}

internal sealed class ValueHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly bool _catalog;
    internal ValueHandle(nint value, bool catalog = false) : base(true) { SetHandle(value); _catalog = catalog; }
    protected override bool ReleaseHandle() { if (_catalog) Native.gua_enum_catalog_destroy(handle); else Native.gua_value_destroy(handle); return true; }
}
// Pins temporary input storage. Native constructors copy it before returning.
internal sealed class ValueInputs : IDisposable
{
    private readonly List<GCHandle> _pins = [];
    private readonly List<ValueHandle> _references = [];
    internal Native.ValueText Text(string value, string path = "$")
    {
        if (value is null) throw new GuaValueException(GuaValueErrorCode.Structure, path);
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(value); }
        catch (EncoderFallbackException) { throw new GuaValueException(GuaValueErrorCode.Unicode, path); }
        return new Native.ValueText { Data = Pin(bytes), Size = checked((uint)bytes.Length) };
    }
    internal nint Pin(Array array) { var pin = GCHandle.Alloc(array, GCHandleType.Pinned); _pins.Add(pin); return pin.AddrOfPinnedObject(); }
    internal nint Use(ValueHandle? handle)
    {
        if (handle is null) return 0;
        bool added = false;
        try { handle.DangerousAddRef(ref added); }
        catch { if (added) handle.DangerousRelease(); throw; }
        _references.Add(handle); return handle.DangerousGetHandle();
    }
    public void Dispose() { foreach (var reference in _references) reference.DangerousRelease(); foreach (var pin in _pins) pin.Free(); }
}

/// <summary>Independent enum definitions for Value v1. Synchronize registrations with reads.</summary>
public sealed class GuaEnumCatalog : IDisposable
{
    internal ValueHandle Handle { get; }
    public GuaEnumCatalog() { var p = Native.gua_enum_catalog_create(); if (p == 0) throw new OutOfMemoryException(); Handle = new ValueHandle(p, true); }
    private GuaEnumCatalog(nint p) { Handle = new ValueHandle(p, true); }
    public static GuaEnumCatalog FromJson(string json)
    {
        using var inputs = new ValueInputs();
        GuaValue.Check(Native.gua_enum_catalog_from_json(inputs.Text(json), out var p, out var error), error);
        return new GuaEnumCatalog(p);
    }
    public void Register(string enumType, params string[] members)
    {
        if (members is null) throw new GuaValueException(GuaValueErrorCode.Structure, "$.members");
        using var inputs = new ValueInputs();
        var values = members.Select((m, i) => inputs.Text(m, $"$.members[{i}]")).ToArray();
        GuaValue.Check(Native.gua_enum_catalog_register(inputs.Use(Handle), inputs.Text(enumType, "$.enumType"), inputs.Pin(values), checked((uint)values.Length), out var error), error);
    }
    public string ToJson() { using var inputs = new ValueInputs(); return GuaValue.Copy(inputs.Use(Handle), Native.gua_enum_catalog_copy_json); }
    public IReadOnlyList<string> GetMembers(string enumType)
    {
        using var inputs = new ValueInputs();
        GuaValue.Check(Native.gua_enum_catalog_validate_type(inputs.Use(Handle), inputs.Text(enumType, "$.enumType"), out var error), error);
        using var doc = JsonDocument.Parse(ToJson());
        foreach (var entry in doc.RootElement.GetProperty("enums").EnumerateArray())
            if (entry.GetProperty("enumType").GetString() == enumType)
                return Array.AsReadOnly(entry.GetProperty("members").EnumerateArray().Select(m => m.GetString()!).ToArray());
        throw new GuaValueException(GuaValueErrorCode.EnumUnknown, "$.enumType");
    }
    public void Dispose() => Handle.Dispose();
}

/// <summary>Immutable native-owned Value v1. Dispose after use. Equality includes all type metadata.</summary>
public sealed class GuaValue : IDisposable
{
    internal ValueHandle Handle { get; }
    private GuaValue(nint p) { Handle = new ValueHandle(p); }
    internal static void Check(int status, Native.ValueError error)
    {
        if (status != 0) throw new GuaValueException((GuaValueErrorCode)status, error.Path ?? "$");
    }
    internal static string Copy(nint p, Func<nint, byte[]?, int, int> copy)
    {
        int size = copy(p, null, 0); if (size <= 0) throw new InvalidOperationException("Value JSON copy failed");
        var bytes = new byte[size]; if (copy(p, bytes, size) != size) throw new InvalidOperationException("Value JSON copy failed");
        return new UTF8Encoding(false, true).GetString(bytes, 0, size - 1);
    }
    public static GuaValue FromJson(string json, GuaEnumCatalog? catalog = null)
    {
        using var inputs = new ValueInputs();
        Check(Native.gua_value_from_json(inputs.Text(json), inputs.Use(catalog?.Handle), out var p, out var error), error);
        return new GuaValue(p);
    }
    private static GuaValue Create(Native.ValueDescriptor descriptor, ValueInputs inputs, GuaEnumCatalog? catalog = null)
    {
        descriptor.StructSize = checked((uint)Marshal.SizeOf<Native.ValueDescriptor>());
        Check(Native.gua_value_create(in descriptor, inputs.Use(catalog?.Handle), out var p, out var error), error);
        return new GuaValue(p);
    }
    public static GuaValue Bool(bool value) { using var inputs = new ValueInputs(); return Create(new() { Type = (int)GuaValueType.Bool, Boolean = value ? 1 : 0 }, inputs); }
    public static GuaValue Integer(long value) { using var inputs = new ValueInputs(); return Create(new() { Type = (int)GuaValueType.Integer, Integer = value }, inputs); }
    public static GuaValue Number(double value) { using var inputs = new ValueInputs(); return Create(new() { Type = (int)GuaValueType.Number, Number = value }, inputs); }
    public static GuaValue String(string value) { using var inputs = new ValueInputs(); return Create(new() { Type = (int)GuaValueType.String, Text = inputs.Text(value, "$.value") }, inputs); }
    public static GuaValue Enum(string enumType, string member, GuaEnumCatalog catalog) { using var inputs = new ValueInputs(); return Create(new() { Type = (int)GuaValueType.Enum, Text = inputs.Text(member, "$.value"), EnumType = inputs.Text(enumType, "$.enumType") }, inputs, catalog); }
    public static GuaValue Collection(GuaValueType type, GuaValueType elementType, IReadOnlyList<GuaValue> items, string? enumType = null, GuaEnumCatalog? catalog = null)
    {
        if (type != GuaValueType.List && type != GuaValueType.Set) throw new GuaValueException(GuaValueErrorCode.Structure, "$.type");
        if (items is null) throw new GuaValueException(GuaValueErrorCode.Structure, "$.value");
        using var inputs = new ValueInputs();
        var pointers = items.Select(v => v is null ? 0 : inputs.Use(v.Handle)).ToArray();
        return Create(new() { Type = (int)type, ElementType = (int)elementType, Items = inputs.Pin(pointers), ItemCount = checked((uint)pointers.Length), EnumType = enumType is null ? default : inputs.Text(enumType, "$.enumType") }, inputs, catalog);
    }
    public GuaValueType Type { get { using var inputs = new ValueInputs(); return (GuaValueType)Native.gua_value_get_type(inputs.Use(Handle)); } }
    public GuaValueType? ElementType { get { using var inputs = new ValueInputs(); int type = Native.gua_value_get_element_type(inputs.Use(Handle)); return type == 0 ? null : (GuaValueType)type; } }
    public string ToJson() { using var inputs = new ValueInputs(); return Copy(inputs.Use(Handle), Native.gua_value_copy_json); }
    public bool ValueEquals(GuaValue other)
    {
        if (other is null) throw new GuaValueException(GuaValueErrorCode.Structure, "$");
        using var inputs = new ValueInputs();
        Check(Native.gua_value_equals(inputs.Use(Handle), inputs.Use(other.Handle), out var equal, out var error), error);
        return equal != 0;
    }
    public void Dispose() => Handle.Dispose();
}

internal static partial class Native
{
#if GUA_STATIC_LINK
    private const string ValueLibrary = "__Internal";
#else
    private const string ValueLibrary = "gua";
#endif
    [StructLayout(LayoutKind.Sequential)] internal struct ValueText { internal nint Data; internal uint Size; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] internal struct ValueError { internal int Code; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Path; }
    [StructLayout(LayoutKind.Sequential)] internal struct ValueDescriptor
    {
        internal uint StructSize; internal int Type, ElementType, Boolean;
        internal long Integer; internal double Number;
        internal ValueText Text, EnumType;
        internal nint Items; internal uint ItemCount;
    }
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern nint gua_enum_catalog_create();
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_enum_catalog_destroy(nint p);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_enum_catalog_register(nint p, ValueText id, nint members, uint count, out ValueError error);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_enum_catalog_validate_type(nint p, ValueText id, out ValueError error);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_enum_catalog_from_json(ValueText json, out nint result, out ValueError error);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_enum_catalog_copy_json(nint p, [Out] byte[]? buffer, int capacity);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_value_from_json(ValueText json, nint catalog, out nint result, out ValueError error);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_value_create(in ValueDescriptor descriptor, nint catalog, out nint result, out ValueError error);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_value_destroy(nint p);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_value_get_type(nint p);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_value_get_element_type(nint p);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_value_copy_json(nint p, [Out] byte[]? buffer, int capacity);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_value_equals(nint a, nint b, out int equal, out ValueError error);
}
