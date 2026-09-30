using System.Text.Json;
using Gua.Core;
using NUnit.Framework;
namespace Gua.Selector.Tests;
[TestFixture]
public sealed class SpatialTests
{
    private static JsonElement Fixture => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "spatial-r1.json"))).RootElement;
    private static GuaSpatialDocumentType Type(JsonElement c) => Enum.Parse<GuaSpatialDocumentType>(c.GetProperty("type").GetString()!, true);
    public static IEnumerable<TestCaseData> Cases(string name) => Fixture.GetProperty(name).EnumerateArray().Select(c => new TestCaseData(c).SetName($"Spatial_{name}_{c.GetProperty("id").GetString()}"));
    public static IEnumerable<TestCaseData> Valid => Cases("valid");
    public static IEnumerable<TestCaseData> Invalid => Cases("invalid");
    public static IEnumerable<TestCaseData> RequestChecks => Cases("requestChecks");
    public static IEnumerable<TestCaseData> ResultChecks => Cases("resultChecks");
    [TestCaseSource(nameof(Valid))]
    public void Roundtrip(JsonElement c)
    {
        using var doc = GuaSpatialDocument.FromJson(Type(c), c.GetProperty("json").GetString()!);
        using var again = GuaSpatialDocument.FromJson(doc.Type, doc.ToJson());
        Assert.That(again.ToJson(), Is.EqualTo(doc.ToJson()));
        using var typed = doc.Type switch {
            GuaSpatialDocumentType.Request => GuaSpatialDocument.FromRequest(doc.ReadRequest()),
            GuaSpatialDocumentType.Result => GuaSpatialDocument.FromResult(doc.ReadResult()),
            _ => GuaSpatialDocument.FromProvider(doc.ReadProvider()) };
        using var original = JsonDocument.Parse(doc.ToJson()); using var copied = JsonDocument.Parse(typed.ToJson());
        SameFacts(original.RootElement, copied.RootElement);
    }
    private static void SameFacts(JsonElement left, JsonElement right)
    {
        Assert.That(right.ValueKind, Is.EqualTo(left.ValueKind));
        if (left.ValueKind == JsonValueKind.Number) Assert.That(right.GetDouble(), Is.EqualTo(left.GetDouble()));
        else if (left.ValueKind == JsonValueKind.Object) {
            Assert.That(right.EnumerateObject().Count(), Is.EqualTo(left.EnumerateObject().Count()));
            foreach (var field in left.EnumerateObject()) SameFacts(field.Value, right.GetProperty(field.Name));
        } else if (left.ValueKind == JsonValueKind.Array) {
            Assert.That(right.GetArrayLength(), Is.EqualTo(left.GetArrayLength()));
            for (int i = 0; i < left.GetArrayLength(); ++i) SameFacts(left[i], right[i]);
        } else if (left.ValueKind == JsonValueKind.String) Assert.That(right.GetString(), Is.EqualTo(left.GetString()));
        else Assert.That(right.GetRawText(), Is.EqualTo(left.GetRawText()));
    }
    [TestCaseSource(nameof(Invalid))]
    public void Reject(JsonElement c)
    {
        var e = Assert.Throws<GuaSpatialException>(() => { using var d = GuaSpatialDocument.FromJson(Type(c), c.GetProperty("json").GetString()!); })!;
        Assert.That((int)e.Code, Is.EqualTo(c.GetProperty("error").GetInt32()));
        Assert.That(e.Path, Does.StartWith("$"));
    }
    private static void Expect(Action action, JsonElement c)
    {
        int code = c.GetProperty("error").GetInt32();
        if (code == 0) Assert.DoesNotThrow(() => action());
        else Assert.That((int)Assert.Throws<GuaSpatialException>(() => action())!.Code, Is.EqualTo(code));
    }
    [TestCaseSource(nameof(RequestChecks))]
    public void ProviderMatch(JsonElement c)
    {
        using var request = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Request, c.GetProperty("request").GetString()!);
        using var provider = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Provider, c.GetProperty("provider").GetString()!);
        Expect(() => request.CheckRequest(provider), c);
    }
    [TestCaseSource(nameof(ResultChecks))]
    public void ResultMatch(JsonElement c)
    {
        using var request = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Request, c.GetProperty("request").GetString()!);
        using var result = GuaSpatialDocument.FromJson(GuaSpatialDocumentType.Result, c.GetProperty("result").GetString()!);
        Expect(() => request.CheckResult(result), c);
    }
    [Test]
    public void DisposedDocumentCannotCrossAbi()
    {
        var c = Fixture.GetProperty("valid")[0]; var d = GuaSpatialDocument.FromJson(Type(c), c.GetProperty("json").GetString()!); d.Dispose();
        Assert.Throws<ObjectDisposedException>(() => d.ToJson());
    }
    [TestCase(double.NaN)] [TestCase(double.PositiveInfinity)] [TestCase(double.NegativeInfinity)]
    public void NonFiniteAuthoringRejected(double number)
    {
        var c = Fixture.GetProperty("valid")[0]; using var d = GuaSpatialDocument.FromJson(Type(c), c.GetProperty("json").GetString()!);
        var request = d.ReadRequest(); request.Shape!.Radius = number;
        Assert.Throws<ArgumentException>(() => { using var invalid = GuaSpatialDocument.FromRequest(request); });
    }
}
