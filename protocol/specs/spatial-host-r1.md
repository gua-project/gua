# Spatial host r1 (#132)

This additive contract is `spatial-host-r1`. It does not modify the strict
`spatial-r1` documents from #131, enable existing Debug sessions automatically,
or expose a transport. Engine adapters (#133) and transports (#134) must opt in
separately. The core owns scheduling and correlation; the host owns physics,
collision filtering, and publication. A fake provider tests dispatch only.

## Ownership and authorization

The independent C ABI host has finite configured provider, owner, retained-batch,
query, hit, deadline and work limits. Every accepted batch occupies one slot until
its owner consumes its result or closes. Registration and owner handles are
monotonically allocated and never reused. Provider unregister invalidates leases.
Owner close disables polling and authorization immediately, but retains consumed
request correlation internally until completion/end; its backend completion is
acknowledged without publishing geometry. Closed-owner records occupy bounded
retention slots until the adapter ends its lease. Hosts synchronize
destruction with C callers; managed SafeHandle references protect concurrent
calls. No callback is invoked and no mutex is held while a host calls physics.

Provider registration contains a validated legacy provider descriptor plus
host-owned policies (ID, positive revision, allowed AABB), and optionally one
explicitly fully loaded AABB. This region must not approximate disconnected
loaded regions. Filters, trigger/backface and self exclusion remain host-owned;
no client layers or arbitrary exclusion IDs are accepted. Providers must filter
ineligible physics objects before detection, counting, sorting or enumeration.
Unregistered semantic objects may still be eligible collision objects. Hosts
must publish only safe opaque epoch-scoped references and authorized World IDs.

Owners have a positive session epoch, profile `Debug`, `Testing`, `Player` or
`PublicAgent`, explicit spatial enablement, policy grants and allowed region.
Only enabled Debug/Testing owners are authorized. The host installs these grants,
never a transport caller. Unknown and forbidden policy IDs both produce the same
`not_authorized` at the public enqueue/consume boundary, before support, space or
policy existence errors. Provider metadata discovery must apply the same grants.
Owner-scoped `describe` returns an `advertisement` document containing the
projected legacy provider descriptor with effective query/hit/deadline limits,
plus finite maxQueueDepth, queryDeadlineMs and maxBatchWorkTimeMs budgets.
Host registration and owner administration APIs are never transport commands.
Changes to owner grants or provider registration are rechecked before every
query and before result publication. A revoked result carries no geometry,
counts, samples, coverage or diagnostic values. Defaults for UI/World do not
change. These APIs confer no authority to change a runtime's existing profile.

The entire ray, sphere, capsule, oriented box and fixed-orientation swept volume
must fit both policy and owner AABBs. Endpoint shape bounds suffice for finite
translation through a convex AABB. Overflowing bounds fail closed. No clipping.
Floating-point shape bounds round outward conservatively, including sub-ULP
dimensions and displacements. Near-boundary oriented boxes may be conservatively
rejected; a rounded-inward bound must never admit an out-of-region volume.

## Batch and boundary lease

A batch document contains version/type, positive batchId, consistency, and 1..64
legacy request documents in order. IDs and query IDs are unique within a batch;
all share session epoch, space ID/epoch and consistency. The effective provider
and host limits apply before acceptance; absent maxHits uses the advertised
effective hit limit. Each query's deadline starts at acceptance.
Expiry applies to each query individually: a later still-live query remains
eligible after an earlier expiry. Pumping skips expired never-dispatched items;
an expired consumed completion is discarded before later work continues.
Queue capacity
includes queued, executing and unconsumed completed batches, preventing an
unbounded completion cache. IDs cannot be reused while retained by that owner.

The engine obtains a batch lease only inside its safe physics read boundary.
It supplies a fresh physicsSampleId, optional actual tick and optional World
snapshot captured in that same read boundary. The host records its steady-clock
ID and monotonic observation interval; a missing tick remains absent. The
adapter holds the physics read state until ending the lease. A lease is consumed
once, without crossing physics boundaries or resuming unfinished queries on
another tick. Reentrant begin is denied while a provider has a live lease.
Taking from an exhausted live lease returns NotReady (managed null), including
after its result has been polled; only an ended or invalidated lease is stale.

The adapter takes one ordered request, executes its engine API outside the core
mutex, and completes that request before taking another. Completion must match
all legacy correlation and geometric semantics. The core stamps sample metadata
and policy revision. A samePhysicsSample batch requires provider support and
one lease; bestEffort may also use one lease. Calling end before all items finish
terminates the remaining items truthfully. No receive thread may call physics.

Deadlines, cancellation, authorization and work budget are checked at begin,
before each take, at completion and at publication. Budgets use host steady time,
not physics ticks or client time. One backend query cannot be forcefully
interrupted; an over-budget completion is discarded, not relabeled a timely
success. Limits are cooperative bounds, never hard real-time guarantees.
At completion an already elapsed deadline/work budget is checked before
result-specific validation, so malformed late evidence cannot overwrite the
elapsed-time termination reason with `internal`.
Grant changes revoke individual affected queries. Still-authorized in-flight
work remains completable and later authorized items continue in order, skipping
revoked never-dispatched items. A selectively revoked consumed item keeps the
lease occupied until its correlated completion is discarded or the boundary
ends; a fully terminated batch rejects late completions as stale.
End preserves any specific stop reason already recorded, then terminates all
remaining items as `boundary_ended` and releases in-flight ownership. A partly
stopped batch cannot be leased again on a later boundary.
Redacting prior evidence releases its retained-byte charge, allowing later
authorized completions to use the reclaimed output budget.

## Results and compatibility

Host query results use the legacy result facts and correlation but version
`spatial-host-r1`; samples require physicsSampleId, clockId, observedFromMs,
observedToMs and queryPolicyRevision, with optional tick and synchronized World
snapshot. This is a new result type, never passed to the legacy validator as r1.
Legacy tick-required parsing remains unchanged; no fabricated tick is used.
Adapter `execution` documents carry the same result facts without any sample;
the core constructs the public sample from its boundary lease. Document types
4..11 are host result, batch, batch result, registration, owner, boundary,
execution and advertisement. Host documents have a 1 MiB input limit; legacy
document parsing retains its existing input semantics. Retained batch output
also has a 1 MiB bound; excessive backend evidence fails the dispatched item
and stops the remainder without publishing an oversized completion. Dedicated .NET DTOs
delegate authoring and reading to these native-validated immutable documents.

Batch results contain one ordered item for every requested query. Items carry
queryId, requestId, state (`completed`, `failed`, `notExecuted`), and either a
validated completed result or a safe reason. `failed` means dispatched without
a publishable success; `notExecuted` means never dispatched. Cancellation,
unregister, owner close, authorization loss, deadline, work budget and early
boundary end must preserve this distinction and retain prior valid completions
except when their authorization has been revoked. Late/duplicate completions
cannot overwrite a terminal item. The polling owner alone consumes its result.
After provider removal, polling redacts completed geometry as `failed` with
`provider_unregistered` while preserving existing terminal reasons for other
items. The owner's current grants are still checked independently; actual
owner authorization loss produces `not_authorized` instead.

Coverage complete requires successful execution and whole-query containment in
the registered loaded region. Otherwise complete is rejected. Providers can
report partial/unknown, never infer complete from noHit or World counts.
Truncation and nearest claims retain #131 semantics and are the provider's
engine-evidence obligations. Enumeration over the effective limit is rejected.
Scene/origin changes require unregister and registration with a fresh epoch.
Tick equality alone proves neither physics nor World simultaneity.

No bridge, MCP, Inspector, WebMCP, Trace or engine advertises spatial execution
through this change. Existing r1 offline consumers and runtime UI/World/input
paths are unaffected. #133 must supply real engine evidence; #134 must preserve
this ownership and authorization contract across transport correlations.

## Trusted prepared engine bounds

The additive C ABI `gua_spatial_host_check_engine_bounds` accepts a finite,
ordered binary64 AABB after Take and before physics, at most once per item.
The trusted adapter must conservatively include the original query and the
actual prepared engine shape with its full translation. The host requires
containment of its outward-rounded original bounds and the current owner and
policy grants. Failure grants no additional authority. Accepted bounds remain
bounded per batch and are reauthorized at completion, owner-policy revocation
and public polling; complete coverage must contain them as well. Older adapters
that do not call this function retain the original r1 behavior. This host-only
API adds no wire document fields, transport exposure or backend precision claim.
