using System.Text.Json;
using Gua.Testing;
using NUnit.Framework;

namespace Gua.Visual.Tests;

public sealed class TraceScreenshotTests
{
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1cAAAAASUVORK5CYII=";
    [TestCase(true, Png, 1, true)]
    [TestCase(false, Png, 1, false)]
    [TestCase(true, Png, 2, false)]
    [TestCase(true, "https://attacker.invalid/secret", 1, false)]
    [TestCase(true, "data:image/svg+xml,<svg/>", 1, false)]
    [TestCase(true, "data:image/png;base64,AAAA", 1, false)]
    public async Task ScreenshotRequiresExplicitPixelAuthorizationAndBoundedPng(bool authorized, string uri, int width, bool expected)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "trace-screenshot", Guid.NewGuid().ToString("N"));
        try
        {
            await using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always, Profile = "player" });
            var step = trace.BeginStep(GuaTraceStepKind.Action, "Screenshot");
            var observation = trace.Observe(step, "ui", "result-decision", "available", new("game", "1"), JsonSerializer.SerializeToElement(new { nodes = Array.Empty<object>() }));
            Assert.That(GuaTraceCapture.Screenshot(trace, step, JsonSerializer.SerializeToElement(new { dataUri = uri, width, height = 1 }), observation, authorized), Is.EqualTo(expected));
            trace.EndStep(step, GuaTraceOutcome.Passed); await trace.CompleteAsync(GuaTraceOutcome.Passed);
            var read = GuaTraceReader.Read(trace.ArtifactPath);
            var attachments = read.Events.Where(e => e.Type == "attachment").ToArray();
            Assert.That(attachments.Length, Is.EqualTo(expected ? 1 : 0));
            if (expected)
            {
                var attachment = read.Blobs[attachments.Single().Data.GetProperty("blob").GetString()!];
                Assert.That(attachment.GetProperty("profile").GetString(), Is.EqualTo("player"));
                Assert.That(attachment.GetProperty("observationId").GetString(), Is.EqualTo(observation));
            }
            else Assert.That(read.Manifest.Quality.Issues, Does.Contain("screenshot-unavailable"));
            var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(root, "report.html"));
            Assert.That(report.Succeeded, Is.True, report.Error);
            Assert.That(File.ReadAllText(report.Path!), Does.Contain("img-src data:").And.Contain("connect-src 'none'"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
