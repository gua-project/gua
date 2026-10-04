# Remaining artifact and spatial acceptance

## Current acceptance status

As of 2026-10-04, #129/#130 are closed with one explicitly retained limitation:
**native Intel macOS / Unity 6000.5.3f1 / actual UPM / Editor Play Mode:
UNVERIFIED (not executed because no Intel Mac is available).** It is neither
PASSED, FAILED nor Unsupported. M4, Rosetta and Intel Mono Player execution do
not establish native Intel Editor acceptance. There is no ongoing Intel
hardware request, procurement, provisioning, persistent runner or emulation plan.
[Issue #174](https://github.com/gua-project/gua/issues/174) tracks this documentation.

Native arm64 Editor Play Mode passed twice on an Apple M4 Mac mini / macOS
26.6.1 / Unity 6000.5.3f1 with the actual accepted UPM. Both runs checked native
identity, rejection without semantic side effects, correlated Click and loading
UI, finalized Trace and packaged Viewer. Two disposable exact-package consumers
performed the actual Editor operation before injecting a false Trace completion
or unfinalized saved manifest; both failed the intended assertion. These are
verdict/manifest controls, not claims of real storage failure.

Product: `595f589eebdea812b0649ab5f57ac079728fa610`, private `0.0.0-ci`;
UPM SHA-256 `21da33106301de108ecb1ff0868fe0342a00af980e805ff14a67635d444412f3`.
Recipe: `4816b5c929fd74f6ceea9d8017986da3fce27577`, PR #173 tree
`089efe190e949413657cbbfeaafefe52e3f6ecfa`. Product and recipe are distinct.
The sanitized evidence ZIP SHA-256 is
`f5beb44f30f5e8d903f8c07e09d029a6957e9bc5f5c0d1eb119366ddef1d7a07`;
it is privately retained, not publicly downloadable from these comments.
See the [#129 execution record](https://github.com/gua-project/gua/issues/129#issuecomment-5975220196)
and [#130 execution record](https://github.com/gua-project/gua/issues/130#issuecomment-5975220444).

Windows/Linux Editor, all four native-host Mono Players, Unity PhysX spatial
and Godot 4.7 GodotPhysics3D/Jolt retain their measured scope. Final PR #173
recipe `8ac6c6161d8576f8fe308eed54d8842b5d8c4604` has a
[successful candidate run](https://github.com/gua-project/gua/actions/runs/37102760247).
This does not revalidate those products at a later source commit or prove every
capability, engine patch or backend. Intel package/Player/spatial/Godot evidence
is not relabeled unverified because the Intel Editor route is unexecuted.
Public version/channel selection and publication remain separate operations.

## Historical investigation and execution

The entries below describe the requirements and outcomes at their stated
recipe revisions. Pending work and failures in this history do not supersede
the current status above; failed attempts remain failed evidence.

Baseline product: main `3a850c041093bd87c3e20530d60927d68782d8f5`.
Existing actual UPM target is `595f589eebdea812b0649ab5f57ac079728fa610`,
private version `0.0.0-ci`, recipe `98cb571d2566bcd510519d40002cdef49acd512e`,
candidate run 37096253057. Recipe changes do not rename that product target.

| Issue / remaining finite condition | Existing evidence | Required new assertion / route |
| --- | --- | --- |
| #129 Linux Editor Play Mode from actual UPM | Four native-host Mono Players passed; Windows archive-only Editor passed on source 0a5cbc8 | Existing licensed GameCI Linux Editor enters actual Play Mode with the extracted UPM; external fresh package-only client checks the same identity, rejection/no side effect, correlated Click and observed loading UI/Trace as Players |
| #129 Intel and arm64 macOS Editor Play Mode | Actual native-host Players passed, not Editors | Existing licensed native Editor on each architecture must run the same archive-only external consumer. Current GameCI macOS activation accepts serial or license server, not the existing UNITY_LICENSE; release Editor path acquires a Personal license. Neither new credentials nor license acceptance is authorized |
| #129 actual Godot archive launch/attach | Four-RID assembled payload execution and actual ZIP composition separately passed | Extract the actual CI ZIP into a fresh directory and execute that payload, retain archive hash/source pin, both GodotPhysics3D/Jolt routes and package-only consumer results |
| #130 four-RID packaged Unity spatial client routes | Windows PhysX TS/built MCP/package consumer passed; non-Windows UI cannot substitute | Build explicit Testing spatial fixtures using only precompiled UPM; all native hosts require three correlated actual physics batches, TS/built MCP and isolated exact-package consumer, common Trace/report, clean exit |
| #130 full geometry/backend/profile at declared pinned desktop combinations | Windows Godot4.7 GodotPhysics3D/Jolt and Unity6000.5.3f1 PhysX: 67 results, 34 lease races and 360 callback samples | Repeat existing analytical fixtures, all expected results/races and each 120-sample phase on each RID. Require actual monotonic thread CPU measurement, retain clock origin; unavailable CPU cycles remain null, never fabricated |
| #130 authority/reset/scene, correlation/partial results and legacy compatibility | PR167/168 controlled real transports and retained-result negatives; four-RID native CI | Reuse within their measured scope; no new blanket all-feature claim |

The fixed scope is Godot4.7 stable/GodotPhysics3D/Jolt and Unity6000.5.3f1
Mono/PhysX on win-x64/linux-x64/osx-x64/osx-arm64. Other patches/backends,
WebGL/IL2CPP, arbitrary mesh/gameplay accuracy and actual Codex routing are
outside these claimed measurements. Built-in privileged browser spatial is
Unsupported. Playtest product completion and public release are separate.

Selected violation detection must exercise the actual client/fixture path.
Reuse the demonstrated mismatched completion/rejection-state and spatial
missing/duplicate/correlation/redaction negatives. New profile acceptance must
reject missing phases/counts or unavailable CPU evidence. Required conditions
remain open until their actual assertions execute; disclosure is not a waiver.

Linux/macOS thread CPU clocks use CLOCK_THREAD_CPUTIME_ID; Windows retains
GetThreadTimes and QueryThreadCycleTime. This measures the actual callback
thread, not process CPU or wall time. See the [POSIX clock contract](https://pubs.opengroup.org/onlinepubs/000095399/functions/clock_getres.html).

Local focused execution on Windows 11 / Unity6000.5.3f1 used the actual accepted
UPM SHA256 `21da33106301de108ecb1ff0868fe0342a00af980e805ff14a67635d444412f3`
and product `595f589`, with this branch's fixture recipe. Actual geometry produced
67 results, 34 lease races and 360 callback samples with GetThreadTimes CPU.
The same archive-only transport Player passed TypeScript, built MCP and a fresh
external exact-package consumer. The corrected Editor entry point passed actual
Play Mode with that product: matching BuildId/ABI/protocol, NodeNotFound and
unchanged semantic state, correlated Click completion, observed loading screen,
finalized Trace and embedded report. These local results do not substitute for
the pending non-Windows or final-commit CI routes.

Four disposable mutations of that actual geometry evidence were rejected:
missing callback sample, missing CPU clock, fabricated Unix cycles and a duplicate
case replacing a required case. Each triggered the intended completeness/CPU/case
assertion. The initial extended candidate run 37099987545 is a failed pre-fix run:
Linux Editor compilation exposed incorrect SessionState method names; all four
Players built but root-owned output prevented later provenance writes. Stage
provenance before the build; retain the failures separately from corrected runs.

Corrected recipe `4d7ec6f` / candidate 37100569090 passed Linux actual Editor Play
Mode and all four spatial builds; Windows geometry/profile and three spatial
clients passed. The native-host failures exposed fixture harness requirements:
macOS resets the launch cwd to its app bundle, so SubsystemRegistration restores
the explicitly supplied fresh evidence directory for both fixture scenes; Linux
requires the same Xvfb harness already used by the accepted UI Player route.
Those failed attempts are not non-Windows spatial acceptance. New final-head
execution must pass the unchanged geometry/profile and client assertions.

Recipe `52721e2` / candidate 37101089385 passed Linux Editor, all four builds and
full geometry/profile plus three spatial clients on Linux, Intel macOS and arm64
macOS. Windows geometry passed, but readiness was observed between file creation
and completion of its JSON write (Unexpected EOF). Both engine fixtures now close
their temporary readiness file before atomic rename; existence denotes complete
publication. This real failure is retained as violation detection, and the final
recipe requires rerunning all actual routes. Normal CI37101078467 also aborted a
Linux native bridge test with `std::length_error` during disconnect handling; the
core/bridge files are unchanged by this branch. It is not a successful gate and
needs final-head validation separately.

Recipe `100d6f5` / candidate 37101629200 passed all ten active Unity jobs,
including Linux Editor and all four full geometry/profile/client routes. Final
GitHub review then identified four supported assertion/infrastructure gaps:
Godot CMake builds omitted the asserted source ID; constant CPU/wall clocks could
pass; the spatial package client discarded unsuccessful Trace completion/reader
issues; Editor timeout could race an exited child. Both native configurations now
receive the source pin, phase clocks must actually advance, finalized complete
Trace is required, and timeout exits in finally. Existing actual evidence from all
four hosts passes the stronger clock checks. A constant-clock mutation passed the
pre-fix assertion and fails the intended new advancing-clock assertion; a
disposable exact-package client with forced false Trace completion exits at the
required completion assertion after real engine queries. The unmutated strengthened
package client passes the real Windows geometry/spatial route. These final review
fixes require fresh exact-head CI and actual candidate execution.
