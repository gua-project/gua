# gui-mcp

## native gameをMCPから操作する

AIツールから「設定画面を読み、音量を変えて確認する」ために、MCPの呼出しを
実行中のgameのWebSocket bridgeへ渡すserverです。gameを起動・実装するものではありません。
接続先gameがframeを公開し、serverがtreeを読み、操作要求を送信し、
request IDに対応する完了を待ちます。必要な画面変化はその後の読取で確認します。

[MCP server入口runGuaMcpServer](src/index.ts)から、tool登録・bridge接続・完了待機を追えます。
送信前の失敗と送信後の完了不明は下のoutcome表で区別します。
通信断後に操作を無条件に再送すると、既に実行した操作を二重に行う可能性があります。
Worldは読取だけ、game inputとspatialはhostの明示許可と能力確認が必要です。
仮想時間はGuaClockを使う処理だけに作用します。

以下のUsageで接続先・成果物directoryを設定し、
[共通準備とMCP test/check](../../docs/developer-reading-guide.ja.md)でclient処理を確認します。
そのunit testは任意のgameでの実操作やengineの対応を保証しません。

Virtual-time tools are `get_clock`, `clock_install`, `clock_pause`,
`clock_run_for`, and `clock_resume`. They control only work explicitly connected
to GuaClock, not engine-global time.

`gui-mcp` is the MCP server for the Gua runtime UI automation protocol.

It proxies MCP tool calls to a running Gua WebSocket bridge, so game runtimes keep
owning the semantic UI tree while MCP clients consume the protocol.

## Usage

```sh
bunx gui-mcp@latest mcp
```

The server connects to `ws://127.0.0.1:8765` by default. Set
`GUA_BRIDGE_URL` for another runtime adapter and `GUA_ARTIFACT_DIR` to control
where recordings, baselines, and visual failure artifacts are written. The
artifact directory defaults to `.gua`; tool-provided names are sanitized and
cannot escape that directory.

## Semantic actions

AI clients can inspect the tree and invoke all protocol v1 semantic actions:

- `get_ui_tree`, `wait_for_node`, `get_screenshot`, `get_logs`
- `get_world_object_tree`, `find_world_objects`, `wait_for_world_object`
- `click_node`, `focus_node`, `set_value`, `set_checked`, `select`, `scroll`, `press_key`
- `run_test` for a small wait/click sequence

When the bridge returns a `requestId`, action tools poll the correlated host
completion event. Enqueue acceptance alone is not reported as completion.

Semantic action tool failures retain `isError: true`. Transport failures also
include `outcome`, `stage`, and `requestSent` in the JSON text content:

- `not_sent` / `before_send`: the action was not submitted to the WebSocket,
  including failure to connect during the UI preflight.
- `completion_unconfirmed` / `awaiting_receipt`: the action was submitted, but
  its acceptance response was not obtained. `bridgeCommandId` identifies the
  submitted bridge command; a host `requestId` is not invented.
- `completion_unconfirmed` / `awaiting_completion`: acceptance supplied a
  `requestId`, but the correlated completion could not be obtained. Both IDs
  are retained when available.
- `completion_unconfirmed` / `awaiting_observation`: a legacy bridge returned
  a null receipt, but the subsequent UI observation could not be obtained.

Submission means the WebSocket send returned; it does not prove host receipt or
execution. Completion polling and legacy post-action observation stay on that
connection and stop on disconnect. A normal close, game log, or process exit
code (including zero) does not confirm the operation. Do not automatically
retry an unconfirmed action, particularly an exit action. Successful correlated
completion, explicit bridge rejection, and the legacy null-receipt success
format remain compatible.

World tools are read-only and use the observation profile fixed by the host. Set
`GUA_OBSERVATION_PROFILE=player` on the native host for player-facing MCP use;
tool arguments cannot elevate it to debug. `find_world_objects` accepts ID,
kind, label, tag, parent scope, visibility, active, and primitive state criteria.
It also accepts paired `relativeToObjectId` and nonnegative `maxDistance`, plus
an optional positive `limit`, for deterministic nearby search in host world
units. Query results include World snapshot metadata and aligned distances.
There is deliberately no world action tool.

The same host profile also projects the Semantic UI Tree and authorizes UI
actions before the bridge responds. In player mode, private or effectively
hidden nodes are indistinguishable from missing nodes, field rules are applied
before queries and waits, debug logs are omitted, and screenshots are disabled.
Neither `gui-mcp` nor WebMCP tool schemas accept an observation-profile override.

## Recording and replay

The existing replay tool is sequential: completion time is added before each
recorded delay. It does not advertise Timed Segment, strict application time or
same-tick application. The additive .NET Recording Timed Segment API uses a
common-origin send schedule and an explicitly ordered, owner-scoped host path;
see [the timing contract](../../protocol/specs/timed-segment-v1.md).

`start_recording` records subsequent semantic action tools. `stop_recording`
returns a `recording.schema.json` v1 document, and `save_recording` writes the
last completed recording under `<artifact-dir>/recordings`.

`replay_recording` accepts an inline recording, a saved recording name, or the
last completed recording. It supports recorded semantic wait conditions and
request-correlated completion. Sensitive `set_value` steps store only a
`secretKey`; replay values are supplied through the tool's in-memory `secrets`
map and are not written to the recording.
Coordinate fallback documents can be loaded, but replay rejects them by default;
MCP automation stays on semantic targets.

## Visual comparison

`compare_screenshot` compares the latest `data:image/png;base64` screenshot with
an explicit test name and renderer/OS variant. Baseline creation or replacement
requires `updateBaseline: true`. A failure writes `actual.png`, `expected.png`
when available, `diff.png`, and `comparison.json` under the artifact directory.
`get_visual_artifacts` returns the latest manifest and artifact paths.

