param(
    [Parameter(Mandatory)][string]$UnityExecutable,
    [Parameter(Mandatory)][string]$ManagedDirectory,
    [Parameter(Mandatory)][string]$EditorAssembly,
    [Parameter(Mandatory)][string]$NativeDirectory,
    [Parameter(Mandatory)][string]$UnityPackageCache,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ('build/unity-variant-' + $Flavor + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
$managed = Join-Path $fixture 'Assets/Plugins/Managed'; $native = Join-Path $fixture 'Assets/Plugins/Native'; $editor = Join-Path $fixture 'Assets/Editor'
New-Item -ItemType Directory -Force $managed,$native,$editor,(Join-Path $fixture 'Packages'),(Join-Path $fixture 'ProjectSettings') | Out-Null
$dependencies=@{'com.unity.modules.uielements'='1.0.0';'com.unity.modules.screencapture'='1.0.0';'com.unity.modules.imageconversion'='1.0.0'}
foreach ($packageName in 'com.unity.inputsystem','com.unity.ugui') {
    $packageSource=@(Get-ChildItem -LiteralPath $UnityPackageCache -Directory -Filter "$packageName@*")
    if ($packageSource.Count -ne 1) { throw "Expected one existing $packageName source package." }
    Copy-Item -LiteralPath $packageSource[0].FullName -Destination (Join-Path $fixture "Packages/$packageName") -Recurse
    # This consumer uses package runtime/editor sources, not their NUnit test projects.
    $copiedTests=[IO.Path]::GetFullPath((Join-Path $fixture "Packages/$packageName/Tests"))
    if (-not $copiedTests.StartsWith([IO.Path]::GetFullPath($fixture)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture test path.' }
    if (Test-Path -LiteralPath $copiedTests) { Remove-Item -LiteralPath $copiedTests -Recurse -Force }
    $dependencies[$packageName]="file:$packageName"
}
@{dependencies=$dependencies} | ConvertTo-Json | Set-Content (Join-Path $fixture 'Packages/manifest.json') -Encoding utf8NoBOM
'm_EditorVersion: 6000.5.3f1' | Set-Content (Join-Path $fixture 'ProjectSettings/ProjectVersion.txt') -Encoding utf8NoBOM
foreach ($assembly in 'Gua.Core.dll','Gua.Runtime.dll','Gua.Unity.dll') {
    Copy-Item -LiteralPath (Join-Path $ManagedDirectory $assembly) -Destination $managed
}
& (Join-Path $PSScriptRoot 'copy-unity-managed-closure.ps1') -AssetsFile (Join-Path $root 'bindings/dotnet/src/Gua.Runtime/obj/project.assets.json') -TargetFramework netstandard2.1 -Destination $managed
Copy-Item -LiteralPath $EditorAssembly -Destination (Join-Path $editor 'Gua.Unity.Editor.dll')
Copy-Item -LiteralPath (Join-Path $root 'scripts/unity-meta/Gua.Unity.Editor.dll.meta') -Destination $editor
foreach ($library in 'gua.dll','gua_runtime.dll') {
    Copy-Item -LiteralPath (Join-Path $NativeDirectory $library) -Destination $native
    Copy-Item -LiteralPath (Join-Path $root "scripts/unity-meta/$library.meta") -Destination $native
}
Copy-Item -LiteralPath (Join-Path $NativeDirectory 'gua-build-manifest.json') -Destination $native
@'
using System;
using System.IO;
using System.Text.Json;
using Gua.Core;
using Gua.Runtime;
using Gua.Unity;
using UnityEngine;
public static class GuaVariantConsumer {
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Run() {
        try {
            using var runtime=new GuaRuntime();
            using var identity=JsonDocument.Parse(runtime.GetBuildInfoJson());
            var info=identity.RootElement;
            bool remote=info.GetProperty("remoteEnabled").GetBoolean();
            bool player=info.GetProperty("remotePermissionCeiling").GetString()=="player";
            if(remote!=GuaUnityCompiledBuild.RemoteEnabled || player!=GuaUnityCompiledBuild.PlayerOnly) throw new Exception("Unity IL/native mismatch");
            runtime.BeginFrame("unity-consumer");
            runtime.RegisterNode(new GuaNodeDescriptor("public","button","Public",new GuaBounds(0,0,10,10)));
            runtime.RegisterNode(new GuaNodeDescriptor("secret","button","Private",new GuaBounds(0,0,10,10),AgentPolicy:new GuaAgentPolicy(GuaAgentExposure.Private)));
            runtime.EndFrame();
            if(runtime.GetUiTreeJson().Contains("Private")==player) throw new Exception("Player projection mismatch");
            if(player) {
                bool rejected=false;
                try {runtime.SetObservationProfile(GuaObservationProfile.Debug);} catch(InvalidOperationException){rejected=true;}
                if(!rejected) throw new Exception("AI Player elevated");
            }
            if(runtime.StartInspectorBridge(19463)!=remote) throw new Exception("Bridge availability mismatch");
            runtime.StopInspectorBridge();
            File.WriteAllText(Path.Combine(Application.dataPath,"..","result.json"),runtime.GetBuildInfoJson());
            Debug.Log("GUA_UNITY_VARIANT_PASS"); Application.Quit(0);
        } catch(Exception error) {Debug.LogException(error); Application.Quit(1);}
    }
}
'@ | Set-Content (Join-Path $fixture 'Assets/GuaVariantConsumer.cs') -Encoding utf8NoBOM
@'
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
public static class GuaVariantFixtureBuild {
    public static void Run() {
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene,"Assets/Consumer.unity");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone,ScriptingImplementation.Mono2x);
        var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions {scenes=new[]{"Assets/Consumer.unity"},target=BuildTarget.StandaloneWindows64,
            locationPathName="Player/Consumer.exe",options=Environment.GetEnvironmentVariable("GUA_BUILD_VARIANT")=="development" ? BuildOptions.Development : BuildOptions.None});
        if(report.summary.result!=BuildResult.Succeeded) throw new Exception("Variant Player build failed");
    }
}
'@ | Set-Content (Join-Path $editor 'GuaVariantFixtureBuild.cs') -Encoding utf8NoBOM
$previousVariant=$env:GUA_BUILD_VARIANT; $previousProfile=$env:GUA_OBSERVATION_PROFILE
try {
    $env:GUA_BUILD_VARIANT=$Flavor; $env:GUA_OBSERVATION_PROFILE=''
    $buildLog=Join-Path $fixture 'editor.log'
    $process=Start-Process -FilePath $UnityExecutable -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',"`"$fixture`"",'-executeMethod','GuaVariantFixtureBuild.Run','-logFile',"`"$buildLog`"") -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(600000)) { & taskkill.exe /PID $process.Id /T /F | Out-Null; throw "Unity build timed out: $buildLog" }
    if ($process.ExitCode -ne 0 -or -not (Test-Path (Join-Path $fixture 'Player/Consumer.exe'))) { throw "Unity Player build failed ($($process.ExitCode)): $buildLog" }
    $playerLog=Join-Path $fixture 'player.log'
    $process=Start-Process -FilePath (Join-Path $fixture 'Player/Consumer.exe') -ArgumentList @('-batchmode','-nographics','-logFile',"`"$playerLog`"") -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { & taskkill.exe /PID $process.Id /T /F | Out-Null; throw "Unity consumer timed out: $playerLog" }
    $result=Get-Content -LiteralPath (Join-Path $fixture 'Player/result.json') -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $result.flavor -ne $Flavor -or $result.remoteEnabled -ne ($Remote -eq 'on') -or
        (Get-Content $playerLog -Raw) -notmatch 'GUA_UNITY_VARIANT_PASS') { throw "Unity consumer failed: $playerLog" }
    Write-Host "Actual Unity Player accepted $Flavor / $Remote : $fixture"
} finally {$env:GUA_BUILD_VARIANT=$previousVariant; $env:GUA_OBSERVATION_PROFILE=$previousProfile}
