# #109 Trace 基盤の検証記録

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
