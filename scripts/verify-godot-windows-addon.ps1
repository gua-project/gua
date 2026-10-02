param(
    [string]$AddonDirectory = "examples/godot-gdscript/addons/gua",
    [switch]$RequireBinaries
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$addon = if ([System.IO.Path]::IsPathRooted($AddonDirectory)) {
    $AddonDirectory
} else {
    Join-Path $root $AddonDirectory
}
$descriptorPath = Join-Path $addon "gua.gdextension"
& (Join-Path $PSScriptRoot 'verify-godot-addon-sources.ps1') -AddonDirectory $addon
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw

$expectedLibraries = [ordered]@{
    "windows.debug.x86_64" = "res://addons/gua/bin/gua_godot.windows.debug.x86_64.dll"
    "windows.release.x86_64" = "res://addons/gua/bin/gua_godot.windows.release.x86_64.dll"
}

foreach ($mapping in $expectedLibraries.GetEnumerator()) {
    $escapedKey = [regex]::Escape($mapping.Key)
    $escapedValue = [regex]::Escape($mapping.Value)
    if ($descriptor -notmatch "(?m)^$escapedKey\s*=\s*`"$escapedValue`"\s*$") {
        throw "Missing or incorrect Godot Windows library mapping: $($mapping.Key) -> $($mapping.Value)"
    }

    if ($RequireBinaries) {
        $fileName = Split-Path -Leaf $mapping.Value
        $binaryPath = Join-Path $addon "bin/$fileName"
        if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
            throw "Godot Windows addon binary is missing: $binaryPath"
        }
        if ((Get-Item -LiteralPath $binaryPath).Length -eq 0) {
            throw "Godot Windows addon binary is empty: $binaryPath"
        }
    }
}

if ($RequireBinaries) {
    $ambiguousRuntime = Join-Path $addon "bin/gua_runtime.dll"
    if (Test-Path -LiteralPath $ambiguousRuntime -PathType Leaf) {
        throw "Godot Windows addon must embed its runtime instead of shipping a configuration-ambiguous DLL: $ambiguousRuntime"
    }
}

Write-Host "Godot Windows addon Debug/Release contract verified."
