# Guarded remote dispatch v1

`guarded_dispatch_v1` is additive. Clients must negotiate the capability before
sending `guarded_` verbs. Existing commands, clients, and local guarded game-input
APIs retain their behavior. A bridge without the new handlers must reject the
guarded verbs; it must never fall back to a legacy enqueue.

Prefix the existing UI or input verb with `guarded_`, and supply all three root
metadata fields: `expectedSessionEpoch` (nonzero), `expectedProfile` (0 Debug,
1 Player), and `expectedRevision`. Integers are unsigned decimal wire integers,
not strings, fractions, exponent notation, or nested payload metadata. Duplicate
keys are invalid. The connection/host profile remains authoritative: an expected
profile is a comparison, never an authorization grant. The native runtime holds
its context lock across profile/capability verification and core guarded enqueue.
Every guarded command also requires a positive Int32 transport `id` so its reply
can be correlated. Missing, fractional, string or out-of-range IDs reject before
enqueue. Required payload fields, types, bounds and verb-specific allowed fields
are checked before dispatch; absent fields never become default host inputs.
As in the legacy text-input schema and core, an explicit empty `text` string is
valid; a missing or non-string field still rejects before enqueue.
Escaped U+0000 is also valid inside JSON text values for Semantic Set and Raw
text input, as in the legacy/core contract. UI key modifiers use the full
native uint32 range, 0 through 4294967295, without signed conversion.
Raw text input accepts the optional nonempty `secretKey` replay reference;
the managed request can forward it. As with the legacy bridge, the host receives
the supplied text: secret resolution belongs to the caller/replay tooling.
This reference does not grant profile authority or trigger a secret lookup.

Epoch/revision counters do not identify a host. A wire client observing through
another connection must capture Observe sourceId with the observation, verify
that identity on the dispatch connection before enabling sends, and keep that
connection pinned. A native bridge connection serves one core context with an
immutable Observe sourceId. The managed session enforces this identity binding;
a source comparison grants no profile authority.

UI revision is the published UI tree revision for the authoritative profile.
Semantic input revision is that profile's Action Map revision, returned by
`get_game_input_actions`. As in local `guarded_v2`, Raw input requires the epoch
and capability checks; its expectedRevision is not compared to the unrelated
semantic Action Map. All guards and current visibility, permissions, confirmation
and capabilities are rechecked before the host receives a consumed request. UI
stale guards return `stale_guard` (-7 at the C ABI/completion); existing native
game-input stale guards retain `invalid_argument` (-1). No side effect is inferred
from an enqueue receipt or successful host input completion.

Supported UI verbs: click_node, focus_node, set_value, set_checked, select, scroll,
press_key. Supported input verbs: press_game_input_action, set_game_input_action,
release_game_input_action, key_down, key_up, press_physical_key, pointer_move,
pointer_button_down/up, pointer_wheel, gamepad_button_down/up, set_gamepad_axis,
text_input. Cleanup and reset are host/owner lifecycle operations, not guarded
player verbs. Existing payload validation applies; this extension adds no command
tunnel and no authority based on a query profile. Private and nonexistent targets
retain the same profile-projected errors.

Each connection owns a native input owner and its guarded UI requests. Receipts
carry `requestId`. `guarded_poll_action` and `guarded_poll_game_input` require
that requestId and the original guard. The connection retains each accepted
request's original epoch/profile/revision; a mismatched poll is rejected before
calling the consuming host poll, including Raw revision metadata. Native polling verifies epoch and profile
under the same lock as result consumption. UI pending is null; input pending is
`completed:false`. Terminal results are one-shot, request-correlated, and include
sessionEpoch; UI also retains nodeId, action, frameSequence, revision, succeeded
and error, input retains succeeded and errorCode. A stale poll is an uncertain
completion, not evidence of nonexecution. A different owner cannot poll another
owner's result; legacy UI poll paths cannot consume guarded UI results.
Legacy input polling on the same connection rejects a guarded request with
`guarded_poll_required`, preserving its result for the original guarded poll.

The connection guard table is bounded to 256 outstanding UI/input requests/results
combined per owner; UI core retention is bounded to 256 across all owners,
including consumed requests awaiting host completion after disconnect. Overflow
rejects without enqueue. Disconnect removes that owner's pending UI requests and
results and invokes existing held-input cleanup. Already-consumed UI requests
retain a host completion path; a later completion is discarded after owner loss.
Other owners and the host runtime remain alive. Existing input lease/reset
cleanup semantics remain authoritative.

Managed `GuaWebSocketContext.CreateGuardedDispatchSession(observedSourceId)` creates a dedicated
connection, negotiates the capability and compares its existing Observe sourceId
with the original observation connection and supplied observed identity before
enabling sends. The optional argument defaults to the original connection's
current sourceId; callers with saved observations should supply their captured
sourceId. The dedicated generation is pinned and never reconnected for dispatch.
`GuaDispatchGuard` carries the observed SourceId, epoch, profile and revision;
a different source is locally Rejected before dispatch. Obtain UI sourceId,
sessionEpoch and uiRevision together from the observation snapshot's document,
not from independent observations across reconnects. Semantic guards use the
map read through the pinned session and its verified SourceId. This does not add
a Raw map revision comparison or an identity namespace distinct from Observe.
`GuaRemoteGuardedSession.SendUi` and
`SendGameInput` return a `GuaRemoteDispatchAttempt`: Rejected, Enqueued, Completed,
or Uncertain. A receipt lost after send, malformed receipt, lost/malformed poll,
or stale completion poisons and disconnects the session. There is no automatic
resend or repeated uncertain poll. Terminal attempts retain their evidence.
Dispose releases only the session's owner. Local serialization failure precedes
dispatch. Consumers supply their own finite action-attempt ledger/deadline and
must not repeat an execution after an uncertain outcome with a new session.

These APIs close the cooperative enqueue-to-consume race; they do not prove a
game outcome or make host execution transactional after consume. The host still
owns engine-thread execution and completion. No Godot/Unity engine acceptance,
Playtest acceptance change, merge, release or package publication follows from
these tests or this extension.
