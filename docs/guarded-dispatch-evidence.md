# Guarded dispatch validation evidence

Base main and verified `gua-v1.1.1`: `88f5dca4aa97c5d5187ab66ea4416377f3affc96`.
Implementation/test scope is the additive guarded remote route described in
`protocol/specs/guarded-dispatch-v1.md`. Playtest #7 acceptance is not revised.
This PR does not merge, tag, or publish a package.

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

macOS x64 CI on both prior heads timed out the new direct wire helper at its
three-second deadline during a one-shot receive; the real proxy/managed tests
passed. Its deadline now includes 15 seconds for handshake/snapshot delivery on
that slower runner, retaining one send and all assertions. Final-head CI must
confirm that correction; no retry or expectation weakening was added.

The first broad managed run had 14 environment failures (Trace viewer/MCP artifacts
not yet built). After the CI-prescribed locked Bun restore, gui-mcp build and
Inspector build:trace, the unmodified full suite passed. Initial mutation ctest
regex selected no tests; corrected explicit test selection supplies detection
evidence below. Neither incomplete run is counted as success.

## Selected violation detection

An isolated copy of native/ and root CMakeLists lives in ignored
build/guard-mutation; submitted source is unchanged. The copy replaces
`value.guard_revision != (` with `false && value.guard_revision != (` to bypass
the UI revision comparison at consume. The mutated guarded-dispatch target
builds successfully. `ctest --test-dir build/guard-mutation-build -R
gua-guarded-dispatch-tests --output-on-failure` fails at the intended assertion
`!gua_consume_action_request(ctx, GUA_ACTION_CLICK, "buy", &request)` in
guarded_dispatch_tests.cpp:34, exit 0xc0000409. The unmutated target passes.
The fault proxy additionally verifies firing/counts for all real response losses.

## Limits

These are current-source native C ABI/runtime tests and actual local WebSocket
managed transport tests, not Godot/Unity engine integration or published package
consumer acceptance. The old-client test covers the legacy wire/ABI behavior,
not a separately executed 1.1.1 binary client. Existing engines consume through
the same C ABI; host execution after successful consume is not transactional.
CI and actual Codex GitHub review must be recorded on the final PR HEAD separately.
