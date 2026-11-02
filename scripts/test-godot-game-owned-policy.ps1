param(
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string]$GodotExecutable,
    [string]$ReleaseTemplate,
    [string]$OfficialGodotExtension
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root ('build/godot-game-policy-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Path $output | Out-Null
$beforeData=$env:APPDATA; $beforeLocal=$env:LOCALAPPDATA; $beforeProfile=$env:GUA_OBSERVATION_PROFILE
$results=[Collections.Generic.List[object]]::new()
dotnet build (Join-Path $root 'tools/Gua.ProcessLoadProbe') -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Cannot build the independent Windows DLL-load observer.' }
$observer=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
function Invoke-ObservedGodot([string]$Executable,[string[]]$Arguments,[string]$Log) {
    $observation=$Log+'.native-loads.json'
    & dotnet $observer $Executable $observation @Arguments --log-file $Log
    if ($LASTEXITCODE) { throw 'The independent DLL observer failed; native-load absence is unverified.' }
    return Get-Content -LiteralPath $observation -Raw | ConvertFrom-Json
}
try {
    $env:GUA_OBSERVATION_PROFILE=$null
    foreach ($mode in 'off','testing','player') {
        $fixture=Join-Path $output $mode
        New-Item -ItemType Directory -Path $fixture | Out-Null
        Copy-Item -LiteralPath (Join-Path $SdkDirectory 'addons') -Destination $fixture -Recurse
        Copy-Item -LiteralPath (Join-Path $root 'native/gua-godot/tests/game_owned_policy_fixture.gd') -Destination (Join-Path $fixture 'fixture.gd')
        @"
config_version=5
[application]
config/name="Gua game policy $mode"
run/main_scene="res://fixture.tscn"
run/max_fps=120
[editor_plugins]
enabled=PackedStringArray("res://addons/gua/plugin.cfg")
[gua]
remote_mode="$mode"
[rendering]
renderer/rendering_method="gl_compatibility"
"@ | Set-Content -LiteralPath (Join-Path $fixture 'project.godot') -Encoding utf8NoBOM
        @'
[gd_scene load_steps=2 format=3]
[ext_resource type="Script" path="res://fixture.gd" id="1"]
[node name="Fixture" type="Control"]
script=ExtResource("1")
'@ | Set-Content -LiteralPath (Join-Path $fixture 'fixture.tscn') -Encoding utf8NoBOM
        $env:APPDATA=Join-Path $output "userdata-$mode"; $env:LOCALAPPDATA=Join-Path $output "localdata-$mode"
        New-Item -ItemType Directory -Force $env:APPDATA,$env:LOCALAPPDATA | Out-Null
        $importLog=Join-Path $fixture 'import.log'
        & $GodotExecutable --headless --editor --path $fixture --import *> $importLog
        if ($LASTEXITCODE -or (Get-Content -LiteralPath $importLog -Raw) -match 'SCRIPT ERROR|Parse Error') { Get-Content -LiteralPath $importLog; throw 'Godot SDK import failed.' }
        $positive=Join-Path $fixture 'positive.log'
        $observed=Invoke-ObservedGodot $GodotExecutable @('--headless','--path',$fixture,'--quit-after','600') $positive
        if ($observed.childExitCode -ne 0 -or @($observed.guaNativeLoads).Count -ne 1 -or (Get-Content -LiteralPath $positive -Raw) -notmatch "GAME_OWNED_GODOT_SUCCESS: $mode") { Get-Content -LiteralPath $positive; throw 'Godot game positive failed.' }
        $results.Add(@{case=$mode;result='passed';evidence=$positive})
        if ($ReleaseTemplate) {
            if (!(Test-Path -LiteralPath $ReleaseTemplate -PathType Leaf)) { throw 'The existing Godot Release template was not found.' }
            $template=[IO.Path]::GetFullPath($ReleaseTemplate).Replace('\','/')
            $exportDirectory=Join-Path $fixture 'export'
            New-Item -ItemType Directory -Path $exportDirectory | Out-Null
            @"
[preset.0]
name="Windows"
platform="Windows Desktop"
runnable=true
export_filter="all_resources"
include_filter=""
exclude_filter=""
export_path="export/game.exe"
script_export_mode=2
[preset.0.options]
custom_template/release="$template"
binary_format/embed_pck=true
texture_format/bptc=false
"@ | Set-Content -LiteralPath (Join-Path $fixture 'export_presets.cfg') -Encoding utf8NoBOM
            $exportLog=Join-Path $fixture 'export.log'
            & $GodotExecutable --headless --path $fixture --export-release Windows *> $exportLog
            if ($LASTEXITCODE -or (Get-Content -LiteralPath $exportLog -Raw) -match 'SCRIPT ERROR|Parse Error') { Get-Content -LiteralPath $exportLog; throw 'Game-owned Godot export failed.' }
            $exportExe=Join-Path $exportDirectory 'game.exe'
            $exportPositive=Join-Path $fixture 'export-positive.log'
            $observed=Invoke-ObservedGodot $exportExe @('--headless','--quit-after','600') $exportPositive
            if ($observed.childExitCode -ne 0 -or @($observed.guaNativeLoads).Count -ne 1 -or (Get-Content -LiteralPath $exportPositive -Raw) -notmatch "GAME_OWNED_GODOT_SUCCESS: $mode") { Get-Content -LiteralPath $exportPositive; throw 'Exported Godot game positive failed.' }
            $nativeFiles=@(Get-ChildItem -LiteralPath $exportDirectory -Recurse -Filter '*gua*.dll' -File)
            if ($nativeFiles.Count -ne 1 -or $nativeFiles[0].Name -ne 'gua.dll') { throw 'Export mixed unexpected Gua native files.' }
            $results.Add(@{case="release-exe-$mode";result='passed';evidence=$exportPositive;nativeFiles=@($nativeFiles.FullName)})
            if ($mode -eq 'off') {
                $testing=Join-Path $fixture 'addons/gua/.variants/testing'
                $manifest=Get-Content -LiteralPath (Join-Path $testing 'gua-build-manifest.json') -Raw | ConvertFrom-Json
                $entry=@($manifest.files | Where-Object { $_.path -match '^addons/gua/bin/.*\.windows\.release\.x86_64\.dll$' })
                if ($entry.Count -ne 1) { throw 'Missing official-shape ON replacement extension.' }
                $replacement=if ($OfficialGodotExtension) {$OfficialGodotExtension} else {Join-Path $testing $entry[0].path}
                Copy-Item -LiteralPath $replacement -Destination $nativeFiles[0].FullName -Force
                $exportNegative=Join-Path $fixture 'export-swap-negative.log'
                $observed=Invoke-ObservedGodot $exportExe @('--headless','--quit-after','30') $exportNegative
                $exitCode=$observed.childExitCode
                $text=Get-Content -LiteralPath $exportNegative -Raw
                if (@($observed.guaNativeLoads).Count -ne 0 -or $text -notmatch 'native hash mismatch; nativeLoaded=false' -or $text -match 'GAME_OWNED_GODOT_SUCCESS') { Get-Content -LiteralPath $exportNegative; throw 'Exported OFF game accepted a replaced ON GDExtension.' }
                $results.Add(@{case='release-exe-off-to-on-gdextension';result='rejected-before-extension-load';exitCode=$exitCode;evidence=$exportNegative})
            }
            if ($mode -eq 'testing') {
                # Re-export over a real enabled destination with a missing OFF
                # provider input. Stale compiled policy/remaps must not enable it.
                $offPackage=Join-Path $fixture 'addons/gua/.variants/off'
                $offManifest=Get-Content -LiteralPath (Join-Path $offPackage 'gua-build-manifest.json') -Raw | ConvertFrom-Json
                $offEntry=@($offManifest.files | Where-Object { $_.path -match '^addons/gua/bin/.*\.windows\.release\.x86_64\.dll$' })
                if($offEntry.Count -ne 1) {throw 'Missing bounded OFF fault-injection target.'}
                Remove-Item -LiteralPath (Join-Path $offPackage $offEntry[0].path)
                $settings=Join-Path $fixture 'project.godot'
                (Get-Content -LiteralPath $settings -Raw).Replace('remote_mode="testing"','remote_mode="off"') | Set-Content -LiteralPath $settings -Encoding utf8NoBOM
                $failedExportLog=Join-Path $fixture 'failed-policy-export.log'
                & $GodotExecutable --headless --path $fixture --export-release Windows *> $failedExportLog
                $exportExit=$LASTEXITCODE
                if($exportExit -eq 0) {throw 'Godot reported success after required policy generation failed.'}
                $refusedGameLog=Join-Path $fixture 'failed-export-game.log'
                if(Test-Path -LiteralPath $exportExe) {
                    $observed=Invoke-ObservedGodot $exportExe @('--headless','--quit-after','30') $refusedGameLog
                    if(@($observed.guaNativeLoads).Count -ne 0 -or ([string](Get-Content -LiteralPath $refusedGameLog -Raw)) -match 'GAME_OWNED_GODOT_SUCCESS') {throw 'Failed export retained an enabled runnable game.'}
                    $failureResult='build-failed-and-no-sdk-native-load'
                } else {$failureResult='build-failed-and-explicit-old-exe-removed'}
                $results.Add(@{case='failed-off-regeneration-over-enabled-export';result=$failureResult;exportExitCode=$exportExit;evidence=$failedExportLog})
            }
        }
        if ($mode -eq 'off') {
            $testing=Join-Path $fixture 'addons/gua/.variants/testing'
            $manifest=Get-Content -LiteralPath (Join-Path $testing 'gua-build-manifest.json') -Raw | ConvertFrom-Json
            $entry=@($manifest.files | Where-Object { $_.path -match '^addons/gua/bin/.*\.windows\.release\.x86_64\.dll$' })
            if ($entry.Count -ne 1) { throw 'Missing ON replacement extension.' }
            $replacement=if ($OfficialGodotExtension) {$OfficialGodotExtension} else {Join-Path $testing $entry[0].path}
            Copy-Item -LiteralPath $replacement -Destination (Join-Path $fixture 'gua-native/gua.dll') -Force
            $negative=Join-Path $fixture 'swap-negative.log'
            $observed=Invoke-ObservedGodot $GodotExecutable @('--headless','--path',$fixture,'--quit-after','30') $negative
            $exitCode=$observed.childExitCode
            $negativeText=Get-Content -LiteralPath $negative -Raw
            # Godot's Windows OS.kill(self) terminates immediately with code0.
            # Require the specific rejection and absence of the later game callback.
            if (@($observed.guaNativeLoads).Count -ne 0 -or $negativeText -notmatch 'native hash mismatch; nativeLoaded=false' -or $negativeText -match 'GAME_OWNED_GODOT_SUCCESS') { Get-Content -LiteralPath $negative; throw 'Godot OFF-to-ON swap was not rejected before load.' }
            $results.Add(@{case='off-to-on-gdextension';result='rejected-before-extension-load';exitCode=$exitCode;evidence=$negative})
            $policyFile=Join-Path $fixture 'gua_generated/policy.gd'
            $policy=(Get-Content -LiteralPath $policyFile -Raw).Replace("`r`n","`n")
            $marker="func _enter_tree() -> void:`n"
            if (-not $policy.Contains($marker)) { throw 'Cannot apply Godot early-load mutation.' }
            $policy.Replace($marker,$marker+"`tGDExtensionManager.load_extension(DESCRIPTOR)`n") | Set-Content -LiteralPath $policyFile -Encoding utf8NoBOM
            $mutationLog=Join-Path $fixture 'early-load-mutation.log'
            $observed=Invoke-ObservedGodot $GodotExecutable @('--headless','--path',$fixture,'--quit-after','30') $mutationLog
            $mutationText=Get-Content -LiteralPath $mutationLog -Raw
            if (@($observed.guaNativeLoads).Count -ne 1 -or $mutationText -notmatch 'Gua was loaded before game verification; nativeLoaded=true' -or $mutationText -match 'GAME_OWNED_GODOT_SUCCESS') { Get-Content -LiteralPath $mutationLog; throw 'Godot no-load condition did not detect early-load mutation.' }
            $results.Add(@{case='early-gdextension-load-mutation';result='no-load-contract-violation-detected';evidence=$mutationLog})
        }
        if ($mode -eq 'player') {
            # A pre-existing autoload must not silently replace the game verifier.
            $settings=Join-Path $fixture 'project.godot'
            $guard=Join-Path $fixture 'gua_generated/policy.gd'
            $guardHash=(Get-FileHash -LiteralPath $guard -Algorithm SHA256).Hash
            'extends Node' | Set-Content -LiteralPath (Join-Path $fixture 'other_policy.gd') -Encoding utf8NoBOM
            (Get-Content -LiteralPath $settings -Raw).Replace('*res://gua_generated/policy.gd','*res://other_policy.gd').Replace('remote_mode="player"','remote_mode="off"') | Set-Content -LiteralPath $settings -Encoding utf8NoBOM
            $collisionLog=Join-Path $fixture 'autoload-collision.log'
            & $GodotExecutable --headless --editor --path $fixture --import *> $collisionLog
            if ([string](Get-Content -LiteralPath $collisionLog -Raw) -notmatch 'GuaGamePolicy autoload is already assigned to another script' -or (Get-FileHash -LiteralPath $guard -Algorithm SHA256).Hash -ne $guardHash) { throw 'An autoload collision was not rejected before changing the game policy.' }
            $results.Add(@{case='autoload-collision';result='refused-without-overwriting-policy';evidence=$collisionLog})
            Add-Content -LiteralPath $settings -Value "`n[dotnet]`nproject/assembly_name=`"UnsupportedManagedGame`""
            $managedLog=Join-Path $fixture 'unsupported-managed-host.log'
            & $GodotExecutable --headless --editor --path $fixture --import *> $managedLog
            if ([string](Get-Content -LiteralPath $managedLog -Raw) -notmatch 'Godot .NET requires a separate verified managed/native host' -or (Get-FileHash -LiteralPath $guard -Algorithm SHA256).Hash -ne $guardHash) {throw 'An unsupported managed host was silently treated as protected.'}
            $results.Add(@{case='unsupported-godot-dotnet';result='refused-without-overwriting-policy';evidence=$managedLog})
        }
    }
} finally { $env:APPDATA=$beforeData; $env:LOCALAPPDATA=$beforeLocal; $env:GUA_OBSERVATION_PROFILE=$beforeProfile }
$results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "Godot game-owned policy evidence: $output"
$global:LASTEXITCODE=0
