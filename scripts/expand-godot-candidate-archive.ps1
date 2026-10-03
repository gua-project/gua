param([Parameter(Mandatory=$true)][string]$ArchivePath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Expand-Archive -LiteralPath $ArchivePath -DestinationPath $OutputDirectory
if(-not (Test-Path (Join-Path $OutputDirectory 'addons/gua/gua_spatial.gd'))) { throw 'Actual archive spatial script missing' }
