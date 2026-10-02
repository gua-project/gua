# Gua.Testing.Recording

`Gua.Testing.Recording` records semantic Gua actions and replays them through the
normal request-ID-correlated action lifecycle. The package targets both `net10.0`
and `netstandard2.1`. It depends on `Gua.Testing` but has
no PNG codec dependency.

## Record actions

`GuaRecorder` measures monotonic elapsed time, captures the semantic revision
before and after each completed action, and stores only the semantic target and
action arguments. Calls are serialized so one recording has deterministic order.

```csharp
using Gua.Testing.Recording;

var recorder = new GuaRecorder(host.Context);
await recorder.ClickAsync(new(Id: "open-login"));
await recorder.SetValueAsync(new(Id: "email"), "player@example.com",
    waitCondition: GuaWaitConditions.Visible("email"));
await recorder.SetValueAsync(new(Id: "password"), password,
    sensitive: true, secretKey: "login-password");
await recorder.PressKeyAsync("Enter");

GuaRecordingFile.Save("recordings/login.json", recorder.Recording);
```

Sensitive values are never stored. A sensitive `set_value` step contains only a
`secretKey`; replay requires the caller to resolve it.

## Replay reliably

Replay waits for the correlated host completion of every semantic action. It does
not treat queue acceptance as success and it does not consume unrelated events.
By default a recorded semantic wait condition replaces the corresponding delay;
steps without a condition retain their recorded delay.

```csharp
var recording = GuaRecordingFile.Load("recordings/login.json");
var result = await GuaReplayer.ReplayAsync(host.Context, recording, new()
{
    SecretResolver = key => key == "login-password" ? password : null,
    ActionTimeout = TimeSpan.FromSeconds(5),
});
```

Supported conditions are produced by `GuaWaitConditions`: visible, hidden,
enabled, disabled, focused, unfocused, checked, unchecked, text, and value.
Coordinate fallback is disabled by default and requires both explicit permission
and a caller-provided executor.

## Import retained diagnostics

`GuaRecordingFile.ImportDiagnostics` pairs enqueued operations with observed events
by request ID. Current runtimes provide monotonic elapsed milliseconds, per-entry
revision, scroll deltas, checked values, key/modifier data, and scroll units. The
returned metadata reports unpaired operations and whether an older diagnostics
payload forced synthetic sequence-based timing. Use `FromDiagnostics` only when
that import metadata is not needed.

Recording version 1 follows `protocol/schema/recording.schema.json`.

## 有限 Timed Segment（明示的な拡張）

既存 Replay は逐次 completion 待ちであり、`PreserveDelays` は応答待ち時間を
次の delay に加える。厳密な送信予定には `GuaReplayer.ReplayTimedSegmentAsync` を使う。
条件 wait は区間の前後で既存 assertions を使い、区間内には入れない。

```csharp
using Gua.Runtime;

// 組み込み Unity / Godot と同じ同期 FIFO apply pump が動作している local runtime。
var segmentHost = new GuaRuntimeSegmentHost(runtime, adapterAppliesInOrder: true);
var segment = new GuaTimedSegment(1, 300, 50, 1000, 500,
[
    new(0, GuaGameInputKind.Keyboard, GuaGameInputOperation.Down, "KeyW", LeaseMilliseconds: 5000),
    new(0, GuaGameInputKind.Pointer, GuaGameInputOperation.MoveDelta, "delta:", X: 2),
    new(100, GuaGameInputKind.Keyboard, GuaGameInputOperation.Press, "Space"),
    new(300, GuaGameInputKind.Keyboard, GuaGameInputOperation.Up, "KeyW"),
]);
var timed = await GuaReplayer.ReplayTimedSegmentAsync(segmentHost, segment);
// 成功でも HostAppliedMilliseconds は取得不能なら null。適用時間の証明にはしない。
if (!timed.NeutralConfirmed) throw new InvalidOperationException("区間境界の解除が未確認です。");
```

offset は区間開始基準であり、開始応答が遅れても jump / release を遅らせない。
maxLateness 超過時は未送信を止める。結果は全入力（未送信も含む）、時間違反、
部分実行、cleanup の成功と中立確認を保持する。cleanup は caller cancel と独立した
実時間予算で、自 owner だけを扱う。未完了入力が残れば中立は未確認となる。
保持は明示 release で閉じ、lease は実時間 execution 予算 + lateness より長くする。
全操作数と execution + cleanup 時間を先に caller の予算に予約する。再送・lease
延長・途中再開はしない。再生ごとに新 owner を作る。同一 host の同時区間は禁止。

host は現在の capability と Action Map を確認し、保護 action には呼出しごとの
confirmation delegate が必要。変更された map では未送信操作を止める。秘密値は
`Func<string, JsonElement?>` resolver で供給し、結果・ファイルへコピーしない。
local runtime host は realtime / game-input FIFO だけを扱い、simulation / 厳密適用
時刻 / 同 tick 一括適用は拒否する。`IGuaTimedSegmentHost` を実装する host は、
その時計の制御対象と apply 順序を明示し、全メソッドを短時間・non-blocking に保つ。
`Send` は preflight / marshal 後に `verifySendBoundary` を一度だけ呼び、その例外時は
enqueue しない。送信後に ID が取得できない場合は中立未確認として扱う。
停止 simulation でも実時間 execution / cleanup 期限は働く。

`GuaTimedSegmentFile.Save/Load` は独立 v1 plan を保存する。`GuaTimedSegmentImport.FromRecording`
は game-input-only v2 の明示変換で、元 offset / 順序と secretKey を保存するが元時計の
意味は `legacy-unknown`。UI、条件 wait、座標、閉じない hold、不十分な lease は拒否。
MCP / Inspector の通常 Replay はこの能力を広告せず、既存互換を維持する。
詳細契約は [Timed Segment v1](../../../../protocol/specs/timed-segment-v1.md)。

`GuaRecordingTrace.AttachTimedResult(trace, stepId, timed, profile)` は値を含まない
タイミング結果を `gua.timed-segment-result.v1` として明示添付する。Trace は再生しない。
# Trace integration

`GuaRecordingTrace.Attach(trace, stepId, recorder.Recording, profile)` validates and
attaches an explicitly created recording. Save/Load and replay behavior are unchanged.
The attachment uses `gua.trace.recording.v1` (`trace-recording.schema.json`): a
versioned envelope around an opaque redacted `recording` object. It is not a Recording
file and must not be loaded with the Recording parser.
Sensitive steps are masked in Trace; safe secret references and decimal-string request
IDs are kept in a separate `gua.recording.references.v1` attachment. Correlation uses
the related Trace step's confirmed source/epoch, never a guessed Recording epoch.
Trace attachments are evidence, not a guarantee of replayability. Rejected, timed-out,
and interrupted operations remain Trace events without fabricated Recording steps.
