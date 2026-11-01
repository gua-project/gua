param(
    [Parameter(Mandatory)][string]$ShippingPackage,
    [Parameter(Mandatory)][string]$QAPackage,
    [Parameter(Mandatory)][string]$AIPlayerPackage
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'tools/Gua.ArtifactProbe/Gua.ArtifactProbe.csproj') -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Cannot build the binary probe.' }
$verify = Join-Path $PSScriptRoot 'verify-remote-build.ps1'
& $verify -Directory $ShippingPackage -Flavor shipping -Remote off
& $verify -Directory $QAPackage -Flavor qa -Remote on
& $verify -Directory $AIPlayerPackage -Flavor ai-player -Remote on
$evidence = Join-Path $root ('build/package-mixing-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $evidence | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Test-MixedFile([string]$Name,[string]$Base,[string]$Flavor,[string]$Remote,[string]$Relative,[bool]$RewriteHash,[string]$ExpectedReason) {
    $directoryPath = Join-Path $evidence $Name
    Copy-Item -LiteralPath $Base -Destination $directoryPath -Recurse
    Copy-Item -LiteralPath (Join-Path $QAPackage $Relative) -Destination (Join-Path $directoryPath $Relative) -Force
    if ($RewriteHash) {
        $manifestPath = Join-Path $directoryPath 'gua-build-manifest.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $entry = @($manifest.files | Where-Object path -CEQ $Relative)
        if ($entry.Count -eq 1) { $entry[0].sha256 = (Get-FileHash -LiteralPath (Join-Path $directoryPath $Relative)).Hash.ToLowerInvariant() }
        else { $manifest.files += @{path=$Relative;sha256=(Get-FileHash -LiteralPath (Join-Path $directoryPath $Relative)).Hash.ToLowerInvariant()} }
        $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    }
    $reason = $null
    try { & $verify -Directory $directoryPath -Flavor $Flavor -Remote $Remote *> (Join-Path $evidence "$Name.log") }
    catch { $reason = $_.Exception.Message }
    if (-not $reason -or $reason -notmatch $ExpectedReason) { throw "Fault $Name did not trigger the required assertion: $reason" }
    $results.Add(@{case=$Name;rejected=$true;reason=$reason})
}
Test-MixedFile 'shipping-qa-core' $ShippingPackage shipping off 'gua.dll' $false 'Packaged bytes differ'
Test-MixedFile 'shipping-rehashed-qa-core' $ShippingPackage shipping off 'gua.dll' $true 'binary identity differs'
Test-MixedFile 'player-rehashed-qa-runtime' $AIPlayerPackage ai-player on 'gua_runtime.dll' $true 'binary identity differs'
Test-MixedFile 'player-rehashed-qa-godot' $AIPlayerPackage ai-player on 'addons/gua/bin/gua_godot.windows.release.x86_64.dll' $true 'binary identity differs'
Test-MixedFile 'shipping-extra-webmcp' $ShippingPackage shipping off 'addons/gua/gua_webmcp_bridge.gd' $false 'Unmanifested package file'
Test-MixedFile 'shipping-rehashed-webmcp' $ShippingPackage shipping off 'addons/gua/gua_webmcp_bridge.gd' $true 'Remote implementation found'
if ($results.Count -ne 6) { throw 'Missing fault results.' }
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'results.json') -Encoding utf8NoBOM
Write-Host "All six deliberate package mixing faults rejected: $evidence"
