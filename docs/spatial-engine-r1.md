# Spatial-03 engine prototype and real geometry evidence

This branch is a bounded, opt-in engine prototype for #133, not completion of
the spatial feature. No runtime transport advertises spatial execution. Existing
UI, World, Input, Observe, Trace and strict legacy spatial-r1 APIs are unchanged.

`GuaUnitySpatial` reads only its explicitly registered `PhysicsScene`.
`GuaSpatialReader` reads only its explicitly registered `World3D` direct state.
Both use the native spatial-host-r1 scheduler, ordered take/complete calls and
mandatory lease End. The host creates enabled Testing/Debug grants independently;
the reader cannot grant client authority. Player requests and entire shapes
crossing an owner region are rejected by the native host before physics.

Unity requires the host to call Pump on the registering main thread in an
approved synchronized interval. It never calls SyncTransforms or Simulate.
The fixture calls SyncTransforms once after host obstacle creation/mutation,
then holds the single-threaded read interval while Pump executes one batch.
It omits tick and World snapshot because the Editor callback does not establish
either. Godot requires the main thread inside a physics frame; its fixture uses
`_physics_process`. It reports the actual engine physics callback counter, with
no inferred World snapshot. Sample IDs identify each held read interval, not a
claim that different intervals sharing a tick are simultaneous.

Unity filters with host layer masks and explicit Ignore/Collide trigger flags.
Self colliders must occupy excluded layers; arbitrary post-query predicates
are unsupported because the host contract requires prefiltering. Unity lacks a
per-query backface flag: the reader checks that the explicitly selected host
backface value equals the current global value and refuses mismatches without
querying or changing global state. Godot uses explicit masks, bodies,
areas, backfaces and bounded host RID exclusions before detection. Shape margin
must be zero so it cannot enlarge the authorized volume. Three reusable Godot
shape resources belong exclusively to each reader; no live collider is edited.

Overlap detection never establishes penetration or an exact contact normal.
The prototype publishes anonymous hits with explicit missing reasons for all
details. Godot sweep first performs overlap, then uses cast_motion fractions
with source and unknown backend error; it never substitutes rest_info from a
different collider. Capsule height is endpoint distance plus twice the radius,
and equal endpoints use a sphere. Box sizes are twice halfExtents. Unity uses
the corresponding overlap and cast APIs and a bounded reusable result array.
Full buffers remain truncated; available hits never establish policyRegion
nearest. No collision pointer/RID/instance ID becomes a public reference.

The loaded AABB is a trusted host declaration, never inferred from empty World
metadata or empty queries. Readers conservatively classify containment with a
0.001-unit inward slack; the native host repeats its authoritative outward
containment check at publication. Outside/missing declarations remain unknown;
an otherwise empty sweep then returns indeterminate, not clear. The slack is
a conservative coverage classification rule, not measured collision precision.
Provider precision is binary32 with unknown absolute backend error.

Exactly representable capsule endpoints are insufficient: derived midpoint,
endpoint distance and total height must also retain their engine representation.
The audit regression uses endpoints y=1 and y=1.0000001192092896, radius=0.25,
and an owner minimum y=0.74999998. The native host accepts the original volume,
whose minimum is 0.75, while a rounded Godot center/height would reach below
the grant. Both readers now reject this query before physics; the real-engine
fixtures assert a failed unsupported_shape item with no result geometry.

Before destroying a scene or changing origin, the host must Dispose/unregister
the reader and register the replacement with a fresh spaceEpoch. Unity also
rejects invalid PhysicsScenes at Pump. Automatic origin-shift detection is not
provided. Retaining a Godot World3D reference does not substitute for this host
lifecycle obligation.

## Reproduction and acceptance limits

Run `scripts/run-spatial-engine-fixtures.ps1` with the pinned Godot and Unity
executables. Optional GodotCppSource points to the repository's pinned
godot-cpp checkout for offline builds. The script builds current native and
managed code, stages isolated projects under artifacts, then executes real
Godot and Unity Play Mode fixtures. No external Playtest dependency is used.
Engine versions, backend labels, OS, tick target, margin and tolerance are fixed
in `protocol/fixtures/spatial-engine-r1.json` before execution. Occupancy
expectations come from the fixed box geometry, not adapter output. Internal
start rays and exact boundary floor contact intentionally have backend-specific
detection, but must preserve unknown origin/normal/relation facts.

The real Windows runs cover 20 shared scenarios plus the second state of one
moving door, 16-query batches, independent loaded worlds/scenes, Player denial,
whole-shape region denial, unregister and stale replacement epochs. Obstacles
have no semantic World registrations, so anonymous walls remain eligible.
Door transforms change only at approved host points; old/new results carry
different physics sample IDs (and different Godot physics ticks).

| Combination | State |
| --- | --- |
| Godot 4.7 stable / GodotPhysics3D / Windows x64 / fixture primitive boxes | Real fixture evidence |
| Unity 6000.5.3f1 / PhysX / Windows x64 / Play Mode / fixture primitive boxes | Real fixture evidence |
| Exactly binary32-representable coordinates/dimensions, cardinal capsules and motion, signed cardinal box bases, coordinates/dimensions within 8192 units | Prototype subset |
| General diagonal motion, arbitrary box/capsule orientations, silently rounded binary64 geometry | Unsupported in this prototype |
| Godot nonzero margins; Unity mismatched global backfaces | Unsupported |
| Mesh obstacles, one-way collision, Jolt, nonuniform inferred collider scale, other OS/patch versions | Unverified; no support claim |
| Reliable normals, penetration depths, public collision/World IDs | Not supplied |
| General geometry precision/authorization design and full game-impact profiling | Remaining #133 work |

Each fixture records pump wall time and enqueue-to-pump wall time for single
and 16-query batches. Unity also records coarse **whole-process** CPU deltas;
these include other Unity threads and may quantize to zero, so they are not
per-query CPU precision. Godot per-query CPU and baseline-subtracted gameplay
impact are not yet measured. A 60 Hz setting is not proof of sustained 60 fps.
One synchronous engine query cannot be interrupted; deadlines are cooperative.
Native host deadlines discard late evidence and stop remaining work; these
budgets do not make engine calls hard real-time. Do not close #133 or merge this
prototype as accepted until the remaining acceptance work is addressed.

## Measured Windows checkpoint (2026-10-01)

The current-source reproducible runner passed **41 records in each real
engine**. Four native spatial suites passed, followed by **101 managed
SpatialTests/SpatialHostTests** using GUA_NATIVE_DIR for the freshly built DLL.
The raw engine results/logs remain in ignored artifacts; generated output is
not committed. No fixture tolerance was relaxed after a failed geometry test.

| Engine | Queries/batch | Timed records | Upper-median pump wall µs | Maximum pump wall µs | Upper-median enqueue-to-pump wall µs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Godot 4.7 / GodotPhysics3D | 1 | 21 | 433 | 786 | 19628 |
| Godot 4.7 / GodotPhysics3D | 16 | 20 | 2963 | 4158 | 897 |
| Unity 6000.5.3f1 / PhysX | 1 | 20 | 340.7 | 4050.5 | 27.2 |
| Unity 6000.5.3f1 / PhysX | 16 | 20 | 3303.5 | 5137 | 256.9 |

These are one local fixture run; the preliminary unsupported-geometry regression
now warms host serialization before the timed geometry queries. Unity's separately
recorded door-open state has no timing entry. Queue measurement begins before
enqueue authoring/validation and is an upper bound on core queue residence;
Godot singletons intentionally wait for the next normal physics boundary.
An earlier run before that warmup observed a 20071.4 µs Unity singleton maximum,
above a nominal 60 Hz interval. These are synchronous cooperative calls and
local timings, not a sustained frame-rate claim.
Read the remaining profiling and geometry limitations above before treating
this checkpoint as acceptance.
