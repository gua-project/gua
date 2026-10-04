# Gua 1.1.0 release plan

Proposed version: **1.1.0**, not yet released. The release workflows derive all
published versions from `gua-v1.1.0`; workspace `0.0.0` and the local .NET
default are not public release versions. This document does not authorize a tag,
publication, deployment or registry configuration change.

## Changes since 1.0.10

- Common Value v1 validation and Observe subscriptions, distributed through the
  new `gua-value-tools` npm package and native/managed APIs.
- Trace recording and offline packaged Viewer, semantic snapshots in the new
  `Gua.Testing.Snapshots` NuGet package, semantic lint and locator auto-wait.
- Capability-bound Spatial-r1 queries and independently versioned InputAction
  metadata, plus bounded Timed Segment recording/replay APIs.
- Packaged consumer/archive acceptance, stronger request correlation,
  authorization/cleanup and Trace validation, and Unity/Godot fixes.
- Publishing repository metadata now points to `gua-project/gua`.

## Bounded compatibility review

Source comparison: `gua-v1.0.10` (`10a8ef71713fe491633cf2b5649c76007cfd4767`)
to rename merge `51a1ba1904c273c241aabdda99846bf29a836a9f`, 69 commits.
This readiness change edits metadata and documentation only.

The review inspected existing native public headers, changed public managed
signatures, npm manifests/exports and modified pre-existing schemas. No removed
native C declarations, changed existing C struct layouts, removed managed entry
points or removed npm package entry points were found in those surfaces.
Existing UI tree v2, Selector and Recording schemas are unchanged; the modified
commands, diagnostics and version schemas add commands or optional fields.
InputAction metadata uses explicit v2 APIs alongside retained v1 APIs and a
separate capability. Timed Segment APIs are additive; the existing managed UI
Recording v1 reader/replayer remains available. The rejected `gua-value` name
was never published; `gua-value-tools` is a new identity, not a rename of an
existing published dependency. New managed packages require their declared
target frameworks; native/managed package sets should be restored together.

Behavior changes include rejecting invalid `null` search limits and v1 action
maps containing v2-only metadata, preserving scalar/null UI values accurately,
and preventing a pre-cancelled managed action wait from enqueueing work. Timeout
polling is bounded by remaining time; cancellation does not promise rollback of
an action already sent. These tighten invalid-input/lifecycle handling and do
not establish a removed valid v1 contract. Code that depended on those earlier
edge behaviors should be checked when upgrading.

### Strict legacy-schema compatibility blocker

The additive signature review alone does **not** clear 1.1.0 for publication.
The published 1.0.10 diagnostics and version schemas use
`additionalProperties: false`. Current Debug diagnostics emits `traceLifecycle`
at the existing diagnostics schemaVersion 1; authorized bound spatial version
responses add `spatial` while retaining protocolSchemaVersion 2 and ABI 1.
Those fields are absent from the corresponding 1.0.10 schemas, so a consumer
that validates current responses against its pinned old schemas rejects them.
Player diagnostics does not expose the Debug lifecycle journal; the spatial
case requires authorized host binding and is not claimed for every connection.

Bounded reproduction: the unchanged 1.0.10 diagnostics/version fixtures pass
their old top-level unknown-property gate; adding the actual new field fails
that gate, while the current schema permits it. Source emission is in
`native/gua-core/src/gua.cpp` (`build_diagnostics_json`) and
`native/gua-runtime/src/runtime.cpp` (`decorate_version_json`). This checks the
strict top-level boundary, not full nested-schema or live-engine validation.

**1.1.0 remains the proposed version, but this strict-schema compatibility
issue must be resolved before publishing it as a backward-compatible release.**
Preserve the legacy response shape through an explicitly versioned or negotiated
extension, or reassess the version/support contract as a breaking change. This
metadata/docs PR does not change runtime behavior or silently waive that issue.
No exhaustive binary compatibility or every-engine-feature certification is implied.
See the [distribution compatibility contract](distribution-contract.md#compatibility-and-completion-gates).

## Support and known limitations

Native Intel macOS / Unity 6000.5.3f1 / actual UPM / Editor Play Mode is
**UNVERIFIED**: no Intel Mac is available, so this test was not executed. It is
neither failed nor Unsupported. Native arm64 Editor success does not prove this
Intel route. Windows/Linux Editor, four-RID Mono Player/spatial and Godot
GodotPhysics3D/Jolt evidence remains scoped to the recorded products, recipes,
engine versions and backends. See the [current acceptance record](remaining-route-acceptance.md#current-acceptance-status)
and [#174](https://github.com/gua-project/gua/issues/174).

[#116](https://github.com/gua-project/gua/issues/116) remains open for actual
Codex browser/native MCP routing coexistence. Built-in privileged browser spatial
is Unsupported; WebGL/IL2CPP and unlisted engine/backend combinations are not
established by the desktop evidence. Issue closure is not all-route acceptance.

## Required owner checks before tagging

1. Require successful CI for the exact final merged commit; older candidate
   evidence is supporting evidence and cannot be relabeled as current-head success.
2. Confirm each npm package (`gua-value-tools`, `gua-world-tools`, `gua-webmcp`,
   `gui-mcp`) trusts owner `gua-project`, repo `gua`, workflow
   `mcp-publish.yml`, with matching `release` environment and direct
   `npm publish` allowed. Bootstrap `gua-value-tools@0.0.0` exists, but does
   not prove trusted publication. npm connections using the old owner must be
   replaced; the public registry does not expose these private settings.
3. Confirm the active NuGet policy for the correct package owner matches
   `gua-project/gua`, `nuget-publish.yml` and the `release` environment.
   `NUGET_USER` must identify that nuget.org profile; the observed environment
   variable is `PlumRice`. Permit existing-version publication for all existing
   IDs and new-package publication covering `Gua.Testing.Snapshots`.
4. Confirm existing Unity license inputs are usable for the GitHub Release
   workflow. Secret presence alone is not execution evidence. Do not expose
   credentials when checking these prerequisites.
5. Recheck version availability, merged SHA and explicit release authorization
   before creating/pushing `gua-v1.1.0`. The parser accepts only `gua-vX.Y.Z`,
   not prerelease suffixes. Source placeholders should remain unchanged.

The tag starts independent npm, NuGet and GitHub Release workflows; publication
is not transactional. npm publishes Value before its dependants and skips an
existing exact version; NuGet uses `--skip-duplicate`. Neither proves an existing
version came from the intended source. If any workflow fails after a partial
publish, inspect existing artifacts and identities before choosing recovery;
do not move the tag or replace immutable versions. After publication, repeat
acceptance against the actual registry packages and released archives.

Official requirements: [npm trusted publishing](https://docs.npmjs.com/trusted-publishers/),
[npm provenance](https://docs.npmjs.com/generating-provenance-statements/), and
[NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
