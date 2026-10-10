# Spatial transport r1 / 空間照会の client・transport

## 空間照会を別プロセスから依頼する

実行中gameの物理世界を、.NET/WebSocket/Native MCPから読みたいときの接続案内です。
ここでclientが物理計算を再実装するわけではありません。
hostがscene・provider・Testing/Debugの有限grantを登録してbindし、
clientが公開能力を確認してbatchを送り、同じownerで結果をpollします。
ownerは要求と結果の接続上の所有者、spaceEpochは空間再生成の世代です。

[GuaRuntime.BindSpatial](../bindings/dotnet/src/Gua.Runtime/GuaRuntime.Spatial.cs)はhost側設定、
[MCP tool実装](../packages/mcp/src/index.ts)は外部要求と完了待機の入口です。
下のOperation mappingを使って同じ段階のAPIを選び、
[host契約](../protocol/specs/spatial-host-r1.md)と
[engine reader](spatial-engine-r1.md)へ進んでください。
通信断で結果を得られなかった要求を、新しい接続から勝手に再開・再送しません。
結果のunknown/partialや保存不能も成功へ読み替えません。

共通の環境準備とfixture/unit testの区別は[開発者ガイド](developer-reading-guide.ja.md)にあります。
実測対応表と過去のacceptance記録は、対象の環境と機能の範囲で参照してください。
browserのPlayer portには、このTesting/Debug照会を実行する権限はありません。

Issue #134 connects the #131 contracts, #132 native scheduler and #133 real-engine
readers. No physics is implemented in a client. World radius DTOs remain unchanged.
Only explicit host-authorized `Testing` / `Debug` grants work. The runtime's
existing Debug observation ceiling alone does not enable spatial reads.

## Operation mapping / 操作対応

| Logical operation | C ABI | C++ / .NET | WebSocket | Native MCP | Built-in WebMCP |
| --- | --- | --- | --- | --- | --- |
| Provider information | `gua_runtime_spatial_command`, `GUA_SPATIAL_INFO` | `SpatialClient.describe`, `GuaRuntimeSpatialClient.Describe`, `GetSpatialInfo` | `get_spatial_info` | `get_spatial_info` | **Unsupported** |
| Bounded query batch | `GUA_SPATIAL_ENQUEUE/POLL/CANCEL` | `enqueue/poll/cancel`, `Enqueue/Poll/Cancel`, `QuerySpatialBatch` | `query_spatial_batch`, `poll_spatial_batch`, `cancel_spatial_batch` | `query_spatial_batch` waits for the original batch | **Unsupported** |

Poll/cancel are lifecycle messages for the same read, not new physics operations.
Enqueue returns acceptance, never a fabricated complete result. The same owner
consumes each completion once; reconnect never resumes an old query. Deadline,
work, hit, queue and geometry bounds remain native host checks at consumption and
publication. Provider removal, scene/origin replacement and policy revocation
discard geometry. A successful runtime reset immediately closes the spatial
owners; rebind with the new runtime epoch and reconnect explicitly.

`gua_runtime_bind_spatial` / `GuaRuntime.BindSpatial` / Godot
`GuaContext.bind_spatial` are trusted in-process configuration APIs. They copy
validated grants, retain the same scheduler and invalidate old connection owners.
Configure before bridge startup. `DisableSpatial` / `disable_spatial` revokes
connections. A Player runtime cannot bind privileged grants. There is no wire
profile, registration, owner handle, collision-layer or arbitrary exclusion argument.
Host binding projects trusted grants without allocating a spare owner, so rebind
works at the configured owner capacity. WebSocket version discovery uses that
connection's actual owner; an ownerless connection advertises no spatial capability.
The .NET batch deadline includes shared gate acquisition, connect, send and receive.
Cancellation aborts the pinned socket immediately, including a stalled peer or
an unrelated request holding the gate, and never closes a replacement connection.

The canonical `spatial_read_r1` runtime capability and optional `version.spatial`
advertisement exist only while the bound registered provider is currently
discoverable under the host grants. The advertisement contains actual supported
operations, shapes, consistencies, backend/patch/precision, projected policy IDs
and effective finite limits. Offline parsing capabilities do not prove execution.
An unconfigured demo bridge has no spatial capability and cannot execute reads.

Schemas are in `protocol/schema/spatial-host-r1.schema.json` and
`commands.schema.json`. Run `node scripts/generate-spatial-tools.mjs` to regenerate
the schema used by Native MCP in `gua-world-tools`; it is separate from
`GuaWorldSpatialResult`. Package sources include this generated schema.

## Example (English)

1. Register `GuaUnitySpatial` or `GuaSpatialReader` with an explicit scene,
   space/epoch, collision policy and region. Use the measured backend descriptor.
2. Bind the same scheduler to the runtime using enabled Testing grants for the
   current session epoch. Start the normal Inspector bridge after binding.
3. Call `GetSpatialInfo()` / `get_spatial_info`; construct an Overlap or Sweep
   request with that space/epoch and a projected policy. Do not infer bounds.
4. Call `QuerySpatialBatch(batch, timeout, trace: trace)` / `query_spatial_batch`.
   Inspect every item's state/reason and completed result's coverage, truncation,
   missing fields and motion error. `unknown`, partial, unsupported or interrupted
   evidence does not establish a clear path. No automatic resend occurs.
5. Finalize the caller's Trace outcome and generate `GuaTraceReport.WriteHtml`.
   Inspect the generic `gua.spatial.batch.r1` attachment in the existing Viewer.

Raw terrain is readable only within explicitly authorized collision policies and
regions. A spatial query does **not** guarantee normal gameplay movement succeeds:
gameplay controllers, one-way rules, doors and time can change the outcome.

## 利用例（日本語）

1. scene / spaceEpoch / 衝突 policy / 許可領域をホストで明示登録し、実測済み
   backend の `GuaUnitySpatial` または `GuaSpatialReader` を作成する。
2. 同じ scheduler を現在の sessionEpoch の Testing 許可で runtime へ bind し、
   既存 bridge を開始する。Debug 設定だけでは照会を公開しない。
3. `GetSpatialInfo()` / `get_spatial_info` で能力と上限を確認し、公開された
   space/epoch/policy の Overlap / Sweep batch を送る。
4. 各 item の state/reason と coverage/truncation/未知情報を確認する。
   不明、未対応、途中停止は「完全に通れる」結果に置き換えない。再接続後に再送しない。
5. 呼出元の outcome で Trace を確定し、既存 Viewer の汎用 attachment 表示で
   sample、実効 policy revision、backend、失敗理由を確認する。

raw 地形は明示許可された policy と領域でのみ読める。空間照会は通常移動の
成功を保証しない。Recording / Replay の入力順序や Action 件数は変更しない。

## Trace and distribution / 証拠・配布

One batch creates one Lifecycle read step, never one Action per ray. Receipt,
terminal item states, request IDs and the native sample interval are correlated.
The bounded typed attachment holds the already received result plus the provider
advertisement. It contains no re-exploration command. Saved Viewer display proves
only saved evidence, not another live query. Storage omission/attachment-limit
quality is separate from backend query truncation. The receipt starts with storage
unconfirmed; successful storage has its own event. Any storage rejection uses the
bounded `capture.failure` marker for `gua.spatial.batch.r1`, or a correlated quality
summary when an event cannot fit. A missing marker never proves storage succeeded.
Caller failure remains the
Trace primary outcome even if detail capture stops; input Recording is untouched.

`examples/spatial-fixtures/client` uses only Gua.Testing/Core and creates a report;
`-p:GuaUsePackages=true` selects a local NuGet consumer. No Playtest types, build,
engine binaries, engine assets or credentials are dependencies of that consumer.
`scripts/verify-spatial-route.ts` exercises the real bridge plus built Native MCP.
The Godot fixture uses `transport.gd`; the Unity fixture builds a standalone Mono
Player with `SpatialTransportBuild.Build` and pumps in real FixedUpdate callbacks.
The existing Trace Viewer must be built with `bun scripts/build-trace-viewer.ts`
before packing Gua.Testing. No release or deployment is performed by these checks.

## Measured support / 実測対応表

| Engine / OS / backend | Native scheduler & bindings | WebSocket / Native MCP | Built-in browser privilege |
| --- | --- | --- | --- |
| Godot 4.7 stable / Windows x64 / GodotPhysics3D | Real packaged addon and host pump | TypeScript, built native MCP and isolated candidate NuGet consumer passed; common Trace/report generated | Unsupported |
| Godot 4.7 stable / Windows x64 / Jolt | Real packaged addon and host pump | TypeScript, built native MCP and isolated NuGet consumer passed; offline report generated | Unsupported |
| Godot 4.7 stable / Linux x64, macOS x64, macOS arm64 / GodotPhysics3D and Jolt | Real packaged addon and physics callback pump in each desktop CI lane | TypeScript, built native MCP and isolated candidate NuGet consumer passed; common Trace/report generated | Unsupported |
| Unity 6000.5.3f1 / Windows x64 / PhysX | Real standalone Mono Player and FixedUpdate pump | TypeScript, built native MCP and isolated candidate NuGet consumer passed; common Trace/report generated | Unsupported |
| Unity Linux/macOS / other engine patches/backends | Unverified for #134; no inherited transport acceptance claim | Unverified | Unsupported built-in privilege path |
| Godot Web / Unity WebGL | Existing ports project Player/PublicAgent | No authorized r1 Testing/Debug browser channel | Unsupported; tools absent |

The current engine/consumer execution record is [PR #168](https://github.com/gua-project/gua/pull/168)
and [parent #130](https://github.com/gua-project/gua/issues/130): tested commits,
commands, per-RID CI artifacts and unverified scope are recorded there. The
desktop Godot CI checks require three actual physics batches (TypeScript, MCP,
NuGet consumer) and a clean engine exit. Full 67-result geometry, 360-sample CPU
profiling and 34 lease-race reruns were measured on the pinned Windows engines;
those full-fixture measurements are not inherited by the other OS route checks.
Candidate package checks alone do not establish #129's declared engine routes.
Actual identified commit/version artifacts can satisfy its distribution
conditions; public publication is a separate future operation. See the
[distribution contract](distribution-contract.md) for the remaining route gates.
Report payload/HTML checks do not imply a new browser visual QA pass.

`guaSpatialBrowserSupport` reports Unsupported. Registering tool names or a fake
`document.modelContext` does not authorize physics. The existing browser UI/World/
Input ports remain public projections. A future privileged browser transport
needs its own host authorization and actual engine/browser verification.

Parent #130 evidence is mapped to #131 contracts, #132 scheduling, #133 engine
geometry/profiling and #134 routes/Trace/package acceptance. Distribution #129
remains open; neither this change nor the previous engine merge completes #130.
