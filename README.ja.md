# Gua

## 実装を読み始める開発者へ

ゲーム側adapterが画面を公開し、外部テストやツールが対象を探して操作を送り、
adapterが適用して完了を報告し、呼び出し側が結果を観測する、という流れが中心です。
操作を受理しただけではgame上の成功を確認したことにはなりません。
UIの意味データ、追加状態、物理読取、記録、配布では確認できる内容が異なります。

[開発者向けの読む順番とローカル確認](docs/developer-reading-guide.ja.md)から始め、
各READMEの使用例と規範仕様へ進んでください。
古い画面や通信断を含む具体的な操作例は[Guarded dispatchガイド](docs/guarded-dispatch-guide.ja.md)、
厳密なwire/ABI条件は[基本protocol](protocol/specs/protocol.md)にあります。
過去の実測結果は対象commitと環境に限る証拠として別に読む構成です。

[English](README.md) | 日本語

[![License](https://img.shields.io/github/license/link1345/gua)](https://github.com/link1345/gua/blob/main/LICENSE)

Guaについての質問・相談は、[サポートDiscordサーバー](https://discord.gg/B495mwsTKK)へどうぞ。

> この日本語版は補助ドキュメントです。内容に差異がある場合は、
> [英語版README](README.md)を正しい最新情報として扱ってください。

> **Playwrightライクな設計思想をもとに開発された、Godot 4.7・Unity 6向けの
> ゲームテスト／自動化プロトコル。UI、ゲーム世界、ゲーム入力を意味で扱い、
> テスト、Inspector、MCP、WebMCPから利用できます。**

**Gua**は、Playwrightライクな設計思想をもとに開発された、ゲーム向けの
ランタイム自動化プロトコルです。実行中のゲームUIをSemantic UI Treeとして公開し、
壊れやすい画像認識や画面座標に頼らず、ID、role、text、状態からControlを検索して、
操作、待機、ログ確認、スクリーンショット取得、結果検証を行えます。

その中核となるUI自動化に加えて、Guaは<strong>ゲーム開発に最適化された機能群</strong>を
備えています。ゲーム世界を意味情報として公開するWorld Object Tree、ジャンプや移動を
扱うSemantic Game Actionと必要時だけ有効にするRaw Input、ネイティブゲーム向けMCPと
ブラウザゲーム向けWebMCP、AIエージェントへ見せる情報とUI操作を制限する公開ポリシー、
時間依存テストのための仮想時計、RecordingとVisual比較を、同じプロトコル境界で利用できます。

Guaは**Godot 4.7 GDScriptアドオン**と**Unity 6**向けのランタイム統合を
提供します。Unityパッケージは、Windows x64、Linux x64、Intel／Apple Silicon macOSのMono環境でUI Toolkit、uGUI、
TextMeshProランタイムUIを自動収集します。開発中はAIコーディングエージェントによる
実装・検証に使えます。リリース版では、ゲームが許可した情報と操作だけを公開し、
AIエージェントプレイヤーが実際にゲームを遊ぶためにも利用できます。

**実装 → 起動 → 観測 → テスト → 修正 → 再テスト。**

WebのDOMをSemantic UI Treeへ置き換え、ロケーター、操作、待機、アサーションを
実行中ゲームへ適用するのがGuaの基本です。そのPlaywrightライクな操作モデルを核に、
UIの外側にあるゲーム世界、ゲームプレイ入力、AI接続、安全な情報公開、決定的な時間制御までを
ゲーム開発向けに拡張した自動化レイヤーだと考えると分かりやすいでしょう。

## Guaでできること

### Godot UIテスト

- 標準Godot `Control`をSemantic UI Treeとして公開
- 座標ではなくID、role、text、value、状態からControlを検索
- click、focus、値入力、選択、scroll、key入力を実行
- 通常の.NETテストからUI状態の変化を待機・検証
- スクリーンショット、Visual baseline比較、失敗診断artifactを取得
- [`link1345/gua-tester`](https://github.com/link1345/gua-tester)でGodotのE2E UIテストをCI実行

### Unity 6 UIテスト

- UI Toolkit、uGUI、TextMeshProのランタイムUIを自動的に公開
- Editor Play Modeまたは4 RIDのdesktop Mono Playerでシーンを操作
- Godotと同じSemantic APIでControlを検索・操作
- ゲーム側アダプターと外部NUnitテストホストを分離
- Unityテスト失敗時にログ、スクリーンショット、診断情報を保存
- [`link1345/gua-tester`](https://github.com/link1345/gua-tester)で4 RIDのMono PlayerをCIビルド・テスト

### AIによるゲーム開発とプレイテスト

`gui-mcp`は、MCP対応AIコーディングエージェントをInspectorと同じブリッジへ
接続します。AIはUI Treeの観測、SemanticなControl操作、状態変化の待機、ログ確認、
スクリーンショット取得、RecordingのReplay、Visual比較、小さなテストシーケンスの
実行を、自ら開発を支援しているゲームに対して行えます。

GuaはAIコーディングエージェントを補完します。AIがゲームを編集し、Guaが
実行中ゲームの観測・操作・検証を可能にします。ゲームエンジンやAIを置き換える
ものではなく、Semanticな対象指定は画像認識に依存しません。

### リリース版を遊ぶAIエージェントプレイヤー

Guaは開発中のテストだけでなく、リリースされたゲームをAIがプレイヤーとして遊ぶ
用途にも対応します。ゲーム側はPlayer profileを使い、AIに見せてよいUIとWorld Object、
許可するUI操作を公開します。ジャンプ、移動、キーボード、マウスなどのゲーム入力は
別権限として明示的に許可します。AIはソースコードを編集したりDebug情報へアクセスしたり
するのではなく、プレイヤー向けに公開された意味情報を観測し、許可された操作でゲームを
進めます。

ネイティブゲームでは`gui-mcp`、Godot Web ExportやUnity WebGLでは`gua-webmcp`を
接続経路として利用できます。World Object Treeで目的地、敵、door、checkpointなどを
把握し、Semantic UI Actionとゲーム入力でメニュー操作やキャラクター操作を行えます。
公開ポリシーはprivateなobject、内部値、Debug logを隠し、screenshotも既定では拒否します。
これにより、開発者がゲーム体験の境界を管理したまま、リリース版へAIプレイヤー対応を
組み込めます。

### ブラウザネイティブWebMCP（実験的）

Godot Web ExportとUnity WebGLのページは、実行中の同じSemantic UI Treeを実験的な
ブラウザ`document.modelContext` APIから公開できます。
`gua-webmcp`パッケージは、エンジンが
所有する同一ページ内ブリッジに対して`get_ui_tree`、Semantic action、wait、read-onlyの
World Object Tree観測ツール、capabilityで制御されたSemantic Game Action / Raw Input、
任意のscreenshotツールを登録します。world型とselector
定義は`gua-world-tools`として公開します。`gui-mcp`もWebSocket接続も不要で、WebMCP非対応
ブラウザでもゲーム本体はそのまま動作します。各タブがゲームとツール登録を所有し、
独自のブラウザセッションルーターは持ちません。
成功した呼び出しは、JSON文字列をMCP形式のtext contentで包まず、Guaの構造化結果を
直接返します。
ゲーム入力は現在のページが所有し、request-correlatedなhost completionを待ちます。
timeout、キャンセル、ツール登録解除、engine終了時には全入力を解放します。Raw toolは
hostが入力pumpとcleanup経路を明示的に初期化するまで公開しません。
Player/Public Agent向けゲーム入力は別権限として既定拒否です。Debug Inspector用の
入力経路を有効化してもWebMCPには公開されません。Godotでは`allow_player_agents`引数、
Unityでは`AllowPlayerAgentSemanticInput` / `AllowPlayerAgentRawInput`を明示指定します。
組み込み方法は[ブラウザWebMCP編](https://gua.orizika.com/ja/webmcp/)、詳細なAPIは
[`gua-webmcp`パッケージリファレンス](packages/webmcp/README.md)を参照してください。
Godot WebアドオンにはDebug用とRelease用のGDExtensionが個別に含まれます。
Godot WebのExport presetで`Extension Support`を有効にすると、Godotが
`web.wasm32.single.debug`または`web.wasm32.single.release`を選択します。

## Godot 4.7対応

推奨Godot統合はGDExtensionを利用するGDScriptアドオンです。標準`Control`を
自動収集し、GDScriptプロジェクトでも.NET有効Godotプロジェクトでも利用できます。
別のC#ランタイムサンプルは実験的で、アダプターの全機能は提供しません。

Godot開発では、Semantic UI自動化、外部E2Eテスト、スクリーンショット・Visual
regressionテスト、CI、Inspectorによる調査、MCPによるAIプレイテストに利用できます。
設定手順は[Godot 4.7 GDScriptアドオン](#godot-47-gdscriptアドオン)を参照してください。

## Unity 6対応

Unityパッケージはランタイムアダプターを自動起動し、UI Toolkit、uGUI、TextMeshProの
Controlを手動登録なしで収集します。`Gua.Testing.Unity`を使うと、通常の.NETテストから
Editor Play Modeまたはビルド済みMono Playerを起動できます。現在の対応範囲は
Unity 6000.5以降、Windows x64／Linux x64／Intel Mac／Apple Silicon Mac、Monoです。IL2CPP、Unity IMGUI、
EditorWindowの自動化には未対応です。導入・検証手順は
[Unity 6 desktop Mono](#unity-6-desktop-mono)を参照してください。

## GitHub Actions

[`link1345/gua-tester`](https://github.com/link1345/gua-tester)は、Godotと
Unityの両方に対応する公開CI部品です。Godotでは、Godot本体と配布済みアドオンを
準備して外部.NETテストを実行します。

```yaml
- uses: link1345/gua-tester/godot@v3.1
  with:
    project-path: game
    test-project: tests/GuaTester.Tests.csproj
    godot-version: "4.7"
    godot-status: stable
```

Unityでは、指定したdesktop Mono Playerをビルドし、対象runnerへ渡して
`Gua.Testing.Unity`のNUnitテストを実行します。

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

`gua-tag`で選ぶUPM packageと`Gua.Testing.Unity`のNuGetバージョンは揃えてください。
fork PRにはUnity credentialsが渡らないため、未信頼forkではUnity jobをskipします。
reusable workflowはPlayer起動前にWindows test runnerの画面解像度を1920x1080へ固定します。
Gua v1.0.4以降には、Linux／macOS platformで必要なcross-platform UPM native assetが含まれます。

## NuGetパッケージ

- **Gua.Core:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Core)](https://www.nuget.org/packages/Gua.Core) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Core)<br>
  .NETからGuaのC ABIランタイムを利用するためのP/Invokeバインディングです。
  Windows x64、Linux x64、Intel Mac、Apple Silicon Mac用ネイティブランタイムも含まれます。
- **Gua.Testing:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing)](https://www.nuget.org/packages/Gua.Testing) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing)<br>
  通常の.NETテストに、Gua用のロケーター、待機、アサーション、アダプターの
  テストループを追加します。
- **Gua.Testing.Godot:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Godot)](https://www.nuget.org/packages/Gua.Testing.Godot) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Godot)<br>
  Godotプロセスを起動し、Guaブリッジ経由で実行中のシーンを操作・検証する
  テストヘルパーです。
- **Gua.Testing.Unity:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Unity)](https://www.nuget.org/packages/Gua.Testing.Unity) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Unity)<br>
  Unityプロセスを起動し、Guaブリッジ経由で実行中のシーンを操作・検証する
  テストヘルパーです。
- **Gua.Runtime:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Runtime)](https://www.nuget.org/packages/Gua.Runtime) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Runtime)<br>
  エンジンアダプター開発者向けの共有マネージドラッパーと、同じ4つのdesktop RID用ネイティブランタイムです。P/Invokeを重複実装せず、Semantic frameの公開、actionの処理、スクリーンショット要求の完了、Inspectorブリッジのホストに利用できます。通常のゲームテストでは各エンジン向けパッケージを使用します。
- **Gua.Testing.Visual:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Visual)](https://www.nuget.org/packages/Gua.Testing.Visual) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Visual)<br>
  Semantic assertionでは検出できないclipping、Controlの位置ずれ、asset間違い、予期しないoverlayなどの描画regressionをPNG baseline比較で検出します。失敗時はexpected、actual、diff、機械可読な比較結果を保存します。
- **Gua.Testing.Snapshots:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Snapshots)](https://www.nuget.org/packages/Gua.Testing.Snapshots) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Snapshots)<br>
  UIと任意のWorldのdeterministic JSON baselineを比較。意味構造の差分、明示更新、呼び出し側が設定するmask/ignoreに対応します。 [パッケージリファレンス](bindings/dotnet/src/Gua.Testing.Snapshots/README.md)を参照してください。
- **Gua.Testing.Recording:** [![NuGet Version](https://img.shields.io/nuget/v/Gua.Testing.Recording)](https://www.nuget.org/packages/Gua.Testing.Recording) ![NuGet Downloads](https://img.shields.io/nuget/dt/Gua.Testing.Recording)<br>
  再現可能なユーザーフローをSemantic操作として記録し、各stepをホスト側の完了と相関確認しながら再生します。壊れやすい座標や秘密値の平文を保存せず、regression flow、bug再現、scenario共有に利用できます。

パッケージの選び方は[.NETパッケージガイド](https://gua.orizika.com/docs/dotnet-packages/)、
具体的な実装・運用は[Gua.Runtime実装ガイド](https://gua.orizika.com/docs/gua-runtime/)、
[Visualテスト実践](https://gua.orizika.com/visual-testing/)、
[Recording実践](https://gua.orizika.com/recording/)を参照してください。

## MCPとInspector

### World Object Tree

Guaは、明示的にopt-inしたゲーム世界のobjectをSemantic UI Treeとは別に公開できる。各objectはstable ID、意味的なkind、2Dまたは3D座標、hostが決めるplayer可視性、tag、flatなprimitive stateを持つ。Godotでは`gua_world_object` groupと`gua_world_*` metadata、Unityでは`GuaWorldObject` componentを使う。全scene nodeやGameObjectを自動公開することはない。

native bridgeの既定はdebug viewである。host processに`GUA_OBSERVATION_PROFILE=player`を設定すると、UIとWorldの双方へancestor visibility、`private`除外、field ruleを適用してから公開する。client入力からdebugへ昇格することはできない。World v1は観測専用であり、MCPは`get_world_object_tree`、`find_world_objects`、`wait_for_world_object`を提供し、Inspectorは独立したWorld Object Tree panelへ表示する。

World selectorへ`relativeToObjectId`と`maxDistance`、任意の正の`limit`を渡すと半径検索できる。runtimeは同じ投影済みsnapshot上で`world2d`をXY、`world3d`をXYZとして距離を評価し、snapshot metadataとmatchに対応するdistanceを返す。waitは新しいsnapshotごとに再評価する。距離の単位はhost側のworld unitであり、privateな基準IDと未知IDはいずれも空結果になる。

`GuaAgentPolicy`は公開fieldのomit、redact、同型replace、数値quantizeとUI action allowlistを定義する。Debug snapshotは完全なtreeを維持し、Playerではsnapshot、query、wait、diagnostics、action認可に同じ投影を使う。debug logは公開せず、screenshotも既定で拒否する。hostが必要性を判断した場合に限り、bridge開始前に明示的に許可できる。

engine設定、selector、Player向け公開方法は
[World Object Tree](https://gua.orizika.com/ja/world-object-tree/)と
[エージェント公開ポリシー](https://gua.orizika.com/ja/agent-policy/)を参照してください。

Godot metadataは`gua_world_id`（必須）、`gua_world_kind`、`gua_world_label`、`gua_world_visible_to_player`、`gua_world_active`、`gua_world_agent_exposure`、`gua_world_tags`、`gua_world_state`を使う。state値は文字列、有限数値、真偽値、nullだけを許可し、秘密情報を含めてはならない。整数値はv1 C ABIの`double`表現を正確に往復できる必要があり、JavaScript selector clientはsafe integer範囲外の整数を拒否する。

### 決定論的な仮想時間

GuaClockを時刻源にしたゲームロジックは、停止したり、実時間を待たずに
進めたりできます。既存のengine timerを自動的に置き換える機能ではありません。
まずゲーム本体の対象ロジックを、Godotの`Timer`、Unityの`Time.deltaTime`や
Coroutineなどではなく、GuaClockのScheduleまたはTickを使う実装へ変更します。
その後、テストから共有ClockをInstallして操作します。

```csharp
// ゲーム側の組み込み。production codeで一度行う。
var clock = runtime.Clock;
clock.Install();
clock.Schedule(TimeSpan.FromSeconds(2), ShowMessage);

// テスト側から同じClockを操作する。
clock.Pause();
clock.RunFor(TimeSpan.FromSeconds(2));
```

ここで`Install`が行うのは共有仮想Clockの有効化であり、任意のgame objectへ
Clockを自動注入することではありません。`Pause`の対象は、あらかじめGuaClockの
SchedulerまたはTickへ接続したゲームロジックだけです。engine標準のTimer、
物理、Animation、Audio、OS時刻、ネットワークは停止しません。
bridge、MCP、Inspectorでも`get_clock`、`clock_install`、`clock_pause`、
`clock_run_for`、`clock_resume`を利用できます。

### Semantic Game ActionとRaw Input

大規模なAction Mapではdescriptorに`category`、`tags`、人向けの`aliases`を追加し、
固定API `find_game_input_actions`で現在のMapを検索できます。条件はAND、複数tagは
全件一致、結果はAction IDのordinal昇順です。paginationはなく既定20件、最大100件で、
`truncated`が真なら条件を絞ります。Player投影では`agentExposure: "private"`を件数・
打ち切り判定より前に除外し、公開Action・公開metadata・contextが変わったときだけ
Player revisionを進めます。検索結果は実行権限ではなく、enqueue時とhost consume直前に
最新descriptorを再認可します。ID、description、category、tags、aliasesには、Debug toolや
recordingに残って困る秘密情報を入れないでください。

ホストはUI Treeと独立したGame Action Mapを明示登録し、button、axis、vector、
textを`press_game_input_action`、`set_game_input_action`、
`release_game_input_action`で操作できます。明示opt-inのRaw toolはcommand schemaに
列挙されたengine共通のW3C physical key code、pointer移動/button/wheel、
Standard Gamepad、text inputを扱います。
保持入力は接続ごとに分離され、leaseは既定5秒・最大60秒です。満了、切断、
reset、replay失敗、session disposeではneutral状態へ戻します。Inspectorの直接操作
panelが提供するのは、Semantic Game Action、単発のphysical key入力、保持状態の確認、
緊急`Release all`です。pointer、gamepad、text inputはInspectorから直接操作できません。
`gui-mcp`は固定のinput tool群を公開して未対応操作を呼び出し時に拒否し、WebMCPは
page tool登録時に読み取ったcapabilityで有効なtoolだけを登録します。
ローカルC++/.NET sessionは返されたrequest IDをpollしてhost完了を確認します。
enqueue受付だけではadapterが入力を注入したことを意味しません。

Unity 6000.5では`com.unity.inputsystem@1.20.0`のvirtual deviceへ注入し、
Godotではmain threadから`Input.parse_input_event`へ`InputEvent`を渡します。
adapterはinput pumpとcleanup経路が初期化済みのcapabilityだけを公開します。
既存のSemantic UI用`press_key`は変更せず、Raw Keyboard gestureには
`press_physical_key`を使います。

capability、owner、lease、confirmation、engine設定の詳細は
[ゲーム入力編](https://gua.orizika.com/ja/game-input/)を参照してください。

- **gua-value-tools:** [![NPM Version](https://img.shields.io/npm/v/gua-value-tools)](https://www.npmjs.com/package/gua-value-tools) ![NPM Downloads](https://img.shields.io/npm/dw/gua-value-tools)<br>
  共通Value v1の型、検証、JSON入出力、enum catalog、等価比較を提供します。[パッケージリファレンス](packages/value/README.md)を参照してください。
- **gua-webmcp:** [![NPM Version](https://img.shields.io/npm/v/gua-webmcp)](https://www.npmjs.com/package/gua-webmcp) ![NPM Downloads](https://img.shields.io/npm/dw/gua-webmcp)<br>
  ページのWebMCP APIを通じて、GuaのSemantic UI、World Object Tree、
  ゲーム入力ツールを登録するブラウザネイティブadapterです。
- **gui-mcp:** [![NPM Version](https://img.shields.io/npm/v/gui-mcp)](https://www.npmjs.com/package/gui-mcp) ![NPM Downloads](https://img.shields.io/npm/dw/gui-mcp)<br>
  Inspectorと同じWebSocketブリッジを通じて、Guaのランタイム操作を
  AIエージェントへ公開する薄いMCPサーバーです。
- **Gua Inspector:** [![Gua Release](https://img.shields.io/github/actions/workflow/status/link1345/gua/gua-release.yml?branch=main&label=Gua%20Release)](https://github.com/link1345/gua/actions/workflows/gua-release.yml)<br>
  Semantic UI Tree、ノード状態、スクリーンショット、ログを確認し、ランタイムへ
  コマンドを送信できるブラウザー・WindowsデスクトップUIです。

概念上は、Web UIをテストするようにゲームUIを操作できます。

```ts
await game.getByRole("button", { name: "Start Game" }).click()
await expect(game.getById("loading")).toBeVisible()
```

現在の実装では、安定境界となるC ABIの上にC++とC#のAPIを提供し、InspectorとMCPをプロトコルの利用者として接続しています。Godot 4.7とUnity 6向けのアダプターとサンプルも含まれます。

```cpp
gua::testing::get_by_role(ctx, "button", "Start Game").click();
gua::testing::wait_for_text(ctx, "Loading...").to_be_visible();
```

```csharp
GuaAssertions.GetByRole(ui, "button", "Start Game").Click();
GuaAssertions.WaitForText(ui, "Loading...").ToBeVisible();
```

`Click()`はゲームの状態を直接変更せず、クリック要求をキューへ追加します。
ImGuiやGodotなどのゲーム側アダプターが後続フレームで要求を消費し、通常のUI入力として処理した結果をイベントとして返します。

Guaは、ゲームランタイムと自動化ツールをつなぐUIレイヤーです。ゲームエンジン、
エディターツール、テストランナー、AIコーディングエージェントと役割分担して動作します。

## 対象範囲

初期実装は、小さく安定した中核に集中しています。

- プロトコル仕様とJSON Schema
- C ABIランタイムコア
- 薄いC++ラッパー
- ImGuiアダプター
- C++・C#テストヘルパー
- C ABIに対する.NET P/Invokeバインディング
- UI Tree、ノード詳細、スクリーンショット、ログ、操作用Inspector
- ランタイムブリッジをAIエージェントへ公開するMCPサーバー
- 共有ネイティブランタイム上で基本的なUI Tree収集とボタンクリックを示す、実験的なGodot 4.7 C#サンプル
- GDExtension経由で標準Control向けの全機能を提供する、推奨Godot 4.7 GDScriptアドオン
- UI Toolkit、uGUI、TextMeshPro向けUnity 6ランタイムパッケージと、Editor Play Mode・desktop Mono Player用の外部テストホスト

エンジン固有機能はGuaの中心ではなく、プロトコル上に構築するアダプターとして扱います。現在はGodotとUnityに対応しており、追加エンジンも同じ境界上へ実装できます。

## ネイティブツールチェーン

Windowsのネイティブ開発ではMSVCを主要ツールチェーンとして使用します。

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug
```

ネイティブコア、WebSocketブリッジ、ランタイム共有ライブラリ、native bridge
サンプルは、LinuxではGCCまたはClang、Intel・Apple Silicon macOSではApple
Clangでもビルドできます。Godot・Unityのネイティブアダプター対応範囲は各統合
セクションの記載どおりで、iOS・Androidは現在の対応対象外です。

## .NETテスト

通常は公開済みの`Gua.Testing`パッケージを参照します。このパッケージは対応するバージョンの`Gua.Core`へ依存しています。

```xml
<PackageReference Include="Gua.Testing" Version="0.5.0-preview.3" />
```

`Gua.Core`には`win-x64`、`linux-x64`、`osx-x64`、`osx-arm64`用のネイティブランタイムが含まれます。`Gua.Runtime`にも同じ4 RID用のInspectorブリッジランタイムが含まれ、通常の復元・ビルドによって現在の環境に合うライブラリが出力先へコピーされます。ローカルビルドを使う場合は`GUA_NATIVE_DIR`と`GUA_RUNTIME_NATIVE_DIR`で上書きできます。

ローカルで`Gua.Core`または`Gua.Runtime`をpackする場合は、各package READMEに
記載した4 RIDのディレクトリを先に揃え、その絶対パスを
`GuaNativeAssetsRoot`として渡します。

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

NUnitサンプルは次のコマンドで実行できます。

```powershell
dotnet test examples/dotnet-nunit/GuaDotNetNUnitSample.csproj
```

### ネットワークモックとテスト開始状態の準備

Guaは独自のネットワークモックエンジンを提供せず、任意のゲーム通信を自動的に
捕捉・差し替えません。既存のモックライブラリ、モックサーバー、ゲーム側の
テスト実装はGuaと併用できます。ゲームの言語、実行環境、差し替え可能な依存の
境界に適した手段を選んでください。

C#/.NETでは、インターフェースやオーバーライド可能なメンバーを通じて依存
オブジェクトを差し替える[Moq](https://github.com/devlooped/moq/wiki/Quickstart)が
選択肢の一つです。ゲーム自身が差し替えたオブジェクトを使う必要があり、テスト
ランナー内のモックだけでは別プロセスのゲームの依存は置き換わりません。
Moqはネイティブ通信や任意の外部プロセスを自動捕捉するツールではありません。
全エンジン・全ビルドで使えると想定せず、対象実行環境への対応を確認してください。

開始状態を準備するだけなら、必ずしも通信モックは必要ありません。テスト用
セーブ、ゲームの初期化処理、テストサーバーの準備APIなどで、必要な所持品、
進行状況、テストアカウントを用意できます。これらはゲーム側で選ぶ方法であり、
Guaに組み込まれた準備APIではありません。テストfixtureで状態を準備し、操作前に
観測可能なゲーム状態から前提条件の成立を確認し、終了後にfixtureが管理する状態と
リソースを片付けてください。[NUnitサンプル](examples/dotnet-nunit/README.md)は
setup／teardownとGuaコンテキストの分離を示しています。Guaコンテキストのresetは、
セーブ、アカウント、バックエンドのデータを復元するものではありません。

モックした境界と、本物のサービスを使って試験した範囲を記録してください。
モック応答に対するクライアントUIの確認は、そのクライアントの振る舞いの検証であり、
実バックエンドや実認証の検証にはなりません。それらを試験対象にする場合は、
本物のサービスを使う統合テストを別途行ってください。

### Unity 6 desktop Mono

`Gua.Core`、`Gua.Testing`、`Gua.Testing.Visual`、`Gua.Testing.Recording`は
`net10.0`と`netstandard2.1`の両方を対象にします。
既定の**.NET Standard 2.1** API Compatibility Levelを使うUnity 6では、
native C ABIを変えずにmanaged assemblyを読み込めます。対応対象は
Windows x64、Linux x64、Intel／Apple Silicon macOSのEditorとStandalone Mono Playerです。managed assemblyとNuGet依存assemblyを
`Assets/Plugins/Gua/Managed`へ、`gua.dll`を`Assets/Plugins/x86_64`へ配置し、
UnityのPlugin Import SettingsでWindows EditorとWindows Standalone x86_64を
有効にします。

Unity Package Manager向けのビルド済み`.tgz`は各GitHub Releaseへ添付されます。
Unity Package Managerの**Add package from tarball**から導入できます。パッケージは
自動起動し、UI Toolkit、uGUI、TextMeshProのランタイムUIを収集します。
`Gua.Testing.Unity`はEditor Play Modeとdesktop Mono Playerの外部テストホストを
提供します。検証済みfixtureは[`examples/unity-smoke`](examples/unity-smoke/README.md)を
参照してください。IL2CPP、IMGUI、EditorWindow UIには未対応です。

## Inspector

InspectorはGuaプロトコルのスナップショットを表示するReactアプリケーションです。MCPには依存せず、`GuaInspectorClient`抽象化を通じてWebSocketブリッジやネイティブランタイムへ接続します。

ブラウザー版を起動します。

```powershell
bun run --filter @gua/inspector dev
```

別のターミナルでサンプルWebSocketブリッジを起動します。

```powershell
bun run bridge:ws
```

Inspectorの接続先は次のとおりです。

```text
ws://127.0.0.1:8765
```

InspectorのAutomationパネルでは、画面から実行したSemantic操作の記録、
`recording.schema.json`の読み込み・ダウンロード、全Semantic操作のReplayを
行えます。秘密値はメモリ上のJSON mapからだけ解決します。Visual comparisonでは、
現在のスクリーンショットまたは選択した画像をbaselineにしてブラウザー内で比較し、
Actual・Expected・Diff画像とmanifestをダウンロードできます。ブラウザー版Inspectorが
任意のローカルパスへ暗黙に書き込むことはありません。
座標fallbackを含むRecordingもschema v1として読み込めますが、Inspectorは実行せず、
Replayは既定でSemantic targetだけを使用します。

静的InspectorのビルドとTauriデスクトップシェルの開発起動には、次のコマンドを使用します。TauriにはRustツールチェーンも必要です。

```powershell
bun run --filter @gua/inspector build
bun run --filter @gua/inspector tauri:dev
```

## MCP

MCPサーバーは、Inspectorと同じブリッジを利用する薄いプロトコルクライアントです。ランタイムブリッジを起動してから、stdioサーバーを実行します。

```powershell
bun run bridge:ws
bun run mcp
```

npmで公開されている`gui-mcp`は、MCPクライアントから次のように起動できます。

```powershell
bunx gui-mcp@latest mcp
```

既定では`ws://127.0.0.1:8765`へ接続します。別のランタイムアダプターへ接続する
場合は、`GUA_BRIDGE_URL`を指定します。

```powershell
$env:GUA_BRIDGE_URL = "ws://127.0.0.1:8765"
bunx gui-mcp@latest mcp
```

提供するMCPツールは次のとおりです。

```text
get_ui_tree
get_world_object_tree
find_world_objects
wait_for_world_object
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

Recording、baseline、Visual失敗artifactは既定で`.gua`へ保存します。
`GUA_ARTIFACT_DIR`で保存rootを変更できますが、MCPツールへ渡した名前からroot外へは
書き出せません。接続先bridgeが対応している場合、Semantic action toolは
request IDに対応するhost完了eventまで待機します。

World Object Treeのtoolはread-onlyであり、公開profileはhost側で固定されます。
player向けMCPではhost processへ`GUA_OBSERVATION_PROFILE=player`を設定してください。
tool引数からdebugへ昇格することはできず、world action toolも提供しません。

## Godot 4.7 C#サンプル

> **実験的機能 — 基本機能のみ。** このサンプルは、共有ランタイムブリッジ、
> 基本的なSemantic UI Tree収集、スクリーンショット、ボタンクリックを示すものです。
> GDScriptアダプターと機能的に等価ではありません。新しいGodot統合では、
> .NET対応プロジェクトを含め、後述のGDScriptアドオンを使用してください。
> Godotでは、C#のゲームスクリプトとGDScriptアドオンを併用できます。

`examples/dotnet-godot`には、`Godot.NET.Sdk/4.7.0`と`net10.0`を使用する
最小サンプルがあります。

```powershell
dotnet build examples/dotnet-godot/GuaGodotSample.csproj -v:minimal
```

サンプルは`GuaGodotRuntime`をルート`Control`へ取り付け、標準コントロールをSemantic UI Treeへ自動収集します。また、ゲームプロセス内でInspectorブリッジを起動し、外部のクリック要求をGodotの通常のボタンシグナルとして処理します。

Godotシーンの外部テストには`Gua.Testing.Godot`を使用できます。

```csharp
using var host = GodotSceneTestHost.Load("game/scenes/title_screen.tscn");

GuaAssertions.GetByRole(host.Context, "button", "開始").ToBeVisible();
host.Click("CenterPanel/Content/ButtonBox/StartButton",
    nextScene: "game/scenes/village_list.tscn");
GuaAssertions.GetByRole(host.Context, "button", "Create").ToBeVisible();
```

## Godot 4.7 GDScriptアドオン

これはGDScriptプロジェクトと.NET対応プロジェクトの両方に推奨するGodot統合です。
標準Control向けアダプターは、現在Guaが文書化しているGodot機能一式を実装しています。

`native/gua-godot`は、`native/gua-runtime`上に構築された薄いGDExtension
アダプターです。Windowsではランタイム実装をGDExtensionへ静的に組み込み、
Debug・Release両方のアドオンバイナリが、構成を区別できない共通の
`gua_runtime.dll`へ依存しないようにしています。GDScript側でランタイムコアを
再実装しません。

WindowsのDebug版とRelease版GDExtensionは、別々の構成済みビルドツリーで
ビルドします。

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug --target gua-godot
cmake --preset windows-msvc-release
cmake --build --preset windows-msvc-release --target gua-godot
```

構成別のDLLは`examples/godot-gdscript/addons/gua/bin`へ出力されます。Godotは
エディターとDebug ExportではDebug DLL、最適化したWindows Exportでは
Release DLLを読み込みます。

ゲームスクリプトでは、自動収集アダプターを明示的にプリロードします。

```gdscript
const GuaAutoAdapterScript := preload("res://addons/gua/gua_auto_adapter.gd")

var ui := GuaAutoAdapterScript.new()
```

アダプターはルート`Control`から標準コントロールを収集し、ボタンシグナルを監視して、Inspectorからのクリック要求を通常のGodot入力経路へ送ります。

## リリース自動化

手動で作成した`gua-vX.Y.Z`タグをpushすると、リリースワークフローが実行されます。
Inspector・Godotアドオン・Unityパッケージはまとめてビルドされ、対応する
GitHub Releaseへ添付されます。MCPと.NETパッケージも同じバージョンでnpm・
NuGetへ公開されます。`main`への変更だけではパッケージは公開されません。ImGui
アダプターは引き続きリポジトリ内のソースとして利用できますが、正式なRelease
ファイルとしては公開しません。

公開されるGitHub Releaseのファイルは、`1.0.0`を例にすると次の構成です。

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

`gua-godot-addon-v1.0.0.zip`には、4 RIDとGodot Web向けのDebug・Release
GDExtensionを含む単一の
`addons/gua`ツリーが入ります。
アドオン内の各ファイル、Unity WebGL用静的ライブラリ、ImGui ZIPはGitHub
Releaseへ個別公開しません。静的ライブラリはUnity Package Managerアーカイブに
収録されます。
各`gua-native-<rid>-v1.0.0.zip`には、そのRID向けの`Gua.Core`・`Gua.Runtime`
共有ライブラリと`LICENSE`が入ります。

## リポジトリ構成

```text
protocol/             プロトコル仕様とJSON Schema
native/gua-core/      C ABIランタイムコアとC++参照実装
native/gua-runtime/   Godot C#・GDScript用共有ネイティブランタイムブリッジ
native/gua-imgui/     ImGuiアダプター
native/gua-testing/   C ABI上のC++テストヘルパー
native/gua-godot/     GDScript用Godot GDExtensionアダプター
bindings/dotnet/      .NET P/InvokeバインディングとC#テストヘルパー
packages/mcp/         公開MCPサーバーパッケージ
packages/inspector/   ブラウザー・TauriデスクトップInspector
examples/             Godotサンプルを含む最小デモとサンプル
docs/                 ネイティブツールチェーン・変更監査資料
```

## Contributor向け変更監査

repository fileを変更した後は、GuaのCodex instructionによりfocused validationと、
関連untracked fileを含む累積diffへのread-onlyな`gua_auditor`監査を1回実行します。
PR全体を意図的に監査する場合は次を実行します。

```powershell
./scripts/run-local-pr-audit.ps1 -Base origin/main
```

harnessは再現可能なfindingを報告しますが、commit、push、GitHub comment投稿は
行いません。詳細は[Bug-hunting subagentとlocal PR audit](docs/bug-hunting-subagent.md)
または[変更監査Docs](https://gua.orizika.com/ja/docs/change-audit/)を参照してください。

## ライセンス

MIT
