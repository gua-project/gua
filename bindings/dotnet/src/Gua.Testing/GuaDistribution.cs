using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Gua.Testing;

/// <summary>Version-pinned protocol resources. These APIs never load native code or connect to a host.</summary>
public static class GuaDistribution
{
    private const string Prefix = "Gua.Distribution.";
    private static readonly Assembly Assembly = typeof(GuaDistribution).Assembly;
    public static IReadOnlyList<string> SchemaNames { get; } = Array.AsReadOnly(Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".schema.json", StringComparison.Ordinal))
        .Select(name => name.Substring(Prefix.Length)).OrderBy(name => name, StringComparer.Ordinal).ToArray());

    public static string ReadSchema(string name)
    {
        if (!SchemaNames.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unknown packaged schema.", nameof(name));
        return ReadResource(name);
    }

    public static string License => ReadResource("LICENSE");
    public static string ViewerMetadata => ReadResource("Viewer.version.json");
    public static string ViewerLicenses => ReadResource("Viewer.LICENSES.txt");
    public static string ObserveSemanticsScript => ReadResource("trace-observe-semantics.mjs");

    /// <summary>Structural JSON Schema validation using only embedded schemas, including local $ref closure.
    /// Semantic/timing validation and engine feature negotiation remain separate contracts.</summary>
    public static bool ValidateJson(string schemaName, string json)
    {
        var schema = JsonSchema.FromText(ReadSchema(schemaName));
        var options = new EvaluationOptions();
        // No network fallback: an unresolved external reference fails locally.
        options.SchemaRegistry.Fetch = _ => throw new InvalidDataException("Schema reference is absent from the pinned package.");
        foreach (var name in SchemaNames) options.SchemaRegistry.Register(JsonSchema.FromText(ReadSchema(name)));
        return schema.Evaluate(JsonNode.Parse(json), options).IsValid;
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidDataException("Packaged resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
