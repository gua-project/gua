# Timed Segment v1 — OPEN-07

Timed Segment は既存 Recording/Replay の明示的・後方互換な拡張である。
旧ファイル、`PreserveDelays` / `PreferConditions`、MCP / Inspector の通常 Replay は
従来の逐次 completion 待ちを維持する。それらは CLOCK-003 の実行経路ではない。
新 API は有限の入力列を区間開始 + offset で送信し、結果待ちは送信列から分離する。
条件 wait、分岐、単一操作から保持したまま返る動作は区間内に入れない。

## 既存経路の調査と変換

| 経路 | 記録の時刻 | Replay | Timed Segment への扱い |
| --- | --- | --- | --- |
| .NET GuaRecorder | 単調時計、target 解決前の caller sample | completion → 次の差分 delay | 送信予定の近似として caller が明示変換。適用時刻にしない |
| MCP GuaAutomationManager | Date.now、一部 caller 指定、一部 completion 後 | completion → 次の差分 delay / condition | 元時刻の意味は legacy-unknown。自動変換しない |
| Inspector InspectorRecorder | Date.now、completion 後 | completion → 次の差分 delay / condition | legacy-unknown。自動変換しない |
| diagnostics import | enqueued と observed の elapsedMilliseconds。旧版は sequence | 既存 .NET Replay | 混在 / synthetic。適用の時間証拠には使わない |

元 Recording の時刻を丸め直したり応答遅延を推定して引いたりしない。変換は
game-input-only Recording v2 の元 offset と配列順を保存し、`legacy-unknown` provenance を付ける。UI、wait や座標
fallback は拒否する。秘密参照だけを保持し、実値は実行時 resolver から取得する。
Recording の schemaVersion 1/2 は変更せず、区間は独立した version 1 DTO とする。
既存 v1 UI と v2 混在 Recording は逐次 Replay を使う。独立 queue を跨ぐ順序を
証明できないため timed 変換を拒否する。省略 / 0 の hold lease は既存経路の実効 default
5000ms として明示化し、勝手に延長しない。wheelUnit 省略は既存 bridge と同じ pixels。
semantic text Set は stateless であり、`semanticValueType: 4` を宣言し host が現在の
descriptor と照合する。秘密参照の変換で型を取得できない場合は caller が明示 resolver を
渡す。axis/vector Set の Holdable=false は拒否理由にしない。

## 時計・順序・能力

既定は realtime の単調時計。simulation は host が制御対象を明記して提供する場合
だけ受け入れる。GuaClock があるだけでは許可しない。現行 local runtime host は
realtime の game-input FIFO のみ対応する。UI と game-input は独立 queue のため、
混在順序を仮定してはいけない。MCP / Inspector の既存 completion-only callback も
能力証拠にならない。simulation、strict application time、same-tick atomic application
を要求した未対応経路は実行前に拒否し、暗黙の realtime 降格をしない。

local host は新しい `gua_runtime_enqueue_game_input_guarded_v2` を必須とする。
承認時の sessionEpoch / profile ごとの Action Map revision を native context lock 内で
enqueue と consume の両方に照合し、変化した要求は失敗 completion として返し適用しない。
旧 enqueue API の互換挙動は変更しない。cleanup は従来の owner-scoped path で送信し、
世代変更時にも自 owner の解除を試みる。対応 entry point が無ければ owner 作成前に拒否する。
runtime の Player ceiling を host preflight と guarded consume で照合する。固定 raw input は
target だけでなく text string / gamepad axis [-1,1] の payload を Load / Validate で検証し、
秘密 text の解決値も owner 作成前に検証する。

同 offset は配列順に送信する。host はこの順に consume/apply する契約が必要である。
send 時刻は client の呼出し境界、resultReceived は client の poll 成功時刻であり、
hostApplied は独立した証拠がない限り null。completion を適用時刻に読み替えない。
現在の Unity / Godot input pump は FIFO consume と同期 apply 後 completion。
将来の非同期 host は consume 順だけで apply 順を広告してはならない。

## 有限予算・lease・境界

maxLateness、区間の real-time execution timeout、cleanup timeout は有限・明示必須。
caller は区間の全操作数と execution + cleanup の実時間予算を先に予約する。
同じ host は区間を同時実行しない。再生ごとに新 owner を作り、途中から再開・自動再送
しない。再実行は新 owner で先頭から caller が明示的に要求する。

保持は区間内の明示 release / reset で閉じる。lease は consume から進む unscaled
host time の安全期限であり、保持の正常終了に使わない。各 hold の lease は全区間の
実時間 execution timeout + maxLateness より長くする（上限 60000ms）。simulation
も同じ実時間上限を使う。勝手な延長・再入力をしない。開始 completion が遅くても
release offset は変更しない。

送信境界で lateness を再確認し、超過時は残りを一括送信せず止める。失敗・cancel・
timeout も送信済み / 未送信・未確認 completion を保持して owner cleanup を試みる。
cleanup は caller cancellation と独立した実時間期限。全送信済み completion の確認、
最後の owner-scoped release-all の host completion、owner state の空を全て確認した時
だけ neutral confirmed とする。未完了要求が残る場合は neutral 未確認。owner dispose
による後続 cleanup を要求しても、適用確認と称さない。他 owner を解除しない。

host の preflight と引数 marshal 後、native enqueue の直前にも caller の send guard を
実行する。guard が投げた時は送信しない。送信を試みた後に request ID を取得できなかった
要求は未解決として保持し、中立を確認したとしない。Load は省略 timing field をゼロへ
補完せず、schema の必須 field と未知 / 重複 property を検査する。

## 証拠・Trace

結果は予定 offset、send、resultReceived、hostApplied（未取得 null）、request ID、
操作結果、時間違反、部分実行、cleanup 結果を分ける。実行された要求の値と秘密を
結果へコピーしない。Trace には caller が結果を明示添付し、欠損時刻を推定しない。
Trace から自動 Replay しない。失敗結果から自動修復・teleport は行わない。

`GuaTimedSegmentResult.Clock` の offset / hostApplied と、realtime の send /
resultReceived は別時計である。simulation scope、maxLateness と両実時間予算も
結果へ保存する。`ApplicationTimingConfirmed` が false の送信時間成功は、適用時刻の
検証成功を意味しない。runtime host は lease-expired の owner journal と欠損も照合する。
lease の早期失効、journal の取りこぼし、epoch 変更は正常再現としない。
Trace 添付の request ID は十進文字列で、未取得の時刻 / ID は null を明示する。
添付前に schema を検証する。cancel / timeout は後から取得した timing evidence で
上書きせず、適用時間は未確認として記録する。simulation scope は host teardown 前に保存する。
添付 schema は `timed-segment-result-v1.schema.json`。入力値を持たない独立 envelope で、
旧 Recording ファイルや Trace の自動再生入力としては読まない。
