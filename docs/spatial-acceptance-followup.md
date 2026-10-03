# Parent #130 non-publication acceptance follow-up

Baseline: PR #167 / main e7efa05ae2f20a0b25a0f2abcdf6ae316f7385e3.
This map supplements spatial-acceptance-evidence.md. Child completion and a
publication decision do not establish parent integration acceptance.

| Remaining requirement | Violation | Assertion and execution |
| --- | --- | --- |
| Retained geometry is reauthorized before publication after disable, successful runtime reset, or scene unregister | A completed anonymous hit, distance/position, collision reference or World ID survives the transition | SpatialTransportTests.TypeScriptAndBuiltMcpRedactRetainedPrivateGeometry: six actual TS/MCP -> proxy -> native WebSocket cases. A positive native control must contain PRIVATE_SPATIAL_SENTINEL and 0.314159265358979 in exactly two completed results. The proxy holds the real poll until the host completes those results and invalidates them; it forwards native replies unchanged. It checks one original submission, safe not_authorized errors or two correlated failed/provider_unregistered items without geometry. |
| Current capability and diagnostics cannot expose invalidated geometry or stale authority | Old connection still advertises spatial_read_r1, provider metadata or private geometry | Same held native connection receives actual get_version/get_diagnostics/get_spatial_info commands after invalidation. Required responses must arrive; version and diagnostics lack spatial capability, info is rejected, all wire and consumer output lacks both identifiable markers. Fresh explicit binding restores capability with reset session 2 / replacement space 2; old requests are rejected as not_authorized. |
| Denials/redacted results remain safe through Trace, attachment, reader and report | Saved attachment or rendered report payload republishes pre-transition private facts | PrivateGeometryInvalidationSurvivesTransportTraceReaderReport: three separate actual .NET transport cases use the same native positive control and held-poll proxy. Revocation/reset require the safe error, spatial.unconfirmed and no blobs; scene removal requires exactly two correlated failed results and one redacted blob. All saved files and HTML lack both private markers; manifest remains failed. This is payload/HTML validation, not browser visual QA. |
| Final real-engine bridge and built Native MCP execution | Equally incomplete arrays, wrong IDs or invented completion appear successful | verify-spatial-route.ts independently requires exactly three results, outer/inner batch/request/query/session/space/kind correlation, completed state and ray hit/overlap detected/sweep blocked, then schema validation. PR #167 already demonstrates these assertions reject selected violations. verify-godot-spatial-routes.ts runs both GodotPhysics3D and Jolt through the actual callback pump and requires clean engine exit and at least two real physics batches. |
| Candidate package-only consumer on actual engine, each supported desktop RID | Source/project/native-directory fallback hides packaging defects | The Godot runner copies only the consumer project and program outside the checkout, selects GuaUsePackages=true with candidate 0.0.0-ci, uses an isolated cache and CI package feed, clears GUA_NATIVE_DIR/GUA_RUNTIME_NATIVE_DIR, rejects project libraries in assets.json, and executes that built consumer against both actual backends. It generates common Trace/report evidence and requires a third host physics batch. All four desktop CI lanes run it using their assembled candidate addon and final-head NuGet artifact. |

The deterministic Godot fixture staging writes only extension discovery and the
GuaSpatialReader class metadata. It caches no geometry or expected results and
does not claim editor-import acceptance. Every run uses a fresh directory.
The local Godot 4.7 immediate editor-import exit crashed on shutdown even though
its direct runtime geometry fixture passed with exit 0; preserve both outcomes.
The engine runner avoids that unrelated editor invocation and verifies the
actual source-only game, packaged addon and native transport instead.

## Existing evidence reused

- #131 fixtures/schema and spatial result suites establish required fields,
  unknown/partial states, geometric contract and version compatibility.
- #132 host suites cover pending/post-Take policy revocation, unregister,
  deadlines, retained-result redaction and safe publication reasons. Runtime
  reset tests prove remaining Take is stopped. The new transport checks add
  retained identifiable output evidence rather than replacing these suites.
- #133 analytical spatial-engine-r1 fixtures cover 33 scenarios plus the door
  transition, batches, 34 lease races and 360 actual callback profile samples.
  Windows Godot 4.7 (GodotPhysics/Jolt) and Unity 6000.5.3f1/PhysX are pinned.
- #134 common Trace and legacy route tests continue to run. The measured
  support table distinguishes the actual four-RID Godot engine/consumer CI
  routes from full pinned Windows geometry/profiling. Native synthetic-provider
  CI alone does not establish engine routes. Exact heads/artifacts are retained
  in the PR and parent execution record.

## Commands and records

Run the focused PrivateGeometry NUnit cases with the matching built native
libraries; build gui-mcp and the shared Trace Viewer first. Run
`bun scripts/verify-godot-spatial-routes.ts <godot> <unpacked-addon> <output> <candidate-feed>`.
`scripts/run-spatial-engine-fixtures.ps1` remains the full pinned Windows
geometry/profile runner. The Unity standalone fixture is built through
SpatialTransportBuild.Build and verified by verify-spatial-route.ts plus the
same package-only consumer. Capture exact head, command/environment, engine
exit, required result/profile/race counts, assets.json, reports and CI links
in the PR/issue execution record. Do not count aborted runs or prior heads as
final-head success.

Initial focused execution of this follow-up: all six workspace typechecks and
70 MCP tests passed; 43 native spatial transport tests passed, including the
nine new private-geometry cases. Windows GodotPhysics/Jolt TS/MCP and isolated
candidate-package consumers passed. Unity 6000.5.3f1 standalone Mono Player
TS/MCP and the same package-only consumer passed. Logs are
artifacts/followup-typechecks.log, followup-mcp.log, followup-spatial-native.log,
redaction-native.log, godot-package-routes.log and unity-package-routes.log;
route directories retain wire/results, engine exits, assets and common reports.
These runs used the working diff on the stated baseline; final commit/CI
executions are recorded separately in the PR and parent issue.

Selected violation detection: a disposable e7efa05 native checkout removes only
the poll-time provider-unregistered completed-geometry redaction assignment.
Both TS and built-MCP scene tests then fail specifically at
verify-spatial-redaction.ts's not.toContain(PRIVATE_SPATIAL_SENTINEL) assertion;
the actual reply and successful MCP payload contain the retained private hit.
The clean native build passes all nine cases. See
artifacts/redaction-mutation-detection.log. The mutation is not submitted.

The initial bounded audit also reproduced duplicate inspection replies and
stale diagnostics.version.spatial passing the original inspection block.
spatial-redaction-assertions.ts now requires exactly one of each inspection ID,
safe discovery rejection and absent provider metadata in both declarations.
packages/mcp/test/spatial-redaction-assertions.test.ts directly exercises that
production helper with empty/missing/duplicate replies and stale declarations.

GitHub review identified that a bare NuGet candidate version permits newer
published fallback. The runner now uses exact [0.0.0-ci], maps Gua.* exclusively
to the local candidate feed, and requires resolved Gua.Core/Gua.Testing candidate
packages while rejecting any differently versioned Gua package or project
library. spatial-candidate-assertions.test.ts verifies those rejection paths;
final-head candidate builds and engine execution must still pass independently.

Re-review strengthened the private-fixture prerequisite shared by both native
transport and Trace controls: require two uniquely correlated items, completed
item and nested-result states, matching inner request/query/session/space/kind,
and private markers in each result. An actual native positive response is then
modified to missing/duplicate/partial/nested-ID/nested-state variants; the old
marker/count checks accept the selected two-item violations, and the strengthened
control fails its intended count/correlation/completion assertion.

## Gates retained

- #129 requires identified target commit/version artifacts and verified declared
  execution routes, not public publication. Its original package/resource and
  route conditions remain mandatory; candidate packing/loading alone is
  insufficient. See [distribution contract](distribution-contract.md).
- Actual Unity Linux/macOS engine/Mono consumer routes need an available
  player-build/runtime-host pipeline. This Windows editor has only Windows
  standalone and WebGL modules; Linux/macOS build modules and an existing Unity
  engine CI job are absent. A suitable editor build host with applicable license,
  or cross-build modules plus target runtime hosts, could provide it. No license/credential
  changes are authorized. This does not block Godot desktop coverage.
- Other patches/backends, meshes/one-way gameplay and physics error bounds
  remain outside the measured support claims documented in spatial-engine-r1.
- Built-in privileged browser spatial remains Unsupported; #116 Codex routing
  remains a separate investigation. No publication/tag/deployment is performed.

Non-publication acceptance must be reconciled against the final execution
record. Missing mandatory routes remain a parent gate even when documented.
