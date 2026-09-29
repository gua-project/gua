using System.Text.Json;
using System.Text;
using Gua.Core;
using Gua.Testing.Unity;
using NUnit.Framework;

namespace Gua.Unity.Integration.Tests;

[TestFixture]
public sealed class UnityIntegrationTests
{
    [TestCase("1")]
    [TestCase("1.5")]
    [TestCase("2")]
    public void RenderedPlayer_ToolkitClickDispatchesPointerEventsAndRejectsCoveredTargets(string scale)
    {
        var player = Environment.GetEnvironmentVariable("GUA_UNITY_PLAYER");
        if (string.IsNullOrWhiteSpace(player)) Assert.Ignore("Set GUA_UNITY_PLAYER to run the Unity integration fixture.");
        using var host = UnitySceneTestHost.LoadRenderedPlayer(player!, new UnitySceneTestHostOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["GUA_UNITY_TOOLKIT_CLICKS"] = "1",
                ["GUA_UNITY_TOOLKIT_SCALE"] = scale,
            },
        });
        Assert.That(WaitForNode(host, "button", "Click Target", out var buttonId), Is.True);

        GuaActionEvent Act(string id, GuaActionType action = GuaActionType.Click)
        {
            Assert.That(host.Context.EnqueueAction(new GuaActionRequest(action, id), out var requestId), Is.EqualTo(GuaActionError.None));
            Assert.That(WaitForActionEvent(host, requestId, out var result), Is.True);
            return result;
        }
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var result = Act(buttonId);
            Assert.That(result.Succeeded, Is.True, result.Error.ToString());
            Assert.That(WaitForText(host, $"clicks:{attempt} down:{attempt} up:{attempt} events:{attempt} count:1 centered:True"), Is.True,
                "A completed click must invoke clicked and pointer/click callbacks exactly once at the panel-space center.");
            Assert.That(host.Context.TryPollActionEvent(out _), Is.False, "Synthetic clicks must not emit duplicate uncorrelated events.");
        }
        Assert.That(WaitForNode(host, "button", "Scroll Target", out var scrollId), Is.True);
        var scrollResult = Act(scrollId);
        Assert.That(scrollResult.Succeeded, Is.True, scrollResult.Error.ToString());
        Assert.That(WaitForText(host, "scrolled:1"), Is.True);

        Assert.That(WaitForNode(host, "tab", "click-second-tab", out var secondTab), Is.True);
        var tabResult = Act(secondTab);
        Assert.That(tabResult.Succeeded, Is.True, tabResult.Error.ToString());
        Assert.That(WaitForText(host, "tab:1"), Is.True, "Click must activate the previously inactive tab.");
        Assert.That(WaitForNode(host, "tab", "click-first-tab", out var firstTab), Is.True);
        Assert.That(Act(firstTab, GuaActionType.Select).Succeeded, Is.True);
        Assert.That(WaitForText(host, "tab:0"), Is.True);

        Assert.That(WaitForNode(host, "button", "Hide Tab Content", out var hideContentId), Is.True);
        Assert.That(Act(hideContentId).Succeeded, Is.True);
        Assert.That(Act(secondTab).Succeeded, Is.True, "Visible tab headers remain actionable when their separate content container is hidden.");
        Assert.That(WaitForText(host, "tab:1"), Is.True);
        Assert.That(WaitForNode(host, "button", "Hide Tab Headers", out var hideHeadersId), Is.True);
        Assert.That(Act(hideHeadersId).Succeeded, Is.True);
        Assert.That(() => host.RemoteContext.GetRemoteTree().Nodes.Single(node => node.Id == secondTab).Visible,
            Is.False.After(2000, 50), "A hidden header ancestor must make its tab hidden.");

        Assert.That(WaitForNode(host, "button", "Show Cover", out var coverId), Is.True);
        Assert.That(Act(coverId).Succeeded, Is.True);
        var covered = Act(buttonId);
        Assert.That(covered.Succeeded, Is.False);
        Assert.That(covered.Error, Is.EqualTo(GuaActionError.Hidden));
        Assert.That(WaitForText(host, "clicks:2 down:2 up:2 events:2 count:1 centered:True"), Is.True);
    }

    [Test]
    public void PrecompiledUpmArtifactPlayer_LoadsManagedAndNativeClosure()
    {
        var player = Environment.GetEnvironmentVariable("GUA_UNITY_ARTIFACT_PLAYER");
        if (string.IsNullOrWhiteSpace(player)) Assert.Ignore("Set GUA_UNITY_ARTIFACT_PLAYER to verify the precompiled UPM artifact.");
        using var host = UnitySceneTestHost.LoadRenderedPlayer(player!, new UnitySceneTestHostOptions { ConnectTimeout = TimeSpan.FromSeconds(30) });
        var version = host.RemoteContext.GetVersion();
        Assert.That(version.AdapterVersions, Contains.Key("unity"));
        Assert.That(WaitForFrame(host), Is.True, "The precompiled Unity adapter did not publish a frame.");
    }

    [Test]
    public void RenderedEditorPlayMode_ReflectsFixture()
    {
        var scene = Environment.GetEnvironmentVariable("GUA_UNITY_SCENE");
        if (string.IsNullOrWhiteSpace(scene)) Assert.Ignore("Set GUA_UNITY_SCENE to run the Unity Editor integration fixture.");
        var batchMode = string.Equals(Environment.GetEnvironmentVariable("GUA_UNITY_EDITOR_BATCH_MODE"), "1", StringComparison.Ordinal);
        var disableAssemblyUpdater = string.Equals(Environment.GetEnvironmentVariable("GUA_UNITY_EDITOR_DISABLE_ASSEMBLY_UPDATER"), "1", StringComparison.Ordinal);
        using var host = UnitySceneTestHost.LoadEditor(scene!, new UnitySceneTestHostOptions
        {
            UnityExecutablePath = Environment.GetEnvironmentVariable("UNITY_EXECUTABLE"),
            ProjectPath = Environment.GetEnvironmentVariable("GUA_UNITY_PROJECT"),
            // A cold Intel macOS import can still be reloading assemblies when the
            // default 60-second deadline expires, even with the API Updater disabled.
            ConnectTimeout = TimeSpan.FromSeconds(120),
            SceneTimeout = TimeSpan.FromSeconds(15),
            AdditionalArguments = EditorAdditionalArguments(batchMode, disableAssemblyUpdater),
        });
        Assert.That(WaitForText(host, "Start Game"), Is.True);
        Assert.That(WaitForText(host, "Gua Unity Sample"), Is.True);
        Assert.That(WaitForScreen(host, "title"), Is.True);
        Assert.That(host.RemoteContext.GetVersion().AdapterVersions, Contains.Key("unity"));
    }

    [Test]
    public void EditorStartupArguments_CanDisableUpdaterAfterBatchMode()
    {
        Assert.That(EditorAdditionalArguments(batchMode: true, disableAssemblyUpdater: true),
            Is.EqualTo(new[] { "-batchmode", "-disable-assembly-updater" }));
        Assert.That(EditorAdditionalArguments(batchMode: true, disableAssemblyUpdater: false),
            Is.EqualTo(new[] { "-batchmode" }));
    }

    [Test]
    public async Task RenderedPlayer_ReflectsUiAndDispatchesButtonListener()
    {
        var player = Environment.GetEnvironmentVariable("GUA_UNITY_PLAYER");
        if (string.IsNullOrWhiteSpace(player)) Assert.Ignore("Set GUA_UNITY_PLAYER to run the Unity integration fixture.");

        using var host = UnitySceneTestHost.LoadRenderedPlayer(player!, new UnitySceneTestHostOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            SceneTimeout = TimeSpan.FromSeconds(15),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["GUA_UNITY_COVERAGE"] = "1",
                ["GUA_UNITY_HOST_CLICK"] = "1",
                ["GUA_UNITY_FIXTURE_RESOLUTION"] = "960x540",
            },
            AdditionalArguments = ["-screen-width", "960", "-screen-height", "540", "-screen-fullscreen", "0"],
        });

        var version = host.RemoteContext.GetVersion();
        Assert.That(version.AdapterVersions, Contains.Key("unity"));
        Assert.That(WaitForText(host, "Start Game"), Is.True);
        Assert.That(WaitForText(host, "Gua Unity Sample"), Is.True);
        Assert.That(WaitForScreen(host, "title"), Is.True);
        Assert.That(host.RemoteContext.GetRemoteTree().Nodes.All(node => Encoding.UTF8.GetByteCount(node.Id) <= 127), Is.True,
            "Unity must keep node IDs round-trippable through the fixed-size C ABI action request.");

        var playerWorld = await host.RemoteContext.WaitForWorldObjectAsync(
            new GuaWorldSelector(Id: "player-world"), TimeSpan.FromSeconds(15));
        Assert.That(playerWorld.Id, Is.EqualTo("player-world"));
        var nearbyDoors = host.RemoteContext.QueryWorldObjects(new GuaWorldSelector(
            Kind: "door", State: new GuaWorldStateCriterion("locked", true))
            { Near = new GuaWorldNear("player-world", 5), Limit = 1 });
        var nearbyDoorIds = nearbyDoors.Matches.Select(match => match.Id).ToArray();
        Assert.That(nearbyDoors.Valid, Is.True);
        Assert.That(nearbyDoorIds, Is.EqualTo(new[] { "door-a" }),
            $"Unexpected spatial query result: {JsonSerializer.Serialize(nearbyDoors)}");
        Assert.That(nearbyDoors.Spatial?.Truncated, Is.True);
        Assert.That(nearbyDoors.Spatial?.Distances.Single().Distance, Is.EqualTo(5));
        Assert.That(nearbyDoors.SessionEpoch, Is.Not.Null);
        Assert.That(nearbyDoors.FrameSequence, Is.Not.Null);
        Assert.That(nearbyDoors.Revision, Is.Not.Null);

        var nearby3D = host.RemoteContext.QueryWorldObjects(new GuaWorldSelector
            { Near = new GuaWorldNear("anchor-3d", 3) });
        Assert.That(nearby3D.Valid, Is.True);
        Assert.That(nearby3D.Matches.Select(match => match.Id), Does.Contain("target-3d"));
        Assert.That(nearby3D.Spatial!.Distances.Single(distance => distance.ObjectId == "target-3d").Distance, Is.EqualTo(3));

        var initialTree = host.RemoteContext.GetRemoteTree();
        Assert.That(initialTree.Nodes.Count(node => node.Label == "Start Game" || node.Text == "Start Game"), Is.EqualTo(1),
            "A button label must not be duplicated as an independent text node.");
        Assert.That(initialTree.Nodes.Count(node => node.Label == "TMP Launch" || node.Text == "TMP Launch"), Is.EqualTo(1),
            "A TMP button label must not be duplicated as an independent text node.");
        Assert.That(initialTree.Nodes.Count(node => node.Label == "pilot" || node.Text == "pilot"), Is.EqualTo(1),
            "A uGUI InputField value must not be duplicated as an independent child text node.");
        Assert.That(initialTree.Nodes.Count(node => node.Label == "bravo" || node.Text == "bravo"), Is.EqualTo(1),
            "A TMP input value must not be duplicated as an independent child text node.");
        var repeatedToolkitNames = initialTree.Nodes.Where(node => node.Label is "Duplicate A" or "Duplicate B").ToArray();
        Assert.That(repeatedToolkitNames.Select(node => node.Id), Is.Unique);
        Assert.That(repeatedToolkitNames.Select(node => node.Label), Is.EquivalentTo(new[] { "Duplicate A", "Duplicate B" }));
        Assert.That(initialTree.Nodes.Single(node => node.Id == "disabled-canvas-button").Visible, Is.False,
            "Children of a disabled uGUI Canvas must remain in the tree as hidden nodes.");

        const string sensitiveValue = "unity-web-secret";
        var sensitiveError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.SetValue, "sensitive-input", sensitiveValue, Sensitive: true),
            out var sensitiveRequestId);
        Assert.That(sensitiveError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, sensitiveRequestId, out var sensitiveResult), Is.True);
        Assert.That(sensitiveResult.Succeeded, Is.True, $"Sensitive uGUI set_value failed: {sensitiveResult.Error}");
        Assert.That(sensitiveResult.Value, Is.Empty);
        Assert.That(WaitForRedactedNode(host, "sensitive-input", sensitiveValue), Is.True,
            "Sensitive Unity controls must omit their plaintext from semantic snapshots.");

        var sensitiveKeyError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.PressKey, "sensitive-input", Key: "A"),
            out var sensitiveKeyRequestId);
        Assert.That(sensitiveKeyError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, sensitiveKeyRequestId, out var sensitiveKeyResult), Is.True);
        Assert.That(sensitiveKeyResult.Succeeded, Is.True, $"Sensitive uGUI press_key failed: {sensitiveKeyResult.Error}");
        Assert.That(sensitiveKeyResult.Sensitive, Is.True);
        Assert.That(sensitiveKeyResult.Value, Is.Empty,
            "Actions on a previously classified sensitive target must not return its plaintext.");

        var sensitiveFocusError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.Focus, "sensitive-input"),
            out var sensitiveFocusRequestId);
        Assert.That(sensitiveFocusError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, sensitiveFocusRequestId, out var sensitiveFocusResult), Is.True);
        Assert.That(sensitiveFocusResult.Sensitive, Is.True);
        var focusedSensitiveKeyError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.PressKey, Key: "B"),
            out var focusedSensitiveKeyRequestId);
        Assert.That(focusedSensitiveKeyError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, focusedSensitiveKeyRequestId, out var focusedSensitiveKeyResult), Is.True);
        Assert.That(focusedSensitiveKeyResult.Succeeded, Is.True, $"Focused sensitive uGUI press_key failed: {focusedSensitiveKeyResult.Error}");
        Assert.That(focusedSensitiveKeyResult.Sensitive, Is.True);
        Assert.That(focusedSensitiveKeyResult.Value, Is.Empty,
            "Focused actions on a sensitive target must not return its plaintext.");

        const string detachedSensitiveValue = "unity-detached-secret";
        Assert.That(WaitForNode(host, "textbox", "detached-sensitive-input", out var detachedSensitiveId), Is.True,
            "The UI Toolkit fixture did not publish its detachable sensitive control.");
        var detachedSensitiveError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.SetValue, detachedSensitiveId, detachedSensitiveValue, Sensitive: true),
            out var detachedSensitiveRequestId);
        Assert.That(detachedSensitiveError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, detachedSensitiveRequestId, out var detachedSensitiveResult), Is.True);
        Assert.That(detachedSensitiveResult.Succeeded, Is.True);
        Assert.That(WaitForNodeAbsent(host, detachedSensitiveId), Is.True,
            "The UI Toolkit fixture did not temporarily detach the sensitive control.");
        Assert.That(WaitForRedactedNode(host, detachedSensitiveId, detachedSensitiveValue), Is.True,
            "A reattached UI Toolkit control must retain its sensitive classification.");

        var integerSlider = initialTree.Nodes.Single(node => node.Role == "slider" && node.Label == "integer-slider");
        var integerSliderError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.SetValue, integerSlider.Id, "7"), out var integerSliderRequestId);
        Assert.That(integerSliderError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, integerSliderRequestId, out var integerSliderResult), Is.True);
        Assert.That(integerSliderResult.Succeeded, Is.True, $"UI Toolkit SliderInt set_value failed: {integerSliderResult.Error}");
        Assert.That(integerSliderResult.Value, Is.EqualTo("7"));
        Assert.That(WaitForValue(host, integerSlider.Id, value => value == "7"), Is.True);

        var sensitiveToolkitSliderError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.SetValue, integerSlider.Id, "8", Sensitive: true),
            out var sensitiveToolkitSliderRequestId);
        Assert.That(sensitiveToolkitSliderError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, sensitiveToolkitSliderRequestId, out var sensitiveToolkitSliderResult), Is.True);
        Assert.That(sensitiveToolkitSliderResult.Succeeded, Is.True);
        Assert.That(WaitForRedactedRangeNode(host, integerSlider.Id), Is.True,
            "Sensitive UI Toolkit sliders must omit both value and state.rangeValue.");

        var sensitiveUGuiSliderError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.SetValue, "sample-slider", "0.75", Sensitive: true),
            out var sensitiveUGuiSliderRequestId);
        Assert.That(sensitiveUGuiSliderError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, sensitiveUGuiSliderRequestId, out var sensitiveUGuiSliderResult), Is.True);
        Assert.That(sensitiveUGuiSliderResult.Succeeded, Is.True);
        Assert.That(WaitForRedactedRangeNode(host, "sample-slider"), Is.True,
            "Sensitive uGUI sliders must omit both value and state.rangeValue.");

        var uGuiFocusError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.Focus, "sample-input"), out var uGuiFocusRequestId);
        Assert.That(uGuiFocusError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForAction(host, uGuiFocusRequestId), Is.True);
        Assert.That(WaitForSingleFocusedNode(host, "sample-input"), Is.True,
            "uGUI focus must be the only published Unity focus source.");

        var toolkitFocusError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.Focus, integerSlider.Id), out var toolkitFocusRequestId);
        Assert.That(toolkitFocusError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForAction(host, toolkitFocusRequestId), Is.True);
        Assert.That(WaitForSingleFocusedNode(host, integerSlider.Id), Is.True,
            "UI Toolkit focus must clear the stale uGUI EventSystem selection.");

        var firstTab = initialTree.Nodes.Single(node => node.Role == "tab" && node.Label == "first-tab");
        var tabClickError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.Click, firstTab.Id), out var tabClickRequestId);
        Assert.That(tabClickError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, tabClickRequestId, out var tabClickResult), Is.True,
            "UI Toolkit tab click must not remain pending.");
        Assert.That(tabClickResult.Succeeded, Is.True, $"UI Toolkit tab click failed: {tabClickResult.Error}");
        Assert.That(WaitForObservedClick(host, "settings", out var observedClick), Is.True,
            "Host-driven Unity clicks must be published as uncorrelated Gua events.");
        Assert.That(observedClick.RequestId, Is.Zero);
        Assert.That(observedClick.Action, Is.EqualTo(GuaActionType.Click));
        Assert.That(observedClick.NodeId, Is.EqualTo("settings"));

        Assert.That(host.Context.FindNodeByRole("button", "TMP Launch"), Is.EqualTo("tmp-button"));
        var listId = host.Context.FindNodeByRole("list", "fixture-list");
        Assert.That(listId, Is.Not.Null.And.Not.Empty);
        Assert.That(WaitForListItems(host, listId!, "One", "Two", "Three"), Is.True);

        var scrollError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.Scroll, listId, DeltaY: 1, ScrollUnit: 1), out var scrollRequestId);
        Assert.That(scrollError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, scrollRequestId, out var scrollResult), Is.True,
            () => "ListView scroll did not complete. " + host.RemoteContext.GetDiagnosticsJson());
        Assert.That(scrollResult.Succeeded, Is.True, $"ListView scroll failed: {scrollResult.Error}");

        var invalidError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.SetValue, "sample-slider", "not-a-number"), out var invalidRequestId);
        Assert.That(invalidError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, invalidRequestId, out var invalidResult), Is.True);
        Assert.That(invalidResult.Succeeded, Is.False);
        Assert.That(invalidResult.Error, Is.EqualTo(GuaActionError.InvalidValue));

        const string exceptionSecret = "unity-exception-secret";
        var throwingError = host.Context.EnqueueAction(
            new GuaActionRequest(GuaActionType.SetValue, "throwing-input", exceptionSecret, Sensitive: true),
            out var throwingRequestId);
        Assert.That(throwingError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, throwingRequestId, out var throwingResult), Is.True);
        Assert.That(throwingResult.Succeeded, Is.False);
        Assert.That(throwingResult.Error, Is.EqualTo(GuaActionError.InvalidValue));
        Assert.That(WaitForRedactedNode(host, "throwing-input", exceptionSecret), Is.True,
            "A sensitive value applied before a Unity setter exception must remain redacted from later frames.");
        var sensitiveDiagnostics = host.RemoteContext.GetDiagnosticsJson();
        Assert.That(sensitiveDiagnostics, Does.Not.Contain(exceptionSecret));
        Assert.That(sensitiveDiagnostics, Does.Contain("[redacted]"));

        var keyError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.PressKey, "sample-input", Key: "A", Modifiers: 1), out var keyRequestId);
        Assert.That(keyError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, keyRequestId, out var keyResult), Is.True);
        Assert.That(keyResult.Succeeded, Is.True, $"uGUI press_key failed: {keyResult.Error}");
        Assert.That(keyResult.Value, Does.Contain("A"));
        Assert.That(WaitForValue(host, "sample-input", value => value.Contains("A", StringComparison.Ordinal)), Is.True);

        var tmpKeyError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.PressKey, "tmp-input", Key: "Z", Modifiers: 1), out var tmpKeyRequestId);
        Assert.That(tmpKeyError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForActionEvent(host, tmpKeyRequestId, out var tmpKeyResult), Is.True);
        Assert.That(tmpKeyResult.Succeeded, Is.True, $"TMP press_key failed: {tmpKeyResult.Error}");
        Assert.That(tmpKeyResult.Value, Does.Contain("Z"));

        var titleScreenshot = host.CaptureScreenshot(TimeSpan.FromSeconds(15));
        Assert.That(titleScreenshot.Width, Is.GreaterThan(0));
        Assert.That(titleScreenshot.Height, Is.GreaterThan(0));
        var panelScale = titleScreenshot.Width / 640f;
        Assert.That(titleScreenshot.Height / 360f, Is.EqualTo(panelScale).Within(0.01f),
            "The scaled UI Toolkit fixture requires a 16:9 render surface.");

        Assert.That(WaitForBounds(host, "scaled-box", out var scaledBounds), Is.True);
        Assert.That(scaledBounds.X, Is.EqualTo(100 * panelScale).Within(1));
        Assert.That(scaledBounds.Y, Is.EqualTo(60 * panelScale).Within(1));
        Assert.That(scaledBounds.Width, Is.EqualTo(200 * panelScale).Within(1));
        Assert.That(scaledBounds.Height, Is.EqualTo(40 * panelScale).Within(1));
        var remoteScaledBounds = host.RemoteContext.GetRemoteTree().Nodes.Single(node => node.Label == "scaled-box").Bounds;
        Assert.That(remoteScaledBounds.Width, Is.EqualTo(200 * panelScale).Within(1));
        Assert.That(remoteScaledBounds.Height, Is.EqualTo(40 * panelScale).Within(1));

        var settings = host.Context.FindNodeByRole("button", "Settings");
        var settingsError = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.Click, settings), out var settingsRequestId);
        Assert.That(settingsError, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForAction(host, settingsRequestId), Is.True);
        Assert.That(WaitForScreen(host, "title"), Is.True, "Settings should intentionally leave the sample on the title screen.");

        var button = host.Context.FindNodeByRole("button", "Start Game");
        var error = host.Context.EnqueueAction(new GuaActionRequest(GuaActionType.Click, button), out var requestId);
        Assert.That(error, Is.EqualTo(GuaActionError.None));
        Assert.That(WaitForAction(host, requestId), Is.True, "Unity did not emit the request-correlated action completion event.");
        Assert.That(host.Context.TryPollActionEvent(out var unexpectedClick) && unexpectedClick.RequestId == 0 && unexpectedClick.NodeId == button, Is.False,
            "Automation clicks must not be reported a second time as host-driven click events.");
        Assert.That(WaitForText(host, "Loading..."), Is.True, "The Start Game listener did not show the loading screen.");
        Assert.That(WaitForScreen(host, "loading"), Is.True);

        var screenshot = host.CaptureScreenshot(TimeSpan.FromSeconds(15));
        Assert.That(screenshot.DecodePng().Take(8), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.That(screenshot.Width, Is.GreaterThan(0));
        Assert.That(screenshot.Height, Is.GreaterThan(0));
    }

    private static bool WaitForAction(UnitySceneTestHost host, ulong requestId)
    {
        return WaitForActionEvent(host, requestId, out var action) && action.Succeeded;
    }

    private static IReadOnlyList<string> EditorAdditionalArguments(bool batchMode, bool disableAssemblyUpdater)
    {
        var arguments = new List<string>();
        if (batchMode) arguments.Add("-batchmode");
        if (disableAssemblyUpdater) arguments.Add("-disable-assembly-updater");
        return arguments;
    }

    private static bool WaitForActionEvent(UnitySceneTestHost host, ulong requestId, out GuaActionEvent result)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (host.Context.TryPollActionEvent(requestId, out result)) return true;
            Thread.Sleep(20);
        }
        result = default;
        return false;
    }

    private static bool WaitForObservedClick(UnitySceneTestHost host, string nodeId, out GuaActionEvent result)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (host.Context.TryPollActionEvent(out var candidate) && candidate.RequestId == 0 &&
                candidate.Action == GuaActionType.Click && candidate.NodeId == nodeId)
            {
                result = candidate;
                return true;
            }
            Thread.Sleep(20);
        }
        result = default;
        return false;
    }

    private static bool WaitForListItems(UnitySceneTestHost host, string parentId, params string[] labels)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var tree = JsonDocument.Parse(host.Context.GetUiTreeJson());
            var items = tree.RootElement.GetProperty("nodes").EnumerateArray()
                .Where(node => node.GetProperty("role").GetString() == "listitem" &&
                    node.TryGetProperty("parentId", out var parent) && parent.GetString() == parentId)
                .Select(node => node.GetProperty("label").GetString()).ToArray();
            if (labels.All(label => items.Contains(label))) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForNode(UnitySceneTestHost host, string role, string label, out string id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var match = host.RemoteContext.GetRemoteTree().Nodes
                .FirstOrDefault(node => node.Role == role && node.Label == label);
            if (match != null)
            {
                id = match.Id;
                return true;
            }
            Thread.Sleep(20);
        }
        id = string.Empty;
        return false;
    }

    private static bool WaitForValue(UnitySceneTestHost host, string id, Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var tree = JsonDocument.Parse(host.Context.GetUiTreeJson());
            foreach (var node in tree.RootElement.GetProperty("nodes").EnumerateArray())
                if (node.GetProperty("id").GetString() == id && node.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String && predicate(value.GetString() ?? "")) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForNodeAbsent(UnitySceneTestHost host, string id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var tree = JsonDocument.Parse(host.Context.GetUiTreeJson());
            if (!tree.RootElement.GetProperty("nodes").EnumerateArray()
                    .Any(node => node.GetProperty("id").GetString() == id)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForRedactedNode(UnitySceneTestHost host, string id, string secret)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var json = host.Context.GetUiTreeJson();
            using var tree = JsonDocument.Parse(json);
            foreach (var node in tree.RootElement.GetProperty("nodes").EnumerateArray())
            {
                if (node.GetProperty("id").GetString() != id) continue;
                if (!node.TryGetProperty("text", out _) && !node.TryGetProperty("value", out _) &&
                    !json.Contains(secret, StringComparison.Ordinal)) return true;
            }
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForRedactedRangeNode(UnitySceneTestHost host, string id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var tree = JsonDocument.Parse(host.Context.GetUiTreeJson());
            foreach (var node in tree.RootElement.GetProperty("nodes").EnumerateArray())
            {
                if (node.GetProperty("id").GetString() != id) continue;
                var exposesRangeValue = node.TryGetProperty("state", out var state) && state.TryGetProperty("rangeValue", out _);
                if (!node.TryGetProperty("value", out _) && !exposesRangeValue) return true;
            }
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForBounds(UnitySceneTestHost host, string idSuffix, out GuaBounds bounds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var tree = JsonDocument.Parse(host.Context.GetUiTreeJson());
            foreach (var node in tree.RootElement.GetProperty("nodes").EnumerateArray())
            {
                var matchesId = (node.GetProperty("id").GetString() ?? "").EndsWith(idSuffix, StringComparison.Ordinal);
                var matchesLabel = node.TryGetProperty("label", out var label) && label.GetString() == idSuffix;
                if (!matchesId && !matchesLabel) continue;
                var value = node.GetProperty("bounds");
                bounds = new GuaBounds(value.GetProperty("x").GetSingle(), value.GetProperty("y").GetSingle(), value.GetProperty("w").GetSingle(), value.GetProperty("h").GetSingle());
                if (bounds.Width > 0 && bounds.Height > 0) return true;
            }
            Thread.Sleep(20);
        }
        bounds = default;
        return false;
    }

    private static bool WaitForSingleFocusedNode(UnitySceneTestHost host, string expectedId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var focused = host.RemoteContext.GetRemoteTree().Nodes.Where(node => node.Focused == true).Select(node => node.Id).ToArray();
            if (focused.Length == 1 && focused[0] == expectedId) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForFrame(UnitySceneTestHost host)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (host.Context.GetContextStatus().FrameSequence > 0) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForText(UnitySceneTestHost host, string text)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var tree = JsonDocument.Parse(host.Context.GetUiTreeJson());
            if (tree.RootElement.GetProperty("nodes").EnumerateArray().Any(node =>
                (node.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == text) ||
                (node.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String && label.GetString() == text))) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool WaitForScreen(UnitySceneTestHost host, string screen)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var tree = JsonDocument.Parse(host.Context.GetUiTreeJson());
            if (tree.RootElement.GetProperty("screen").GetString() == screen) return true;
            Thread.Sleep(20);
        }
        return false;
    }
}
