# Repository Instructions

## Project

Gua is a runtime UI automation protocol for games. Its center is the protocol,
not any single language implementation.

The initial implementation should stay focused on:

- Semantic UI tree schema
- Command and event schemas
- Stable C ABI runtime core
- Thin C++ wrapper over the C ABI
- ImGui adapter as the first reference integration
- C++ and C# test ergonomics over the C ABI
- MCP and Inspector as consumers of the protocol

Avoid turning the project into a game engine, editor MCP, image-recognition QA
bot, or full UI framework.

## Architecture Rules

- Treat `protocol/` as the source of truth for cross-language behavior.
- Keep externally stable native APIs in C ABI form.
- Put C++ convenience APIs on top of the C ABI.
- For .NET, prefer P/Invoke over a separate runtime implementation.
- Test helpers should be usable from C++ and C# first. Do not make TypeScript the
  primary test API.
- Treat Windows MSVC as the primary native development toolchain.
- Keep the native core portable for Apple Clang and Android NDK Clang by staying
  within the C ABI and standard C++20 unless platform code is isolated.
- Prefer polling/event queues across language boundaries instead of callbacks.
- Add engine integrations as adapters after the core protocol is usable.

## AI Working Notes

- Read `README.md` and `protocol/specs/protocol.md` before broad design work.
- Keep generated output, dependency folders, and local build artifacts out of git.
- When changing schema behavior, update protocol docs and affected clients together.
- Preserve the monorepo shape unless the user explicitly asks to split packages.
- For independent defect investigation, use the project-local `$gua-bug-hunt`
  skill. Keep bug-hunting subagents read-only and require reproducible evidence;
  fixes are a separate task unless the user explicitly requests them.

## Contract Validation

- Use issue acceptance conditions as the validation baseline, not expectations
  derived only from the implementation. Resolve ambiguous contracts against the
  existing specs, compatibility, and responsibilities before validating.
- In the PR, briefly map each major condition: required behavior -> concrete
  contract violation -> test location and assertion -> execution evidence and
  unverified scope. Reuse existing tests; do not add duplicates to raise counts.
- Select negative cases by change risk: boundaries, missing/duplicate results,
  correlation mismatch, partial failure, expiry/cancellation, disconnect/reset,
  authorization loss, or storage failure. Do not require unrelated cases.
- For a few high-impact contracts, confirm the test detects a violation using
  the pre-fix implementation, an invalid response, fault injection, or a local
  deliberate mutation. Verify that the fault fires and the intended assertion
  fails; an unrelated environment error is not detection evidence. Keep deliberate
  mutations in a disposable checkout or equivalent and out of submitted changes.
- Check contract-required counts, correlation IDs, and completion states before
  checking every array item; an empty array must not pass a required-result check.
  For failures, assert the required error, processing stage, and side effects,
  rather than merely that an exception occurred.
- Keep evidence specific to the implementation and client/transport path tested.
  Distinguish schema/Fake/unit checks, real transport, real engine, and packaged
  consumer checks; another implementation's negative tests cannot prove this path.
- Record the tested commit, environment, command, result, and relevant log or
  artifact references. Distinguish old-commit success, skips, unrun checks, and
  success only on rerun from consistent validation of the final commit.
- State unverified scope and reasons against existing acceptance conditions;
  disclosure does not waive required conditions. Low-impact documentation changes
  need content/consistency checks, not invented runtime tests. Do not require full
  mutation testing, a new test framework, every case on every PR, or revalidation
  of all past PRs as a prerequisite.

## Code Review Rules

- Review the cumulative branch diff against its merge base, not only the most
  recent task or commit. A fix can expose a defect in an older part of the same
  pull request.
- Reviewers and auditors must check the Contract Validation mapping and ask
  whether an implementation that breaks a major contract could still pass its
  tests. Verify selected violation-detection evidence and path-specific coverage;
  apply this within the existing audit gate, not a separate review loop.
- For cross-boundary changes, trace the affected contract from `protocol/`
  through the C ABI, managed bindings, engine adapters, bridge, MCP, Inspector,
  recording, and tests. Do not assume that matching type names prove matching
  behavior.
- Reauthorize owner-, session-, visibility-, capability-, and confirmation-bound
  operations at the point where the host consumes them. Enqueue-time validation
  is not sufficient across disconnect, reset, policy, or Action Map changes.
- Owner cleanup must neutralize held input on disconnect, dispose, reset, and
  lease expiry. A consumed request must retain a completion path even if its
  owner disconnects before the host reports completion.
- Validate common request metadata before dispatching to command-specific paths;
  an early return must not bypass epoch, timing, cancellation, or correlation
  checks.
- Publicly advertised keyboard, pointer, gamepad, action, and capability values
  must be accepted consistently or rejected consistently at every boundary.
- Preserve request correlation and one-shot bounded result retention. Never let
  cleanup, polling, or one client consume another request's completion.
- Keep sensitive input out of logs, diagnostics, errors, recordings, and review
  artifacts while preserving safe replay references.

## Automatic Audit Gate

- After changing repository content, including adding an untracked file,
  complete the initial focused validation, then spawn exactly one `gua_auditor`
  subagent before the final handoff.
- Give the auditor the current task scope plus the cumulative branch diff against
  the intended base branch. Include `git status --short`, tracked and staged
  diffs, and the contents of relevant untracked files so additions cannot escape
  review. Require the auditor to use `$gua-bug-hunt`, select every audit-matrix
  lane touched by that cumulative diff, and return reproducible findings with
  file references and verification evidence.
- Treat the auditor as behaviorally read-only: its custom-agent sandbox defaults
  to read-only, but a live parent permission override may take precedence.
  Explicitly prohibit edits in every audit prompt, reject any audit-authored
  file changes, and leave all fixes to the parent agent.
- Wait for the auditor and independently validate each finding. Ignore
  speculative, style-only, duplicate, out-of-scope, or unsupported findings.
- If the user authorized implementation, fix validated findings that are within
  the task scope and rerun the narrowest relevant verification. After audit-led
  fixes, run at most one final `gua_auditor` pass on those fixes.
- Do not let an auditor spawn another auditor, and do not repeat auditing when
  the final pass reports no actionable finding. This bounds the automatic gate
  to at most two audit passes per task.
- Skip the automatic audit only when no repository file changed, when the task
  is itself a read-only audit, when running as the fix phase inside
  `scripts/run-local-pr-audit.ps1` (the outer loop owns re-audit), or when the
  user explicitly asks to skip it.

For a deliberate pull-request-wide gate, run
`scripts/run-local-pr-audit.ps1 -Base origin/main`. Add `-Fix` to let a parent
Codex validate findings, apply only supported fixes, run focused checks, and
repeat the full review for at most four passes.
