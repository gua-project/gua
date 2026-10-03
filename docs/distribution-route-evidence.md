# Identified Unity candidate route evidence

This record supplements issue #129; it does not waive any declared route or the
independent #130 spatial integration requirements. Publication is separate.

## Windows execution

Target: main `0a5cbc83fbfd5ce9a465508644f96aced5694d4a`, existing private version
`0.0.0-ci`, Unity 6000.5.3f1, Windows x64, Mono/PhysX. NuGet inputs came from
successful main Syntax Check run `37082802731`. The privately assembled Windows
UPM archive SHA-256 is
`a9a79360ee116f467614e62ab9a52ddb82bc0eec2d40cc74c34270e99151cc97`.
This identifies a local candidate, not an existing public release artifact.
Its earlier notices expose the own-MIT omission fixed here; route execution does
not establish redistribution readiness of that earlier archive. The new helper
closure check is separate, and candidate CI must pack the corrected archive.

The producer compiled the adapter; the separate consumer installed only that
`.tgz` via UPM's local-tarball route. Installed payload hashes matched the archive;
Unity's added `package.json` `_fingerprint` was removed only for the package JSON
semantic comparison. No adapter implementation source was copied into consumer
Assets. The external .NET client restored exact candidate package references
outside the checkout, from only a local feed into an empty cache, with zero
project libraries and no native-directory overrides.

| Required behavior | Concrete violation and assertion | Execution / retained evidence |
| --- | --- | --- |
| Artifact provenance and attachment | Wrong build ID, missing Unity adapter or incompatible ABI/protocol fails before operation | Actual Editor Play Mode and standalone Player reported the exact target, ABI 1 and protocol 2; `version.json`, UPM provenance and installed-file hashes |
| Usable UI operation | Missing/duplicate/nonactionable Start control, rejected submission, absent/wrong completion or absent observed transition fails | `examples/distribution-unity-consumer/Program.cs` requires exactly one actionable `start`, nonzero request, correlated successful Click completion and actual title-to-loading UI transition; both actual routes passed |
| Rejection does not act | Missing-node click must return NodeNotFound and leave semantic state unchanged | Client compares the entire UI JSON except volatile frameSequence/revision, including nodes/state/session/screen; retains rejected-before/after snapshots |
| Saved observation and offline Viewer | Unfinalized/invalid Trace, fewer than two observed blobs or report failure fails | Both routes saved before/after real UI, finalized Trace with zero reader issues and generated HTML through the packaged embedded Viewer; no browser visual QA claim |
| Precompiled spatial provider | Source fallback, unsupported provider, wrong result correlation or missing actual physics batches fails | Separate Player installed the same archive and supplied only fixture host/build scripts; existing TS, built Native MCP and isolated package-only .NET spatial consumer passed three actual PhysX batches, Unity 6000.5.3f1, clean exit |
| Redistribution notices | Missing metadata/file or missing own MIT attribution fails packaging | `copy-unity-package-notices.ps1` passed the restored closure, including Microsoft dependencies' own copyright and MIT terms; a disposable Bad.Dep package without license metadata failed at the intended assertion |
| Source pin | Wrong native-package repository commit must fail before assembly | `stage-unity-candidate-native.ps1` rejected a deliberately wrong 40-character commit at its identity assertion |

A disposable client changed only the completion request comparison to expect
`request + 1`; the actual Player operation then failed specifically at
`Correlated successful click completion missing`. The clean client was restored
and rebuilt. No deliberate mutation is submitted.

Local evidence is under the isolated `gua-unity129-*` temporary execution root:
`tracked-client-player-evidence-2`, `tracked-client-editor-evidence-2`,
`spatial-route-evidence-2`, `consumer-provenance.json`, `upm-provenance.json`,
`notice-check-2`, `negative-license/detected.txt` and `negative-correlation.log`.
The workspace's ignored `artifacts/unity129-paths.json` identifies the exact root.
Earlier failed setup/selector/spatial-port attempts are retained separately and
are not counted as passes. Raw Editor licensing logs are not uploaded by the
reusable consumer.
The [durable Windows execution record](distribution-windows-run.md) retains actual
operation/provenance outputs, archive file hashes, the new notice-helper manifest
and selected failure logs for repository reviewers. Full binary/Viewer artifacts
remain in the desktop evidence ZIP, not in git; candidate CI must independently
retain the corrected archive and actual route evidence.

## Candidate CI route

The existing release workflow already has four-RID Unity Player and Editor jobs;
they are tag-triggered, so normal Syntax Check evidence does not execute them.
`unity-candidate.yml` provides a separate manual, non-publishing Player path.
It requires a successful exact-commit main push Syntax Check run, checks NuGet
source/version identity, assembles Universal 2 macOS native libraries, compiles
the UPM and installs its actual archive before building each of the four Mono
Players. Each Player executes on its native RID host using the same isolated
package-only client, with Xvfb on Linux. macOS validates the actual app executable's
declared arm64/x86_64 slice with lipo before execution, so translation cannot
substitute for the declared architecture. Archive hashes, dependency notices,
package manifests, assets, real operation results and Trace/report are retained.

The candidate uses the existing release environment's Unity license inputs and
GameCI images. It adds no publication job, tag, credential changes, desktop
module installation or new Personal-license acquisition step. Secret presence
is only a prerequisite; it does not prove license validity or route execution.
The workflow must actually pass before claiming its four Player routes. An
invalid/missing existing license is an explicit failure, not a skipped pass.

Non-Windows Editor Play Mode remains unverified and requires applicable existing
licensed build/runtime hosts or separately authorized setup. Player UI success
does not establish all spatial backends, patches, replay timing, Observe or other
features. The local spatial run establishes only its stated Windows PhysX path.
WebGL/IL2CPP and the remaining #130 integration conditions are independent.
