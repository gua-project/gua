using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gua.Testing;

public sealed record GuaTraceReportResult(bool Succeeded, string? Path, string? Error);
public static class GuaTraceReport
{
    /// <summary>Offline self-contained HTML with the same bundled React component as Inspector.</summary>
    public static GuaTraceReportResult WriteHtml(string traceDirectory, string outputPath)
    {
        try
        {
            var trace = GuaTraceReader.Read(traceDirectory);
            using var resource = typeof(GuaTraceReport).Assembly.GetManifestResourceStream("Gua.Trace.Viewer.js");
            if (resource is null) return new(false, null, "viewer-not-bundled");
            using var reader = new StreamReader(resource, Encoding.UTF8);
            var script = reader.ReadToEnd();
            if (script.IndexOf("</script", StringComparison.OrdinalIgnoreCase) >= 0) return new(false, null, "invalid-viewer-bundle");
            using var sha = SHA256.Create();
            var hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(script)));
            // Default JSON encoder escapes '<', so data cannot terminate the inert script element.
            var data = JsonSerializer.Serialize(trace, GuaTraceJson.Options);
            var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'sha256-" + hash + "'; style-src 'unsafe-inline'; connect-src 'none'; img-src data:; base-uri 'none'; form-action 'none'\">" +
                "<title>Gua Trace Viewer v1</title></head><body style=\"background:#121926;color:#e7eaf1;font-family:system-ui\"><div id=\"root\"></div>" +
                "<script type=\"application/json\" id=\"gua-trace-data\">" + data + "</script><script>" + script + "</script></body></html>";
            var full = Path.GetFullPath(outputPath); GuaTraceReader.CheckNoLinks(full);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(html);
            return new(true, full, null);
        }
        catch { return new(false, null, "report-failed"); }
    }
}

