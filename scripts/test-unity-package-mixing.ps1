param(
    [Parameter(Mandatory)][string]$ShippingNativeDirectory,
    [Parameter(Mandatory)][string]$QARuntimeLibrary
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('build/unity-mixing-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $fixture | Out-Null
Copy-Item -LiteralPath (Join-Path $ShippingNativeDirectory 'gua.dll'),(Join-Path $ShippingNativeDirectory 'gua_runtime.dll'),(Join-Path $ShippingNativeDirectory 'gua-build-manifest.json') -Destination $fixture
$check=Join-Path $PSScriptRoot 'assert-unity-native-layout.ps1'
& $check -Directory $fixture -Platform Windows -Remote off
Copy-Item -LiteralPath $QARuntimeLibrary -Destination (Join-Path $fixture 'gua_runtime_old.dll')
$reason=''
try { & $check -Directory $fixture -Platform Windows -Remote off } catch { $reason=$_.Exception.Message }
if ($reason -cne 'Unexpected Unity native package file: gua_runtime_old.dll') { throw "Renamed QA DLL was not rejected by the expected UPM boundary: $reason" }
@{passed=$true;reason=$reason} | ConvertTo-Json | Set-Content (Join-Path $fixture 'result.json') -Encoding utf8NoBOM
Write-Host "Renamed QA DLL refused by Unity package boundary: $fixture"
