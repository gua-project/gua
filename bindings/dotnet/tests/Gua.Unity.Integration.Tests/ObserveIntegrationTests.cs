using System.Text.Json;
using Gua.Core;
using Gua.Testing.Unity;
using NUnit.Framework;

namespace Gua.Unity.Integration.Tests;

[TestFixture, NonParallelizable]
public sealed class ObserveIntegrationTests
{
    private static JsonElement Parse(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    [TestCase("debug"), TestCase("player")]
    public void RealWorldObjectRegistrationReachesRemoteSnapshotAndChanges(string profile)
    {
        var player = Environment.GetEnvironmentVariable("GUA_UNITY_PLAYER");
        if (string.IsNullOrWhiteSpace(player)) Assert.Ignore("Set GUA_UNITY_PLAYER to run the real Unity Observe fixture.");
        using var host = UnitySceneTestHost.LoadRenderedPlayer(player!, new UnitySceneTestHostOptions {
            ConnectTimeout = TimeSpan.FromSeconds(30), SceneTimeout = TimeSpan.FromSeconds(15),
            EnvironmentVariables = new Dictionary<string, string> { ["GUA_UNITY_OBSERVE"] = "1", ["GUA_OBSERVATION_PROFILE"] = profile },
            AdditionalArguments = ["-screen-width", "640", "-screen-height", "360", "-screen-fullscreen", "0"]
        });
        string snapshot = "";
        Assert.That(() => { snapshot = host.RemoteContext.GetObserveSnapshotJson(); return Parse(snapshot).GetProperty("document").GetProperty("entries").GetArrayLength(); }, Is.GreaterThanOrEqualTo(3).After(10000, 50));
        if (profile == "player") Assert.That(snapshot, Does.Not.Contain("SECRET_MARKER"));
        using var sub = host.RemoteContext.SubscribeObservations();
        var initial = Parse(sub.SnapshotJson); var entries = initial.GetProperty("document").GetProperty("entries").EnumerateArray().ToArray();
        int phaseIndex = Array.FindIndex(entries, e => e.GetProperty("name").GetString() == "phase");
        Assert.That(entries[phaseIndex].GetProperty("runtimeId").GetString(), Is.EqualTo("door-a"));
        Assert.That(entries[phaseIndex].GetProperty("value").GetProperty("enumType").GetString(), Is.EqualTo("game.Phase"));
        Assert.That(initial.GetProperty("catalogs")[phaseIndex].GetProperty("value").GetProperty("enums")[0].GetProperty("members").GetArrayLength(), Is.EqualTo(2));
        var world = host.RemoteContext.GetWorldObjectTree();
        Assert.That(world.Objects.Single(o => o.Id == "door-a").Position.GetProperty("x").GetDouble(), Is.EqualTo(640));
        Assert.That(entries.Select(e => e.GetProperty("name").GetString()), Does.Not.Contain("worldPosition"));
        string settings = host.Context.FindNodeByRole("button", "Settings");
        Assert.That(host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.Click, settings), out _), Is.EqualTo(GuaActionError.None));
        string changes = "";
        Assert.That(() => { changes = sub.PollJson(); return changes.Contains("Second", StringComparison.Ordinal); }, Is.True.After(10000, 50));
        Assert.That(changes, Does.Contain("changed"));
        host.RemoteContext.Reset(); Assert.That(sub.PollJson(), Does.Contain("stale_session"));
    }
}
