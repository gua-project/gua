param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64', 'all')][string]$Rid = 'all'
)
$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('gua-addon-verify-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $temporary
$addon = Join-Path $temporary 'addons/gua'
& (Join-Path $PSScriptRoot 'verify-godot-addon-sources.ps1') -AddonDirectory $addon -RequireLicense
& (Join-Path $PSScriptRoot 'verify-godot-desktop-addon.ps1') -AddonDirectory $addon -Rid $Rid -RequireBinaries
if ($Rid -eq 'all') { & (Join-Path $PSScriptRoot 'verify-godot-web-addon.ps1') -AddonDirectory $addon -RequireBinaries }
Write-Host "Verified packaged Godot archive: $ArchivePath (composition only; engine execution is a separate gate)."
