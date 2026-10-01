using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Gua.Testing;

// Deterministic offline UI acceptance fixtures; never operate a game or acquire pixels.
var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/trace-viewer-qa");
Directory.CreateDirectory(root);
var fixtures = new List<object>();
foreach (var outcome in new[] { GuaTraceOutcome.Passed, GuaTraceOutcome.Failed, GuaTraceOutcome.Interrupted })
{
    await using var trace = new GuaTraceSession(new() { OutputDirectory = root, CaptureMode = GuaTraceCaptureMode.Streaming,
        SavePolicy = GuaTraceSavePolicy.Always, Profile = "player", Secrets = new[] { "SECRET-MARKER" } });
    for (var i = 0; i < 2; i++)
    {
        var request = new GuaTraceRequest("fixture:input:1", "1", (7 + i).ToString());
        var step = trace.BeginStep(GuaTraceStepKind.Action, "Repeated hold", request, sourceFile: "C:/private/Fixture.cs", sourceLine: 18 + i);
        trace.RecordRequest(request, "enqueue", Json(new { hostClockId = "fixture", hostElapsedMilliseconds = 1 }));
        trace.RecordRequest(request, "hold-pending", Json(new { hostClockId = "fixture", hostElapsedMilliseconds = 2 }));
        trace.RecordRequest(request, "hold-started", Json(new { hostClockId = "fixture", hostElapsedMilliseconds = 3 }));
        JsonElement Ui(int x) => Json(new { nodes = new object[] { new { id = "player", role = "button", label = "Checkpoint", bounds = new { x, y = 60, w = 120, h = 80 } },
            new { id = "unknown-bounds", role = "button", label = "Unavailable bounds", bounds = new { x = 0, y = 0, w = 0 } } } });
        trace.Observe(step, "ui", "before", "available", new("game", "1", "1", "1"), Ui(40));
        trace.Observe(step, "world", "before", "available", new("game", "1", "1", "1"), Json(new { objects = new[] { new { id = "player", position = new { x = 1, y = 2 } } } }));
        trace.Record(step, "observation.change", Json(new { target = "phase", change = "changed", continuity = "received", before = new { type = "string", value = "First" }, after = new { type = "string", value = "Second" } }));
        trace.Record(step, "observation.change", Json(new { target = "phase", change = "changed", continuity = "received", before = new { type = "string", value = "Second" }, after = new { type = "string", value = "Third" } }));
        trace.Observe(step, "world", "result-decision", "available", new("game", "1", "3", "3"), Json(new { objects = new[] { new { id = "player", position = new { x = 2, y = 2 } } } }));
        var observation = trace.Observe(step, "ui", "result-decision", "available", new("game", "1", "3", "3"), Ui(160));
        if (!GuaTraceCapture.Screenshot(trace, step, Json(new { dataUri = Png(), width = 640, height = 360 }), observation, true)) throw new Exception("Screenshot fixture failed");
        trace.Record(step, "assertion.evaluation", Json(new { truth = "false", role = "goal", callerOutcome = outcome.ToString().ToLowerInvariant(), observations = new[] { observation } }));
        trace.Attach(step, "external.future.v1", Json(new { note = "</script><img src=https://attacker.invalid/pixels onerror=alert(1)>", credential = "SECRET-MARKER", code = "window.__hostileExecuted=true", url = "file:///C:/private" }));
        trace.Record(step, "annotation", Json(new { name = "external.future.goal", value = new { type = "string", value = "unknown-annotation" } }));
        trace.Attach(step, "gua.recording.v1", Json(new { reference = "recording-fixture", url = "https://attacker.invalid/recording" }));
        trace.Attach(step, "gua.lint.v1", Json(new { reference = "lint-fixture" }));
        trace.Attach(step, "gua.comparison.v1", Json(new { reference = "comparison-fixture" }));
        trace.Attach(step, "gua.diagnostics.v1", Json(new { logs = new[] { new { level = "info", message = "fixture log" } } }));
        trace.EndStep(step, outcome);
        // A late host result is retained without rewriting the caller result/duration.
        if (i == 0)
        {
            trace.RecordRequest(request, "late-completion", Json(new { succeeded = true }));
            trace.RecordRequest(request, "release-requested", Json(new { trigger = "cleanup" }));
            trace.RecordRequest(request, "release-confirmed", Json(new { succeeded = true }));
        }
    }
    trace.SetPrimaryOutcome(outcome);
    var cleanup = trace.BeginStep(GuaTraceStepKind.Lifecycle, "Runner cleanup");
    trace.Observe(cleanup, "ui", "after-cleanup", "available", new("game", "1", "4", "4"), Json(new { nodes = Array.Empty<object>() }));
    foreach (var availability in new[] { "failed", "partial", "stale", "gap", "outsideRetention" })
        trace.Observe(cleanup, "observe", "missing-" + availability, availability, new("game", "1"));
    trace.Attach(cleanup, "gua.trace.screenshot.v1", Json(new { schemaVersion = 1, profile = "player", pixelPolicy = "caller-authorized",
        observationId = "missing-observation", screenshot = new { dataUri = Png(), width = 640, height = 360 } }));
    trace.Attach(cleanup, "gua.trace.screenshot.v1", Json(new { schemaVersion = 1, profile = "player", pixelPolicy = "caller-authorized",
        observationId = "missing-observation", screenshot = new { dataUri = "https://attacker.invalid/pixels", width = 640, height = 360 } }));
    trace.EndStep(cleanup, GuaTraceOutcome.Failed);
    await trace.CompleteAsync(GuaTraceOutcome.Passed);
    var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(root, outcome.ToString().ToLowerInvariant() + ".html"));
    if (!report.Succeeded) throw new Exception(report.Error);
    fixtures.Add(new { outcome = outcome.ToString().ToLowerInvariant(), directory = trace.ArtifactPath, report = report.Path });
}
// A valid manifest with no complete records is visibly unverified, including an unfinished tail.
var empty = Path.Combine(root, "missing"); Directory.CreateDirectory(empty);
File.Copy(Path.Combine(((JsonElement)Json(fixtures[0])).GetProperty("directory").GetString()!, "manifest.json"), Path.Combine(empty, "manifest.json"));
File.WriteAllText(Path.Combine(empty, "events.jsonl"), "{\"unfinished\":");
var missingReport = GuaTraceReport.WriteHtml(empty, Path.Combine(root, "missing.html"));
if (!missingReport.Succeeded) throw new Exception(missingReport.Error);
File.WriteAllText(Path.Combine(root, "fixtures.json"), JsonSerializer.Serialize(fixtures));
Console.WriteLine(root);

static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
static string Png()
{
    const int width = 640, height = 360;
    using var png = new MemoryStream(); png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    byte[] UInt(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    void Chunk(string type, byte[] data)
    {
        var name = Encoding.ASCII.GetBytes(type); png.Write(UInt((uint)data.Length)); png.Write(name); png.Write(data);
        uint crc = uint.MaxValue;
        foreach (var b in name.Concat(data)) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0); }
        png.Write(UInt(crc ^ uint.MaxValue));
    }
    Chunk("IHDR", UInt(width).Concat(UInt(height)).Concat(new byte[] { 8, 2, 0, 0, 0 }).ToArray());
    using var pixels = new MemoryStream();
    using (var z = new ZLibStream(pixels, CompressionLevel.SmallestSize, true))
        for (var y = 0; y < height; y++) { z.WriteByte(0); for (var x = 0; x < width; x++) { var box = x >= 160 && x < 280 && y >= 60 && y < 140;
            z.WriteByte(box ? (byte)80 : (byte)24); z.WriteByte(box ? (byte)150 : (byte)36); z.WriteByte(box ? (byte)200 : (byte)56); } }
    Chunk("IDAT", pixels.ToArray()); Chunk("IEND", Array.Empty<byte>());
    return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
}

