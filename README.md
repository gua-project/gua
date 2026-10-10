# Gua

日本語で実装を読み始める開発者は
[読む順番・仕組み・ローカル確認のガイド](docs/developer-reading-guide.ja.md)を参照してください。
機能別README、規範仕様、過去の検証記録をそれぞれの役割に沿って案内しています。

English | [日本語](README.ja.md)

[![License](https://img.shields.io/github/license/link1345/gua)](https://github.com/link1345/gua/blob/main/LICENSE)

Questions about Gua? Join our [support Discord server](https://discord.gg/B495mwsTKK).

> **A game testing and automation protocol built around Playwright-like design
> principles, with Godot 4.7 and Unity 6 support and semantic access to UI,
> game-world information, and gameplay input.**

**Gua** is a runtime automation protocol for games, built around Playwright-like
design principles. Its core exposes a running game's UI as a Semantic UI Tree.
Instead of relying on fragile image recognition or screen coordinates, automated
tests, inspectors, and AI agents can find controls by ID, role, text, and state;
interact with them; wait for changes; read logs; capture screenshots; and verify
results.

Around that UI automation core, Gua provides a set of capabilities optimized for
game development: a World Object Tree for semantic game-world information,
Semantic Game Actions and opt-in Raw Input for gameplay, MCP for native games,
WebMCP for browser builds, an exposure policy that limits what AI players can see
and which UI operations they may use, deterministic virtual time, Recording, and
visual comparison. These capabilities share the same protocol boundary rather
than becoming separate engine-specific automation systems.

Gua provides runtime integrations for the **Godot 4.7 GDScript addon** and
**Unity 6**. The Unity package automatically reflects UI Toolkit, uGUI, and
TextMeshPro runtime UI on Windows x64, Linux x64, and Intel/Apple Silicon macOS
Mono builds. During development, AI coding
agents can use Gua to implement and verify the game. In a release build, the game
can expose only approved information and operations so an AI agent can play as a
player.

**Build → Run → Inspect → Test → Fix → Test again.**

Gua starts with the Playwright-like model: the browser DOM becomes a Semantic UI
Tree, and locators, actions, waits, and assertions operate against the live game
runtime. It then extends that model for game development with game-world
observation, gameplay input, AI-facing transports and safety policy, deterministic
time control, screenshots, Recording, and visual comparison.

## What can I do with Gua?

### Godot UI testing

- Expose standard Godot `Control` nodes as a Semantic UI Tree.
- Find controls by ID, role, text, value, and state instead of coordinates.
- Click, focus, enter values, select options, scroll, and press keys.
- Wait for UI state changes and assert them from regular .NET tests.
- Capture screenshots, compare visual baselines, and retain failure diagnostics.
- Run end-to-end Godot UI tests in CI with [`link1345/gua-tester`](https://github.com/link1345/gua-tester).

### Unity 6 UI testing

- Automatically expose UI Toolkit, uGUI, and TextMeshPro runtime UI.
- Exercise scenes in Editor Play Mode or a desktop Mono Player on the four supported RIDs.
- Locate and operate controls through the same semantic API used for Godot.
- Keep game-side adapter code separate from external NUnit test hosts.
- Capture logs, screenshots, and diagnostics when a Unity test fails.
- Build and test all four desktop RID Mono Players in CI with [`link1345/gua-tester`](https://github.com/link1345/gua-tester).

### AI-assisted game development and playtesting

`gui-mcp` connects an MCP-enabled AI coding agent to the same bridge used by the
Inspector. The agent can inspect the UI tree, operate semantic controls, wait for
expected state, read game logs, capture screenshots, replay recordings, compare
visuals, and run small test sequences against the game it is helping to build.

Gua complements an AI coding agent: the agent edits the game, while Gua lets it
observe, operate, and verify the running game. Gua does not replace the game
engine or coding agent, and semantic targeting does not depend on image
recognition.

### AI agent players in release builds

Gua also supports AI agents playing a released game as players, not only testing
it during development. The game selects a Player profile that exposes approved UI
and World Objects and permits selected UI operations. Gameplay actions such as
jump or movement, plus keyboard and pointer input, require separate explicit
authorization. The AI observes player-facing semantic information and uses only
the permitted operations; it does not edit source code or receive the complete
Debug view.

Native games can use `gui-mcp`, while Godot Web Export and Unity WebGL builds can
use `gua-webmcp` as the connection path. The World Object Tree can describe
objectives, enemies, doors, and checkpoints, while Semantic UI Actions and game
input operate menus and gameplay. The exposure policy removes private objects,
internal values, and Debug logs, and screenshots remain denied by default. This
lets a shipped game support AI players while the developer retains control over
the boundaries of the game experience.

### Browser-native WebMCP (experimental)

Godot Web Export and Unity WebGL pages can expose the same live Semantic UI Tree
through the experimental browser `document.modelContext` API. The
`gua-webmcp` package registers
`get_ui_tree`, semantic actions, waits, the
read-only World Object Tree observation tools, capability-gated Semantic Game
Action / Raw Input tools, and an optional screenshot tool
against an engine-owned same-page bridge. Shared world types and selector
definitions are published as `gua-world-tools`. It requires
neither `gui-mcp` nor a WebSocket connection, and browsers without WebMCP remain
fully functional. Each tab owns its game and tool registrations; there is no
custom browser session router. See the [browser-native WebMCP guide](https://gua.orizika.com/webmcp/)
or the [`gua-webmcp` package reference](packages/webmcp/README.md).
Successful calls return their structured Gua result directly instead of wrapping
serialized JSON in an MCP-style text content block.
Game input is owned by the current page, waits for request-correlated host
completion, and is released on timeout, cancellation, unregister, or engine
shutdown. Raw tools remain absent until the host explicitly initializes their
input pump and cleanup path.
Player/Public Agent game input is denied separately by default: enabling a
Debug Inspector input path does not expose it to WebMCP. Godot hosts opt in with
the `allow_player_agents` argument and Unity hosts use the corresponding
`AllowPlayerAgentSemanticInput` / `AllowPlayerAgentRawInput` map flags.
The Godot Web addon includes separate Debug and Release GDExtensions. Enable
`Extension Support` in the Godot Web export preset; Godot then selects the
matching `web.wasm32.single.debug` or `web.wasm32.single.release` library.

## Godot 4.7 support

The recommended Godot integration is the GDScript addon backed by GDExtension.
It automatically reflects standard `Control` nodes and can be used by both
GDScript projects and .NET-enabled Godot projects. The separate C# runtime sample
is experimental and does not provide the full adapter feature set.

For Godot developers, Gua provides semantic UI automation, external end-to-end
testing, screenshot and visual regression testing, CI testing, Inspector-based
debugging, and MCP-based AI playtesting. See [Godot 4.7 GDScript Addon](#godot-47-gdscript-addon)
for setup details.

## Unity 6 support

The Unity package automatically starts the runtime adapter and reflects UI
Toolkit, uGUI, and TextMeshPro controls without manual semantic-node
registration. `Gua.Testing.Unity` can launch Editor Play Mode or a built Mono
Player from ordinary .NET tests. The desktop scope is Unity 6000.5+ on Windows
x64, Linux x64, Intel macOS, and Apple Silicon macOS with Mono; IL2CPP, Unity
IMGUI, and EditorWindow automation are not supported. See [Unity 6 desktop Mono](#unity-6-desktop-mono)
for installation and verification details.

## NuGet Packages

See the [external distribution contract](docs/distribution-contract.md) for pinned
schemas, the embedded offline Viewer, package-only consumers and acceptance gates.

- **Gua.Core:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Core)](https://www.nuget.org/packages/Gua.Core) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Core)<br>
  P/Invoke bindings for using the Gua C ABI runtime from .NET, including native assets for Windows x64, Linux x64, Intel macOS, and Apple Silicon macOS.
- **Gua.Testing:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing)](https://www.nuget.org/packages/Gua.Testing) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing)<br>
  Adds Gua locators, waits, assertions, and adapter test loops to regular .NET tests.
- **Gua.Testing.Godot:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Godot)](https://www.nuget.org/packages/Gua.Testing.Godot) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Godot)<br>
  Starts a Godot process and provides helpers for controlling and verifying a running scene through the Gua bridge.
- **Gua.Testing.Unity:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Unity)](https://www.nuget.org/packages/Gua.Testing.Unity) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Unity)<br>
  Starts a Unity process and provides helpers for controlling and verifying a running scene through the Gua bridge.
- **Gua.Runtime:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Runtime)](https://www.nuget.org/packages/Gua.Runtime) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Runtime)<br>
  Shared managed wrapper and native runtime for authors of engine adapters on the same four desktop RIDs. Use it to publish semantic frames, consume actions, complete screenshot requests, and host the Inspector bridge without duplicating P/Invoke code. Application test projects normally use an engine package instead.
- **Gua.Testing.Visual:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Visual)](https://www.nuget.org/packages/Gua.Testing.Visual) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Visual)<br>
  Adds opt-in PNG baseline comparison for rendering regressions that semantic assertions cannot detect, such as clipping, misplaced controls, incorrect assets, and unexpected overlays. Failures retain expected, actual, diff, and machine-readable comparison artifacts.
  `gua-tester` can combine those artifacts with its prebuilt Astro viewer for workflow artifacts and GitHub Pages.
- **Gua.Testing.Snapshots:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Snapshots)](https://www.nuget.org/packages/Gua.Testing.Snapshots) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Snapshots)<br>
  Opt-in deterministic UI and optional World JSON baselines with semantic differences, explicit updates, and caller-configured masking. See the [package reference](bindings/dotnet/src/Gua.Testing.Snapshots/README.md).
- **Gua.Testing.Recording:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Recording)](https://www.nuget.org/packages/Gua.Testing.Recording) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Recording)<br>
  Records repeatable user journeys as semantic operations and replays every step with correlated host completion. Use it for regression flows, bug reproduction, and sharing a scenario without storing fragile coordinates or plaintext secrets.

See the [.NET package guide](https://gua.orizika.com/docs/dotnet-packages/)
for package selection, then use the dedicated guides for
[Gua.Runtime](https://gua.orizika.com/docs/gua-runtime/),
[Visual testing](https://gua.orizika.com/visual-testing/), and
[Recording](https://gua.orizika.com/recording/) for complete
workflows and diagrams.

## MCP and Inspector

### World Object Tree

Gua publishes explicitly opted-in game-world objects separately from the Semantic UI Tree. Each object has a stable ID, semantic kind, 2D or 3D position, host-defined player visibility, tags, and flat primitive state. Godot objects opt in through the `gua_world_object` group plus `gua_world_*` metadata; Unity objects use `GuaWorldObject`. Gua never dumps every scene node or GameObject automatically.

The native bridge uses the debug view by default. Set `GUA_OBSERVATION_PROFILE=player` in the host process to project both UI and World trees before any transport sees them. Player UI requires effective ancestor visibility; World objects require host-defined semantic visibility and active ancestors. `private` removes a complete subtree. Clients cannot elevate that profile. World v1 is observation-only; MCP exposes `get_world_object_tree`, `find_world_objects`, and `wait_for_world_object`, while Inspector displays a separate World Object Tree panel.

World selectors can search within a radius by supplying `relativeToObjectId` and `maxDistance`, with an optional positive `limit`. The runtime evaluates distance in one projected snapshot (XY for `world2d`, XYZ for `world3d`), returns snapshot metadata and aligned distances, and re-evaluates fresh snapshots during waits. Distances use the host's world units; private and unknown reference IDs both produce an empty result.

`GuaAgentPolicy` can omit, redact, replace, or quantize public fields and restrict UI actions. Debug snapshots remain complete. In player mode the same projection is used by snapshots, queries, waits, diagnostics, and action authorization. Debug logs are unavailable, and screenshots are denied by default because semantic projection cannot safely redact rendered pixels; a host may explicitly opt in before starting the bridge.

Godot metadata uses `gua_world_id` (required), `gua_world_kind`, `gua_world_label`, `gua_world_visible_to_player`, `gua_world_active`, `gua_world_agent_exposure`, `gua_world_tags`, and `gua_world_state`. UI controls use `gua_agent_exposure`, `gua_agent_field_rules`, and `gua_agent_allowed_actions`; World objects accept the corresponding `gua_world_agent_*` keys. State values must be strings, finite numbers, booleans, or null and must not contain secrets. Integer values must survive the v1 C ABI `double` representation exactly; JavaScript selector clients reject integers outside the safe-integer range.

See the [World Object Tree](https://gua.orizika.com/world-object-tree/) and
[Agent exposure policy](https://gua.orizika.com/agent-policy/) guides for
engine setup, selector behavior, and Player-safe publication.

### Deterministic virtual time

Gua can pause and advance game logic that uses GuaClock as its time source. It
does not replace existing engine timers automatically. First change the game
logic to use GuaClock schedules or ticks instead of Godot `Timer`, Unity
`Time.deltaTime` / coroutines, or another native time source. Tests can then
install and control that shared clock without waiting for wall time:

```csharp
// Game-side integration (done once in the production code).
var clock = runtime.Clock;
clock.Install();
clock.Schedule(TimeSpan.FromSeconds(2), ShowMessage);

// Test-side control of the same clock.
clock.Pause();
clock.RunFor(TimeSpan.FromSeconds(2));
```

Here, `Install` activates Gua's shared virtual clock; it does not inject the
clock into arbitrary game objects. `Pause` affects only game logic already
wired to GuaClock schedules or ticks. Engine-native timers, physics, animations,
audio, OS time, and networking continue normally.
The bridge, MCP, and Inspector expose `get_clock`, `clock_install`,
`clock_pause`, `clock_run_for`, and `clock_resume`.

### Semantic game actions and raw input

Hosts can publish an explicit game-action map independent of the UI tree, then
drive buttons, axes, vectors, or text through `press_game_input_action`,
`set_game_input_action`, and `release_game_input_action`. Opt-in raw tools cover
the cross-adapter W3C physical keyboard code subset enumerated by the command
schema, pointer motion/buttons/wheel, Standard Gamepad controls, and text input.
Stateful input is isolated per connection, defaults
to a five-second lease (maximum 60 seconds), and is neutralized on expiry,
disconnect, reset, replay failure, or session disposal. The Inspector's direct
panel provides Semantic Game Action controls, one-shot physical-key presses,
held-state inspection, and an emergency **Release all** button; pointer,
gamepad, and text input are not direct Inspector controls. `gui-mcp` advertises
its fixed input-tool surface and rejects unsupported calls at invocation time,
while WebMCP registers only tools enabled by capabilities read during page-tool
registration.
Local C++ and .NET sessions poll the returned request ID for host completion;
enqueue acceptance alone does not prove that the adapter injected the input.

Large Action Maps can add `category`, `tags`, and human-facing `aliases` to each
descriptor and discover the current map with the fixed
`find_game_input_actions` API. Filters are combined with AND, tags require every
requested value, and results are ordered by ordinal Action ID. The unpaged result
defaults to 20 actions, accepts at most 100, and reports `truncated` so clients
can narrow the query. Player projection omits descriptors marked
`agentExposure: "private"` before counting or truncation; its revision changes
only when public actions, public metadata, or the context changes. Search is a
snapshot, not execution authority: enqueue and host consumption both revalidate
the latest descriptor. Do not put secrets in IDs, descriptions, categories,
tags, or aliases because Debug tools and recordings may preserve that metadata.

Unity 6000.5 integration uses `com.unity.inputsystem@1.20.0` virtual devices;
Godot injects main-thread `InputEvent` values through `Input.parse_input_event`.
Adapters advertise each input capability only after its pump and cleanup path
are initialized. The existing semantic UI `press_key` API remains unchanged;
raw keyboard gestures use `press_physical_key`.

See the [Game input guide](https://gua.orizika.com/game-input/) for capability,
ownership, lease, confirmation, and engine setup details.

- **gua-value-tools:** [![NPM Version](https://img.shields.io/npm/v/gua-value-tools)](https://www.npmjs.com/package/gua-value-tools) ![NPM Downloads](https://img.shields.io/npm/dw/gua-value-tools)<br>
  Types, validation, serialization, enum catalogs, and equality for the shared Value v1 contract. See the [package reference](packages/value/README.md).
- **gua-webmcp:** [![NPM Version](https://img.shields.io/npm/v/gua-webmcp)](https://www.npmjs.com/package/gua-webmcp) ![NPM Downloads](https://img.shields.io/npm/dw/gua-webmcp)<br>
  A browser-native adapter that registers Gua semantic UI, World Object Tree,
  and game-input tools through the page's WebMCP API.
- **gui-mcp:** [![NPM Version](https://img.shields.io/npm/v/gui-mcp)](https://www.npmjs.com/package/gui-mcp) ![NPM Downloads](https://img.shields.io/npm/dw/gui-mcp)<br>
  A thin MCP server that exposes Gua runtime actions to AI agents through the
  same WebSocket bridge used by the Inspector.
- **Gua Inspector:** [![Gua Release](https://img.shields.io/github/actions/workflow/status/link1345/gua/gua-release.yml?branch=main&label=Gua%20Release)](https://github.com/link1345/gua/actions/workflows/gua-release.yml)<br>
  A browser and Windows desktop UI for inspecting the semantic UI tree, node
  state, screenshots, and logs, and for sending runtime commands.

```ts
await game.getByRole("button", { name: "Start Game" }).click()
await expect(game.getById("loading")).toBeVisible()
```

The current implementation exposes that shape for C++ and C# over the C ABI,
adds Inspector and MCP consumers, and includes Godot 4.7 and Unity 6 adapters
and samples:

```cpp
gua::testing::get_by_role(ctx, "button", "Start Game").click();
gua::testing::wait_for_text(ctx, "Loading...").to_be_visible();
```

```csharp
GuaAssertions.GetByRole(ui, "button", "Start Game").Click();
GuaAssertions.WaitForText(ui, "Loading...").ToBeVisible();
```

`Click()` intentionally enqueues a click request; it does not directly mutate
the game. A game adapter, such as the ImGui or Godot adapter, must consume the
request on a later frame and emit the observed click event. Tests that do not
run a real engine adapter can use `GuaTestHost` for the same loop:

```csharp
using var ui = new GuaContext();
var host = new GuaTestHost(ui);
var loading = false;

host.Frame("title", frame =>
{
    frame.Button("start", "Start Game", new GuaBounds(100, 100, 240, 64));
});

GuaAssertions.GetByRole(ui, "button", "Start Game").Click();

host.Frame("title", frame =>
{
    frame.Button("start", "Start Game", new GuaBounds(100, 100, 240, 64));
});

host.DrainClickEvents(id => loading = id == "start");

host.Frame("loading", frame =>
{
    if (loading)
    {
        frame.Text("loading", "Loading...", new GuaBounds(100, 180, 240, 24));
    }
});

GuaAssertions.WaitForText(ui, "Loading...").ToBeVisible();
```

Real .NET tests should use an existing test runner such as NUnit. `Gua.Testing`
does not try to become a Vitest-style runner; it provides Gua-specific locators,
waits, assertions, and adapter test loops inside normal NUnit tests. One C# file
can contain multiple `[Test]` methods:

```csharp
using Gua.Core;
using Gua.Testing;
using NUnit.Framework;

[TestFixture]
public sealed class TitleScreenTests
{
    [Test]
    public void StartClickShowsLoadingText()
    {
        using var _ = GuaAssertionScope.UseNUnit(Assert.Fail);
        using var ui = new GuaContext();
        var host = new GuaTestHost(ui);

        host.Frame("title", frame =>
        {
            frame.Button("start", "Start Game", new GuaBounds(100, 100, 240, 64));
        });

        GuaAssertions.GetByRole(ui, "button", "Start Game").Click();
    }
}
```

The repository includes a runnable NUnit sample in `examples/dotnet-nunit`.

Godot scene tests can use the `Gua.Testing.Godot` package to start a Godot
process and assert against the live Gua bridge:

```csharp
using var host = GodotSceneTestHost.Load("game/scenes/title_screen.tscn");

GuaAssertions.GetByRole(host.Context, "button", "開始").ToBeVisible();
host.Click("CenterPanel/Content/ButtonBox/StartButton", nextScene: "game/scenes/village_list.tscn");
GuaAssertions.GetByRole(host.Context, "button", "Create").ToBeVisible();
```

For v0.2.0 and later, repository-local `GuaLiveAssertions` helpers can migrate
directly to `WaitForId` / `WaitForText` / `WaitForVisible` / `WaitForEnabled`,
locator `WaitForCount*`, and correlated node actions such as `ClickAsync`.
Wait-returned expectations retain the successful completed-frame snapshot;
use `Refresh()` or `WaitUntil*` when a later frame is required. Repository
`GodotHostFactory` wrappers should keep only game-specific process and fixture
policy while `GodotSceneTestHost` owns executable/project discovery, automatic
loopback ports, rendered/headless startup, bridge diagnostics, and reset policy.
All APIs are additive over the existing C ABI and WebSocket protocol.

Failure diagnostics can be coordinated through the framework-independent
`GuaDiagnosticsSession`. Assertions and completed actions use the same artifact
layout, return typed absolute paths/media types/secondary capture errors, and
can attach through a callback. Godot sessions can include live process metadata,
stdout/stderr, runtime version, and an opt-in on-demand screenshot; successful
tests do not create artifacts unless capture is explicitly requested.

Want to try that in GitHub Actions without wiring every setup step by hand?
[`link1345/gua-tester`](https://github.com/link1345/gua-tester) provides
versioned workflows for both Godot and Unity. The Godot action downloads Godot,
links the released addon, sets `GODOT_EXECUTABLE`, and runs the external .NET
tests:

For a typical consumer repository, the workflow can be as small as:

```yaml
- uses: link1345/gua-tester/godot@v3.1
  with:
    project-path: game
    test-project: tests/GuaTester.Tests.csproj
    godot-version: "4.7"
    godot-status: stable
```

The Unity reusable workflow installs the released UPM package, builds the
requested desktop Mono Player, transfers it to the matching runner, and runs
the `Gua.Testing.Unity` NUnit project:

```yaml
jobs:
  unity:
    if: github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository
    uses: link1345/gua-tester/.github/workflows/unity.yml@v3.1
    with:
      project-path: game
      scene-path: Assets/Scenes/Title.unity
      test-project: tests/GuaTester.Unity.Tests.csproj
      artifact-key: game
      platform: LinuxX64
      unity-version: auto
      gua-tag: gua-v1.0.4
    secrets:
      UNITY_EMAIL: ${{ secrets.UNITY_EMAIL }}
      UNITY_PASSWORD: ${{ secrets.UNITY_PASSWORD }}
      UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
      UNITY_SERIAL: ${{ secrets.UNITY_SERIAL }}
```

Keep the UPM release selected by `gua-tag` aligned with the
`Gua.Testing.Unity` NuGet version. Unity credentials are unavailable to fork
pull requests, so skip the Unity job for untrusted forks. The reusable workflow
fixes the Windows test runner display at 1920x1080 before launching the Player.
Gua v1.0.4 and later include the cross-platform UPM native assets required by
the Linux and macOS platform values.

```cpp
context.log(gua::LogLevel::info, "title screen opened");
context.set_screenshot("data:image/png;base64,...", 1280, 720);

std::cout << context.ui_tree_json() << '\n';
std::cout << context.logs_json() << '\n';
std::cout << context.screenshot_json() << '\n';
```

```csharp
while (ui.TryPollEvent(out var e))
{
    if (e.Type == GuaEventType.Click && e.NodeId == "start")
    {
        ShowLoading();
    }
}
```

Gua is the runtime UI layer between a game and its automation tools. It works
alongside game engines, editor tools, test runners, and AI coding agents rather
than replacing them.

## Scope

The first implementation focuses on a small, stable core:

- Protocol specification and JSON schemas
- C ABI runtime core
- Thin C++ wrapper
- ImGui adapter
- C++ and C# testing helpers
- .NET P/Invoke binding over the C ABI
- Inspector for tree, node detail, screenshot, logs, and runtime commands
- MCP server for AI-agent access to the runtime bridge
- Experimental Godot 4.7 C# sample for basic tree reflection and button clicks
  over the shared native runtime bridge
- Recommended Godot 4.7 GDScript addon using the same runtime bridge through
  GDExtension, including the full standard-Control adapter
- Unity 6 runtime package for UI Toolkit, uGUI, and TextMeshPro, plus external
  Editor Play Mode and desktop Mono Player test hosts

Engine-specific integrations remain adapters built on top of the protocol, not
the center of the project. Godot and Unity are the current supported adapters;
additional engines can follow the same boundary.

## Native Toolchain

Runtime compatibility can be inspected through the additive C ABI
`gua_copy_version_json`, the WebSocket `get_version` command, or .NET
`GetVersion()`. The response follows `protocol/schema/version.schema.json` and
lists stable capability IDs. Non-Godot runtimes report `godotPluginVersion` as
`null`; release workflows inject the release version and commit build ID.

Windows native development uses MSVC as the primary toolchain:

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug
```

The native core, WebSocket bridge, runtime shared library, and native bridge
example also build on Linux with GCC or Clang and on Intel and Apple Silicon
macOS with Apple Clang. Godot and Unity desktop adapters support the same four
RIDs described above. iOS and Android are
not currently supported targets.

## .NET Testing

The .NET packages are published on NuGet and can also be packed locally. To
pack `Gua.Core` or `Gua.Runtime` with native assets, first stage all four RID
directories described in their package READMEs and pass the absolute staging
path as `GuaNativeAssetsRoot`:

```powershell
$nativeAssets = "C:\absolute\path\to\native-assets"
dotnet pack bindings/dotnet/src/Gua.Core/Gua.Core.csproj --configuration Release -p:GuaNativeAssetsRoot=$nativeAssets
dotnet pack bindings/dotnet/src/Gua.Testing/Gua.Testing.csproj --configuration Release
dotnet pack bindings/dotnet/src/Gua.Runtime/Gua.Runtime.csproj --configuration Release -p:GuaNativeAssetsRoot=$nativeAssets
dotnet pack bindings/dotnet/src/Gua.Testing.Unity/Gua.Testing.Unity.csproj --configuration Release
dotnet pack bindings/dotnet/src/Gua.Testing.Godot/Gua.Testing.Godot.csproj --configuration Release
dotnet pack bindings/dotnet/src/Gua.Testing.Visual/Gua.Testing.Visual.csproj --configuration Release
dotnet pack bindings/dotnet/src/Gua.Testing.Snapshots/Gua.Testing.Snapshots.csproj --configuration Release
dotnet pack bindings/dotnet/src/Gua.Testing.Recording/Gua.Testing.Recording.csproj --configuration Release
```

The packages are written to `artifacts/packages`. `Gua.Testing` declares a NuGet
dependency on the matching `Gua.Core` package, so a test project only needs the
testing package:

```xml
<PackageReference Include="Gua.Testing" Version="0.5.0-preview.3" />
```

`Gua.Core` is also delivered as a NuGet package and `Gua.Testing` depends on the
matching version. Native assets are included for `win-x64`, `linux-x64`,
`osx-x64`, and `osx-arm64`, so a normal package restore/build copies the correct
library to the consuming app or test output. `Gua.Runtime` carries the matching
Inspector bridge runtime for those RIDs. `GUA_NATIVE_DIR` and
`GUA_RUNTIME_NATIVE_DIR` remain local-build overrides. A missing or wrong
architecture native library is reported with the exact checked paths.

Run the standalone testing sample:

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug --target gua
$env:GUA_NATIVE_DIR = "$PWD\build\windows-msvc-debug\native\gua-core\Debug"
dotnet run --project examples/dotnet-testing/GuaDotNetTestingSample.csproj
```

Run the NUnit testing sample:

```powershell
cmake --preset windows-msvc-release
cmake --build --preset windows-msvc-release --target gua
dotnet pack bindings/dotnet/src/Gua.Core/Gua.Core.csproj --configuration Release
dotnet pack bindings/dotnet/src/Gua.Testing/Gua.Testing.csproj --configuration Release
dotnet test examples/dotnet-nunit/GuaDotNetNUnitSample.csproj
```

### Network mocks and test state preparation

Gua does not provide its own network mock engine or automatically intercept
arbitrary game traffic. You can use existing mock libraries, mock servers, or
game-owned test implementations alongside Gua. Choose them for the game's
language, runtime, and the dependency boundary you can replace.

For C#/.NET, [Moq](https://github.com/devlooped/moq/wiki/Quickstart) is one option
for substituting dependency objects through interfaces or overridable members.
The game must use the substituted object; a mock in the test runner does not
replace dependencies in a separate game process. Moq does not automatically
capture native network traffic or arbitrary external processes. Check support
for your target runtime rather than assuming it works in every engine or build.

Preparing a starting state does not necessarily require a network mock. A test
save, game initialization routine, or preparation API on a test server can set
up the required inventory, progression, or test accounts. These are game-owned
choices, not built-in Gua preparation APIs. Let the test fixture prepare that
state, verify the prerequisites through observable game state before acting,
and clean up the state and resources it owns afterward. The
[NUnit sample](examples/dotnet-nunit/README.md) shows setup/teardown and Gua
context isolation; resetting a Gua context does not restore saves, accounts,
or backend data.

Record which boundaries were mocked and which services were exercised for real.
Checking client UI against a mock response verifies that client behavior; it
does not verify the real backend or authentication. Use the real services in a
separate integration test when those are the behavior under test.

### Unity 6 desktop Mono

`Gua.Core`, `Gua.Testing`, `Gua.Testing.Visual`, and
`Gua.Testing.Recording` target both `net10.0` and `netstandard2.1`.
Unity 6 projects using the default **.NET Standard 2.1** API Compatibility
Level can load the managed assemblies without changing the native C ABI.
The UPM archive includes OS/CPU-scoped native plugins for Windows x64, Linux x64,
and Universal 2 macOS. For a manual Windows installation, place the
managed assemblies and their NuGet dependency closure under
`Assets/Plugins/Gua/Managed`, and place `gua.dll` under
`Assets/Plugins/x86_64`. Unity's Plugin Import Settings must enable the DLL for
the Windows Editor and Windows Standalone x86_64 targets.

Build the precompiled Unity Package Manager artifact and `.tgz` archive with:

```powershell
.\scripts\build-unity-package.ps1
```

Each automated Gua GitHub Release also attaches
`com.link1345.gua-<version>.tgz`. Download that file and install it from
Unity Package Manager with **Add package from tarball**. Release builds require
the `UNITY_LICENSE`, `UNITY_EMAIL`, and `UNITY_PASSWORD` secrets in the GitHub
`release` environment so the precompiled Unity assemblies are produced by the
same release workflow as the Windows native plugins.

The package starts automatically and reflects UI Toolkit, uGUI, and
TextMeshPro runtime UI. `GuaId` and `GuaScreen` are optional overrides; game
code does not register semantic nodes manually. `Gua.Testing.Unity` provides
Editor Play Mode and desktop Mono Player hosts. See
[`examples/unity-smoke`](examples/unity-smoke/README.md) for the verified Unity
6000.5.3f1 fixture. All four desktop RIDs, Unity 6000.5+, and Mono are supported;
IL2CPP, IMGUI, and EditorWindow UI
automation are not.

## Inspector

The Inspector is a React application that consumes Gua protocol snapshots. It is
not tied to MCP. The UI talks to a `GuaInspectorClient` abstraction so transport
implementations can be added for mock data, WebSocket bridges, HTTP bridges,
MCP, saved files, or the native runtime bridge.

Run the browser version:

```powershell
bun run --filter @gua/inspector dev
```

Run the sample WebSocket bridge in another terminal:

```powershell
bun run bridge:ws
```

Or build and run the native C++ bridge that serves a real `gua::Context`:

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug --target gua-native-bridge-example
.\build\windows-msvc-debug\examples\native-bridge\Debug\gua-native-bridge-example.exe
```

The ImGui example also hosts the same WebSocket bridge while the UI is running:

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug --target gua-cpp-imgui-example
.\build\windows-msvc-debug\examples\cpp-imgui\Debug\gua-cpp-imgui-example.exe
```

Then connect the Inspector to:

```text
ws://127.0.0.1:8765
```

The ImGui bridge pushes snapshot notifications while the UI is running, so the
Inspector updates without polling. The `Poll` toggle in the Inspector remains as
a fallback for bridges that only implement request/response.

The Inspector Automation panel can record semantic actions issued from the UI,
download or import a `recording.schema.json` document, replay every semantic
action, and supply sensitive replay values from an in-memory JSON map. Its Visual
comparison controls accept the current screenshot or a selected image as a
baseline, compare in the browser, and download Actual/Expected/Diff images plus a
machine-readable manifest. Browser Inspector files are explicit downloads; it
does not silently write arbitrary local paths.
Coordinate fallback recordings are accepted as schema v1 documents but are not
executed by the Inspector; replay remains semantic-target-only by default.

The bridge speaks the same JSON command shape used by Gua runtime adapters:

```json
{ "id": 1, "type": "get_ui_tree" }
{ "id": 2, "type": "click_node", "nodeId": "start" }
```

Build the static Inspector:

```powershell
bun run --filter @gua/inspector build
```

Run the Tauri desktop shell during development:

```powershell
bun run --filter @gua/inspector tauri:dev
```

Tauri requires a Rust toolchain in addition to the JavaScript dependencies.

## MCP

The MCP server is a thin protocol consumer over the same bridge used by the
Inspector. Start a runtime bridge first:

```powershell
bun run bridge:ws
```

Then run the MCP server over stdio:

```powershell
bun run mcp
```

The MCP server is published to npm as `gui-mcp`. MCP clients can start it with:

```powershell
bunx gui-mcp@latest mcp
```

By default it connects to `ws://127.0.0.1:8765`. Override that for another
runtime adapter:

```powershell
$env:GUA_BRIDGE_URL = "ws://127.0.0.1:8765"
bunx gui-mcp@latest mcp
```

The MCP tool surface is:

```text
get_ui_tree
get_world_object_tree
find_world_objects
wait_for_world_object
get_spatial_info
query_spatial_batch
click_node
focus_node
set_value
set_checked
select
scroll
press_key
get_game_input_actions
find_game_input_actions
press_game_input_action
set_game_input_action
release_game_input_action
get_game_input_state
release_all_game_inputs
key_down
key_up
press_physical_key
pointer_move
pointer_button_down
pointer_button_up
pointer_wheel
gamepad_button_down
gamepad_button_up
set_gamepad_axis
reset_gamepad
text_input
wait_for_node
get_screenshot
get_logs
get_clock
clock_install
clock_pause
clock_run_for
clock_resume
start_recording
stop_recording
save_recording
replay_recording
compare_screenshot
get_visual_artifacts
run_test
```

The spatial tools require an explicitly bound Testing/Debug provider. See the
[English/Japanese spatial guide](docs/spatial-transport-r1.md) for host opt-in,
bounded reads, Trace/report evidence and the measured support table. Built-in
browser privilege is Unsupported; these reads do not become Recording actions.

Recording, baseline, and visual failure files default to `.gua`. Set
`GUA_ARTIFACT_DIR` to choose a different root. Names supplied to MCP tools cannot
escape that root. Semantic action tools wait for request-ID-correlated host
completion when the connected bridge supports it.

## Release automation

Pushing a manually created `gua-vX.Y.Z` tag runs the release workflows. The
Inspector, Godot addon, and Unity package are built together and attached to the
matching GitHub Release, while the MCP and .NET packages publish the same
version to npm and NuGet. Changes pushed to `main` do not publish packages.
The ImGui adapter remains available as source in this repository but is not a
public release asset.

The public GitHub Release contains these versioned assets (using `1.0.0` as an
example):

```text
gua-godot-addon-v1.0.0.zip
com.link1345.gua-1.0.0.tgz
gua-native-win-x64-v1.0.0.zip
gua-native-linux-x64-v1.0.0.zip
gua-native-osx-x64-v1.0.0.zip
gua-native-osx-arm64-v1.0.0.zip
Gua.Inspector_<inspector-version>_x64-setup.exe
Gua.Inspector_<inspector-version>_x64_en-US.msi
```

`gua-godot-addon-v1.0.0.zip` contains one `addons/gua` tree with Debug and
Release GDExtensions for all four desktop RIDs and Godot Web. Files
inside that addon, Unity WebGL static libraries, and an ImGui ZIP are not
published as separate GitHub Release assets; the static libraries are already
included in the Unity Package Manager archive.
Each `gua-native-<rid>-v1.0.0.zip` contains the `Gua.Core` and `Gua.Runtime`
shared libraries for that RID plus `LICENSE`.

The MCP workflow uses npm trusted publishing through GitHub Actions OIDC. The
`gua-world-tools`, `gua-webmcp`, and `gui-mcp` packages must each be configured
on npm with this repository, the `.github/workflows/mcp-publish.yml` workflow,
and the `release` environment.

## Godot 4.7 C# Sample

> **Experimental — basic functionality only.** This sample demonstrates the
> shared runtime bridge, basic semantic tree reflection, screenshots, and button
> clicks. It is not feature-equivalent to the GDScript adapter. For new Godot
> integrations, including .NET-enabled projects, use the GDScript addon below;
> Godot projects can use it alongside C# game scripts.

The v0.5 C# runtime sample lives in `examples/dotnet-godot`. It is a minimal
Godot 4.7 project using `Godot.NET.Sdk/4.7.0`, `net10.0`, project references to
`Gua.Core` and `Gua.Testing`, and a runtime addon in `addons/gua`. The addon
P/Invokes `gua_runtime.dll`, which owns both the Gua context and the Inspector
WebSocket bridge.

Build it with:

```powershell
dotnet build examples/dotnet-godot/GuaGodotSample.csproj -v:minimal
```

The sample uses `Gua.Godot.GuaGodotRuntime` from the addon, attaches it to the
root Godot `Control`, and starts an Inspector bridge on `ws://127.0.0.1:8765`.
The adapter collects standard controls into the semantic tree and dispatches
external click requests through normal Godot button signals.
`examples/dotnet-monogame` is still present as a future placeholder.

## Godot 4.7 GDScript Addon

This is the recommended Godot integration for both GDScript projects and
.NET-enabled projects. Its standard-Control adapter implements the complete
Godot feature set currently documented by Gua.

The GDScript-facing adapter lives in `native/gua-godot` and builds a thin
GDExtension wrapper over `native/gua-runtime`. On Windows the GDExtension embeds
that runtime implementation so Debug and Release addon binaries do not depend
on a shared, configuration-ambiguous `gua_runtime.dll`. It exposes `GuaContext`
to GDScript without reimplementing the runtime core or the Inspector bridge:

```gdscript
var ui := GuaContext.new()
ui.begin_frame("title")
ui.register_node("start", "button", "Start Game", Rect2(512, 312, 256, 56), true, true)
ui.end_frame()
ui.start_inspector_bridge(8765)
ui.enqueue_click("start")

while true:
    var event := ui.poll_event()
    if event.is_empty():
        break
```

Build the Windows Debug and Release GDExtensions in separate configured build trees:

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug --target gua-godot
cmake --preset windows-msvc-release
cmake --build --preset windows-msvc-release --target gua-godot
```

The builds write configuration-specific DLLs into
`examples/godot-gdscript/addons/gua/bin`. Godot loads the Debug DLL in the
editor and Debug exports, and the Release DLL in optimized Windows exports.
Open `examples/godot-gdscript` with Godot 4.7 and run the project. The running
game process starts an Inspector bridge on `ws://127.0.0.1:8765`, which Gua
Inspector can connect to while the game is open. The addon includes `plugin.cfg`
only for standard Godot addon packaging; Gua's runtime API is provided by
`gua.gdextension`, not by an editor MCP.

For game scripts, instantiate the GDScript adapter through an explicit preload
instead of relying on `class_name` registration order:

```gdscript
const GuaAutoAdapterScript := preload("res://addons/gua/gua_auto_adapter.gd")

var ui := GuaAutoAdapterScript.new()
```

The adapter resolves the native `GuaContext` class through `ClassDB` on first
use and verifies that required methods such as `consume_click_request` exist
before dispatching Inspector click requests. If that check fails, rebuild
`gua-godot`; the stale vendored DLL is the problem, not the game script.

To publish a world object, opt a `Node2D` or `Node3D` in explicitly:

```gdscript
$Door.add_to_group(&"gua_world_object")
$Door.set_meta(&"gua_world_id", "door-a")
$Door.set_meta(&"gua_world_kind", "door")
$Door.set_meta(&"gua_world_visible_to_player", true)
$Door.set_meta(&"gua_world_state", {"locked": true})
```

Run the GDScript smoke check with:

```powershell
.\scripts\run-godot-smoke.ps1 -GodotExecutable 'C:\path\to\Godot_v4.7-stable_win64_console.exe'
```

The wrapper places Godot's temporary `user://` data under the ignored `build/`
directory. This also prevents a Godot 4.7 Windows access violation when the
normal `%APPDATA%` location is unavailable in a restricted test environment.
The sample also disables Godot's built-in file logging because smoke output is
collected from stdout and Godot 4.7 can crash while creating `user://logs` when
that location is unavailable.

## Repository Layout

```text
protocol/             Protocol specs and JSON schemas
native/gua-core/      C ABI runtime core and C++ reference implementation
native/gua-runtime/   Shared native runtime bridge for Godot C# and GDScript
native/gua-imgui/     ImGui adapter layer
native/gua-testing/   C++ testing helpers over the C ABI
native/gua-godot/     Godot GDExtension adapter for GDScript
bindings/dotnet/      .NET P/Invoke binding and C# testing helpers
bindings/dotnet/src/Gua.Testing.Godot/
                      Godot process test helpers over Gua.Testing
bindings/dotnet/src/Gua.Testing.Visual/
                      Opt-in PNG baseline comparison helpers
bindings/dotnet/src/Gua.Testing.Recording/
                      Semantic operation recording and correlated replay
packages/mcp/         Published MCP server package
packages/inspector/   Browser and Tauri desktop Inspector UI
examples/             Minimal demos and samples, including the Godot C# sample
docs/                 Native toolchain and change-audit guidance
```

## Change auditing for contributors

After repository edits, Gua's Codex instructions run focused validation and one
read-only `gua_auditor` pass over the cumulative diff, including relevant
untracked files. For a deliberate pull-request-wide review, run:

```powershell
./scripts/run-local-pr-audit.ps1 -Base origin/main
```

The harness reports reproducible findings without committing, pushing, or
posting GitHub comments. See [Bug-hunting subagent and local PR audit](docs/bug-hunting-subagent.md)
or the [contributor audit guide](https://gua.orizika.com/docs/change-audit/).

## License

MIT
