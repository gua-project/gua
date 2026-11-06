# ゲーム側で Gua の差し替えを拒否する仕組み

これは実装途中のレビュー用説明です。ローカルの NuGet／UPM 導入は試験済みですが、正式な配布ワークフローへの統合と全エンジンへの対応はまだ完了していません。公開済み v1.2.0 を安全な導入先として推奨するものではありません。

## 検証を担当するもの

期待するファイルの SHA-256 は、ゲームをビルドする時にゲーム側のコードまたは検証データへ埋め込みます。起動した Gua DLL に「遠隔操作できるか」を問い合わせて許可する方式ではありません。

| 対象 | 検証コードと期待値の配置 | 現在の確認範囲 |
| --- | --- | --- |
| Windows .NET | ゲーム assembly に生成するコード | 正例、権限、公式 DLL 一式交換、CI |
| Windows Godot GDScript | ゲーム PCK 内の最初の autoload | Editor からの起動と Release EXE、実通信、公式 GDExtension 交換 |
| Windows Unity Mono | ゲーム固有の検証 DLL と、その内部リソース | 実 Player の通常版／テスト用／Player 用、通常版からの公式 SDK 一式交換 |
| Windows Godot .NET 4.7 | ゲーム assembly 内に生成する初期化コード・期待値 | 末尾の Godot .NET 追加対応を参照。GDScript 用とは別の初期化経路 |

Unity の検証 DLL は提供側で作った雛形に、ゲームのビルド時に期待値を埋めたものです。通常の Gua SDK DLL 一式には入らない、ゲーム固有の名前で出力します。この検証 DLL 自体のコードやデータまで交換する場合は、今回の「ゲーム側の検証コード・データは変更しない」という条件から外れます。

利用者による Gua ネイティブ本体のソースビルドを必要としない構成を目指しています。現在はビルド済み3構成の選択と検証用 SDK を試験する段階で、既存パッケージを入れるだけでこの導線が完成しているとはしていません。

## 起動するまでの順序

.NET では、[生成コード](../tools/Gua.GameBuild/GameGuard.cs.txt)の module initializer がゲームの入口より先に動きます。対象ファイルを開いてハッシュを照合し、両方の native DLL を検査し終えてからロードします。検証したモジュールのハンドルを bindings の解決先として固定するため、後から `GUA_NATIVE_DIR` などで別の DLL を指定しても切り替わりません。

Godot の通常の GDExtension 自動ロードは、ゲームスクリプトより先に DLL を読み込みます。そのまま後からハッシュを見る方法は採用していません。[新しい SDK のエディタープラグイン](../bindings/godot/game-build/plugin.gd)は、選んだ DLL と descriptor を自動ロードされない場所に置きます。ゲーム側へ生成する[最初の autoload](../bindings/godot/game-build/policy.gd.txt)がハッシュを確認してから、明示的に GDExtension をロードします。SDK の context 作成も、この検証の成功を要求します。

Unity Mono では、[ゲーム固有の検証モジュール](../native/gua-game-policy/game_policy.cpp)を native plugin として先にロードします。[ビルド時の書き込み処理](../bindings/game-build/NativePolicyWriter.cs)が、実際に出力された SDK DLL のハッシュを埋め込みます。全ファイルを検査する前には Gua native DLL をロードしません。ゲーム側では、検証モジュールへの API 呼び出しより先に OS にモジュールの存在を確認する試験も行っています。managed assembly のメタデータ読み込み自体より先に介入できた、という主張ではありません。

Unity アダプターのローカル初期化は、遠隔操作を開始しない処理へ分離しました。遠隔操作は別の開始経路とし、ゲーム側の有効化されたコンパイル分岐から呼びます。

## 差し替え試験で確かめたこと

ゲーム側の検証コードと期待ハッシュを変えず、通常版の Gua ファイルを正規の遠隔 ON 版へ交換しました。Unity と .NET では managed と native の組を交換しています。Godot は native runtime を内包する GDExtension を交換しています。

ハッシュ不一致を検出して拒否し、OS が通知する DLL ロードを起動時から観測した試験では、対象 Gua native DLL のロードは0件でした。正例で実際のロードを観測できることも要求しています。Godot の console EXE は別のエンジンプロセスを起動するため、その子プロセスまで観測対象に含めています。

検査より先に DLL をロードする意図的な変異も試しました。試験がその違反を検出することを確認しており、単にエラーメッセージが出たことだけをロード前拒否の証拠にはしていません。

実行したコマンド・結果・ソースの識別情報は[英語の検証記録](game-owned-remote-integrity.md)にまとめています。ローカルの初期試験に使った native ファイルは旧 PR186 のビルド成果物であり、新 HEAD の native ビルド結果とは区別しています。

## 保証の境界と残りの作業

Windows の .NET／native 検証では、読み取りハンドルを保持して通常の書き込み・削除を拒否し、絶対パスと検証済みハンドルを使います。native 検証モジュールの依存物検索は System32 に制限します。OS・CLR・エンジンとその通常の依存物は信頼する前提です。特権による改変、任意コードの注入、すべてのファイルシステム競合まで防げるとはしていません。Godot の現行 GDScript 検証には Windows の保持ハンドルがなく、検査とロードの間を狙った実行中の改変は未対応です。

Godot の信頼点は EXE だけでなくゲーム PCK 内のコード・データも含みます。別の PCK を指定して実行することも、実行するゲーム側コードを変える行為です。

Player 用では native 側の Player 上限を維持します。公開 UI 操作の成功と、時計操作や Debug profile への変更の拒否を実際の通信／API で確認しています。通常版は遠隔実装を含まない native ファイルを選ぶため、profile や環境変数だけでその実装を戻すことはできません。

Editor 用の native 検証雛形は、エラーでもプロセスを終了しない別コードにしています。ゲーム側へ生成する [Editor コード](../bindings/unity/game-build~/GameIntegrityEditor.cs.txt)が Play 開始前に検証し、不一致なら Play を中止して復元・再起動を求めます。同じ Editor での２回の Play／編集モードへの復帰、公式 SDK 一式交換時の中止、古い検証設定と新しいコンパイルフラグの不一致の拒否を実証しました。不一致のフラグは native をロードする前に拒否します。Editor の強制終了は実施していません。専用のテスト用 Editor は完了後に通常の終了 API で閉じ、時間切れ時は終了させず PID を報告します。

Unity のローカル検証用 UPM では、`Gua/Game build` メニューから通常配布用・開発テスト用・AI プレイヤー配布用を選ぶと、プロジェクトの `GUA_REMOTE_AUTOMATION`／`GUA_PLAYER_ONLY` とゲーム側の検証ファイルを設定します。[ビルド処理](../bindings/unity/game-build~/GuaGameBuildSetup.cs)が３組のビルド済み native DLL から１組だけを出力し、実際の Player 出力の期待ハッシュを埋め込みます。利用者による Gua native 本体の追加ビルドや DLL の手動交換は不要で、通常の Unity ゲームビルドで処理します。SDK 本来の毎フレーム処理と TMP アダプターも同梱し、テスト側が代わりにフレーム処理しなくても実通信・入力反映が動くことを確認しています。

ビルド処理の検査失敗は Unity の失敗結果へ確実に伝え、以前の ON 版 EXE を同じ出力名に残さない試験も通っています。検証情報を埋め終わるまで新しい EXE は一時名にします。ただし、このコールバックに到達しない段階のコンパイル失敗まで過去ファイルを消せる、という保証ではありません。配布対象は隔離した出力先でビルドが成功した成果物に限ります。正式リリース用パッケージへの置き換えや公開はまだ行っていません。

.NET は、新しい任意導入の `Gua.GameBuild` NuGet に３構成と生成ツールを梱包する実装を追加しました。ゲーム側はこのパッケージを参照し、`GuaRemoteMode` を `testing`／`player` にしたときだけ明示的に有効になります。省略時は `off` です。別の DLL を選ぶ作業や Gua 本体のソースビルドは不要で、通常のゲームのビルドで選択・期待値生成を行います。ローカルフィードからの参照だけで３構成の publish・実行・差し替え拒否を確認しました。公開・リリースワークフローへの追加はしていません。

IL2CPP は現在の Editor に未導入のため実行未検証です。Web、その他のプラットフォーム、正式公開までの移行は未完了です。Godot .NET は専用の Windows 4.7 ホストを追加しました（末尾参照）。GDScript 用 SDK との混用は拒否します。Windows Mono の成功を、それらの保証の証拠として扱いません。

PR の CI には、試験を通った .NET／GDScript 用 SDK をアーカイブ・NuGet として受け取る候補成果物の生成を追加しています。３構成の native source が期待値と一致するか検査し、ファイル名・サイズ・SHA-256 と生成元を記録します。この配布用検査情報自体を実行時の信頼点にはしません。ローカルの生成は成功していますが、新 HEAD の hosted CI は push 承認待ちです。既存の正式配布は Linux／macOS／Web も含むため、Windows 用候補でそのまま置き換えてはいません。

正式配布ワークフローには、タグの版数を注入した native ３構成と Windows 用候補の生成・検査を追加しました。公開ジョブはその検査を待ちますが、公開するファイルの集合や NuGet の公開対象は増やしていません。定義の変更であり、hosted CI の実行は push 承認待ちです。ローカルでは新しい版数を注入した通常版 native のビルド・リンク構成・受入に成功しています。

C++ は Windows x64 MSVC 用の[組込み支援](../bindings/cpp/game-build/README.md)を追加しました。下記の範囲でゲーム側検証と遅延ロードを接続し、ローカル回帰を行っています。C++ 全プラットフォームや正式配布への統合が完了した、という意味ではありません。

Web は既存 Unity 内の Emscripten 4.0.20-git を使い、通常版・テスト用・Player 用の実 Wasm／Node による native profile・入力・reset の受入が通りました。インストールやグローバル設定の変更はしていません。これは静的入力の検証で、ブラウザー／Unity Web Player の起動前検査や SDK 差し替え拒否の証拠ではありません。対応するゲーム側ホストは未完了です。

Unity managed ３構成の正式生成元も候補ジョブへ接続しました。既存の Unity CI ビルドから API 参照を一時的に取り出し、ホスト側で３構成をコンパイルして実 IL のフラグを検査します。Unity 本体の参照 DLL は非公開のビルド入力に留め、SDK の出力は Gua の３ DLL と検査情報だけに限定します。同じ経路を既存の Windows Editor で実行し、その生成物を使った通常版・テスト用・Player 用のゲームビルド、実通信、交換拒否、Editor 復帰の回帰が成功しました。native 入力は以前の一貫した source4a の組を再利用しており、最新 HEAD の native 全構成 CI と同一視していません。

Web 静的入力の自動選択にはゲーム側ホストが引き続き必要です。Godot .NET のゲーム assembly 初期化は末尾で追加対応を説明します。Godot Web で独自 HTML の起動を SDK が担うこと、Windows だけを先行して正式サポートとする範囲は未決です。新しい大規模ホスト／カスタムエンジンを作ったり、先行公開したりはしていません。未対応経路の停止と既存の公開対象の維持を既定にしています。

## C++ の順序保証と利用方法

提供側が用意する SDK 候補には、３構成のビルド済み DLL・リンク用ファイル・ヘッダー・検証雛形・生成ツールを入れます。利用側の CMake では `find_package(GuaGameBuild CONFIG REQUIRED)` と `gua_protect_cpp_game(my_game)` を一度設定します。省略時は遠隔操作なし、開発テスト用は `MODE testing`、AI プレイヤー配布用は `MODE player` です。ゲームの Debug／Release とは別の選択です。Gua 本体のソースビルドや DLL の手動交換は不要です。この候補の生成ツールには .NET 10 runtime が必要で、既存公開 SDK の導線が既に置き換わったわけではありません。

通常のゲームビルドで、[CMake 支援](../bindings/cpp/game-build/GuaCppGame.cmake)が Gua の import を遅延ロードにし、[生成ツール](../tools/Gua.GameBuild/CppGameBuild.cs)が出力 EXE と依存 DLL の PE import を検査します。検証前に Gua をロードする通常 import があれば、配布用 EXE を残さずビルドを失敗させます。選択した実ファイルのハッシュをゲーム固有の `GameSecurity_<識別子>.dll` のリソースへ埋め込み、成功してから一時名の EXE を正式名へ戻します。配布には通常の全体ビルド、または `my_game_gua_package` が成功した隔離出力を使います。ビルド失敗以前の古い出力まで常に消すという保証ではありません。

実行時は、Gua DLL ではなくゲームへコンパイルした [guard](../bindings/cpp/game-build/delay_guard.cpp) が先に動きます。ゲーム EXE の場所からゲーム固有の検証 DLL を絶対パスで開きます。その検証処理が Gua DLL ２本を両方ともロック・ハッシュ照合し終えてからロードします。遅延ロードのフックは、そこで検証済みとなったモジュールのハンドルだけを返します。その後に通常の Gua API でローカル初期化を行えます。遠隔開始はゲーム側の `GUA_REMOTE_AUTOMATION` 分岐に入れ、通常版から呼出し経路を除きます。SDK の自己申告や同梱 manifest を実行時の信頼点にはしません。

読み込みの初期化はゲームモジュールごとに `INIT_ONCE` で一度だけ行います。これは Gua context を一つに制限するものではありません。Gua を使う別のゲーム DLL は `PLUGINS my_game_ui` と宣言して同じ支援を組み込みます。その DLL が `DllMain`／グローバル初期化で早すぎる Gua 呼出しをすると、ローダーロック中のロードを試みず停止します。通常 import の残った外部 Gua 依存 DLL は検査で拒否するため、その DLL の供給元による遅延ロード対応が必要です。無関係なライブラリーには変更を要求しません。実行時に任意の名前で開く未宣言プラグインは PE import から発見できないため、Gua 利用モジュールを宣言する必要があります。

検証 DLL とその期待値はゲーム側のコード・データです。それらや EXE まで改造されないことが今回の前提です。通常のファイル書込み・削除を防ぐ保持ハンドル、絶対パス、System32 に限定する依存検索を使いますが、特権による改変や任意コード注入、すべてのディレクトリー競合を防ぐ主張ではありません。既存 delay hook とは明示的な転送先を指定して共存させ、無関係な遅延ロードを横取りしません。

[C++ 回帰試験](../scripts/test-cpp-game-policy.ps1)では、通常版・テスト用・Player 用でローカル UI API、16スレッドからの API 利用、初期化回数１、ユーザーのグローバル初期化からの Gua 呼出し、無関係な DLL の利用を確認します。通常版を正規テスト用 DLL 一式へ交換した試験はハッシュ不一致78、欠落は81、早すぎる DLL 初期化呼出しは86で拒否し、起動時からの OS 観測で Gua native ロード０件を要求します。直接／依存 DLL 経由の通常 import を故意に残した変異は、配布 EXE が残らないことまで確認します。Player は Debug 昇格を拒否します。初回の C++ 単位試験は bridge 開始・停止まででしたが、次の候補 SDK 試験で外部からの実入力も確認しました。

## 候補 SDK を配布物として使った C++ 試験

共通の候補パッケージ生成処理に C++ ZIP を接続しました。中には３構成の DLL・import library・ヘッダー、CMake 支援、ゲーム側検証雛形、生成ツールを含めます。リンク用ファイルが受入済み native DLL と同じビルドにあること、生成元・ハッシュ・全ファイル一覧を提供側のビルドで検査します。PR 用と正式リリース用 workflow の候補ジョブにも、この生成と候補の受入試験を接続しました。定義とローカル実行までであり、hosted CI は未実行、公開パッケージは更新していません。

[候補の受入試験](../scripts/test-cpp-game-candidate.ps1)は、作業中の SDK ディレクトリーを直接参照せず、候補 ZIP のハッシュを検査してから展開し、その中身だけでゲームをビルドします。44ファイルの展開前後の一致、通常版のローカル API、検証 DLL が Gua DLL より先にロードされたことを確認しました。テスト用・AI プレイヤー用では、別プロセスのクライアントが loopback WebSocket に接続してボタン入力を送り、ゲームがその入力をちょうど１件消費し、成功結果を生成して終了しました。Player 用の時計操作は拒否、テスト用では許可されました。OS・ネットワーク設定は変更していません。

同じ候補から作った通常版のゲームで、Gua DLL ２本だけを遠隔 ON 版に置換しました。候補内の正規テスト用 DLL の組と、既存キャッシュにあった公開済み NuGet 1.2.0 の DLL の組をそれぞれ試し、どちらもハッシュ不一致78で終了、Gua native ロード０件、ゲームの入力受付へ未到達でした。1.2.0 はこの否定試験の再現入力としてのみ使い、利用推奨を意味しません。

ゲーム固有の検証 DLL、そのリソース、EXE まで差し替えた場合は保証対象外です。候補のファイル一覧や同梱 manifest は提供側の梱包検査に使う情報であり、実行時にゲームが信頼する期待ハッシュを置き換える権限を持ちません。通常のゲームビルドで生成したゲーム側の期待値を固定することが、正規 ON DLL への単純交換を拒否する根拠です。

## Godot .NET の追加対応（Windows x64、Godot 4.7）

以下は上記の初期チェックポイント以降の実装です。GDScript 用アドオンを読み込ませる方式とは別に、実際の `Godot.NET.Sdk/4.7.0` プロジェクトを共通の `Gua.GameBuild` パッケージで扱います。現在は `net10.0`、Windows x64、通常の分離ファイルによる .NET ゲームだけを受け付けます。別バージョン、Linux／macOS／Web、AOT／単一ファイル／トリミングはビルドで拒否します。通常の .NET ライブラリに `project.godot` を置くだけでは利用できません。

利用者が行うのは、提供側の候補パッケージをゲームの `.csproj` から参照し、必要なときだけ `<GuaRemoteMode>testing</GuaRemoteMode>` または `<GuaRemoteMode>player</GuaRemoteMode>` を指定して通常どおりゲームをビルド・エクスポートすることです。省略は `off`（通常配布用・遠隔操作なし）です。Debug／Release とは別の設定で、Gua 本体の再ビルドや DLL の手動交換は不要です。この候補パッケージを公開済みの製品としては扱いません。公開 NuGet 1.2.0 は差し替え否定試験の入力であり、推奨先ではありません。

[ビルド設定](../bindings/dotnet/Gua.GameBuild.targets)が選択した native 2本と実際に参照した managed 2本の SHA-256、生成元・構成を、[生成ツール](../tools/Gua.GameBuild/Program.cs)がゲーム assembly にコンパイルするコードへ固定します。[保護した Godot アダプターのソース](../scripts/stage-game-build-bundle.ps1)もゲーム側のコンパイル入力です。`off` ではその遠隔開始呼び出し自体を条件付きコンパイルで除きます。ローカル UI 反映は残します。Player 上限は選択した native のコンパイル制限も併用します。

実行順序は[ゲーム側の module initializer](../tools/Gua.GameBuild/GodotGameGuard.cs.txt)による期待値検査 → 絶対パスから native をロード → Gua の初期化です。Gua DLL が自分の種類を回答して許可する経路ではありません。各ファイルを共有書き込み・削除を許さず開き、全 managed／native のハッシュが一致した後にだけ `gua.dll`、`gua_runtime.dll` を読み込みます。Gua が既にロード済みなら拒否します。検索対象は絶対パスと OS の System32 に制限し、ディレクトリーと対象ファイルの reparse point は拒否します。読み取りハンドルと native handle は実行中保持するため、検査後の通常の上書き・削除を許しません。環境変数による別ディレクトリー指定は、この検証済み handle を置き換えません。

検証主体は交換対象の Gua 4本から独立したゲーム assembly です。Editor では Godot が assembly をストリームから読み込むため `Assembly.Location` が空になります。その場合だけ、Gua DLL ではないエンジンの `GodotPlugins.PluginLoadContext.AssemblyLoadedPath` を参照します。この根拠は Godot 4.7 の [ローダー](https://github.com/godotengine/godot/blob/4.7-stable/modules/mono/glue/GodotSharp/GodotPlugins/PluginLoadContext.cs)と[初期化処理](https://github.com/godotengine/godot/blob/4.7-stable/modules/mono/glue/GodotSharp/GodotPlugins/Main.cs)です。Release ゲームの検証元はエクスポート先のゲーム assembly のパスです。SDK 内の書き換え可能な manifest は、起動時の期待値を更新する権限を持ちません。

Editor の再読み込みでは、同じ検証済み SDK・ゲームの場所・構成・生成元・期待ハッシュの場合だけ、プロセス内に保持した検証済み handle を再利用します。共通 CLR 型だけの記録とし、古いゲーム assembly を保持しません。同一内容の DLL はビルド時に上書きしないため、ロックを保ったまま C# の通常再ビルドができます。SDK や構成を変える場合は Editor の再起動が必要です。検査失敗時はゲーム assembly の初期化を失敗させ、Editor を終了しません。旧 Gua GDExtension の自動ロード、GDScript 用 policy autoload、旧 managed アダプターとの混在は[ビルド時検査](../tools/Gua.GameBuild/GodotHost.cs)で拒否します。無関係な拡張機能のロード管理は変更しません。

Release の module initializer は Godot の native callback 表が初期化される前に呼ばれ得るため、拒否処理は Godot API を呼びません。Godot 4.7 が Editor 用に作る collectible loader と、実ゲームの CLR component loader を区別し、確認できたゲームだけを終了コード 78／79 で停止します。未知のローダーは Editor と同様に例外で初期化を止め、プロセスの強制終了対象にしません。Editor の未保存作業を失わせる終了処理は実装・試験とも行いません。

試験入口は [3構成・実通信・混在拒否](../scripts/test-godot-dotnet-game-policy.ps1)、[Release 書き出しと DLL 一式交換](../scripts/test-godot-dotnet-export.ps1)、[Editor 再読み込みと拒否後の生存](../scripts/test-godot-dotnet-editor.ps1)です。独立した OS ロード観測で「検査開始 → 一致 → native ロード」の順序を確認し、負例は単なる listener 不在ではなく、検査到達・具体的な拒否・native ロード0件・ゲーム callback 未到達を要求します。CI は [Remote build variants](../.github/workflows/remote-build-variants.yml) に同じ試験を接続します。実行済み結果と最終 HEAD の CI は PR の報告で区別します。

ここでの対象は「ゲームとエンジンが変更されず、Gua DLL 一式だけが交換された場合」です。ゲーム assembly／期待値、エンジンや CLR、OS まで改変された場合の完全防御ではありません。managed アダプターは既存の UI 用 API を対象とし、native GDExtension の空間機能まで対応したとは扱いません。Godot Web は次節の範囲で追加しています。非 Windows のゲーム側 native ローダーは未対応、IL2CPP は未導入・未検証です。これらを従来の native CI の成功で代用しません。

2026-11-06 のローカル受入では、公式 portable Godot .NET 4.7、.NET SDK 10.0.401／runtime 10.0.12 を使用しました。3構成のゲーム起動と Release 書き出し、テスト用・Player 用の実入力1件、Player の時計・Debug 昇格拒否が成功しました。通常版の native 2本交換、managed・manifest まで含む交換、native を通常版のままにした managed のみの交換は、それぞれ終了78・nativeロード0件・ゲーム処理未到達でした。Editor の通常ビルド／解放／再読み込み、拒否後の生存・正常終了と、5種類の具体的なビルド拒否も確認しています。native 入力は以前の CI の一貫した source `77d966aae50f62c4c21437e603017df5c225ee84`、ゲーム側コード・生成ツールは今回の差分です。最終 HEAD native を使った hosted CI と同一視しません。

Godot .NET の新規範囲では独立レビューを2回行い、対応すべき指摘はありませんでした。レビュー側でも、同一 DLL はロックしたまま上書きせず、構成変更はロックを解かずビルドで拒否することを確認しました。以前の基盤監査2回とは別の限定範囲であり、既存全体の監査を繰り返したものではありません。ローカル成果と最終 HEAD CI の結果は PR に分けて記載します。

## Godot Web：ページ側で検証してから読み込む

ゲーム開発者が指定する設定は GDScript 版と同じ `gua/remote_mode=off|testing|player` です。省略時は通常配布用の `off`、テスト用は `testing`、AIプレイヤー対応の配布用は `player` を選びます。提供者が用意する3構成から書き出し処理が自動選択するため、利用者による Gua のソースビルドやモジュールの手動交換は不要です。現在の対応は Godot 4.7 の GDExtension 対応・単一スレッドの Release Web テンプレートです。

1. 書き出し時に SDK のファイルと構成を検査し、選択した `gua.wasm` の期待 SHA-256・生成元・用途を**ゲームの HTML**へ埋め込みます。交換対象の SDK 内 manifest を実行時の信頼根にしません。
2. エクスポーターは HTML の最初のスクリプトに検証コードを置き、Godot の起動処理をゲーム側の検証用 factory 経由に変更します。ページのセキュリティ設定で検証スクリプトがブロックされても、この factory が存在しないため起動しません。
3. `fetch` で得た SDK の実バイト列を SHA-256 で比較し、一致した内容を非公開のバッファーへ保持します。ローダーへ渡すのはそのコピーであり、検査後にもう一度ファイルを取得しません。異なる配信元・クエリ付きの同名モジュールや XHR への迂回は拒否します。
4. ゲーム PCK の先頭 autoload がページ側の検証完了・用途・生成元を確認した後、隠した GDExtension の登録を進めます。通常版では遠隔開始処理と WebMCP bridge をゲームから除きます。Player の権限上限は native 側にも固定されています。

Godot 4.7 の `JavaScriptBridge.eval` は JavaScript の真偽値を整数として返す経路があるため、ハンドシェイクは厳密な比較結果を JSON 文字列にし、GDScript 側で文字列型かつ `"true"` と一致する場合だけ許可します。単なる truthy 判定ではありません。

標準の起動ページと、SDK に付属する確認済み `web-custom-shell.html` が対象です。他の独自 HTML は安全と推測せず、実際の起動順序の確認とブラウザー試験後に対応します。現時点では書き出しを拒否します。PWA・スレッド版・出力名 `gua.html` も拒否し、指定出力先に以前の ON 版 HTML／PCK がある場合は、その2ファイルを除きます。出力先フォルダー全体やゲームの保存データは削除しません。

この方式は Windows の DLL 検査とは異なり、ページから渡す検証済みバイト列に読み込み対象を固定します。ブラウザー、Godot のエンジン JS／Wasm、ゲーム HTML／PCK／検証コードは信頼する前提です。それらも変更された場合や、任意の別ページ・カスタムエンジンで同じ保証をするものではありません。既存のローカル API は維持し、ゲーム自身の時計操作を Player の遠隔制限と混同しません。

試験は [書き出しと具体的な拒否理由・古い成果物除去](../scripts/test-godot-web-game-policy.ps1)、[実ブラウザーと WebMCP 入力](../scripts/verify-godot-web-game-policy.ts)、[検査迂回を意図的に入れた検出確認](../scripts/test-godot-web-game-guard.test.ts)です。ローカルでは公式 Godot 4.7・Chrome 156 で3構成と付属独自 HTML の起動、テスト用／Player 用それぞれ入力1件、Player Debug 昇格拒否、ON Wasm 交換時の検証済み0件・ゲーム未起動・遠隔ポートなし、検証スクリプトをブロックした場合の SDK 取得0件を確認しました。ローカル native 入力は source `a50bd054e5af3c766d4defdc4e0bd7e6168b5907` です。Linux 上の実ブラウザーと最終 HEAD の結果は CI で別途記録し、未実行の段階では成功扱いしません。
