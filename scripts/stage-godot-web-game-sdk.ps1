param(
    [Parameter(Mandatory)][string]$ProviderInputs,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$NativeSdkDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(Test-Path $OutputDirectory){throw 'Use a new SDK staging directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
if($NativeSdkDirectory){Copy-Item (Join-Path $NativeSdkDirectory 'addons') $out -Recurse}
$addon=(New-Item -ItemType Directory (Join-Path $out 'addons/gua') -Force).FullName
$variants=(New-Item -ItemType Directory (Join-Path $addon '.web_variants')).FullName
Set-Content (Join-Path $variants '.gdignore') ''
$identity=$null
if($NativeSdkDirectory){
    $identity=(Get-Content (Join-Path $NativeSdkDirectory 'addons/gua/.variants/off/gua-build-manifest.json') -Raw|ConvertFrom-Json).build.sourceCommit
    foreach($mode in 'testing','player'){
        if((Get-Content (Join-Path $NativeSdkDirectory "addons/gua/.variants/$mode/gua-build-manifest.json") -Raw|ConvertFrom-Json).build.sourceCommit -ne $identity){throw 'Native SDK modes have different sources.'}
    }
}
foreach($case in @(@{mode='off';flavor='shipping';ceiling='none'},@{mode='testing';flavor='qa';ceiling='debug'},@{mode='player';flavor='ai-player';ceiling='player'})) {
    $package=Join-Path $ProviderInputs $case.mode
    $manifest=Get-Content (Join-Path $package 'gua-build-manifest.json') -Raw|ConvertFrom-Json
    $b=$manifest.build;$identity ??= $b.sourceCommit
    if($manifest.schemaVersion -ne 1 -or $manifest.engineBuildKind -ne 'release' -or $b.configuration -ne 'Release' -or $b.sourceCommit -notmatch '^[a-f0-9]{40}$' -or $b.sourceCommit -ne $identity -or $b.flavor -ne $case.flavor -or $b.remoteEnabled -ne ($case.mode -ne 'off') -or $b.remotePermissionCeiling -ne $case.ceiling){throw 'Web provider identity/mode mismatch.'}
    $required=@('gua.wasm','gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd')
    if($case.mode -ne 'off'){$required+='gua_webmcp_bridge.gd'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($entry in $manifest.files) {
        if($entry.path -notin $required -or !$seen.Add($entry.path) -or $entry.sha256 -notmatch '^[a-f0-9]{64}$'){throw 'Unexpected/duplicate Web SDK member.'}
        $file=Join-Path $package $entry.path
        if((Get-Item $file).Attributes -band [IO.FileAttributes]::ReparsePoint -or (Get-FileHash $file).Hash.ToLowerInvariant() -ne $entry.sha256){throw 'Modified/linked provider input.'}
    }
    if($seen.Count -ne $required.Count){throw 'Missing Web provider member.'}
    $actual=@(Get-ChildItem $package -Recurse -File)
    if($actual.Count -ne $required.Count+1){throw 'Unmanifested Web provider file.'}
    Copy-Item $package (Join-Path $variants $case.mode) -Recurse
}
$editor=(New-Item -ItemType Directory (Join-Path $addon 'editor') -Force).FullName
foreach($name in 'plugin.gd','export.gd','policy.gd.txt','native_guard.gd','web_export.gd','web_guard.js.txt','web_policy.gd.txt','web-custom-shell.html'){Copy-Item (Join-Path $root "bindings/godot/game-build/$name") $editor -Force}
Set-Content (Join-Path $editor '.gdignore') ''
foreach($name in 'gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd'){Copy-Item (Join-Path $root "examples/godot-gdscript/addons/gua/$name") $addon -Force}
@'
[plugin]
name="Gua game build"
description="Select provider prebuilts and verify them from game-owned code."
author="Gua"
version="0.1.0"
script="editor/plugin.gd"
'@|Set-Content (Join-Path $addon 'plugin.cfg') -Encoding utf8NoBOM
Copy-Item (Join-Path $root 'LICENSE') $out
$global:LASTEXITCODE=0
