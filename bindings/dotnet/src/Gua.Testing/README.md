# Gua.Testing

## Locator の遅延解決と auto-wait

`GuaAssertions.Query(context)` の locator は、操作時に最新の strict single match を
解決する。`Get()` と既存 `GuaNodeExpectation` の即時解決・操作の意味は変わらない。

```csharp
var play = GuaAssertions.Query(context).ByRole("button", "Play");
var completion = await play.ClickAsync(
    timeout: TimeSpan.FromSeconds(5),
    pollInterval: TimeSpan.FromMilliseconds(10),
    cancellationToken: cancellationToken);
// completion は同じ request ID に対する host の結果。画面遷移は別途待つ。
await GuaAssertions.Query(context).ById("game-screen").ResolveAsync();
```

`ResolveAsync` は出現を待ち、`WaitForActionableAsync(GuaActionType.Click)` は
可視・有効・指定 action の公開も待つ。どちらも enqueue せず、解決済みの
`GuaNodeExpectation` を返す。その後も遅延解決したい場合は元の locator を使う。
`ClickAsync` / `FocusAsync` / `SetValueAsync` / `SetCheckedAsync` / `SelectAsync` /
`ScrollAsync` / `PressKeyAsync` は actionability を待ち、enqueue 直前に再解決し、
同じ request ID の observed completion を返す。`Async` のない同名 API は同期
wrapper であり、completion まで待つ。

- 0 件・hidden・disabled・action 未公開は再検索する。複数一致は直ちに失敗する。
- query 前後の snapshot の epoch/revision と node 内容を照合し、変更があれば
  再検索する。全 selector 条件と actionability に同じ観測を使う。
  value regex の構文は候補の有無に関係なく待機前に検証する。
- resolve/actionability → enqueue → completion が一つの timeout 予算を共有する。
  既定値は `GuaActionCompletion.DefaultTimeout` / `DefaultPollInterval`。
  timeout は非負、poll interval は正である必要があり、ゼロ予算では操作しない。
- 同期 transport 呼び出し自体は中断できない。復帰後に残り予算・キャンセルを確認する。
- enqueue の拒否や host failure を自動 retry しない。送信後の timeout/cancel は
  副作用がなかったことを保証しない。完了後の UI/World 状態は明示的に待つ。
- action の失敗は `GuaActionException.Kind` と `RequestId` で識別できる。
  送信前は request ID が 0。診断には selector、phase、最終状態と frame/revision を
  含め、selector の name/text/value および action payload は平文で出力しない。
- local、WebSocket、Godot、Unity は同じ `IGuaContext` helper を使用する。
  strict query を提供しない旧 context に対して、先頭一致への fallback は行わない。

## Virtual clock

Contexts implementing `IGuaClockContext` can use
`GuaClockControls.InstallClock`, `PauseClock`, `RunClockFor`, and `ResumeClock`
with matching async methods. The clock advances explicitly connected GuaClock
work; it is not a wall-clock sleep or an engine-global pause.
For a local `GuaContext`, the controls use `context.Clock`; `RunClockFor`
returns only after its queued steps, schedules, and Tick notifications are
drained. Remote contexts retain their adapter-correlated completion behavior.

`Gua.Testing` adds locator, assertion, wait, and test-host helpers on top of
`Gua.Core`.

The package targets both `net10.0` and `netstandard2.1`. The latter is intended
for Unity 6's .NET Standard 2.1 API Compatibility Level; the initial verified
native configuration is the Windows x64 Editor described in the
[Unity smoke guide](https://github.com/link1345/gua/tree/main/examples/unity-smoke).

```csharp
using var ui = new GuaContext();
var host = new GuaTestHost(ui);
var loading = false;

host.Frame("title", frame =>
{
    if (frame.Button("start", "Start Game", new GuaBounds(0, 0, 200, 40)))
    {
        loading = true;
    }
});

GuaAssertions.GetByRole(ui, "button", "Start Game").Click();

host.Frame("title", frame =>
{
    if (frame.Button("start", "Start Game", new GuaBounds(0, 0, 200, 40)))
    {
        loading = true;
    }
});

host.Frame("loading", frame =>
{
    if (loading)
    {
        frame.Text("loading", "Loading...", new GuaBounds(0, 48, 200, 24));
    }
});

GuaAssertions.WaitForText(ui, "Loading...").ToBeVisible();
```

`Click()` enqueues a click request. A real adapter or `GuaTestHost` must consume
that request and emit the click event while advancing frames.

For real .NET tests, use NUnit, xUnit, or MSTest as the test runner and use
`Gua.Testing` inside each test. The repository's recommended sample is NUnit:

```csharp
using var _ = GuaAssertionScope.UseNUnit(Assert.Fail);

GuaAssertions.GetById(ui, "start").ToBeVisible();
```

See `examples/dotnet-nunit` for a complete NUnit project with multiple `[Test]`
methods in one file.

Async condition waits are the primary synchronization API. They use a monotonic
timeout, honor cancellation, and work with both local `GuaContext` and remote
`GuaRemoteContext` snapshot polling:

```csharp
await GuaAssertions.WaitForVisibleAsync(ui, "status", cancellationToken: token);
await GuaAssertions.WaitForTextAsync(ui, "status", "Ready", pollInterval: TimeSpan.FromMilliseconds(20));
await GuaAssertions.WaitForValueAsync(ui, "progress", "100");
await GuaAssertions.WaitForStableSnapshotAsync(ui, stableFrames: 3);
```

Stable snapshot waiting counts only distinct `frameSequence` values whose
`revision` remains unchanged; repeatedly polling one stopped frame never
satisfies the wait. Hidden waiting succeeds for either `visible=false` or a
removed node. Timeout messages include the condition, last node state, frame,
and revision. Sync wrappers remain available for compatibility.

`WaitForStateAsync(context, id, predicate)` polls fresh snapshots for detailed state such as caret/selection,
scroll offsets, range bounds, and selected index. Action completion includes session/frame/revision metadata,
but remains distinct from observing the requested state; chain a state wait when the UI result matters.

Use `GuaTestSession` as the explicit process-reuse boundary. The default reset
clears semantic nodes, requests, events, and retained history while preserving
logs and screenshots. Strict teardown detects leaked requests/events without
discarding them:

```csharp
var session = new GuaTestSession(context);
session.Reset(); // setup; starts a new session epoch
// ...test...
session.Reset(new GuaResetOptions(Strict: true)); // teardown; throws if dirty
```

For high-level isolation, construct `GuaTestSession` with lifecycle options.
`GuaTestSessionOptions.Strict` enables strict startup and teardown reset.
Policies can also be selected independently with `GuaResetPolicy.Disabled`,
`NonStrict`, or `Strict`; their default targets are nodes, requests, events,
retained history, and clock state. Logs and screenshots remain preserved unless
selected. The published `GuaResetTargets.Default` (15) and `All` (63) values are
retained for binary compatibility; new default behavior uses `SessionDefault`.
`AllWithClock` is also retained as the legacy 127 mask; use `AllCurrent` when a
full reset must include World Object Tree state.

```csharp
session.Reset(new GuaResetOptions(GuaResetTargets.AllCurrent));
```

```csharp
using var session = new GuaTestSession(context, new GuaTestSessionOptions
{
    StartupReset = GuaResetPolicy.Strict,
    TeardownReset = GuaResetPolicy.Strict,
    CaptureDiagnosticsBeforeTeardown = true,
    CleanupAfterLeakReport = true,
    DiagnosticsSession = diagnostics,
});

session.Run(() => RunTestBody());
```

`Run` rethrows the original test-body exception with its type and stack trace.
A teardown failure is attached as `GuaTeardownFailure`, and a typed diagnostic
result as `GuaDiagnosticsResult`. Leak inspection reports epoch, counts,
request ID, action/event type, and node ID without action payload values.
Cleanup runs only after diagnostics were attempted and only when enabled.
Clean completion creates no artifact, and `Dispose` is idempotent.

`ResetAsync` provides the same contract for remote contexts and honors
`CancellationToken`. Remote reset always includes the inspected session epoch,
so a stale client cannot reset a newer shared runtime session.
Semantic locators are strict: `GetBy*` fails when zero or multiple nodes match,
while `QueryAll()` is the explicit multi-result API. String matching is exact by
default and can opt into ordinal contains or ECMAScript regex matching:

```csharp
var save = GuaAssertions.Query(ui)
    .ByRole("button")
    .ByText("^保存", GuaMatchMode.Regex)
    .Within("settings-panel")
    .WhereVisible()
    .WhereEnabled()
    .Get();

GuaAssertions.Query(ui).ByRole("listitem").Within("servers").AssertCount(3);
```

`Within(parentId)` searches descendants and excludes the parent itself. Pass
`directChild: true` to limit the query to immediate children. Local and Godot
remote contexts send the same selector to the native evaluator.

Node expectations expose `Focus`, `SetValue`, `SetChecked`, `Select`, `Scroll`,
and `PressKey`. These methods enqueue requests and return a request ID;
`WaitForAction` waits for the adapter's correlated observed result rather than
treating enqueue acceptance as completion.

## Failure diagnostics

Configure `GuaAssertionOptions.Diagnostics` to capture a unique artifact
directory automatically when a semantic assertion or wait fails:

```csharp
using var scope = GuaAssertionScope.Use(new GuaAssertionOptions
{
    Diagnostics = new GuaDiagnosticOptions
    {
        TestName = TestContext.CurrentContext.Test.FullName,
        OutputDirectory = Path.Combine("artifacts", "gua"),
    },
});
```

For one framework-independent failure path across assertions and completed
actions, create a `GuaDiagnosticsSession` and assign it to
`GuaAssertionOptions.DiagnosticsSession`. `Capture` preserves the primary
exception and returns absolute artifact paths, media types, and secondary
capture errors. Directories use the sanitized test name, timestamp, and a
unique ID so parallel tests do not collide. Caller metadata, runtime version,
and optional text/screenshot providers are evaluated only after a failure;
successful tests produce no artifact unless the caller explicitly captures.

`GuaDiagnosticOptions.AttachmentSink` is framework-neutral. NUnit consumers
can pass `file => TestContext.AddTestAttachment(file.Path, file.MediaType)`;
other frameworks can use the same typed callback without adding a framework
dependency to `Gua.Testing`. Screenshot files can contain rendered secrets and
remain the consumer repository's storage and upload responsibility.

The directory contains the final UI tree, bounded operation/event history,
pending requests, logs, environment metadata, and an optional PNG. A wait also
writes its initial tree and a deterministic node-id diff. Sensitive action
values are redacted before the writer receives them. If capture fails, the
original assertion delegate still determines the exception type and a secondary
capture error is appended to its message.

Protocol v2 operations have one-step sync and async completion APIs for focus,
set value, set checked, select, scroll, and key press. They return the correlated
`GuaActionEvent`; `GuaActionException` exposes rejection, host failure, timeout,
or cancellation together with request/action/node/error and snapshot metadata.
`GuaAssertions.PressKeyAsync(context, key)` targets the adapter's current focus.

Queries can add `Within`, `ByValue`, `WhereFocused`, `WhereSelected`,
`WhereChecked`, and `ByAction`. Corresponding async state waits re-fetch the
latest UI tree on every poll instead of holding the first snapshot.
Wait-returned expectations retain the exact successful node snapshot, including
`sessionEpoch`, `frameSequence`, and `revision`, so chained assertions evaluate
one completed frame. Call `Refresh()` or a `WaitUntil*` method to opt into a
newer published frame. A retained snapshot from an older session epoch remains
readable evidence but must be refreshed before making current-session decisions.

Locator counts can wait on every selector dimension, including scope, state,
value, and action:

```csharp
await GuaAssertions.Query(context).ByRole("listitem").Within("ServerList")
    .WaitForCountAsync(count => count >= 3, timeout, pollInterval, cancellationToken);
GuaAssertions.Query(context).ByAction("scroll").WaitForCount(1, timeout, pollInterval);
```

Node expectations expose correlated sync/async action completion for `click`,
`focus`, `set_value`, `set_checked`, `select`, `scroll`, and `press_key`. These
helpers wait for the same `requestId`; unrelated events remain queued.

## Gua Trace（opt-in）

`GuaTraceSession` は test framework に依存しない記録口。既定は直近 100 step と
非成功時保存。成功探索は `SavePolicy = GuaTraceSavePolicy.Always` で保存する。

```csharp
await using var trace = new GuaTraceSession(new() {
    OutputDirectory = "artifacts/traces",
    SavePolicy = GuaTraceSavePolicy.Always,
});
using var scope = GuaAssertionScope.Use(new() { Trace = trace });
try {
    await GuaAssertions.GetById(context, "play").ClickAsync();
    await trace.CompleteAsync(GuaTraceOutcome.Passed);
} catch {
    await trace.CompleteAsync(GuaTraceOutcome.Failed);
    throw;
}
var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, "artifacts/report.html");
// report.Succeeded と trace.Status は主結果とは別に呼び出し側で判定する。
```

`BeginStep`/`EndStep`/`Mark`/`Correlate`/`RecordRequest`/`Observe`/`Change`/
`Evaluate`/`Annotate`/`Attach`/`FlushAsync` は外部 Runner から使用できる。

Observeの実購読を保存する場合は独立した`GuaTraceObservations`を使う。
購読はTraceのprofileと一致する応答だけを保存する。各取得契機は呼出側が指定する:

```csharp
var step = trace.BeginStep(GuaTraceStepKind.Action, "transition");
using var observation = GuaTraceObservations.Subscribe(trace, step, context);
// 操作・wait等を実行した実際の境界で呼ぶ。
observation.Poll(step, "input-complete");
observation.Snapshot(step, "wait-end");
observation.Snapshot(step, "main-result");
// cleanup後は別の観測として追記する。
observation.Poll(step, "after-cleanup");
observation.Snapshot(step, "after-cleanup");
trace.EndStep(step, GuaTraceOutcome.Passed);
```

`context`はGuaContextまたはGuaWebSocketContext。Gua.Runtimeには購読とSnapshot取得の
factory overloadを使う。`Poll`がfalseなら欠損・失敗・保存上限を確認し、必要時に
`Resubscribe(step)`で新しいSnapshot+cursorを取得する。過去の欠損は消えない。
getter失敗とnot_sampledはentry/Changeのstatus/errorに残り、ダミーnullを生成しない。
標準UI/Worldは`GuaTraceCapture.Ui/World`（remote等は`Tree`の認可済みgetter）で別取得し、
sourceIdとreasonを明記する。部分検索は`Observe`へ`availability="partial"`で保存する。

`GuaTraceCapture.Diagnostics` は既存 context の公開 diagnostics を JSON 添付にする。
Screenshot は別の pixel mask 方針が必要なので自動収集しない。
主結果は `SetPrimaryOutcome` で固定でき、以後の cleanup/遅い結果は追記する。
汎用 API の文字列や JSON は公開可能な内容を渡し、秘密 object には
`sensitive: true`/`mask: true`、または API の sensitive 引数を明示する。

NuGet 配布には版固定の Viewer を同梱するため、利用者側に Web ビルドは不要。
ソースからの開発・pack 時には先に以下を実行する:

```powershell
bun install --frozen-lockfile
bun run --filter @gua/inspector build:trace
dotnet test bindings/dotnet/tests/Gua.Visual.Tests/Gua.Visual.Tests.csproj
# native Value を使う外部 writer の実例（GUA_NATIVE_DIR をビルド先へ設定）
dotnet run --project examples/dotnet-trace/Gua.TraceExample.csproj -- artifacts/trace-example
```

完全な契約、上限と親 #109 の未完了条件は
[Trace v1](../../../../protocol/specs/trace-v1.md) を参照。

外部writerのObserve Changeは、配布`trace/trace.schema.json`の構造検証後に同梱の
`trace/trace-observe-semantics.mjs`から`validateTraceObserveSemantics(record)`を呼ぶ。
enumType一致と全memberのcatalog所属は標準JSON Schemaのinstance間比較では表現できない
意味制約であり、別言語validatorも同じ照合を行う。両段階の成功が必要である。

native lifecycleを記録する場合は、raw操作前に `var lifecycle = trace.Watch(context);`
を呼ぶ。UI completion/Locator helperでは自動接続される。WatchはDebug diagnosticsの
独立した有限履歴だけを読み、既存completion queueを消費しない。Timeout後も遅い結果を
記録するため、cleanup/遅い結果の待機後にTraceをfinalizeする。

明示Stepと自動操作を同じStepにする場合:

```csharp
var step = trace.BeginStep(GuaTraceStepKind.Action, "メニューを開く");
using (trace.UseStep(step))
    await GuaAssertions.Query(context).ByRole("button", "Menu").ClickAsync();
trace.Assert("メニュー表示", () => GuaAssertions.GetById(context, "menu").ToBeVisible());
```

native側のホスト成功とAssertionの期待状態成立は別の事実である。未対応contextでは
native-lifecycle-not-provided、履歴切り詰めではnative-lifecycle-gapを品質に残す。
source/epoch/domain/owner/request単位で相関し、source locationなしのraw操作も記録する。
