# 遠隔ビルド variant の検証記録

この文書は実装途中の証拠台帳。最終 HEAD の CI・レビュー完了とは区別する。
設計契約は `protocol/specs/remote-build-variants.md`。

## ローカル環境とスナップショット

Windows x64 / MSVC 19.51 / Visual Studio 18 2026 / CMake 4.3 / .NET 10。
最初のローカル検証は base `decece496ea239cb811080b7aaa571e386d482e3` 上の
実装作業ツリーに対するもの。build-info の base SHA だけを、変更後コミットの証明とはしない。
Godot は既存の公式 4.7 stable。ローカル GDExtension の依存は既存 godot-cpp source と
RefCounted/JSON/OS を含む縮小 API profile を使用した。CI は既定 API profile で構築する。

| 契約 | 具体的違反と検査 | ローカル結果 |
|---|---|---|
| Shipping は compile/link で遠隔なし | native shared と Godot static の実 runtime.obj に BridgeServer 参照がある、または生成 project が WS target をリンクする | `verify-remote-build-graph.ps1`、Shipping 両 target 合格 |
| Shipping は runtime で復活不可 | env/profile/API 指定後 start/running/URL が有効になる | `build_variant_tests.cpp`、Shipping 合格 |
| Development ON と OFF の分離 | ON で接続不可、または OFF が起動する | Debug の両構成で native、managed、object、package 検査合格 |
| QA 正常系 | 実 WS の公開 UI 操作や時計操作が動かない | `Gua.BuildVariants` の実 ClientWebSocket と host consume/completion 合格 |
| Player 固定と core 迂回防止 | 初期 Debug、Debug setter/client、borrowed context/unscoped read から hidden UI を取得できる | native、C#、Godot binding の各拒否と公開操作の正常系合格 |
| 入力許可失効と held cleanup | 借用 core から許可なし入力を enqueue、失効後 consume、失効した hold の中立化漏れ | native にて拒否と cleanup request の生成・完了を確認 |
| AI Player の遠隔 QA コマンド除外 | Player 版で clock capability を広告、または clock_install が成功 | 実 WS にて capability 不在・失敗を確認。QA では成功 |
| 最終 Godot consumer | 正しい class 未登録、hidden UI 漏えい、bridge 可否不一致、Shipping PCK に WebMCP resource が残る | Shipping/QA/AI Player の実 Release EXE+埋込み PCK が合格。Development は Editor/source 実行が合格 |
| 配布物の混入拒否 | QA native/Godot DLL、WebMCP script を Shipping/AI Player に混ぜる。hash も更新して検査回避を試みる | `test-remote-package-mixing.ps1` の6ケースで狙った hash/identity/余分な resource の検査が失敗することを確認 |
| 不正なビルド条件を拒否 | Shipping+ON、QA/Player+OFF、旧 WS option だけ ON、未知 variant | `test-remote-build-config.ps1` の5ケース合格 |
| Unity managed の実体除外 | Shipping assembly に HandleWebRequest が残る、または compiled constant が native 目的と違う | 既存 Unity 参照 DLL に対して実 runtime source をコンパイル。Shipping=false/false/handlerなし、Development=true/false/あり、AI Player=true/true/ありを PE metadata から確認 |

ローカル native/managed/GDE package の5構成ログは `artifacts/remote-validation/`。
object・生成 project の検査は各 `build/remote-*/remote-build-evidence/`、
CTest の実出力は各 `Testing/Temporary/LastTest.log`、staging は各 `package-*`。
Godot EXE/PCK と実行 log/result は `build/godot-variant-*`。
混入試験の各拒否理由は `build/package-mixing-*/results.json`。

## 失敗・未検証を分ける

- 初期 native/C# 試験の引数名・呼出し名の誤りは修正して再検証した。
- 最初の Godot 縮小依存 profile に OS class が不足していたため追加して再構築した。
- console wrapper を export template とした試行は PCK 領域がなく失敗。正規 Release template へ修正した。
- GUI subsystem の EXE の stdout だけでは結果が得られなかったため、終了待機と専用 userdata の結果・log で実 consumer を検証した。
- Development の editor import は終了時に失敗した。これを解決済みとは扱わず、fixture が必要とする直接の Editor/source game 実行を別に実行して合格を確認した。
- 構成否定試験の長い一時パスが MSBuild で失敗したケースは否定試験の合格とせず、パス短縮後に狙った configure error を確認した。
- .NET build は合格したが、この環境では NuGet vulnerability feed に接続できず NU1900 警告が残った。脆弱性 audit 成功とは主張しない。
- Unity 実 Player/Editor のビルド・実行、各 variant の Web/WASM 実 consumer、Windows 以外の出荷 variant、最終 HEAD CI/独立・外部レビューは、この台帳初版時点では未完了。静的 IL や他 engine の合格で代用しない。

## 独立監査 pass 1 と修正

固定 HEAD `f1d81f7c6fc6f13454b9599df3dca2722bc5e1f2` を独立・読み取り専用で監査し、4件を修正した。
Unity の PackageInfo 曖昧参照は Editor guard 実コンパイルで再現し、限定名で解消。
WebGL は desktop DLL の必須数判定から static archive の manifest/hash 検査へ分岐し、
Emscripten で実リンクした Node probe の immutable build-info から archive manifest を生成する。
Player legacy polling は click/focus だけを取り出し、set-value 完了の request-specific v3 取得を壊さない。
reset/status のイベント数と strict dirty 判定は公開 Player イベントへ投影する。
後二件は `build_variant_tests.cpp` に exact-count / correlation / private-public 比較を加え、
AI Player Release の実 native テストで合格（`artifacts/remote-validation/ai-review-fixes.log`）。
Unity runtime source の PE metadata では Shipping の remote handler / native start reference /
Web install import がすべて absent、Development/AI Player ではすべて present。
`artifacts/remote-validation/unity-editor-guard.log` の最初の失敗は修正後の成功ログへ更新した。

追加した Web static probe/UPM多プラットフォーム検査と新 CI consumer job は、最終 HEAD CI の結果で判定する。

## 実 consumer と回帰の追加検証

`b4c8d74` 上のローカル作業ツリーで、Unity 6000.5.3f1 の既存ライセンスと
隔離した新規 project を使い Shipping Release Player のビルド/実行を確認した。
ログと実結果は `build/unity-variant-shipping-20261101T021555639/{editor.log,player.log,Player/result.json}`。
検査するのは実配布 DLL の local API / Player 投影 / immutable identity / bridge 可否であり、
GuaUnityRuntime の全 UI 反映経路の合格と拡大解釈しない。
fixture の初期失敗は Editor 用 InputSystem DLL の Player 混入と依存 package 自体の NUnit テストだった。
既存 package source を fixture 内へコピーして Player 用にコンパイルし、未使用の package test は除いた。
AI Player の実ビルドで Editor guard の const inlining も検出し、runtime accessor 呼出しに修正した。
同一 Editor guard と4つの native/managed variant の最終確認は実行中。

通常 .NET selector/remote/Trace 回帰は `selector-regression-prepared.log` にて465/465合格。
準備前の実行は450/465で、未構築の Trace viewer/MCP と deadline 失敗を含んだため合格とはしない。
MCP/Trace を lockfile に従って構築し、Bun の PATH を揃えた実行で全件合格した。
Windows Shipping の native runtime / embedded Godot object・link graph と最終 DLL の
socket DLL 依存不在も再確認した。graph 検査は保存済み証拠 project を再探索しないようにし、再実行も合格。

Draft PR は https://github.com/gua-project/gua/pull/186 。初回 CI は `b4c8d74` の PR merge snapshot。
Web static identity の4構成、標準回帰の Windows/Linux/macOS native/managed lane は合格した。
新たな実 WebAssembly variant acceptance と portable Shipping/AI Player CI は追加後 HEAD で評価する。

## 最終監査 pass 2 と追加修正

固定 HEAD `9df9192` の最終独立監査で、前回4件の修正を確認し、UPM native 配布先に
改名 QA DLL を加えた場合の extra-file 検査漏れを1件報告された。
全 native ファイルを必要な pair/manifest/Unity meta と照合する共通検査を追加し、
UPM verifier と実 Editor guard の双方で拒否する。managed 依存も既存 Plugins の
wildcard コピーから、明示した binding と解決済み dependency closure のコピーへ変更した。
既存 Windows 開発用 packager も native を2個だけ明示コピーし IL/配布先を検査する。
`test-unity-package-mixing.ps1` の正常→改名 QA DLL 故障注入が期待した
`Unexpected Unity native package file: gua_runtime_old.dll` で拒否された。
実 Unity Editor でも同じ混入を行い Player 生成前の guard が同じ理由で失敗した。
実ログは Shipping fixture の `editor-renamed-qa-fault.log`。故障ファイルは除去済み。
監査は通常タスクの上限2/2を消費しており、修正の検証は親が担当、追加監査は行わない。

最終修正 guard を再コンパイルし、同一 guard を4つの既存隔離 fixture へ入れて
Player を再ビルド・実行した。4構成とも `GUA_UNITY_VARIANT_PASS` と終了0を確認。
`build/unity-variant-{shipping,ai-player,qa,development}-*/editor-final.log` と
`player-final.log`、`Player/result.json` に実証を保存した。
native identity は各構築時点のものであり、全件を同一最終 HEAD の native build と扱わない。
最終 HEAD 自体の新規ビルドは CI 側で判定する。

HEAD `9df9192` の PR CI では、実 WebAssembly（Node上）の4構成 profile/input/resetテスト、
Linux/macOS の Shipping・AI Player 実 native と staging、Development OFF が合格。
Windows full-profile GDE の4構成と Godot packaged consumer は実行中。
これは WebGL browser/Unity WebGL Player の完了証明ではなく、各証拠の実行経路を区別する。
