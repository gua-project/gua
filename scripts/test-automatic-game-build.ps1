param(
    [Parameter(Mandatory)][string]$BundleRoot,
    [string]$OfficialCoreAssembly,
    [string]$OfficialRuntimeAssembly,
    [string]$OfficialCoreNative,
    [string]$OfficialRuntimeNative
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bundle = [IO.Path]::GetFullPath($BundleRoot).TrimEnd('\','/') + '/'
$output = Join-Path $root ('build/automatic-game-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Path $output | Out-Null
dotnet build (Join-Path $root 'tools/Gua.ArtifactProbe') -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Artifact probe build failed.' }
$probe = Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
$results = [Collections.Generic.List[object]]::new()
$beforeCore=$env:GUA_NATIVE_DIR; $beforeRuntime=$env:GUA_RUNTIME_NATIVE_DIR; $beforeProfile=$env:GUA_OBSERVATION_PROFILE
try {
    # Wrong native environment paths must not override game-owned module handles.
    $env:GUA_NATIVE_DIR=Join-Path $bundle 'testing'
    $env:GUA_RUNTIME_NATIVE_DIR=$env:GUA_NATIVE_DIR
    $env:GUA_OBSERVATION_PROFILE='debug'
    foreach ($mode in 'off','testing','player') {
        $env:GUA_OBSERVATION_PROFILE = if ($mode -eq 'player') { $null } else { 'debug' }
        $consumer=Join-Path $output $mode
        dotnet build (Join-Path $root 'bindings/dotnet/tests/Gua.AutomaticGameBuild') -c Release --nologo -v quiet -m:1 -nr:false "-p:GuaGameBuildBundleRoot=$bundle" "-p:GuaRemoteMode=$mode" -o $consumer
        if ($LASTEXITCODE) { throw "Automatic $mode consumer build failed." }
        $app=Join-Path $consumer 'Gua.AutomaticGameBuild.dll'
        $il = (& dotnet $probe game $app) | ConvertFrom-Json
        if ($LASTEXITCODE -or $il.remoteStartReference -ne ($mode -ne 'off') -or !$il.gameOwnedPolicy) { throw 'Game IL does not match its compile setting.' }
        $log=Join-Path $output "$mode.log"
        & dotnet $app *> $log
        if ($LASTEXITCODE -or (Get-Content -LiteralPath $log -Raw) -notmatch "game-owned policy: $mode") { Get-Content -LiteralPath $log; throw "Automatic $mode consumer failed." }
        $results.Add(@{case=$mode;result='passed';gameIl=$il;evidence=$log})
        if ($mode -eq 'player') {
            # Existing Player-only native builds reject a Debug startup request;
            # rejection is fail-closed, not a successful Debug-profile launch.
            $env:GUA_OBSERVATION_PROFILE='debug'
            $debugLog=Join-Path $output 'player-debug-env-negative.log'
            & dotnet $app *> $debugLog
            if ($LASTEXITCODE -eq 0 -or (Get-Content -LiteralPath $debugLog -Raw) -notmatch 'Failed to create a Gua runtime') { throw 'Player mode did not reject the Debug environment profile.' }
            $results.Add(@{case='player-debug-environment';result='runtime-creation-rejected';evidence=$debugLog})
            $env:GUA_OBSERVATION_PROFILE=$null
        }
        if ($mode -eq 'off') {
            Copy-Item -LiteralPath (Join-Path $bundle 'testing/gua.dll'),(Join-Path $bundle 'testing/gua_runtime.dll') -Destination $consumer -Force
            if ($OfficialCoreAssembly -and $OfficialRuntimeAssembly -and $OfficialCoreNative -and $OfficialRuntimeNative) {
                Copy-Item -LiteralPath $OfficialCoreAssembly -Destination (Join-Path $consumer 'Gua.Core.dll') -Force
                Copy-Item -LiteralPath $OfficialRuntimeAssembly -Destination (Join-Path $consumer 'Gua.Runtime.dll') -Force
                Copy-Item -LiteralPath $OfficialCoreNative -Destination (Join-Path $consumer 'gua.dll') -Force
                Copy-Item -LiteralPath $OfficialRuntimeNative -Destination (Join-Path $consumer 'gua_runtime.dll') -Force
                $swapName='off-to-official-managed-and-native-set'
            } else { $swapName='off-to-testing-native-set' }
            $negative=Join-Path $output 'off-swap-negative.log'
            & dotnet $app *> $negative
            if ($LASTEXITCODE -ne 78 -or (Get-Content -LiteralPath $negative -Raw) -notmatch 'nativeLoaded=false') { throw 'Automatic game allowed a substituted Gua DLL set, or rejected after native load.' }
            $results.Add(@{case=$swapName;result='rejected-before-native-load';exitCode=78;evidence=$negative})
        }
    }
} finally { $env:GUA_NATIVE_DIR=$beforeCore; $env:GUA_RUNTIME_NATIVE_DIR=$beforeRuntime; $env:GUA_OBSERVATION_PROFILE=$beforeProfile }
$results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "Automatic game build evidence: $output"
$global:LASTEXITCODE=0 # Expected negative child exits are assertions, not a script failure.
