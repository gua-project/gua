# Spatial-r1 contracts (Spatial-01 / #131)

## 空間照会の要求と結果を同じ意味で扱うために

「この線分に壁があるか」「この形が重なるか」を異なる言語・engineから扱うための
データ契約です。raycastは線分、overlapはその場の重なり、sweepは向きを固定した形の
有限な平行移動を問い合わせます。`r1`は契約の識別子で、製品release番号ではありません。

要求documentを作って検証し、hostのproviderへ渡し、戻った結果のID・空間・世代を
対応付けて検証します。providerは実際に物理照会するhost側の実装です。
spaceEpochはscene再生成や原点変更の境界、coverageはhostが確認した領域とpolicy内の
証拠範囲です。noHitやclearをゲーム全体の安全や移動成功に読み替えてはいけません。

[GuaSpatialDocument](../../bindings/dotnet/src/Gua.Core/GuaSpatial.cs)と
[native document検証](../../native/gua-core/src/spatial.cpp)はoffline読取・照合の入口です。
[SpatialTests](../../bindings/dotnet/tests/Gua.Selector.Tests/SpatialTests.cs)は契約fixtureの確認箇所で、
物理実行には[host契約](spatial-host-r1.md)と
[engine側読取](../../docs/spatial-engine-r1.md)を続けて読みます。
[開発者ガイドのローカル確認手順](../../docs/developer-reading-guide.ja.md)のSelector suiteはoffline条件を含みますが、engine精度や性能の測定ではありません。
以下の幾何・結果・ABI条件と既存evidenceを保持します。

`spatial-r1` is a contract identifier, not a product release. The source of truth
is `spatial-r1.schema.json` plus the semantic rules below. These are offline
documents, validation and type mappings. #132 implements provider registration,
authorization and safe physics boundaries; #133 measures engine geometry and
performance; #134 connects clients/transports and recording. This change runs
no physics, installs no provider and advertises no runtime capability.

## SP-01: geometry

Read-only 3D raycast, overlap and fixed-orientation finite translational sweep.
Sphere center/radius, capsule end-sphere centers pointA/pointB/radius, and box
center/halfExtents/three orthogonal unit basis vectors are explicit. Dimensions
are finite and strictly positive. Equal capsule endpoints mean a sphere.
The absolute encoding tolerance for squared unit lengths and pairwise basis dot
products is 1e-6. Either handedness is valid; no coordinate conversion is implied.
Normals and up use the same unit check. This dimensionless tolerance is not a
collision precision guarantee. NaN/Infinity, overflowing ray displacement or
sweep length, zero-length rays, rotation/mesh/2D fields and unknown fields are
rejected, never approximated. Zero-length sweep is overlap-only and its matched
result must have `motion: {type: "zeroLength", distance: 0}`, never travel blocked.

## SP-02: request and space

Coordinates use the corresponding World's host world unit. Provider worldSpace
is world3d; basis/up/unit label/optional metersPerUnit/precision describe that
correspondence. Meters, character collider identity and duplicate World position
registration are not assumed. Hosts change spaceEpoch on scene recreation or
origin shifts. Opaque collision references are scoped to that epoch. World IDs
are attached only if registered and publication is authorized; anonymous hits
remain usable. Native IDs/RIDs/pointers are not legal publication sources.
The offline validator cannot prove provenance; #132 must enforce it.

Request retains requestId/sessionEpoch correlation and adds queryId,
spaceId/spaceEpoch, queryPolicyId, kind, segment/shape/delta, deadlineMs,
consistency and optional maxHits. Sequences are exact JSON safe integers:
positive IDs/epochs, non-negative ticks/revisions. IDs are non-empty Unicode
scalar strings, exact/case-sensitive, passed as length-delimited UTF-8.
Duplicate JSON keys are rejected. The enclosing transport retains its existing
request/response correlation and error envelope.

deadlineMs is a positive finite budget starting at host acceptance in monotonic
unscaled time, not a client wall-clock timestamp. It does not promise forced
interruption of one native call. #132 must bound queue time and execution and
report deadlineExceeded. Policy IDs select host-owned filter/trigger/backface/
self-exclusion/region settings. Client masks, arbitrary exclusions and profile
fields are forbidden.

Provider metadata lists operations, shapes (for overlap/sweep), policies,
consistencies, engine/backend names and versions, precision, and limits:
1..64 queries/batch and 1..32 hits/query; host limits may be lower. Unknown
versions/errors must be labeled unknown. Absent maxHits selects the provider's
configured limit (at most 32); an explicit limit cannot exceed that limit.
bestEffort retains individual samples; samePhysicsSample requires one batch
sample, otherwise UnsupportedConsistency (`unsupported_consistency`).
check_request checks matching space/epoch, support and limits. It is not host
authorization, loaded-region verification, deadline consumption or scheduling.

## SP-03: result facts

Status is completed/rejected/unsupported/failed/cancelled/deadlineExceeded.
Non-completed results carry only correlation and safe typed error codes; they
never carry geometry/noHit/clear. Unsupported operation/shape/policy/consistency
are distinct error codes. Completed outcomes are:

| Query | Outcomes |
| --- | --- |
| Raycast | hit / noHit / indeterminate |
| Overlap | detected / notDetected / indeterminate |
| Sweep | initialOverlap / blocked / clear / indeterminate |

Sweep requires independent initialOverlap detected/notDetected/indeterminate.
Detected forces initialOverlap; unknown forces indeterminate. A collider cannot
be ignored to turn initial overlap into clear. clear requires notDetected and
complete coverage. Hit relation contact/penetration/unknown is independent;
contact alone does not establish an invasion bug. Detection can have no details.
truncated describes enumeration, not whether collision was detected. noHit/
notDetected/clear require empty hits and no truncation. Failed/unloaded queries
must not be relabeled completed noHit. A successfully executed ray can retain
local noHit with coverage unknown; this does not prove the absence of obstacles
outside its available evidence. No result guarantees normal movement success.

Ray nearest is none/returnedHits/policyRegion. returnedHits guarantees only the
nearest enumerated hit. policyRegion requires complete coverage and guarantees
the nearest eligible collision within the declared region/policy even if later
hits are truncated. Tie ordering is unspecified. originInside is yes/no/unknown;
unknown internal-start semantics cannot establish floor support.

coverage complete requires an explicit loadedRegion AABB with min <= max.
It means completeness inside the host-declared loaded physical region AND
policy, not game-wide safety or sampling coverage. The provider must ensure the
entire query fits this region and policy at execution (#132). Disconnected or
partially loaded regions cannot be overclaimed by their bounding AABB; use
partial/unknown with reason instead. Offline validation checks structure only.
sample records physicsSampleId/tick/observedAtMs (host monotonic ms). Optional
World sessionEpoch/frameSequence/revision is attached only if independently
synchronized, not inferred from matching ticks. Match check enforces its epoch.

Each optional hit position/distance/normal/collisionRef/worldObjectId is either
present or has an exclusive reason in missing. Absence is not null, zero normal,
or an invented contact point. Reasons and IDs must be safe under the publication
policy. Dedicated DTO absence is separate from common Value's null prohibition.

Optional motion is tagged nativeBracket(safeFraction <= unsafeFraction in [0,1])
or nativeEstimate(distance and/or fraction). Preserve source and error:
{state: known, absolute} or {state: unknown, reason}. Absolute error is in
fractions for bracket; for estimate it uses distance units if distance exists,
otherwise fractions. Never convert an estimate to a rigorous safe bound, or
require motion evidence to decide clear versus non-clear. Matched estimate
distance cannot exceed delta length. Distance/fraction relations are native
evidence, not recomputed. zeroLength is the separate overlap-only tag.
clear with a nativeBracket requires both fractions to be 1; a native unsafe
boundary partway through the delta contradicts clear. A ray hit distance is
measured from the segment origin and cannot exceed its finite segment length.

## SP-06: version, ABI and .NET

`gua/spatial.h` adds independent immutable owned document handles, v1 options
(uint32 struct_size + int32 document_type), error (int32 code + char path[128]),
length-delimited UTF-8 input and JSON copy/destroy/check APIs. Exact v1 size is
required. Future layouts need a new API; future schema versions return Version.
Dedicated schema JSON carries all fields losslessly (decimal lexemes retained);
no arbitrary object is added to common Value or existing stable ABI structures.
Input storage is copied. C callers synchronize destruction with readers.
Short output buffers are cleared; required bytes include NUL; invalid buffer
arguments return 0. Failure clears the output handle and catches C++ exceptions.
Errors contain safe protocol paths, never submitted values/unknown keys.

| Contract | C ABI | C++ | .NET |
| --- | --- | --- | --- |
| request/result/provider | opaque document + type 1/2/3 | SpatialDocument | GuaSpatialDocument + authoring DTOs |
| vector/basis/shape/segment | schema JSON, finite binary64 | native-owned JSON | GuaSpatialVector/Basis/Shape/Segment |
| metadata | exact safe integers, UTF-8 strings | same native validation | long/string, native range validation |
| optional facts | absent fields + reasons/tags | lossless copy | nullable properties, omitted on serialization |
| matching | check_request/check_result | same methods | CheckRequest/CheckResult |
| lifetime | explicit destroy | move-only RAII | SafeHandle retained during calls |

.NET authoring DTOs are mutable copies; FromRequest/Result/Provider serializes
and native-validates an immutable document. Read methods return independent
typed copies. net10.0 and netstandard2.1 delegate via P/Invoke, with gua or
__Internal under GUA_STATIC_LINK. No second semantic implementation is added.
Native-validated integral decimal/exponent metadata (1.0 / 1e0) is decoded into
managed integers without precision loss. Typed authoring copies normalize
number spelling while native JSON copy preserves original decimal lexemes.
Status codes 1..7 map to Invalid/Version/Geometry/Context/Unsupported/Semantics/
Internal. Validator Unsupported is distinct from an executed unsupported result.
Nonfinite authoring doubles are rejected by System.Text.Json before native parse.

`spatial_read_r1` is the canonical additive runtime capability. Supporting hosts
negotiate it plus provider r1 metadata before dedicated typed reads. Offline
validation does not advertise it. The #132/#133 host pump and readers plus the
[#134 transports](../../docs/spatial-transport-r1.md) advertise it only after
explicit host authorization and provider binding. Existing clients do not require
it. Spatial-r1 is explicitly enabled Testing/Debug only, denied for Player/
Public Agent. Old default profiles, abiVersion/protocolSchemaVersion, World/
Input schemas, Observe sources/values and Trace remain unchanged.

## Acceptance mapping and evidence

Shared spatial-r1 fixtures are consumed by native C++ and .NET. C tests verify
layout/import/error handling; Ajv verifies schema-expressible cases. Semantic
cases (orthogonality, ordering inequalities, exact decimal integers, duplicate
keys) are marked schemaReject false. Existing four-RID native/.NET CI picks up
the tests; the Value Bun suite picks up schema tests. Legacy tests remain active.

| #131 acceptance | Decision | Evidence |
| --- | --- | --- |
| schema/mappings + normal/rejected/unsupported/unknown | SP-02/03/06 | valid/invalid fixtures and typed roundtrips |
| NaN/Infinity, dimensions, basis, spaces/epochs, zero/unsupported | SP-01/02 | invalid + requestChecks/resultChecks |
| initial/travel, contact/penetration, hits/truncation/coverage | SP-03 | independent facts and semantic rejections |
| bracket/estimate, missing normals/refs | SP-03 | anonymous/truncated bracket and estimate fixtures |
| legacy World/Input and old clients | SP-06 | unchanged schemas, existing tests, additive version check |

Fresh prerequisites #72/#73/#98/#118 were closed/completed at implementation
start on main 60c903b3b218310cc2bf13d2d583fdcc987dee51. Contract tests are not
adapter execution, engine precision, performance, distribution or Trace proof.
