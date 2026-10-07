# Guarded dispatch validation evidence

Base main and verified `gua-v1.1.1`: `88f5dca4aa97c5d5187ab66ea4416377f3affc96`.
Implementation/test scope is the additive guarded remote route described in
`protocol/specs/guarded-dispatch-v1.md`. Playtest #7 acceptance is not revised.
Source merge has separate explicit user authorization. Tags and package
publication remain outside the authorized scope.

## Contract mapping

| Required behavior / previous violation | Concrete assertion and path |
| --- | --- |
| Remote UI had no atomic epoch/revision enqueue guard | `GuardedDispatchTests.UiEnqueueGuardsAndConsumeRaceHaveNoSideEffectsAndOwnedOneShotCompletion`: stale epoch/revision/profile reject without request ID or host request; changed UI frame after actual native enqueue produces failed -7 completion, no host consumption |
| Remote Semantic input had no guard | `SemanticStaleMapAndEpochRejectBeforeHostAndRaceReportsNativeFailure`: actual bridge rejects stale metadata before host; changed map after enqueue fails with existing native -1 |
| Authorization must be current at consume | `native/gua-core/tests/guarded_dispatch_tests.cpp`: changed current profile fails UI consume; `native/gua-runtime/tests/guarded_profile_tests.cpp`: Debug input downgraded to Player fails even with Player keyboard capability; managed Raw test verifies capability revocation before consume |
| Owner/result isolation and cleanup | Native owner test forbids other-owner/unscoped polling, proves one-shot result, retains consumed completion after disconnect, rejects request 257, removes only disconnected owner's queue; managed Raw test proves one owner's release-all does not release another owner and its result remains pollable |
| Private/nonexistent parity | Managed Player UI and Semantic input tests assert the same native projected error and no consumed host request; action-map read excludes private metadata |
| Reset freshness and completion identity | Managed UI completion test checks original request ID; stale old guard fails after real reset, old pending poll becomes Uncertain; Poll validates request, epoch, UI node and action |
| Dropped response must never create blind retry | Four actual native fault-proxy cases: UI/input enqueue and completion reply loss. Proxy forwards once and drops only the real response after host consume+complete; dispatches=1, dropped=1, polls=0 or 1, no duplicate host request, terminal Uncertain attempt and poisoned session |
| Old client wire behavior and unsupported capability | Legacy UI enqueue/consume/poll succeeds in managed/native tests; capability-stripped actual host version rejects new managed session before dispatch. Existing remote transport/selector tests remain unchanged |
| Client pre-dispatch guard | Callback throws after marshalling: Rejected/pre-dispatch-rejected, no request ID, no native host request |
| Invalid common metadata | Actual WebSocket tests reject zero/missing epoch, fractional/overflow revision, invalid profile and duplicate keys; nested payload cannot supply guard metadata |
| Malformed completion must not establish completion | Eight actual native proxy tests remove succeeded, error/errorCode, frameSequence, revision, requestId or epoch after real host completion: terminal Uncertain, no completion evidence, session poisoned, faults=1 and polls=1 |
| Pointer/gamepad marshalling | Real native absolute/delta pointer, wheel and indexed axis requests retain X/Y/device/value and correlated completion; delta omits coordinateSpace and absolute defaults to viewport_pixels |
| Schema composition | Existing AJV command consumers register the additive schema; guarded/legacy examples and missing/unauthorized/lifecycle negatives validate without broadening legacy verbs |
| Required guarded UI payloads | `GuardedUiRequiresTypedVerbPayloadBeforeEnqueue`: twelve missing/wrong-type root payloads reject with invalid_request and no host request; explicit checked:false enqueues, consumes and completes. Schema negatives derive required fields from legacy definitions |
| Original poll guard must not destroy mis-correlated results | `PollRequiresOriginalRevisionAndKeepsResultForCorrectGuard`: UI, Semantic and Raw real-native completions reject a wrong expectedRevision, then return the original completion to a correct poll on the same owning connection |
| Same counters cannot bind observations to another host | `SameCounterDifferentHostCannotReceiveObservedUiOrInput`: two real runtimes have identical epoch/UI/map counters but distinct Observe sourceIds. A per-connection routing proxy switches the dedicated connection to B; session creation rejects A's source. Foreign UI/input guards dispatch zero requests to B; correctly bound B dispatch completes |
| Input schema parity | Differential schema examples cover all fifteen supported input verbs and eighteen invalid required-field, enum, numeric-bound and forbidden-field cases against legacy commands; managed marshalling now emits only the verb's allowed fields |
| Correlatable transport IDs | `GuardedTransportIdMustBeCorrelatableBeforeDispatch`: UI/input reject seven missing, null, string, fractional, nonpositive and Int32-overflow IDs before any host request; schema rejects the same transport forms |
| Required guarded input payloads | `GuardedInputRequiredFieldsCannotDefaultIntoHostInput`: twenty-two malformed actual-wire payloads across all fifteen input verbs reject before enqueue; absent/null/string coordinates cannot become pointer movement |
| Aggregate retention across disconnected owners | Native owner test retains a consumed request after disconnect, fills the remaining 255 slots through another owner, rejects a third owner, and frees capacity only after late host completion |
| Legacy input poll cannot bypass guards | Original-revision regression first attempts legacy polling of both Semantic and Raw guarded completions; guarded_poll_required preserves each result for the correct guarded poll |
| Explicit empty text preserves legacy parity | `EmptyTextRetainsLegacyWireAndManagedGuardedCompletion`: legacy and guarded actual-wire requests consume identical empty JSON string values and return correlated completions; the managed guarded session also completes the same payload |
| Escaped NUL text preserves JSON values | Two actual-wire and managed cases retain `a\\u0000b` for Raw text and Semantic Text Set, matching legacy native consumption and correlated completion |
| Full native modifier range | Two managed guarded UI cases consume exact 2147483648 and 4294967295 modifier masks; schema accepts those masks and rejects values beyond uint32 |
| Opaque replay reference compatibility | Legacy/guarded wire and managed text requests accept a nonempty secretKey reference and consume only the supplied fixture text; invalid reference types/empty strings reject before enqueue, with no secret resolution |

| Profile-agnostic native consumers must fail closed | Native C ABI -1/invalid-profile and generic C++ consumers fail guarded requests with -7 and owner-only completion; explicit authoritative-profile C++ overload consumes a valid guard; legacy generic consumption still succeeds |
| Numeric guards must agree across JavaScript schema/native/managed boundaries | AJV accepts MAX_SAFE_INTEGER and rejects original JSON tokens 2^53, uint64 max and uint64 max+1; actual UI/input wire rejects these at invalid_guard before enqueue; managed UI/input throws before pre-dispatch callback; Raw revision MAX_SAFE_INTEGER completes |
| Queued owned cancellation must terminate and free retention | Native and real managed bridge perform 300 enqueue/cancel/poll cycles with correlated Failed/-8 completion, no host consumption or legacy result; native checks live different-owner isolation, one-shot polling and disconnected-owner cleanup; in-flight cancellation retains eventual host completion |

## Local execution

Windows x64; MSVC 19.51.36260, CMake/Ninja from installed Visual Studio,
.NET SDK 10.0.401. No engine/application installation or security settings changed.
Native configure: root CMake, Ninja, Release, GUA_BUILD_GODOT=OFF,
GUA_BUILD_EXAMPLES=OFF, GUA_BUILD_IMGUI_EXAMPLE=OFF; build/guarded-msvc.

- `cmake --build build/guarded-msvc -j 4` and
  `ctest --test-dir build/guarded-msvc --output-on-failure`: 18/18 passed.
- `dotnet test bindings/dotnet/tests/Gua.Selector.Tests/Gua.Selector.Tests.csproj
  -c Release --no-restore --logger trx --results-directory artifacts/guarded-final`:
  438/438 passed before the final additional private-input test; no skips.
- Focused GuardedDispatchTests after audit fixes: 22/22 passed (including four
  dropped-response and eight malformed-completion paths).
- Final full managed suite after audit fixes: 448/448 passed, no skips;
  artifacts/guarded-reviewed retains the TRX.
- After actual GitHub review fixes: full managed suite 449/449 passed, no skips
  (includes all 23 guarded cases); artifacts/guarded-github-review-fixed retains
  the TRX. Native CTest remains 18/18 passed.
- `bun run --filter gua-value-tools test`: 209/209 passed after GitHub review
  fixes; schema test log artifacts/guarded-schema-review-tests.log.
  `bun run --filter gua-value-tools check` also passed.
- After source/poll/schema feedback: focused guarded suite 27/27 and full managed
  suite 453/453 passed, no skips; artifacts/guarded-source-poll-full-final retains
  the TRX. Native CTest remains 18/18; Value/schema suite is 210/210 and tsc passes.
  Final netstandard2.1 build passed with no warnings/errors.
- After the four further GitHub review fixes: native CTest 18/18, focused guarded
  suite 30/30, Value/schema 210/210 (1052 assertions) and tsc pass;
  artifacts/guarded-strict-focus and guarded-strict-schema-tests.log retain results.
  Two default-parallel full runs each passed 455/456, failing only the unchanged
  TraceLifecycleTests.SlowDiagnosticsCannotBlockFinalizationOrAppendAfterItsStopDeadline
  one-second scheduling deadline; its isolated run passed 1/1 in 97 ms.
  These failed full runs are retained in artifacts/guarded-strict-full and
  guarded-strict-full-repeat and are not counted as passing full validation.
  The complete suite with `-- NUnit.NumberOfTestWorkers=1` passed 456/456 with
  no skips; artifacts/guarded-strict-full-serial retains the TRX. CI must still
  verify the default runner configuration on the final HEAD.
- After the readiness-review empty-text fix: native CTest 18/18, guarded tests
  31/31, full managed 457/457 with `-- NUnit.NumberOfTestWorkers=1`, and
  Value/schema 210/210 (1054 assertions) plus tsc all pass. Results are retained
  in artifacts/guarded-empty-text-focus, guarded-empty-text-full and
  guarded-empty-text-schema.log. Final HEAD CI/review are tracked on the PR.
- `dotnet build bindings/dotnet/src/Gua.Testing/Gua.Testing.csproj -c Release
  -f netstandard2.1 --no-restore`: passed without warnings/errors.
- Native output and result logs: build/guarded-msvc/Testing/Temporary;
  managed TRX files: artifacts/guarded-final and artifacts/guarded-focus.

The initial independent gua_auditor reproduced a missing-required-outcome defect
on both real native UI/input polls: missing succeeded was accepted as Completed.
The parent added complete typed result validation and the eight proxy negatives
above. Parent inspection also corrected pointer coordinate marshalling. First PR
CI TypeScript failed because the new schema reference lacked registration in two
existing AJV consumers; both are now updated and all 208 tests pass locally.
The bounded final repo audit closed its original finding and found no additional
actionable findings on 64089e0; no further repo audit was spawned.

Actual Codex GitHub review of 64089e0 identified a P2 missing-required-UI-payload
defect: https://github.com/gua-project/gua/pull/180#discussion_r4193324360.
The new regression failed against the old native binary because a checkbox
request without checked returned ok:true. The guarded bridge now rejects missing
or wrong-type verb fields before enqueue and the schema requires the same UI
fields/types. The new regression passes against the rebuilt runtime, including
explicit checked:false completion.

Actual Codex review of aec3cb5 found two further P2s: input schema constraints
must match legacy verbs, and polling must compare the original revision before
one-shot result consumption. Both now have the differential schema and real
native polling tests above. The connection-owned guard table is bounded to 256
outstanding UI/input records combined and destroyed on owner disconnect.
Playtest integration feedback on PR comment 6012771144 prompted the source
binding and real two-host regression above. SourceId is the existing Observe
context identity; Raw ActionMap guard semantics are unchanged.

macOS x64 CI on prior heads timed out the malformed-UInt64 direct wire case.
Increasing its deadline from three to fifteen seconds did not resolve it and
is not counted as a fix. Guard metadata no longer runs redundant legacy stoull
conversions before strict parsing, and request correlation is captured before
metadata rejection. The one-shot helper now asserts any ID-bearing reply's
correlation immediately instead of waiting past an uncorrelated error. Final
macOS CI must verify this correction; no resend or assertion weakening was added.
Source-binding development tests also caught the transport envelope's document
shape and the old proxy fixture's single-connection limit; both were corrected
before the successful focused/full runs above.

Actual Codex review of 7993b22 identified four P2s: malformed input defaults,
aggregate retained UI capacity, invalid transport IDs and unguarded input polling.
The native payload gate, aggregate core limit, strict correlation IDs and legacy
poll isolation above address them with deterministic regressions. Legacy command
parsing remains unchanged. Playtest's source-binding review accepted the existing
immutable Observe identity contract in PR comment 6013661942.

After the user approved merging, marking 0cae57d ready triggered another actual
Codex review. Its P2 discussion 4201094538 reproduced explicit empty text being
rejected only by the guarded bridge despite legacy/core/schema acceptance.
The new regression first completed legacy empty text, then failed with
invalid_request at the guarded receipt on the old runtime; the TRX is retained
in artifacts/guarded-empty-text-before. The bridge now allows explicit empty
strings without broadening required-field, UTF-8 or length checks. The schema
differential test also includes empty text; no existing schema changed.
The first ee6cbf3 Linux CI passed empty-text dispatch but failed the two-host
test in HttpListener.Close with an already disposed NetworkStream during fixture
teardown. The proxy now cancels accept waits, joins all accept/relay tasks before
closing listener streams, and tolerates only ObjectDisposedException from the
final listener close. Host assertion failures still propagate; no assertions,
timeouts or fault counts were weakened.
After the teardown fix, all 31 guarded cases pass locally; their TRX is retained
in artifacts/guarded-proxy-teardown-focus. Cross-platform CI verifies the actual
Linux shutdown path before merge.

Actual Codex review of 729bb9c found three further payload parity P2s:
4201205405 (escaped NUL JSON text), 4201205413 (uint32 modifier masks), and
4201205416 (optional text replay reference). Five new cases all failed against
the old runtime in artifacts/guarded-parity-before after the legacy paths passed
where applicable. The corrected bridge accepts NUL only in JSON text values,
uses strict uint32 modifier parsing without the later legacy signed overwrite,
and validates/accepts the opaque secretKey metadata just like the legacy wire.
Managed text requests may forward the optional reference. Native bridge/core
code has no secret resolver; the test uses only synthetic reference/text data.
Legacy command parsing and native ABI layouts are unchanged. The schema now
specifies the native uint32 bound and restores the legacy optional reference.
After rebuild, all 36 guarded cases, native CTest 18/18, schema 211/211 (1071
assertions) and tsc pass; artifacts/guarded-parity-fixed-focus and
guarded-parity-schema.log retain those results.
The full managed suite passes 462/462 with one NUnit worker (no skips), retained
in artifacts/guarded-parity-fixed-full; netstandard2.1 builds with no warnings
or errors after the additive managed replay-reference field.

The first broad managed run had 14 environment failures (Trace viewer/MCP artifacts
not yet built). After the CI-prescribed locked Bun restore, gui-mcp build and
Inspector build:trace, the unmodified full suite passed. Initial mutation ctest
regex selected no tests; corrected explicit test selection supplies detection
evidence below. Neither incomplete run is counted as success.

Actual Codex review of 558c1e6 identified discussions 4201311276, 4201311284
and 4201311290. The combined fix fails guarded consumption without explicit
current authority, limits new wire guards to JS-safe numeric ranges consistently,
and retains cancellation results under their original ownership until polling.
Legacy/local uint64 APIs and legacy unowned cancellation remain unchanged.
Before correction, the added native test fails at the intended generic-profile
assertion (artifacts/review-three-native-before.log); the real managed bridge
fails three new cases, including cancellation still Enqueued at poll 0
(artifacts/review-three-before). AJV fails the new rounded-uint64 schema test
(artifacts/review-three-schema-before.log). An initial test compile error used
the wrong host CompleteGameInput signature; it was corrected before these
failure demonstrations and is not counted as detection evidence.
After correction, native CTest 18/18, guarded transport 39/39, full managed
465/465 (NUnit.NumberOfTestWorkers=1, no skips), schema 212/212 (1079 assertions),
tsc and netstandard2.1 build (zero warnings/errors) pass. The native test was
rerun after adding live-owner cancellation isolation/cleanup; the full managed
run includes the final UI/input guard cases and upper boundary checks.
Artifacts: review-three-fixed-focus, review-three-fixed-full,
review-three-schema-fixed.log. These are Windows/MSVC checks on the working
tree based on 558c1e6; final submitted HEAD CI/review is recorded in the PR.
No further independent repository audits were run: the two-pass audit gate
was already completed; these are primary-agent fixes of actual GitHub findings.

## Selected violation detection

Historical revision-race detection on the earlier reviewed implementation:
an isolated copy of native/ and root CMakeLists lives in ignored
build/guard-mutation; submitted source is unchanged. The copy replaces
`value.guard_revision != (` with `false && value.guard_revision != (` to bypass
the UI revision comparison at consume. The mutated guarded-dispatch target
builds successfully. `ctest --test-dir build/guard-mutation-build -R
gua-guarded-dispatch-tests --output-on-failure` fails at the intended assertion
`!gua_consume_action_request(ctx, GUA_ACTION_CLICK, "buy", &request)` in
guarded_dispatch_tests.cpp:34, exit 0xc0000409. The unmutated target passes. The final regression now calls the explicit
profile-aware consumer (current profile 0), so a generic fail-closed check cannot
mask the required revision-race assertion. The historical mutant is not claimed
as a rerun of the final submitted HEAD.
The fault proxy additionally verifies firing/counts for all real response losses.

## Limits

These are current-source native C ABI/runtime tests and actual local WebSocket
managed transport tests, not Godot/Unity engine integration or published package
consumer acceptance. The old-client test covers the legacy wire/ABI behavior,
not a separately executed 1.1.1 binary client. Existing engines consume through
the same C ABI; host execution after successful consume is not transactional.
CI and actual Codex GitHub review must be recorded on the final PR HEAD separately.
