using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gua.Testing;

public sealed record GuaTraceReportResult(bool Succeeded, string? Path, string? Error);
public sealed record GuaTraceHtmlResult(bool Succeeded, byte[]? Utf8Html, string? Error);
public static class GuaTraceReport
{
    /// <summary>
    /// Renders an already inspected snapshot without filesystem I/O. The positive limit includes
    /// all UTF-8 HTML bytes (no BOM). Failure returns no partial output. Input validation/redaction
    /// and reader limits belong to the caller; this method does not redact the snapshot.
    /// </summary>
    public static GuaTraceHtmlResult RenderHtml(GuaTraceReadResult trace, int maxOutputBytes)
    {
        if (maxOutputBytes <= 0) return new(false, null, "invalid-output-limit");
        try
        {
            if (trace is null) return new(false, null, "report-failed");
            using var resource = typeof(GuaTraceReport).Assembly.GetManifestResourceStream("Gua.Trace.Viewer.js");
            if (resource is null) return new(false, null, "viewer-not-bundled");
            using var reader = new StreamReader(resource, Encoding.UTF8);
            var script = reader.ReadToEnd();
            if (script.IndexOf("</script", StringComparison.OrdinalIgnoreCase) >= 0) return new(false, null, "invalid-viewer-bundle");
            using var sha = SHA256.Create();
            var hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(script)));
            using var output = new LimitedBuffer(maxOutputBytes);
            output.Text("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'sha256-" + hash + "'; style-src 'unsafe-inline'; connect-src 'none'; img-src data:; base-uri 'none'; form-action 'none'\">" +
                "<title>Gua Trace Viewer v1</title></head><body style=\"background:#121926;color:#e7eaf1;font-family:system-ui\"><div id=\"root\"></div>" +
                "<script type=\"application/json\" id=\"gua-trace-data\">");
            // The default JSON encoder escapes '<', including strings/keys in JsonElement values.
            // Stream serialization avoids materializing the complete JSON or HTML as strings.
            JsonSerializer.Serialize(output, trace, GuaTraceJson.Options);
            output.Text("</script><script>");
            output.Text(script);
            output.Text("</script></body></html>");
            return new(true, output.ToArray(), null);
        }
        catch (OutputLimitException) { return new(false, null, "report-limit"); }
        catch { return new(false, null, "report-failed"); }
    }

    /// <summary>Offline self-contained HTML with the same bundled React component as Inspector.</summary>
    public static GuaTraceReportResult WriteHtml(string traceDirectory, string outputPath)
    {
        try
        {
            var result = RenderHtml(GuaTraceReader.Read(traceDirectory), int.MaxValue);
            if (!result.Succeeded) return new(false, null, result.Error == "report-limit" ? "report-failed" : result.Error);
            var full = Path.GetFullPath(outputPath); GuaTraceReader.CheckNoLinks(full);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(result.Utf8Html!, 0, result.Utf8Html!.Length);
            return new(true, full, null);
        }
        catch { return new(false, null, "report-failed"); }
    }

    private sealed class OutputLimitException : Exception { }

    // The backing buffer's capacity never exceeds the cap. ToArray adds one success-only copy.
    private sealed class LimitedBuffer(int limit) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public byte[] ToArray() => _buffer.ToArray();
        private void Reserve(int count)
        {
            if (count > limit - _buffer.Length) throw new OutputLimitException();
            var needed = (int)_buffer.Length + count;
            if (needed > _buffer.Capacity)
                _buffer.Capacity = (int)Math.Min(limit, Math.Max((long)needed, Math.Max(256L, (long)_buffer.Capacity * 2)));
        }
        public void Text(string text)
        {
            Reserve(Encoding.UTF8.GetByteCount(text));
            var bytes = Encoding.UTF8.GetBytes(text);
            Write(bytes, 0, bytes.Length);
        }
        public override void Write(byte[] buffer, int offset, int count)
        { Reserve(count); _buffer.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Reserve(buffer.Length); _buffer.Write(buffer); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }
}
