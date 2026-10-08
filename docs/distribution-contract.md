# Distribution contract for external consumers

## 外部consumerが同じ成果物を使えるようにする

Gua checkoutのない別projectで、native library・schema・offline Viewerを使うための契約です。
packageがrestoreできても、engine起動や操作まで成功したとは判断できません。
まず版・source commit・hashを固定し、packageだけのconsumerでload/validation/reportを確認し、
必要なengineを起動・接続して実際の操作の完了を確かめます。
RIDはOS/CPU別の資産を選ぶ識別子です。

[GuaDistribution.ReadSchema / ValidateJson](../bindings/dotnet/src/Gua.Testing/GuaDistribution.cs)は埋込resourceの入口、
[verify-distribution-consumer.ps1](../scripts/verify-distribution-consumer.ps1)は
隔離したfeed/cache・archive・Toolの確認を担当します。
下の実行例には、事前に作った正確なpackage feedとfull commitが必要です。
確認準備と確認の段階は[開発者ガイド](developer-reading-guide.ja.md)、
native buildは[toolchain文書](native-toolchains.md)を参照してください。
今回このconsumerコマンドは未実行・未検証です。

以下のacceptance表・hash・過去のroute記録は検証証拠です。
それぞれの対象commit・環境・機能の範囲で読み、現在の全engine対応や公開済み版の保証へ広げません。
公開・version選択はacceptanceと別の操作です。

This contract supports a separate consumer through packages and artifacts. Gua
does not reference Playtest or build its product. A consumer must pin the package
version, full source commit and artifact hashes; runtime startup never retrieves
the latest schemas or rebuilds the Viewer.

## Artifact acceptance and separate publication

`0.0.0-ci` is the existing private CI candidate identifier. It is meaningful only
with a full commit and SHA-256 manifest, and must never be uploaded as a public
release. The source default `0.5.0-preview.3` and existing GitHub releases do not
select the next release version or channel. No tag or publication is needed to
run artifact acceptance. Issue #129 is judged against actual artifacts built
from an identified target commit/version, including retained hashes and evidence
for each declared execution route. The existing commit-pinned `0.0.0-ci`
candidate can identify that validation target; it does not select a new public
release version. Public version/channel selection and publication are separate
future operations, not prerequisites for #129 closure. If publication is later
authorized, repeat acceptance on the actual published packages and archives.
A locally staged addon alone does not prove the contents of an archive.

## Package and resource APIs

| Consumer need | Distribution/API | Acceptance |
| --- | --- | --- |
| Native C ABI / managed context | `Gua.Core`, `Gua.Runtime`, `runtimes/<rid>/native` | Load both native libraries from restored packages; verify `buildId` equals the source commit |
| Protocol schemas | `Gua.Testing`, `schemas/*.schema.json` and `schemas/trace-observe-semantics.mjs` | All source schemas ship unchanged; `GuaDistribution.SchemaNames` and `ReadSchema` expose embedded JSON schemas |
| Static offline validation | `GuaDistribution.ValidateJson` | Embedded registry resolves package-local references; missing references fail locally; no native library, engine, network or Codex is required |
| Trace read and generated Viewer | `GuaTraceReader.Read`, `GuaTraceReport.WriteHtml` | Read the Trace and emit self-contained HTML from the embedded Inspector React bundle; no Node/Bun/web build is needed by the consumer |
| Viewer identity/notices | `GuaDistribution.ViewerMetadata`, `ViewerLicenses`, `License`; `trace/version.json`, `Viewer.LICENSES.txt`, `LICENSE` | Bundle hash/source commit plus React, React DOM and Scheduler MIT notices |
| Recording / engine clients | `Gua.Testing.Recording`, `Gua.Testing.Godot`, `Gua.Testing.Unity` | Exact package references restore together in the external consumer; engine execution remains a separate test |

JSON Schema validation checks structure, not Recording replay timing, secret
resolution, cross-event Trace semantics or host feature support. Use the existing
Recording and Trace readers for their semantic checks and the packaged
`trace-observe-semantics.mjs` where the Observe contract requires it.
`GuaDistribution.ObserveSemanticsScript` exposes that exact embedded helper for
an external archive without locating a Gua checkout.

## Initial RID and route matrix

| RID | Native load / archive / .NET Tool | Godot route | Unity route |
| --- | --- | --- | --- |
| `win-x64` | Required CI acceptance on Windows | Godot 4.7 GDScript GDExtension Debug/Release, external bridge | Unity 6000.5+ desktop Mono, Editor Play Mode / Player |
| `linux-x64` | Required CI acceptance on Ubuntu 22.04 | Same addon contract, Linux Debug/Release; rendered tests require display/Xvfb | Same Mono contract; rendered tests require display/Xvfb |
| `osx-x64` | Required CI acceptance on Intel macOS | Same addon contract, x86_64 Debug/Release | Mono Player evidence retained; native Intel Editor Play Mode **UNVERIFIED**, not executed because no Intel Mac is available |
| `osx-arm64` | Required CI acceptance on Apple Silicon macOS | Same addon contract, arm64 Debug/Release | Same Mono contract with an arm64 Editor / Player |

These are declared routes and their required verification, not an unconditional
feature claim. Package loading does not prove process launch. Process launch does
not prove bridge attachment. Attachment does not prove a particular command can
complete in that host. Inspect `get_version` / capability negotiation and test the
actual operation with correlated completion. Observe, InputAction metadata,
spatial provider binding and replay timing have independent contracts. Offline
spatial DTO validation does not bind or execute a physics provider. Engine smoke
tests on a source fixture are supporting evidence only; artifact acceptance
must use the identified addon/UPM payload in the engine fixture and verify the
actual archive composition separately. A published release is not required.

The [PR #168 execution record](spatial-acceptance-followup.md) provides actual
Godot 4.7 stable GodotPhysics3D/Jolt bridge, built Native MCP and isolated
NuGet-consumer runs on all four RIDs. It uses the candidate addon payload also
assembled into validated ZIPs, rather than demonstrating engine execution from
the extracted ZIPs. Its reviewed head is
`0fd01cbcd11c5b5d30399c9a6273418d193edd68`; CI artifacts pin the identical-tree
checkout `9c2eb7b59a356680806e287a4e2c48fb3b0fb1be` and package version `0.0.0-ci`.
Windows Unity 6000.5.3f1 standalone Mono/PhysX passed the same three spatial
client paths. The later [identified UPM route record](distribution-route-evidence.md)
also establishes Windows artifact-only Editor Play Mode and Player UI operation,
plus a precompiled-provider Player spatial run, at its stated commit/archive hash.
These results establish their measured scope, not every feature in the engine
packages. Existing release-only CI already defines four-RID Unity jobs; the
separate candidate workflow makes Player validation available without a tag or
publication. The [current acceptance record](remaining-route-acceptance.md#current-acceptance-status)
retains Windows/Linux Editor, four-RID Mono Player/spatial and Godot evidence,
and the later native arm64 Editor success. Native Intel macOS / Unity
6000.5.3f1 / actual UPM / Editor Play Mode is **UNVERIFIED**: no Intel Mac is
available and the test was not executed. This is not a failed or unsupported
route. Issue #129/#130 closure does not establish Intel Editor acceptance;
M4, Rosetta and Intel Player results cannot substitute for it. This limitation
does not extend to all Intel routes or other measured features, patches or backends.

Godot C# runtime samples, Unity IL2CPP, Unity IMGUI, EditorWindow automation,
unlisted RIDs and experimental browser paths are outside this desktop acceptance.
The aggregate Godot archive also carries Web binaries; their presence alone does
not extend the four-RID desktop feature matrix.

## Dependency and license closure

The consumer restores managed dependencies once into an isolated seed cache,
copies their `.nupkg` files into a local feed, and then repeats archive restore and
Tool installation with new empty caches and only that feed. The resulting
`distribution-manifest.json` lists exact resolved package versions, SHA-256,
license expressions/files and repository commits. Retain this manifest, the
local feed, dependency license files and `project.assets.json` with external
consumer evidence. Gua is MIT; the Viewer contains React-family MIT code.
Both the extracted archive and Tool payload include a `notices/` directory with
Gua/Viewer notices, dependency license files and Microsoft runtime notices.
`JsonSchema.Net` and its transitive dependencies retain their own package
licenses. A self-contained .NET archive also includes Microsoft runtime files
and their notices. A .NET Tool requires a compatible installed .NET runtime.

Native archives contain the core/runtime libraries, not an entire operating
system. Windows MSVC builds require the matching supported x64 Visual C++
runtime (system/UCRT plus redistributable C++ runtime); Linux requires its
supported glibc/libstdc++/libgcc environment; macOS requires system libSystem and
libc++. Inspect dependency output on the actual candidate RID and preserve it.
Self-contained .NET does not remove native OS dependencies. Godot GDExtensions
embed the Gua runtime rather than depending on a sibling `gua_runtime` library;
Godot and Unity executables/licenses are supplied separately by the consumer.
Godot addon archives include the MIT notice for the pinned godot-cpp dependency
(`ba0edfed90512ec64aba51d4295a3e7e30112f86`) in `godot-cpp.LICENSE.txt`.

## Compatibility and completion gates

Previously published package names, native ABI and public managed signatures are retained.
The new Value package is `gua-value-tools`; the rejected `gua-value` name was
never published. See the [1.1.0 release plan](release-1.1.0.md) for the bounded
compatibility review and publication prerequisites.
External tools that pin the strict 1.0.10 diagnostics/version schemas must
update those schemas when upgrading: added response fields fail their old
unknown-property rules. No failure was demonstrated on the supported
same-release paths; this caveat does not require a runtime compatibility fix
or a major-version change before the proposed 1.1.0 publication.
The current native version API reports `abiVersion: 1` and
`protocolSchemaVersion: "2"`; versioned descriptor structs remain additive C ABI
extensions. The external consumer asserts these values and retains
`core-version.json` / `runtime-version.json`, including actual capability lists.
Use `GuaVersion.EnsureCompatible` to check a consumer's required ABI/protocol and
capabilities before operation. Those lists describe the connected context and
must not be copied into an engine-wide support claim.
The resource API and `schemas/` package directory are additive; existing `trace/`
entries stay available. UI tree schema v2, Trace v1, Selector and InputAction
metadata schemas retain their independent versions. The Recording JSON schema
accepts v1/v2. `GuaRecordingFile` retains its UI Recording v1 reader/writer and
rejects v2; `GuaTimedSegmentImport.FromRecording` explicitly converts only
game-input v2 to an independent timed-segment v1 plan. The merged #122 contract
ships `timed-segment-v1.schema.json` and `timed-segment-result-v1.schema.json`.
`GuaTimedSegmentFile.Save/Load` round-trips a bounded plan without native code.
The external smoke retains legacy UI v1 compatibility, preserves imported
offsets/defaults and `legacy-unknown` provenance, rejects undeclared versions,
validates package-local schema references and round-trips a values-free timing
result serialization fixture through Trace. A file round trip does not execute
Replay or establish host application timing.

| Serialized contract | Read/write support at this candidate boundary |
| --- | --- |
| UI tree | v2; additive optional state fields remain omitted when unsupported |
| Trace | v1 reader/writer; unknown major versions rejected, additive fields retained |
| Recording | JSON schema v1/v2; managed UI v1 round trip and v2 rejection; explicit game-input v2 timed import |
| Timed Segment | Independent v1 plan/result schemas; managed plan import/file round trip with unknown original timing provenance and result serialization fixture; host Replay support verified separately |
| Semantic Selector | Existing `selector.schema.json` string-criterion objects; no new selector grammar |
| InputAction map | Existing v1 preserved; explicit v2 metadata map/search use their separate schemas and `semantic_game_input_metadata_v1` capability |
| Observe / Value / Spatial | Independently pinned v1/r1 schemas; execution/provider support negotiated separately |

`syntax-check.yml` runs package-only acceptance on all four real RID runners and
uploads `gua-distribution-<rid>-<commit>` evidence. No skip counts as a pass.
`nuget-publish.yml` repeats the same gate before public NuGet publication. The
GitHub release asset packer extracts and validates both actual Godot ZIPs,
including `gua_spatial.gd`, licenses and required desktop/Web binaries, before
uploading them. Composition checks do not substitute for engine execution.

Run the candidate consumer from an exact package feed:

```powershell
scripts/verify-distribution-consumer.ps1 -PackageDirectory artifacts/packages `
  -Version 0.0.0-ci -SourceCommit <full-commit> -Rid win-x64 `
  -OutputDirectory artifacts/distribution-evidence
```

| Requirement | Inspectable evidence |
| --- | --- |
| AT-BOUND-001 | Package-only consumer project; no ProjectReference, source Include or HintPath; no Gua-to-Playtest reference |
| AT-FILE-002 | Exported pinned schemas, source commit/hash manifest; offline run with native libraries removed; embedded Viewer metadata |
| AT-PACK-002 | Consumer copied outside the repository; isolated NuGet caches; local-feed-only acceptance restore; native overrides rejected |
| AT-PACK-003 | Extracted self-contained consumer ZIP and fresh installed .NET Tool run, package manifests and embedded report |

Candidate acceptance incorporates merged #122 at
`989163781a0e5ce6ac629e99a7d140a47e0db1ad` and repeats the four-RID consumer gates.
Issue closure requires exact-head required CI/audit success and a mapping of
the original #129 acceptance conditions to the identified target artifacts,
environment, assertions, results and remaining support scope. Retain package
versions, source commits, hashes, dependency/license manifests, Viewer evidence
and actual launch/attach/operation evidence for each declared RID/route; skips,
loading alone and unrelated consumer success do not satisfy those conditions.
The four-RID package gates and PR #168 Godot evidence can be reused within their
measured scope. The owner closed #129/#130 with the native Intel Editor
unverified limitation retained and tracked in #174; see the current acceptance
record above. Closure is not a claim that every declared route passed. Public
publication is separate and is neither required for closure nor authorized by
successful artifact acceptance.
