param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [string]$PlayerPath,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = '0.0.0-ci',
    [ValidateSet('player','prepare')][string]$Mode = 'player',
    [string]$PublishRid
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($key in 'GUA_NATIVE_DIR','GUA_RUNTIME_NATIVE_DIR') {
    if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($key))) { throw "Native override is forbidden: $key" }
}
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$project = Join-Path ([IO.Path]::GetTempPath()) ('gua-unity-client-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $project,$OutputDirectory | Out-Null
foreach ($file in 'Program.cs','Consumer.csproj') { Copy-Item -LiteralPath (Join-Path $root "examples/distribution-unity-consumer/$file") -Destination $project }
$config = Join-Path $project 'NuGet.Config'
"<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/></packageSources></configuration>" |
    Set-Content -LiteralPath $config -Encoding utf8NoBOM
$savedCache = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $project 'empty-cache'
    dotnet restore (Join-Path $project 'Consumer.csproj') --configfile $config --no-http-cache -p:GuaDistributionVersion=$Version -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Package-only restore failed.' }
    $assets = Get-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    foreach ($required in 'Gua.Core','Gua.Runtime','Gua.Testing','Gua.Testing.Unity') {
        if (-not $assets.libraries.ContainsKey("$required/$Version")) { throw "Required candidate library is missing: $required" }
    }
    foreach ($key in $assets.libraries.Keys) {
        if ($assets.libraries[$key].type -eq 'project' -or ($key -like 'Gua.*' -and -not $key.EndsWith("/$Version"))) { throw "Source or mixed-version library: $key" }
    }
    & (Join-Path $PSScriptRoot 'write-distribution-manifest.ps1') -PackageDirectory $feed -Version $Version -SourceCommit $SourceCommit -OutputPath (Join-Path $OutputDirectory 'package-manifest.json')
    dotnet build (Join-Path $project 'Consumer.csproj') -c Release --no-restore -p:GuaDistributionVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw 'Package-only build failed.' }
    Copy-Item -LiteralPath (Join-Path $project 'obj/project.assets.json') -Destination $OutputDirectory
    @{ project=$project; sourceCommit=$SourceCommit; version=$Version; packageDirectory=$feed; nativeOverrides='absent'; projectLibraries=0 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'client-provenance.json') -Encoding utf8NoBOM
    if($Mode -eq 'prepare') {
        if(-not $PublishRid) {throw 'Publish RID required for the isolated Editor client'}
        dotnet publish (Join-Path $project 'Consumer.csproj') -c Release -r $PublishRid --self-contained true -o (Join-Path $OutputDirectory 'client') --configfile $config -p:GuaDistributionVersion=$Version -p:NuGetAudit=false
        if($LASTEXITCODE -ne 0) {throw 'Isolated Editor client publication failed'}
        Copy-Item (Join-Path $project 'obj/project.assets.json') $OutputDirectory -Force
    } else {
        if(-not $PlayerPath) {throw 'Player path required'}
        dotnet (Join-Path $project 'bin/Release/net10.0/Consumer.dll') player ([IO.Path]::GetFullPath($PlayerPath)) $project ([IO.Path]::GetFullPath($OutputDirectory)) $SourceCommit
        if ($LASTEXITCODE -ne 0) { throw 'Actual Player route verification failed.' }
    }
} finally { $env:NUGET_PACKAGES = $savedCache }
