# Gua.Core

## このパッケージを使う場面

C#からGuaのツリーを読み、操作要求や追加観測をnative coreへ渡すためのbindingです。
C ABIは言語をまたぐnative関数の境界で、P/InvokeはC#からその関数を呼ぶ仕組みです。
このpackageがC#だけの別runtimeを作るわけではありません。
普通のテストのlocator/assertionは[Gua.Testing](../Gua.Testing/README.md)、
engine側でframeを公開するadapterは[Gua.Runtime](../Gua.Runtime/README.md)を先に選びます。

操作は「公開済みUIを読む → EnqueueActionで要求ID取得 → hostがconsumeし実処理 →
EmitActionResult → 同じIDの完了をpoll」です。受理だけで成功とは判断しません。
[GuaContext](GuaContext.cs)が基本API、
[GuaValue](GuaValue.cs)と
[Observe API](GuaObserve.cs)が追加値の入口です。
`RID`はOS/architectureごとのnative資産の選択名です。
下のtarget/resolver/packing説明と[共通の確認準備とsuite別手順](../../../../docs/developer-reading-guide.ja.md)を読み、Selector suiteでbinding境界を確認します。
今回はローカル手順を実行していません。native testの成功だけでengine内の適用を保証しません。

`Gua.Core` is the .NET binding for the native Gua C ABI.

The package includes `net10.0` and `netstandard2.1` managed assemblies. The
`net10.0` target keeps Gua's development-time native resolver and
`GUA_NATIVE_DIR` override. The `netstandard2.1` target uses ordinary UTF-8
`DllImport` declarations so Unity owns native discovery through its Plugin
Import Settings.

The NuGet package includes native libraries for the supported desktop RIDs:

```text
runtimes/win-x64/native/gua.dll
runtimes/linux-x64/native/libgua.so
runtimes/osx-x64/native/libgua.dylib
runtimes/osx-arm64/native/libgua.dylib
```

.NET copies that native asset to the consuming app or test output as part of
normal package restore/build. `GUA_NATIVE_DIR` remains available when you want to
override the packaged runtime with a locally built one.

For Unity 6 Windows Editor x64 setup, including managed dependencies and native
plugin placement, see the
[Unity smoke guide](https://github.com/link1345/gua/tree/main/examples/unity-smoke).

Semantic operations use `EnqueueAction` and return a `requestId`. The host adapter
consumes the request, performs the real UI operation, then calls
`EmitActionResult`; tests observe completion with `TryPollActionEvent`. Supported
v1 actions are focus, set value, set checked, select, scroll, and key press.
Click remains available through the original API and shares the same queue.

`GuaContext.Clock` is the context-owned `GuaClock` Scheduler/Tick surface.
Constructing `new GuaClock(context)` registers that instance as the owned clock,
so test controls and game code drain the same scheduled work. A context accepts
only one managed clock; reuse `context.Clock` instead of constructing a second
instance.

At runtime the resolver checks:

1. `GUA_NATIVE_DIR`
2. the .NET output directory
3. the assembly directory
4. the current directory

To pack only the legacy Windows local asset, build the release native runtime
first. For a distributable package, stage all four RID directories below one
root and pass its absolute path as `GuaNativeAssetsRoot`; packing fails if the
property is omitted without the legacy Windows DLL, or if any staged library is
absent.

```powershell
cmake --preset windows-msvc-release
cmake --build --preset windows-msvc-release --target gua
dotnet pack bindings/dotnet/src/Gua.Core/Gua.Core.csproj --configuration Release
```

The complete staging layout is `win-x64/gua.dll`,
`linux-x64/libgua.so`, `osx-x64/libgua.dylib`, and
`osx-arm64/libgua.dylib`.

`GuaContext.ConfigureDiagnostics` sets the bounded retained-history limit and
environment JSON. `GetDiagnosticsJson` returns the versioned semantic failure
snapshot without draining the event queue; sensitive action values are already
redacted by the native core.

Attach `GuaAgentPolicy` to node or world descriptors to define the Player
projection. Field rules support omit, redact, typed replacement, and numeric
quantization; an optional UI action list is intersected with role support.
When a world policy omits `Exposure`, it inherits the descriptor's legacy
`AgentExposure`; set it explicitly only to override that value.
`GetUiTreeJson(GuaObservationProfile.Player)` and the matching diagnostics
overload preview the exact host-side projection without changing Debug defaults.

`GuaWorldSelector.Near` performs an additive v2 query around a stable object ID;
`Limit` is optional and must be positive. `GuaWorldQueryResult` reports the
evaluated World snapshot's epoch/frame/revision and, for nearby queries, aligned
distances ordered by distance then object ID. Distances use the publisher's
world units and are evaluated after Debug/Player projection.
