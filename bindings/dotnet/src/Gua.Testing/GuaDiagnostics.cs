using System.Text;
using System.Text.Json;
using Gua.Core;

namespace Gua.Testing;

public sealed class GuaDiagnosticOptions
{
    public required string TestName { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine("artifacts", "gua");
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, string> CallerMetadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, Func<string>> TextArtifacts { get; init; } = new Dictionary<string, Func<string>>();
    public Func<byte[]>? ScreenshotCapture { get; init; }
    public Action<GuaDiagnosticFile>? AttachmentSink { get; init; }
    /// <summary>Optional Trace copy, captured before teardown. Existing diagnostics files retain their own policy.</summary>
    public GuaTraceSession? Trace { get; init; }
    public string? TraceStepId { get; init; }
    /// <summary>Legacy diagnostics is Debug-only. Player Trace copies are rejected; use an explicit authorized getter instead.</summary>
    public GuaObservationProfile TraceProfile { get; init; } = GuaObservationProfile.Debug;
}

public sealed record GuaDiagnosticFile(string Path, string MediaType);
public sealed record GuaDiagnosticError(string Stage, string ErrorType, string Message);
public sealed record GuaDiagnosticsResult(
    Exception PrimaryException,
    string? ArtifactPath,
    IReadOnlyList<GuaDiagnosticFile> Files,
    IReadOnlyList<GuaDiagnosticError> CaptureErrors)
{
    public bool Succeeded => ArtifactPath is not null;
}

public sealed class GuaDiagnosticsSession
{
    private readonly IGuaContext _context;
    private readonly GuaDiagnosticOptions _options;

    public GuaDiagnosticsSession(IGuaContext context, GuaDiagnosticOptions options)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public GuaDiagnosticsResult Capture(Exception primaryException, string? initialUiTreeJson = null)
    {
        Guard.NotNull(primaryException, nameof(primaryException));
        var version = new Lazy<GuaVersion>(_context.GetVersion);
        var capture = GuaDiagnosticWriter.Capture(_context, primaryException.ToString(), _options, initialUiTreeJson,
            () => version.Value, recordTraceFailure: false);
        var errors = new List<GuaDiagnosticError>();
        if (capture.Error is not null) errors.Add(new("diagnostics", "CaptureError", capture.Error));
        var files = new List<GuaDiagnosticFile>();
        if (capture.ArtifactPath is not null)
        {
            AddSupplementalArtifacts(capture.ArtifactPath, errors, () => version.Value);
            if (errors.Count > 0)
                File.WriteAllText(Path.Combine(capture.ArtifactPath, "session-capture-errors.json"), JsonSerializer.Serialize(errors, GuaDiagnosticWriter.JsonOptions), new UTF8Encoding(false));
            foreach (var path in Directory.EnumerateFiles(capture.ArtifactPath).OrderBy(path => path, StringComparer.Ordinal))
                files.Add(new GuaDiagnosticFile(Path.GetFullPath(path), MediaType(path)));
            foreach (var file in files)
            {
                try { _options.AttachmentSink?.Invoke(file); }
                catch (Exception error) { errors.Add(new("attachment", error.GetType().Name, error.Message)); }
            }
        }
        if (errors.Count > 0) GuaDiagnosticWriter.RecordTraceFailure(_options);
        return new(primaryException, capture.ArtifactPath, files, errors);
    }

    private void AddSupplementalArtifacts(string directory, List<GuaDiagnosticError> errors, Func<GuaVersion> getVersion)
    {
        WriteJson(directory, "caller-metadata.json", _options.CallerMetadata, errors, "caller-metadata");
        try
        {
            File.WriteAllText(Path.Combine(directory, "version.json"), JsonSerializer.Serialize(getVersion(), GuaDiagnosticWriter.JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception error) { errors.Add(new("version", error.GetType().Name, error.Message)); }
        foreach (var (name, read) in _options.TextArtifacts)
        {
            try { File.WriteAllText(Path.Combine(directory, SanitizeFileName(name)), read(), new UTF8Encoding(false)); }
            catch (Exception error) { errors.Add(new($"text:{name}", error.GetType().Name, error.Message)); }
        }
        if (_options.ScreenshotCapture is not null)
        {
            try { File.WriteAllBytes(Path.Combine(directory, "screenshot-on-failure.png"), _options.ScreenshotCapture()); }
            catch (Exception error) { errors.Add(new("screenshot", error.GetType().Name, error.Message)); }
        }
    }

    private static void WriteJson(string directory, string name, object value, List<GuaDiagnosticError> errors, string stage)
    {
        try { File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, GuaDiagnosticWriter.JsonOptions), new UTF8Encoding(false)); }
        catch (Exception error) { errors.Add(new(stage, error.GetType().Name, error.Message)); }
    }

    private static string SanitizeFileName(string value) => string.Concat(value.Select(ch =>
        Path.GetInvalidFileNameChars().Contains(ch) || char.IsControl(ch) ? '_' : ch));
    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json", ".jsonl" => "application/x-ndjson", ".png" => "image/png", _ => "text/plain",
    };
}

public sealed record GuaDiagnosticCapture(string? ArtifactPath, string? Error)
{
    internal string MessageSuffix => ArtifactPath is not null
        ? $" Gua diagnostics: {ArtifactPath}"
        : Error is not null ? $" Gua diagnostics capture error: {Error}" : string.Empty;
}

public static class GuaDiagnosticWriter
{
    public static GuaDiagnosticCapture Capture(
        IGuaContext context, string failureMessage, GuaDiagnosticOptions options, string? initialUiTreeJson = null)
        => Capture(context, failureMessage, options, initialUiTreeJson, () => context.GetVersion(), recordTraceFailure: true);

    internal static GuaDiagnosticCapture Capture(
        IGuaContext context, string failureMessage, GuaDiagnosticOptions options, string? initialUiTreeJson,
        Func<GuaVersion> getVersion, bool recordTraceFailure)
    {
        Guard.NotNull(context, nameof(context));
        Guard.NotNull(options, nameof(options));
        var errors = new List<string>();
        string diagnosticsJson;
        try
        {
            diagnosticsJson = context.GetDiagnosticsJson();
        }
        catch (Exception error)
        {
            if (recordTraceFailure) RecordTraceFailure(options);
            return new GuaDiagnosticCapture(null, $"{error.GetType().Name}: {error.Message}");
        }

        if (options.Trace is { } trace)
        {
            // Both outputs describe this one read, not two potentially different live snapshots.
            GuaTraceCapture.Diagnostics(trace, options.TraceStepId ?? "", () =>
                options.TraceProfile == GuaObservationProfile.Debug ? diagnosticsJson : throw new NotSupportedException(),
                GuaObservationProfile.Debug);
            GuaTraceCapture.JsonAttachment(trace, options.TraceStepId ?? "", "gua.environment.v1", () =>
                options.TraceProfile == GuaObservationProfile.Debug
                    ? JsonSerializer.Serialize(new { version = getVersion(), environment = options.Environment,
                        callerMetadata = options.CallerMetadata }, JsonOptions) : throw new NotSupportedException(),
                GuaObservationProfile.Debug);
        }

        try
        {
            using var diagnostics = JsonDocument.Parse(diagnosticsJson);
            var root = diagnostics.RootElement;
            var directory = CreateUniqueDirectory(options);
            WriteNew(Path.Combine(directory, "failure-summary.txt"), failureMessage + Environment.NewLine);
            var environment = new Dictionary<string, object?>
            {
                ["testName"] = options.TestName,
                ["processId"] = CurrentProcessId(),
                ["framework"] = ".NET",
                ["runtime"] = Environment.Version.ToString(),
                ["capturedAt"] = options.Clock().ToString("O"),
                ["host"] = options.Environment,
                ["runtimeMetadata"] = root.GetProperty("environment").Clone(),
            };
            WriteNew(Path.Combine(directory, "environment.json"), JsonSerializer.Serialize(environment, JsonOptions));
            var finalTree = root.GetProperty("uiTree").GetRawText();
            WriteNew(Path.Combine(directory, "ui-tree.json"), Pretty(finalTree));
            WriteNew(Path.Combine(directory, "pending-requests.json"), Pretty(root.GetProperty("pendingRequests").GetRawText()));
            WriteNew(Path.Combine(directory, "logs.json"), Pretty(root.GetProperty("logs").GetRawText()));
            WriteJsonLines(Path.Combine(directory, "operations.jsonl"), root.GetProperty("operations"));
            WriteJsonLines(Path.Combine(directory, "events.jsonl"), root.GetProperty("events"));

            if (initialUiTreeJson is not null)
            {
                WriteNew(Path.Combine(directory, "ui-tree-before.json"), Pretty(initialUiTreeJson));
                WriteNew(Path.Combine(directory, "ui-tree.diff.json"), BuildNodeDiff(initialUiTreeJson, finalTree));
            }

            if (root.TryGetProperty("screenshot", out var screenshot) && screenshot.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    WriteScreenshot(directory, screenshot);
                }
                catch (Exception error)
                {
                    errors.Add($"screenshot: {error.GetType().Name}: {error.Message}");
                }
            }
            if (errors.Count > 0)
            {
                if (recordTraceFailure) RecordTraceFailure(options);
                WriteNew(Path.Combine(directory, "capture-errors.json"), JsonSerializer.Serialize(errors, JsonOptions));
            }
            return new GuaDiagnosticCapture(Path.GetFullPath(directory), errors.Count == 0 ? null : string.Join("; ", errors));
        }
        catch (Exception error)
        {
            if (recordTraceFailure) RecordTraceFailure(options);
            return new GuaDiagnosticCapture(null, $"{error.GetType().Name}: {error.Message}");
        }
    }

    internal static void RecordTraceFailure(GuaDiagnosticOptions options)
    {
        if (options.Trace is not { } trace) return;
        trace.RecordCaptureFailure(options.TraceStepId ?? "", "diagnostics", "diagnostics-failed");
    }

    private static string CreateUniqueDirectory(GuaDiagnosticOptions options)
    {
        var testName = Sanitize(options.TestName);
        var parent = Path.Combine(options.OutputDirectory, testName);
        Directory.CreateDirectory(parent);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var name = $"{options.Clock():yyyyMMddTHHmmssfffZ}-{id}";
            var path = Path.Combine(parent, name);
            if (Directory.Exists(path)) continue;
            Directory.CreateDirectory(path);
            return path;
        }
        throw new IOException("Could not allocate a unique Gua diagnostics directory.");
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed-test" : sanitized;
    }

    private static int CurrentProcessId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.Id;
    }

    private static void WriteNew(string path, string contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static void WriteJsonLines(string path, JsonElement array)
    {
        var lines = array.ValueKind == JsonValueKind.Array
            ? string.Join(Environment.NewLine, array.EnumerateArray().Select(item => item.GetRawText()))
            : string.Empty;
        if (lines.Length > 0) lines += Environment.NewLine;
        WriteNew(path, lines);
    }

    private static string Pretty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, JsonOptions);
    }

    private static string BuildNodeDiff(string beforeJson, string afterJson)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var oldNodes = NodesById(before.RootElement);
        var newNodes = NodesById(after.RootElement);
        var added = newNodes.Keys.Except(oldNodes.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var removed = oldNodes.Keys.Except(newNodes.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var changed = oldNodes.Keys.Intersect(newNodes.Keys, StringComparer.Ordinal)
            .Where(id => !string.Equals(oldNodes[id], newNodes[id], StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(new { schemaVersion = 1, added, removed, changed }, JsonOptions);
    }

    private static Dictionary<string, string> NodesById(JsonElement root) =>
        root.GetProperty("nodes").EnumerateArray().ToDictionary(
            node => node.GetProperty("id").GetString() ?? string.Empty,
            node => node.GetRawText(), StringComparer.Ordinal);

    private static void WriteScreenshot(string directory, JsonElement screenshot)
    {
        var dataUri = screenshot.GetProperty("dataUri").GetString() ?? string.Empty;
        const string prefix = "data:image/png;base64,";
        if (dataUri.Length == 0) return;
        if (!dataUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only PNG data URIs are written by diagnostics v1.");
        var bytes = Convert.FromBase64String(dataUri[prefix.Length..]);
        var path = Path.Combine(directory, "screenshot.png");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
