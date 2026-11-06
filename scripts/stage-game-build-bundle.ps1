param(
    [Parameter(Mandatory)][string]$ShippingPackage,
    [Parameter(Mandatory)][string]$TestingPackage,
    [Parameter(Mandatory)][string]$PlayerPackage,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Stage a game SDK into a new directory.' }
dotnet build (Join-Path $root 'tools/Gua.GameBuild') -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Cannot build game SDK generator.' }
New-Item -ItemType Directory -Path $output | Out-Null
$sourceIdentity = $null
foreach ($case in @(@{Mode='off';Package=$ShippingPackage},@{Mode='testing';Package=$TestingPackage},@{Mode='player';Package=$PlayerPackage})) {
    $manifest = Get-Content -LiteralPath (Join-Path $case.Package 'gua-build-manifest.json') -Raw | ConvertFrom-Json
    $sourceIdentity ??= $manifest.build.sourceCommit
    if ($manifest.build.sourceCommit -ne $sourceIdentity) { throw 'SDK modes must have the same provider source identity.' }
    $manifest.files = @($manifest.files | Where-Object { $_.path -in 'gua.dll','gua_runtime.dll','LICENSE' })
    $destination = Join-Path $output $case.Mode
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($entry in $manifest.files) { Copy-Item -LiteralPath (Join-Path $case.Package $entry.path) -Destination $destination }
    $manifest | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $destination 'gua-build-manifest.json') -Encoding utf8NoBOM
    & dotnet (Join-Path $root 'tools/Gua.GameBuild/bin/Release/net10.0/Gua.GameBuild.dll') generate $destination $case.Mode (Join-Path $destination 'validation.cs')
    if ($LASTEXITCODE) { throw 'Provider component validation failed.' }
    Remove-Item -LiteralPath (Join-Path $destination 'validation.cs')
}
$tools = Join-Path $output 'tools'
New-Item -ItemType Directory -Path $tools | Out-Null
foreach ($name in 'Gua.GameBuild.dll','Gua.GameBuild.deps.json','Gua.GameBuild.runtimeconfig.json','GameGuard.cs.txt','GodotGameGuard.cs.txt') {
    Copy-Item -LiteralPath (Join-Path $root "tools/Gua.GameBuild/bin/Release/net10.0/$name") -Destination $tools
}
Copy-Item -LiteralPath (Join-Path $root 'bindings/dotnet/Gua.GameBuild.targets') -Destination $output
New-Item -ItemType Directory (Join-Path $output 'godot') | Out-Null
$adapter=Get-Content (Join-Path $root 'examples/dotnet-godot/addons/gua/GuaGodotRuntime.cs') -Raw
$oldConstructor=@'
        System.Environment.SetEnvironmentVariable(
            "GUA_RUNTIME_NATIVE_DIR",
            ProjectSettings.GlobalizePath("res://addons/gua/bin"));
'@
$adapter=$adapter.Replace("`r`n","`n")
if(!$adapter.Contains($oldConstructor.Replace("`r`n","`n"))){throw 'Godot managed adapter constructor changed; update the protected producer.'}
$adapter=$adapter.Replace($oldConstructor.Replace("`r`n","`n"),'        GuaGameNativePolicy.RequireVerified();')
$oldStart='        return _runtime!.StartInspectorBridge(port);'
if(!$adapter.Contains($oldStart)){throw 'Godot managed adapter remote-start boundary changed.'}
$adapter=$adapter.Replace($oldStart,"#if GUA_REMOTE_AUTOMATION`n$oldStart`n#else`n        return false;`n#endif")
("using System;`nusing System.Collections.Generic;`nusing System.Linq;`n"+$adapter)|Set-Content (Join-Path $output 'godot/GuaGodotRuntime.cs') -Encoding utf8NoBOM
Write-Host "Staged provider-built game SDK (no game-side Gua source build): $output"
