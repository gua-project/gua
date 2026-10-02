using System.Runtime.InteropServices;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Testing;
using Gua.Testing.Recording;

foreach (var variable in new[] { "GUA_NATIVE_DIR", "GUA_RUNTIME_NATIVE_DIR" })
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        throw new InvalidOperationException("Native override is forbidden: " + variable);
var output = Path.GetFullPath(args.LastOrDefault() ?? "evidence");
Directory.CreateDirectory(output);
var required = new[] { "recording.schema.json", "trace.schema.json", "selector.schema.json", "game-input-actions-v2.schema.json", "input-value-schema-v1.schema.json", "observe-v1.schema.json", "spatial-r1.schema.json" };
var expectedCommit = Environment.GetEnvironmentVariable("GUA_EXPECTED_COMMIT") ?? throw new Exception("Expected source commit is required.");
using var viewerMetadata = JsonDocument.Parse(GuaDistribution.ViewerMetadata);
if (viewerMetadata.RootElement.GetProperty("sourceCommit").GetString() != expectedCommit) throw new Exception("Viewer provenance mismatch.");
File.WriteAllText(Path.Combine(output, "viewer-version.json"), GuaDistribution.ViewerMetadata);
foreach (var name in required)
    if (!GuaDistribution.SchemaNames.Contains(name)) throw new Exception("Missing packaged schema: " + name);
foreach (var name in GuaDistribution.SchemaNames) File.WriteAllText(Path.Combine(output, name), GuaDistribution.ReadSchema(name));
File.WriteAllText(Path.Combine(output, "LICENSE"), GuaDistribution.License);
File.WriteAllText(Path.Combine(output, "Viewer.LICENSES.txt"), GuaDistribution.ViewerLicenses);
File.WriteAllText(Path.Combine(output, "trace-observe-semantics.mjs"), GuaDistribution.ObserveSemanticsScript);
if (!GuaDistribution.ValidateJson("recording.schema.json", "{\"schemaVersion\":1,\"steps\":[]}") ||
    !GuaDistribution.ValidateJson("recording.schema.json", "{\"schemaVersion\":2,\"steps\":[]}") ||
    GuaDistribution.ValidateJson("recording.schema.json", "{\"schemaVersion\":99,\"steps\":[]}") ||
    !GuaDistribution.ValidateJson("selector.schema.json", "{\"id\":{\"value\":\"ready\"}}") ||
    GuaDistribution.ValidateJson("selector.schema.json", "{\"id\":42}")) throw new Exception("Offline schema validation failed.");
var recordingPath = Path.Combine(output, "legacy-recording.json");
File.WriteAllText(recordingPath, "{\"schemaVersion\":1,\"steps\":[{\"action\":\"click\",\"relativeMilliseconds\":0,\"preRevision\":1,\"postRevision\":2,\"sensitive\":false,\"target\":{\"id\":\"ready\"}}]}");
var legacy = GuaRecordingFile.Load(recordingPath);
if (legacy.SchemaVersion != 1 || legacy.Steps.Count != 1 || legacy.Steps[0].Target?.Id != "ready") throw new Exception("Legacy recording compatibility failed.");
GuaRecordingFile.Save(Path.Combine(output, "legacy-roundtrip.json"), legacy);

using (var trace = new GuaTraceSession(new GuaTraceOptions { OutputDirectory = Path.Combine(output, "trace"), SavePolicy = GuaTraceSavePolicy.Always }))
{
    var step = trace.BeginStep(GuaTraceStepKind.Mark, "package-only consumer");
    trace.EndStep(step, GuaTraceOutcome.Passed);
    if (!await trace.CompleteAsync(GuaTraceOutcome.Passed)) throw new Exception("Trace completion failed.");
    var read = GuaTraceReader.Read(trace.ArtifactPath);
    if (!read.Manifest.Finalized || read.Issues.Count != 0 || read.Events.Count != 2) throw new Exception("Packaged Trace reader failed.");
    var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, Path.Combine(output, "report.html"));
    if (!report.Succeeded) throw new Exception("Embedded Viewer failed: " + report.Error);
    var html = File.ReadAllText(report.Path!);
    if (!html.Contains("connect-src 'none'") || !html.Contains("gua-trace-data") || html.Contains("<script src=")) throw new Exception("Viewer is not self-contained.");
}
if (!args.Contains("--offline"))
{
    var nativeNames = OperatingSystem.IsWindows() ? new[] { "gua.dll", "gua_runtime.dll" } :
        OperatingSystem.IsMacOS() ? new[] { "libgua.dylib", "libgua_runtime.dylib" } : new[] { "libgua.so", "libgua_runtime.so" };
    foreach (var name in nativeNames)
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, name)) &&
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", name)))
            throw new Exception("Native asset is absent from the consumer output: " + name);
    using var context = new GuaContext();
    context.BeginFrame("distribution-consumer");
    context.RegisterNode("ready", "status", "Ready", new GuaBounds(0, 0, 1, 1));
    context.EndFrame();
    if (!context.GetUiTreeJson().Contains("distribution-consumer")) throw new Exception("Core load failed.");
    var coreVersion = context.GetVersion();
    if (coreVersion.BuildId != expectedCommit) throw new Exception("Core provenance mismatch.");
    coreVersion.EnsureCompatible(protocolSchemaVersion: "2", abiVersion: 1);
    File.WriteAllText(Path.Combine(output, "core-version.json"), JsonSerializer.Serialize(coreVersion, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    using var runtime = new GuaRuntime();
    runtime.BeginFrame("distribution-consumer");
    runtime.RegisterNode(new GuaNodeDescriptor("ready", "status", "Ready", new GuaBounds(0, 0, 1, 1)));
    runtime.EndFrame();
    using var version = JsonDocument.Parse(runtime.GetVersionJson());
    if (version.RootElement.GetProperty("buildId").GetString() != expectedCommit) throw new Exception("Runtime provenance mismatch.");
    File.WriteAllText(Path.Combine(output, "runtime-version.json"), version.RootElement.GetRawText());
    // Loading proves packaging only. No engine launch, attach or feature capability is implied.
}
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new {
    rid = RuntimeInformation.RuntimeIdentifier, sourceCommit = expectedCommit, offline = args.Contains("--offline"),
    schemas = GuaDistribution.SchemaNames, embeddedViewer = true, legacyRecording = true,
    nativeLoad = !args.Contains("--offline"), engineAttach = "not-tested", featureSupport = "not-inferred"
}));
Console.WriteLine("Distribution consumer passed: " + output);
