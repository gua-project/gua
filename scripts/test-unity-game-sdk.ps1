param(
    [Parameter(Mandatory)][string]$UnityExecutable,
    [Parameter(Mandatory)][string]$ExistingFixture,
    [Parameter(Mandatory)][string]$SdkDirectory,
    [ValidateSet('off','testing','player')][string]$Mode='off',
    [string]$OfficialUnityPackage
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('build/unity-packaged-game-'+$Mode+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Force (Join-Path $fixture 'Assets/Editor') | Out-Null
foreach($folder in 'Packages','ProjectSettings') {Copy-Item (Join-Path $ExistingFixture $folder) -Destination $fixture -Recurse}
foreach($entry in Get-ChildItem (Join-Path $ExistingFixture 'Assets') | Where-Object {$_.Name -notin 'Plugins','Plugins.meta','Editor','Editor.meta','GameSecurity','GameSecurity.meta','csc.rsp','csc.rsp.meta','GameOwnedPolicyConsumer.cs','GameOwnedPolicyConsumer.cs.meta'}) {Copy-Item $entry.FullName -Destination (Join-Path $fixture 'Assets') -Recurse}
Copy-Item (Join-Path $ExistingFixture 'Assets/Editor/GuaVariantFixtureBuild.cs') -Destination (Join-Path $fixture 'Assets/Editor')
@'
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
public static class GameSdkFaultFixture {
    public static void SelectOff() {
        var flags=PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone).Split(';').Where(f=>f!="GUA_REMOTE_AUTOMATION"&&f!="GUA_PLAYER_ONLY");
        PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone,string.Join(";",flags));
    }
}
'@ | Set-Content (Join-Path $fixture 'Assets/Editor/GameSdkFaultFixture.cs') -Encoding utf8NoBOM
$package=Join-Path $fixture 'Packages/com.link1345.gua'
Copy-Item -LiteralPath $SdkDirectory -Destination $package -Recurse
$manifest=Join-Path $fixture 'Packages/manifest.json'
$dependencies=Get-Content $manifest -Raw | ConvertFrom-Json
$dependencies.dependencies | Add-Member -NotePropertyName 'com.link1345.gua' -NotePropertyValue 'file:com.link1345.gua' -Force
$dependencies | ConvertTo-Json -Depth 10 | Set-Content $manifest -Encoding utf8NoBOM
function Invoke-OwnedEditor([string]$Method,[string]$Log,[switch]$ExpectFailure,[switch]$NoQuit) {
    $arguments=@('-batchmode','-nographics','-projectPath',"`"$fixture`"",'-executeMethod',$Method,'-logFile',"`"$Log`"")
    if(!$NoQuit) {$arguments+='-quit'}
    $editor=Start-Process -FilePath $UnityExecutable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddMinutes(5)
    while(!$editor.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) {}
    if(!$editor.HasExited) {throw "Editor remains pending PID$($editor.Id); no termination attempted. $Log"}
    if($ExpectFailure) {if(!$editor.ExitCode) {throw 'A fault-injected build unexpectedly succeeded.'}}
    elseif($editor.ExitCode) {throw "Owned Editor failed: $Log"}
}
$configure=if($Mode -eq 'off') {'ConfigureOff'} elseif($Mode -eq 'testing') {'ConfigureTesting'} else {'ConfigurePlayer'}
Invoke-OwnedEditor "Gua.Unity.Editor.GuaGameBuildSetup.$configure" (Join-Path $fixture 'prepare.log')
$settings=Get-Content (Join-Path $fixture 'Assets/GameSecurity/build-settings.json') -Raw | ConvertFrom-Json
$gameSource=(Get-Content (Join-Path $root 'bindings/unity/Tests~/Fixtures/GameOwnedPolicyConsumer.cs') -Raw).Replace('GameSecurity_fixture','GameSecurity_'+$settings.id)
$pump='GuaUnityRuntime.RunFrame();'
if(-not $gameSource.Contains($pump)) {throw 'Cannot remove the fixture frame pump; the SDK must supply its normal driver.'}
$gameSource=$gameSource.Replace($pump,'// The packaged SDK Bootstrap drives frames; no fixture pump.')
$gameSource | Set-Content (Join-Path $fixture 'Assets/GameOwnedPolicyConsumer.cs') -Encoding utf8NoBOM
$beforeVariant=$env:GUA_BUILD_VARIANT;$beforeProfile=$env:GUA_OBSERVATION_PROFILE;$beforePort=$env:GUA_BRIDGE_PORT
try {
    $env:GUA_BUILD_VARIANT=if($Mode -eq 'off') {'shipping'} elseif($Mode -eq 'testing') {'qa'} else {'ai-player'}
    $env:GUA_OBSERVATION_PROFILE=$null;$env:GUA_BRIDGE_PORT='19473'
    Invoke-OwnedEditor 'GuaVariantFixtureBuild.Run' (Join-Path $fixture 'build.log')
    $player=Join-Path $fixture 'Player/Consumer.exe';$data=Join-Path $fixture 'Player/Consumer_Data'
    dotnet build (Join-Path $root 'tools/Gua.ProcessLoadProbe') -c Release --nologo -v quiet -m:1 -nr:false
    if($LASTEXITCODE) {throw 'Cannot build independent load observer.'}
    $observer=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
    $observation=Join-Path $fixture 'positive.native-loads.json';$log=Join-Path $fixture 'player.log'
    if($Mode -eq 'off') {
        & dotnet $observer $player $observation -batchmode -nographics -logFile $log
        if($LASTEXITCODE) {throw 'Player observer failed.'}
    } else {
        dotnet build (Join-Path $root 'tools/Gua.EngineWireProbe') -c Release --nologo -v quiet -m:1 -nr:false
        if($LASTEXITCODE) {throw 'Cannot build wire client.'}
        $wire=Join-Path $root 'tools/Gua.EngineWireProbe/bin/Release/net10.0/Gua.EngineWireProbe.dll'
        $observe=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @("`"$observer`"","`"$player`"","`"$observation`"",'-batchmode','-nographics','-logFile',"`"$log`"") -WindowStyle Hidden -PassThru
        try {
            $deadline=[DateTime]::UtcNow.AddSeconds(30);$ready=$false
            while(!$observe.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                if((Test-Path $log) -and [string](Get-Content $log -Raw) -match 'GAME_OWNED_UNITY_READY') {$ready=$true;break}
                Start-Sleep -Milliseconds 100
            }
            if(!$ready) {throw 'Packaged Player did not reach ready state.'}
            & dotnet $wire ws://127.0.0.1:19473 $Mode *> (Join-Path $fixture 'wire.log')
            if($LASTEXITCODE -or !$observe.WaitForExit(30000)) {throw 'Packaged Player real communication failed.'}
        } finally {if(!$observe.HasExited) {$observe.Kill($true)}} # Owned Player observer only, never Editor.
    }
    $observed=Get-Content $observation -Raw | ConvertFrom-Json
    if($observed.childExitCode -ne 0 -or @($observed.guaNativeLoads).Count -ne 2 -or [string](Get-Content $log -Raw) -notmatch 'GAME_OWNED_UNITY_SUCCESS') {throw 'The real packaged Player failed.'}
    $results=[Collections.Generic.List[object]]::new()
    $results.Add(@{case=$Mode;result='upm-prepare-build-play-passed';evidence=$observation})
    if($Mode -eq 'off' -and $OfficialUnityPackage) {
        foreach($file in Get-ChildItem (Join-Path $OfficialUnityPackage 'Runtime/Plugins/Managed') -Filter '*.dll' -File) {Copy-Item $file.FullName -Destination (Join-Path $data 'Managed') -Force}
        foreach($name in 'gua.dll','gua_runtime.dll') {Copy-Item (Join-Path $OfficialUnityPackage "Runtime/Plugins/Windows/x86_64/$name") -Destination (Join-Path $data 'Plugins/x86_64') -Force}
        $negative=Join-Path $fixture 'official-swap.native-loads.json'
        & dotnet $observer $player $negative -batchmode -nographics -logFile (Join-Path $fixture 'swap.log')
        $observed=Get-Content $negative -Raw | ConvertFrom-Json
        if($LASTEXITCODE -or $observed.childExitCode -ne 78 -or @($observed.guaNativeLoads).Count -ne 0) {throw 'Packaged game accepted a complete official ON SDK replacement.'}
        $results.Add(@{case='official-sdk-swap';result='refused-before-native-load';evidence=$negative})
    }
    if($Mode -eq 'off') {
        Copy-Item (Join-Path $root 'bindings/unity/Tests~/Fixtures/GameIntegrityEditorFixture.cs') -Destination (Join-Path $fixture 'Assets/GameSecurity/Editor')
        $beforeRefusal=$env:GUA_EDITOR_EXPECT_REFUSAL
        try {
            $env:GUA_EDITOR_EXPECT_REFUSAL='0'
            $editorLog=Join-Path $fixture 'packaged-editor-play.log'
            Invoke-OwnedEditor 'GameIntegrityEditorFixture.Run' $editorLog -NoQuit
            if([string](Get-Content (Join-Path $fixture 'editor-policy-result.txt') -Raw) -notmatch '^PASS: actual-play-local-api-and-return-to-edit-twice') {throw 'The packaged SDK could not reenter Play safely.'}
            $results.Add(@{case='packaged-editor-replay';result='play-local-api-return-to-edit-twice';evidence=$editorLog})
        } finally {$env:GUA_EDITOR_EXPECT_REFUSAL=$beforeRefusal}
    }
    if($Mode -eq 'testing') {
        # A failed switch to OFF must not leave this enabled executable at the
        # selected output name. Faults affect only this disposable project.
        Remove-Item -LiteralPath (Join-Path $package 'Provider~/windows/off/gua.dll')
        Invoke-OwnedEditor 'GameSdkFaultFixture.SelectOff' (Join-Path $fixture 'select-off.log')
        $failedLog=Join-Path $fixture 'failed-off-build.log'
        Invoke-OwnedEditor 'GuaVariantFixtureBuild.Run' $failedLog -ExpectFailure
        if(Test-Path $player) {throw 'A failed OFF build left the previous enabled executable.'}
        if([string](Get-Content $failedLog -Raw) -notmatch 'ValidateProvider|GuaGameBuildSetup') {throw 'The injected OFF failure did not reach provider validation.'}
        $results.Add(@{case='failed-off-over-enabled-output';result='build-failed-old-executable-removed';evidence=$failedLog})
    }
    $results | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $fixture 'results.json') -Encoding utf8NoBOM
    Write-Host "Packaged Unity game SDK evidence: $fixture"
} finally {$env:GUA_BUILD_VARIANT=$beforeVariant;$env:GUA_OBSERVATION_PROFILE=$beforeProfile;$env:GUA_BRIDGE_PORT=$beforePort}
$global:LASTEXITCODE=0
