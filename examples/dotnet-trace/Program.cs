using System.Text.Json;
using System.Security.Cryptography;
using Gua.Core;
using Gua.Testing;

// An external runner: no test framework, Playtest or running game dependency.
var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/trace-example");
foreach (var outcome in new[] { GuaTraceOutcome.Passed, GuaTraceOutcome.Failed, GuaTraceOutcome.Interrupted })
{
    await using var trace = new GuaTraceSession(new() { OutputDirectory = root,
        CaptureMode = GuaTraceCaptureMode.Streaming, SavePolicy = GuaTraceSavePolicy.Always, Profile = "player",
        Secrets = new[] { "RUNNER-SECRET" } });
    var request = new GuaTraceRequest("fixture-host", "1", "7");
    var step = trace.BeginStep(GuaTraceStepKind.Action, "Move to checkpoint", request, sourceFile: "Runner.cs", sourceLine: 18);
    Require(trace.Correlate(step, request), "request correlation");
    trace.Mark("External runner started");
    using var before = JsonDocument.Parse("""{"nodes":[{"id":"player","bounds":{"x":10,"y":20,"w":30,"h":40}}]}""");
    using var after = JsonDocument.Parse("""{"nodes":[{"id":"player","bounds":{"x":40,"y":20,"w":30,"h":40}}]}""");
    trace.Observe(step, "ui", "before", "available", new("fixture-host", "1", "1", "1"), before.RootElement);
    using var facts = JsonDocument.Parse("""{"accepted":true,"applicationTime":"unconfirmed"}""");
    trace.RecordRequest(request, "enqueue", facts.RootElement);
    using var first = GuaValue.String("First"); using var second = GuaValue.String("Second"); using var third = GuaValue.String("Third");
    trace.Change(step, "checkpoint.phase", "changed", new("fixture-host", "1", "2", "2"), first, second, "received");
    trace.Change(step, "checkpoint.phase", "changed", new("fixture-host", "1", "3", "3"), second, third, "received");
    var observation = trace.Observe(step, "ui", "main-result", "available", new("fixture-host", "1", "3", "3"), after.RootElement);
    trace.EndStep(step, outcome);
    var assertion = trace.BeginStep(GuaTraceStepKind.Assertion, "Failure condition evaluation");
    trace.Evaluate(assertion, "checkpoint.phase", "equals", third, third, "true", "failure-condition", new[] { observation }, outcome);
    trace.Annotate(assertion, "gua-playtest.scenarioId", first);
    using var hostile = JsonDocument.Parse("""{"note":"</script><img src=https://attacker.invalid onerror=alert(1)>"}""");
    trace.Attach(assertion, "example.unknown.v1", hostile.RootElement);
    trace.EndStep(assertion, outcome);
    // The runner decides the primary result, then records cleanup separately.
    Require(trace.SetPrimaryOutcome(outcome), "primary result");
    var cleanup = trace.BeginStep(GuaTraceStepKind.Lifecycle, "Runner cleanup", parentStepId: step);
    trace.Observe(cleanup, "ui", "after-cleanup", "available", new("fixture-host", "1", "4", "4"), before.RootElement);
    using var decision = JsonDocument.Parse(JsonSerializer.Serialize(new {
        request = new { observationIds = new[] { observation }, prompt = "public summary" },
        proposal = "reset checkpoint", decision = "adopted", reason = "restore fixture",
        primaryCause = "runner-decision", additionalCauses = new[] { "cleanup-failed" },
        credential = "RUNNER-SECRET", @private = new { mask = true, value = "PRIVATE-MARKER" },
    }));
    Require(trace.Attach(cleanup, "external-runner.ai-decision.v1", decision.RootElement), "AI attachment");
    // Unavailable usage is omitted, never invented as zero. No full chain of thought is needed.
    trace.EndStep(cleanup, GuaTraceOutcome.Failed);
    Require(await trace.FlushAsync(), "flush");
    Require(await trace.CompleteAsync(GuaTraceOutcome.Passed), "finalization");
    var read = GuaTraceReader.Read(trace.ArtifactPath);
    Require(read.Issues.Count == 0 && read.Manifest.PrimaryOutcome == outcome.ToString().ToLowerInvariant(), "primary outcome preserved");
    Require(read.Events.Single(e => e.Type == "assertion.evaluation").Data.GetProperty("truth").GetString() == "true", "failure condition truth");
    Require(read.Events.Single(e => e.Type == "annotation").Data.GetProperty("name").GetString() == "gua-playtest.scenarioId" &&
        read.Events.Single(e => e.Type == "annotation").Data.GetProperty("value").GetProperty("value").GetString() == "First", "unknown namespace round trip");
    Require(read.Events.Single(e => e.StepId == cleanup && e.Type == "step.end").Data.GetProperty("outcome").GetString() == "failed", "cleanup result");
    var attachment = read.Events.Single(e => e.Type == "attachment" && e.Data.GetProperty("schema").GetString() == "external-runner.ai-decision.v1");
    var blobName = attachment.Data.GetProperty("blob").GetString()!;
    Require(!read.Blobs[blobName].TryGetProperty("usage", out _), "unknown usage omitted");
    var bytes = File.ReadAllBytes(Path.Combine(trace.ArtifactPath, blobName));
    Require(Path.GetFileNameWithoutExtension(blobName) == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "redacted blob hash");
    Require(!string.Join("", Directory.GetFiles(trace.ArtifactPath, "*", SearchOption.AllDirectories).Select(File.ReadAllText))
        .Contains("RUNNER-SECRET") && !read.Blobs[blobName].GetRawText().Contains("PRIVATE-MARKER"), "redaction before persistence");
    var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(root, outcome.ToString().ToLowerInvariant() + ".html"));
    if (!report.Succeeded) throw new InvalidOperationException(report.Error);
    Console.WriteLine(report.Path);
}

// Force a real filesystem failure, keeping both an original exception and a successful result.
foreach (var succeeds in new[] { false, true })
{
    await using var trace = new GuaTraceSession(new() { OutputDirectory = root, SavePolicy = GuaTraceSavePolicy.Always });
    Directory.CreateDirectory(root);
    File.WriteAllText(trace.ArtifactPath, "block directory creation");
    var original = new InvalidOperationException("PRIVATE-EXCEPTION-MARKER");
    object result = new();
    try
    {
        Require(ReferenceEquals(await Run(), result), "runner result");
        Require(succeeds, "expected runner exception");
    }
    catch (InvalidOperationException caught)
    {
        Require(ReferenceEquals(caught, original), "original exception identity");
        Require(caught.StackTrace?.Contains("Program") == true, "original exception stack");
    }
    async Task<object> Run()
    {
        try
        {
            trace.Assert("Runner verification", () => { if (!succeeds) throw original; });
            return result;
        }
        finally
        {
            Require(!await trace.FlushAsync(), "expected flush failure");
            Require(!await trace.CompleteAsync(succeeds ? GuaTraceOutcome.Passed : GuaTraceOutcome.Failed), "expected finalization failure");
            Require(trace.Status.Issues.Contains("write-failed"), "failure status");
        }
    }
    Console.WriteLine($"storage-failure/{(succeeds ? "passed" : "failed")}: caller result preserved");
}

static void Require(bool condition, string contract)
{
    if (!condition) throw new InvalidOperationException("External runner acceptance failed: " + contract);
}
