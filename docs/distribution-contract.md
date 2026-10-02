# Distribution contract for external consumers

This contract supports a separate consumer through packages and artifacts. Gua
does not reference Playtest or build its product. A consumer must pin the package
version, full source commit and artifact hashes; runtime startup never retrieves
the latest schemas or rebuilds the Viewer.

## Candidate versus public acceptance

`0.0.0-ci` is the existing private CI candidate identifier. It is meaningful only
with a full commit and SHA-256 manifest, and must never be uploaded as a public
release. The source default `0.5.0-preview.3` and existing GitHub releases do not
select the next release version or channel. No tag or publication is needed to
run candidate acceptance. A final public version/channel remains an owner
decision, followed by repeating acceptance on the actual published packages and
archives. A locally staged addon does not establish public artifact support.

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
| `osx-x64` | Required CI acceptance on Intel macOS | Same addon contract, x86_64 Debug/Release | Same Mono contract with an Intel Editor / Player |
| `osx-arm64` | Required CI acceptance on Apple Silicon macOS | Same addon contract, arm64 Debug/Release | Same Mono contract with an arm64 Editor / Player |

These are declared routes and their required verification, not an unconditional
feature claim. Package loading does not prove process launch. Process launch does
not prove bridge attachment. Attachment does not prove a particular command can
complete in that host. Inspect `get_version` / capability negotiation and test the
actual operation with correlated completion. Observe, InputAction metadata,
spatial provider binding and replay timing have independent contracts. Offline
spatial DTO validation does not bind or execute a physics provider. Engine smoke
tests on a source fixture are supporting evidence only; final public acceptance
must install the shipped addon/UPM archive into the engine fixture first.

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
consumer evidence. Gua is MIT; the Viewer contains React-family MIT code;
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

Existing package names, native ABI and public managed signatures are retained.
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
accepts v1/v2, while the managed Recording reader must be checked against its
declared versions after #122; schema acceptance alone does not prove a replay
reader can read v2. The external smoke retains a legacy v1 Recording round trip,
rejects an unknown version and checks the embedded Viewer.

| Serialized contract | Read/write support at this candidate boundary |
| --- | --- |
| UI tree | v2; additive optional state fields remain omitted when unsupported |
| Trace | v1 reader/writer; unknown major versions rejected, additive fields retained |
| Recording | JSON schema v1/v2; managed legacy semantic v1 file round trip is tested; #122 timing acceptance remains required |
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

Final issue closure additionally requires #122 integration, exact-head required
CI/audit success, the owner's public version/channel decision and verification
of that actual public distribution across the declared engine routes. Candidate
success does not authorize publication or satisfy that release-only gate.
