# #109 integrated acceptance — 2026-10-02

Baseline: `ca9400e8` (PR #168 included). Tested acceptance changes:
`b5ef0fd6e1ec29ca5d0dd3e8bf711694bd655fd7`. The latter changes tests and browser
verification only; it does not alter Trace runtime behavior. Later documentation
commits do not imply another runtime test run. All counts below exclude skips.

Windows x64, MSVC 19.51.36260, .NET SDK 10.0.401, Bun 1.4.0,
Chrome 154.0.8037.59. Native DLLs were built in this isolated worktree.
`GUA_NATIVE_DIR=build/windows-msvc-debug/native/gua-core/Debug` and
`GUA_RUNTIME_NATIVE_DIR=build/windows-msvc-debug/native/gua-runtime/Debug`
must both be absolute paths. Godot and ImGui sample builds were disabled for
this native fixture run; no engine game-screen E2E is claimed.

## Parent acceptance mapping

Test symbols below identify exact assertions in the linked files, not merely
suite names. A broken contract in the third column must fail the named test.

| Parent acceptance condition | Test / assertion | Violation detected and evidence |
| --- | --- | --- |
| Default success discarded; always success retained; all capture/save combinations | [TraceTests](../../bindings/dotnet/tests/Gua.Visual.Tests/TraceTests.cs) `SavingPoliciesCoverPassedFailedAndInterrupted`; [TraceStorageTests](../../bindings/dotnet/tests/Gua.Visual.Tests/TraceStorageTests.cs) `MoreThanOneHundredStepsPreserveBlobsInEverySavingCombination`; [TraceIntegrationTests](../../bindings/dotnet/tests/Gua.Visual.Tests/TraceIntegrationTests.cs) `ActualLintComparisonRecordingDiagnosticsAndObserveShareTheirStep` | Artifact absent only for clean Passed/OnFailure; manifest primary equals caller result otherwise. Integrated 2 capture × 2 policy × 3 outcome = 12 cases; retention additionally includes Unknown. Visual 373/373. |
| 100 steps, byte/queue limits, incomplete tail, unfinalized, saving failure visible | Storage `IndividualPayloadLimitStopsDetailsAndKeepsSummary`, `PendingWriterIsBoundedByQueueItemsAndBytes`, `StreamingReaderDetectsMissingCompleteRecordEvenWhenLastSequenceMatches`; Trace `InterruptedAndIncompleteTailExposeOnlyCompleteRecords`, `ArtifactByteLimitIncludesActualLineEndings`, `SavingFailureIsSecondaryAndDoesNotLeakFilesystemErrors` | Count 100 recent/105 streaming with 5 evictions and retained blobs; actual locked-writer queue saturation and byte/event/attachment limits; missing line → sequence-gap; partial JSON → incomplete-tail; checkpoint → unfinalized; file/directory collision → false/write-failed/no manifest and reader throws. Visual 373/373. |
| request/source/epoch, client/native and automatic/explicit never mixed or counted twice | Trace `CorrelationDistinguishesSourcesEpochsAndLateResultsWithoutRewritingPrimary`; [TraceLifecycleTests](../../bindings/dotnet/tests/Gua.Selector.Tests/TraceLifecycleTests.cs) `ParallelContextsSameRequestIdAndEpochAreNotMerged`, `ParallelConnectionsObserveOneNativeOperationAndLeaveItsCompletionAvailable`, `ExplicitAndAutomaticLocatorRecordingShareOneStepAndKeepSelectorAndRuntimeId`, `ConcurrentReadersKeepNativePhasesOrderedAndCorrelated` | Exact distinct step counts, source/epoch/request keys for every phase, one action for two real WebSocket readers, exact enqueue/consume/completion order over 64 rendezvous iterations. Request-ID-only mutant fails expected 3 distinct steps, actual 1. Selector 422/422. |
| enqueue/completion, hold/release, Timeout/late result distinct | Lifecycle `EnqueueDoesNotClaimHostCompletionAndOverflowIsVisible`, `InputHoldExpiryAndDisconnectRequireObservedReleaseAndLeaveResultsAvailable`, `TimeoutRetainsLateCompletionAndDoesNotChangePrimaryOrConsumeOtherRequests` | Native overflow actually occurs; no completion for pending enqueue. Lease-expired present but release-confirmed absent until host completion. TimedOut error, late phase, primary failed, exactly one original step end, both completion queues still pollable. Viewer checks each distinct phase. |
| Blob versus read time, position, intermediate phase, missing/absent/stale values distinct | [TraceObserveTests](../../bindings/dotnet/tests/Gua.Selector.Tests/TraceObserveTests.cs) `IdenticalContentsAcrossPublicationsDeduplicateButKeepReadTimesAndFrames`, `RealLocalAndRemotePreserveIntermediateEnumValuesAndIndependentCursor`, `GapResubscribeAndResetRemainMissingAfterFreshSnapshot`, `GetterFailureRemovalAndReregistrationNeverFabricateNull`, `ObjectRemovalAndSameRuntimeIdRecreationKeepDistinctLifetimes`; Integration `ActualLintComparisonRecordingDiagnosticsAndObserveShareTheirStep` | One sanitized blob/multiple observation IDs and host frames; actual local/native WebSocket First→Second→Third changes, independent cursor; gap stays missing after new snapshot, reset stays stale; getter status/error, removal/re-registration IDs and absent values instead of dummy null. UI x=0 and x=80 retained while comparison matched and baseline bytes unchanged. |
| External Runner without framework/Playtest dependency stores annotations/evaluation/attachments | [TraceExternalRunnerTests](../../bindings/dotnet/tests/Gua.Selector.Tests/TraceExternalRunnerTests.cs) `ExternalRunnerContractAcrossNativeLifecycleAndObserve`; [console writer](../../examples/dotnet-trace/Program.cs) | Local/real WebSocket × capture modes, unknown namespace/schema retained, truth independent of caller outcome, observations referenced, primary versus cleanup preserved. Public API remains framework independent. Extracted-DLL console writer/reader/report also passed without ProjectReference. |
| Lint/comparison/diagnostics/Recording referenced from related step | Integration `ActualLintComparisonRecordingDiagnosticsAndObserveShareTheirStep`; `MalformedDiagnosticsNeverAdvertisesValidProjection`; [attachment schema verifier](../../packages/value/scripts/verify-trace-attachment-schemas.ts) | Required schema set nonempty and every attachment step equals action; real diagnostics pending count >0/logs present, Recording reference envelope; original Recording schema rejects redacted envelope. Generated projections pass independent AJV. No automatic lint or baseline update. |
| Player boundary, secrets, hostile HTML/path/URL | Integration `ProfileMismatchDoesNotReadPrivateDiagnosticsAndFailureIsRetained`, legacy Player rejection cases; Storage `SensitivePayloadsAreRedactedBeforeRetentionQueueAndHash`; Trace `ReaderRejectsTraversalAndTamperedBlobAndFutureVersion`; [Inspector reader tests](../../packages/inspector/test/trace.test.tsx) `traversal and external URLs are unavailable, never links`, `unknown annotations and hostile HTML remain inert text` | Mismatched getter read count zero; private marker absent. Inspect retained buffer/queue bytes and sanitized hash, file contents and exception text. Tampered hash → mismatch, ../ and external URLs → unavailable, no href/executable HTML. Both browser routes show unknown attachments as inert text; network/exception monitors see deliberate startup faults before reporting zero actual external reads/exceptions. |
| Completion, clock/input unchanged; original failure survives saving failure | Lifecycle raw-context, timeout and hold cases; Integration `TerminalOutcomeSurvivesCaptureAndFlushFaults`, `CaptureAndReportFailuresPreserveOriginalExceptionIdentityAndStack`, diagnostics-session fault cases; Trace `AutomaticActionRecordingPreservesCompletionAndOriginalException` | Exact unrelated request ID still consumable and correlated results pollable; real host phases require caller input/lease/cleanup actions. Original exception identity and stack preserved; report output collision returns false. Caller owns clock/input; Trace reads independent journal/subscription and performs no replay. |
| Same schema/component in Inspector/static reports; generated assets sufficient | [Viewer browser verifier](../../scripts/verify-trace-viewer-browser.ts), [integration browser verifier](../../scripts/verify-trace-integration-browser.ts); Trace `PackagedViewerCreatesSelfContainedSafeReportWithoutSourceCheckout` | Actual shared React component, same directories/outcomes/truth/attachments, differences/overlay/loading/error cases; 7 Viewer cases and 12 real integration surface cases. Extracted nupkg writer/reader/embedded report and Chrome report passed without source project references; extracted offline schema validator passed. |
| Every child requirement/implementation/test/evidence corresponds | Child table below plus [previous path-specific evidence](trace-validation.md) | All six merges reachable from baseline. Historical evidence is identified by its child merge, rather than silently presented as current-head execution. |

## October 2 additions and deliberate fault detection

`TerminalOutcomeSurvivesCaptureAndFlushFaults` covers Recent/Streaming ×
Passed/Failed/Interrupted × capture/flush. It runs real native Lint capture,
fixes the caller primary before postprocessing, actually throws from the capture
getter or holds the existing storage flush gate, and requires the intended false
return/quality reason. Reader manifest and step.end must retain the caller outcome,
finalization is independent of quality, only the successful attachment exists,
capture.failure has the same step, and secrets/exception identity/stack remain safe.
Flush recovery may write a finalized artifact but still returns false and keeps
flush-timeout. It must not advertise successful recording.

The existing file/directory collision test now crosses both capture modes and all
three primary outcomes. Because the destination cannot be written, it requires no
manifest and an explicit reader error, rather than inventing on-disk evidence.
Original exception identity/stack through actual assertion/storage/report failures
is separately covered by the preexisting lifecycle and integration tests.

Two selected mutations ran only in `gua-109-negative`, detached at `b5ef0fd`:

- Request-ID-only BeginStep lookup: correlation test failed at the intended
  distinct-step assertion (expected 3, actual 1), 1 failed/0 passed/0 skipped.
- Allow replacing an already fixed primary result: terminal matrix failed at the
  manifest primary assertion for all 8 Failed/Interrupted cases (expected failed
  or interrupted, actual passed). The 4 Passed cases passed; no build/environment
  error is counted as detection. This mutation was applied after restoring the
  correlation change, so the faults were independent.

Actual capture exceptions, flush timeout, queue overflow, missing lines, malformed
diagnostics, hash tampering and traversal are asserted in the reused tests; no
blanket mutations or inferred product defects were added. Browser verifiers
require an observed localhost startup request and `MONITOR-FIXTURE-FAILURE`
exception before clearing probe counters. Both recorded probes passed.

## Child implementation and contract evidence

| Child / reachable merge | Requirements and exact tests |
| --- | --- |
| #123 / `c5cf980` (#143); initial #140 `394955e` | TRACE-003/007/008/009: saving/retention/storage/redaction/reader cases above. OPEN-01 version 1 envelope/unknown major rejection and OPEN-10 finite defaults are fixed in [trace-v1](trace-v1.md). Shared blobs and evicted observation/parent references have explicit tests. |
| #124 / `7765970` (#146) | TRACE-002/010: lifecycle exact counts, Selector/resolvedId, real host enqueue/consume/completion, repeat attempts/epoch/source, raw context without source location, hold/lease/disconnect/release, late results and non-destructive completion checks above. No inferred input application time. |
| #125 / `15fbf65` (#145) | TRACE-004/005/006: Observe local/real WebSocket tests above plus `TreePositionFailureAndStaleRemainDistinctWithoutBaselineNormalization`, `ParallelCaptureAndCapacityOverflowRemainBoundedAndKeepReferences`, malformed transport/schema/catalog tests. OPEN-03/04 resolved in Observe/Trace specs; no restoration of unpublished/lost intermediate values. |
| #126 / `bb06911` (#155) | TRACE-001/002/003/006/007/008/009/010: external Runner contract fixture and API; Evaluate/Annotate/Attach/Correlate/Observe/Complete, unknown AI/usage records, frozen primary/cleanup and faults. The console requires no test framework or Playtest dependency. |
| #127 / `61ff94b` (#157) | TRACE-011/009: 7 real Chrome Viewer cases, mechanical reader/component tests, exact outcome fixture cardinality, screenshot decoding/identity/bounds, unknown namespace and hostile content, no eager images/external fetch/Replay. Generated bundle and source-free consumer. |
| #128 / `b83ad39` (#158) | TRACE-005/009/010/011/FIX-006: actual native Lint, baseline comparison, Recording, diagnostics/logs/pending/version/Observe integrations. Expanded current-head outcome matrix, parallel/dispose tests and real capture/flush/report faults. 12 browser surface cases verify actual integrated producers. |

## Execution evidence and reproduction

Ignored artifacts are under `artifacts/acceptance/` in the isolated worktree.
TRX files contain individual case results and zero skip counts. Commands use the
absolute native directories stated above and `PATH` including Bun.

| Command / artifact | Result |
| --- | --- |
| `cmake --preset windows-msvc-debug -DCMAKE_GENERATOR_INSTANCE="C:/Program Files/Microsoft Visual Studio/18/Community,version=18.10.12217.157" -DGUA_BUILD_GODOT=OFF -DGUA_BUILD_IMGUI_EXAMPLE=OFF`; build preset; `ctest --test-dir build/windows-msvc-debug -C Debug --output-on-failure` | 16/16; baseline native code unchanged at b5ef0fd. `build/windows-msvc-debug/Testing/Temporary/LastTest.log`. |
| `dotnet test bindings/dotnet/tests/Gua.Visual.Tests/Gua.Visual.Tests.csproj --logger "trx;LogFileName=visual.trx" --results-directory artifacts/acceptance` | 373/373; `visual.log`, `visual.trx`. Set `GUA_TRACE_INTEGRATION_EVIDENCE` to absolute `artifacts/acceptance/integration` to retain reports and directories. |
| `bun run --filter gui-mcp build`; `dotnet test bindings/dotnet/tests/Gua.Selector.Tests/Gua.Selector.Tests.csproj --no-build --logger "trx;LogFileName=selector-final.trx" --results-directory artifacts/acceptance -- NUnit.NumberOfTestWorkers=1` | 422/422; `selector-final.log/trx`. Real native/HTTP/WebSocket fixtures. |
| `dotnet test bindings/dotnet/tests/Gua.Snapshots.Tests/Gua.Snapshots.Tests.csproj` | 32/32; `snapshot.log/trx`. |
| `bun run check`; `bun test packages/inspector/test packages/value/test` | All workspace checks; 261/261, 1139 assertions. `check-final.log`, `bun-tests.log`. |
| `dotnet run --project examples/dotnet-trace-viewer/Gua.TraceViewerExample.csproj -- artifacts/acceptance/viewer`; `bun scripts/verify-trace-viewer-browser.ts artifacts/acceptance/viewer 9349 http://127.0.0.1:1420` | 7 cases; `viewer/browser-evidence.json` and PNGs. Deliberate startup probes detected; actual external requests/exceptions zero. |
| `bun scripts/verify-trace-integration-browser.ts artifacts/acceptance/integration 9349 http://127.0.0.1:1420` | 6 actual reports × static/Inspector = 12 cases; `integration/browser-evidence.json` and PNGs, actual external requests/exceptions zero. |
| `bun packages/value/scripts/verify-trace-attachment-schemas.ts artifacts/acceptance/integration/schema` | Actual diagnostics/Recording projection valid, original Recording rejects redacted envelope. |
| `dotnet pack bindings/dotnet/src/Gua.Testing/Gua.Testing.csproj -c Release`; extracted package offline schema validator and console consumer | net10.0/netstandard2.1 built; embedded report opened in Chrome. `pack.log`, `consumer.log`, `consumer-browser.log`, `source-free/browser-evidence.json`. Local package only; no publication. |
| Disposable negative worktree, targeted dotnet tests | `negative-correlation.log/trx`, `negative-outcome.log/trx`: intended assertion failures described above. |
| `git diff --check` | Passed. |

Initial execution errors were not counted as acceptance: restricted MSVC/NuGet
access; missing runtime DLL override; missing built MCP prerequisite (7 spatial
cases); incorrect singular Snapshot project path; and the browser verifier's
unnormalized Inspector URL. Corrected commands passed as recorded above.
The Selector run uses one NUnit worker; it does not claim local default-parallel
deadline validation. Remote CI/review of the final PR remains the merge gate.

## Scope and completion

These are native/transport and browser artifact fixtures, not actual Godot/Unity
game-screen E2E. Parent #109 explicitly allows Gua-only fixtures and assigns
Playtest product E2E to its caller; neither a new Playtest product nor a public
release is a dependency of this acceptance. Screenshot pixel acquisition,
authorization/masking, unpublished game state, caller scoring and recovery remain
caller responsibilities. #129/#130 distribution and engine-wide gates are separate;
#116 tooling is not a new prerequisite. No release, tag, deployment, credentials
or security settings are changed. Close #109 only after the final PR audit,
review and CI are clean and the checked parent mapping is reconciled.
