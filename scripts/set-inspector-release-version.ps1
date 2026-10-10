param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
    throw "Inspector release version must use X.Y.Z; received '$Version'."
}
$path = Join-Path $RepositoryRoot 'packages/inspector/src-tauri/tauri.conf.json'
$config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
$config.version = $Version
$config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
if ((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).version -ne $Version) {
    throw 'Inspector release version was not applied.'
}
