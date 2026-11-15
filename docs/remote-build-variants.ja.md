# 通常出荷版・開発用・QA・AI Player のビルド

最初に、出荷するゲームを外部から操作できるようにするかを決めます。
通常の出荷版は遠隔機能を組み込まず、AI Player 版は Player に公開した情報と操作だけを使えるようにします。
どちらも Release 構成でビルドします。遠隔機能とその権限上限は、下表のビルド用途と明示設定で決まります。

| 用途 | `GUA_DISTRIBUTION_FLAVOR` | `GUA_ENABLE_REMOTE_AUTOMATION` | 遠隔操作の権限上限 |
| --- | --- | --- | --- |
| 通常出荷 | `shipping` | `OFF` | 遠隔機能を組み込まない |
| 開発・Editor | `development` | `OFF` または `ON` | `OFF` ならローカルのみ、`ON` なら Debug まで |
| QA 用 Release | `qa` | `ON` | Debug まで。ホストで Player に制限できる |
| AI Player 出荷 | `ai-player` | `ON` | Player に固定 |

ソースから通常版を作る例：

```powershell
cmake --preset windows-shipping
cmake --build --preset windows-shipping --target gua gua-runtime gua-godot
scripts/stage-remote-build.ps1 -BuildDirectory build/windows-shipping `
  -OutputDirectory artifacts/shipping -Flavor shipping -Remote off `
  -GodotExtension build/windows-shipping/stage/addons/gua/bin/gua_godot.windows.release.x86_64.dll
```

QA は `windows-qa`、Player 出荷用は `windows-ai-player` を選びます。
`windows-msvc-debug` と `windows-msvc-release` は、どちらも遠隔機能を有効にした開発・サンプル確認用です。
通常出荷には `windows-shipping`、AI Player 出荷には `windows-ai-player` で作った成果物を使います。
遠隔機能を有効にした開発用のテスト SDK は、どちらの出荷物にも含めません。

新しいビルド先の既定値は `shipping`／`OFF` です。組み込まれていない遠隔機能は、
実行時に環境変数や引数を変えても有効になりません。AI Player 版では
`GUA_OBSERVATION_PROFILE=debug` を指定するとランタイムの作成が失敗します。
Player に見せるゲーム状態、許可する UI 操作、Semantic Input と Raw Input の権限は、ホストで個別に設定します。

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

ビルドと実行時の正確な条件は [遠隔機能の仕様](../protocol/specs/remote-build-variants.md) を参照してください。
実際に確認した環境と結果は [用途別ビルドの検証記録](remote-build-variants-evidence.md) にあります。
