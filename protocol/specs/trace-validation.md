# #109 Trace 基盤の検証記録

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
