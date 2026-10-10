# Gua.Testing.Godot

## Godotを外部テストから起動して確認する

Godot project/sceneを起動し、ゲーム側addonが公開するUIへ接続するtest hostです。
C#のテストがあるからゲーム側もC# adapterを使う必要がある、という意味ではありません。
推奨runtime addonとテスト側hostの役割を分けて選びます。

「projectとGodot実行ファイルを準備 → scene起動 → bridgeに接続 →
Gua.Testingで操作・完了・新しい状態を確認 → teardown」の流れです。
描画の必要なPNG確認はrendered起動とhost側のcapture許可が別途必要です。
processの起動成功だけではaddon登録やテストの成功になりません。

[GodotSceneTestHost.Load / LoadRendered](GodotSceneTestHost.cs)が起動と接続、
[GDScript sample](../../../../examples/godot-gdscript/README.md)がゲーム側の準備の入口です。
下の設定例・form testを読み、共通suiteと実engineの区別は[共通の確認準備とsuite別手順](../../../../docs/developer-reading-guide.ja.md)を参照してください。

`Gua.Testing.Godot` provides a small Godot process test host over `Gua.Core` and
`Gua.Testing`. It starts a Godot project, connects to the Gua WebSocket bridge,
and lets tests assert against the live semantic UI tree.

Godot visual capture is opt-in. After the adapter publishes a viewport PNG,
`GodotSceneTestHost.WaitForScreenshotAsync` waits for that existing protocol
payload. Add `Gua.Testing.Visual` only in visual test projects; the base Godot
testing package deliberately has no PNG codec dependency.
Add `Gua.Testing.Recording` separately when semantic operation recording and
correlated replay are required.

```csharp
using Gua.Testing;
using Gua.Testing.Godot;

using var host = GodotSceneTestHost.Load("game/scenes/title_screen.tscn");

GuaAssertions.GetByRole(host.Context, "button", "開始").ToBeVisible();

host.Click("CenterPanel/Content/ButtonBox/StartButton", nextScene: "game/scenes/village_list.tscn");

GuaAssertions.GetByRole(host.Context, "button", "Create").ToBeVisible();
```

Set `GODOT_EXECUTABLE` or pass `GodotSceneTestHostOptions.GodotExecutablePath`
when Godot is not on `PATH`. The running game must start a Gua bridge, normally
at `ws://127.0.0.1:8765`.

On macOS the executable option may point to either the Godot binary or a
`Godot.app` bundle. Unix executable permissions are handled before launch.

For parallel tests, set `UseAvailableBridgePort = true`; the host reserves a
loopback port, connects to it, and passes it to the child as `GUA_BRIDGE_PORT`.
`EnvironmentVariables` adds per-test child settings. `LoadRendered(...)` is the
rendering-enabled shortcut for `Headless = false`. Optional `StartupReset` and
`TeardownReset` enforce session isolation; a strict teardown reports dirty
requests/events instead of silently discarding them.

```csharp
using var host = GodotSceneTestHost.LoadRendered(scene, new()
{
    UseAvailableBridgePort = true,
    EnvironmentVariables = new Dictionary<string, string> { ["GUA_TEST_CASE"] = "servers" },
    StartupReset = new GuaResetOptions(Strict: true),
    TeardownReset = new GuaResetOptions(Strict: true),
});
```

New tests can use lifecycle policies while legacy `StartupReset`,
`TeardownReset`, and `ResetContext` remain available:

```csharp
using var host = GodotSceneTestHost.LoadRendered("res://Main.tscn", new GodotSceneTestHostOptions
{
    StartupResetPolicy = GuaResetPolicy.Strict,
    TeardownResetPolicy = GuaResetPolicy.Strict,
    CaptureDiagnosticsBeforeTeardown = true,
    CleanupAfterLeakReport = true,
    CaptureScreenshotBeforeTeardown = true,
    DiagnosticsTestName = TestContext.CurrentContext.Test.Name,
});
```

Teardown diagnostics, optional on-demand screenshot, process metadata, stdout,
and stderr are captured while the process and bridge are alive. Explicit
non-strict cleanup may run only afterward. `host.Run(...)` preserves a test-body
exception as primary and attaches teardown failure as secondary data. Clean
teardown creates no artifact, and repeated disposal is safe.

Repository-specific factories should retain only game concerns such as locating
API/database fixtures, choosing repository timeouts, and mapping game-relative
scene names. Executable discovery, `project.godot` discovery, bridge ports,
process output, rendering mode, and Gua reset policy belong to this package.

When `ProjectPath` is omitted, the host walks up from the scene file to the
nearest `project.godot` and uses that directory as Godot's `--path`. Pass
`ProjectPath` explicitly when loading a `res://` scene path.

`nextScene` may be a logical Gua screen name such as `loading` or a Godot scene
path such as `game/scenes/village_list.tscn`. Scene paths are matched against
common logical screen names derived from the file name, and also accept a screen
change from the previous value because Godot adapters often publish logical
screen names instead of resource paths.

For failure artifacts, `host.CreateDiagnosticOptions(testName)` includes the
Godot process id, bridge URL, and project metadata. The remote context captures
the same versioned diagnostics JSON as an in-process context.

## Protocol v2 form testing

Typed operations wait for the correlated action result, not merely enqueue
acceptance. Request-id-specific polling leaves unrelated results and ordinary
events queued. Async methods accept timeout, poll interval, and cancellation.

```csharp
using var host = GodotSceneTestHost.Load("res://Main.tscn", new GodotSceneTestHostOptions
{
    ProjectPath = projectPath,
    StartupReset = new GuaResetOptions(Strict: true), // opt-in
});

var userName = GuaAssertions.Query(host.Context)
    .ByRole("textbox").Within("LoginForm").ByAction("set_value").Get();
await userName.SetValueAsync("alice");
await userName.FocusAsync();
await GuaAssertions.PressKeyAsync(host.Context, "Enter"); // current focus
await GuaAssertions.GetById(host.Context, "ProviderOption").SelectAsync("google");
await GuaAssertions.GetById(host.Context, "RememberMe").SetCheckedAsync(true);
await GuaAssertions.GetById(host.Context, "ManagementContent").ScrollAsync(0, 500);
await userName.WaitForValueAsync("alice");
await userName.WaitUntilFocusedAsync();

var saved = host.SaveScreenshot(TestContext.CurrentContext.WorkDirectory,
    TestContext.CurrentContext.Test.Name);
TestContext.AddTestAttachment(saved.AbsolutePath, $"{saved.Width}x{saved.Height}");
```

Use `SetValueAsync(value, sensitive: true)` for secrets. Plaintext is redacted
from action results, logs, diagnostics, and snapshots for adapter-marked
sensitive controls. A screenshot can still contain a rendered secret: saving,
attaching, retaining, and publishing PNG files is the caller's responsibility.
`SaveScreenshot` creates a collision-resistant absolute path and distinguishes
unpublished, malformed data-URI, and invalid-PNG payloads. A headless renderer
without viewport capture is reported as unpublished rather than an empty file.
`GodotSceneTestHost.CaptureScreenshotAsync` requests a new PNG from the next
drawable frame and returns its request ID, session epoch, and frame sequence.
Headless, rendering-disabled, unsupported, timeout, and cancellation outcomes are
distinct. Requests arriving together may share one viewport readback. The older
`GetScreenshot`/`WaitForScreenshotAsync` APIs keep reading the latest published
image. Rendered secrets are not automatically redacted.

`GodotSceneTestHost.CreateDiagnosticsSession` adds process metadata, separate
stdout/stderr files, and optional on-demand screenshot capture to the common
`GuaDiagnosticsSession` layout. Capture runs while the process and bridge are
still alive. A screenshot or attachment failure is returned as a secondary
capture error and does not replace the original assertion/action exception.
