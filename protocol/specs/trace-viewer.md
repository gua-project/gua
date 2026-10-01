# Common Trace Viewer and offline report

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
Lint/comparison producer integrations remain T-06 (#128).

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

Overlay requires exactly one retained, available UI observation matching the
attachment's observation ID, confirmed source/epoch identity, and complete finite
x/y/w/h bounds. Bounds scale with image dimensions in physical viewport pixels;
partial bounds are not filled with zero. The caller must associate pixels and
snapshot correctly; this reference alone does not establish simultaneous capture.
Unknown or evicted snapshots still permit authorized pixels but no guessed overlay.

## Developer validation

```powershell
bun install --frozen-lockfile
bun run --filter gua-value build
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
