# Protected Windows C++ game builds (candidate)

The provider SDK contains prebuilt native pairs, import libraries, headers, the
game-policy template and its build tool. Consumers build their own game normally;
they do not build Gua or manually replace its DLLs. This candidate requires
Windows x64 MSVC, CMake 3.20+ and the .NET10 runtime for the supplied build tool.
It is not an already published package or a cross-platform loader.

```cmake
find_package(GuaGameBuild CONFIG REQUIRED) # GuaGameBuild_DIR = extracted SDK
add_executable(my_game main.cpp)
gua_protect_cpp_game(my_game)             # default: remote disabled
# Instead choose MODE testing, or MODE player for AI-player-enabled distribution.
```

`MODE` describes the intended use independently of the game's Debug/Release
configuration. `testing` explicitly enables automation, while `player` fixes the
native permission ceiling to Player. They define `GUA_REMOTE_AUTOMATION` and,
for Player, `GUA_PLAYER_ONLY` in the game's compilation. Wrap game-specific bridge
startup in `#if defined(GUA_REMOTE_AUTOMATION)`; local initialization remains
available in every mode. The native OFF pair also rejects bridge calls.

Build `ALL_BUILD` (the normal default) or `my_game_gua_package`, and distribute
only a successful isolated output. The helper selects the prebuilt pair, checks
the PE import closure, generates `GameSecurity_<game-id>.dll` with the final
expected hashes, and promotes the withheld EXE only after success. Neither a
runtime manifest nor an environment variable can regenerate those expectations.
Check/postprocessing failure removes the candidate EXE; compiler/linker failure
before postprocessing may leave older outputs, so do not distribute a failed
build directory. An explicit game-target-only no-op build does not run the
package target; use the normal default or the named package target for deployment.

For separately built game DLLs that use Gua:

```cmake
add_library(my_game_ui SHARED ui.cpp)
target_link_libraries(my_game PRIVATE my_game_ui)
gua_protect_cpp_game(my_game MODE testing PLUGINS my_game_ui)
```

The helper gives these DLLs delay imports too. Ordinary unrelated libraries need
no changes. A prebuilt external DLL with an ordinary Gua import is rejected when
it is in the startup import closure or supplied as a declared module; its supplier
must provide a compatible delayed build. Undeclared dynamically loaded modules
are not discoverable from PE import tables: declare SDK-consuming game DLLs in
`PLUGINS`. SDK calls from their `DllMain`/global constructors before EXE bootstrap
are refused (86), not serviced while Windows holds its loader lock. Calls after
bootstrap use the verified modules. Do not unload/reload the pinned SDK or use a
different custom SDK loader.

The EXE's compiled guard runs before ordinary game global constructors. It loads
the game-specific checker by absolute EXE-relative path; the checker locks and
hashes **both** native files before loading either, using System32-only dependency
search. The delay hook returns only those verified module handles. `INIT_ONCE`
caches bootstrap per game module; Gua contexts retain their normal independent
lifetime. Missing files (81), changed files (78), preloaded SDK (79), or wrong
mode (84) fail closed. No bridge starts merely by loading the pair.

The checker and its resource are game code/data outside the exchangeable Gua
SDK DLL set. Replacing those game-owned files, EXE code, or injecting arbitrary
code is outside this guarantee. OS/CRT dependencies are trusted. Read handles
deny ordinary file writes/deletes for process lifetime; this is not a claim of
defence against privileged directory races or every loader/filesystem attack.

MSVC has one pair of process-module delay-hook globals. Existing hooks cannot
silently coexist: provide the original global functions through
`NOTIFY_FALLBACK name FAILURE_FALLBACK name`, removing their old global variable
definitions once. Non-Gua delay imports retain their default/fallback behaviour.
Only these compatibility settings are needed once; no handwritten Gua loader
or per-build manual procedure is required.
