# gua-webmcp

## browser内のgameへツールをつなぐ

Godot Web ExportやUnity WebGLのページ内で、ブラウザーのツール呼出しを
同じページのengine bridgeへ渡すadapterです。
例えばUIを読み、確認済みのボタン操作を送り、engineが適用した完了を受け取ります。
外部MCP serverの起動やWebSocket接続はこのpackageの役割ではありません。

[registerGuaWebMcp / createGodotWebBridge / createUnityWebGlBridge](src/index.ts)が入口です。
engineが用意したportを接続し、利用できるcapabilityだけをtoolとして登録します。
ownerはこのページの保持入力と完了の所有者です。timeout・取消・解除・engine終了では
その入力を解放します。既にconsumeした操作の副作用がなかったとは判断できません。
Player公開範囲とgame inputの許可はhostが決め、tool引数では昇格できません。

以下のport準備とfeature detectionを確認し、
[共通準備とWebMCP test/check](../../docs/developer-reading-guide.ja.md)で登録・取消・完了待機を確認します。
実験的なbrowser API・実engine exportの確認は別に必要です。
fake modelContextの成功はbrowser/物理機能の実行証拠ではありません。

Browser-native WebMCP adapter for a Gua-enabled Godot Web Export or Unity WebGL page.
It registers tools on the experimental `document.modelContext` API and calls an
engine bridge in the same page. It does not start an MCP server or WebSocket.
Alongside Semantic UI Tree actions, the Godot and Unity bridge helpers register
the read-only `get_world_object_tree`, `find_world_objects`, and
`wait_for_world_object` tools when the engine-owned world adapter is available.
The helpers also register the fixed `find_game_input_actions` discovery tool and
Semantic Game Action / Raw Input tools only for
capabilities reported by an initialized engine input pump. One page-local input
owner is used for all such calls; timeout, cancellation, unregister, port
replacement, and engine shutdown release its held input.
World types and selector definitions come from the public `gua-world-tools`
package; observation remains host-filtered and browser callers cannot request a
debug profile.
The existing find and wait tools accept `relativeToObjectId`, `maxDistance`, and
an optional positive `limit`. Nearby results carry the projected snapshot's
epoch/frame/revision and one distance for every returned match.
The engine ports always use the runtime's Player projection for UI and World
observations and Player authorization for actions. This is independent of the
profile used by a local Inspector, and no WebMCP tool exposes a profile argument.

```ts
import { createGodotWebBridge, registerGuaWebMcp } from "gua-webmcp";

const registration = await registerGuaWebMcp(createGodotWebBridge());
if (!registration.supported) console.info(registration.error);
```

Only objects explicitly opted into the engine adapter are observable. World v1
does not expose actions, arbitrary scene traversal, or host method invocation.

For Unity WebGL, use `createUnityWebGlBridge()` instead. The engine export must
run in the same document so that its `__guaGodotWebPort` or
`__guaUnityWebPort` is available before constructing the bridge.

The engine bridge is the source of truth. Its `performAction()` promise must not
resolve on enqueue; it resolves with the request-correlated completion emitted
after the engine adapter applies the action. `performAction(request, { signal })`
must cancel an accepted request that is still queued when the signal aborts;
`registerGuaWebMcp` uses this to propagate caller cancellation and its configured
action timeout into Godot or Unity. If the host already consumed the action, the
caller remains cancelled while the engine bridge drains its eventual correlated
completion. Successful tool executions return the structured Gua result directly;
they do not wrap serialized JSON in an MCP-style text content block. `getScreenshot` is an optional read
of the latest published image, and the tool is not registered when the engine
has no drawable-frame readback path.

Feature detection is non-fatal. Each browser tab creates its own bridge and tool
registrations, so no Gua session router or cross-tab session ID is introduced.
Actions with `requiresConfirmation` are checked against the current action map
immediately before dispatch and require `confirmed: true`.

The Godot Web addon ships separate Debug and Release GDExtensions. Enable
`Extension Support` in the Web export preset so Godot selects the matching
`web.wasm32.single.debug` or `web.wasm32.single.release` library before this
package connects to `window.__guaGodotWebPort`.
