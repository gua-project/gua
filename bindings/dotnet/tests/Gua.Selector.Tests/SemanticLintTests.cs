using System.Text.Json;
using Gua.Core;
using NUnit.Framework;
namespace Gua.Selector.Tests;
public class SemanticLintTests
{
    [Test]
    public void SharedFixturesUseNativeRulesAndTypedReports() {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "semantic-lint-v1.json")));
        foreach (var fixture in document.RootElement.GetProperty("cases").EnumerateArray()) {
            var ui = fixture.GetProperty("uiTree").GetRawText(); var world = fixture.GetProperty("worldObjectTree").GetRawText();
            var expected = fixture.GetProperty("expected").EnumerateArray().Select(x => x.GetString()).ToHashSet();
            var debug = GuaSemanticLinter.Analyze(ui, world);
            var player = GuaSemanticLinter.Analyze(ui, world, new(GuaObservationProfile.Player));
            Assert.That(debug.Findings.Select(f => f.RuleId).ToHashSet(), Is.EquivalentTo(expected));
            Assert.That(player.Findings, Is.EqualTo(debug.Findings));
            Assert.That(debug.Summary.Total, Is.EqualTo(debug.Findings.Count));
            Assert.That(GuaSemanticLintReport.Parse(debug.ToJson()).Findings, Is.EqualTo(debug.Findings));
            using var serialized = JsonDocument.Parse(debug.ToJson());
            Assert.That(serialized.RootElement.GetProperty("uiTree").TryGetProperty("scene", out _), Is.False);
            Assert.That(serialized.RootElement.GetProperty("worldObjectTree").TryGetProperty("screen", out _), Is.False);
        }
    }
    [Test]
    public void LintReadsPublishedProjectionAndDoesNotPublishStaging() {
        using var context = new GuaContext();
        context.BeginFrame("fixture");
        context.RegisterNode("safe", "button", "Safe", new(0,0,1,1));
        context.RegisterNode("secret", "button", "", new(0,0,1,1), visible:false);
        context.EndFrame();
        var debug = GuaSemanticLinter.Analyze(context);
        var player = GuaSemanticLinter.Analyze(context, new(GuaObservationProfile.Player));
        Assert.That(debug.Findings.Single().TargetId, Is.EqualTo("secret"));
        Assert.That(player.Findings, Is.Empty);
        Assert.That(GuaSemanticLinter.Analyze(context, new(IncludeWorld:false)).ToJson(), Does.Contain("\"worldObjectTree\":null"));
        var tree = context.GetUiTreeJson();
        context.BeginFrame("staging");
        context.RegisterNode("staging", "button", "", new(0,0,1,1));
        Assert.That(GuaSemanticLinter.Analyze(context).ToJson(), Is.EqualTo(debug.ToJson()));
        Assert.That(context.GetUiTreeJson(), Is.EqualTo(tree));
    }
}
