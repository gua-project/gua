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
    "web.wasm32.single.debug" = "res://addons/gua/bin/gua_godot.web.debug.wasm32.wasm"
    "web.wasm32.single.release" = "res://addons/gua/bin/gua_godot.web.release.wasm32.wasm"
}

foreach ($mapping in $expectedLibraries.GetEnumerator()) {
    $escapedKey = [regex]::Escape($mapping.Key)
    $escapedValue = [regex]::Escape($mapping.Value)
    if ($descriptor -notmatch "(?m)^$escapedKey\s*=\s*`"$escapedValue`"\s*$") {
        throw "Missing or incorrect Godot Web library mapping: $($mapping.Key) -> $($mapping.Value)"
    }

    if ($RequireBinaries) {
        $fileName = Split-Path -Leaf $mapping.Value
        $binaryPath = Join-Path $addon "bin/$fileName"
        if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
            throw "Godot Web addon binary is missing: $binaryPath"
        }
        if ((Get-Item -LiteralPath $binaryPath).Length -eq 0) {
            throw "Godot Web addon binary is empty: $binaryPath"
        }
    }
}

Write-Host "Godot Web addon Debug/Release contract verified."
