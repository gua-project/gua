# Native Toolchains

## native実装をビルドして確認する

C#やTypeScriptの呼出しが最終的に利用するC ABIのcore/runtimeを、対象OSのcompilerで作るための案内です。
C ABIは言語間の関数境界、C++20は実装側の言語、MSVCはWindowsで使うcompilerです。
configureはbuild条件と出力directoryを決める段階、buildは実際にlibraryとtest実行ファイルを作る段階です。

Windowsでは[現在のCMakePresets.json](../CMakePresets.json)がVisual Studio 18 2026/x64と
Debug/Releaseの出力先を指定します。CMake 3.21以上に加え、そのgeneratorを扱えるCMakeと
対応Visual StudioのC++ workloadが必要です。下のpresetコマンドにはその環境を用意します。
[Ninjaによる限定した確認準備とCTest](developer-reading-guide.ja.md)も参照してください。
libraryのbuild成功、native unit test、managed binding、実engine適用は別々の確認です。

[root CMakeLists.txt](../CMakeLists.txt)がtargetの入口、
[native core](../native/gua-core/CMakeLists.txt)と
[runtime](../native/gua-runtime/CMakeLists.txt)がlibrary/testを定義します。
以下の各platformコマンドは今回未実行・未検証です。
iOS/Androidの将来方針を、現在の検証済みdesktop経路と同じ対応保証にはしません。

Gua's native reference implementation is developed first on Windows with MSVC.
That is the primary local toolchain for early C++ work.

The project should still keep the native core portable:

- Public native boundary: C ABI
- C++ implementation: standard C++20
- Windows: MSVC
- macOS: the native core, runtime, and WebSocket bridge are built with Apple Clang on Intel and Apple Silicon
- iOS: Apple Clang when this target becomes active
- Android: Android NDK Clang
- Linux: the native core, runtime, WebSocket bridge, and native bridge example
  are built in CI with the default Ubuntu C++ toolchain

Do not put Windows API calls, MSVC-only extensions, or platform-specific behavior
inside protocol-level code. If platform code becomes necessary, isolate it under
a platform-specific native directory.

## Windows MSVC

Use the CMake presets as the official Windows entrypoint:

```powershell
cmake --preset windows-msvc-debug
cmake --build --preset windows-msvc-debug
```

Release build:

```powershell
cmake --preset windows-msvc-release
cmake --build --preset windows-msvc-release
```

## Linux CI

Portable native targets are configured and built on `ubuntu-latest`:

```sh
cmake -S . -B build/cpp -DCMAKE_BUILD_TYPE=Debug
cmake --build build/cpp --parallel
```

The portable native matrix also runs on Intel and Apple Silicon macOS. It builds
the shared runtime and native bridge example, runs CTest, and exercises the
Inspector WebSocket contract through the .NET selector suite. The Win32 DirectX
11 ImGui example remains Windows-only.

## Apple And Android

Desktop macOS is validated with Apple Clang for both `osx-x64` and `osx-arm64`.
iOS and Android should be added as separate CMake presets or toolchain files
when those targets become active. The native API shape should not change for
them; they should consume the same C ABI.
