# Game-owned remote integrity: implementation and evidence

This is an implementation work log, not a claim that the complete engine/package
migration is finished. It describes locally tested Windows x64 .NET, Unity Mono
and Godot GDScript paths. The Godot .NET follow-up is described at the end of
this document, followed by the Godot Web game-page verifier. Final release-workflow distribution,
and other operating systems still need their own integration and acceptance
evidence. Existing public v1.2.0
binaries are reproduction inputs, not a recommended secure installation.

## Threat and trust boundary

The game executable/assembly, its generated policy, the operating system and CLR
are trusted. An end user replaces the Gua DLL set after the game was built. The
replacement can be a legitimate remote-enabled Gua build, including its matching
manifest and managed assemblies. The replacement must not enable a normal game.

This does not promise protection when the game's verifier, expected hashes,
EXE/assembly, engine executable, or runtime is also modified. There is no embedded
secret. Publisher signatures alone cannot distinguish two legitimate Gua modes.

## Build and runtime sequence

1. The provider builds the native modes using the existing compile/link gates.
   `stage-game-build-bundle.ps1` selects native component files from independently
   staged shipping/QA/AI-player packages, verifies their manifests through the
   generator and requires a common source identity. It does not execute these
   DLLs on the consumer's machine.
2. A game imports `Gua.GameBuild.targets`. Its single setting is
   `GuaRemoteMode=off|testing|player`; omitted means `off`. Debug/Release is a
   separate game build configuration. The target selects prebuilt files; it does
   not invoke CMake or build Gua source.
3. The generator validates the selected build-time package identity and hashes,
   hashes the selected managed Gua reference assemblies, and writes C# below the
   **game project's** `obj/gua-game-build/` directory. Expected hashes, provider
   source/build IDs, and mode become constants in the game assembly. The runtime
   does not read a manifest to update those expectations.
4. The generated game module initializer runs before game entry code. It refuses
   an already loaded `gua.dll` or `gua_runtime.dll`, checks path reparse points,
   and opens every expected native/managed file with `FileShare.Read`.
5. It hashes **all** opened files before loading either native file. A mismatch
   ends the process. `GetModuleHandleW`, independent of Gua, reports whether native
   Gua was loaded when rejection occurred: exit78 means no native Gua module;
   exit79 means the no-preload contract was already violated.
6. It loads the verified core first and runtime second by full path with
   `LOAD_LIBRARY_SEARCH_SYSTEM32`. Other dependency lookup does not consult
   PATH, the working directory or application directory. This is verified-pair
   ordering; runtime links its core implementation statically, rather than
   depending dynamically on the separate core DLL. Windows/CLR/system dependencies remain
   part of the trusted platform, rather than an unverified Gua plug-in closure.
7. Handles remain alive for the process lifetime. The .NET resolvers return the
   game-pinned handles, so `GUA_NATIVE_DIR`/`GUA_RUNTIME_NATIVE_DIR` cannot select a
   different native file after verification. The example game compiles its remote
   start call only under `GUA_REMOTE_AUTOMATION`; `player` also defines
   `GUA_PLAYER_ONLY`. OFF game IL contains no `StartInspectorBridge` reference.

The exact load order is in `tools/Gua.GameBuild/GameGuard.cs.txt`; manifest and
source generation are in `tools/Gua.GameBuild/Program.cs`. Pinned resolution is
in each managed component's `Native.cs`. The verifier is compiled into the game,
not a new replaceable `Gua.*.dll`.

## Boundaries that must not be overstated

- Windows share handles reject ordinary in-place writes/deletes of the verified
  files while held. These checks are not a general hostile filesystem or kernel
  race defense. Directory rename races, privileged memory modification, injected
  code and altered platform dependencies are outside the current proven boundary.
- Managed Gua assemblies may already have been resolved by the CLR. The current
  guarantee is before **native Gua load and game entry**, not before managed
  metadata/assembly loading. Arbitrary malicious managed module initializers are
  not equivalent to replacing files with the tested legitimate Gua release.
- The current .NET target explicitly rejects non-Windows/non-x64, single-file,
  NativeAOT and trimmed publication. These targets need a different integration;
  an error is not support for those platforms.
- Unity Mono needs a game-owned generated source in the game assembly and a hook
  earlier than the SDK's `BeforeSceneLoad` startup. Preloaded native plug-ins and
  repeated Editor Play sessions require explicit treatment. The existing
  SDK-owned `GuaUnityCompiledBuild` constants are not a game trust root.
- IL2CPP/WebGL fold managed/static code into game artifacts; their exact build
  validation and startup boundaries cannot be inferred from Mono tests.
- Godot's normal extension list loads GDExtensions before game scripts. The new
  game-build SDK keeps provider variants hidden and the selected DLL/descriptor in
  `.gdignore` directories. Its game-owned first autoload verifies an embedded hash
  and explicitly calls `GDExtensionManager.load_extension`. Export adds only the
  selected native file and descriptor, with the game-owned policy in the PCK.
  The adapter requires that policy and its OFF version has no native bridge-start
  call. These changes are in `bindings/godot/game-build`; the existing automatically
  loaded addon is not silently treated as equivalent.
- Godot's `ClassDB` precondition detects registered extensions; it is not a general
  OS module enumerator. Tests independently launch and observe the owned process
  tree with Windows debug events, requiring one real native Gua load in positives,
  zero in substitution negatives, and one in an early-load mutation. Godot's
  console EXE is a wrapper: observing only that wrapper failed the required
  positive load count, so it is not accepted as absence evidence.
- Godot's Windows `OS.kill(self)` immediately prevents later autoload callbacks,
  but reports exit0. Its tests require the exact refusal, no later game callback
  and independently zero native load events; exit0 alone is not success. GDScript
  does not retain Windows deny-write/delete leases, so active modification in the
  hash-to-load interval is not covered. The proven case is DLL-set replacement
  before startup with unchanged game code/data.
- Godot's verifier and expectations are part of the game's PCK. Changing that
  PCK or selecting different game data via engine arguments changes the trusted
  game code/data and is outside DLL-only substitution protection; an unchanged
  engine EXE alone is not the whole trust root.
- Godot Web side modules and WebMCP registration require their own closure and
  startup validation. Native Windows results do not establish Web behavior.

## Contract validation and local evidence

| Required behavior | Violation detected | Test/evidence |
| --- | --- | --- |
| Game expectations independent of runtime manifest | Replace OFF native pair and matching manifest with ON pair | `test-game-owned-native-policy.ps1`: exit78, `nativeLoaded=false` |
| Refuse before native load | Deliberately move native load before hashing in a disposable generated policy | Same script: exit79, `nativeLoaded=true`; the no-load acceptance condition detects the mutation |
| Local API preserved | OFF loses local UI tree or enables bridge | Existing real-native `Gua.BuildVariants` consumer assertions, reused by `Gua.GameBuildConsumer` |
| Explicit ON and Player ceiling | Wire operations fail, private data/privileged operations escape Player | Same consumer's actual WebSocket and consume/complete assertions |
| Automatic selection and game-owned bootstrap | Wrong native mode selected, remote startup remains in OFF game IL | `test-automatic-game-build.ps1`: actual consumer run plus PE metadata probe |
| Native environment cannot override policy | Wrong ON native paths override the selected OFF handles | Automatic consumer runs with both native directory variables pointing at ON |
| Complete official DLL replacement rejected | Official ON managed/core/runtime set replaces the game-approved set | Optional four official-file parameters to automatic-consumer test; game policy remains untouched |
| Player environment cannot elevate | Debug environment produces a Debug-capable Player process | Existing native behavior rejects runtime creation; logged as rejection, separately from the normal Player positive case |

Local initial source: base `2e12fb3` with working-tree changes. Initial native test
packages were built by the previous PR186 from source `4a4debd...`; they are not
new-HEAD native evidence. Evidence directories are under ignored `build/`:

- `game-owned-native-20261102T025439160/results.json`: OFF/ON/Player success,
  complete native pair+manifest swap rejected, early-load mutation detected.
- Automatic-selection evidence is generated separately by
  `scripts/test-automatic-game-build.ps1`; do not equate a build with a successful
  execution. The first run exposed the existing Player native behavior of
  rejecting a Debug environment request, and the test now distinguishes that
  negative from its normal positive case.
- `automatic-game-20261102T025856343/results.json`: automatic OFF/ON/Player
  selection, OFF game IL without remote-start reference, pinned resolution with
  wrong native environment directories, and full official v1.2.0 managed/native
  replacement rejected before native load. Player's Debug environment startup
  request was rejected separately from its normal successful Player run.
- `godot-game-policy-20261102T032656234/results.json`: Godot4.7 GDScript Editor
  runs and actual Windows Release EXEs in all three modes, official v1.2.0
  GDExtension replacement rejected before native load, and early-load mutation
  detected. The `.native-loads.json` files contain independent OS load events for
  the owned wrapper/engine tree; positives observed1, negatives0, mutation1.
- `godot-game-policy-20261102T062418569/results.json`: all modes repeated with
  non-elidable Release checks, actual WebSocket public UI action consumption and
  Player clock denial. Failed OFF policy generation over an enabled destination
  returned a build error and removed the explicitly selected old EXE. Windows
  supplies a `.tmp` export path to the plugin; cleanup maps it to its final `.exe`.
  The editor plugin directory is excluded from game exports. A conflicting
  game-policy autoload is refused without overwriting the existing policy.
  A Godot .NET project is explicitly refused rather than silently claimed as
  protected by the GDScript-only host.
  The generated runtime guard also refuses that unsupported host before native
  extension load, independently of the editor's preparation failure.

## Unity native preload foundation (not yet normal package integration)

The provider builds `GamePolicyTemplate.dll` and a distinct
`EditorPolicyTemplate.dll` from `native/gua-game-policy`. The game build embeds
its final expected native/managed file hashes in resource179 using
`bindings/game-build/NativePolicyWriter.cs`. The output is a game-specific
`GameSecurity_<game-id>.dll`, outside the Gua runtime DLL set. Its code and embedded
policy are part of the unchanged game verifier/data assumption, just as the .NET
game assembly is. Replacing this generated checker is outside that assumption.
This is not a permission answer supplied by a replaceable Gua runtime DLL.

Unity preloads the generated Player module (`isPreloaded=1`, Editor incompatible).
It checks all expected files and retains their read/share leases before loading
the native SDK pair from absolute paths with System32-only dependency lookup.
Its resource contains mode, source and build identity; startup does not replace
those expectations with a runtime manifest. Source adapter initialization is now
local-only; `StartRemoteForGame` is an explicit separate path and the game-owned
bootstrap calls it only in the enabled compilation branch.

`unity-game-policy-off-20261102T053610153/results.json` records actual Unity
6000.5.3f1 Windows Mono OFF local success and full official1.2 managed/native SDK
replacement refused with exit78 and zero native SDK load events. The game queries
the OS for checker-module presence before any checker P/Invoke, with a separate
non-inlined call boundary, and requires Unity's preload callback to have run.
This does not claim that CLR managed assembly metadata is never loaded first.

`unity-game-policy-testing-20261102T051907249` and
`unity-game-policy-player-20261102T052056670` contain actual Mono explicit-ON and
Player runs. An external .NET10 client reads the real Unity UI tree and queues a
public button click; the game requires exactly one consumed click before success.
Clock installation succeeds only in testing. Player also rejects Debug profile
selection through the local managed API. An earlier custom in-process Mono
WebSocket test timed out; its cause is not established and it is not counted as
success. External-client tests follow the intended tester/game deployment path.

The native template tests are in
`native-game-policy-20261102T042558021/results.json`: correct initialization,
ordinary managed-file write denial while the lease host is alive, ON replacement
refused before SDK load, preloaded SDK rejection, and a malformed Editor-template
policy returning80 without terminating its host. The Editor template's no-exit
behavior is compiled separately, not selected by a runtime permission flag.
`bindings/unity/game-build~/GameIntegrityEditor.cs.txt` is now generated into the
game's Editor source for a fixture-level integration. It checks the game-owned,
compile-time nonfatal Editor template when exiting Edit mode. Failure cancels
Play and asks for restore/restart; it never terminates the Editor. Successful
native policies and file leases survive managed domain reload. The test in
`unity-editor-policy-20261102T061851338/results.json` entered Play, exercised local
APIs, returned to Edit, and repeated that sequence in the same Editor. Full
official SDK substitution cancelled Play with status78 and independently queried
zero loaded native SDK modules. A changed game compile mode with stale generated
policy is refused with status84 before loading the checker or native SDK. The
fixture generator embeds Editor expectations before launch, never on game
startup. Project relocation or SDK/mode changes require explicit regeneration
and, once native files were loaded/leased, Editor restart.
No forced Editor termination was used. The owned disposable batch test Editors
closed through ordinary `EditorApplication.Exit` after completing their tests;
timeouts leave the Editor running and report its PID.

The installed6000.5.3f1 backend set contains Mono and WebGL support, but no Windows
IL2CPP backend. No component/Editor was installed or upgraded. IL2CPP execution,
Web startup and production release packaging remain unverified/incomplete.

## Local Unity UPM integration

`stage-unity-game-sdk.ps1` now produces an embedded-package validation SDK. It
contains provider-built native and managed variants. The developer selects
Normal distribution, Development and testing, or AI player distribution under
`Gua/Game build`; that sets the project's `GUA_REMOTE_AUTOMATION` and
`GUA_PLAYER_ONLY` compiler symbols and prepares game-owned files. Direct changes
to those project symbols also select the Player's prebuilt mode; Editor policy
changes require explicit preparation and, after a loaded session, restart.
Neither the game nor Editor startup regenerates failed integrity expectations.

`GuaGameBuildSetup.cs` creates game-specific Player/Editor verifiers and game
bootstrap source under `Assets/GameSecurity`. Common managed bindings remain
prebuilt; Unity imports exactly one prebuilt adapter through define constraints.
The ordinary Bootstrap and TMP adapter assemblies compile with the game. All
three native inputs stay in ignored `Provider~/windows` directories: they cannot
auto-load or collide as Unity native plugins. The game postprocessor emits only
the selected native pair, checks the selected managed adapter bytes, then embeds
the final managed/native hashes into the game-specific Player verifier. No
consumer Gua native source build or manual DLL swap is used.

The Player EXE is withheld under a pending name during finalization and promoted
only after policy embedding. Failures handled by this builder remove the
requested old EXE. General validation exceptions are converted to
`BuildFailedException`: the fault test exposed that Unity could otherwise log an
exception yet report success. This is not a claim that callbacks execute after
every possible earlier compiler/Editor failure; packaging must consume successful
builds from isolated outputs.

Local actual6000.5.3f1 Mono evidence:

- `unity-packaged-game-off-20261102T064415942/results.json`: normal UPM setup,
  ordinary game build, local APIs, and full official SDK replacement refused
  before native SDK load. Its `packaged-editor-play.log` and
  `editor-policy-result.txt` additionally record two real Play/Edit cycles using
  the packaged verifier, including leases on the SDK Bootstrap/TMP assemblies.
- `unity-packaged-game-testing-20261102T063037131/results.json`: actual external
  public UI action consumed using the SDK Bootstrap, with the test's manual frame
  pump removed; a failed OFF rebuild over this enabled output returned failure
  and removed its previous EXE. The initial independent frame-pump proof remains
  a separate foundation test, not the sole packaged SDK evidence.
- `unity-packaged-game-player-20261102T064458260/results.json`: the packaged
  normal frame driver consumed the external public UI action; clock installation
  and local Debug-profile elevation were refused.

Initial native/managed component inputs retain the earlier source identities
noted above. These UPM tests are local; they are not new-HEAD hosted Unity CI.
The SDK currently requires Windows x64 Mono and unchanged prebuilt main-adapter
bytes; unverified rewriting/stripping, IL2CPP, and Web builds fail explicitly.
The validation package has not replaced the public release package or been
published. Final release workflow integration remains open.

Provider templates and fixtures are stored in Unity-ignored `game-build~` and
`Tests~` directories. `unity-source-layout-20261102T071317801/results.json` records
normal legacy source-package import and local API success, plus a deliberately
imported provider source inside its Runtime asmdef being detected as the intended
compiler violation. A bare package-root source without an asmdef was separately
observed to be ignored already; that earlier placement was not demonstrated to
break normal UPM import. The explicit ignored directories clarify packaging and
keep those inputs out of assembly discovery.

## Build-only delivery artifacts

`package-game-build-candidates.ps1` packages the .NET developer SDK ZIP, its
`Gua.GameBuild` NuGet, and the GDScript SDK ZIP only after validating the expected
native source identity in all three modes. The Godot archive check requires its
hidden `.variants/.gdignore` and all three provider manifests. Delivery metadata
records native source, producer commit, clean/dirty producer status, filenames,
sizes and SHA-256. This metadata is delivery evidence, not a runtime trust root.

The Remote build variants PR workflow now produces a separate
`game-build-candidates-<commit>` artifact after its acceptance steps. It neither
publishes a release nor replaces the existing cross-platform public packages.
Local packaging succeeded in `build/game-build-candidates-local`; its metadata
explicitly records earlier native source4a and an uncommitted producer tree.
An intentionally different source identity was refused before creating output.
Hosted execution remains pending the authorized push and new-HEAD CI.

The current formal release workflow builds one development-enabled native/Unity
package for desktop/Web consumers. Replacing it with this Windows-only candidate
would remove existing platform coverage. The formal release definition now
prepares three versioned native modes and build-only .NET/GDScript candidates in
separate Windows jobs. Public release waits for those candidate checks, but the
public asset aggregation and NuGet publishing set are unchanged. The existing
pinned Unity route now captures private compiler references, and a host-side
producer builds and inspects all three managed adapter variants. The candidate
job consumes those Gua-only outputs and native verifier templates to stage a
separate Windows UPM archive. Hosted acceptance still remains before replacing
any public package.
No credentials, tags or release runs were changed or invoked. This job definition
is not hosted CI execution evidence.

## Local NuGet package integration

`scripts/pack-game-build-nuget.ps1` produces a new optional `Gua.GameBuild`
package containing the provider's three Windows prebuilt modes and generator.
It depends on the exact matching `Gua.Runtime` package version. Consumers add
that package reference and set `GuaRemoteMode` to `testing` or `player` when
needed; omission selects `off`. The buildTransitive import selects the native
pair, excludes legacy Core/Runtime native assets, and compiles the expectations
into the executable game. There is no manual target import, native DLL choice,
or consumer Gua native source build. The normal game build remains necessary.
This package integration currently targets standalone Windows x64 .NET10 games.
Using `Gua.Core` or `Gua.Runtime` alone does not install a game-owned verifier;
the optional game-build package and its generated game code are required.

`nuget-game-consumer-20261102T055155534/results.json` records PackageReference-only
publish/run in all three modes, OFF IL without a remote-start reference,
exactly the selected native pair in each published directory, ON substitution
refused before load, and explicit build refusal for an unsupported Godot host.
Dependency packages in this local feed are clearly marked private test fixtures
made from current managed assemblies, deliberately including legacy ON native
assets to test their exclusion. They are not release packages. The new package
has not been published or added to the release publishing workflow.

The GDScript SDK still refuses Godot .NET projects. The common .NET target now
has a distinct Godot.NET.Sdk 4.7.0 Windows host; see the follow-up below.
Web is also unsupported by the Windows GDScript exporter; Unity Web requires
verified static-archive selection and a game-owned Web bootstrap before it can
be counted. Existing static Web variant CI alone is not proof of consumer
startup integrity.

CI adds the same Windows tests after actual commit-specific native packages have
been built. Final commit/run evidence must be recorded after those jobs complete.
The current work does not establish complete engine/package migration, final
HEAD CI success, or release readiness.

## Remaining host boundaries

| Target | Current safe work and limitation |
| --- | --- |
| Formal producer | Three native and managed candidate producers are connected; local managed-output consumer regression passed. Hosted execution awaits push approval. Existing public platform assets remain. |
| Windows C++ | Existing MSVC can validate the loader route. Normal imports load SDK code before main. A contract covering delay imports in every SDK-consuming module remains necessary; no complete C++ guard is shipped. |
| Web | Installed Unity6000.5.3f1 includes Emscripten4.0.20-git. Native static inputs build locally without installation. Browser/engine startup and exchangeable JS/side-module paths still need a game-owned host; current SDK exporters refuse this target. |
| Godot .NET | A dedicated Windows x64 Godot.NET.Sdk 4.7.0 host is implemented and tested below. Mixed Gua GDE/managed integrations and other versions/platforms are refused. |
| Non-Windows | Platform-specific pre-load verification and actual target runners are needed. Current WSL inventory returned-1 and Docker server discovery failed; neither established a usable local target environment. No service/configuration was changed. |

`test-remote-build-variant.ps1 -ProductVersion 0.0.0-ci` was exercised locally for
Release/off using native source3318, producing
`build/versioned-game-native-shipping/package-20261102T073735623`. Native,
managed, link-graph and packaged identity acceptance passed. CMake records both
product and Godot plugin versions. Godot was disabled in this narrow local
version-injection check; the formal matrix's three GDE builds await CI.

`build/cpp-import-boundary/eager.json` observed gua.dll loaded at the first line
of main before the first SDK call. The same program using MSVC delay import
observed no module before that call and a module afterward (`delayed.json`).
This proves the ordering issue and a linker mechanism, not a complete verifier,
import-closure audit, or SDK-substitution guarantee for C++ consumers.

`test-existing-unity-web-variants.ps1` reuses installed tools with the cache frozen
and a process-local environment. Temporary files stay in the workspace. An
initial default-sandbox-TEMP access failure was not counted as a pass.
`build/existing-unity-web-20261102T075325376/results.json` records all three linked
Wasm native profile/input/reset checks and archive manifests succeeding. These
are real Wasm/Node checks, not browser, Unity Web Player, WebMCP bootstrap, or
OFF-to-ON SDK replacement guarantees. IL2CPP remains uninstalled.

## Managed producer and convergence

`GuaGameBuildProducer.BuildPlayerAndCaptureReferences` extends the existing Unity
CI build method. It captures engine API references and the compiled UI/Input
references into a private build-only directory. `build-unity-managed-variants.ps1`
uses the existing adapter compiler and verifies actual IL flags in off/testing/
player. Output directories contain exactly Gua.Core, Gua.Runtime and Gua.Unity;
engine reference DLLs remain private and are not uploaded or packed. SDK staging
checks the managed mode again, preserves dependency notices, and the delivery
manifest includes the optional Unity TGZ hash. The formal job DAG is acyclic.

The exact capture/producer route succeeded locally with installed6000.5.3f1:
`build/unity-formal-managed/managed-variants.json`. Its resulting SDK was used by
ordinary Windows game builds, not the earlier hand-built managed inputs:

- `unity-packaged-game-off-20261102T080907147/results.json`: local APIs, complete
  official ON SDK replacement refused before native load, two actual Editor Play
  and Edit cycles.
- `unity-packaged-game-testing-20261102T080959015/results.json`: external UI action
  consumed by the normal SDK driver and failed OFF rebuild removing the old EXE.
- `unity-packaged-game-player-20261102T081058296/results.json`: permitted external
  UI action, clock denial and local Debug-profile denial.

These regressions deliberately reused the consistent earlier native source4a;
managed producer/source and native input identities are distinct. They are not
all-new-HEAD native/GDE or hosted Linux-capture evidence. Local candidate TGZ
listing found no UnityEngine, UnityEditor or InputSystem reference DLL payload.
`build/game-build-full-candidates/game-sdk-delivery.json` records all candidate
archive hashes and its working-tree producer status. No public package changed.

Automatic static Web input selection still needs a game-owned host. The later
Godot .NET section covers its implemented managed initialization. Consumers use
provider prebuilts; they do not build Gua. Windows evidence cannot stand in for
Linux/macOS loaders. The official portable Godot .NET test engine/templates are
workspace inputs; no OS service or IL2CPP installation was made.

User-facing design decisions arise if Godot Web must take over a custom HTML
bootstrap, or if a Windows-only candidate is to become a public supported package
before other platforms reach parity. Those are proposals, not changes made here.
The default is to keep unsupported routes disabled and preserve the existing
public asset set, rather than build a new large host/custom engine or weaken the
stated verifier boundary. This separates remaining code work, environment needs
and product decisions; it is not an assertion that only push approval remains.
The produced Unity TGZ was also extracted locally: all47 SDK files were byte-
identical to the staged formal SDK (`build/unity-formal-archive-roundtrip/results.json`).
The archive contains no captured engine/API reference DLLs. This validates its
payload round trip; it does not claim the hosted capture job has executed.

### Windows C++ delay-load integration (follow-up to19474d7)

The accepted follow-up adds `bindings/cpp/game-build/GuaCppGame.cmake` and
`delay_guard.cpp` as GAME compilation inputs, not SDK DLL code. One
`gua_protect_cpp_game(target MODE off|testing|player PLUGINS ...)` call selects
provider prebuilts, adds MSVC delay imports, and runs `prepare-cpp` on ordinary
ALL/package builds. Default mode is off, independently of Debug/Release. The
consumer needs CMake/MSVC and .NET10 runtime for the provider-built generator;
it does not compile Gua. This is a staged candidate, not an updated public SDK.

Ordering: game compiled guard -> absolute game-specific checker -> lock/hash
every SDK native member -> absolute/System32-only load -> delay thunk uses the
verified module handle -> local SDK initialization -> explicitly compiled remote
start path. `INIT_ONCE` retains each module's bootstrap result. Context creation
keeps its existing API/lifetime. The checker resource holds expected file hashes,
mode, source and build identity; no runtime manifest can replace expectations.
Checker status publication is atomic so a dependent game DLL can safely query
readiness without entering initialization or a mutex under the loader lock.
The main EXE bootstraps before ordinary user global constructors. Declared DLLs
refuse pre-bootstrap Gua calls from DllMain/global construction (86); they never
attempt loading/hashing under that lock. Existing non-Gua delay hooks may be
forwarded explicitly, and unrelated libraries retain their usual loading.

`CppGameBuild.Check` inspects the EXE/declared module PE import closure before
deployment, rejects direct/transitive regular Gua imports and unresolved startup
dependencies, and accepts delayed Gua references. OS/API-set/CRT dependencies are
trusted. The EXE is withheld during preparation and removed on check failure.
Build failures before postprocessing can retain old outputs: distribute only a
successful isolated ALL/package build, not a failed directory or a game-target-
only no-op build. Undeclared dynamic modules and arbitrary custom loaders cannot
be discovered from import tables. Declare SDK-consuming modules, and obtain
compatible delayed builds for prebuilt external Gua-dependent DLLs. This does
not impose a guard on unrelated libraries or claim arbitrary code-injection
defence. Checker/EXE/resource replacement is outside the unchanged-game premise.
Read leases cover ordinary write/delete replacement, not all privileged directory
races. See the [consumer instructions](../bindings/cpp/game-build/README.md).

Focused acceptance mapping:

| Required behaviour | Fault/normal assertion | Evidence |
| --- | --- | --- |
| OFF/Test/Player remain usable | local UI API,16 API threads, bootstrap count1, global constructor Gua call, unrelated DLL; correct bridge availability, Player rejects Debug | `tests/cpp-game-policy/main.cpp` |
| SDK swap cannot restore remote | replace both OFF DLLs with official Test pair; exit78 and OS-observed native loads0 | `scripts/test-cpp-game-policy.ps1`, `official-on-replacement` |
| Missing input fails before SDK load | missing runtime/checker exit81 and loads0 | same script |
| No import bypass before verifier | deliberately remove EXE/plugin delay option; build fails with specific import error and deploy EXE absent | `eager-game`, `eager-transitive-plugin` |
| Do not load inside DLL initialization | declared DLL early Gua call exits86, loads0 | `early-plugin-initializer` |
| Existing unrelated dependency behaviour | ordinary unrelated DLL plus delayed import with explicit notification forwarding both succeed | `unrelated-delay-fallback` |

The initial C++ unit starts/stops the existing loopback bridge; the subsequent
archive-consumer unit below adds actual external wire/UI consumption. Windows MSVC
19.51.36260.0 / SDK10.0.26100.0 / .NET10 is the local environment. Native SDK
inputs remain the consistent earlier source4a trio, separately from the current
game guard/tool/template source. Public C++ package distribution, hosted CI,
non-Windows, Web/Godot.NET/IL2CPP acceptance remain incomplete. The prior2/2
foundation audit budget was not restarted: this follow-up is self-reviewed and
locally tested, not independently audited. No push or publication is implied.

Final local evidence (2026-11-03 UTC):

* `scripts/stage-cpp-game-sdk.ps1` staged `build/cpp-game-sdk-final` from
  `build/game-sdk-nuget-host-boundary`, source4a import libraries in
  `../gua-remote-variants/build/remote-{shipping-off,qa-on,ai-player-on}-Release`,
  and the current locally built `GamePolicyTemplate.dll`.
* `scripts/test-cpp-game-policy.ps1 -SdkDirectory build/cpp-game-sdk-final
  -OutputDirectory build/cpp-game-policy-20261103-final` passed all11 cases.
  `build/cpp-game-policy-20261103-final/results.json` contains exact exit/load
  counts; each `*-observed.json` records OS events from process creation.
* OFF/Test/Player exit0 and load2 native SDK members, with the game verifier
  first in the observed timeline. Official ON replacement exits78, missing
  SDK/checker81, early plugin86, all with0 SDK loads. Direct/transitive eager
  imports and the external legacy DLL are rejected by the specific import
  assertion; direct/transitive deployment EXEs are absent. Unrelated delayed
  import/fallback succeeds. `cpp-stage-final.log` reports0 warnings/errors in
  the managed generator build; fixture linkers warn when a module does not
  import one of the two requested delayed libraries, which is expected.
* The initial parallel sandbox MSBuild stalled before compilation. Only its
  identified owned process tree was stopped; serial `/nr:false` local builds
  completed. An initial C++ fixture Windows-header macro conflict was removed
  from the test source. The first completed script retained the last deliberately
  failed child exit code; the final script normalizes success and the final
  invocation itself exits0. None of these initial attempts is counted as final
  acceptance evidence. No Editor process was killed.

### C++ candidate delivery and real input follow-up

The shared `package-game-build-candidates.ps1` now accepts `CppSdkDirectory`,
verifies its complete file inventory, source/build identity and both import
library digests, and adds a hash-listed C++ ZIP to the build-only delivery.
`stage-cpp-imports.ps1` ties provider import libraries to the accepted native DLL
pair before producing separate input artifacts. Both the PR remote-variants and
release candidate workflows stage, package and accept that ZIP; existing public
asset names/publish set remain unchanged. Hosted execution is still pending
push approval. This follow-up is self-reviewed, not a third foundation audit.

Local command: stage SDK to `build/cpp-formal-sdk-20261103`; run the common
packager with that directory, the existing .NET/GDScript/Unity SDKs, source
`4a4debd735175df0e229c39aac33e9bf35c8db0b`, version/runtime `0.0.0-ci`, and explicit
`AllowWorkingTreeValidation`, output `build/cpp-formal-delivery-20261103`; then:

```powershell
scripts/test-cpp-game-candidate.ps1 -CandidateDirectory build/cpp-formal-delivery-20261103 -OutputDirectory build/cpp-formal-consumer-20261103
```

All7 assertions passed, recorded in the consumer `results.json`: archive digest,
44 byte-identical payload files; OFF/Test/Player compile/run using the extracted
SDK; Test and Player external WebSocket `click_node` accepted and exactly1 request
consumed/emitted successfully by the game; correct clock allowance/denial;
OFF-to-provider-ON swap exit78/load0/no host entry; archived eager-import checker
rejects an external legacy module. Detailed wire/host/OS-observer JSON is retained
beside the result. This extends a3c4407's11-case unit proof without repeating
its mutation/negative matrix. The input uses a distinct loopback port19573.
`build/cpp-import-artifact-check/results.json` also confirms the downloaded-
artifact input path yields all44 identical payload files and semantically equal
inventory, and that an intentionally changed import library is refused before
a complete SDK inventory is emitted. JSON property order is not a byte-identity
contract for separately generated inventory metadata.

An additional public1.2.0 reproduction swaps the cached NuGet Core/Runtime pair
into that packaged OFF consumer, changing neither EXE nor game checker/data:
`build/cpp-formal-public-v120-swap/result.json` and `observed.json` show exit78,
native loads0, no game-host entry. Core SHA256 is
`10e086ef508e14018bb87ef3f1e3958cccd17e05c9893b6871b2bf96bd29efb7`, runtime SHA256
`600548d026ab657b023af6b98a67ac28b300c39542c042c872a885db19264b11`.
The deprecated release is a negative-test input only, not a recommendation.
The previous provider-source4a ON substitution and this actual published1.2.0
substitution are distinct evidence.

PowerShell AST parsing and workflow YAML parsing use existing installed tools;
no parser/tool installation or hosted workflow run was performed. Windows-only
candidate acceptance does not complete Godot.NET/Web/non-Windows/IL2CPP or grant
publication approval. The expected hashes remain inside unchanged game-owned
code/data; delivery inventory/SDK manifests never authorize a runtime substitution.

## Godot .NET follow-up: Windows x64 / Godot 4.7

This section supersedes the earlier checkpoint's unsupported Godot .NET status.
The common `Gua.GameBuild` package now recognizes a real
`Godot.NET.Sdk/4.7.0` game. It accepts net10.0 Windows x64 with separate normal
managed/native files; other SDK versions, platforms, single-file, AOT and trimming
are refused. A plain library with a `project.godot` file is not a valid substitute.
The developer references the provider candidate package in the game's project and
builds/exports the game normally. `GuaRemoteMode=off` is the default; explicit
`testing` or `player` selects that provider prebuilt, independently of Debug or
Release. No consumer Gua source build or manual DLL selection is required. This
does not claim that a new public package has been released.

The [MSBuild target](../bindings/dotnet/Gua.GameBuild.targets) passes the selected
native pair and actual managed references to the generator. The expected hashes,
mode and source/build identity are compiled into the GAME assembly, together with
the protected managed Godot adapter source supplied by the provider. The OFF
adapter has its native remote-start call removed by conditional compilation; local
UI reflection stays available. The Player prebuilt enforces the native ceiling.

The [game module initializer](../tools/Gua.GameBuild/GodotGameGuard.cs.txt) checks
all SDK managed/native hashes under retained deny-write/delete file handles BEFORE
loading either native member. It rejects already loaded Gua modules and reparse
points, loads absolute native paths with System32-only dependency search, and
pins both module handles for the managed resolvers. Runtime environment paths
cannot select another SDK. The exchangeable runtime manifest never supplies new
expectations. This is file/loader enforcement, not a Gua DLL capability self-report.

The verifier is outside the four exchangeable Gua DLLs. Godot's Editor loads game
assemblies from streams, leaving `Assembly.Location` empty. Only in that case the
guard reads the trusted ENGINE's `GodotPlugins.PluginLoadContext.AssemblyLoadedPath`.
See the official 4.7 [loader](https://github.com/godotengine/godot/blob/4.7-stable/modules/mono/glue/GodotSharp/GodotPlugins/PluginLoadContext.cs)
and [project initialization](https://github.com/godotengine/godot/blob/4.7-stable/modules/mono/glue/GodotSharp/GodotPlugins/Main.cs).
The exported game's normal assembly location is its verification root. Replacing
the engine, CLR, game assembly or generated expectations is outside the threat model.

On Editor reload, an exact game path/mode/provider/hash stamp may reuse retained
verified streams and module handles. The shared record contains framework types
only, so it does not root the collectible game assembly. The builder skips copying
identical native files while the Editor retains its locks; changing SDK/mode needs
an Editor restart. Failure poisons game assembly initialization and never terminates
the Editor. Build checks reject known cached/visible legacy Gua GDExtensions,
the separate GDScript policy and duplicate managed adapter. Unrelated extensions
are not globally constrained by this host.

Failure handling must not call Godot native APIs before the exported game's
callback table exists. The guard identifies the 4.7 engine's collectible Editor
loader or the observed noncollectible CLR component loader without a Godot API
call. Only a confirmed game is terminated with 78/79; unknown contexts get an
initialization exception rather than process termination. The Editor acceptance
fixture uses its own ordinary quit callback and never kills an Editor.

Path-specific acceptance is in [actual SDK games](../scripts/test-godot-dotnet-game-policy.ps1),
[actual Release exports and full SDK substitution](../scripts/test-godot-dotnet-export.ps1),
and [Editor build/reload/refusal survival](../scripts/test-godot-dotnet-editor.ps1).
The external OS observer requires verification-before-native-load ordering.
Negative cases require policy entry, specific refusal, zero Gua native loads and
no game callback, rather than listener absence alone. The corresponding
[Windows CI step](../.github/workflows/remote-build-variants.yml) records execution
evidence; local results and final-HEAD hosted results must be reported separately.
Official NuGet 1.2.0 is a substitution reproduction input, not a recommendation.

This managed adapter retains the existing UI surface; it does not claim native
GDE spatial support. Godot Web custom HTML remains a pending design decision.
Non-Windows game-owned loaders remain unsupported and IL2CPP remains uninstalled
and untested. Native Linux/macOS CI success is not evidence for these game loaders.

Local acceptance on 2026-11-06 used the official portable Godot .NET 4.7,
.NET SDK10.0.401/runtime10.0.12. Three SDK games and three real Release exports
passed; Testing/Player consumed one external input each, with Player clock/Debug
escalation refused. Native-pair, full managed/native SDK plus manifest, and
managed-only substitutions each exited78 with zero native loads and no game
callback. The actual Editor rebuilt/unloaded/reopened the game, and survived a
specific refusal before normal quit. Five host build refusals passed. Native
inputs reused consistent CI source77d966aae50f62c4c21437e603017df5c225ee84;
the game guard/tool changes are this working-tree scope. These are not final-HEAD
native build results. Two bounded independent Godot.NET review passes reported
no actionable findings; the reviewer independently exercised locked identical
native sync (no rewrite) and a locked mode change (clean refusal). This did not
restart the earlier foundation2/2 review gate. Final hosted CI is reported
separately in the PR.

## Godot Web follow-up: game-page verification before side-module loading

The GDScript consumer setting remains `gua/remote_mode=off|testing|player`, default `off`. These mean an ordinary release without remote control, development/testing, and an AI-player-enabled release with a fixed Player ceiling. The provider builds all three inputs; a normal game export selects them automatically. Consumers do not build Gua source or manually swap native files.

The accepted target is the Godot 4.7 single-threaded, GDExtension-enabled Release Web template. The exporter hashes the selected `gua.wasm` and embeds the expected digest, source/build identity and mode into the **game HTML**, outside that exchangeable module. It inserts the verifier as the first script and rewrites the engine constructor to a game-owned factory. If CSP blocks the verifier while allowing the original bootstrap, the factory is absent and startup cannot proceed.

The guarded fetch hashes the actual response bytes before returning them to the dynamic loader, retains a private immutable reference, and returns independent copies. It never refetches the SDK after checking it. Same-basename alternate origin/query URLs and XHR fallback cannot bypass the check. The first game-PCK autoload checks the page's verified identity before registering the hidden GDExtension. The exporter explicitly adds the selected enabled WebMCP bridge even when the combined SDK prepared the native Editor target before Godot collected project files. The off export removes remote startup and the WebMCP bridge; the Player native permission ceiling remains enforced. Local APIs, including game-owned clock control, remain available; these are not remote privileges. Remote clock control is unsupported by the Web port in all modes.

Godot 4.7's JavaScriptBridge can marshal a JavaScript boolean as a GDScript integer. The handshake therefore JSON-encodes a strict JavaScript boolean comparison and accepts only a string exactly equal to `"true"`; it does not use truthiness.

The standard page and supplied reviewed `web-custom-shell.html` are supported. Other custom shells require a focused startup review and real-browser acceptance before their digest is accepted. Threads, PWA and the reserved `gua.html` output basename are refused. A failed export removes only the selected HTML/PCK, including a stale previously enabled game, not the output directory or saves. Game HTML/PCK, engine JS/Wasm, browser and verifier are trusted; modifying those is outside the SDK-only replacement threat. This is byte pinning, not a claim to control arbitrary browser loaders or modified engines.

Acceptance entry points are [actual export/refusal/stale-output tests](../scripts/test-godot-web-game-policy.ps1), [real Chrome/WebMCP consumer tests](../scripts/verify-godot-web-game-policy.ts) and [unit mutation-detection tests](../scripts/test-godot-web-game-guard.test.ts). Local official Godot 4.7/Chrome156 acceptance passed off/testing/player and the supplied custom page, exactly one correlated input in each enabled game, Player Debug-client refusal, legitimate ON-Wasm substitution with verified count0/no game/no port, and blocked verifier with SDK fetch count0. Local native input source is `a50bd054e5af3c766d4defdc4e0bd7e6168b5907`. Hosted Linux/browser, combined native/Web SDK and final-HEAD evidence are tracked separately; pending jobs are not successes. Non-Windows native game-owned loaders and IL2CPP remain outside this verified set.
