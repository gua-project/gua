# 通常出荷版・開発用・QA・AI Player のビルド

通常出荷版は遠隔機能なし、AI Player 出荷版は Player 制限の遠隔機能あり。
どちらも Release だが、用途と組み込む機能が違う。

| 用途 | CMake flavor | 明示 opt-in | 上限 |
|---|---|---|---|
| 通常出荷 | shipping | OFF | 遠隔実装なし |
| 開発 / Editor | development | OFF / ON | ローカルのみ / Debug |
| QA Release | qa | ON | Debug。Player に制限可 |
| AI Player 出荷 | ai-player | ON | コンパイル時から Player 固定 |

ソースから通常版を作る例：

```powershell
cmake --preset windows-shipping
cmake --build --preset windows-shipping --target gua gua-runtime gua-godot
scripts/stage-remote-build.ps1 -BuildDirectory build/windows-shipping `
  -OutputDirectory artifacts/shipping -Flavor shipping -Remote off `
  -GodotExtension build/windows-shipping/stage/addons/gua/bin/gua_godot.windows.release.x86_64.dll
```

QA は `windows-qa`、Player 出荷用は `windows-ai-player` を選ぶ。既存の
`windows-msvc-debug` / `windows-msvc-release` はサンプル検証用 Development
の明示 opt-in preset として維持する。後者も通常出荷版ではない。SDK の
既存テスト用 package を通常出荷へそのまま入れないこと。

新規 build tree の既定は shipping/OFF。通常版を実行時設定で QA に変えることは
できない。AI Player 版でも `GUA_OBSERVATION_PROFILE=debug` で権限は増えず、
作成は失敗する。Player の公開範囲・UI 操作許可・Semantic/Raw Input の個別許可は
引き続きホストが設定する。公開したくないゲーム状態は Player policy で保護する。

ローカル API を使う場合は通常版でも必要な core/runtime を残せる。Gua をまったく
使わない製品なら addon ごと出荷物から除く。bridge 関数は ABI 互換 stub として
残るため、関数の有無ではなく build-info の `remoteEnabled` と権限上限を確認する。

Godot の `OS.has_feature` は選択補助でありコード除去ではない。staging は用途別 DLL
だけを含む descriptor を作り、通常版から WebMCP script とそのロード処理も外す。
native shared と Godot 内蔵 runtime の実 build-info を照合する。

Unity のソース組込みでは `GUA_REMOTE_AUTOMATION` を明示定義した場合だけ遠隔
bootstrap/WebMCP dispatch をコンパイルする。AI Player はさらに `GUA_PLAYER_ONLY`
を定義し、対応する native artifact と組み合わせる。プロジェクトのフラグだけで
プリビルド DLL の実装は変わらない。配布済み Gua.Unity.dll についても検証ツールが
コンパイル定数と handler の IL を読み取り、用途不一致を拒否する。

native NuGet pack は、全 RID の検証済み manifest を含む `GuaNativeAssetsRoot` を要求する。
未ラベルの Windows DLL だけを拾う従来の fallback は配布の証明にならないため拒否する。
pack の既定も Shipping/OFF。既存テスト SDK 用には
`-p:GuaDistributionFlavor=development -p:GuaEnableRemoteAutomation=true` を明示する。
package ID は維持し、variant 別の出力先・native root を使う。同じ ID/version の
異なる variant を同じ feed に重ねて公開する運用は行わない。

`stage-remote-build.ps1` は空の出力先を要求し、実バイナリを probe してから
`gua-build-manifest.json` を作る。`verify-remote-build.ps1` は再度 native identity と
全ファイルの hash・余分なファイルを検査する。manifest を省略したり、名前だけを
変えても適合する成果物にはならない。各 variant のビルド/成果物を共用しない。

検証は `test-remote-build-variant.ps1` で native/API/実 WebSocket、MSVC の実 object と
生成 compile/link project、配布物を確認する。listener がないことや文字列検索だけで
コード不存在とは判断しない。Windows 以外・Unity 実 Player・Web の実行証拠は
各経路の CI/実機結果と区別し、別環境の成功で代用しない。

規範仕様は [remote-build-variants](../protocol/specs/remote-build-variants.md) を参照。
