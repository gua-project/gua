# Spatial-r1 parent acceptance evidence (#130)

This supplements the [route/support matrix](spatial-transport-r1.md), not a
declaration that #130 or the public distribution gate #129 is complete. Child
#131–#134 merges are implementation milestones; parent acceptance remains open.
Built-in privileged browser spatial routes remain **Unsupported**. #116 actual
Codex routing is investigated separately. No release, tag or deployment is part
of this verification work.

## Contract → violation → assertion → execution

Baseline: main `095d10133a45fce25017d4e65d5fd878aff1a3e0` (PR #165). New checks
below run against the proposed branch; final commit and CI links belong in the
PR/checklist record. Local environment: Windows x64, MSVC 19.51, .NET 10, Bun
1.4.0. CI pins Bun 1.3.14 and exercises all four native desktop RIDs.

| Required behavior / source | Concrete violation | Test and decisive assertion | Execution and scope |
| --- | --- | --- | --- |
| #130 added condition: verify three fixture queries independently on bridge and MCP | Empty/missing/duplicate items, wrong batch/request/query IDs, failed outer or inner completion, duplicate/missing MCP reply | `scripts/spatial-route-assertions.ts`, imported by `verify-spatial-route.ts`; `packages/mcp/test/spatial.test.ts` real-engine assertion test: exact count before iteration, outer/inner correlation and completed status, expected hit/detected/blocked, exactly one MCP response | Bun regression uses deliberately invalid replies and the actual acceptance helper. It does **not** rerun a real engine or published package. Existing real engine evidence at PR #161 stays evidence for that head only. |
| Host contract: one ordered terminal item per request, completed result correlated to its query | Correlated outer item with queued state, mismatched inner ID, missing failure reason, failure carrying geometry | `packages/mcp/test/spatial.test.ts` ten invalid-reply cases on actual WebSocket client and MCP CLI: exact safe error and closed connection; `GuaBridgeClient.spatialBatch` rejects invalid terminal envelope | Real loopback WebSocket with synthetic replies, actual TypeScript implementation and MCP subprocess. Native host remains authoritative for physics/schema semantics. |
| #130: stalls, deadlines, cancellation and mid-flight disconnect must not replay accepted batches | Lost acceptance/poll reply, endless null polls, wrong wire ID, disconnect during acceptance or polling | Same Bun tests: fault reached, expected command/stage-specific error, one submission with original batch, unique wire IDs, one connection, peer owner count zero | TypeScript 150 ms deadline / AbortSignal; MCP real default 5 s deadline / `notifications/cancelled`. Another client's negative tests are not used as substitutes. |
| Host ownership: close invalidates actual native owner while consumed work has a discard completion path | TS/MCP error leaves host owner/lease alive, retry is leased on reconnect, new owner consumes old result | `SpatialTransportTests.TypeScriptAndBuiltMcpInvalidateActualNativeOwner`, `scripts/verify-spatial-owner.ts`: native Begin/Take confirms dispatch before fault; actual TS/built MCP crosses a fault proxy; one submission/connection; late Complete acknowledged, Take null, End; capacity-one fresh owner Describe succeeds, old batch Poll stale, no remaining Begin | Eight local native-host cases: both routes × cancellation/deadline/disconnect/correlation. Synthetic physics provider, real native scheduler/runtime/WebSocket; CI runs these on four desktop RIDs. No real-engine fault or hard real-time claim. |
| Partial batch → transport → Trace → reader/report must retain failure, IDs, absent geometry and truncation separately from storage | Partial batch relabeled all successful, dropped failed/notExecuted items, synthesized geometry, truncation confused with omitted attachment | `SpatialTransportTests.PartialBoundaryResultSurvivesWebSocketTraceReaderAndReport`: exact three IDs/query IDs/states; consumed second failed and untouched third notExecuted with boundary_ended; both lack result; reader receipts and blob preserve facts; one Lifecycle/no Action; failed manifest; offline HTML contains failure states; four combinations of truncated true/false × stored/omitted | Real native scheduler → native WebSocket → .NET transport → shared Trace capture/reader/report. This is **not** MCP failure-path evidence. Bun independently checks partial replies through TypeScript and MCP. Offline HTML payload assertions do not claim browser-render QA. |
| Storage refusal must not become query truncation or successful storage | Attachment/memory/artifact/queue limit reached but receipt claims saved success | Existing `SpatialAttachmentSeparatesStorageOmissionAndPreservesCallerFailure` and `StorageCapacityFailureKeepsTruthfulCorrelatedOmission`: selected capacity issue actually fired, blobs absent, truthful receipt/capture failure, original failed outcome retained | Reused native/.NET Trace regressions; the new partial tests reuse the same capture path. |
| Provider/owner authorization and compatibility | Revoke/unregister/rebind leaks geometry or stale capability; legacy paths regress | Reused `ExplicitTestingOnlyAndRevocationPreventsPublication`, `ProviderRemovalMakesCapabilityDisappearAndRedactsRetainedGeometry`, `RebindingCannotRestorePreviousConnectionOrEnlargeItsGrants`, `OwnerlessConnectionNeverAdvertisesSpatialSupport` and existing host/native/schema tests | These retain their original native/.NET scope. They do not prove identifiable-secret nonleakage through every TS/MCP/Trace path after reset/scene replacement. |

## Violation-detection evidence

`spatial.test.ts` exercises the exact real-engine assertion helper with good
and invalid replies, including empty/missing/duplicate and correlation/status
faults. Each invalid case must throw the intended acceptance error.

In a disposable worktree at `095d101`, copying the new tests but retaining the
old `GuaBridgeClient` makes the selected inner-ID and nonterminal tests fail at
the expected rejection assertion: the invalid reply resolves successfully.
The modified client rejects both. This demonstrates a consumer validation gap,
not a claim that the native host or an engine emitted those replies. Deliberate
invalid fixtures and baseline copies are not shipped product changes.

The initial independent audit found that native-owner assertions ran after
forced driver teardown, which could mask a missing production close. The driver
now requires upstream closure before manual client close, MCP kill or proxy
teardown; MCP peer checks likewise require owner closure while MCP is alive.
A disposable mutation removing `spatialBatch`'s failure-path `socket.close()`
now fails the MCP peer check with `MCP did not close its failed spatial owner`,
and both TypeScript/native and built-MCP/native cancellation cases fail with
`Production spatial failure did not close native owner`. The selected fault
was triggered after native dispatch; watchdog/environment failures are not used
as detection evidence. Logs: `artifacts/no-close-mutation-peer.log` and
`artifacts/no-close-mutation-native.log`. The submitted close remains intact.

Focused commands (from repository root, dependencies installed and native env
directories set):

```text
bun run check
bun test packages/mcp/test
bun run --filter gui-mcp build
bun run --filter @gua/inspector build:trace
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug --target gua gua-runtime
dotnet test bindings/dotnet/tests/Gua.Selector.Tests/Gua.Selector.Tests.csproj --no-restore --filter FullyQualifiedName~SpatialTransportTests
```

Initial local results: all workspace type checks passed; 60 MCP tests passed
(26 spatial cases); 34 native-hosted spatial transport tests passed. GitHub
review then identified acceptance of arbitrary nonempty failure reasons. The
client now uses the protocol-generated safe reason enum; two additional actual
TypeScript/MCP reply cases require rejection without forwarding backend text.
These focused unsafe-reason tests and the acceptance-helper test pass. A second
supported GitHub finding concerned unknown diagnostic fields in terminal items.
The client and route verifier reject unknown terminal item and batch fields;
four TS/MCP extra-field regressions reject identifiable diagnostic text. A third
supported finding exposed missing/extra fields inside completed geometry.
The final client and route helper validate the entire batch-result document,
including nested results/sample/hits, against the protocol-generated JSON
Schema using Ajv (already locked for schema tests, now a native MCP runtime
dependency). Four additional TS/MCP regressions reject absent completed facts,
extra result/sample fields and invalid outcomes. Query count and outer/inner
correlation remain separate checks. Physics, authorization, native semantic
validation and the cross-path confidentiality gate remain host responsibilities.
The route helper's positive cases reuse valid contract ray/overlap/sweep result
fixtures rather than inventing geometry payloads. Local
logs are `artifacts/typescript-check.log`, `artifacts/spatial-mcp-tests.log`,
`artifacts/spatial-native-tests.log`, `artifacts/pre-fix-detection.log`.
The portable native CI lane now builds the MCP CLI and shared Trace viewer
before running the integration tests. Unrun or skipped routes are not passes.

## Remaining parent gates

- #129 public distribution/published-artifact acceptance remains open. Local
  builds, pack smokes and existing PR #161 unpacked consumers do not replace it.
- The existing support matrix's actual Linux/macOS engine/backend/patch spatial
  routes remain unverified; native synthetic-provider CI is separate evidence.
- Complete identifiable-fixture nonleakage across TypeScript/MCP diagnostics and
  Trace after privilege loss/reset/scene replacement is still a parent condition.
  Existing native redaction tests are useful but not a blanket waiver.
- Real-engine executions of the strengthened route verifier and published
  consumer verification on the final head remain distinct acceptance work.

The parent must stay open while these required conditions remain unmet. Updating
the focused batch-verification checkbox does not mark the whole integration,
route support, or distribution checklist complete.
