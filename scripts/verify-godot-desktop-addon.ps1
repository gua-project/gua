param(
    [string]$AddonDirectory = "examples/godot-gdscript/addons/gua",
    [ValidateSet("win-x64", "linux-x64", "osx-x64", "osx-arm64", "all")]
    [string]$Rid = "all",
    [switch]$RequireBinaries
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$addon = if ([IO.Path]::IsPathRooted($AddonDirectory)) { $AddonDirectory } else { Join-Path $root $AddonDirectory }
& (Join-Path $PSScriptRoot 'verify-godot-addon-sources.ps1') -AddonDirectory $addon
$descriptor = Get-Content -LiteralPath (Join-Path $addon "gua.gdextension") -Raw
$libraries = [ordered]@{
    "win-x64" = @{
        "windows.debug.x86_64" = "gua_godot.windows.debug.x86_64.dll"
        "windows.release.x86_64" = "gua_godot.windows.release.x86_64.dll"
    }
    "linux-x64" = @{
        "linux.debug.x86_64" = "gua_godot.linux.debug.x86_64.so"
        "linux.release.x86_64" = "gua_godot.linux.release.x86_64.so"
    }
    "osx-x64" = @{
        "macos.debug.x86_64" = "gua_godot.macos.debug.x86_64.dylib"
        "macos.release.x86_64" = "gua_godot.macos.release.x86_64.dylib"
    }
    "osx-arm64" = @{
        "macos.debug.arm64" = "gua_godot.macos.debug.arm64.dylib"
        "macos.release.arm64" = "gua_godot.macos.release.arm64.dylib"
    }
}

$selected = if ($Rid -eq "all") { $libraries.Keys } else { @($Rid) }
foreach ($selectedRid in $selected) {
    foreach ($mapping in $libraries[$selectedRid].GetEnumerator()) {
        $resource = "res://addons/gua/bin/$($mapping.Value)"
        if ($descriptor -notmatch "(?m)^$([regex]::Escape($mapping.Key))\s*=\s*`"$([regex]::Escape($resource))`"\s*$") {
            throw "Missing or incorrect Godot library mapping: $($mapping.Key) -> $resource"
        }
        if ($RequireBinaries) {
            $binary = Join-Path $addon "bin/$($mapping.Value)"
            if (-not (Test-Path -LiteralPath $binary -PathType Leaf) -or (Get-Item -LiteralPath $binary).Length -eq 0) {
                throw "Godot addon binary is missing or empty: $binary"
            }
        }
    }
}

if ($RequireBinaries) {
    foreach ($ambiguous in "gua_runtime.dll", "libgua_runtime.so", "libgua_runtime.dylib") {
        $path = Join-Path $addon "bin/$ambiguous"
        if (Test-Path -LiteralPath $path -PathType Leaf) { throw "Godot addon must embed its runtime: $path" }
    }
}

Write-Host "Godot desktop addon contract verified for $($selected -join ', ')."
