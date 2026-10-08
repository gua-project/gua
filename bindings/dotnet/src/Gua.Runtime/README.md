# Gua.Runtime

## engine adapterのための役割

ゲーム側が「現在の画面を公開し、外部から来た操作を実行する」ためのwrapperです。
例えば設定画面をadapterがframeとして公開すると、外部テストはチェックボックスを見つけられます。
テスト用locatorではなく、host側の公開・consume・完了報告・bridgeを担当します。

「BeginFrame → node登録 → EndFrameで公開 → TryConsumeAction → engineの実処理 →
EmitActionResult」の順で追ってください。game inputも受理と適用を分け、
adapterがhost threadで注入してCompleteGameInputを報告します。
Action Mapは操作の一覧、capabilityは初期化済みの機能の宣言です。
Debugの入力経路を初期化してもPlayerへ同じ権限が自動付与されるわけではありません。
GuaClockも、明示的に利用したゲーム処理だけを制御します。

[GuaRuntime](GuaRuntime.cs)はUI/bridgeの入口、
[game-input API](GuaGameInput.cs)は保持・lease・cleanupの入口です。
[ObserveTransportTests](../../tests/Gua.Selector.Tests/ObserveTransportTests.cs)等のruntime経路と
[共通の確認準備とsuite別手順](../../../../docs/developer-reading-guide.ja.md)を参照してください。下のAPI条件を保持し、受理結果からゲーム上の成功を推定しません。

Semantic Game Action descriptor v2 exposes `Category`, `Aliases`, `Tags`, and
`AgentExposure`. Use `FindGameInputActions` with a `GuaGameInputActionSelector`
for bounded, profile-aware discovery; its result revision must be treated as a
snapshot because execution is revalidated against the current Action Map.

Managed `net10.0` and `netstandard2.1` wrapper over the stable Gua runtime C
ABI. Engine adapters use it to publish semantic frames, consume actions,
complete screenshot requests, expose adapter versions, and run the Inspector
WebSocket bridge without duplicating P/Invoke declarations.

`GuaRuntime.Clock` is the adapter-side clock pump. Adapters call
`AdvanceMilliseconds(unscaledDeltaMs)` when their engine exposes elapsed time
as a floating-point value, or `Advance(unscaledDelta)` when a `TimeSpan` is
already available. Game code uses `Schedule` and `Tick` for deterministic
pause/run-for behavior. Installing the
clock does not intercept `Time.deltaTime`, coroutines, or engine timers; each
game subsystem that should be controllable must explicitly use this clock as
its time source.

`Tick` receives `GuaClockDelta`, which retains the native double-precision
millisecond value even below `TimeSpan`'s 100 ns resolution. It exposes
`TotalMilliseconds`, `TotalSeconds`, and an explicitly fallible `TimeSpan`
projection. Clock status likewise keeps `NowMilliseconds` as the authoritative
protocol value; `Now` is null when that value exceeds `TimeSpan`'s range.
Adapter callback failures are reported by `CallbackFailed` after the scheduler
isolates the failure and continues the remaining due callbacks and tick
notification.

The NuGet package deploys `gua_runtime.dll`, `libgua_runtime.so`, or
`libgua_runtime.dylib` from its `win-x64`, `linux-x64`, `osx-x64`, and
`osx-arm64` native assets. `GUA_RUNTIME_NATIVE_DIR` remains available for local
build overrides.

World queries use `GuaWorldSelector.Near` plus an optional positive `Limit` for
same-snapshot radius search. The runtime delegates XY/XYZ distance evaluation to
the native core and returns snapshot metadata and aligned spatial distances, so
engine adapters and remote bridges do not implement their own geometry.

Local distributable packing requires an absolute `GuaNativeAssetsRoot` whose
four RID directories contain `gua_runtime.dll`, `libgua_runtime.so`, or
`libgua_runtime.dylib` as appropriate. Packing fails when that root is omitted
without the legacy Windows Release DLL, or when any required RID asset is
missing.

Screenshot adapters should use `TryCompleteScreenshot`. It returns `false`
when a timeout, cancellation, or context reset has already invalidated the
request; such a late completion is benign and must not be retried.

## Game input adapter API

Publish a complete action-map frame with `PublishGameInputActions`, then enable
only the initialized `GuaGameInputCapabilities` with `EnableGameInput` and a
shutdown action that synchronously neutralizes every injected host value before
the native runtime is destroyed. Local consumers create a
`GuaGameInputSession`; `Dispose` queues owner-scoped neutral cleanup. Adapters
call `TryConsumeGameInput`, inject the request on the host thread, and then call
`CompleteGameInput`. Call `TickGameInputLeases` once per host frame with
unscaled elapsed time. Enqueue acceptance and host completion are deliberately
separate; local callers use `GuaGameInputSession.PollResult(requestId)` for the
correlated completion. Lease timing never uses `GuaRuntime.Clock`.
The optional third `EnableGameInput` argument is the Player/Public Agent
capability ceiling and defaults to `None`. Trusted engine bridges create a
Player session with `CreateGameInputSession(GuaObservationProfile.Player)`;
the runtime intersects that authorization with initialized adapter capabilities
at enqueue and again immediately before host consumption.
