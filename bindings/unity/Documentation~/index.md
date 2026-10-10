# Gua for Unity

## Unity側で公開・操作を担当するpackage

Play Modeやdesktop Mono PlayerでUIを公開し、外部テストの要求をUnityの操作へ適用するruntime adapterです。
例えばボタンのrole/name/stateをframeにまとめて公開し、bridgeから来た要求をgame側でconsumeし、
request IDに対応する完了を報告します。呼出し側はその後の画面変化も確認します。

[GuaUnityRuntime](../Runtime/GuaUnityRuntime.cs)が自動起動・UI収集・操作処理の入口です。
外部processの起動とassertionは
[Gua.Testing.Unity](../../dotnet/src/Gua.Testing.Unity/README.md)が担当します。
UIは自動収集しますが、World objectとgame inputは下の明示登録・opt-inが別途必要です。
Playerは公開範囲と入力許可を制限するprofileで、Debug初期化だけではbrowserへ権限を与えません。

以下の対象platformとengine準備を読み、
[共通の確認準備と実engine確認の区別](../../../docs/developer-reading-guide.ja.md)へ進みます。
click成功はdispatchした入力の報告なので、applicationの期待状態もassertしてください。
対応範囲と利用手順は以下を参照してください。

The package automatically starts the Gua runtime in Play Mode and desktop Mono
players on Windows x64, Linux x64, Intel macOS, and Apple Silicon macOS. It
reflects UI Toolkit, uGUI, and TextMeshPro runtime controls and listens on
`GUA_BRIDGE_PORT` (8765 by default). Add `GuaId` only where a stable explicit id
is required; semantic registration is otherwise automatic.

The stable support range is Unity 6000.5 or newer with Mono on Windows x64,
Linux x64, Intel macOS, and Apple Silicon macOS. The package contains
precompiled managed assemblies and OS/CPU-scoped native libraries for the
supported Editors and Players. IL2CPP, IMGUI, and EditorWindow UI automation
remain outside that stable range; the WebGL path below is experimental.

## UI Toolkit clicks

Button `click` and Tab `click`/`select` send pointer move/down/up at the
target's center in panel coordinates (the header for a Tab). This invokes
the normal UI Toolkit pointer and click handlers. Ancestor ScrollViews scroll
the target into view first; virtualized rows that have not been created are
not materialized by this operation. If the panel cannot pick the target or
one of its descendants at that point, the action fails with `hidden` instead
of clicking through a covering element. Success reports dispatched input;
tests should also assert the application's resulting state.

## Unity WebGL and browser-native WebMCP (experimental)

WebGL builds install a tab-local `__guaUnityWebPort` from
`Runtime/Plugins/WebGL/GuaWebMcp.jslib`. A surrounding page passes it to
`gua-webmcp` with `createUnityWebGlBridge()` and `registerGuaWebMcp()`. Calls
remain in the page. Action promises resolve from request-correlated host
completion after Unity applies the action, not when it is enqueued.
UI reads and action enqueueing cross Player-only runtime entry points, so a
local Debug Inspector does not make private nodes available to browser tools.
The same bridge exposes the host-filtered World Object Tree through the
read-only `get_world_object_tree`, `find_world_objects`, and
`wait_for_world_object` tools. Shared browser-safe world contracts are provided
by `gua-world-tools`; WebMCP callers cannot elevate the runtime observation
profile or invoke actions on world objects.
The existing find and wait tools accept `relativeToObjectId`, `maxDistance`, and
an optional positive `limit`. Unity publishes positions in Unity world units;
the native core evaluates a single projected snapshot and returns its
epoch/frame/revision plus aligned distances.
When `GuaGameInputMap` initializes the input pump, the same page bridge exposes
only the matching Semantic Game Action and Raw Input capabilities. The Action
Map component exposes category, aliases, tags, and Player agent exposure;
`find_game_input_actions` performs bounded discovery without generating dynamic
tools. Keep secrets out of descriptor metadata. Calls wait
for correlated host completion and use a page-owned session that is released on
abort, timeout, bridge uninstall, or runtime destruction.

The WebGL build must include the Gua C ABI runtime as a WebAssembly native plugin;
the managed adapter remains a P/Invoke wrapper and does not implement another UI
model. The initial Unity bridge does not advertise screenshot support.

## Semantic game actions and raw input

Add `GuaGameInputMap` to one scene object and register only the actions intended
for automation. Game code reads semantic values with
`GuaUnityRuntime.GetGameInputValue` or subscribes to `GameInputChanged`.
Enabling **Raw Input** creates virtual keyboard, mouse, and gamepad devices with
Unity Input System 1.20.0. The adapter queues host-frame state events and
neutralizes/removes the devices when it stops. Raw capabilities are omitted
when Input System support or the opt-in map setting is unavailable.
`AllowPlayerAgentSemanticInput` and `AllowPlayerAgentRawInput` are independent,
default-off permissions for WebMCP/Public Agent callers. `EnableRawInput` alone
only initializes the local Debug path and never grants browser input access.
Physical keyboard tools accept the cross-adapter W3C code subset enumerated by
`commands.schema.json`, including left/right modifiers and common numpad operators.
When scenes change, the persistent runtime neutralizes the previous map and
publishes the `GuaGameInputMap` found in the new scene before accepting more
input.

## World objects

Add `GuaWorldObject` only to scene objects that are safe to observe. Assign a
stable `Id`, semantic `Kind`, 2D/3D space, player visibility, exposure, tags,
and primitive state explicitly:

```csharp
var door = gameObject.AddComponent<Gua.Unity.GuaWorldObject>();
door.Id = "door-a";
door.Kind = "door";
door.Space = Gua.Core.GuaWorldSpace.World2D;
door.VisibleToPlayer = true;
door.SetState("locked", true);
```

The adapter publishes global transform positions each frame and links the
nearest opted-in ancestor. It does not expose ordinary GameObjects or UI objects
automatically. Do not place secrets in labels, tags, or state.

Add `GuaAgentPolicyComponent` to a uGUI or `GuaWorldObject` GameObject to mark it
private, transform Player-visible fields, or override allowed UI actions. For UI
Toolkit and custom adapters, call `GuaUnityAdapterRegistry.SetAgentPolicy` with
the reflected target object. These policies affect Player profile only.
Enable `Override Exposure` only when the component should replace the exposure
already declared by `GuaWorldObject`; field-only policies inherit that setting.
