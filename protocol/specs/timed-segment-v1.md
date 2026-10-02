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
直接作成 / Load した区間の省略 / 0 lease も実効 5000ms で予算と比較する。
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

local host は新しい `gua_runtime_enqueue_game_input_guarded_v2` と
`gua_runtime_validate_game_input_guarded_v2` を必須とする。
owner-lifetime health の `gua_runtime_get_game_input_owner_health` も必須である。
承認時の sessionEpoch / profile ごとの Action Map revision を native context lock 内で
enqueue と consume の両方に照合し、変化した要求は失敗 completion として返し適用しない。
予定された semantic Release も承認時の map revision に束縛する。
旧 enqueue API の互換挙動は変更しない。cleanup は従来の owner-scoped path で送信し、
世代変更時にも自 owner の解除を試みる。対応 entry point が無ければ owner 作成前に拒否する。
runtime の Player ceiling を host preflight と guarded consume で照合する。固定 raw input は
target だけでなく text string / gamepad axis [-1,1] の payload を Load / Validate で検証し、
秘密 text の解決値も owner 作成前に検証する。
semantic target は native と同じ小文字 ASCII の Action ID grammar と127 byte上限を
file / schema で検査する。固定 TextInput と宣言済み semantic Text Set は40 Unicode
code pointまでとし、値契約のない操作は null / 省略以外の payload を保存・実行しない。
target を持たない text input / gamepad reset / owner cleanup は空 target だけ許可する。
Vector2 は重複する decoded property 名を拒否し、native の範囲検査は top-level x/y を
共有 bounded JSON parser で読む。nested key や escaped alias を別の座標として扱わない。

semantic Set の型、descriptor の範囲、bounded ValueSchema、および既存 native payload
上限も、同じ native validator で全操作を開始前に検査する。純粋な validate API は owner、
queue、request ID、result、Trace event を作らない。scheduler は optional
`IGuaTimedSegmentValueHost` に秘密を含む解決済み値を渡し、local host は検証中だけ使用する。
旧 host interface は変更しない。生の `Begin(segment)` は秘密値を取得できないため、秘密を
含む区間の完全な preflight は `ReplayAsync` または解決値付き overload を使用する。

同 offset は配列順に送信する。host はこの順に consume/apply する契約が必要である。
同 offset の送信間には結果 polling を挟まず、待機時は有限件数の round-robin polling を使う。
ready completion を取得できた chunk の後は人工的に sleep せず、deadline を照合して続ける。
結果 polling と境界時計の読取り後にも execution deadline / cancellation を照合する。
send 時刻は client の呼出し境界、resultReceived は client の poll 成功時刻であり、
hostApplied は独立した証拠がない限り null。completion を適用時刻に読み替えない。
現在の Unity / Godot input pump は FIFO consume と同期 apply 後 completion。
将来の非同期 host は consume 順だけで apply 順を広告してはならない。

## 有限予算・lease・境界

maxLateness、区間の real-time execution timeout、cleanup timeout は有限・明示必須。
caller は区間の全操作数と execution + cleanup の実時間予算を先に予約する。
開始前に cancel 済みなら host 能力の読取りや秘密 resolver を呼ぶ前に終了する。
同じ host は区間を同時実行しない。再生ごとに新 owner を作り、途中から再開・自動再送
しない。再実行は新 owner で先頭から caller が明示的に要求する。

保持は区間内の明示 release / reset で閉じる。lease は consume から進む unscaled
host time の安全期限であり、保持の正常終了に使わない。各 hold の lease は全区間の
実時間 execution timeout + maxLateness より長くする（上限 60000ms）。simulation
も同じ実時間上限を使う。勝手な延長・再入力をしない。開始 completion が遅くても
release offset は変更しない。

送信境界で lateness を再確認し、超過時は残りを一括送信せず止める。失敗・cancel・
timeout も送信済み / 未送信・未確認 completion を保持して owner cleanup を試みる。
cleanup は caller cancellation と独立した実時間期限。その予算は最初の release-all
dispatch 前から計測し、poll / neutral 確認にも適用する。全送信済み completion の確認、
最後の owner-scoped release-all の host completion、owner state の空を全て確認した時
だけ neutral confirmed とする。owner state の空を読んだ後に epoch / health を再照合し、
異なる session の解除証拠を混ぜない。未完了要求が残る場合は neutral 未確認。owner dispose
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
検証成功を意味しない。runtime host は native lock 内で owner の存続、sessionEpoch、
owner-lifetime lease expiry flag を取得する。この値なし flag は owner disconnect まで残り、
256 件の diagnostics journal の切詰めと他 owner の流量に依存しない。旧 Trace journal の
schema と保持上限は変えない。lease の早期失効、health 不取得、epoch 変更は正常再現としない。
Trace 添付の request ID は十進文字列で、未取得の時刻 / ID は null を明示する。
添付前に schema を検証する。非 null request ID の重複も拒否し、一つの completion を
複数入力の証拠にしない。cancel / timeout は後から取得した timing evidence で
上書きせず、適用時間は未確認として記録する。simulation scope は host teardown 前に保存する。
`ApplicationTimingConfirmed` は Succeeded と cleanup / neutral 確認、および全入力の
取得済み・順序通り・lateness 内の適用時間を必要とし、Trace 添付でも検証する。host の任意の
health text は証拠へコピーせず、既知の lifecycle code 以外を `host-health-failed` に置換する。
添付 schema も failure code を既知の固定 code に制限する。
Succeeded の添付は適用時刻取得の有無にかかわらず、全 ordinary completion の成功、
cleanup / neutral 確認、null failure code を必要とする。宣言済み semantic Set の
Button / Axis1D / Vector2 / Text の値型は protocol schema と file validator で一致させる。
simulation の証拠は非空 scope を必要とし、終端 outcome と failure code の組合せも検証する。
各 input の index は array の0-based位置と一致し、予定 offset は非減少とする。
Succeeded の send / receipt は実時間 execution deadline より前でなければならない。
Succeeded の既知の apply stamp も確認 flag に関係なく予定 / lateness 内でなければならない。
未取得 apply stamp は引き続き null とし、flag は完全な順序証拠がある場合だけ true とする。
すべての outcome で neutral confirmed は cleanup succeeded を必要とする。
realtime の send は予定 offset より前を許可せず、Succeeded は maxLateness 内とする。
simulation の offset と realtime send は異なる時計のため、この比較を適用しない。
これらの cross-field比較と request ID 重複は標準 JSON Schema だけで表現できないため、
消費者は schema 検査に加えて意味検査を行う。参照実装の AttachTimedResult は両方を実行する。
ID は native uint64 上限内の正の十進文字列。hostApplied / completion status は receipt を、
receipt は send / ID / succeeded / errorCode を必要とする。未取得IDの send と未完了要求は
部分実行の証拠として残せるが、completionを取得したようには記録しない。
添付 schema は `timed-segment-result-v1.schema.json`。入力値を持たない独立 envelope で、
旧 Recording ファイルや Trace の自動再生入力としては読まない。
