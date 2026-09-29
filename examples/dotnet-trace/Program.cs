using System.Text.Json;
using Gua.Core;
using Gua.Testing;

// An external runner: no test framework, Playtest or running game dependency.
var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/trace-example");
foreach (var outcome in new[] { GuaTraceOutcome.Passed, GuaTraceOutcome.Failed, GuaTraceOutcome.Interrupted })
{
    await using var trace = new GuaTraceSession(new() { OutputDirectory = root,
        CaptureMode = GuaTraceCaptureMode.Streaming, SavePolicy = GuaTraceSavePolicy.Always, Profile = "player" });
    var request = new GuaTraceRequest("fixture-host", "1", "7");
    var step = trace.BeginStep(GuaTraceStepKind.Action, "Move to checkpoint", request, sourceFile: "Runner.cs", sourceLine: 18);
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
    await trace.CompleteAsync(outcome);
    var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(root, outcome.ToString().ToLowerInvariant() + ".html"));
    if (!report.Succeeded) throw new InvalidOperationException(report.Error);
    Console.WriteLine(report.Path);
}
