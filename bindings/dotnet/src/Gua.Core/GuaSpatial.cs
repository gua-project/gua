using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
namespace Gua.Core;

public enum GuaSpatialDocumentType { Request = 1, Result = 2, Provider = 3 }
public enum GuaSpatialErrorCode { Invalid = 1, Version = 2, Geometry = 3, Context = 4, Unsupported = 5, Semantics = 6, Internal = 7 }
public sealed class GuaSpatialException : InvalidOperationException
{
    public GuaSpatialErrorCode Code { get; }
    public string Path { get; }
    internal GuaSpatialException(Native.SpatialError error) : base($"Spatial contract error {error.Code} at {error.Path}")
    { Code = (GuaSpatialErrorCode)error.Code; Path = error.Path; }
}
internal sealed class SpatialHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SpatialHandle(nint p) : base(true) { SetHandle(p); }
    protected override bool ReleaseHandle() { Native.gua_spatial_destroy(handle); return true; }
}
internal sealed class SpatialReferences : IDisposable
{
    private readonly List<SpatialHandle> _handles = new();
    internal nint Use(SpatialHandle handle)
    {
        bool added = false;
        try { handle.DangerousAddRef(ref added); }
        catch { if (added) handle.DangerousRelease(); throw; }
        _handles.Add(handle); return handle.DangerousGetHandle();
    }
    public void Dispose() { foreach (var handle in _handles) handle.DangerousRelease(); }
}
/// <summary>Owned immutable offline contract. Parsing does not execute or authorize physics.</summary>
public sealed class GuaSpatialDocument : IDisposable
{
    private readonly SpatialHandle _handle;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private GuaSpatialDocument(nint p) { _handle = new(p); }
    private static void Check(int status, Native.SpatialError error) { if (status != 0) throw new GuaSpatialException(error); }
    public static GuaSpatialDocument FromJson(GuaSpatialDocumentType type, string json)
    {
        using var inputs = new ValueInputs();
        var options = new Native.SpatialOptions { StructSize = (uint)Marshal.SizeOf<Native.SpatialOptions>(), DocumentType = (int)type };
        Check(Native.gua_spatial_from_json(in options, inputs.Text(json), out var p, out var error), error);
        return new(p);
    }
    public static GuaSpatialDocument FromRequest(GuaSpatialRequest request) => FromJson(GuaSpatialDocumentType.Request, JsonSerializer.Serialize(request, JsonOptions));
    public static GuaSpatialDocument FromResult(GuaSpatialResult result) => FromJson(GuaSpatialDocumentType.Result, JsonSerializer.Serialize(result, JsonOptions));
    public static GuaSpatialDocument FromProvider(GuaSpatialProvider provider) => FromJson(GuaSpatialDocumentType.Provider, JsonSerializer.Serialize(provider, JsonOptions));
    public GuaSpatialDocumentType Type { get { using var inputs = new SpatialReferences(); return (GuaSpatialDocumentType)Native.gua_spatial_document_type(inputs.Use(_handle)); } }
    public string ToJson() { using var inputs = new SpatialReferences(); return GuaValue.Copy(inputs.Use(_handle), Native.gua_spatial_copy_json); }
    public GuaSpatialRequest ReadRequest() => Read<GuaSpatialRequest>(GuaSpatialDocumentType.Request);
    public GuaSpatialResult ReadResult() => Read<GuaSpatialResult>(GuaSpatialDocumentType.Result);
    public GuaSpatialProvider ReadProvider() => Read<GuaSpatialProvider>(GuaSpatialDocumentType.Provider);
    private T Read<T>(GuaSpatialDocumentType type) { if (Type != type) throw new InvalidOperationException("Spatial document type mismatch"); return JsonSerializer.Deserialize<T>(ToJson(), JsonOptions)!; }
    public void CheckRequest(GuaSpatialDocument provider)
    {
        using var inputs = new SpatialReferences();
        Check(Native.gua_spatial_check_request(inputs.Use(_handle), inputs.Use(provider._handle), out var error), error);
    }
    public void CheckResult(GuaSpatialDocument result)
    {
        using var inputs = new SpatialReferences();
        Check(Native.gua_spatial_check_result(inputs.Use(_handle), inputs.Use(result._handle), out var error), error);
    }
    public void Dispose() => _handle.Dispose();
}
// Mutable authoring DTOs are copied into immutable native documents. Optional
// evidence stays nullable here and is omitted on wire, never replaced with zero.
public sealed record GuaSpatialVector(double X, double Y, double Z);
public sealed record GuaSpatialBasis(GuaSpatialVector X, GuaSpatialVector Y, GuaSpatialVector Z);
public sealed record GuaSpatialSegment(GuaSpatialVector From, GuaSpatialVector To);
public sealed record GuaSpatialRegion(GuaSpatialVector Min, GuaSpatialVector Max);
public sealed class GuaSpatialShape
{
    public string Type { get; set; } = "sphere";
    public GuaSpatialVector? Center { get; set; }
    public GuaSpatialVector? PointA { get; set; }
    public GuaSpatialVector? PointB { get; set; }
    public double? Radius { get; set; }
    public GuaSpatialVector? HalfExtents { get; set; }
    public GuaSpatialBasis? Basis { get; set; }
}
public sealed class GuaSpatialRequest
{
    public string SchemaVersion { get; set; } = "spatial-r1";
    public string DocumentType { get; set; } = "request";
    public long RequestId { get; set; }
    public long SessionEpoch { get; set; }
    public string QueryId { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public long SpaceEpoch { get; set; }
    public string QueryPolicyId { get; set; } = "";
    public string Kind { get; set; } = "";
    public double DeadlineMs { get; set; }
    public string Consistency { get; set; } = "bestEffort";
    public GuaSpatialSegment? Segment { get; set; }
    public GuaSpatialShape? Shape { get; set; }
    public GuaSpatialVector? Delta { get; set; }
    public int? MaxHits { get; set; }
}
public sealed record GuaSpatialFailure(string Code);
public sealed record GuaSpatialWorldSnapshot(long SessionEpoch, long FrameSequence, long Revision);
public sealed class GuaSpatialSample
{
    public string PhysicsSampleId { get; set; } = "";
    public long Tick { get; set; }
    public double ObservedAtMs { get; set; }
    public GuaSpatialWorldSnapshot? WorldSnapshot { get; set; }
}
public sealed class GuaSpatialCoverage
{
    public string State { get; set; } = "unknown";
    public GuaSpatialRegion? LoadedRegion { get; set; }
    public string? Reason { get; set; }
}
public sealed class GuaSpatialHit
{
    public string Relation { get; set; } = "unknown";
    public Dictionary<string, string> Missing { get; set; } = new();
    public GuaSpatialVector? Position { get; set; }
    public double? Distance { get; set; }
    public GuaSpatialVector? Normal { get; set; }
    public string? CollisionRef { get; set; }
    public string? WorldObjectId { get; set; }
}
public sealed class GuaSpatialErrorBound
{
    public string State { get; set; } = "unknown";
    public double? Absolute { get; set; }
    public string? Reason { get; set; }
}
public sealed class GuaSpatialMotion
{
    public string Type { get; set; } = "";
    public string? Source { get; set; }
    public GuaSpatialErrorBound? Error { get; set; }
    public double? SafeFraction { get; set; }
    public double? UnsafeFraction { get; set; }
    public double? Distance { get; set; }
    public double? Fraction { get; set; }
}
public sealed class GuaSpatialResult
{
    public string SchemaVersion { get; set; } = "spatial-r1";
    public string DocumentType { get; set; } = "result";
    public long RequestId { get; set; }
    public long SessionEpoch { get; set; }
    public string QueryId { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public long SpaceEpoch { get; set; }
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public GuaSpatialFailure? Error { get; set; }
    public string? Outcome { get; set; }
    public GuaSpatialCoverage? Coverage { get; set; }
    public bool? Truncated { get; set; }
    public GuaSpatialSample? Sample { get; set; }
    public GuaSpatialHit[]? Hits { get; set; }
    public string? Nearest { get; set; }
    public string? OriginInside { get; set; }
    public string? InitialOverlap { get; set; }
    public GuaSpatialMotion? Motion { get; set; }
}
public sealed class GuaSpatialUnit
{
    public string Label { get; set; } = "";
    public double? MetersPerUnit { get; set; }
}
public sealed class GuaSpatialPrecision
{
    public string Representation { get; set; } = "unknown";
    public string Reason { get; set; } = "";
    public double? AbsoluteError { get; set; }
}
public sealed record GuaSpatialEngine(string Name, string Version, string Backend, string BackendVersion);
public sealed record GuaSpatialLimits(int MaxQueriesPerBatch, int MaxHitsPerQuery, double MaxDeadlineMs);
public sealed class GuaSpatialProvider
{
    public string SchemaVersion { get; set; } = "spatial-r1";
    public string DocumentType { get; set; } = "provider";
    public string ProviderId { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public long SpaceEpoch { get; set; }
    public string WorldSpace { get; set; } = "world3d";
    public GuaSpatialBasis Basis { get; set; } = null!;
    public GuaSpatialVector Up { get; set; } = null!;
    public GuaSpatialUnit Unit { get; set; } = null!;
    public GuaSpatialPrecision Precision { get; set; } = null!;
    public string[] Operations { get; set; } = [];
    public string[] Shapes { get; set; } = [];
    public string[] Policies { get; set; } = [];
    public string[] Consistencies { get; set; } = [];
    public GuaSpatialEngine Engine { get; set; } = null!;
    public GuaSpatialLimits Limits { get; set; } = null!;
}
internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct SpatialOptions { internal uint StructSize; internal int DocumentType; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] internal struct SpatialError { internal int Code; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Path; }
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_from_json(in SpatialOptions options, ValueText json, out nint p, out SpatialError error);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_document_type(nint p);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_copy_json(nint p, [Out] byte[]? buffer, int capacity);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern void gua_spatial_destroy(nint p);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_check_request(nint request, nint provider, out SpatialError error);
    [DllImport(ValueLibrary, CallingConvention = CallingConvention.Cdecl)] internal static extern int gua_spatial_check_result(nint request, nint result, out SpatialError error);
}
