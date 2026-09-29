using System.Text.Json;
using Gua.Core;
using NUnit.Framework;

namespace Gua.Selector.Tests;

[TestFixture]
public sealed class ValueTests
{
    private static JsonElement Fixture => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "value-v1.json"))).RootElement;
    public static IEnumerable<TestCaseData> Cases(string name) => Fixture.GetProperty(name).EnumerateArray().Select(c => new TestCaseData(c).SetName($"Value_{name}_{c.GetProperty("id").GetString()}"));
    public static IEnumerable<TestCaseData> Valid => Cases("valid");
    public static IEnumerable<TestCaseData> Invalid => Cases("invalid");
    public static IEnumerable<TestCaseData> Comparisons => Cases("comparisons");
    public static IEnumerable<TestCaseData> Generators => Cases("generators");
    private static GuaEnumCatalog Catalog() => GuaEnumCatalog.FromJson(Fixture.GetProperty("catalog").GetRawText());
    private static GuaValueErrorCode Code(JsonElement c) => Enum.Parse<GuaValueErrorCode>(c.GetProperty("error").GetString()!.Replace("_", ""), true);
    [TestCaseSource(nameof(Valid))]
    public void Roundtrip(JsonElement c)
    {
        using var catalog = Catalog(); using var v = GuaValue.FromJson(c.GetProperty("json").GetString()!, catalog);
        using var r = GuaValue.FromJson(v.ToJson(), catalog);
        Assert.That(v.ValueEquals(r), Is.True);
        Assert.That(r.Type, Is.EqualTo(v.Type)); Assert.That(r.ElementType, Is.EqualTo(v.ElementType));
    }
    [TestCaseSource(nameof(Invalid))]
    public void InvalidValue(JsonElement c)
    {
        using var catalog = Catalog();
        var e = Assert.Throws<GuaValueException>(() => { using var v = GuaValue.FromJson(c.GetProperty("json").GetString()!, catalog); })!;
        Assert.That(e.Code, Is.EqualTo(Code(c))); Assert.That(e.Path, Does.StartWith("$")); Assert.That(e.Message, Does.Not.Contain("DO_NOT_LOG"));
    }
    [TestCaseSource(nameof(Comparisons))]
    public void Compare(JsonElement c)
    {
        using var catalog = Catalog(); using var a = GuaValue.FromJson(c.GetProperty("left").GetString()!, catalog); using var b = GuaValue.FromJson(c.GetProperty("right").GetString()!, catalog);
        Assert.That(a.ValueEquals(b), Is.EqualTo(c.GetProperty("equal").GetBoolean()));
    }
    [TestCaseSource(nameof(Generators))]
    public void Generated(JsonElement c)
    {
        if (c.GetProperty("generate").GetString() == "BigInt")
        {
            // No BigInteger overload is exposed: its wire tag is rejected too.
            Assert.That(typeof(GuaValue).GetMethods().SelectMany(m => m.GetParameters()).Any(p => p.ParameterType == typeof(System.Numerics.BigInteger)), Is.False);
            var e = Assert.Throws<GuaValueException>(() => GuaValue.FromJson("{\"type\":\"bigint\",\"value\":1}"));
            Assert.That(e!.Code, Is.EqualTo(Code(c))); return;
        }
        double value = c.GetProperty("generate").GetString() switch { "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, _ => double.NegativeInfinity };
        Assert.That(Assert.Throws<GuaValueException>(() => GuaValue.Number(value))!.Code, Is.EqualTo(Code(c)));
    }
    [Test]
    public void TypedConstructorsOwnInputsAndRetainTypes()
    {
        using var one = GuaValue.Integer(1); using var two = GuaValue.Number(1);
        Assert.That(one.ValueEquals(two), Is.False);
        using var text = GuaValue.String("a\0😀b"); using var roundtrip = GuaValue.FromJson(text.ToJson()); Assert.That(text.ValueEquals(roundtrip), Is.True);
        using var flag = GuaValue.Bool(true); Assert.That(flag.Type, Is.EqualTo(GuaValueType.Bool));
        var source = GuaValue.String("copied"); using var list = GuaValue.Collection(GuaValueType.List, GuaValueType.String, [source]); source.Dispose();
        Assert.That(list.ToJson(), Does.Contain("copied"));
        Assert.Throws<ObjectDisposedException>(() => source.ToJson());
        Assert.That(Assert.Throws<GuaValueException>(() => GuaValue.Integer(long.MaxValue))!.Code, Is.EqualTo(GuaValueErrorCode.Range));
        Assert.That(Assert.Throws<GuaValueException>(() => GuaValue.String("\ud800"))!.Code, Is.EqualTo(GuaValueErrorCode.Unicode));
        Assert.That(Assert.Throws<GuaValueException>(() => GuaValue.Collection(GuaValueType.Set, GuaValueType.Integer, [one, one]))!.Code, Is.EqualTo(GuaValueErrorCode.Duplicate));
    }
    [Test]
    public void CatalogLookupValidatesTypeBeforeLookup()
    {
        using var catalog = new GuaEnumCatalog();
        foreach (var id in new[] { "bad", "", "game.", ".Phase", "game.1Phase" })
        {
            var error = Assert.Throws<GuaValueException>(() => catalog.GetMembers(id))!;
            Assert.That(error.Code, Is.EqualTo(GuaValueErrorCode.Structure), id);
            Assert.That(error.Path, Is.EqualTo("$.enumType"));
        }
        var unicodeError = Assert.Throws<GuaValueException>(() => catalog.GetMembers("game.\ud800"))!;
        Assert.That(unicodeError.Code, Is.EqualTo(GuaValueErrorCode.Unicode));
        Assert.That(unicodeError.Path, Is.EqualTo("$.enumType"));
        Assert.That(Assert.Throws<GuaValueException>(() => catalog.GetMembers("game.Missing"))!.Code, Is.EqualTo(GuaValueErrorCode.EnumUnknown));
    }
    [Test]
    public void CatalogRetainsCandidatesAndRejectsChanges()
    {
        using var catalog = Catalog(); var before = catalog.GetMembers("game.BossPhase");
        catalog.Register("game.BossPhase", before.Reverse().ToArray());
        Assert.That(catalog.GetMembers("game.BossPhase"), Is.EqualTo(before));
        Assert.That(Assert.Throws<GuaValueException>(() => catalog.Register("game.BossPhase", "Third"))!.Code, Is.EqualTo(GuaValueErrorCode.EnumConflict));
        Assert.That(Assert.Throws<GuaValueException>(() => catalog.Register("game.Other", "A", "A"))!.Code, Is.EqualTo(GuaValueErrorCode.Duplicate));
        using var v = GuaValue.Enum("game.BossPhase", "Second", catalog);
        using var empty = GuaValue.Collection(GuaValueType.Set, GuaValueType.Enum, [], "game.BossPhase", catalog);
        catalog.Dispose(); Assert.That(v.ToJson(), Does.Contain("Second")); Assert.That(empty.ToJson(), Does.Contain("game.BossPhase"));
    }
}
