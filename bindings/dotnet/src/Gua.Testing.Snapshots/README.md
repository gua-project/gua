# Gua.Testing.Snapshots

Opt-in semantic regression comparison for UI and optional World trees. Targets
`net10.0` and `netstandard2.1`; depends on `Gua.Testing`. No native or engine API
changes are required. This package does not replace screenshot/Visual comparison:
pixels, bounds and World position changes are excluded by default.

```csharp
using Gua.Testing.Snapshots;

var options = new SemanticSnapshotOptions
{
    BaselineDirectory = "baselines/semantic",
    ArtifactDirectory = "artifacts/semantic",
    BaselineVariant = "english",
    // Optional: supply the JSON World tree using your context's existing API.
    // WorldTreeProvider = () => worldContext.GetWorldTreeJson(),
    Rules = new[]
    {
        new SemanticSnapshotRule("/ui/nodes/*/text", SemanticSnapshotRuleAction.Mask, "account/name"),
        new SemanticSnapshotRule("/world/objects/*/state/timer", SemanticSnapshotRuleAction.Ignore)
    }
};
GuaSemanticSnapshots.ExpectSnapshot(context, "settings", options);
```

`CompareSnapshot` returns a typed result on missing or differing baselines;
`ExpectSnapshot` throws `SemanticSnapshotAssertionException` carrying that result.
Results include reason, baseline path, artifact path, run ID and typed differences.
Missing baselines fail without writing one. Approve with `UpdateBaselines = true`
or exactly `GUA_UPDATE_SEMANTIC_BASELINES=1`. The Visual environment variable
`GUA_UPDATE_BASELINES` does not approve semantic baselines. Keep approval disabled
in regular CI and review baseline diffs before committing them.

The version-1 baseline envelope contains `ui` and optionally `world`. Object
properties sort ordinally; numeric tokens normalize without rounding, including arbitrarily large or small values. Arrays preserve publication order, including nodes, objects,
actions and tags. All semantic properties, unknown versus present false/null,
screen/scene, IDs, parent relationships, role/kind, text/value, visibility,
enabled/active and state remain significant. Root sessionEpoch/frameSequence/
revision and item bounds/position are excluded unless `IncludeRuntimeMetadata`
or `IncludeGeometry` is enabled. These exclusions apply at schema locations only:
World state keys such as `revision` or `position` remain significant. Host/OS
diagnostics are never sampled. UI and World providers are read sequentially;
callers must obtain stable trees themselves when they require coordinated frames.

Differences distinguish added/removed IDs, changed property paths and relative
order of shared IDs. Added items do not falsely classify shared items as reordered.
Changing parentId is a hierarchy change. Paths escape `~` and `/` with JSON Pointer
escaping; `@id` diff segments identify items instead of unstable indices.

Rules use absolute JSON Pointer patterns (`~0`, `~1` escaping), with `*` matching
one segment. Optional `ItemId` selects an exact UI or World item ID. Ignore wins
over mask. Whole-tree `/ui` or `/world` rules are allowed; the baseline envelope
version is reserved and cannot be masked or ignored. Rules apply before comparison, approval, expected/actual output and
diff output; raw baselines are never copied to artifacts. IDs cannot be masked or
ignored individually because selectors need stable identity; ignore an entire
item instead. Array child paths must use wildcard plus optional ItemId; numeric
array indices are rejected because removal changes positions in saved baselines.
Numeric object/state property names are supported. Removing an item from comparison
also removes its place in order comparison. Masks retain a `[MASKED]` marker on
present fields; unknown versus present stays significant. Rules do not rewrite
an existing baseline unless approval is explicit.

**The caller owns sensitive-data protection.** No heuristic discovers passwords,
personal data or secret state. Configure rules before the first capture and cover
all representations (label, text, value and nested state). IDs, test names, variants,
directory names and unmasked values can themselves contain sensitive information;
choose safe identifiers and storage access controls. Old baselines can contain
secrets from earlier rules: review/remove them explicitly. Semantic masking does
not redact screenshots, Visual artifacts, Trace or other diagnostics.

Baseline paths include portable name/variant labels plus SHA-256 hashes to avoid
sanitization collisions. Artifact paths add a unique run ID and contain
`actual.json`, optional `expected.json`, `diff.json`, and `comparison.json`.
Every comparison has independent artifacts. Approval uses unique temporary files
and atomic replacement so concurrent readers see complete documents. Concurrent
approval of the same test/variant is last-writer-wins; use distinct variants for
different expected states. No artifacts or packages are automatically published.

For an explicitly invoked comparison, set `Trace`, `TraceStepId`, and the already
authorized `TraceProfile` to attach the normalized result to that step. Trace does
not run comparisons or approve baselines. The attachment omits absolute paths and
uses the same masking rules before Trace redaction. Keep separate baseline variants
for profiles/builds. Raw Trace UI/World observations retain geometry and frame data
even when the comparison excludes them. Trace capture failure does not change the
comparison result or `ExpectSnapshot` exception.
