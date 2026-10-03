# Remaining artifact and spatial acceptance

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
