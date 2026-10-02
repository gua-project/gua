param([Parameter(Mandatory = $true)][string]$AddonDirectory, [switch]$RequireLicense)
$ErrorActionPreference = 'Stop'
foreach ($name in 'gua.gdextension', 'gua.gdextension.uid', 'gua_auto_adapter.gd', 'gua_observe_owner.gd', 'gua_spatial.gd', 'gua_webmcp_bridge.gd', 'plugin.cfg', 'plugin.gd', 'plugin.gd.uid') {
    $path = Join-Path $AddonDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Godot addon source is missing or empty: $name"
    }
}
if ($RequireLicense) {
    foreach ($name in 'LICENSE', 'godot-cpp.LICENSE.txt') {
        $path = Join-Path $AddonDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Godot addon license is missing: $name" }
    }
}
