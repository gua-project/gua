param([Parameter(Mandatory = $true)][string]$AddonDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'examples/godot-gdscript/addons/gua'
New-Item -ItemType Directory -Force $AddonDirectory | Out-Null
foreach ($name in 'gua.gdextension', 'gua.gdextension.uid', 'gua_auto_adapter.gd', 'gua_observe_owner.gd', 'gua_spatial.gd', 'gua_webmcp_bridge.gd', 'plugin.cfg', 'plugin.gd', 'plugin.gd.uid') {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $AddonDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $AddonDirectory -Force
Copy-Item -LiteralPath (Join-Path $root 'native/gua-godot/godot-cpp.LICENSE.txt') -Destination $AddonDirectory -Force
& (Join-Path $PSScriptRoot 'verify-godot-addon-sources.ps1') -AddonDirectory $AddonDirectory -RequireLicense
