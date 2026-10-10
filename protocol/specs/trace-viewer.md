# Common Trace Viewer and offline report

## 保存したTraceを調べて共有するために

失敗したテストの記録を、ゲームを再起動せずに開く画面です。
Inspectorのoffline panelと単体HTML reportは同じViewerを使います。
Trace directoryを選ぶ、または`GuaTraceReport.WriteHtml`で新しいHTMLを作り、
step・要求・観測・欠損の区別を読みます。記録からゲーム操作やReplayは実行しません。

例えば完了が遅れた入力では、enqueue/consumeとlate-completionを別の事実として表示します。
スクリーンショットは呼び出し側が許可・必要なmaskを済ませたものだけを添付し、
UI boundsとの関連付けが確認できないときは推測したoverlayを作りません。

[GuaTraceReport.WriteHtml](../../bindings/dotnet/src/Gua.Testing/Trace/GuaTraceReport.cs)がHTML生成、
[GuaTraceViewer](../../packages/inspector/src/TraceViewer.tsx)が表示の入口です。
[Trace v1](trace-v1.md)で各事実の意味を読み、下のDeveloper validationで
fixture生成・画面操作・成果物を確認します。この手順にはブラウザーが必要です。
共通準備とtest suiteは[開発者ガイドのローカル確認手順](../../docs/developer-reading-guide.ja.md)を参照してください。
表示にエラーがないことは、元のgameの成功や通知の連続性を保証しません。

Inspector's **Open Gua Trace (offline)** panel and `GuaTraceReport.WriteHtml`
use the same `GuaTraceViewer` component and Trace v1 reader. The viewer never
controls a game, executes Recording, or loads a trace-provided URL/path.

## Consume generated assets

NuGet `Gua.Testing` contains `trace/index.html`, `viewer.js`, `version.json`,
Trace/lifecycle/screenshot schemas, Observe/Value schema dependencies, and the
Observe semantic validator. Open `trace/index.html` directly and select one Trace
directory. No source checkout, Node/Bun installation, or web build is required.
The component version and exact script SHA-256 are in `version.json`.

A .NET consumer can create a single distributable HTML file:

```csharp
var report = GuaTraceReport.WriteHtml(trace.ArtifactPath, "artifacts/report.html");
if (!report.Succeeded) Console.WriteLine(report.Error);
```

The output must be new; an existing artifact is never overwritten. The same
versioned viewer is embedded in the assembly. CSP permits only the pinned script,
inline styles and validated PNG data; external network access remains disabled.

## Read recorded facts

Timeline rows retain separate step IDs even for repeated labels. Filtering by
label, kind, outcome or ID selects a visible row; no match hides old details.
Source location, duration and step result remain unconfirmed when not recorded.
Native phases have their own host clock, separate from the collector clock.
Enqueue, consume, hold-pending, hold-started, release-requested, release-confirmed
and late-completion remain separate records. Pending requests are correlated by
source/epoch/request, never request ID alone. A timeout does not imply release.

Assertion **truth**, caller **execution result**, step result and primary result
are shown separately. A true failure condition can coexist with failed execution;
false goal truth can coexist with passed execution. Cleanup observations have
their own reason/step and never replace the result-decision snapshot or primary
result. UI/World/Observe/Property content and received intermediate changes remain
inspectable as JSON, including position-only changes. Interval differences require
available snapshots from the same channel/source/epoch; they never claim causality
or continuity across a missing notification interval.

Unknown annotations, event/envelope fields and schema-identified attachments are
preserved as escaped JSON. Lint, comparison, logs/diagnostics and Recording are
references/content, not automatically executed or externally fetched. Actual
Lint/comparison producer integrations are implemented in T-06 (#128); current
integrated acceptance is mapped in [trace-integrated-acceptance](trace-integrated-acceptance.md).

Quality warnings distinguish an absent Trace, no retained records, reader errors,
unknown version, unfinalized tail, missing blobs, acquisition failures, stale or
partial observations, notification gaps, truncation and ordinary retention eviction.
The profile label describes captured data; it does not convert Debug into Player.

## Optional screenshot and bounds overlay

Pixels have a separate caller-owned policy. Capture and mask the screenshot using
an already authorized host operation; do not infer permission from semantic-tree
access. The helper accepts only a bounded PNG, validates signature and dimensions,
and makes no host call:

```csharp
// uiObservation is the ID returned by trace.Observe / GuaTraceCapture.Tree.
// authorizedPixels contains dataUri (PNG base64), width and height.
bool saved = GuaTraceCapture.Screenshot(trace, stepId, authorizedPixels,
    uiObservation, pixelsAuthorized: true);
```

`false` authorization or invalid data records `screenshot-unavailable` and retains
no pixels. The declaration is the caller's responsibility; it neither authorizes
a runtime nor detects secrets in pixels. Generic external writers may use
`Attach(stepId, "gua.trace.screenshot.v1", content)` with the
[attachment schema](../schema/trace-screenshot.schema.json). Limits are 4 MiB URI,
8192 per dimension and 16,777,216 pixels, also subject to Trace attachment budgets.
PNG dimensions must match IHDR. SVG, URL images and arbitrary data MIME are rejected.
Decoder failure remains visible and cannot create an overlay.
Select **Show screenshot** to decode pixels. Only one image is active at a time,
so the aggregate active decode budget is 16,777,216 pixels, even when many highly
compressed screenshots share one step. Observation IDs are indexed once per Trace;
duplicate observation IDs remain ambiguous. Event sequence, rather than a possibly
reused event ID, isolates per-screenshot selection and decoder state.

Overlay requires exactly one retained, available UI observation matching the
attachment's observation ID, confirmed source/epoch identity, and complete finite
x/y/w/h bounds. Bounds scale with image dimensions in physical viewport pixels;
epochs must be positive decimal uint64 strings; zero/placeholders are unconfirmed.
partial bounds are not filled with zero. The caller must associate pixels and
snapshot correctly; this reference alone does not establish simultaneous capture.
Unknown or evicted snapshots still permit authorized pixels but no guessed overlay.

## Developer validation

```powershell
bun install --frozen-lockfile
bun run --filter gua-value-tools build
bun run --filter @gua/inspector build:trace
dotnet run --project examples/dotnet-trace-viewer/Gua.TraceViewerExample.csproj -- artifacts/trace-viewer-qa
bun run --filter @gua/inspector dev
# Start an isolated headless Chrome with --remote-debugging-port=9337.
bun scripts/verify-trace-viewer-browser.ts artifacts/trace-viewer-qa 9337 http://127.0.0.1:1420
```

Use a fresh output directory for fixture generation. The deterministic writer
produces actual .NET reports for success, failure and interruption, caller-masked
secret markers, native hold/late/release phases, repeated actions, result/cleanup
observations, intermediate changes, unknown metadata, reference attachments,
missing/stale/partial/gap records and hostile screenshot/HTML/URL records.
The browser script runs both static reports and the Inspector, exercises the
actual directory picker and loading races, checks responsive overlay geometry,
and writes PNG screenshots plus `browser-evidence.json`. These are generated
artifacts and stay out of git.
The verifier requires exactly the success/failure/interruption fixture set and
defaults to the configured Inspector port 1420. Its local-file allowlist uses
normalized file URLs on both Windows and POSIX.
