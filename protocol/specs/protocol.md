# Gua Protocol Draft

## Common Value v1

Additional observations use the independent [Value v1 contract](value-v1.md).
Its tagged scalar/enum/list/set values, enum catalog, validation and equality do
not replace existing nullable UI/World fields or game-input vector2 objects.
Observe registration, lifetime and local subscriptions follow [Observe v1](observe-v1.md).
Transport integration and capability negotiation remain separate follow-up work.

Gua exposes the current UI state of a running game as a semantic UI tree and
accepts commands that interact with nodes in that tree.

## UI Tree

A UI tree response describes one frame or snapshot of runtime UI state.

- `screen`: logical screen name, such as `title` or `settings`
- `nodes`: flat list of semantic UI nodes
- `schemaVersion`: currently `2`
- `frameSequence`: increments once for every completed host frame
- `revision`: increments only when the semantic screen or node content changes
- `sessionEpoch`: identifies the current test-isolation session and increments after every successful context reset

Nodes are intentionally semantic. They describe role, label, state, bounds, and
supported actions, not rendering internals.

All adapters report bounds in physical viewport pixels using the same coordinate
space as screenshots: the origin is the top-left corner, X increases rightward,
and Y increases downward. Adapters must not publish NaN or infinite coordinates.

Version 2 adds `parentId`, `text`, `value`, and optional boolean state. An
omitted property means that the adapter cannot observe that state. A present
property whose value is `false` means that the adapter observed the state and
it was false. Adapters must not fill unsupported state with false. Values
crossing the initial C ABI are normalized to UTF-8 strings; the schema also
allows native JSON scalar values for future transports.

Detailed state is an additive v2-tree extension backed by the versioned v3 C ABI descriptor. It includes
`caretPosition`, `selectionStart`/`selectionEnd`, `scrollX`/`scrollY`, `scrollMaxX`/`scrollMaxY`,
`rangeValue`/`rangeMin`/`rangeMax`, and `selectedIndex`. Unsupported properties are omitted, so an observed
zero remains distinct from unknown. At most one published node may have `focused: true`; adapters reject a
staged frame with multiple focused nodes. Sensitive controls continue to omit text/value.

`frameSequence` and `revision` have different purposes. Rebuilding the same
semantic tree in consecutive frames increments `frameSequence` but leaves
`revision` unchanged. Changing the screen, node membership, hierarchy, bounds,
text, value, state, or actions increments `revision` at `end_frame`.

Frame construction is transactional. `begin_frame` creates a private staging
frame, registration functions update only that staging frame, and `end_frame`
atomically publishes the screen, nodes, `frameSequence`, and `revision` under
one context lock. Reads, selectors, action validation, diagnostics, and remote
transports only observe the last completed frame. Before the first successful
publish they observe the empty `unknown` snapshot. An invalid or abandoned
staging frame never changes the last completed snapshot or its metadata.

`sessionEpoch` starts at 1. A successful reset starts a new epoch and resets
`frameSequence` and `revision` to zero. Consumers must use the epoch together
with frame/revision metadata; values from an older epoch are stale.

## Test session inspection and reset

Context inspection reports semantic node count, pending and in-flight action
request counts, unconsumed event count, log count, screenshot presence, and the
first request/event action plus node id. It never includes action payload values,
so sensitive values cannot leak through teardown diagnostics.

Reset is scoped to one `gua_context_t` / `gua_runtime_t`; it does not use global
state and cannot affect another context. The selectable flags are nodes (1),
requests (2), events (4), retained diagnostic history (8), logs (16), and
screenshot (32), virtual clock state and pending work (64), and World Object Tree state (128). The current default is
207: nodes, requests, events, history, clock state, and world state are cleared, while logs
and screenshot are preserved unless explicitly selected. Resetting the clock
also advances its generation so language wrappers can discard old schedules.
Bridge server, port, active WebSocket connections, and context configuration are
never reset.

The published C ABI constant `GUA_RESET_DEFAULT` retains its original value 15.
Current native callers use `GUA_RESET_DEFAULT_V3` (207) and set
`gua_reset_options_t.flags_version` to `GUA_RESET_FLAGS_VERSION_CURRENT` (2).
For binary compatibility, the runtime recognizes an older reset-options struct
or legacy flags version and upgrades the exact legacy Default (15) and All (63)
masks to include the clock and world state. Flags-version 1 exact Default (79) and All (127) masks are upgraded to include world state. Current-version explicit masks are never upgraded.

Strict reset checks selected request/event queues before mutation. If pending,
in-flight, or unconsumed state exists, it returns `dirty` with counts and a
redacted first-item summary and changes nothing. Non-strict reset reports how
many selected items it discarded. Every successful reset increments
`sessionEpoch`; local callers may pass zero to use the current epoch, but remote
`reset_context` commands must provide `expectedSessionEpoch`. Current clients
that provide `flags` also send `flagsVersion: 2`; omitted flags resolve to
the current default 207. A missing `flagsVersion` marks an older client, so exact
legacy Default (15) and All (63) masks are upgraded to include the clock and world state. A stale remote
epoch is rejected without mutation. Multiple clients of one runtime observe the
same reset because the isolation boundary is the shared runtime context, not a
WebSocket connection. Runtime-owned on-demand screenshot requests, in-flight
batches, and unpolled results from the previous epoch become `stale_session`
after every successful reset; `GUA_RESET_SCREENSHOT` separately controls whether
the latest published context screenshot is cleared.

High-level testing clients may compose status, reset, diagnostics, and screenshot
APIs into independent startup/teardown policies. Teardown capture runs before
the context, bridge, or engine process is destroyed; optional non-strict cleanup
runs only after leak diagnostics were attempted. Test-body failures remain
primary, with teardown/capture failures reported as secondary information. Leak
reports may include request ID, action or event type, node ID, counts, and session
epoch, but never action payload values. Clean sessions do not create artifacts.

## Failure diagnostics and artifact version 1

### .NET locator の auto-wait

`GuaLocatorQuery` の遅延解決 API は既存の query、UI snapshot、semantic action と
request-correlated completion を組み合わせる client helper であり、C ABI や wire
schema は変更しない。0 件、hidden、disabled、対象 action 未公開を総 timeout 内で
再検索し、複数一致は即座に strict violation とする。操作直前にも再解決して得た
node ID を一度だけ enqueue し、その request ID の observed completion を待つ。
enqueue acceptance は操作完了ではない。actionability・送信・completion は同じ
単調時計の予算を共有し、残り予算以下の poll delay と cancellation を使用する。
同期 transport 呼び出しは中断できないため、呼び出し復帰時にも予算を確認する。
送信後の timeout/cancel は副作用の不在を意味せず、自動再送しない。
completion 後の UI/World postcondition は既存の状態 wait で明示する。
旧 eager `Get()` と `GuaNodeExpectation` の意味は維持する。

The opt-in [Gua Trace v1](trace-v1.md) recorder adds a bounded, versioned timeline
and offline viewer without replacing diagnostics or Recording. It separates
caller outcome, capture quality, observation times and host completion.

`diagnostics.schema.json` is the source of truth for a best-effort failure
snapshot. The additive C ABI copy-JSON API and WebSocket `get_diagnostics`
command return the same versioned document. It includes the final semantic UI
tree, session/frame/revision, pending and in-flight requests, recent operation
and observed-event history, logs, optional screenshot, and caller-provided
environment metadata.

Operation and event history are context-owned bounded ring buffers. The default
limit is 100 entries per history and callers may set another non-negative limit;
zero disables retention. Polling an event never removes its retained history.
`GUA_RESET_HISTORY` clears both histories without changing queue semantics.

Sensitive action values are available only while the adapter consumes a
request. Pending diagnostics and retained history store an empty value with
`sensitive: true`; they never retain plaintext. A missing screenshot is `null`
and does not fail semantic diagnostics capture.

Filesystem layout and overwrite policy belong to testing helpers, not the
runtime core. Writers use a unique failure directory, preserve the original
assertion/timeout when capture fails, and report capture errors as a secondary
note. When a wait captured an initial tree, the writer creates a deterministic
node-id diff with added, removed, and changed IDs.

## Adapter-Owned Reflection

Runtime adapters should rebuild the semantic tree from the host UI every frame
or snapshot. Game code should not have to restate its visible buttons, labels,
and controls as separate Gua calls when the adapter can observe those controls.

- If a host UI element disappears, the next adapter snapshot omits its Gua node.
- If a host UI element remains in the host tree but becomes hidden, the adapter
  keeps the node and publishes `visible: false`.
- If a host UI element is clicked by the user, the adapter emits a Gua event.
- If automation requests `click_node`, the core records a pending click request.
  The adapter consumes that request when it reaches the matching host UI element
  and then activates the host control through the same path normal game code
  already uses.

This keeps the C ABI as the stable protocol boundary while letting ImGui, Godot,
and later engine adapters own the engine-specific reflection details.

### Initial adapter mapping

| Host control | Gua role | Adapter-owned fields |
| --- | --- | --- |
| ImGui `Button` facade | `button` | text, visible, enabled, focused, hovered, pressed |
| ImGui `Text` facade | `text` | text, visible |
| Godot `Button` | `button` | parentId, text, visible, enabled, focused |
| Godot `CheckBox` | `checkbox` | parentId, text, focused, checked |
| Godot `LineEdit` / `TextEdit` | `textbox` | parentId, text, value, focused |
| Godot `SpinBox` | `slider` | parentId, value, enabled from editable state, focused through the inner editor |
| Godot `OptionButton` | `combobox` | parentId, value, focused |
| Godot `ItemList` | `list` + `listitem` children | stable derived child id, parentId, text, selected |
| Godot `TabContainer` | `tablist` + `tab` children | stable derived child id, parentId, text, selected |
| Godot `ScrollContainer` | `scrollarea` | bounds, visible, enabled, scroll action |

## Semantic selectors

`selector.schema.json` is the source of truth for semantic queries. String
criteria are ordinal and case-sensitive. Their default match mode is `exact`;
callers may explicitly select `contains` or ECMAScript `regex`. An invalid regex
is a selector syntax error, never a zero-match result.

Criteria combine with logical AND. `name` means the node's accessible `label`.
`text` uses the v2 `text` field when known and falls back to `label` for legacy
nodes. `visible` and `enabled` are tri-state at ABI level (`any`, `false`,
`true`) so omitting a filter differs from requiring `false`.

A scope parent is excluded from its own results. The default scope searches all
descendants by following `parentId`; `directChild: true` limits it to immediate
children. A strict single query fails for both zero and multiple matches.
`QueryAll` is the only query form that accepts multiple results. Ambiguity
diagnostics include candidate `id`, `role`, `label`, and `parentId` and suggest
adding a stable id, state filter, or narrower scope.

The C ABI evaluates selectors through versioned `gua_selector_v1_t` and
`gua_query_nodes_json`. C++, .NET local contexts, and the remote `query_nodes`
command all use that evaluator. The legacy `gua_find_node_by_*` exports remain
available for ABI compatibility but retain first-match behavior.

The legacy `gua_register_node` and `gua_get_node_state` C exports remain valid.
New integrations use `gua_node_descriptor_v2_t` / `gua_node_state_v2_t`, whose
`struct_size` protects ABI versioning and whose `known_mask` distinguishes
unsupported properties from observed false or empty values. Readers should
continue accepting legacy payloads that contain only `screen` and `nodes`
during migration.

## World Object Tree v1

`world-object-tree.schema.json` defines a separate, read-only snapshot for explicitly registered game-world objects. It never mixes Node2D, Node3D, GameObject, or gameplay state into the Semantic UI Tree. A snapshot has its own `scene`, `frameSequence`, and `revision`, while sharing the context `sessionEpoch`.

Publishers use `begin_world_frame -> register_world_object -> end_world_frame`, and call `abort_world_frame` when adapter-side conversion or collection fails before native validation. Publication is atomic: duplicate IDs, missing parents, cycles, non-finite positions, invalid state values, or malformed descriptors reject the whole staged frame and preserve the previous snapshot. Omission from the next valid frame removes an object. State is limited to flat string, finite number, boolean, or null values and must not contain secrets. Because the v1 C ABI represents numbers as `double`, integer publishers and selector clients must reject values that cannot be distinguished exactly at that boundary; JavaScript clients accept integer criteria only within the safe-integer range.

`visibleToPlayer` is host-defined semantic visibility, not pixel occlusion. The host fixes an observation profile before transport use: `debug` returns every registered object, while `player` removes invisible or `private` objects and descendants whose parent is not observable. Commands do not accept a profile override. Queries project first, so guessing a private ID produces the same empty result as an unknown ID. Player snapshots and transport status/reset metadata use the projected revision and object count; changes confined to hidden objects cannot be detected through those fields.

`query_world_objects` either omits all state criterion fields or supplies `stateKey`, `stateType`, and exactly the typed value required by that type (`stateString`, `stateNumber`, or `stateBool`; null requires no value field). Incomplete or conflicting criteria are rejected rather than treated as an unfiltered or zero-valued query.
The MCP and WebMCP tools expose the same criterion as the paired `stateKey` and `stateValue` arguments. Supplying only one is invalid and must fail before a provider query or wait begins.
`directChild` is meaningful only within a parent scope. A command or tool call that enables it without a non-empty `parentId` is invalid and must be rejected before querying.

The additive selector v2 supports `near: { relativeToObjectId, maxDistance }` and a positive `limit` that is valid only with `near`. MCP, WebMCP, and WebSocket commands flatten these as `relativeToObjectId`, `maxDistance`, and `limit`. Projection is completed before the reference object is resolved, so a private, unknown, or coordinate-omitted reference produces the same valid empty result. Candidates must have complete finite projected coordinates and the same space as the reference. Distance is Euclidean XY in `world2d` and XYZ in `world3d`; the boundary is inclusive (`distance <= maxDistance`) and the reference object itself never matches. Existing selector conditions are applied first, then matches are ordered by distance and object ID compared ordinally as UTF-8 bytes, and finally `limit` is applied.

`world-query-result.schema.json` defines the v2 result. Successful queries return the snapshot's `sessionEpoch`, `frameSequence`, and `revision`. Spatial results also return `spatial: { relativeToObjectId, truncated, distances }`; `distances` corresponds one-for-one with the returned `matches`, and `truncated` is true only when `limit` omitted otherwise-matching objects. Each wait poll resolves both the reference and candidates again from one current projected World snapshot; no earlier position or match is retained. Positions and `maxDistance` use host-defined world units, so publishers must keep one unit convention within each space and scene.

World v1 provides no actions, pathfinding, teleportation, or arbitrary host method invocation. Capability `world_object_tree_v1` is advertised only after an adapter installs its world-frame publisher.

## Agent projection policy v1

Capability `agent_projection_v1` applies the host-owned `debug` or `player` observation profile before data reaches a transport. Existing C ABI entry points remain debug-compatible; runtimes use the additive profile-aware tree, query, diagnostics, and action entry points. A runtime may narrow from debug to player before starting its bridge and cannot be elevated again by a command or tool argument.

Debug returns the complete registered UI and World snapshots. Player UI `auto` nodes require effective visibility through every ancestor. Player World `auto` objects require host-defined semantic visibility and active state through every ancestor; render visibility is not a substitute. A `private` ancestor removes its complete subtree, and projection never reparents descendants.

`agent-policy.schema.json` defines field rules `keep`, `omit`, `redact`, typed replacement, and numeric `quantize`; UI policies validate against `$defs/uiPolicy` and World policies against `$defs/worldPolicy`. When a policy contains multiple rules for the same path, the later rule replaces the earlier rule. Quantization is `floor(value / quantum) * quantum`; integer-valued UI state uses an exact integer path whenever the quantum is an exactly representable positive integer. Replacement values must preserve the target field's protocol constraints, including nonnegative UI bounds width and height and integral caret, selection, and selected-index state. Identity and hierarchy fields (`id`, `parentId`, `role`, and `kind`) cannot be transformed. UI action allowlists are intersected with role support and current enabled state. World actions remain outside World v1, but future world and raw-input capabilities must use the same host authorization boundary.
`omit` removes the targeted JSON member rather than publishing an empty substitute; accordingly, projected World objects may omit `tags` even though Debug publishers continue to emit it. A string `replace` rule keeps the member present even when its replacement is the empty string, including for World `description`, `domainId`, and `relatedUiNodeId`.
`replace` supplies the projected field even when the host did not report that field as known. Replacing one component of a paired UI state does not make its unknown sibling component observable. The at-most-one-focused-node invariant is validated after Player field rules are applied as well as before projection; a frame whose policies create multiple focused nodes is rejected atomically.

Snapshot, query, wait, revision/count metadata, diagnostics, and action authorization use the projected view. Query match objects omit `label` when the corresponding field rule omits it; omission is not serialized as an observed empty string. Player actions are revalidated both when queued and when consumed; a private and an unknown ID produce the same not-found result. Player diagnostics omit debug environment metadata and Debug/unscoped history, while retaining operations and events whose Player profile was captured at authorization time even if their former target is no longer in the current projection. Retained and pending Player action payloads use the field policy captured at authorization time; slider `set_value` payloads use `state.rangeValue`, while textual controls use `value`. A sensitive request also forces its correlated event payload to remain empty even if the host forgets to mark the result sensitive. Logs are empty. Screenshots are denied by default because rendered pixels cannot be projected semantically; a host may explicitly allow them only before starting the bridge, and transports cannot change that setting.

## Inspector Snapshots

The Inspector consumes four protocol payloads:

- UI tree: the current semantic UI snapshot, matching `ui-tree.schema.json`
- World Object Tree: the current host-authorized world snapshot, matching `world-object-tree.schema.json`
- Screenshot: the latest runtime screenshot, matching `screenshot.schema.json`
- Logs: ordered runtime log entries, matching `logs.schema.json`

Each pushed Inspector snapshot captures the UI and World Object trees under one runtime context lock, so both trees always report the same `sessionEpoch` even when another client resets the context concurrently. An Inspector assembling a snapshot through separate polling commands must compare both epochs and retry instead of publishing a mixed-epoch state.

The screenshot payload stores an already encoded `dataUri` plus `width` and
`height`. This keeps the C ABI small and avoids forcing the core protocol to own
PNG encoding, GPU readback, or platform-specific capture code.

`capture_screenshot` is explicit and on demand. The runtime queues and correlates
requests while the adapter owns viewport readback and PNG encoding on the next
drawable frame. Concurrent pending requests are coalesced into one capture and
receive the same image with distinct request IDs. Results include `sessionEpoch`
and `frameSequence`; `headless`, `rendering_disabled`, and `unsupported` are
distinct unavailable errors defined by `screenshot-capture.schema.json`; a reset
while queued produces `stale_session`. `get_screenshot` remains the latest-published-image
compatibility API. Screenshots can contain rendered secrets and are not redacted.

## Visual comparison and operation recording v2

Visual comparison is an opt-in consumer of the existing PNG screenshot payload;
semantic assertions remain the primary test path. Baselines use an explicit test
name and variant, are updated only by an API option or `GUA_UPDATE_BASELINES=1`,
and are never inferred from OS/GPU state. Masks are removed from both diff output
and the ratio denominator. Dimension mismatch never performs an implicit resize.

`recording.schema.json` is the source of truth. Version 2 adds mixed
`ui_action`, `semantic_game_input`, and `raw_input` steps while retaining version
1 read compatibility. Targets
prefer stable `id`, then strict role/name/scope selection, with current focus valid
only for key input and coordinate fallback only when explicitly recorded and
permitted. Diagnostics history includes monotonic elapsed milliseconds and the
revision observed at each entry; enqueued and consumed operation entries also
include complete action arguments. An enqueued operation can therefore be paired
with its observed completion without inventing timing or state metadata. Request
IDs deduplicate retained operation/event history. Replay
preserves delays or prefers recorded semantic wait conditions and waits for the
request-ID-correlated host completion of every semantic action. Sensitive steps
contain a `secretKey`, never plaintext, and require a caller resolver.

MCP and Inspector implement these features as protocol consumers rather than as
new C ABI runtime state. MCP artifact names are confined to its configured
artifact root. The browser Inspector uses explicit JSON/image import and download
instead of arbitrary local filesystem access. Both consumers must preserve the
recording redaction rule. Held input, explicit release, and request-correlated
host completion are recorded; automatic lease cleanup is runtime state and is
not emitted as a replay step. Replay always releases all game inputs in a
`finally` path after success, failure, or cancellation.

## Commands

Commands are external automation requests.

### Virtual clock (v1)

`clock_install`, `clock_pause`, `clock_run_for`, `clock_resume`, and `get_clock`
control the opt-in Gua virtual clock. Installation starts at `initialTimeMs` (zero
by default) in the running state. `clock_run_for` is accepted only while paused,
advances in `stepMs` slices (the installed default when omitted), and remains
paused. Clock time is monotonic, so installing an already installed clock is
rejected with `invalid_state`; reset the context before starting a new timeline.
Context reset also invalidates pending steps.

Only work explicitly scheduled on GuaClock or subscribed to its tick stream is
controlled. Engine time, physics, animations, audio, OS time, network time, and
engine-native timers are outside this capability. `clock_install` activates the
shared virtual clock but does not hook or replace those native time sources.
Game and adapter code must explicitly use GuaClock as the time source for every
subsystem that needs to be controlled. Adapters must continue their unscaled
bridge pump while the Gua clock is paused. An adapter must opt in to capability
`virtual_clock_v1` only after it implements that pump and consumes every clock
step; a bare runtime bridge does not advertise the capability. Remote
`clock_run_for` responses include `operationSequence`, `completionSessionEpoch`,
and `completionAfterFrameSequence`, captured atomically when the operation is
queued. After consuming the final step, the core acknowledges that operation
sequence on the adapter's next successfully published semantic frame. Consumers
wait for `completedOperationSequence` to reach their own operation sequence and
for the semantic frame to pass the correlated boundary. They do not use global
`pendingMs` as completion evidence because it may belong to a later client run.
An explicitly supplied `stepMs` must be positive; only an absent field selects
the installed default. A positive duration or step that cannot advance the
clock's finite-precision timeline is rejected with `invalid_duration` rather
than consuming work without changing `nowMs`. A remote `clock_pause` is acknowledged only after any
already queued running advance has been consumed and its host frame published;
the lower-level C ABI reports `invalid_state` if pause is attempted while such
work is still pending.

Initial command types:

- `get_ui_tree`
- `get_world_object_tree`
- `query_world_objects`
- `get_node`
- `click_node`
- `focus_node`
- `press_key`
- `set_value`
- `set_checked`
- `select`
- `scroll`

### Semantic game actions and raw input (v1)

`game-input-actions.schema.json` describes the host-registered action map. It
has its own monotonic `revision`, current `context`, and descriptors for
`button`, `axis1d`, `vector2`, and `text` values. Only explicitly registered
actions are visible; action maps are not inferred from engine assets or the UI
tree. Hosts validate IDs, active context, finite values, ranges, and holdability
both when enqueueing and when consuming requests.

Semantic commands are `get_game_input_actions`, `find_game_input_actions`, `press_game_input_action`,
`set_game_input_action`, `release_game_input_action`, `get_game_input_state`,
and `release_all_game_inputs`. Raw commands are `key_down`, `key_up`,
`press_physical_key`, pointer move/button/wheel operations, standard-mapping
gamepad button/axis/reset operations, and `text_input`. A `text_input` payload
and a semantic `text` action value are limited to 40 Unicode code points so the
stable v1 C ABI can carry the complete JSON string without truncation, including
escaped input.
The existing UI-tree `press_key` command is unchanged; physical keyboard input uses the supported
W3C `KeyboardEvent.code` values enumerated by `commands.schema.json` through
`press_physical_key`. This explicit cross-adapter subset includes standard
alphanumeric, navigation, left/right modifier locations, F1-F24, numpad
digit/operator, and PrintScreen codes; clients must not assume every optional
or platform-specific W3C code is available.

Descriptor v2 adds optional `category`, `aliases`, `tags`, and `agentExposure`.
Category follows the Action ID ASCII identifier and byte limits. Aliases and
tags contain at most 16 distinct, non-empty values of 1-64 Unicode code points;
metadata is compared exactly without Unicode normalization or case folding.
Aliases and tags reject embedded U+0000 before conversion to the stable
NUL-terminated v1 C ABI.
Selector query, context, and tag strings reject embedded U+0000 because the
stable v1 C ABI represents them as NUL-terminated UTF-8 strings.
`find_game_input_actions` accepts exact `id`, ordinal case-sensitive substring
`query` over ID/description/aliases, `valueType`, `active`, exact `context`,
exact `category`, all-of `tags`, and `limit`. Conditions are ANDed and Action IDs
are sorted ordinally. Results contain projected `revision`, actual `context`,
returned `count`, `truncated`, and `actions`, but never the total match count.
The public C++/.NET/MCP/WebMCP selector field remains `id`; the flat WebSocket
command encodes that field as `actionId` because its top-level `id` is reserved
for request correlation.
Player projection removes `private` actions before all filtering and truncation,
so result shape does not reveal their presence. Its independent revision does
not advance for private-only changes. Descriptor metadata is observable and
must not contain secrets.
Before dispatching `press_game_input_action` or `set_game_input_action`, clients
resolve the current descriptor and require `confirmed=true` when
`requiresConfirmation` is set. The confirmation decision travels through the
v2 C request descriptor and is revalidated against the current Action Map when
the host consumes the request; legacy v1 C requests are unconfirmed. Inspector
presents its local confirmation UI. Release operations remain available without
confirmation so stale input can always be neutralized.

Stateful operations use a 5000 ms lease when `leaseMs` is omitted and reject
values above 60000 ms. Their safety deadline begins when the host consumes the
request, rather than waiting for completion, and advances from unscaled elapsed
host-frame time, not GuaClock. Expiry queues neutralization even when completion
is delayed; a late completion cannot recreate the hold. Each WebSocket connection has a private owner ID; local C++ and
.NET callers create an explicit game-input session. Disconnect, lease expiry,
reset, and session disposal enqueue owner-scoped neutral cleanup for the next
host frame. Adapter shutdown synchronously neutralizes all injected host state
before runtime destruction; .NET adapters register that shutdown path when they
enable game input. Enqueue acceptance is not completion: clients
must wait for the matching request ID to be completed after injection into the
host input path.
If an owner disconnects after its request was consumed, the adapter may still
complete that in-flight request so its pump can drain normally. The core accepts
that late completion but does not recreate a hold or retain a result for the
released owner. It queues neutral cleanup immediately and, if that cleanup was
already consumed before the last ordinary in-flight request completes, queues a
final cleanup again so delayed host injection cannot remain active.
Non-strict reset preserves the same internal completion tombstone for consumed
game-input requests, suppresses their stale caller result, and repeats owner
cleanup after the final late completion.
Local C++ sessions use `result_json(requestId)` and .NET sessions use
`PollResult(requestId)` to distinguish pending, successful, and failed host
injection. A completed result is acknowledged and removed after a successful
full-buffer copy; implementations retain at most 1024 unacknowledged results per owner
and discard results owned by a released session.

Adapters advertise search additively as `semantic_game_input_search_v1` and only
initialized input paths from `semantic_game_input_v1`,
`raw_keyboard_input_v1`, `raw_pointer_input_v1`, `raw_gamepad_input_v1`,
`text_input_v1`, and `game_input_lease_v1`. Raw input is opt-in; an adapter with
no active pump and neutral-release path must omit the capability and return
`unsupported`.

Player/Public Agent game input is a separate, host-owned opt-in and is denied by
default. Enabling input for a local Debug inspector does not authorize the same
Semantic or Raw capability for WebMCP. The engine captures the Player profile
for page-owned requests and the runtime intersects the Player authorization with
the initialized adapter capabilities both at enqueue and immediately before the
host consumes the request. Transports cannot select or elevate that profile.
Neutral cleanup remains available for an already-created owner even when the
host later revokes its Player capability.

### Semantic action lifecycle (v1)

Semantic actions follow `enqueue -> consume -> host action -> observed event`. Enqueue acceptance only records a request; it is never completion. Each accepted request receives a monotonically increasing `requestId` that is not reused across session resets during the context's lifetime, and the adapter must copy that ID into its success or failure event after attempting the host operation.

The core captures `sessionEpoch`, `frameSequence`, and `revision` when the adapter emits completion; additive v3 event APIs and remote responses preserve that metadata even when polled later. Callers must still wait for the expected semantic state: action completion proves host processing, while `WaitForStateAsync` repeatedly obtains fresh snapshots until its predicate succeeds.

The v1 action names map directly to the additive C ABI action enum: `click`, `focus`, `set_value`, `set_checked`, `select`, `scroll`, and `press_key`. Enqueue validation distinguishes `node_not_found`, `hidden`, `disabled`, `unsupported`, and `invalid_value`. Existing click functions remain compatibility wrappers over the generic queue.

A caller that abandons an accepted action may cancel it by request ID while it
is still queued. Cancellation returns `cancelled`, `not_found`, or `in_flight`;
an in-flight request has already been handed to the host and must finish through
the ordinary correlated result path. A successfully cancelled request is never
later consumed if a node with the same ID reappears.

`sensitive=true` permits the adapter to receive the requested value, but event values, logs, diagnostics, and recordings must use an empty or redacted representation. After applying a sensitive value, adapters must also omit that control's plaintext `text` and `value` from subsequent semantic snapshots (and must not copy the plaintext into another semantic field such as `state.rangeValue`). `scrollUnit=0` means host pixels and `scrollUnit=1` means semantic lines. A key request may omit `nodeId` to target the host's current focus; when a node is provided it must expose `press_key`. In Player mode, a node-less key request is accepted only when exactly one currently projected node is explicitly focused, and that resolved target is reauthorized again before consumption.
Key modifiers use a transport-neutral bit mask: Shift is `1`, Alt is `2`, Control is `4`, and Meta/Command is `8`. Adapters must route both key-down and key-up through the host input pipeline and report success only after accepting the complete key gesture.
- `text_input`
- `move_gamepad`
- `wait_for_node`
- `get_screenshot`
- `capture_screenshot` with optional `afterFrameSequence`
- `get_logs`
- `get_diagnostics`
- `get_version`
- `poll_events`
- `get_context_status`
- `reset_context` with `expectedSessionEpoch`, optional reset `flags`, and optional `strict`

For the Inspector WebSocket bridge, commands are sent as JSON text messages with
a numeric `id`. Responses echo the same `id` and either include `result` or
`error`.

```json
{ "id": 1, "type": "get_ui_tree" }
{ "id": 2, "type": "click_node", "nodeId": "start" }
```

```json
{ "id": 1, "ok": true, "result": { "screen": "title", "nodes": [] } }
{ "id": 2, "ok": false, "error": "Gua node not found: start" }
```

Bridges may also push snapshots without a request. Inspectors should treat these
as authoritative runtime updates and refresh the visible panels immediately.

```json
{
  "type": "snapshot",
  "snapshot": {
    "uiTree": { "screen": "title", "nodes": [] },
    "logs": [],
    "screenshot": { "dataUri": "", "width": 0, "height": 0 }
  }
}
```

## MCP Tools

The v0.4 MCP server is a protocol consumer. It does not own runtime state and it
does not replace the Inspector bridge. By default it connects to the same Gua
WebSocket bridge at `ws://127.0.0.1:8765`; set `GUA_BRIDGE_URL` to target another
runtime adapter. The npm-ready CLI entrypoint is `gui-mcp mcp`, so a published
package can be launched with `bunx gui-mcp@latest mcp`.

MCP tools:

- `get_ui_tree`: returns the current semantic UI tree
- `get_world_object_tree`, `find_world_objects`, `wait_for_world_object`: observe the host-authorized World Object Tree
- `click_node`, `focus_node`, `set_value`, `set_checked`, `select`, `scroll`, `press_key`: invoke protocol v1 semantic actions
- `wait_for_node`: polls `get_ui_tree` until a node id appears
- `get_screenshot`: returns the latest screenshot payload
- `get_logs`: returns ordered runtime logs
- `get_diagnostics`: returns the versioned best-effort diagnostics snapshot
- `get_version`: returns `version.schema.json` for the components actually loaded. `godotPluginVersion` is retained for compatibility and is `null` outside Godot. Engine integrations publish their versions in the additive `adapterVersions` map (for example, `{"unity":"0.5.0"}`). Capability IDs are stable public identifiers; new IDs are additive.
- `start_recording`, `stop_recording`, `save_recording`: manage a client-local recording session and persist a recording v1 document
- `replay_recording`: resolves semantic targets and secrets, honors delay/condition timing, and waits for correlated completion
- `compare_screenshot`: compares an explicit PNG baseline and writes visual artifacts without implicitly changing the baseline
- `get_visual_artifacts`: lists the latest comparison manifest and artifact paths
- `run_test`: executes a small list of `wait_for_node` and `click_node` steps

The MCP server uses stdio JSON-RPC for MCP clients and the existing Gua
request/response WebSocket payloads for the runtime side. It may keep recording
and artifact metadata for its own client session, but does not own or duplicate
the game runtime's semantic state.

## Browser-native WebMCP

Web exports may expose semantic tools directly from the game page through the
experimental `document.modelContext.registerTool()` API. This is an additional
transport consumer, not a replacement for `gui-mcp` or the Inspector bridge. One
page calls one engine-owned in-page bridge; it does not open a WebSocket, select
a remote endpoint, or introduce a Gua session ID. Browser tabs remain isolated
by their ordinary JavaScript and engine instances.
Successful tool executions return the structured Gua result directly from the
WebMCP callback. They do not wrap serialized JSON in an MCP-style text content
block; browser serialization remains a transport detail.

The browser-safe surface shares the base tool names and semantics for
`get_ui_tree`, v1 semantic actions, `wait_for_node`, and optional
`get_screenshot` with `gui-mcp`. When the engine reports an initialized game
input pump and cleanup route, it also exposes the Issue 77 Semantic Game Action
and Raw Input tools matching the active capability set. Transport-specific definitions may add
requirements that do not cross the browser boundary; for example, `gui-mcp`
requires `secretKey` for every sensitive `set_value` so recordings can retain a
stable secret reference. The
in-page engine contract reads the current protocol UI tree, performs an action
and resolves only with its request-ID-correlated host completion, and may expose
the latest published screenshot. JavaScript validates live visibility, enabled
state, and the advertised action but never owns or recreates the semantic tree.
The engine boundary always projects UI and World observations as Player and
captures browser action and game-input requests as Player, even when the same local runtime
continues to serve Debug observations to an Inspector. Neither tool arguments
nor transport payloads may select or elevate an observation profile.

Feature detection is required. Missing WebMCP, a missing engine bridge, invalid
input, unsupported action, host failure, timeout, and cancellation are structured
errors. Browser action timeouts and caller aborts propagate to the engine-owned
in-page bridge, which cancels the correlated native request when it is still
queued before returning the structured error. If cancellation loses the race to
host consumption, the caller still receives its timeout or cancellation error
immediately, while the bridge continues request-specific polling until it drains
the correlated completion. `get_screenshot` is registered only after the engine supplies a drawable
frame readback path. Sensitive values may reach the host action consumer but are
blank in completion results and absent from logs and error details.

Each page bridge creates one internal game-input owner; that owner is not
exposed on the wire and is never shared across tabs. Game-input promises resolve
only after request-correlated host completion. Timeout, caller cancellation,
tool unregister, port replacement, and engine disposal release every semantic
and raw input held by that page owner. Semantic actions marked
`requiresConfirmation` are re-resolved against the current action map and reject
unless the current call supplies `confirmed: true`. Raw keyboard, pointer,
gamepad, and text tools are registered only for capabilities advertised by the
initialized host adapter.

## Events

Events are queued by the runtime core when adapters observe host UI input.
Language bindings should poll events instead of passing callbacks across ABI
boundaries. External commands such as `click_node` are requests first; adapters
consume them and emit events only after the corresponding host UI action has
been applied.
Requestless host-observed events remain available to Debug consumers. Player
consumers receive them only when the target belongs to the Player projection at
both emission and polling time.

Testing clients may combine enqueue and request-id-specific polling in one
convenience operation. Such helpers must not consume unrelated results or
ordinary events, must preserve sensitive-value redaction, and should report the
final screen/frame/revision when completion fails.

Initial event types:

- `click`
- `focus`
- `key`
- `text`
- `gamepad`

## Transport

The protocol should not depend on one transport. Early implementations may use
in-process calls, TCP, WebSocket, stdio, or MCP tool calls as long as the payloads
match the schemas.
