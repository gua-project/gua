param(
    [Parameter(Mandatory)][string]$ShippingPackage,
    [Parameter(Mandatory)][string]$TestingPackage,
    [Parameter(Mandatory)][string]$PlayerPackage,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PolicyTemplate
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$PolicyTemplate){$PolicyTemplate=Join-Path $root 'build/game-policy-template/native/gua-game-policy/Release/GodotPolicyTemplate.dll'}
if(!(Test-Path $PolicyTemplate -PathType Leaf)){throw 'The provider must build the Godot game verification template before SDK staging.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Stage into a new SDK directory.' }
$addon=Join-Path $output 'addons/gua'
New-Item -ItemType Directory -Force (Join-Path $addon '.variants') | Out-Null
Set-Content -LiteralPath (Join-Path $addon '.variants/.gdignore') -Value ''
$sourceIdentity=$null
foreach ($case in @(@{Mode='off';Flavor='shipping';Remote='off';Package=$ShippingPackage},@{Mode='testing';Flavor='qa';Remote='on';Package=$TestingPackage},@{Mode='player';Flavor='ai-player';Remote='on';Package=$PlayerPackage})) {
    & (Join-Path $PSScriptRoot 'verify-remote-build.ps1') -Directory $case.Package -Flavor $case.Flavor -Remote $case.Remote
    $manifest=Get-Content -LiteralPath (Join-Path $case.Package 'gua-build-manifest.json') -Raw | ConvertFrom-Json
    $sourceIdentity ??= $manifest.build.sourceCommit
    if ($sourceIdentity -ne $manifest.build.sourceCommit) { throw 'Godot SDK modes have different source identities.' }
    Copy-Item -LiteralPath $case.Package -Destination (Join-Path $addon ".variants/$($case.Mode)") -Recurse
}
foreach ($name in 'plugin.gd','export.gd','policy.gd.txt','native_guard.gd','web_export.gd','web_guard.js.txt','web_policy.gd.txt','web-custom-shell.html') {
    New-Item -ItemType Directory -Force (Join-Path $addon 'editor') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "bindings/godot/game-build/$name") -Destination (Join-Path $addon 'editor')
}
Set-Content -LiteralPath (Join-Path $addon 'editor/.gdignore') -Value ''
Copy-Item $PolicyTemplate (Join-Path $addon 'editor/GodotPolicyTemplate.dll')
@{schemaVersion=1;sha256=(Get-FileHash $PolicyTemplate).Hash.ToLowerInvariant();producerCommit=(& git -C $root rev-parse HEAD).Trim();producerHasUncommittedChanges=![string]::IsNullOrWhiteSpace([string](& git -C $root status --porcelain))}|ConvertTo-Json|Set-Content (Join-Path $addon 'editor/native-guard-template.json') -Encoding utf8NoBOM
foreach ($name in 'gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd') {
    Copy-Item -LiteralPath (Join-Path $root "examples/godot-gdscript/addons/gua/$name") -Destination $addon
}
@'
[plugin]
name="Gua game build"
description="Select prebuilt Gua modes and generate game-owned native verification."
author="Gua"
version="0.1.0"
script="editor/plugin.gd"
'@ | Set-Content -LiteralPath (Join-Path $addon 'plugin.cfg') -Encoding utf8NoBOM
Write-Host "Staged Godot game SDK: $output"
