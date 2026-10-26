using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Visual.Tests;

public sealed class TraceReportTests
{
    private static GuaTraceReadResult Snapshot()
    {
        using var data = JsonDocument.Parse("{\"</ScRiPt>\":\"</script><img src=x onerror=alert(1)>日本語😀&\"}");
        return new(new(1, new string('a', 32), "recent", "always", "debug", "failed", true,
            "clock", "2026-10-26T00:00:00Z", 1, new(false, 0, 0, Array.Empty<string>())),
            new[] { new GuaTraceEvent(1, new string('a', 32), 1, "e1", new string('b', 32), "mark", 0, data.RootElement.Clone()) },
            new Dictionary<string, JsonElement> { ["future"] = data.RootElement.Clone() }, Array.Empty<string>());
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void NonpositiveLimitReturnsExplicitFailureWithoutOutput(int limit)
    {
        var result = GuaTraceReport.RenderHtml(Snapshot(), limit);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Is.EqualTo("invalid-output-limit"));
        Assert.That(result.Utf8Html, Is.Null);
    }

    [Test]
    public void ExactUtf8BoundaryAndSafeEmbeddingRoundTrip()
    {
        var snapshot = Snapshot();
        var large = GuaTraceReport.RenderHtml(snapshot, 8 * 1024 * 1024);
        Assert.That(large.Succeeded, Is.True, large.Error);
        var bytes = large.Utf8Html!;
        var exact = GuaTraceReport.RenderHtml(snapshot, bytes.Length);
        Assert.That(exact.Succeeded, Is.True, exact.Error);
        Assert.That(exact.Utf8Html, Is.EqualTo(bytes));
        var shortResult = GuaTraceReport.RenderHtml(snapshot, bytes.Length - 1);
        Assert.That(shortResult.Succeeded, Is.False);
        Assert.That(shortResult.Error, Is.EqualTo("report-limit"));
        Assert.That(shortResult.Utf8Html, Is.Null);
        var tiny = GuaTraceReport.RenderHtml(snapshot, 1);
        Assert.That(tiny.Error, Is.EqualTo("report-limit"));
        Assert.That(tiny.Utf8Html, Is.Null);

        var html = new UTF8Encoding(false, true).GetString(bytes);
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 239, 187, 191 }));
        const string marker = "<script type=\"application/json\" id=\"gua-trace-data\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        Assert.That(start, Is.GreaterThanOrEqualTo(marker.Length));
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        var json = html.Substring(start, end - start);
        Assert.That(json, Does.Not.Contain("<").And.Not.Contain("</script>"));
        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("events").GetArrayLength(), Is.EqualTo(1));
        Assert.That(document.RootElement.GetProperty("events")[0].GetProperty("data").GetProperty("</ScRiPt>").GetString(),
            Is.EqualTo(snapshot.Events[0].Data.GetProperty("</ScRiPt>").GetString()));
        Assert.That(document.RootElement.GetProperty("blobs").GetProperty("future").GetProperty("</ScRiPt>").GetString(),
            Is.EqualTo(snapshot.Blobs["future"].GetProperty("</ScRiPt>").GetString()));
        var scriptStart = end + "</script><script>".Length;
        var scriptEnd = html.IndexOf("</script>", scriptStart, StringComparison.Ordinal);
        var script = html.Substring(scriptStart, scriptEnd - scriptStart);
        using var sha = SHA256.Create();
        var hash = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(script)));
        Assert.That(html, Does.Contain("script-src 'sha256-" + hash + "'"));
        Assert.That(html, Does.Contain("connect-src 'none'").And.Contain("default-src 'none'"));
        Assert.That(script, Does.Not.Contain("</script"));
    }

    [Test]
    public async Task LegacyWriterUsesIdenticalRendererAndDoesNotOverwrite()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "report-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
            trace.Mark("Trace 日本語 </script>");
            Assert.That(await trace.CompleteAsync(GuaTraceOutcome.Failed), Is.True);
            var rendered = GuaTraceReport.RenderHtml(GuaTraceReader.Read(trace.ArtifactPath), 8 * 1024 * 1024);
            Assert.That(rendered.Succeeded, Is.True, rendered.Error);
            var path = Path.Combine(root, "out", "report.html");
            Assert.That(Directory.Exists(Path.GetDirectoryName(path)), Is.False, "render must not create output paths");
            var written = GuaTraceReport.WriteHtml(trace.ArtifactPath, path);
            Assert.That(written.Succeeded, Is.True, written.Error);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(rendered.Utf8Html));
            var second = GuaTraceReport.WriteHtml(trace.ArtifactPath, path);
            Assert.That(second.Succeeded, Is.False);
            Assert.That(second.Error, Is.EqualTo("report-failed"));
            Assert.That(second.Path, Is.Null);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(rendered.Utf8Html));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void InvalidSnapshotDoesNotExposePartialOutput()
    {
        var result = GuaTraceReport.RenderHtml(Snapshot() with { Blobs = new Dictionary<string, JsonElement> { ["undefined"] = default } }, 8 * 1024 * 1024);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Is.EqualTo("report-failed"));
        Assert.That(result.Utf8Html, Is.Null);
    }
}
