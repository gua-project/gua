# Gua.Testing.Unity

## Unityを外部テストから起動して確認する

UnityのEditor Play Modeか、ビルドしたdesktop Mono Playerを起動して
ゲーム側adapterのbridgeに接続するtest hostです。
UIの公開と実操作はUPMのruntime adapter、探索・待機・assertionはGua.Testingが担当します。

準備するものはUnity実行ファイル、対象project/scene、runtime packageと対象platformです。
`LoadEditor`はEditor経路、`LoadPlayer`は既存Player、
`BuildAndLoadPlayer`はbuildを含む経路です。起動 → 接続 → テスト → teardownを追い、
実行ログと相関した完了・新しい観測を確認してください。

[UnitySceneTestHost](UnitySceneTestHost.cs)と
[Unity runtime文書](../../../unity/Documentation~/index.md)が入口です。
[UnityIntegrationTests](../../tests/Gua.Unity.Integration.Tests/UnityIntegrationTests.cs)は実engine準備が必要な別suiteです。
共通の準備と確認範囲は[共通の確認準備とsuite別手順](../../../../docs/developer-reading-guide.ja.md)を参照してください。
対象platformとAPIは以下を参照してください。Mono経路の確認だけでは、IL2CPPや全Editor構成の動作は確認できません。

Starts Unity 6000.5+ Editor Play Mode or Mono standalone players on Windows x64,
Linux x64, Intel macOS, and Apple Silicon macOS, and
connects `Gua.Testing` to the Unity adapter's WebSocket bridge.

Use `UnityPlayerBuilder.Build`, `UnitySceneTestHost.LoadPlayer`,
`LoadRenderedPlayer`, `LoadEditor`, or `BuildAndLoadPlayer`. The host resolves
Unity from an explicit option, `UNITY_EXECUTABLE`, then the Unity Hub install
directory. It allocates an available bridge port by default and captures Unity
logs in startup and teardown diagnostics.

`UnityPlayerBuildOptions.Platform` accepts `Auto`, `WindowsX64`, `LinuxX64`,
`MacOSX64`, `MacOSArm64`, and `MacOSUniversal`. `Auto` selects the current host.
