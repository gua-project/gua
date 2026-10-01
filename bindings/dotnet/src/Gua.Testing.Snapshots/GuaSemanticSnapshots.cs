using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Gua.Core;

namespace Gua.Testing.Snapshots;

public enum SemanticSnapshotRuleAction { Mask, Ignore }

/// <summary>JSON Pointer pattern, with * matching one segment. ItemId optionally selects a UI node or World object.</summary>
public sealed record SemanticSnapshotRule(string Path, SemanticSnapshotRuleAction Action, string? ItemId = null);

public sealed class SemanticSnapshotOptions
{
    public string BaselineDirectory { get; init; } = "baselines/semantic";
    public string ArtifactDirectory { get; init; } = "artifacts/gua/semantic";
    public string BaselineVariant { get; init; } = "default";
    public bool UpdateBaselines { get; init; }
    public bool IncludeRuntimeMetadata { get; init; }
    public bool IncludeGeometry { get; init; }
    public Func<string>? WorldTreeProvider { get; init; }
    public IReadOnlyList<SemanticSnapshotRule> Rules { get; init; } = Array.Empty<SemanticSnapshotRule>();
    /// <summary>Optional capture of this explicit comparison, using only normalized in-memory data.</summary>
    public GuaTraceSession? Trace { get; init; }
    public string? TraceStepId { get; init; }
    /// <summary>Caller-authorized profile of the supplied context/World getter. Does not project or elevate data.</summary>
    public GuaObservationProfile TraceProfile { get; init; } = GuaObservationProfile.Debug;
}

public enum SemanticDifferenceKind { Added, Removed, Changed, Order }
public sealed record SemanticSnapshotDifference(SemanticDifferenceKind Kind, string Path, string? ExpectedJson, string? ActualJson);
public sealed record SemanticSnapshotComparisonResult(bool Matched, bool BaselineUpdated, string Reason,
    string BaselinePath, string ArtifactPath, string RunId, IReadOnlyList<SemanticSnapshotDifference> Differences);

public sealed class SemanticSnapshotAssertionException : InvalidOperationException
{
    public SemanticSnapshotComparisonResult Result { get; }
    public SemanticSnapshotAssertionException(SemanticSnapshotComparisonResult result)
        : base($"Semantic snapshot {result.Reason}. Baseline: {result.BaselinePath}. Artifacts: {result.ArtifactPath}. " +
               "To approve a baseline, explicitly set UpdateBaselines=true or GUA_UPDATE_SEMANTIC_BASELINES=1.") => Result = result;
}

public static class GuaSemanticSnapshots
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static SemanticSnapshotComparisonResult ExpectSnapshot(IGuaContext context, string name, SemanticSnapshotOptions? options = null)
    {
        var result = CompareSnapshot(context, name, options);
        if (!result.Matched) throw new SemanticSnapshotAssertionException(result);
        return result;
    }

    public static SemanticSnapshotComparisonResult CompareSnapshot(IGuaContext context, string name, SemanticSnapshotOptions? options = null)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        options ??= new();
        Validate(name, options);
        var actual = Normalize(context.GetUiTreeJson(), options.WorldTreeProvider?.Invoke(), options);
        var baseline = System.IO.Path.GetFullPath(System.IO.Path.Combine(options.BaselineDirectory, Key(name), Key(options.BaselineVariant) + ".json"));
        var runId = Guid.NewGuid().ToString("N");
        var artifact = System.IO.Path.GetFullPath(System.IO.Path.Combine(options.ArtifactDirectory, Key(name), Key(options.BaselineVariant), runId));
        Directory.CreateDirectory(artifact);
        var update = options.UpdateBaselines || Environment.GetEnvironmentVariable("GUA_UPDATE_SEMANTIC_BASELINES") == "1";
        JsonNode? expected = null;
        var differences = new List<SemanticSnapshotDifference>();
        var reason = "baseline_missing";
        // Cross-process coordination also avoids Windows replacement failures while another test reads.
        using var gate = new Mutex(false, "gua-semantic-" + Key(baseline.ToUpperInvariant()));
        try { gate.WaitOne(); } catch (AbandonedMutexException) { /* Ownership was granted; atomic files remain readable. */ }
        try
        {
            if (File.Exists(baseline))
            {
                // Reapply today's rules to both sides; never copy the raw baseline into artifacts.
                var stored = JsonNode.Parse(File.ReadAllText(baseline)) as JsonObject ?? throw new InvalidDataException("Invalid semantic baseline document.");
                if (stored["snapshotVersion"]?.GetValue<int>() != 1)
                    throw new InvalidDataException("Unsupported semantic baseline version.");
                expected = Transform(stored, "", null, options);
                Diff(expected, actual, "", differences);
                reason = differences.Count == 0 ? "matched" : "semantic_difference";
            }
            if (update)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(baseline)!);
                // Unique temporary files plus same-directory atomic replacement prevent partial reads under parallel approval.
                var temporary = baseline + "." + runId + ".tmp";
                try
                {
                    File.WriteAllText(temporary, Serialize(actual));
                    if (File.Exists(baseline)) File.Replace(temporary, baseline, null);
                    else File.Move(temporary, baseline);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                reason = "baseline_updated";
            }
        }
        finally { gate.ReleaseMutex(); }
        var result = new SemanticSnapshotComparisonResult(update || reason == "matched", update, reason, baseline, artifact, runId, differences.AsReadOnly());
        File.WriteAllText(System.IO.Path.Combine(artifact, "actual.json"), Serialize(actual));
        if (expected is not null) File.WriteAllText(System.IO.Path.Combine(artifact, "expected.json"), Serialize(expected));
        File.WriteAllText(System.IO.Path.Combine(artifact, "diff.json"), JsonSerializer.Serialize(differences, JsonOptions));
        // Do not retain raw runtime metadata or rule selectors that might contain sensitive values.
        File.WriteAllText(System.IO.Path.Combine(artifact, "comparison.json"), JsonSerializer.Serialize(result, JsonOptions));
        if (options.Trace is { } trace)
            GuaTraceCapture.JsonAttachment(trace, options.TraceStepId ?? "", "gua.semantic-comparison.v1", () =>
                // Do not import files or include machine paths. Snapshot masking rules have already run.
                JsonSerializer.Serialize(new { result.Matched, result.BaselineUpdated, result.Reason, result.RunId,
                    differences = result.Differences.Select(d => new { d.Kind, d.Path,
                        expected = d.ExpectedJson is null ? null : JsonNode.Parse(d.ExpectedJson),
                        actual = d.ActualJson is null ? null : JsonNode.Parse(d.ActualJson) }),
                    actual, expected }, JsonOptions), options.TraceProfile);
        return result;
    }

    public static string NormalizeJson(string uiTreeJson, string? worldTreeJson = null, SemanticSnapshotOptions? options = null)
    {
        options ??= new(); Validate("snapshot", options);
        return Serialize(Normalize(uiTreeJson, worldTreeJson, options));
    }

    private static JsonNode Normalize(string ui, string? world, SemanticSnapshotOptions options)
    {
        var root = new JsonObject { ["snapshotVersion"] = 1, ["ui"] = ParseTree(ui, "nodes", "screen") };
        if (world is not null) root["world"] = ParseTree(world, "objects", "scene");
        return Transform(root, "", null, options)!;
    }

    private static JsonObject ParseTree(string json, string collection, string scene)
    {
        var tree = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Snapshot tree must be an object.");
        if (tree[collection] is not JsonArray items || tree[scene] is not JsonValue sceneValue || !sceneValue.TryGetValue<string>(out _))
            throw new InvalidDataException("Snapshot tree requires its semantic collection and screen/scene.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not JsonObject obj || obj["id"] is not JsonValue id || !id.TryGetValue<string>(out var value) || string.IsNullOrEmpty(value) || !ids.Add(value))
                throw new InvalidDataException("Snapshot items require unique, nonempty string IDs.");
        }
        return tree;
    }

    private static JsonNode? Transform(JsonNode? node, string path, string? itemId, SemanticSnapshotOptions options)
    {
        if (node is JsonObject obj)
        {
            if (IsItem(path)) itemId = obj["id"]?.GetValue<string>();
            var result = new JsonObject();
            foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var childPath = path + "/" + Escape(property.Key);
                if (Excluded(path, property.Key, options) || Rule(childPath, itemId, options, SemanticSnapshotRuleAction.Ignore)) continue;
                result[property.Key] = Rule(childPath, itemId, options, SemanticSnapshotRuleAction.Mask)
                    ? JsonValue.Create("[MASKED]") : Transform(property.Value, childPath, itemId, options);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            // Index-based removal changes subsequent positions in an approved document. Stable
            // selector rules use wildcard + ItemId so applying them again remains idempotent.
            var parentSegments = path.Split('/').Length;
            foreach (var rule in options.Rules)
            {
                var segments = rule.Path.Split('/');
                if (segments.Length > parentSegments && Matches(string.Join("/", segments.Take(parentSegments)), path) && segments[parentSegments] != "*")
                    throw new ArgumentException("Rules targeting array children must use '*' and optional ItemId; numeric array indices cannot be persisted safely.");
            }
            var result = new JsonArray();
            for (var i = 0; i < array.Count; i++)
            {
                var childPath = path + "/" + i.ToString(CultureInfo.InvariantCulture);
                var childId = IsItem(childPath) && array[i] is JsonObject child ? child["id"]?.GetValue<string>() : itemId;
                if (Rule(childPath, childId, options, SemanticSnapshotRuleAction.Ignore)) continue;
                result.Add(Rule(childPath, childId, options, SemanticSnapshotRuleAction.Mask)
                    ? JsonValue.Create("[MASKED]") : Transform(array[i], childPath, childId, options));
            }
            return result;
        }
        if (node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number)
        {
            // Canonicalize the decimal token itself, without floating-point or decimal rounding.
            var raw = scalar.ToJsonString().ToLowerInvariant();
            var parts = raw.Split('e');
            var exponent = parts.Length == 2 ? BigInteger.Parse(parts[1], CultureInfo.InvariantCulture) : BigInteger.Zero;
            var negative = parts[0].StartsWith("-", StringComparison.Ordinal);
            var mantissa = parts[0].TrimStart('-');
            var dot = mantissa.IndexOf('.');
            if (dot >= 0) exponent -= mantissa.Length - dot - 1;
            var digits = mantissa.Replace(".", "").TrimStart('0');
            if (digits.Length == 0) return JsonNode.Parse("0");
            var trimmed = digits.TrimEnd('0'); exponent += digits.Length - trimmed.Length;
            return JsonNode.Parse((negative ? "-" : "") + trimmed + (exponent.IsZero ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture)));
        }
        return node?.DeepClone();
    }

    private static bool Excluded(string path, string key, SemanticSnapshotOptions options) =>
        (!options.IncludeRuntimeMetadata && (path == "/ui" || path == "/world") && key is "sessionEpoch" or "frameSequence" or "revision") ||
        (!options.IncludeGeometry && IsItem(path) && key is "bounds" or "position");
    private static bool IsItem(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 4 && ((parts[1] == "ui" && parts[2] == "nodes") || (parts[1] == "world" && parts[2] == "objects"));
    }
    private static bool Rule(string path, string? id, SemanticSnapshotOptions options, SemanticSnapshotRuleAction action) => options.Rules.Any(rule =>
        rule.Action == action && (rule.ItemId is null || rule.ItemId == id) && Matches(rule.Path, path));
    private static bool Matches(string pattern, string path)
    {
        var a = pattern.Split('/'); var b = path.Split('/');
        return a.Length == b.Length && a.Zip(b, (x, y) => x == "*" || x == y).All(x => x);
    }

    private static void Diff(JsonNode? expected, JsonNode? actual, string path, List<SemanticSnapshotDifference> result)
    {
        if (JsonNode.DeepEquals(expected, actual)) return;
        if (expected is JsonObject e && actual is JsonObject a)
        {
            foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)).OrderBy(x => x, StringComparer.Ordinal))
            {
                var child = path + "/" + Escape(key);
                if (!e.ContainsKey(key)) Add(SemanticDifferenceKind.Added, child, null, a[key]);
                else if (!a.ContainsKey(key)) Add(SemanticDifferenceKind.Removed, child, e[key], null);
                else Diff(e[key], a[key], child, result);
            }
            return;
        }
        if (expected is JsonArray ea && actual is JsonArray aa && (path == "/ui/nodes" || path == "/world/objects") && HasIds(ea) && HasIds(aa))
        {
            var em = ea.ToDictionary(n => n!["id"]!.GetValue<string>(), n => n, StringComparer.Ordinal);
            var am = aa.ToDictionary(n => n!["id"]!.GetValue<string>(), n => n, StringComparer.Ordinal);
            foreach (var id in em.Keys.Union(am.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                var child = path + "/@" + Escape(id);
                if (!em.ContainsKey(id)) Add(SemanticDifferenceKind.Added, child, null, am[id]);
                else if (!am.ContainsKey(id)) Add(SemanticDifferenceKind.Removed, child, em[id], null);
                else Diff(em[id], am[id], child, result);
            }
            var eo = ea.Select(n => n!["id"]!.GetValue<string>()).Where(am.ContainsKey).ToArray();
            var ao = aa.Select(n => n!["id"]!.GetValue<string>()).Where(em.ContainsKey).ToArray();
            if (!eo.SequenceEqual(ao)) Add(SemanticDifferenceKind.Order, path, JsonSerializer.SerializeToNode(eo), JsonSerializer.SerializeToNode(ao));
            return;
        }
        if (expected is JsonArray el && actual is JsonArray al && el.Count == al.Count &&
            el.Select(Serialize).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(al.Select(Serialize).OrderBy(x => x, StringComparer.Ordinal)))
            Add(SemanticDifferenceKind.Order, path, expected, actual);
        else Add(SemanticDifferenceKind.Changed, path, expected, actual);
        void Add(SemanticDifferenceKind kind, string where, JsonNode? before, JsonNode? after) => result.Add(new(kind, where, before?.ToJsonString(), after?.ToJsonString()));
    }
    private static bool HasIds(JsonArray items) => items.All(n => n is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out _)) &&
        items.Select(n => n!["id"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == items.Count;
    private static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
    private static bool ValidPointerEscapes(string path)
    {
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '~' && (++i >= path.Length || path[i] is not ('0' or '1'))) return false;
        return true;
    }
    private static string Serialize(JsonNode? node) => node?.ToJsonString(JsonOptions) + "\n";
    private static string Key(string value)
    {
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        var label = new string(value.Take(40).Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' ? c : '_').ToArray());
        return "s-" + label + "-" + hash;
    }
    private static void Validate(string name, SemanticSnapshotOptions options)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(options.BaselineVariant) || string.IsNullOrWhiteSpace(options.BaselineDirectory) || string.IsNullOrWhiteSpace(options.ArtifactDirectory))
            throw new ArgumentException("Name, variant and directories are required.");
        if (options.Rules is null || options.Rules.Any(r => r is null || string.IsNullOrEmpty(r.Path) || !r.Path.StartsWith("/", StringComparison.Ordinal) || !ValidPointerEscapes(r.Path) || !Enum.IsDefined(typeof(SemanticSnapshotRuleAction), r.Action)))
            throw new ArgumentException("Rules require JSON Pointer paths and valid actions.");
        if (options.Rules.Any(r => Matches(r.Path, "/snapshotVersion")))
            throw new ArgumentException("Rules cannot replace the baseline envelope version.");
        // Stable identity is needed to apply selector rules again when reading an approved baseline.
        if (options.Rules.Any(r =>
        {
            var p = r.Path.Split('/');
            return p.Length == 5 && (p[1] is "ui" or "world" or "*") && (p[2] is "nodes" or "objects" or "*") && (p[4] is "id" or "*");
        }))
            throw new ArgumentException("Use Ignore on an entire item to exclude identity; rules cannot replace item IDs.");
    }
}
