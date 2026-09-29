using System.Text.Json;
using Gua.Core;
using NUnit.Framework;
namespace Gua.Selector.Tests;
[TestFixture]
public sealed class ObserveTests
{
    private static JsonElement Parse(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    private static JsonElement[] Entries(GuaContext c) => Parse(c.GetObserveSnapshotJson()).GetProperty("entries").EnumerateArray().ToArray();
    private static JsonElement[] Events(GuaObserveSubscription s) => Parse(s.PollJson()).GetProperty("events").EnumerateArray().ToArray();
    private static void Frame(GuaContext c, bool a = true, bool b = true)
    {
        c.BeginFrame("test");
        if (a) c.RegisterNode("a", "button", "A", new GuaBounds());
        if (b) c.RegisterNode("b", "button", "B", new GuaBounds());
        c.EndFrame();
    }
    [Test]
    public void WorldObjectsHaveIndependentNamesAndOwnerProjection()
    {
        using var c = new GuaContext();
        void World(bool second = true, bool visible = true) {
            c.BeginWorldFrame("world");
            c.RegisterWorldObject(new("enemy-1", "enemy", "One", GuaWorldSpace.World2D, new(0, 0), VisibleToPlayer: visible));
            if (second) c.RegisterWorldObject(new("enemy-2", "enemy", "Two", GuaWorldSpace.World2D, new(1, 0), VisibleToPlayer: true));
            c.EndWorldFrame();
        }
        World(); using var one = c.CreateObserveOwner(GuaObserveSource.Object, "enemy-1");
        using var two = c.CreateObserveOwner(GuaObserveSource.Object, "enemy-2");
        using var a = one.Observe("phase", () => GuaValue.String("First"), allowPlayer: true);
        using var b = two.Observe("phase", () => GuaValue.String("Second"), allowPlayer: true);
        World(); Assert.That(Entries(c).Length, Is.EqualTo(2));
        using var player = c.SubscribeObservations(GuaObservationProfile.Player);
        World(second: false, visible: false);
        Assert.That(Entries(c).Length, Is.EqualTo(1));
        Assert.That(Parse(c.GetObserveSnapshotJson(GuaObservationProfile.Player)).GetProperty("entries").GetArrayLength(), Is.Zero);
        Assert.That(Parse(player.PollJson()).GetProperty("status").GetString(), Is.EqualTo("gap"));
        World(); using var reborn = c.CreateObserveOwner(GuaObserveSource.Object, "enemy-2");
        using var replacement = reborn.Observe("phase", () => GuaValue.String("New")); World();
        Assert.That(c.GetObserveSnapshotJson(), Does.Contain("New").And.Not.Contain("Second"));
        Assert.Throws<GuaObserveException>(() => one.Property("bad", () => GuaValue.Bool(true)));
    }
    [Test]
    public void ExplicitOwnerDestructionInvalidatesPlayerHistory()
    {
        using var c = new GuaContext(); using var owner = c.CreateObserveOwner(GuaObserveSource.World);
        using var r = owner.Property("phase", () => GuaValue.String("REVOKED_VALUE"), allowPlayer: true);
        using var player = c.SubscribeObservations(GuaObservationProfile.Player);
        using var debug = c.SubscribeObservations();
        r.Notify(); owner.Dispose();
        string result = player.PollJson();
        Assert.That(Parse(result).GetProperty("status").GetString(), Is.EqualTo("gap"));
        Assert.That(result, Does.Not.Contain("REVOKED_VALUE"));
        Assert.That(debug.PollJson(), Does.Contain("removed"));
    }
    [TestCase(false)]
    [TestCase(true)]
    public void GetterMayDisposeContextWithoutPublishing(bool inFrame)
    {
        var c = new GuaContext();
        using var owner = c.CreateObserveOwner(GuaObserveSource.World);
        using var r = owner.Property("dispose", () => { c.Dispose(); return GuaValue.Integer(1); });
        if (inFrame) { c.BeginWorldFrame("world"); Assert.DoesNotThrow(c.EndWorldFrame); }
        else Assert.DoesNotThrow(r.Notify);
    }
    [Test]
    public void SharedTransitionFixture()
    {
        var fixture = Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "observe-v1.json")));
        using var c = new GuaContext(); using var owner = c.CreateObserveOwner(GuaObserveSource.World);
        JsonElement step = default;
        using var r = owner.Observe("phase", () => {
            if (step.TryGetProperty("error", out var error)) {
                if (error.GetInt32() == 4) return GuaValue.Number(double.NaN);
                throw new Exception("SECRET_MARKER");
            }
            return GuaValue.Integer(step.GetProperty("value").GetInt64());
        });
        using var sub = c.SubscribeObservations();
        foreach (var next in fixture.GetProperty("transitions").EnumerateArray()) {
            step = next;
            if (step.TryGetProperty("remove", out _)) r.Dispose(); else r.Notify();
            var events = Events(sub); Assert.That(events.Length, Is.EqualTo(1));
            Assert.That(events[0].GetProperty("kind").GetString(), Is.EqualTo(step.GetProperty("kind").GetString()));
        }
    }
    [Test]
    public void FrameExplicitFailureRecoveryAndCollections()
    {
        using var c = new GuaContext(); Frame(c);
        using var owner = c.CreateObserveOwner(GuaObserveSource.Ui, "a"); int value = 0;
        using var r = owner.Observe("phase", () => GuaValue.Integer(value));
        using var sub = c.SubscribeObservations(); Frame(c); Assert.That(Events(sub)[0].GetProperty("kind").GetString(), Is.EqualTo("added"));
        value = 1; value = 0; Frame(c); Assert.That(Events(sub), Is.Empty);
        value = 1; r.Notify(); value = 0; r.Notify(); Assert.That(Events(sub).Length, Is.EqualTo(2));
        bool fail = true;
        using var broken = owner.Observe("bad", () => fail ? throw new Exception("SECRET_MARKER") : GuaValue.Bool(true));
        Frame(c); Assert.That(c.GetObserveSnapshotJson(), Does.Not.Contain("SECRET_MARKER"));
        var bad = Entries(c).Single(e => e.GetProperty("name").GetString() == "bad");
        Assert.That(bad.GetProperty("error").GetInt32(), Is.EqualTo(100)); Assert.That(bad.TryGetProperty("value", out _), Is.False);
        fail = false; Frame(c); Assert.That(sub.PollJson(), Does.Contain("recovered"));
        var values = new List<long> { 1 };
        using var list = owner.Observe("items", () => {
            var items = values.Select(GuaValue.Integer).ToArray();
            try { return GuaValue.Collection(GuaValueType.List, GuaValueType.Integer, items); }
            finally { foreach (var item in items) item.Dispose(); }
        });
        Frame(c); sub.PollJson(); values.Add(2); Frame(c); Assert.That(Events(sub)[0].GetProperty("after").GetProperty("value").GetArrayLength(), Is.EqualTo(2));
        using var invalid = owner.Observe("invalid", () => GuaValue.Number(double.NaN)); Frame(c);
        Assert.That(Entries(c).Single(e => e.GetProperty("name").GetString() == "invalid").GetProperty("error").GetInt32(), Is.EqualTo(4));
    }
    [Test]
    public void LifetimeDuplicateAndResetDoNotResurrectRegistrations()
    {
        using var c = new GuaContext(); Frame(c);
        using var a = c.CreateObserveOwner(GuaObserveSource.Ui, "a"); using var b = c.CreateObserveOwner(GuaObserveSource.Ui, "b");
        int calls = 0;
        using var ra = a.Observe("phase", () => GuaValue.Integer(1));
        using var rb = b.Observe("phase", () => { ++calls; return GuaValue.Integer(2); });
        Assert.Throws<GuaObserveException>(() => a.Observe("phase", () => GuaValue.Integer(3)));
        Frame(c); Assert.That(Entries(c).Length, Is.EqualTo(2));
        ra.Dispose(); using var replacement = a.Observe("phase", () => GuaValue.Integer(4));
        Frame(c, b: false); int before = calls; Frame(c, b: false); Assert.That(calls, Is.EqualTo(before));
        Frame(c); using var b2 = c.CreateObserveOwner(GuaObserveSource.Ui, "b");
        using var sub = c.SubscribeObservations(); c.Reset();
        Assert.That(Parse(sub.PollJson()).GetProperty("status").GetString(), Is.EqualTo("stale_session"));
        Frame(c); Assert.That(Entries(c), Is.Empty);
    }
    [Test]
    public void UnavailableRemovalSecretsAndBoundedSubscriptions()
    {
        using var c = new GuaContext(); using var world = c.CreateObserveOwner(GuaObserveSource.World);
        int value = 0; using var r = world.Observe("value", () => GuaValue.Integer(value), allowPlayer: true);
        using var secret = world.Observe("secret", () => GuaValue.String("SECRET_MARKER"), allowPlayer: true, sensitive: true);
        secret.Notify(); Assert.That(c.GetObserveSnapshotJson(), Does.Not.Contain("SECRET_MARKER"));
        using var s1 = c.SubscribeObservations(); using var s2 = c.SubscribeObservations(); r.Notify();
        Assert.That(Events(s1).Length, Is.EqualTo(1)); Assert.That(Events(s2).Length, Is.EqualTo(1));
        c.SetObserveHistoryLimits(1, 100000); value = 1; r.Notify(); value = 2; r.Notify();
        Assert.That(Parse(s1.PollJson()).GetProperty("status").GetString(), Is.EqualTo("gap"));
        using var fresh = c.SubscribeObservations(); Assert.That(Events(fresh), Is.Empty);
        c.SetObserveHistoryLimits(1024, 1); value = 3; r.Notify(); Assert.That(Parse(fresh.PollJson()).GetProperty("status").GetString(), Is.EqualTo("gap"));
        c.SetObserveHistoryLimits(); using var removal = c.SubscribeObservations(); secret.Dispose();
        var e = Events(removal)[0]; Assert.That(e.GetProperty("kind").GetString(), Is.EqualTo("removed")); Assert.That(e.TryGetProperty("before", out _), Is.False);
    }
    [Test]
    public void HandlesAreSafeAfterContextDisposalAndReentrantReset()
    {
        var c = new GuaContext(); var world = c.CreateObserveOwner(GuaObserveSource.World);
        var r = world.Observe("reset", () => { c.Reset(); return GuaValue.Integer(1); });
        r.Notify(); Assert.That(Entries(c), Is.Empty);
        c.Dispose(); r.Dispose(); world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => c.GetObserveSnapshotJson());
    }
    [Test]
    public void WorldPropertyUsesWorldBoundaryAndRejectsInvalidFrames()
    {
        using var c = new GuaContext(); using var world = c.CreateObserveOwner(GuaObserveSource.World);
        using var r = world.Property("weather", () => GuaValue.String("sunny"));
        Frame(c); Assert.That(Entries(c)[0].GetProperty("status").GetString(), Is.EqualTo("unavailable"));
        c.BeginWorldFrame("world"); c.EndWorldFrame(); Assert.That(Entries(c)[0].GetProperty("value").GetProperty("value").GetString(), Is.EqualTo("sunny"));
        using var sub = c.SubscribeObservations(); c.BeginWorldFrame("world"); c.AbortWorldFrame(); Assert.That(Events(sub), Is.Empty);
    }
}
