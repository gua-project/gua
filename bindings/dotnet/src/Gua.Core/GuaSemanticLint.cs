using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gua.Core;

public enum GuaLintSeverity { Info, Warning, Error }
public sealed record GuaSemanticLintOptions(GuaObservationProfile Profile = GuaObservationProfile.Debug, bool IncludeWorld = true);
public sealed record GuaLintTreeMetadata(int SchemaVersion, ulong SessionEpoch, ulong FrameSequence, ulong Revision, string? Screen, string? Scene);
public sealed record GuaLintSummary(int Error, int Warning, int Info, int Total);
public sealed record GuaLintFinding(string RuleId, GuaLintSeverity Severity, string Message, string TargetKind, string TargetId, string Path);
public sealed record GuaSemanticLintReport(int SchemaVersion, GuaObservationProfile Profile, GuaLintTreeMetadata UiTree,
    GuaLintTreeMetadata? WorldObjectTree, GuaLintSummary Summary, IReadOnlyList<GuaLintFinding> Findings)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static JsonSerializerOptions CreateOptions() {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)); return options;
    }
    public static GuaSemanticLintReport Parse(string json) {
        var report = JsonSerializer.Deserialize<GuaSemanticLintReport>(json, JsonOptions) ?? throw new ArgumentException("Invalid lint report.", nameof(json));
        if (report.SchemaVersion != 1 || report.UiTree is null || report.Summary is null || report.Findings is null)
            throw new ArgumentException("Unsupported lint report.", nameof(json));
        return report;
    }
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public bool HasFindings(GuaLintSeverity minimum = GuaLintSeverity.Error) => Findings.Any(f => f.Severity >= minimum);
}

/// <summary>Explicit native lint of committed snapshots. Offline inputs must already
/// be projected to their recorded profile; Analyze does not authorize or project JSON.</summary>
public static class GuaSemanticLinter
{
    public static GuaSemanticLintReport Analyze(GuaContext context, GuaSemanticLintOptions? options = null) {
        Guard.NotNull(context, nameof(context));
        return context.AnalyzeSemanticLint(options ?? new GuaSemanticLintOptions());
    }
    public static GuaSemanticLintReport Analyze(string uiTreeJson, string? worldObjectTreeJson = null, GuaSemanticLintOptions? options = null) {
        Guard.NotNull(uiTreeJson, nameof(uiTreeJson));
        options ??= new GuaSemanticLintOptions();
        if (uiTreeJson.IndexOf('\0') >= 0 || (worldObjectTreeJson?.IndexOf('\0') ?? -1) >= 0) throw new ArgumentException("Snapshot contains NUL.");
        var status = Native.gua_semantic_lint_analyze_snapshots(uiTreeJson, options.IncludeWorld ? worldObjectTreeJson : null, (int)options.Profile, out var report);
        return Read(status, report);
    }
    internal static GuaSemanticLintReport Read(int status, nint report) {
        if (status != 0) throw new ArgumentException("Semantic lint rejected the snapshot or options.");
        try { return GuaSemanticLintReport.Parse(GuaValue.Copy(report, Native.gua_semantic_lint_report_copy_json)); }
        finally { Native.gua_semantic_lint_report_destroy(report); }
    }
}
public sealed partial class GuaContext
{
    internal GuaSemanticLintReport AnalyzeSemanticLint(GuaSemanticLintOptions options) {
        ThrowIfDisposed();
        var native = new Native.SemanticLintOptions { StructSize = (uint)Marshal.SizeOf<Native.SemanticLintOptions>(), Profile = (int)options.Profile, IncludeWorld = options.IncludeWorld ? 1 : 0 };
        var status = Native.gua_semantic_lint_analyze(_handle, in native, out var report);
        return GuaSemanticLinter.Read(status, report);
    }
}
internal static partial class Native
{
#if GUA_STATIC_LINK
    private const string SemanticLintLibrary = "__Internal";
#else
    private const string SemanticLintLibrary = "gua";
#endif
    [StructLayout(LayoutKind.Sequential)]
    internal struct SemanticLintOptions { internal uint StructSize; internal int Profile; internal int IncludeWorld; }
    [DllImport(SemanticLintLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int gua_semantic_lint_analyze(nint context, in SemanticLintOptions options, out nint report);
    [DllImport(SemanticLintLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int gua_semantic_lint_analyze_snapshots([MarshalAs(UnmanagedType.LPUTF8Str)] string ui, [MarshalAs(UnmanagedType.LPUTF8Str)] string? world, int profile, out nint report);
    [DllImport(SemanticLintLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int gua_semantic_lint_report_copy_json(nint report, [Out] byte[]? bytes, int size);
    [DllImport(SemanticLintLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void gua_semantic_lint_report_destroy(nint report);
}
