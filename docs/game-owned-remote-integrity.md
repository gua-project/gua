# Game-owned remote integrity: implementation and evidence

This is an implementation work log, not a claim that the complete engine/package
migration is finished. It describes the Windows x64 .NET and Godot GDScript paths. Unity,
Godot .NET, Web, final NuGet/UPM distribution, and other operating systems still
need their own integration and acceptance evidence. Existing public v1.2.0
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
   PATH, the working directory or application directory. The verified loaded core
   satisfies runtime's core dependency. Windows/CLR/system dependencies remain
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
- `godot-game-policy-20261102T053520109/results.json`: all modes repeated with
  non-elidable Release checks, actual WebSocket public UI action consumption and
  Player clock denial. Failed OFF policy generation over an enabled destination
  returned a build error and removed the explicitly selected old EXE. Windows
  supplies a `.tmp` export path to the plugin; cleanup maps it to its final `.exe`.
  The editor plugin directory is excluded from game exports. A conflicting
  game-policy autoload is refused without overwriting the existing policy.

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

`unity-game-policy-off-20261102T045242758/results.json` records actual Unity
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
Actual Editor Play cancellation/restart/replay integration remains unimplemented.
No forced Editor termination was used.

The installed6000.5.3f1 backend set contains Mono and WebGL support, but no Windows
IL2CPP backend. No component/Editor was installed or upgraded. IL2CPP execution,
Web startup and normal UPM/NuGet integration remain unverified/incomplete.

CI adds the same Windows tests after actual commit-specific native packages have
been built. Final commit/run evidence must be recorded after those jobs complete.
The current work does not establish complete engine/package migration, final
HEAD CI success, or release readiness.
