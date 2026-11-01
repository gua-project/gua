# Remote build variants

This contract separates build purpose, immutable compiled availability, and
session authorization. A Release optimization configuration does not grant or
remove remote access by itself.

| Purpose | Explicit remote opt-in | Compiled remote ceiling |
|---|---|---|
| `shipping` (source default) | OFF | none; remote transport/dispatch excluded |
| `development` (Debug/Editor or development Release) | OFF / ON | none / Debug |
| `qa` (Release) | ON required | Debug; host may narrow to Player |
| `ai-player` (Release) | ON required | Player, immutable |

`GUA_ENABLE_REMOTE_AUTOMATION` defaults OFF. Select the purpose explicitly with
`GUA_DISTRIBUTION_FLAVOR`. Shipping+ON and QA/AI-player+OFF are errors. Distribution
variants require the actual Release configuration. Debug/Editor remote use is an
explicit development opt-in, never an effect of `DEBUG` alone.

The compile gate applies to native shared and Godot embedded runtimes, WS and
WebMCP registration, and managed engine dispatch. Compiled-out bridge C ABI calls
remain compatibility stubs: start/running return false, URL is empty, stop/publish
are no-ops. The symbol's existence does not imply availability. Local observation,
publication, action queues, recording and clock APIs remain usable.

`gua_copy_build_info_json` and `gua_runtime_copy_build_info_json` return immutable
schema version 1 JSON: `flavor`, native `configuration`, `remoteEnabled`,
`remotePermissionCeiling`, `transports`, `sourceCommit`, and `buildId`. The Godot
binding exposes `get_build_info_json`; .NET exposes `GuaBuildInfo.GetJson()` and
`GuaRuntime.GetBuildInfoJson()`. Build identity is not an authorization grant or
game-build attestation. The engine export kind is recorded separately by staging.

An AI-player runtime starts in Player before any client/frame can be created.
An explicit Debug environment request fails creation. Profile setters, client
factories, reset and reconnect cannot elevate it. Core profile-aware Debug reads
are rejected; unscoped compatibility reads/actions use Player. Borrowing the
context or creating another core context does not remove the compiled ceiling.
Host publication of private state remains possible; consumer projection hides it.

Existing Player UI projection/allowlists and separate game-input authorization
continue to apply at enqueue and host consumption. AI-player core input defaults
to no capabilities; host-only `gua_set_player_game_input_capabilities` uses the
runtime capability bit layout. Runtime configuration synchronizes this mask, so
borrowed-core requests cannot evade revocation. Revoked holds receive cleanup,
including late completion. Host screenshot opt-in remains separate and is not
available as a remote setting. Debug-only remote clock and Spatial execution
handlers are excluded from AI-player builds; local clock/offline DTO APIs remain.

Normal Shipping cannot regain the missing implementation through environment,
arguments, settings or a bridge API call. AI-player cannot gain Debug through those
paths. This is a guarantee about Gua's supplied build and API paths, not arbitrary
game-defined IPC, code injection, or replacing the application with another build.

Final packaging checks purpose/availability/ceiling, actual native build-info,
managed compile constants and remote handler IL, and hashes of all packaged files.
Unknown, mismatched, stale or extra files fail closed. Shipping and AI-player
distribution jobs reject testing artifacts. Do not ship every variant and select
one through a runtime environment variable. Keep each variant's build/staging and
native dependency selection isolated.

Player-only clients may run Playtest scenarios that require only allowed Player
capabilities. They must not advertise or negotiate QA-only abilities. Failure to
connect is not proof of compiled-out functionality: offline identity is needed to
distinguish that case from startup/network failure. Playtest integration changes
are separate from this Gua implementation.
