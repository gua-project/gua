# Spatial-03 adapters and real geometry evidence

## engineの物理世界を読む担当

「この形を前方へ動かした範囲に壁があるか」を、Unity/Godotの実際の物理APIへ照会するadapterです。
clientは要求を作り、native hostが許可と待ち行列を扱い、engine readerが安全な読取境界で実行します。
spaceEpochはsceneや原点を作り直した空間の世代で、古い空間への結果を識別します。
leaseは一つの読取境界で有限batchを実行する権利で、終了時にEndで返します。

[Unity GuaUnitySpatial](../bindings/unity/Runtime/GuaUnitySpatial.cs)と
[Godot spatial_host.cpp](../native/gua-godot/src/spatial_host.cpp)が
準備した形・物理照会・完了報告の入口です。
許可とscheduleは[host契約](../protocol/specs/spatial-host-r1.md)、
外部からの接続は[transport案内](spatial-transport-r1.md)を読みます。
空結果でもcoverageが不明・結果がtruncatedなら、完全な空間や移動成功の証拠にはなりません。

下のReproductionにはengine path・固定fixture・backendが必要です。
[共通準備と実engine確認の区別](developer-reading-guide.ja.md)を読み、
幾何・性能・許可取消の既存結果は対象patch/OS/backendと合わせて確認してください。
測定値・件数・成果物は、以下の記録で確認できます。

`GuaUnitySpatial` reads its explicitly registered `PhysicsScene`;
`GuaSpatialReader` reads its explicitly registered `World3D` direct state.
Both are opt-in trusted host integrations over the native spatial-host-r1
scheduler, with ordered take/complete and mandatory End. Both readers continue after an individual consumed item becomes
terminal: they preserve the native reason, use at most one completion retry to
release retained correlation, and take the next eligible item in the same lease.
All-batch terminal states still stop and End; an already revoked lease needs no
second reclamation. They expose no runtime
transport capability. Existing strict spatial-r1 documents remain unchanged.
Host-installed enabled Testing/Debug grants are required; Player and whole
original shapes crossing a grant are rejected before dispatch.

Unity Pump requires its registering main thread and a host-approved synchronized
read interval held throughout one bounded batch. It never calls SyncTransforms,
Simulate, or changes physics globals. The geometry fixture syncs once after each
host mutation. Its Editor boundary omits tick and World snapshot. The runtime
profile uses normal FixedUpdate callbacks and likewise omits invented physics
ticks. Godot uses the main thread within `_physics_process` and records the
actual engine physics callback counter. Distinct sample IDs identify held read
intervals, not simultaneous samples across different calls or engines.

## Geometry and authorization

The readers implement rays, sphere/capsule/oriented-box overlaps and fixed
orientation translation sweeps, including diagonal motion and either box basis
handedness. Godot capsule height is endpoint distance plus twice radius; equal
endpoints use a sphere. Box full size is twice halfExtents. Three exclusively
owned reusable shape resources avoid editing live colliders. Unity uses its
corresponding PhysicsScene methods and finite reusable arrays (33..1024 slots).
Each sweep checks initial overlap first. Zero delta is an overlap-only query.
Godot preserves cast_motion native brackets with source and unknown error;
it never substitutes rest_info from a different target.

Godot spatial document serialization explicitly enables full precision so the
JSON writer cannot collapse tiny nonzero values before native validation.
Binary64 requests are converted to binary32 engine parameters. Both readers
compute a conservative union of original and prepared geometry, including the
full motion, and add a parameter-encoding guard of 64 binary32 epsilons times
max(1, maximum absolute bound). The additive trusted C ABI
`gua_spatial_host_check_engine_bounds` requires this volume to contain the
native outward-rounded original bounds, then checks current owner and policy
regions before physics. It retains the volume for complete/poll revocation and
coverage checks. Narrow grants are refused rather than expanded. This guard
bounds parameter preparation; it does not certify collision-kernel accuracy.
Godot rejects registrations claiming a representation other than binary32,
matching the pinned build and its parameter-preparation rules.
Coordinates/dimensions beyond 8192 units, nonfinite/collapsed parameters and
nonzero margins remain unsupported. Backend absolute error remains unknown.
Distinct capsule endpoints whose binary32 squared axis length is zero or
subnormal (below 1.1754943508222875e-38) are refused before physics. Godot also
checks the normalized axis remains unit length within 1e-6, preventing a scaled
capsule transform. Real-engine regressions use axis lengths 1e-25 and 1e-22.
Dimensions come from the request; inferred live-collider nonuniform scale is
not supported. The small-endpoint capsule regression (y=1 and
1.0000001192092896, radius=.25, owner min y=.74999998) must produce no geometry
under its narrow grant even though the original volume is authorized.

Only an explicit trusted loaded AABB can establish complete coverage, and it
must contain the entire prepared union. Missing/outside coverage is unknown;
an empty sweep then returns indeterminate. Overlap never proves penetration.
Anonymous hits retain missing reasons for position, distance, normal and public
identity, with relation unknown. No pointer, RID or instance ID is published.
Rays claim nearest only within returned hits. Buffer saturation and extra
results beyond maxHits remain truncated.

## Policies and lifecycle

Unity uses explicit layer masks and Ignore/Collide triggers. Self colliders must
use excluded layers; arbitrary post-query predicates are unsupported. Unity has
no per-query backface option: it verifies the selected policy against current
Physics.queriesHitBackfaces and refuses mismatches without physics or changing
settings. Godot uses explicit bodies/areas, masks, bounded host RID exclusions
and ray hit_back_faces. Shape queries use host obstacle backface settings;
hosts must install matching concave-shape settings as part of their registered
policy. Per-query changes to those obstacle settings are unsupported.

Dispose/unregister before scene destruction or origin changes and register a
fresh spaceEpoch afterward. Unity rejects invalid PhysicsScenes at Pump.
Automatic origin-shift detection is not supplied. A retained World3D reference
does not replace the host lifecycle obligation.

## Reproduction and measured combinations

Run `scripts/run-spatial-engine-fixtures.ps1` with the pinned engine paths.
Run again with `-GodotBackend 'Jolt Physics'` for Jolt. Optional GodotCppSource
can reuse the pinned godot-cpp checkout offline. Fresh native and managed code
is staged in isolated ignored artifacts projects. Previous evidence is deleted
before execution; missing records or nonzero engine exits fail the runner.
No Playtest dependency is used.

`protocol/fixtures/spatial-engine-r1.json` fixes versions, geometry dimensions,
expected occupancy, .001-unit tolerance, tick target and policies. Expectations
are analytical, not generated from adapter output. The triangular wall has
front face toward negative Z; the Godot and Unity constructors use their
respective front-face winding. Inside-start rays and exact floor contact are
explicitly backend-specific; neither may certify normals or penetration.

The Windows runs cover 33 shared scenarios, a second moving-door state, and
16-query batches: 67 result records per engine. An additional 34 lease-race records exercise
post-Take owner revocation for every geometry scenario and deterministic
post-Take deadline expiry once. The exact Execute/completion helpers used by
Pump release the terminal item, then run the eligible query against the actual
held engine state; its fixed outcome and same boundary sample are checked.
Unity reflection is confined to the fixture, with no production physics override
or test hook. The deadline fixture deliberately pauses the host for 120 ms after
taking an item with a fixed 100 ms deadline; it does not simulate physics.
They also assert independent
world/scene isolation, anonymous walls, Player denial, whole-shape grant denial,
prepared-geometry refusal, explicit trigger/self filters, saturation, unloaded
coverage, unregister and stale replacement epochs. Door old/new states have
distinct samples and Godot ticks. The measured inside-start ray returns hit in
both Godot backends and noHit in Unity, while originInside remains unknown.
Exact floor contact reports detected in all three, without claiming penetration
or a reliable normal.

| Combination | Evidence/support |
| --- | --- |
| Godot 4.7.stable.official.5b4e0cb0f / GodotPhysics3D / Windows x64 | Real primitive, general orientation/motion and static triangle front/back ray fixtures |
| Same Godot patch / Jolt Physics / Windows x64 | Same fixed fixtures and actual callback profile |
| Unity 6000.5.3f1 / PhysX / Windows x64 / Play Mode | Same fixed fixtures and runtime FixedUpdate profile |
| Other patch versions/OS/backend combinations | Unverified; no support claim |
| Static triangle overlap/sweep, arbitrary concave meshes and game-specific one-way logic | Unverified; no portable support claim; raw physics does not execute gameplay one-way callbacks |
| Nonzero margin, inferred nonuniform collider scale, arbitrary Unity predicate self filtering | Unsupported |
| Reliable normals, penetration depth, public collision/World IDs | Not supplied |

Godot concave shapes are hollow static triangle collections, not solid-volume
penetration certificates; see [official ConcavePolygonShape3D documentation](https://docs.godotengine.org/en/stable/classes/class_concavepolygonshape3d.html).
Measured triangle ray behavior does not certify all mesh or one-way combinations.
No completion of parent #130 is claimed.

## CPU, queue and game impact

Each engine profile runs 150 actual physics callbacks in each phase (baseline,
one query, 16 queries), discards the first 30 as warmup and retains 120 samples:
360 records per run. Every callback performs the same 2000 sine accumulations
as game work. Records include total callback wall time, pump wall time,
enqueue-to-pump wall time and actual callback interval. Phase main-thread CPU
and elapsed time are measured across the whole retained interval, including
engine work between callbacks. Windows GetThreadTimes has a 100 ns numeric
representation but coarse scheduler accounting; short deltas may be zero or
attributed at a later call. Do not interpret individual deltas as precise
per-query CPU. QueryThreadCycleTime also records actual thread CPU cycles for
each callback; cycles are never converted to time using a guessed frequency.
Phase CPU totals include engine and editor work between callbacks; compare them
alongside callback cycle counts and wall time, not as isolated adapter CPU.
This is a synthetic host workload, not a performance guarantee for another game.

The final full-precision serialization and capsule-underflow regression run
passed 67 results plus 360 callback samples for each combination. Four native
spatial suites and 102 managed spatial tests passed. The following callback
wall times are microseconds; raw evidence/logs remain under ignored artifacts.

| Engine/backend | Baseline median | Single median / p95 | Batch16 median / p95 |
| --- | ---: | ---: | ---: |
| GodotPhysics3D | 153.5 | 739.5 / 950 | 6130 / 7851 |
| Jolt Physics | 144 | 759.5 / 1009 | 5904 / 6823 |
| Unity PhysX | 18.2 | 597 / 869.6 | 5280.7 / 7223.7 |

Godot single/batch p95 callback intervals reached about 29.9 ms with normal
60 FPS render pacing; Unity reached about 16.7 ms. The configured 60 Hz target
therefore does not establish uninterrupted 60 Hz delivery. Queue timing starts
before enqueue serialization/validation and is an upper bound on core queue
residence. Geometry Godot singletons deliberately wait for the next normal
physics boundary. First-use/JIT and engine background work can change timings.

Engine calls are synchronous and cannot be interrupted. The native host checks
cooperative deadlines/work budgets between operations and discards late
results; it cannot guarantee a maximum frame stall. Host limits bound queue,
batch size, hits, retained results and dispatch work, not backend execution
time. Budget checks, revocation, End and owner cleanup are covered by native
and managed host suites; real engine fixtures preserve those boundaries.

| Engine/backend | Mean callback CPU cycles: baseline / single / batch16 | Whole retained phase main-thread CPU ms: baseline / single / batch16 |
| --- | ---: | ---: |
| GodotPhysics3D | 643843 / 2762477 / 23655329 | 31.25 / 171.875 / 765.625 |
| Jolt Physics | 595460 / 2856448 / 22622195 | 125 / 0 / 546.875 |
| Unity PhysX | 71817 / 2437274 / 20747119 | 1968.75 / 1953.125 / 1968.75 |

Each retained phase spans approximately 1.98 seconds. Unity batch-mode Editor
work keeps the main thread busy between callbacks, so phase CPU totals do not
isolate query costs. Jolt reports zero single-query phase CPU despite more callback work than the
baseline; this exposes the accounting resolution limit of GetThreadTimes;
callback CPU cycles still distinguish the workload. Baseline-subtracted median
callback wall overhead is about 0.59 ms for one query and 5.98 ms for batch16
in GodotPhysics3D; 0.62/5.76 ms in Jolt; 0.58/5.26 ms in Unity. These measure
adapter, serialization, enqueue and result consumption within the host workload.
For full primitive/triangle scenario timings, use the single/batch records,
not only this empty-space profiling scenario.
