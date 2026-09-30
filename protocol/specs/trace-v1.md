# Gua Trace v1（#109）

Trace は操作と観測の事実を保存する。Goal、条件の時間的採点、主結果の
優先順位、再送、Replay、入力解除、時計操作は呼び出し側の責務である。
保存失敗によって主結果を変更しない。

## 形式と互換性（OPEN-01）

`protocol/schema/trace.schema.json` を envelope の契約とする。
一つの生成済み `traceId` ディレクトリに `manifest.json`、`events.jsonl`、
`snapshots/<sha256>.json`、`attachments/<sha256>.json` を置く。
Recording は `gua.recording.v1` 等の schema を持つ添付として元形式のまま
保存できる。Trace は Recording の再生可能性を保証しない。

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

親 #109 を閉じるには以下が残る。汎用 attachment/API で表現できることを
依存先との実接続済みと取り違えない。

| 子 Issue | この変更 | 残る受け入れ条件 |
| --- | --- | --- |
| #123 T-01 | schema、writer/reader、有限上限、4 保存組合せ、部分末尾、redaction、容量/中断の故障 fixture | 強制終了時の未flush/メモリのみの保存は保証対象外 |
| #124 T-02 | Selector/解決ID、明示/自動Step、native UI/Input/cleanup lifecycle、遅い結果、非破壊履歴 | native履歴未提供hostのphaseは未提供と表示。ゲーム画面でのGodot/Unity E2Eは別途 |
| #125 T-03 | Snapshot/観測の分離、Change 記録口、位置差分 | #119/#120 の購読連続性/欠損契約と実接続（OPEN-03/04） |
| #126 T-04 | 外部 Runner API、共通 Value、注釈/添付、評価/主結果分離、サンプル | 上記 native/Observe 統合後の横断受け入れ |
| #127 T-05 | 共通 React、静的 HTML、timeline/状態/JSON/区間差分、配布 | Screenshot pixel policy と bounds overlay、全端点の可視化 |
| #128 T-06 | diagnostics 添付、既存形式の汎用添付 | #106/#108 の Lint/comparison 実接続、全機能統合試験 |

Gua は Gua Playtest に依存しない。Observe 本体、InputAction metadata、Replay 時間制御は
この変更では実装しない。未提供の依存 API を仮実装して完了扱いにはしない。
