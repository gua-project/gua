param(
    [Parameter(Mandatory)][string]$NativeAssetsRoot,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('true','false')][string]$EnableRemoteAutomation
)
$ErrorActionPreference = 'Stop'
$enabled = $EnableRemoteAutomation -eq 'true'
if (($Flavor -eq 'shipping' -and $enabled) -or ($Flavor -in 'qa','ai-player' -and -not $enabled)) { throw 'Invalid package purpose and remote opt-in.' }
$ceiling = if (-not $enabled) { 'none' } elseif ($Flavor -eq 'ai-player') { 'player' } else { 'debug' }
$source = $null; $buildId = $null
foreach ($rid in 'win-x64','linux-x64','osx-x64','osx-arm64') {
    $directoryPath = Join-Path $NativeAssetsRoot $rid
    $manifest = Get-Content -LiteralPath (Join-Path $directoryPath 'gua-build-manifest.json') -Raw | ConvertFrom-Json
    $identity = $manifest.build
    if ($manifest.schemaVersion -ne 1 -or $identity.flavor -ne $Flavor -or $identity.remoteEnabled -ne $enabled -or $identity.remotePermissionCeiling -ne $ceiling -or
        $identity.sourceCommit -notmatch '^[0-9a-f]{40}$' -or [string]::IsNullOrWhiteSpace($identity.buildId) -or ($Flavor -ne 'development' -and $identity.configuration -ne 'Release')) { throw "Wrong native variant: $rid" }
    if ($source -and ($identity.sourceCommit -ne $source -or $identity.buildId -ne $buildId)) { throw 'Native RIDs came from different builds.' }
    $source = $identity.sourceCommit; $buildId = $identity.buildId
    $names = if ($rid -eq 'win-x64') { @('gua.dll','gua_runtime.dll') } elseif ($rid -eq 'linux-x64') { @('libgua.so','libgua_runtime.so') } else { @('libgua.dylib','libgua_runtime.dylib') }
    foreach ($name in $names) {
        $entry = @($manifest.files | Where-Object path -CEQ $name)
        if ($entry.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $directoryPath $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry[0].sha256) { throw "Unverified native asset: $rid/$name" }
    }
}
Write-Host "Verified all native RID manifests: $Flavor, remote $EnableRemoteAutomation"
