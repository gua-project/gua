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
that requestId and the original guard. Native polling verifies epoch and profile
under the same lock as result consumption. UI pending is null; input pending is
`completed:false`. Terminal results are one-shot, request-correlated, and include
sessionEpoch; UI also retains nodeId, action, frameSequence, revision, succeeded
and error, input retains succeeded and errorCode. A stale poll is an uncertain
completion, not evidence of nonexecution. A different owner cannot poll another
owner's result; legacy UI poll paths cannot consume guarded UI results.

UI retention is bounded to 256 outstanding requests/results per owner; overflow
rejects without enqueue. Disconnect removes that owner's pending UI requests and
results and invokes existing held-input cleanup. Already-consumed UI requests
retain a host completion path; a later completion is discarded after owner loss.
Other owners and the host runtime remain alive. Existing input lease/reset
cleanup semantics remain authoritative.

Managed `GuaWebSocketContext.CreateGuardedDispatchSession()` creates a dedicated
connection and negotiates the capability. `GuaRemoteGuardedSession.SendUi` and
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
