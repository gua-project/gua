param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote,
    [Parameter(Mandatory)][string]$QARuntimeLibrary,
    [switch]$DesktopOnly
)
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'This fault uses an actual Windows QA DLL and requires a Windows host.' }
$root=Split-Path -Parent $PSScriptRoot
$verify=Join-Path $PSScriptRoot 'verify-unity-variant-package.ps1'
& $verify -Directory $PackageDirectory -Flavor $Flavor -Remote $Remote -DesktopOnly:$DesktopOnly
$evidence=Join-Path $root ('build/upm-mixing-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $evidence | Out-Null
$results=@()
foreach ($fault in 'renamed-qa','rehashed-qa') {
    $fixture=Join-Path $evidence $fault
    Copy-Item -LiteralPath $PackageDirectory -Destination $fixture -Recurse
    $native=Join-Path $fixture 'Runtime/Plugins/Windows/x86_64'
    if ($fault -eq 'renamed-qa') {
        Copy-Item -LiteralPath $QARuntimeLibrary -Destination (Join-Path $native 'gua_runtime_old.dll')
        $expected='Unexpected Unity native package file: gua_runtime_old.dll'
    } else {
        Copy-Item -LiteralPath $QARuntimeLibrary -Destination (Join-Path $native 'gua_runtime.dll') -Force
        $manifestPath=Join-Path $native 'gua-build-manifest.json'
        $manifest=Get-Content $manifestPath -Raw | ConvertFrom-Json
        $entry=@($manifest.files | Where-Object path -CEQ 'gua_runtime.dll')
        if ($entry.Count -ne 1) { throw 'Expected one runtime hash in the verified fixture.' }
        $entry[0].sha256=(Get-FileHash -LiteralPath (Join-Path $native 'gua_runtime.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest | ConvertTo-Json -Depth 12 | Set-Content $manifestPath -Encoding utf8NoBOM
        $expected='Unity native compiled identity mismatch: Windows/x86_64/gua_runtime.dll'
    }
    $reason=''
    try { & $verify -Directory $fixture -Flavor $Flavor -Remote $Remote -DesktopOnly:$DesktopOnly } catch { $reason=$_.Exception.Message }
    if ($reason -cne $expected) { throw "UPM fault $fault was not rejected at its required boundary: $reason" }
    $results+=@{fault=$fault;passed=$true;reason=$reason}
}
$results | ConvertTo-Json | Set-Content (Join-Path $evidence 'results.json') -Encoding utf8NoBOM
Write-Host "Actual UPM verifier refused renamed and rehashed QA DLLs: $evidence"
