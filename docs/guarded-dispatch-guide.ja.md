# Guarded dispatch 開発者ガイド

## 1. 何を防ぐ機能か

ショップ画面で「購入」ボタンを見つけ、クリックしようとしたとします。
観測してからゲームが操作を受け取るまでに、商品が切り替わったり、
テスト用のゲーム状態がリセットされたり、操作の許可が取り消されたりすると、
先ほど見た画面を前提とするクリックは安全に実行できません。
同じボタンIDが残っていても、操作を決めたときの前提が変わっているためです。

Guarded dispatch は、**観測時の前提を操作に添え、受理時とゲーム側が
キューから取り出す時点で確認する**仕組みです。前提が合わなくなった操作を
ゲーム側へ渡す前に拒否します。対象はremoteのSemantic UI操作とgame inputです。
単にクライアントが送信直前に状態を読むだけでは、送信後の待ち時間に生じる
変化を防げません。

このガイドは仕組みを読むための案内です。規範となる条件・wire形式は
[Guarded remote dispatch v1仕様](../protocol/specs/guarded-dispatch-v1.md)と
[schema](../protocol/schema/guarded-dispatch-v1.schema.json)を参照してください。
実装時のContract mapping、レビュー履歴、テスト件数と成果物保存先は
[検証証拠](guarded-dispatch-evidence.md)に保持しています。
その実行履歴だけから現在の動作を保証するものではありません。

## 2. 操作が実行されるまでの流れ

### 画面を観測する

購入ボタン `buy` を含むObserve snapshotを読みます。「画面」は画像だけを
指すのではなく、ゲームが公開するSemantic UI TreeのID、ラベル、状態などです。
UI用のguardには、そのsnapshotの `document` から一緒に取得した
`sourceId`、`sessionEpoch`、`uiRevision` と、観測に使ったprofileを保存します。

- `sourceId` はObserveが持つホストの識別子です。別のゲームが偶然同じ
  カウンター値を持っていても、同じ観測元として扱わないために使います。
- `sessionEpoch`（epoch）はリセットをまたぐ古い観測を区別する世代です。
  成功したcontext resetで変わります。
- `uiRevision`（UIのrevision）は、そのprofile向けに公開されたUIの内容が
  変わったことを表す番号です。毎フレーム増える `frameSequence` とは別です。
- profileはDebugかPlayerかという公開・操作の範囲です。`expectedProfile`
  は現在の権限との比較値であり、クライアントが権限を取得する指定ではありません。

複数回の読み取りや再接続をまたいで値を寄せ集めると、一つの観測の前提には
なりません。保存したsnapshotを使う場合は、その `sourceId` も保持します。

### 操作を送信する

.NETでは `GuaWebSocketContext.CreateGuardedDispatchSession(observedSourceId)`
で専用接続を作ります。`guarded_dispatch_v1` capabilityを確認し、元の接続と
専用接続のObserve sourceIdが一致してから送信を有効にします。この接続の
世代は固定され、dispatchのために自動再接続されません。

`GuaDispatchGuard` を添えて `GuaRemoteGuardedSession.SendUi` を呼ぶと、
例えば `guarded_click_node` に `nodeId: "buy"` と
`expectedSessionEpoch`、`expectedProfile`、`expectedRevision` を載せます。
別のsourceのguardは.NET側で送信前に拒否されます。bridge側でもrootのmetadata、
相関可能なtransport `id`、操作ごとの必須payload・型・範囲を検査します。
未対応のbridgeに通常のclickとして送り直すfallbackはありません。

bridgeは接続ごとに `owner` を持ちます。これは要求・結果・押し続けている
入力を「どの接続のものか」で分離する所有者です。受理されると
`requestId` が返り、attemptは `Enqueued` になります。
transport `id` は通信の返信を、`requestId` はゲーム側の要求と完了を
対応付けます。**この受理通知は、クリックが実行された証拠ではありません。**

### ゲームが受け取る

ゲーム側のadapter/hostがキューから要求を取り出す段階をconsumeと呼びます。
ここでepoch、profile、revisionや現在の操作許可を再検査します。
例えばクリックの受理後、consume前にUIのラベルが変わりrevisionが進めば、
ゲーム側へクリック要求を渡さず、UIは `stale_guard`（完了のerror `-7`）になります。
直接C/C++でUIをconsumeするhostは、現在の正式なprofileを
`gua_consume_action_request_for_profile` に渡す必要があります。
権限を指定しないgeneric consumerはguarded要求をfail closedで拒否します。

game inputには二つの前提があります。

| 入力 | revisionの意味とconsume前の確認 |
| --- | --- |
| Semantic input（例: `jump`） | 公開Action Mapのrevision。固定された専用sessionの `GetGameInputActionsJson` でepochとrevisionを読み、検証済みSourceIdと組み合わせます。マップ・descriptor・確認要件・権限を再確認します。 |
| Raw input（例: `KeyA`） | 無関係なSemantic Action Mapのrevisionは比較しません。epochと現在のprofile/capabilityなどを確認します。ただしpollには送信時と同じrevision metadataが必要です。 |

Semantic inputの古いepoch/map revisionはnativeで `invalid_argument`（`-1`）
になります。profile変更やcapability喪失は別の拒否条件です。
guardが通った後の実際のUI変更・入力注入と、その完了報告はhostの責任です。

### 結果を確認する

`GuaRemoteGuardedSession.Poll(attempt)` は、元のrequestIdとguardを使って
`guarded_poll_action` / `guarded_poll_game_input` を送ります。
元のepoch/profile/revisionと違うpollは、結果を消費する前にbridgeで拒否されます。
UIのpendingは `null`、inputのpendingは `completed:false` です。
正常なpendingの間は有限の期限内でpollできます。terminal結果はowner限定で
一度だけ取り出せます。.NETのattemptには受け取った証拠が残ります。

.NETの `Completed` は「正しく相関した完了結果を取得した」という状態です。
成功・失敗は `Completion.succeeded` とUIの `error` / inputの `errorCode`
を読みます。例えばrevision競合の失敗結果も `Completed` になり得ます。
成功結果があっても、「購入後に所持品が増えた」というゲーム上の目的は、
新しいUI/World観測やゲーム側のassertionで別に確認してください。

| attemptの状態 | 判断できること |
| --- | --- |
| `Rejected` | 送信前の拒否、または明確なdispatch拒否。受理されたrequestIdはありません。 |
| `Enqueued` | 受理された要求の完了待ち。実行・成功はまだ確定しません。 |
| `Completed` | 型と相関を確認したterminal結果がある。成功とは限りません。 |
| `Uncertain` | 実行の有無や完了を確定できないterminal状態。未実行とは扱えません。 |

### 状態変化・通信断・所有者の終了

送信した後で返信が失われると、ゲームは既に操作を実行しているかもしれません。
受理返信の欠落、不正な返信、poll返信の欠落・不正、古い完了条件では、
.NET sessionは `Uncertain` となったattemptを保持し、接続を閉じます。
自動resendや不明なpollの再試行はしません。呼び出し側は操作attemptの台帳と
有限の期限を持ち、不明な操作を新しいsessionから盲目的に繰り返してはいけません。

切断/Disposeではそのownerの未consume UI要求と結果を除去し、保持入力の
cleanupを行います。他ownerの入力は解放しません。既にconsumeされたUI要求は
hostが完了を報告できる経路を残しますが、owner喪失後の結果は破棄されます。
切断は、既に行われたゲーム操作を巻き戻す機能ではありません。
保持入力のlease期限・reset時のneutralizeは既存game-input lifecycleに従います。

hostがキュー内のowned UI要求をcancelすると、consume対象から除き、
owner専用の失敗結果 `Cancelled`（`-8`）を残します。poll/owner cleanupで
保持枠が解放されます。consume後のcancelは `InFlight` であり、hostの完了を待ちます。

## 3. 実装を読むための案内

規範仕様を読んだ後、以下を流れの順に追うと主要な境界を確認できます。
リンク先のファイル内で記した識別子を検索してください。

| 入口 | 担当する段階 |
| --- | --- |
| [GuaRemoteGuardedDispatch.cs](../bindings/dotnet/src/Gua.Testing/GuaRemoteGuardedDispatch.cs): `GuaDispatchGuard`、`CreateGuardedDispatchSession`、`SendUi`、`SendGameInput`、`Poll` | 観測元の結び付け、専用接続、attempt状態と完了の検証、不明時の接続終了。 |
| [ws_bridge.cpp](../native/gua-ws-bridge/src/ws_bridge.cpp): `parse_command`、`DispatchGuard`、`GuardedRequests`、`handle_command` | wire検査、接続owner、元のguardの保持、poll時の照合、legacy pollからの分離。 |
| [runtime.cpp](../native/gua-runtime/src/runtime.cpp): `gua_runtime_consume_action_request`、`gua_runtime_consume_game_input_request`、`poll_guarded_result` handler | hostの現在profile/capabilityとcoreの接続。context lock下でのguard付きenqueue/poll、host側consume。 |
| [gua.cpp](../native/gua-core/src/gua.cpp): `gua_enqueue_action_guarded_v1`、`gua_consume_action_request_for_profile`、`gua_enqueue_game_input_guarded_v2`、`gua_consume_game_input_request`、`gua_cancel_action_request` | nativeのキュー、consume前の再検査、owner限定結果、cancelと保持枠。 |

具体例の読み合わせには
[GuardedDispatchTests.cs](../bindings/dotnet/tests/Gua.Selector.Tests/GuardedDispatchTests.cs)
の次のケースが入口になります。単なるmockではなく、native runtimeと実WebSocketを
使う範囲を確認できます。proxyの障害注入が実際に発火したassertionも読んでください。

- `UiEnqueueGuardsAndConsumeRaceHaveNoSideEffectsAndOwnedOneShotCompletion`:
  UIのenqueue拒否とconsume直前のrevision競合、owner限定の一度限りの結果。
- `SemanticStaleMapAndEpochRejectBeforeHostAndRaceReportsNativeFailure`:
  Semantic inputのmap/epochとconsume競合。
- `SameCounterDifferentHostCannotReceiveObservedUiOrInput`:
  同じカウンターを持つ別hostへ観測を持ち込めないこと。
- `ActualNativeResponseLossIsTerminalWithoutResendOrRepoll`、
  `MalformedRealCompletionPoisonsSessionWithoutRepoll`:
  返信損失や不正な完了を成功扱いせず、再送しないこと。
- `QueuedCancellationCompletesOwnedUiAndReclaimsTransportCapacity`:
  cancel後の相関した失敗結果と保持枠の回収。

## 4. ローカルで確認する手順

### 前提環境と準備

以下はWindows x64のMSVC開発環境用です。Visual StudioのC++ toolchain、
CMake 3.20以上、Ninja、.NET SDK 10.0.x、Bun（CIのportable-nativeは1.3.14）、
依存取得用のネットワークが必要です。MSVCとNinjaを使えるDeveloper PowerShellで、
checkoutしたリポジトリのrootから実行してください。Godot/Unityの起動は不要です。

**今回、以下のローカルコマンドはすべて未実行・未検証です。**
既存のCMake/test定義と[CI設定](../.github/workflows/syntax-check.yml)を照合して
記載しています。現在の成功件数を示す手順ではありません。実行時はcommit、
環境、終了コード、test結果、skipとログを記録してください。
既存の履歴は[検証証拠](guarded-dispatch-evidence.md)を参照してください。

`build/guarded-guide` はこの手順用の出力先です。別構成のビルドを混ぜず、
新しいdirectoryでconfigureしてください。

```powershell
bun install --frozen-lockfile
bun run --filter gui-mcp build
bun run --filter @gua/inspector build:trace
dotnet restore bindings/dotnet/tests/Gua.Selector.Tests/Gua.Selector.Tests.csproj
cmake -S . -B build/guarded-guide -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=ON -DGUA_BUILD_WS_BRIDGE=ON -DGUA_BUILD_GODOT=OFF -DGUA_BUILD_EXAMPLES=OFF -DGUA_BUILD_IMGUI_EXAMPLE=OFF
cmake --build build/guarded-guide --parallel 4
$env:GUA_NATIVE_DIR = (Resolve-Path build/guarded-guide/native/gua-core).Path
$env:GUA_RUNTIME_NATIVE_DIR = (Resolve-Path build/guarded-guide/native/gua-runtime).Path
```

準備の期待結果:

- `bun install` はlockfileを変更せず依存を取得します。
- `gui-mcp build` と `build:trace` はmanaged suiteが使用するconsumer/Viewer成果物を作ります。
- `dotnet restore` はtest projectの参照とNUnit等を取得します。
- `cmake -S` はengine sampleなし、WebSocketとtestありのMSVC/Ninja構成を生成します。
- `cmake --build` はnative libraryとtest executableを作ります。
- 二つの環境変数は、このビルドのcore/runtime DLLをmanaged testに読み込ませます。
  Ninjaはsingle-configなので、この例のlibrary directoryに `Release` は付きません。

各コマンドの成功を確認してから次へ進んでください。準備失敗やDLL未検出による
skipは、guardの動作を確認した結果には数えません。

### 実行コマンド

| コマンド | 確認する内容 |
| --- | --- |
| `ctest --test-dir build/guarded-guide -R "^(gua-guarded-dispatch-tests\|gua-runtime-guarded-profile-tests)$" --output-on-failure` | native coreのrevision/profile競合・owner/cancel/容量と、runtimeのprofile変更時拒否。選択された二つのtest名と実行を確認します。 |
| `dotnet test bindings/dotnet/tests/Gua.Selector.Tests/Gua.Selector.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Gua.Selector.Tests.GuardedDispatchTests --logger trx --results-directory artifacts/guarded-guide` | native runtimeを実WebSocketで通るmanaged guarded経路。競合、wire検査、別host、返信損失、不正完了、cleanupなど。 |
| `bun run --filter gua-value-tools test` | 既存Value suite内のguarded schema例とlegacyとのpayload差分検証。gameやWebSocketは実行しません。 |
| `bun run --filter gua-value-tools check` | Value packageのTypeScript型検査。runtime動作の証明ではありません。 |

### 期待する結果と結果の読み方

CTestは対象二つが選択され、両方成功することを確認します。
managed testはguarded fixtureの各ケースが成功し、skipがないことを確認します。
TRXは `artifacts/guarded-guide` に出力します。
Valueのtest/checkは終了コード0であることを確認します。
件数はコードの追加で変わり得るため、過去の件数と一致することを合格条件にしません。

失敗したらtest名・assertion・どの境界かを読むことで原因を絞ります。
例えばconsume競合は「hostへ要求が渡らない」と「相関した失敗完了」を、
障害proxyは「faultが発火した」と「再送がない」を組み合わせて確認します。
単に例外が発生しただけでは、その契約の確認になりません。
これらの手順はGodot/Unity adapterの実ゲーム内注入や配布package consumerの
受入確認まで行いません。

## 5. 保証の範囲と限界

防げることは、協調するbridge/core/host間で、観測に基づく前提が古くなった要求を
consume前にゲームへ渡すこと、他ownerが結果を取り出すこと、明確でない完了を
.NET clientが成功扱いして自動再送することです。現在の公開・操作許可も再確認し、
Playerのprivate targetと存在しないtargetのエラーを同じように扱います。

UI/inputの要求・結果は接続ごとに合計256件に制限されます。
native UIの保持枠は全owner合計256件で、切断後もhost完了待ちのconsume済み要求を
含みます。上限超過はenqueueなしで拒否されます。これは無制限の台帳ではなく、
呼び出し側にもpoll・期限・終了処理が必要です。

一方で、次のことは保証しません。

- consume後のゲーム処理をtransactionにすること、rollback、購入や移動など
  ゲーム上の目的の達成。host完了とは別に観測/assertionが必要です。
- 通信断時の「必ず未実行」や、再送によるexactly-once実行。
- hostが公開していない状態変化の検出。Raw入力ではAction Map revisionを比較せず、
  sourceIdやカウンター比較もクライアントの権限を増やしません。
- legacy操作を使ったclientへの新しいguard条件の適用。
  この拡張はcapabilityを交渉した `guarded_` 経路です。
- 全engine、MCP/Inspector/WebMCPの全経路、配布済みbinary/packageの受入確認。
  上記ソースとtestを読んだことや、以前のCI成功だけではそれらを証明できません。

### このガイドの照合範囲

2026-10-07にGitHub APIで確認したmain
`9297e7b49e56d42cfb62ffa7b1a0277337442462` を基準に、
上記仕様/schema、managed session、bridge/runtime/core、関連test、
CMake test名、test projectとCIの準備処理を読み合わせました。
ローカル実行環境の開始エラーが既に確認されているため再プローブは行わず、
今回のruntime・engine・packageの動作確認は未実施です。
この記載は過去の検証証拠を更新する新しい実行記録ではありません。
