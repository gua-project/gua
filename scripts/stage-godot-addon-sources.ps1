param([Parameter(Mandatory = $true)][string]$AddonDirectory, [string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -and $Version -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
    throw "Godot release version must use X.Y.Z; received '$Version'."
}
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'examples/godot-gdscript/addons/gua'
New-Item -ItemType Directory -Force $AddonDirectory | Out-Null
foreach ($name in 'gua.gdextension', 'gua.gdextension.uid', 'gua_auto_adapter.gd', 'gua_observe_owner.gd', 'gua_spatial.gd', 'gua_webmcp_bridge.gd', 'plugin.cfg', 'plugin.gd', 'plugin.gd.uid') {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $AddonDirectory -Force
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $AddonDirectory -Force
Copy-Item -LiteralPath (Join-Path $root 'native/gua-godot/godot-cpp.LICENSE.txt') -Destination $AddonDirectory -Force
if ($Version) {
    $path = Join-Path $AddonDirectory 'plugin.cfg'
    $config = Get-Content -LiteralPath $path -Raw
    $pattern = '(?m)^version="[^"]*"\r?$'
    if ([regex]::Matches($config, $pattern).Count -ne 1) { throw 'Expected exactly one Godot plugin version.' }
    [regex]::Replace($config, $pattern, "version=`"$Version`"") | Set-Content -LiteralPath $path -Encoding utf8NoBOM -NoNewline
}
& (Join-Path $PSScriptRoot 'verify-godot-addon-sources.ps1') -AddonDirectory $AddonDirectory -RequireLicense
