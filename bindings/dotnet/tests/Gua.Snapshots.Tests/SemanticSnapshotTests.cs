using System.Text.Json;
using System.Text.Json.Nodes;
using Gua.Core;
using Gua.Testing.Snapshots;
using NUnit.Framework;

namespace Gua.Snapshots.Tests;

[NonParallelizable] // Environment-variable approval tests share process state.
public class SemanticSnapshotTests
{
    private string _root = null!;
    private string? _previousUpdate;
    private string? _previousVisualUpdate;
    [SetUp] public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "gua-semantic-tests", Guid.NewGuid().ToString("N"));
        _previousUpdate = Environment.GetEnvironmentVariable("GUA_UPDATE_SEMANTIC_BASELINES");
        _previousVisualUpdate = Environment.GetEnvironmentVariable("GUA_UPDATE_BASELINES");
        Environment.SetEnvironmentVariable("GUA_UPDATE_SEMANTIC_BASELINES", null);
        Environment.SetEnvironmentVariable("GUA_UPDATE_BASELINES", null);
    }
    [TearDown] public void Cleanup()
    {
        Environment.SetEnvironmentVariable("GUA_UPDATE_SEMANTIC_BASELINES", _previousUpdate);
        Environment.SetEnvironmentVariable("GUA_UPDATE_BASELINES", _previousVisualUpdate);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private SemanticSnapshotOptions Options(bool update = false, string variant = "default", IReadOnlyList<SemanticSnapshotRule>? rules = null,
        Func<string>? world = null, bool metadata = false, bool geometry = false) => new()
    {
        BaselineDirectory = Path.Combine(_root, "baselines"), ArtifactDirectory = Path.Combine(_root, "artifacts"),
        UpdateBaselines = update, BaselineVariant = variant, Rules = rules ?? [], WorldTreeProvider = world,
        IncludeRuntimeMetadata = metadata, IncludeGeometry = geometry
    };
    private static JsonObject Node(string id, string text = "hello") => new()
    {
        ["id"] = id, ["role"] = "button", ["label"] = "Save", ["text"] = text, ["value"] = 1,
        ["visible"] = true, ["enabled"] = true, ["actions"] = new JsonArray("click", "focus"),
        ["bounds"] = new JsonObject { ["x"] = 1, ["y"] = 2, ["w"] = 3, ["h"] = 4 },
        ["state"] = new JsonObject { ["focused"] = false, ["rangeValue"] = 1 }
    };
    private static JsonObject Ui(params JsonObject[] nodes) => new()
    {
        ["schemaVersion"] = 2, ["screen"] = "settings", ["sessionEpoch"] = 1, ["frameSequence"] = 2, ["revision"] = 3,
        ["nodes"] = new JsonArray(nodes.Cast<JsonNode?>().ToArray())
    };
    private static JsonObject World(params string[] ids) => new()
    {
        ["schemaVersion"] = 1, ["scene"] = "arena", ["sessionEpoch"] = 1, ["frameSequence"] = 2, ["revision"] = 3,
        ["objects"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id, ["kind"] = "enemy", ["space"] = "world2d", ["position"] = new JsonObject { ["x"] = 1 },
            ["active"] = true, ["visibleToPlayer"] = true, ["agentExposure"] = "auto", ["state"] = new JsonObject { ["health"] = 10 }
        }).ToArray())
    };
    private SemanticSnapshotComparisonResult Compare(JsonObject ui, SemanticSnapshotOptions? options = null, string name = "test") =>
        GuaSemanticSnapshots.CompareSnapshot(new FakeContext(ui.ToJsonString()), name, options ?? Options());

    [Test] public void MissingFailsAndProvidesExplicitApprovalAndArtifacts()
    {
        var error = Assert.Throws<SemanticSnapshotAssertionException>(() => GuaSemanticSnapshots.ExpectSnapshot(new FakeContext(Ui(Node("a")).ToJsonString()), "test", Options()))!;
        Assert.Multiple(() =>
        {
            Assert.That(error.Result.Matched, Is.False);
            Assert.That(error.Result.Reason, Is.EqualTo("baseline_missing"));
            Assert.That(File.Exists(error.Result.BaselinePath), Is.False);
            Assert.That(error.Message, Does.Contain("GUA_UPDATE_SEMANTIC_BASELINES=1").And.Contain(error.Result.ArtifactPath));
            Assert.That(Directory.GetFiles(error.Result.ArtifactPath).Select(Path.GetFileName), Is.EquivalentTo(new[] { "actual.json", "diff.json", "comparison.json" }));
        });
    }

    [TestCase(null, false)] [TestCase("0", false)] [TestCase("true", false)] [TestCase("1", true)]
    public void OnlyExplicitSemanticEnvironmentApproves(string? value, bool approved)
    {
        Environment.SetEnvironmentVariable("GUA_UPDATE_BASELINES", "1");
        Environment.SetEnvironmentVariable("GUA_UPDATE_SEMANTIC_BASELINES", value);
        var result = Compare(Ui(Node("a")));
        Assert.That(result.BaselineUpdated, Is.EqualTo(approved));
        Assert.That(File.Exists(result.BaselinePath), Is.EqualTo(approved));
    }

    [Test] public void MetadataGeometryPropertyOrderAndNumericSpellingsAreDeterministic()
    {
        var ui = Ui(Node("a")); var world = World("w");
        var baseline = Compare(ui, Options(true, world: () => world.ToJsonString()));
        ui["sessionEpoch"] = 99; ui["frameSequence"] = 999; ui["revision"] = 999;
        ui["nodes"]![0]!["bounds"]!["x"] = 900;
        world["objects"]![0]!["position"]!["x"] = 900; world["sessionEpoch"] = 99;
        var reversed = new JsonObject(ui.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
        var text = reversed.ToJsonString().Replace("\"value\":1", "\"value\":1.00");
        var result = GuaSemanticSnapshots.CompareSnapshot(new FakeContext(text), "test", Options(world: () => world.ToJsonString()));
        Assert.That(result.Matched, Is.True);
        Assert.That(File.ReadAllText(baseline.BaselinePath), Is.EqualTo(File.ReadAllText(Path.Combine(result.ArtifactPath, "actual.json"))));
        Assert.That(Compare(ui, Options(world: () => world.ToJsonString(), metadata: true, geometry: true)).Matched, Is.False);
    }

    [Test] public void NumbersNeverRoundSmallOrLargeValuesAway()
    {
        var ui = Ui(Node("a")); ui["nodes"]![0]!["value"] = JsonNode.Parse("1e-100");
        Compare(ui, Options(true)); ui["nodes"]![0]!["value"] = JsonNode.Parse("2e-100");
        Assert.That(Compare(ui).Matched, Is.False);
        ui["nodes"]![0]!["value"] = JsonNode.Parse("123456789012345678901234567890123456789"); Compare(ui, Options(true));
        ui["nodes"]![0]!["value"] = JsonNode.Parse("123456789012345678901234567890123456788");
        Assert.That(Compare(ui).Matched, Is.False);
    }

    [Test] public void AddedRemovedChangedAndRelativeOrderAreSeparate()
    {
        Compare(Ui(Node("a"), Node("b"), Node("c")), Options(true));
        var result = Compare(Ui(Node("c", "changed"), Node("d"), Node("a")));
        Assert.Multiple(() =>
        {
            Assert.That(result.Differences.Any(d => d.Kind == SemanticDifferenceKind.Added && d.Path == "/ui/nodes/@d"), Is.True);
            Assert.That(result.Differences.Any(d => d.Kind == SemanticDifferenceKind.Removed && d.Path == "/ui/nodes/@b"), Is.True);
            Assert.That(result.Differences.Any(d => d.Kind == SemanticDifferenceKind.Changed && d.Path == "/ui/nodes/@c/text"), Is.True);
            Assert.That(result.Differences.Any(d => d.Kind == SemanticDifferenceKind.Order), Is.True);
        });
        Assert.That(Compare(Ui(Node("d"), Node("a"), Node("b"), Node("c"))).Differences.Any(d => d.Kind == SemanticDifferenceKind.Order), Is.False);
    }

    [TestCase("role")] [TestCase("label")] [TestCase("text")] [TestCase("value")] [TestCase("parentId")]
    [TestCase("visible")] [TestCase("enabled")] [TestCase("state")] [TestCase("actions")] [TestCase("screen")]
    public void SemanticFieldsRemainSignificant(string field)
    {
        var ui = Ui(Node("a")); Compare(ui, Options(true));
        if (field == "screen") ui[field] = "other";
        else ui["nodes"]![0]![field] = field switch
        {
            "visible" or "enabled" => JsonValue.Create(false), "state" => new JsonObject { ["focused"] = true },
            "actions" => new JsonArray("focus", "click"), _ => JsonValue.Create("changed")
        };
        Assert.That(Compare(ui).Matched, Is.False);
    }

    [Test] public void WorldIsOptionalAndAllSemanticChangesAreReported()
    {
        var ui = Ui(Node("a")); var world = World("a", "b", "c");
        Compare(ui, Options(true, world: () => world.ToJsonString()));
        var changed = World("c", "d", "a"); changed["objects"]![0]!["active"] = false;
        changed["objects"]![0]!["state"]!["revision"] = 10;
        var result = Compare(ui, Options(world: () => changed.ToJsonString()));
        Assert.That(result.Differences.Select(d => d.Kind).Distinct(), Is.EquivalentTo(Enum.GetValues<SemanticDifferenceKind>()));
        Assert.That(result.Differences.Any(d => d.Path.EndsWith("/state/revision")), Is.True);
        Assert.That(Compare(ui).Differences.Any(d => d.Kind == SemanticDifferenceKind.Removed && d.Path == "/world"), Is.True);
    }

    [Test] public void MasksAndIgnoresApplyToExistingExpectedActualDiffAndApproval()
    {
        var ui = Ui(Node("a", "old-secret")); var world = World("w"); world["objects"]![0]!["state"]!["token/~"] = "old-token";
        var stored = Compare(ui, Options(true, world: () => world.ToJsonString()));
        var rules = new SemanticSnapshotRule[]
        {
            new("/ui/nodes/*/text", SemanticSnapshotRuleAction.Mask, "a"),
            new("/world/objects/*/state/token~1~0", SemanticSnapshotRuleAction.Ignore, "w")
        };
        ui["nodes"]![0]!["text"] = "new-secret"; world["objects"]![0]!["state"]!["token/~"] = "new-token";
        ui["nodes"]![0]!["enabled"] = false;
        var result = Compare(ui, Options(rules: rules, world: () => world.ToJsonString()));
        Assert.That(result.Matched, Is.False);
        foreach (var file in Directory.GetFiles(result.ArtifactPath))
            Assert.That(File.ReadAllText(file), Does.Not.Contain("old-secret").And.Not.Contain("new-secret").And.Not.Contain("old-token").And.Not.Contain("new-token"));
        Assert.That(File.ReadAllText(stored.BaselinePath), Does.Contain("old-secret")); // No implicit rewrite.
        Compare(ui, Options(true, rules: rules, world: () => world.ToJsonString()));
        Assert.That(File.ReadAllText(stored.BaselinePath), Does.Not.Contain("secret").And.Not.Contain("token"));
        Assert.That(Compare(ui, Options(rules: rules, world: () => world.ToJsonString())).Matched, Is.True);
    }

    [Test] public void IgnoreItemDoesNotAffectRemainingOrderAndMissingMaskedFieldsRemainDifferent()
    {
        var rules = new SemanticSnapshotRule[] { new("/ui/nodes/*", SemanticSnapshotRuleAction.Ignore, "a"), new("/ui/nodes/*/text", SemanticSnapshotRuleAction.Mask) };
        Compare(Ui(Node("a"), Node("b")), Options(true, rules: rules));
        Assert.That(Compare(Ui(Node("b"), Node("a", "changed")), Options(rules: rules)).Matched, Is.True);
        var ui = Ui(Node("b")); ui["nodes"]![0]!.AsObject().Remove("text");
        Assert.That(Compare(ui, Options(rules: rules)).Matched, Is.False);
    }

    [Test] public async Task ConcurrentWritersAndReadersHaveCompleteBaselinesAndUniqueArtifacts()
    {
        var ui = Ui(Node("a")); Compare(ui, Options(true));
        var json = ui.ToJsonString();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            GuaSemanticSnapshots.CompareSnapshot(new FakeContext(json), "test", Options(update: i % 2 == 0)))));
        Assert.That(results.All(r => r.Matched), Is.True);
        Assert.That(results.Select(r => r.ArtifactPath).Distinct().Count(), Is.EqualTo(32));
        foreach (var r in results) JsonDocument.Parse(File.ReadAllText(Path.Combine(r.ArtifactPath, "actual.json"))).Dispose();
        Assert.That(Directory.GetFiles(Path.Combine(_root, "baselines"), "*.tmp", SearchOption.AllDirectories), Is.Empty);
    }

    [Test] public void NamesAndVariantsAvoidTraversalAndSanitizationCollisions()
    {
        var a = Compare(Ui(), Options(true, variant: "../CON"), "../a/b");
        var b = Compare(Ui(), Options(true, variant: ".._CON"), ".._a_b");
        Assert.That(a.BaselinePath, Is.Not.EqualTo(b.BaselinePath));
        Assert.That(a.BaselinePath, Does.StartWith(Path.Combine(_root, "baselines") + Path.DirectorySeparatorChar));
        Assert.That(Compare(Ui(), Options(variant: "other"), "../a/b").Reason, Is.EqualTo("baseline_missing"));
    }

    [Test] public void InvalidTreesAndIdentityRulesFailBeforeWritingBaseline()
    {
        Assert.Throws<InvalidDataException>(() => Compare(Ui(Node("a"), Node("a")), Options(true)));
        Assert.Throws<InvalidDataException>(() => GuaSemanticSnapshots.NormalizeJson("{}"));
        Assert.Throws<ArgumentException>(() => Compare(Ui(Node("a")), Options(true, rules: [new("/ui/nodes/1/id", SemanticSnapshotRuleAction.Mask)])));
        Assert.That(Directory.Exists(_root), Is.False);
    }

    private sealed class FakeContext(string json) : IGuaContext
    {
        public string GetUiTreeJson() => json;
        public GuaNodeState GetNodeState(string id) => throw new NotSupportedException();
        public string FindNodeById(string id) => throw new NotSupportedException();
        public string FindNodeByRole(string role, string? name = null) => throw new NotSupportedException();
        public string FindNodeByText(string text) => throw new NotSupportedException();
        public bool EnqueueClick(string id) => throw new NotSupportedException();
        public GuaActionError EnqueueAction(GuaActionRequest request, out ulong requestId) => throw new NotSupportedException();
        public bool TryPollActionEvent(out GuaActionEvent e) => throw new NotSupportedException();
        public bool TryPollActionEvent(ulong id, out GuaActionEvent e) => throw new NotSupportedException();
        public bool TryPollEvent(out GuaEvent e) => throw new NotSupportedException();
    }
}
