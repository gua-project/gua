# Gua Trace v1（#109）

Trace は操作と観測の事実を保存する。Goal、条件の時間的採点、主結果の
優先順位、再送、Replay、入力解除、時計操作は呼び出し側の責務である。
保存失敗によって主結果を変更しない。

## 形式と互換性（OPEN-01）

共通Viewer、静的report、任意のcaller-authorized Screenshot/overlayと版固定配布は
[Trace Viewer](trace-viewer.md) に記載する。

`protocol/schema/trace.schema.json` を envelope の契約とする。
一つの生成済み `traceId` ディレクトリに `manifest.json`、`events.jsonl`、
`snapshots/<sha256>.json`、`attachments/<sha256>.json` を置く。
Recordingは別Save/Load形式を維持する。Trace添付は`gua.trace.recording.v1`のredacted envelopeで、
元Recordingのparserへ渡さない。Traceは再生可能性を保証しない。

schemaVersion は 1。未知の major version は reader が拒否する。
同じ version の未知の追加フィールド・event type・添付 schema・注釈 namespace は
保持し、汎用 JSON として表示する。必須フィールドの変更は改版する。
イベントは完全な UTF-8 JSON と改行の組で確定する。不完全な末尾は復元せず、
それ以前の完全レコードを読む。破損レコードでは読み取りを停止して欠損を示す。
manifest は一時ファイルから置き換える。未 finalize・読取上限・hash 不一致も
主結果とは独立した issue である。
Streaming、および Step の通常 eviction がない Recent では、途中の完全レコードの
欠落も `sequence-gap` で示す。末尾の sequence が一致するだけで完全とは判定しない。
保持されていない `parentStepId` は `step-outside-retention` として示す。

`sequence` は収集順の正整数。`collectedMilliseconds` は session 内の単調時計。
`startedAt` は UTC 壁時計で、ホスト時計の同期保証には使わない。
sourceId/sessionEpoch/requestId が一つの要求のキーである。native uint64 は
JavaScript の精度欠落を避けるため十進文字列にする。同じ requestId を異なる
source/epoch に流用しても相関しない。sourceId は呼び出し側が公開可能な識別子を渡す。
stepId/eventId/observationId は別の識別子である。

## Step と追記 Event

- `step.begin`: kind（action/assertion/mark/lifecycle）、label、任意の request、parentStepId、source。
- `step.end`: 呼び出し側の outcome（passed/failed/interrupted/unknown）。最初の終了のみを記録する。
- `request.correlated`: 既存 Step と要求キーを関連付ける。同一キーから新しい操作 Step を作らない。
- `request.phase`: request、phase、元の result。phase は送信、enqueue、consume、completion、
  hold、release-requested、release-confirmed、late-completion 等、呼び出し側が実際に確認した段階。
- `request.sending/enqueue/completion`: .NET completion helper の実測事実。enqueue の
  成功と host completion、さらに期待状態の成立を区別する。
- `caller.result`: Timeout/中断等。副作用が取り消されたことを意味しない。
- `annotation`: namespace 付き name と native-backed 共通 Value。
- `assertion.evaluation`: target/operator、存在する expected/actual Value、truth
  （true/false/unknown）、role、observations、callerOutcome。failure-condition の
  true と callerOutcome=failed は両立する。
- `observation.change`: target、change（added/removed/changed）、host、continuity、
  before/after Value。追加は after のみ、削除は before のみ、変更は両方。
- `attachment`: schema 識別子と blob。lint/comparison/diagnostics/Recording/AI の
  公開済み判断要求・提案・採否等を保存できる。未知利用量は省略し、0 と推定しない。

遅い結果は `RecordRequest` から追記する。終了済み Step や確定済み主結果は
書き換えない。未観測の適用時刻、解除、consume、ゲーム状態の変化を捏造しない。
Trace 自身は既存 completion queue に触らない。

## 観測・Blob・欠損

`observation` は observationId/channel/reason/availability/host/continuity/任意の blob。
host は sourceId/sessionEpoch と、取得できた frame/revision/timestamp/clockId。
UI と World の別取得は別レコードである。両者の同時性を推定しない。
reason は before/input-complete/wait-end/main-result/after-cleanup 等の取得契機。
availability は available/absent/notProvided/omitted/failed/partial/gap/stale/outsideRetention
等を呼び出し側が明記する。failed を空の成功 Snapshot に変換しない。
continuity の既定は unverified。再取得は失われた中間 Change を復元しない。

秘密値処理後の JSON を SHA-256 で参照する。同じ Blob の再観測にも別の
observationId を発行する。bounds/position/frame を baseline の既定正規化で
削除しない。baseline 結果は別添付である。Viewer の前後差分は因果を意味しない。

## T-03 Observe 実購読の保存（OPEN-03/04、#125）

Observeの公開・取得不能・寿命・標準fieldとの分離は
[Observe v1](observe-v1.md)、接続所有権は[Observe transport v1](observe-transport-v1.md)に従う。
Traceは検知境界を追加せず、getterの未公開の中間代入を推測しない。

`.NET GuaTraceObservations.Subscribe(trace, stepId, context)` はTrace専用の独立購読を作る。
local GuaContextと実WebSocket clientに対応する。Gua.Runtimeには公開factory overloadで
`SubscribeObservations(profile)` と `GetObserveSnapshotTransportJson(profile)` を渡せる。
localはTraceと同じprofileを選び、remote/factoryは応答profileの一致を保存前に検証する。
hostの認可は既存経路の責務であり、Traceのprofileで昇格しない。

- 初期の原子的Snapshot+cursorを `before` 等の取得契機で保存する。
  保存内容は `{entries, catalogs}`、公開時点はObservation Recordの
  `metadata.publication`へ分ける。entryの取得不能status/errorも保存し、空Snapshotにしない。
- `Poll(stepId, reason)` は受信した各Changeを `observation.change` に追記する。
  `channel=observe`、`intervalId`、収集時点のreason、`continuity=continuous`、
  `received`（元のkind/boundary/Owner/登録/Value/status/error/host参照）、`catalogs`を持つ。
  native uint64のID/epoch/frame/revision/sequenceだけを十進文字列にし、共通Valueの整数は数値を維持する。
  added/changed/removed/unavailable/recoveredを区別し、存在しないValueは省略する。
- source/epoch/sequenceの連続性を受信区間全体で確認する。`continuous`はこの購読の
  受信済み公開Changeの連続性で、内部状態・標準tree・操作との因果は保証しない。
- gap/stale_session/通信・取得失敗はblobなしのgap/stale/failed観測と品質issueへ残す。
  以降のPollも確認済みの欠損理由を再購読まで維持し、stale/failedをgapへ読み替えない。
  取得を再試行して成功した空履歴に読み替えない。保存上限も`observe-storage-gap`で示す。
  成功した主結果でも、これらの品質issueがあればOnFailureで破棄しない。
  汎用`Observe`でもpartial/gap/stale/failed/outsideRetentionを品質issueへ記録し、
  正常不在のabsentとは分けて、成功した主結果のTraceを保持する。
- `Snapshot(stepId, reason)` は最新公開値の新しい読取時点。cursorを進めず、連続性はunverified。
  古い購読epochとは別epoch、またはcursorより古いsequenceのSnapshotはstale。`Resubscribe`は新しいintervalIdと
  Snapshot+cursorから始めるが、過去の欠損は残る。background pollingや自動再購読は行わない。
- `Dispose`は専用購読だけ解放する。他のObserve購読やaction completion queueに触らない。

`GuaTraceCapture.Ui/World`は標準treeをprofileに従って別々に取得し、各epoch/frame/revisionと
内容を分離する。`Tree`の公開getter overloadは呼出側が認可済みprofileを使う。
UI/World/ObserveのsourceIdは取得元ごとに明記し、同時取得とは称さない。
`World`のcontext overloadはprofileを選択できるローカル`GuaContext`のみ取得する。
remote/custom contextはfailedとし、hostの認可済みprofileを確認した呼出側が
`Tree`のgetter overloadを使う。Traceのprofile名だけでremote profileを保証しない。
位置・bounds・公開fieldをbaseline用に除外しない。失敗はfailed、指定epochとの不一致はstale。
TreeはUI Tree v2／World Object Tree v1のschemaで子要素を含めて
検証してからavailableにする。metadataだけの応答や不正な要素はfailedでblobを作らず、
正しいenvelopeの空配列は正常な空Treeとして保存する。
getter例外本文は保存しない。検索partial/truncatedは汎用`Observe(..., "partial", ...)`で
結果そのものとともに明記し、正常不在のabsentと混同しない。

呼出側はbefore/input-complete/wait-end/main-result/after-cleanupの各実取得時点で
PollとSnapshotを呼ぶ。主結果決定時とcleanup後は別Observation Recordであり上書きしない。
schemaVersion 1の追加event dataとして旧readerも汎用JSON表示できる。
`observation.change`の`change`と`received.kind`は一致が必須であり、`catalogs`は
Observe transportと共通のsingle-enum catalog契約で検証する。ChangeではenumTypeを持つ
before/after Valueに対応するcatalogが必須であり、非enum・欠けたValueのcatalogとvalue catalogは不可。
catalogの単一定義はValueと同じenumTypeを持ち、scalarまたはcollectionの全memberを含むことが必須。
標準JSON Schemaは異なるinstance pathの値を比較できないため、schemaは構造制約を検証し、
続けて配布同梱の`trace-observe-semantics.mjs`の`validateTraceObserveSemantics(record)`を適用する。
別言語のvalidatorも同じ意味制約を実装する。schemaだけの成功はこの対応の検証完了を意味しない。
受信ChangeのsessionEpoch・ownerId・registrationIdは正の十進文字列で、
sequence・revision・UI/World frame等のカウンターは0を許す。
十進uint64はUInt64.MaxValue以下に限り、外側hostもsourceId・epoch・revisionの型を検証する。
NuGetの`trace/`にはTraceと参照先Observe transport・Observe・enum catalog・Value schemaを
同梱する。offline validatorでは同梱schemaをそれぞれの`$id`で登録して使う。
既存Observe APIの引数・binary signatureは保持する。Reader/Viewerはgap/stale/failed/partial/outsideRetentionを
記録品質として示し、Viewerの前後比較は同一channel/source/epochの読取に限る。

## T-04 外部Runnerの受け入れ（#126 / AT-TRACE-001）

公開APIを重複して追加せず、既存の`GuaTraceSession`を使用する。
`examples/dotnet-trace`はGua.Testingだけを参照するconsole writerで、test framework・
Playtest・実行中のゲームを必要としない。共通ValueのためGua.Coreのnativeライブラリは必要。
`-p:TracePackageVersion=<version>`でProjectReferenceをNuGetのPackageReferenceへ切り替え、
配布されたwriter/reader/Viewerを使う。同じサンプルをCIの4 RIDで実行する。

| 外部Runnerの責務 | 公開API / 保存形式 | 受け入れ証拠 |
| --- | --- | --- |
| Stepとmark | BeginStep/EndStep/Mark | console writer、local/実WebSocket fixture |
| 既存要求への相関 | Correlate/RecordRequest/Watch/UseStep | native enqueue/consume/completionを1 Action Stepへ集約、同一キーのBeginStepも同じID |
| 評価と公開観測参照 | Evaluate/Observe、GuaTraceObservations | truthとcallerOutcomeを別保存。failure-condition=trueと主結果failedは両立 |
| 未知注釈とschema添付 | Annotate/Attach | external-playtest.future.*を汎用JSONとして保持。未知usageは省略 |
| 主結果とcleanup | SetPrimaryOutcome、独立Lifecycle Step | Passed+cleanup Failed、Failed+cleanup Passed、Unknown/Interruptedを保持 |
| 記録品質と保存失敗 | Status/FlushAsync/CompleteAsync/DisposeAsync | filesystem故障時にfalseとwrite-failed、元例外のidentity/stackと成功戻り値を維持 |

外部Runnerの通常/異常入力は`protocol/fixtures/trace-external-runner.json`で共有し、
`TraceExternalRunnerTests`でlocal/実WebSocket × Recent/Streamingを検証する。
このfixtureはRunner側の判断でありTraceの独自採点ではない。未知namespace/schemaは
理解して実行しない。公開AI要求・観測ID・提案・採否・短い理由は同じ汎用添付で表せる。
競合理由も`primaryCause`/`additionalCauses`等の公開JSONとして渡し、優先順位はRunnerが決める。

主結果を固定してからcleanupを記録し、必要な観測/遅い結果を収集してからfinalizeする。
`CompleteAsync(cleanupOutcome)`を呼んでも既に固定した主結果は変わらない。
元例外を維持するには`catch { ...; throw; }`または`finally`で記録する。
保存失敗のfalseを主結果のFailedへ変換しない。Statusの記録issueを別に報告する。
明示JSONも秘密objectにmask/sensitiveを指定し、既知秘密をSecretsへ登録する。
非公開の値・例外本文を無印で渡して自動検出を期待しない。redaction後の内容が
保持・hash・添付に入り、未知利用量を0で埋めない。

schemaVersion 1、既存binary signature、GuaからPlaytestを参照しない依存方向を維持する。
net10.0/netstandard2.1の公開writerは同一契約で、native-backed Valueとschema validatorは
必要なGua依存。NUnit/xUnit/MSTestやRunner固有型は必須依存にしない。

## 保存と上限（OPEN-10）

`GuaTraceOptions` の公開既定値:

| 設定 | 既定値 |
| --- | --- |
| CaptureMode / SavePolicy | Recent / OnFailure |
| MaxSteps | 100（native phase を含まないユーザー Step 数） |
| MaxMemoryBytes | 16 MiB（保持・待機中の serialized payload byte budget） |
| MaxQueueItems | 256 |
| MaxArtifactBytes | 256 MiB（詳細。manifest 用 64 KiB を別枠で確保） |
| MaxAttachmentBytes | 4 MiB（Snapshot を含む） |
| MaxEventBytes | 256 KiB（Event data） |
| FlushTimeout | 5 秒 |
| Step 当たり request correlation | 64 |

Recent は直近 Step とその Blob を保持し、evictedSteps に通常の範囲外を示す。
同じ Blob の複数 Step からの参照は各 Step に保持される。byte budget は
安全側に重複を数える。明示 Flush は現在の窓を保存し、古い窓を置き換える。
Streaming は順次 background writer に委譲し、全 Event を artifact 上限まで保存する。
MaxSteps は Streaming のメモリ内相関窓にも適用し、窓外の要求には欠損を報告する。
queued/in-flight payload を含めた byte budget を超えて保持しない。CLR のオブジェクト
管理領域、呼び出し側が所有する元データ、一時 serialization、明示 checkpoint の
コピーは byte 数そのものではなく有限の追加メモリを使う。

上限到達は detailStopped と issue に残し、詳細収集を停止する。最終主結果と
品質サマリーは別枠から書く。保存失敗の生の例外文字列・絶対パスは記録しない。
OnFailure で破棄するのは Passed かつ記録異常のないもののみ。中断・Unknown・
必須記録失敗は残す。Streaming の OnFailure は実行中に一時 artifact を作る。
最終化時に未終了 Step があれば `unfinished-steps`、終了結果が Unknown/Interrupted の
Step は `uncertain-step-outcome` を品質に残し、主結果が Passed でも保存する。
未終了数は保持窓外の Step も含む有限サイズのカウンターで管理する。
これらは主結果や Step の終了結果を変更せず、未観測の終了 Event も補わない。
Dispose は結果未確定なら Interrupted で閉じる。強制終了時の Recent のメモリ、
未flushデータ、I/Oが応答しない場合の保存は保証しない。

## 秘密値・公開範囲・読取

公開 profile は呼び出し側が既存 context で認可したものを渡す。Trace の profile
ラベルは Debug データを Player に変換する機能ではない。非公開データを渡さない。
明示 sensitive=true/mask=true の object 全体、または API の sensitive 引数を
マスクしてから session buffer/queue/hash に入れる。追加の既知秘密文字列は
Secrets に登録できる。heuristic による完全検出は約束しない。
UI/World Tree helperはredaction後にもchannel schemaを検証し、構造語の置換で
無効になったTreeをfailedとして扱う。schemaを維持するlabel等のマスクはavailableを維持する。
Observe transportはdocumentの全要素と対応catalogをprotocol schemaで検証してから保持する。
typed Observe payloadに既知秘密文字列の置換が必要な場合は、その観測区間全体をfailedとして
記録し、Value・catalog・中間Changeを保持しない。enum memberや構造語の置換でschemaを壊さず、
既存の欠損区間を再購読で復元済みとして扱わない。unsubscribe失敗時は当該世代の接続を閉じる。
sourceFile は basename のみにする。名前・任意 JSON・例外添付にも同じ redaction を適用する。

Diagnostics helper は supplied context の公開 diagnostics のみ読む。Screenshot は
独立した pixel policy が必要なので取り込まない。自動で Debug に昇格しない。
既存 diagnostics の専用保存処理とは別であり、Trace 外の既存 artifact を再マスクする
機能ではない。

reader は hash 名の JSON Blob のみ読み、`..`/URL/絶対パス/ファイルシステムリンクを
拒否する。writer/reader のディレクトリは呼び出し側が専有する。敵対的プロセスによる
同時ファイル置換への race-free sandbox ではない。
Viewer は文字列を React の text として表示し、Trace 内コード・外部 URL を実行/取得しない。
静的 HTML は CSP で外部通信を遮断する。

## 現在の実装境界と親 Issue の未完了条件

### T-02 native lifecycle と client Step（#124）

native core の Debug `get_diagnostics` / C ABI diagnostics JSON は
`traceLifecycle` を追加する。[journal schema](../schema/trace-lifecycle.schema.json)
に従い、runtime context 固有の sourceId、単調 sequence、最大256件の独立した
非破壊履歴を返す。UI と Game Input の requestId 空間は domain で分け、Input は
ownerId も source に含める。同じ runtime を別接続から読む場合は同じ要求として
関連付ける。Player diagnostics はこの Debug 履歴を返さない。

UI の enqueue/consume/completion/cancelled、Semantic/Raw Input の enqueue、
consume、completion、hold-pending、hold-started、lease-expired、release-requested、
release-confirmed、owner-disconnected を実際の処理地点で収集する。
completion はホスト結果であって期待状態の成立ではない。hold-pending は消費時の
保持準備で、成功完了の hold-started と区別する。lease-expired は解除を要求する
契機であり、release-confirmed は解除要求に対するホスト成功報告だけで記録する。
reset/owner-disconnect/lease-expired の cleanup 要求は trigger を持つ。
ホストが失敗を報告した解除には release-confirmed を付けない。

sessionEpoch は要求受付時の epoch。hostSessionEpoch/frame/revision は記録地点の
実測値で、部分 reset によって要求と host の epoch が異なる場合も保つ。
hostElapsedMilliseconds と hostClockId は context 生存期間の独立した単調時計で、
diagnostics history reset や仮想時計とは連動しない。collector 時計との同期、実際の
入力適用時刻、ゲーム状態への因果は保証しない。

履歴は256件かつ1件の詳細は8 KiBまで。大きな詳細は omitted=size-limit にする。
入力値とtext payloadはnative履歴に保持しない。sensitive targetも保持前に伏せる。
履歴の作成にはファイルI/O、callback、既存結果queueのpoll、入力/clock操作がない。
coreの既存diagnostics C ABIとruntime/bridgeの既存diagnostics転送を利用するので、
外部ABIのstruct sizeやcompletion所有権を変更しない。

`.NET trace.Watch(context)` はraw contextの操作前に呼ぶ。20ms間隔のbackground
reader、または返されたwatcherの `Capture()` で履歴を読む。収集開始より前の履歴は
baselineとして読み飛ばす。最大64 context/sourceを保持する。native sequenceの
重複はsession内で除き、別接続からの同じnative要求を二重操作として数えない。
履歴欠損は native-lifecycle-gap、保持窓外の遅いphaseは
native-request-outside-correlation-window、取得失敗/未提供は
native-capture-failed/native-lifecycle-not-provided として品質に残す。
native履歴未対応のcontext（GDScript-only addon等）ではclient側の記録は継続するが、
consume/host完了の自動収集を保証しない。

GuaActionCompletion/Locator操作はopt-in TraceからWatchへ自動接続する。Selector、
今回の解決ID、送信、受付、結果受信をclientの同じAction Stepに保存する。
`using (trace.UseStep(explicitStepId))` は明示Stepと自動記録を関連付ける。
raw入力の明示Stepは `watcher.Request(observedEpoch, requestId, inputOwnerId)` を
BeginStepへ渡して同じnative要求に結び付ける。epochを推定して渡してはいけない。
`trace.Assert(label, assertion)` は検証処理をAssertion Stepに記録し、失敗は元例外を
rethrowする。詳細なexpected/actualは既存Evaluateで明示する。

Timeout/中断後もWatchは完了まで独立して読む。clientがcompletion未受信で
Unknown/Interruptedとして終了したStepへのホスト結果はlate-completionで追記する。
正常に結果を受信済みのStepは、collectorが後から読んでもcompletionのままにする。
Step終了や主結果を書き換えない。TraceのCompleteAsyncは
Watchを停止・最後に取得してからfinalizeする。必要な遅い結果/cleanupはその前に
待つ。終了後の結果や未対応hostのphaseを捏造しない。contextを先にdisposeした場合は
取得失敗を品質に残す。remote取得はcontext自身の有限request timeoutに従う。
Watch停止と保存は共通のFlushTimeout予算を使う。応答しない同期readは強制中断せず、
native-stop-timeoutを残してreaderの追記を停止する。期限後の取得結果は記録へ混入しない。

この変更は T-01 の保存基盤、T-04 の汎用記録口、T-05 の共通 offline Viewer を提供する。
`.NET GuaActionCompletion` は opt-in 自動記録に接続済み。取得できない epoch は
unconfirmed として扱い、completion が返した epoch でのみ確定相関する。

子作業の実装と保証境界は以下の通り。親 #109 の統合受け入れは
[trace-integrated-acceptance](trace-integrated-acceptance.md) に条件・assertion・
対象commit・実行結果を対応付ける。汎用APIだけの証拠と実接続の証拠を区別する。

| 子 Issue | この変更 | 残る受け入れ条件 |
| --- | --- | --- |
| #123 T-01 | schema、writer/reader、有限上限、4 保存組合せ、部分末尾、redaction、容量/中断の故障 fixture | 強制終了時の未flush/メモリのみの保存は保証対象外 |
| #124 T-02 | Selector/解決ID、明示/自動Step、native UI/Input/cleanup lifecycle、遅い結果、非破壊履歴 | native履歴未提供hostのphaseは未提供と表示。ゲーム画面でのGodot/Unity E2Eは別途 |
| #125 T-03 | Snapshot/観測の分離、Observeの実native/実WebSocket購読、Value/カタログ、中間Change、欠損、独立UI/World読取 | ゲーム内部の未公開変化、失われた履歴の復元は保証対象外。各Runnerが取得契機を明示する |
| #126 T-04 | 外部 Runner API、共通 Value、未知注釈/添付、評価/主結果/cleanup分離、native/Observe横断fixture、framework-free配布consumer | 任意の外部Runnerの採点・非公開情報の自動判別・ゲーム画面のE2Eは保証対象外 |
| #127 T-05 | 共通 React、静的 HTML、timeline/状態/JSON/区間差分、caller-authorized PNGとbounds overlay、全端点・欠損表示、版固定資産/schema配布 | 同時pixel/snapshot取得とpixel秘密判別はcaller責務。ゲーム操作・Replay・外部添付fetchは対象外 |
| #128 T-06 | 明示Lint report、明示baseline comparison、既存diagnostics session、Recordingの実API接続とnative/Observe統合fixture | 実ゲーム/利用側RunnerのE2E、pixel秘密判別は保証対象外。実行証拠はtrace-validation.md |

## T-06 既存機能の明示接続（#128）

`GuaTraceCapture.Lint(trace, step, report)` は明示実行済みの
`GuaSemanticLinter.Analyze` のreportを `gua.semantic-lint.v1` 添付にする。
実行や採点は行わず、reportのprofileとTraceのprofileが違えば保存しない。

`SemanticSnapshotOptions.Trace/TraceStepId/TraceProfile` は呼出側が実行した
`CompareSnapshot/ExpectSnapshot` の結果を `gua.semantic-comparison.v1` にする。
既存getterと保存baselineのPlayer投影を証明できないため、この自動添付はDebug限定。
`TraceProfile = Player`でもPlayer TraceへDebugのactual/expectedを添付しない。
添付拒否は明示比較/baseline更新の既存動作を変えず、品質へ残す。
matched/baselineUpdated/reason/runId、rules適用済みexpected/actual/differencesを保存する。
比較artifactやbaselineの絶対pathは含めず、任意ファイルを読み込まない。
Traceがなくても既存の結果・例外・artifact・明示更新の契約は同じである。
Traceを指定しても `UpdateBaselines` / 明示環境変数を変更しない。
runtime metadata/geometryの比較除外は比較添付だけに適用し、Tree/Observeからは削除しない。
TraceProfileは既に認可された取得元のprofileで、DebugからPlayerへの変換機能ではない。
context/World getterの認可、profile/build別のBaselineVariantとmask rulesは呼出側の責務。

`GuaRecordingTrace.Attach` は `GuaRecordingFile.Validate` 済みRecordingを
`gua.trace.recording.v1` 添付にし、元のRecording Save/Load形式を変更しない。
`trace-recording.schema.json`のschemaVersion=1/recording envelopeを使う。recording内は
redaction済みのopaque JSONで、敏感Stepの`{redacted:true}`も含む。元Recording parserで読み込まない。
Trace側のwhole-object redactionによりsensitive stepは伏せられる。
`gua.recording.references.v1` は元stepのindex、十進文字列requestId/eventIdと安全なsecretKey参照を
別に保持する。source/epochは関連Trace Stepの実測要求相関を使い、RecordingのrequestIdだけから
別接続/epochへ推定相関しない。Traceの伏せられたRecording添付はReplay可能と称さない。
secretKey自体に秘密を使わず、必要ならSecretsへ登録する。拒否/Timeout/中断は通常の
Trace Eventへ残し、Recordingの成功stepを捏造しない。

`GuaDiagnosticOptions.Trace/TraceStepId/TraceProfile` を指定すると既存の
`GuaDiagnosticsSession` / Writerはcontext解放前に公開diagnosticsを同じStepへ保存する。
未profile選択の既存getterを使う自動添付はDebug限定。Playerは下記の認可済みgetter
overloadを使う。ラベルだけでDebug payloadをPlayerとして保存しない。
logs/pendingRequests/environment等を保ち、Screenshotは常に取り除く。
元payloadを全`diagnostics.schema.json`（version/UI/logs等の参照を含む）で先にvalidateする。
既存`gua.diagnostics.v1`は互換性を保つTrace projectionで、`trace-diagnostics.schema.json`を契約にする。
Screenshot省略とnested redactionを許し、元runtime diagnostics形式とは区別する。
version、呼出側environment/callerMetadataは `gua.environment.v1` の別添付にする。
このlegacy自動コピーもDebug限定。Playerへ既存environment/callerMetadataをラベルだけで移さず、
明示的に認可済みdataを渡す`GuaTraceCapture.Environment` APIを使う。
`gua.environment.v1`共通fieldはcamelCaseのversion/environment（自動Debugコピーには任意callerMetadata）。
両APIは同じTrace serializerを使う。既存version.jsonのPascalCaseは変更しない。
既存diagnosticsのディスクファイルや例外本文をTraceへimportせず、それらの既存policyを
再マスクする機能ではない。取得/保存/表示故障はTrace品質として扱い、元例外を置き換えない。
live読取後のJSON必須property欠損、file生成、supplement/sink故障もdiagnostics-failed品質と
capture.failureへ記録する。故障の例外本文やpathはTraceへコピーしない。
`GuaTraceCapture.Diagnostics(trace, step, IGuaContext)` はprofile未選択のためDebug限定。
Player/remoteは認可済みgetterとprofileを明示するoverloadを使う。

`JsonAttachment` はprofile不一致をgetter実行前に拒否する。失敗はfalseとcapture.failure、
attachment-failed/attachment-unavailable/profile-mismatch等の品質issueを残し、
大きなpayloadのlimit停止後も小さなterminal failure eventは通常の予算内で記録を試みる。
queue/artifactが満杯なら追加eventを保証せず、最大64品質issue内の
`capture-failure:<retained stepId>:<channel>:<reason>`へ相関を残す。容量上限を緩めない。
主結果PassedでもOnFailureで破棄しない。JSON redactionは保持/hash前、Screenshotのpixel認可は
別APIのまま。汎用添付schemaや未知recordはViewerが安全なtextとして表示する。

Gua は Gua Playtest に依存しない。Observe 本体、InputAction metadata、Replay 時間制御は
この変更では実装しない。未提供の依存 API を仮実装して完了扱いにはしない。
