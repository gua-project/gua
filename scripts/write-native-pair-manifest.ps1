param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'tools/Gua.ArtifactProbe/Gua.ArtifactProbe.csproj') -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Cannot build artifact identity probe.' }
$probe = Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
$core = if ($IsWindows) { 'gua.dll' } elseif ($IsMacOS) { 'libgua.dylib' } else { 'libgua.so' }
$runtime = if ($IsWindows) { 'gua_runtime.dll' } elseif ($IsMacOS) { 'libgua_runtime.dylib' } else { 'libgua_runtime.so' }
$coreInfo = (& dotnet $probe native (Join-Path $Directory $core) gua_copy_build_info_json) | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Native core build identity unavailable.' }
$runtimeInfo = (& dotnet $probe native (Join-Path $Directory $runtime) gua_runtime_copy_build_info_json) | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Native runtime build identity unavailable.' }
$ceiling = if ($Remote -eq 'off') { 'none' } elseif ($Flavor -eq 'ai-player') { 'player' } else { 'debug' }
if (($Flavor -eq 'shipping' -and $Remote -ne 'off') -or ($Flavor -in 'qa','ai-player' -and $Remote -ne 'on') -or
    $coreInfo.flavor -ne $Flavor -or $coreInfo.remoteEnabled -ne ($Remote -eq 'on') -or $coreInfo.remotePermissionCeiling -ne $ceiling -or
    ($Flavor -ne 'development' -and $coreInfo.configuration -ne 'Release') -or $coreInfo.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    ($coreInfo | ConvertTo-Json -Compress -Depth 10) -ne ($runtimeInfo | ConvertTo-Json -Compress -Depth 10)) { throw 'Native pair has a different compiled variant.' }
$files = @($core,$runtime | ForEach-Object { @{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $Directory $_) -Algorithm SHA256).Hash.ToLowerInvariant()} })
@{schemaVersion=1;build=$coreInfo;files=$files} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Directory 'gua-build-manifest.json') -Encoding utf8NoBOM
