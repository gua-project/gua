# #109 Trace 基盤の検証記録

## #125 review追加検証（2026-10-01）

追加の接続世代reviewではsubscribeの送信前に世代を保持し、失敗cleanupがその世代だけを
閉じるよう修正した。失われた返信・不正成功返信の閉鎖2/2とObserve/transport 38/38が成功。
catalogの動的enumType/member照合はschema後の配布同梱意味validatorで検証し、scalar・
list・setと空collectionの対応を確認した。schema+意味検証9/9、抽出NuGetだけからの
オフライン検証（不一致型・欠けたmember拒否を含む）、両.NET target pack、型検査が成功。

main `f8726e5`（#152のLifecycle/Web修正）取り込み後、Snapshotのcursorより古い公開sequenceを
staleとして拒否し、同一/先行Snapshotがcursorを進めないことを検証した。Trace schemaは
before/afterのenum Valueにだけ対応catalogを要求し、非enum・欠けた側・value catalogを拒否する。
Observe 25/25、schema 9/9、全Selector 341/341、Visual 100/100、Inspector/Value 236/236、
MSVC CTest 13/13、workspace型検査、両.NET targetのpack、NuGet offline schema検証が成功。
全Selectorの初回では遅い診断読取の開始待ちが失敗した。診断読取を専用スレッドで確実に
開始し、停止後の固定50ms待機を読取完了待機へ変更して再検証した。元の1秒期限と30ms
停止期限、遅延記録拒否・completion queueを消費しないassertionは維持している。

独立checkoutでmainとの文書競合を解消し、MSVC Debug nativeを再ビルドした。
Observeの不正なentry/change/catalog、enum Valueとcatalogの型・member対応、空enum collection、
enum member/構造語に一致する既知秘密文字列、unsubscribe拒否/timeout/不正成功応答を検証した。
型付きObserve payloadにredactionが必要な区間はfailedとなり、Value/catalogを保持しない。

- TraceObserveTests + ObserveTransportTests: 37/37成功。
- Selectorの独立回帰: 311/311成功。共有TraceLifecycleTestsは#152のrace修正取り込み後に別検証する。
- Gua.Visual.Tests: 96/96成功。
- Inspector/ValueのBun回帰: 223/223成功。未確認/空host identityではSnapshot差分を表示しない。
- MSVC build、CTest 12/12、両.NET target build、workspace型検査、NuGet offline schema検証が成功。
- 累積差分の読み取り専用監査後、対応するcatalogとunsubscribeの追加修正を最終監査し、追加指摘なし。

これらはローカル検証記録であり、最終HEADのremote CI・他OS・engine E2E成功を意味しない。

追加レビューではUI/World Treeのredaction後schema検証と、正規化Observe Changeの
未知field拒否を追加した。構造値button/world2dの置換はfailed・Blobなし、labelの安全な
マスクはavailableとして保持する。Tree focused 8/8、Trace schema 9/9、Visual全100/100、
Inspector/Value全224/224、両.NET target、型検査とNuGet offline検証が成功した。

## #125 T-03 の追加検証（2026-09-30）

Windows x64、MSVC 19.51 / Ninja、.NET SDK 10.0.401、Bun 1.4.0。
このcheckoutのnative core/runtimeをビルドし、実登録→実購読→Trace writer→readerを検証した。
WebSocketテストもFakeではなく実GuaRuntime/bridgeを使う。

| 対象 | 結果 |
| --- | --- |
| CTest `build/trace` | 8/8 成功 |
| `Gua.Selector.Tests`（Observe transportを含む） | 207/207 成功、skip 0 |
| 追加 `TraceObserveTests` | 10/10 成功（local/remoteパラメータを含む） |
| `Gua.Visual.Tests`（既存Trace/配布Viewerを含む） | 73/73 成功、skip 0 |
| Trace schema/fixture + Reader/ViewerのBunテスト | 18/18 成功 |
| `bun run check` | 全workspace成功 |
| `Gua.Testing` net10.0 / netstandard2.1 | 成功、警告0 |
| `bun run --filter @gua/inspector build:trace` | 成功 |

AT-TRACE-004: 同一内容を異なるworldFrameで3回取得し、1 Blobと3 Observation Recordを確認。
秘密値のValue/Enum Catalog、getter例外marker、profile不一致のデータが保存されないことを確認。
AT-TRACE-005: 位置だけが1→2に変わるWorldの内容と別々のhost frameが保存されることを確認。
AT-TRACE-006: First→Second→Thirdの実公開・受信Changeとカタログをlocal/remote経由で保存。
主結果決定時のThirdがcleanup後のFirstで上書きされないことを確認。
履歴容量1でgapを発火させ、最新Snapshotと再購読後にも古い欠損が残ることを確認。
reset後はstale、getter失敗はunavailable/error=100、再登録の初期値はnot_sampled=101。
removed-beforeの取得不能をダミーnullへ変換せず、新registrationIdを区別する。
実WorldからObjectを削除し、同Runtime IDを再生成しても古い値と新しいOwnerを混同しない。
並列Snapshot、Trace byte容量超過、参照整合性を確認。partial/failed/stale/outsideRetentionと
正常不在をfixtureで分け、Viewerが異なるchannel/source/epochの前後比較を作らないことを確認。

VS presetの自動検出は失敗したため、VsDevCmdでMSVC環境を初期化してNinjaで構成。
Visualテストの初回1件は未生成の同梱Viewerが原因で失敗し、Viewer生成後に全件成功。
remote CI、他OS、Godot/Unityのゲームを使うE2Eは今回未実行。
各Runnerの自動取得タイミング、未公開の内部代入、失われた中間状態の復元は保証していない。

読み取り専用監査のstale/failed後の再Pollがgapへ変わる指摘を、親側の回帰試験で再現した。
理由を購読状態に保持する修正後、repeated-stale/repeated-failedと全TraceObserve回帰に成功。
Trace schemaのstatus欠落受理も失敗fixtureで再現し、Observe v1と同じ
before/after status・Value・error整合条件へ修正。status欠落・正常Valueとerrorの併記を拒否する。

## T-02 lifecycle相関（#124、2026-09-30）

Windows / MSVC 19.51 / Ninja Debug / .NET 10.0.401で実施した。
nativeは `build/trace-124` に生成し、GUA_NATIVE_DIRで今回のDLLを指定した。

| 検証 | 結果 |
| --- | --- |
| core/shared/runtime MSVC build | 成功 |
| Gua.Selector.Tests全体 | 211/211成功、skip 0 |
| 追加TraceLifecycleTests | 14/14成功（上記に含む） |
| TraceTests / TraceStorageTests | 60/60成功、skip 0 |
| native gua-core-state-tests | 1/1成功 |
| Inspector Trace reader/Viewer | 13/13成功 |
| bun run check | 全workspace成功 |
| Gua.Testing netstandard2.1 | build成功、警告/エラー0 |
| native実測factの新journal schema検証 | Ajv2020で成功 |
| git diff --check | 成功 |

追加fixtureはsource locationなしのraw context、Selector/解決ID、自動/明示Stepの
一体化、別contextの同じrequestId、異なるepoch、同じruntimeへの複数WebSocket接続、
繰り返し操作、未完了enqueue、有限履歴のoverflow、Timeout後の遅い完了、別要求の
completion保持、Semantic/Keyboard保持、lease期限切れ後の解除確認、owner切断、
秘密値、例外type/stack維持、保存失敗、停止しないdiagnostics読取の終了時間制限、
native source切替直後の最初の自動操作のStep一体化を検証する。
既存Trace contention試験は準備記録がSelector解決前に移ったため、解決完了後の
send境界で意図的に競合するhandshakeへ更新し、取消/期限切れ後のenqueue禁止を維持した。
初回監査の3件は親側で再現し、正常完了のlate誤分類、送信前cancel/timeoutの
Unknown誤分類、source切替時のStep分裂を修正した。各回帰試験を上記に含む。

Godot/Unityのゲーム画面でのE2E、他OS、remote CIは未実行。
GDScript-only等のnative journal未提供hostは未提供品質として扱う。
native履歴を読んだだけで、入力適用時刻、解除成功、期待状態、時計同期を補完しない。

2026-09-29、Windows x64、MSVC 19.51.36260.0、.NET SDK 10.0.401、Bun 1.4.0。
これはローカルの実行証拠であり、公開 NuGet やリモート CI の完了証拠ではない。
親 Issue 全体の残りは [trace-v1.md](trace-v1.md) の対応表に記載する。

| 対象 | 結果 |
| --- | --- |
| `cmake --preset windows-msvc-debug` | インストール版番号を明示して構成成功（下記参照） |
| `cmake --build --preset windows-msvc-debug --parallel 12` | 成功 |
| `ctest --test-dir build/windows-msvc-debug -C Debug --output-on-failure` | 6/6 成功 |
| `Gua.Selector.Tests` | 134/134 成功、skip 0 |
| `Gua.Visual.Tests`（Trace を含む） | 34/34 成功、skip 0 |
| TraceTests のみ | 21/21 成功 |
| `Gua.Testing` net10.0 / netstandard2.1 | 両方ビルド成功、警告 0 |
| `bun run check` | 全 workspace 成功 |
| Inspector テスト | 26/26 成功（Trace 11 件を含む） |
| Inspector Vite 本番ビルド | 成功 |
| offline Viewer build と `dotnet pack Gua.Testing` | 成功 |
| 展開済み nupkg DLL のみを参照する別 console | source project 参照なしで HTML 生成成功 |
| 外部 Runner sample | native 共通 Value を用い、成功/失敗/中断の Trace と HTML を生成 |
| ブラウザ | 3 種の静的 HTML、Inspector の同じ失敗 Trace、位置差分、未知注釈、failure-condition=true と callerOutcome=failed、攻撃文字列の text 表示を確認 |

このマシンでは VS2026 のディレクトリは存在するが既定の CMake 検出に失敗した。
インストーラーから取得した実在する情報を用い、次で構成できた:

```powershell
cmake --preset windows-msvc-debug '-DCMAKE_GENERATOR_INSTANCE=C:/Program Files/Microsoft Visual Studio/18/Community,version=18.10.12217.157'
```

DLL を使う managed テスト・sample には `GUA_NATIVE_DIR` をこの checkout の
`build/windows-msvc-debug/native/gua-core/Debug` へ設定した。
NuGet の初回 restore と Vite/esbuild は sandbox 外の既存設定・親ディレクトリへの
アクセスが必要だった。コードの条件を弱めてテストを通したものではない。

## 故障を実際に発火させた回帰

- 保存先をディレクトリではなくファイルにして write-failed を確認。
- byte 上限を小さくして detailStopped と droppedEvents、最終主結果の保存を確認。
- 完全行の後ろへ途中 JSON を追記し、不完全末尾と読取可能な完全レコードを確認。
- 添付 hash を壊し、`../` に差し替えて hash mismatch / unavailable を確認。
- completion の無相関 poll API を呼ぶと必ず失敗する fake context で非干渉を確認。
- 初回監査の終了/Dispose 競合、Windows 改行による上限超過、不正 UTF-8、data 欠落、
  未知 envelope field 消失を親側の追加テストでも失敗させてから修正。
- 最終監査の収集時刻逆行と UTF-8 byte 上限も、親側で失敗を再現して修正。
  監査は 2 回で終了し、最後の修正は上記のローカル回帰テストで確認した。
- 保持窓から消えた観測を参照する assertion を作り、欠損として表示することを確認。

生成物は `artifacts/trace-viewer`、sample の出力先、`artifacts/packages` に置く。
ソースには生成 JS/HTML、バイナリ、依存フォルダをコミットしない。

## #123 T-01 の追加検証（2026-09-30）

Windows x64、.NET SDK 10.0.401、Bun 1.4.0。下記はこの変更のローカル検証であり、
上記 2026-09-29 の native/配布/ブラウザ検証を再実行したという意味ではない。

| 対象 | 結果 |
| --- | --- |
| `dotnet test bindings/dotnet/tests/Gua.Visual.Tests/Gua.Visual.Tests.csproj --no-restore --filter "FullyQualifiedName~TraceStorageTests\|FullyQualifiedName~TraceTests"` | 60/60 成功、skip 0 |
| `dotnet build bindings/dotnet/src/Gua.Testing/Gua.Testing.csproj --no-restore --framework netstandard2.1` | 成功、警告 0 |
| `bun test packages/inspector/test` | 28/28 成功（Trace 13 件） |
| `bun run --filter @gua/inspector check` | 成功 |
| `bun run --filter @gua/inspector build:trace` | 成功 |

受け入れ条件との対応:

- AT-TRACE-003: 既存の host/収集時計分離、時刻逆行拒否、遅延結果と主結果の分離を再検証。
- AT-TRACE-007: 105 Step について capture 2 × save 2 × 主結果 4 の 16 組合せを実行。
  正常 eviction、添付保持、成功時破棄、非成功/中断/Unknown 時の保存を検証。
  未終了・Unknown/Interrupted Step がある Passed 主結果も保存し、保持窓外の未終了数を失わない。
- AT-TRACE-008: イベント、Snapshot、添付の単体上限とメモリ/キュー byte・item 上限を独立に発火。
  キューの fixture は session lock で background writer を止め、機械の速度に依存せず飽和させる。
  flush gate を占有して timeout を発火し、後続の最終化でも主結果と timeout 品質を保持する。
  既存の artifact 全体上限、保存先エラー、未 finalize、不完全末尾も再検証。
  完全な途中レコードを削除して `sequence-gap` を確認。reader 件数/行 byte 上限も別 fixture。
- AT-TRACE-009: Recent の保持データと Streaming の待機 batch を直接検査し、秘密 marker が
  Event・Blob の保存用 bytes・hash に入らないことと、player profile の保存を検証。
  既存の source path、例外、sensitive 引数、hash 改ざん、範囲外参照拒否も再検証。
- 残存参照: Recent checkpoint 後に親を eviction し、共有添付は保持したまま
  `step-outside-retention` を報告する。.NET と Inspector の欠損判定は一致する。

fixture は正常な Step 終了を捏造しない。強制終了時のメモリのみ・未flush分の完全保存、
応答しない filesystem、敵対的な同時ファイル置換の保証は引き続き対象外である。
この対応で親 #109 や他の子 Issue の接続・Viewer受け入れ条件を完了扱いにしない。
