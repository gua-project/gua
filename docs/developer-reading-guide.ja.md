# Gua開発者の読む順番と確認ガイド

## 1. 何のための仕組みか

外部テストからタイトル画面のPlayボタンを押し、ゲーム開始を確認したいとします。
画面の座標だけでは、解像度変更や別画面への遷移で対象を取り違えます。
Guaはgameが公開したUIのrole・名前・状態から対象を探し、操作と結果を対応付ける境界です。
ゲーム世界の追加情報、物理照会、入力再現、失敗の記録は、この境界に用途別の機能を足します。

この文書は読む順番と確認方法の案内です。wire・C ABI・データの厳密な条件は
[基本protocol](../protocol/specs/protocol.md)と機能別仕様が規範です。
ガイドに同じ契約を複製せず、各READMEは用途と使用例、仕様は条件、evidence文書は
対象commit・環境・結果の証拠を担当します。

## 2. 公開から結果確認まで

1. game側adapterがframeを組み立て、完成したUI/Worldを公開します。
   frameは一回の公開のまとまり、revisionは内容の変更番号、
   sessionEpochはresetで新しいテスト状態へ切り替わった世代です。
   未完成frameをtestが読んだことにはしません。
2. test/toolが公開状態を観測し、一つの対象を解決して操作を送ります。
   locatorは対象を探す条件、capabilityは接続先が初期化して公開する機能です。
   capabilityを見つけても、Playerへの操作許可を得たとは限りません。
3. gameが要求をqueueから取り出すconsumeを行い、engineで実処理します。
   request IDが受理と完了を対応付けます。受理されたことと適用したことは別です。
   ownerは接続や有限入力区間の要求・結果・保持入力の所有者です。
4. testが相関した完了を読み、次の画面や値を再観測して目的をassertします。
   Playの完了があっても、loadingやゲーム開始の状態は別に確認します。

状態変化で観測の前提が古くなる問題は
[Guarded dispatchガイド](guarded-dispatch-guide.ja.md)で詳しく扱います。
通信断やtimeout後は既に操作が行われた可能性があり、未実行と決めて再送しません。
保持入力の解放・ownerの終了・不明な完了の扱いは、利用する経路の契約を読みます。
UI、Observe、physics、画像が常に同一時刻に取得されたとは判断できません。

### 用途から読む順番

| やりたいこと | 読む入口と次の仕様 | コードが担当する段階 |
| --- | --- | --- |
| 普通のUIテスト | [Gua.Testing](../bindings/dotnet/src/Gua.Testing/README.md) → [基本protocol](../protocol/specs/protocol.md) | GuaLocatorQueryの対象解決・操作・完了待機 |
| engine adapterを作る | [Gua.Runtime](../bindings/dotnet/src/Gua.Runtime/README.md) → [Gua.Core](../bindings/dotnet/src/Gua.Core/README.md) | frame公開、consume、完了報告、C ABIへの呼出し |
| UIにない追加状態を読む | [Value](../protocol/specs/value-v1.md) → [Observe](../protocol/specs/observe-v1.md) → [Observe transport](../protocol/specs/observe-transport-v1.md) | 型を保持する値・登録の寿命・接続上の購読 |
| 入力の意味と許される値を調べる | [InputAction metadata](../protocol/specs/input-action-metadata-v1.md) | Action Mapの説明と値の検査。許可は別にhostが決める |
| 公開データの矛盾を診断する | [Semantic Lint](../protocol/specs/semantic-lint-v1.md) | 完成したsnapshotをルール検査 |
| 壁や重なりを物理照会する | [Spatial](../protocol/specs/spatial-r1.md) → [host](../protocol/specs/spatial-host-r1.md) → [engine](spatial-engine-r1.md) → [transport](spatial-transport-r1.md) | データ検証、許可とschedule、実物理、接続の順 |
| 操作を保存・再現する | [Recording](../bindings/dotnet/src/Gua.Testing.Recording/README.md) → 必要な場合だけ[Timed Segment](../protocol/specs/timed-segment-v1.md) | 逐次完了待機、または共通開始時点からの有限送信 |
| 失敗時の事実を調べる | [Trace](../protocol/specs/trace-v1.md) → [Viewer](../protocol/specs/trace-viewer.md) | 相関した処理/観測の保存とoffline表示 |
| regressionを比較する | [JSON Snapshot](../bindings/dotnet/src/Gua.Testing.Snapshots/README.md) / [PNG Visual](../bindings/dotnet/src/Gua.Testing.Visual/README.md) | 正規化した意味データ / renderer別画像と承認baselineの比較 |
| 外部ツールへ接続する | [Native MCP](../packages/mcp/README.md) / [WebMCP](../packages/webmcp/README.md) / [World tools](../packages/world-tools/README.md) | 別process bridge / 同じページのengine port / 読取型・tool |
| packageだけで利用する | [配布契約](distribution-contract.md) → [native toolchain](native-toolchains.md) | 固定resource・native資産・隔離consumer確認 |

## 3. 実装を読むための入口

用途を選んだ後、以下の境界を順に追うと、型名だけが一致している状態を避けられます。
リンク先で識別子を検索してください。機能ごとの入口は上の文書に絞っています。

- [GuaContext](../bindings/dotnet/src/Gua.Core/GuaContext.cs):
  UIを読み、操作をenqueueし、相関した結果をpollするmanaged/native境界。
- [GuaRuntime](../bindings/dotnet/src/Gua.Runtime/GuaRuntime.cs):
  adapterがframeを公開し、consumeして実処理の完了を報告するhost側。
- [GuaLocatorQuery.ClickAsync / ResolveAsync](../bindings/dotnet/src/Gua.Testing/GuaLocatorActions.cs):
  最新状態から対象を解決し、期限内で操作と完了を待つtest側。
- [GuaTraceSession.BeginStep / Observe / CompleteAsync](../bindings/dotnet/src/Gua.Testing/Trace/GuaTraceSession.cs):
  処理のまとまりと観測を記録し、保存を確定する側。
- [GuaDistribution.ReadSchema / ValidateJson](../bindings/dotnet/src/Gua.Testing/GuaDistribution.cs):
  checkoutや接続を使わず、packageに固定したresourceを読む側。

テストは期待条件を理解する入口です。例えば
[ObserveTransportTests](../bindings/dotnet/tests/Gua.Selector.Tests/ObserveTransportTests.cs)は
実WebSocketでの型保持・reset・購読を、
[RecordingTests](../bindings/dotnet/tests/Gua.Visual.Tests/RecordingTests.cs)は
各完了を待つ再生を、
[SemanticSnapshotTests](../bindings/dotnet/tests/Gua.Snapshots.Tests/SemanticSnapshotTests.cs)は
baseline欠落・明示承認・maskを扱います。
テストではassertionが確認する条件を読み、実行時にそのケースが選択され成功したことを確認してください。

## 4. ローカルで確認する手順

### 前提環境と準備

以下はWindows x64のMSVC Developer PowerShellからrepo rootで行う、
engineを起動しない確認です。Visual StudioのC++ toolchain、Ninja、
CMake 3.20以上、.NET SDK 10.0.x、Bun 1.3.14、依存を取得できるnetworkが必要です。
公式VS presetを使う場合は[toolchain文書](native-toolchains.md)のgenerator条件が別途必要です。

[CI設定](../.github/workflows/syntax-check.yml)にも、test project・native build・packageの準備例があります。
実行時は対象commit・環境・終了コード・test結果・skip・ログを保存してください。
準備の失敗や0件選択を成功として数えません。

新しい出力先build/docs-guideを使い、他の構成のDLLを混ぜません。
各準備コマンドの成功を確かめてから次へ進みます。

```powershell
bun install --frozen-lockfile
bun run --filter gui-mcp build
bun run --filter @gua/inspector build:trace
cmake -S . -B build/docs-guide -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=ON -DGUA_BUILD_WS_BRIDGE=ON -DGUA_BUILD_GODOT=OFF -DGUA_BUILD_EXAMPLES=OFF -DGUA_BUILD_IMGUI_EXAMPLE=OFF
cmake --build build/docs-guide --parallel 4
$env:GUA_NATIVE_DIR = (Resolve-Path build/docs-guide/native/gua-core).Path
$env:GUA_RUNTIME_NATIVE_DIR = (Resolve-Path build/docs-guide/native/gua-runtime).Path
dotnet restore bindings/dotnet/tests/Gua.Selector.Tests/Gua.Selector.Tests.csproj
dotnet restore bindings/dotnet/tests/Gua.Visual.Tests/Gua.Visual.Tests.csproj
dotnet restore bindings/dotnet/tests/Gua.Snapshots.Tests/Gua.Snapshots.Tests.csproj
```

準備の期待結果:

- bun installはlockfileを変更せず依存を用意します。MCP/Viewer buildはsuiteが使う成果物を作ります。
- configureはsample/engineなし、WebSocket/testありの構成を生成し、buildはlibraryとtest executableを作ります。
- GUA_NATIVE_DIR/GUA_RUNTIME_NATIVE_DIRはそのcore/runtime DLLをmanaged testに指定します。
  Ninjaはsingle-configなので上記directoryにReleaseのsuffixは付きません。
- dotnet restoreは各projectの参照とtest frameworkを取得します。

### 実行コマンドと確認する内容

native suite:

```powershell
ctest --test-dir build/docs-guide --output-on-failure
```

core/runtimeの契約とhost側queue・所有権・guard等を確認します。
全suiteの成功を読み、0件・skip・環境失敗を合格にしません。
Guarded dispatchだけのtest名と絞り込みは[専用ガイド](guarded-dispatch-guide.ja.md)を参照してください。

managed suite:

```powershell
dotnet test bindings/dotnet/tests/Gua.Selector.Tests/Gua.Selector.Tests.csproj -c Release --no-restore --logger trx --results-directory artifacts/docs-guide/selector
dotnet test bindings/dotnet/tests/Gua.Visual.Tests/Gua.Visual.Tests.csproj -c Release --no-restore --logger trx --results-directory artifacts/docs-guide/visual
dotnet test bindings/dotnet/tests/Gua.Snapshots.Tests/Gua.Snapshots.Tests.csproj -c Release --no-restore --logger trx --results-directory artifacts/docs-guide/snapshots
```

| suite | 主に確認する内容 | 期待する結果 |
| --- | --- | --- |
| Selector/Value/Observe/Spatial | C ABI binding、Value、Observe、実WebSocket、metadata、guard、offline spatialとhost条件 | 必要なfixtureが実行され成功。native不足によるskipは確認不足 |
| Visual/Recording/Trace | PNG比較、通常Replay/Timed Segment、Trace保存/読取/report | 成功とTRX。fake/fixture結果を実engine描画・時刻の証明にしない |
| Snapshot | 意味データの差分、mask、明示承認、成果物 | 成功とTRX。通常確認にbaseline更新を混ぜない |

TypeScript package:

```powershell
bun run --filter gua-value-tools test
bun run --filter gua-value-tools check
bun run --filter gui-mcp test
bun run --filter gui-mcp check
bun run --filter gua-webmcp test
bun run --filter gua-webmcp check
bun run --filter gua-world-tools test
bun run --filter gua-world-tools check
```

Valueは型・JSON境界・等価比較、MCPはbridge client/tool・相関と失敗、
WebMCPは登録・取消・完了待機、Worldはpayload/selector/読取toolを確認します。
testは必要なケースが選択され成功、checkは型検査が終了コード0であることを確認します。
fake browser portやpackage unit testから、実browser/engineの成功は推定しません。

### engine・browser・配布を確認する場合

上記の準備だけではGodot/Unityは起動しません。
[Godot test host](../bindings/dotnet/src/Gua.Testing.Godot/README.md)と
[GDScript sample](../examples/godot-gdscript/README.md)、
[Unity test host](../bindings/dotnet/src/Gua.Testing.Unity/README.md)と
[Unity runtime文書](../bindings/unity/Documentation~/index.md)で
実行ファイル・project・scene・platform・描画環境を準備します。
起動だけでなく、bridge接続、相関した操作完了、期待状態、engine終了を確認します。

[Spatial engine](spatial-engine-r1.md)は固定patch/backendの幾何・許可・性能、
[transport](spatial-transport-r1.md)は実bridge/MCP/consumer、
[配布契約](distribution-contract.md)は識別したfeed・hash・隔離cache/archiveを扱います。
[Viewer手順](../protocol/specs/trace-viewer.md)はfixtureと実browser画面操作を確認します。
過去の検証結果を参照するときは、対象commit・環境・実行経路を確認してください。

## 5. 保証の範囲と限界

公開データと相関した完了によって、何を見て何を要求し、どの結果を得たかを確認できます。
協調するhostのguard・owner・現在の許可の検査は、古い前提や他接続の要求の取り違えを防ぎます。
保証には各機能の契約を満たすhost実装が必要です。

一方、gameの未公開状態、任意のengine/backendの正しさ、通信断後の未実行、
操作のrollback、目的の達成は保証しません。
Valueの検証は観測ではなく、Observeは全内部代入の履歴ではなく、
Spatialの空結果は通常移動の成功ではなく、Trace/画像の表示は元の実行の成功ではありません。
baseline比較やschema validationにも、それぞれの比較対象・構造検査の限界があります。

