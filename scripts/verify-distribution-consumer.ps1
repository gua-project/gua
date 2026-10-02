param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('gua-consumer-' + [guid]::NewGuid().ToString('N'))
$consumer = Join-Path $temporary 'consumer'
New-Item -ItemType Directory -Force $consumer | Out-Null
$root = Split-Path -Parent $PSScriptRoot
Copy-Item -LiteralPath (Join-Path $root 'bindings/dotnet/tests/Gua.DistributionSmoke/Program.cs') -Destination $consumer
Copy-Item -LiteralPath (Join-Path $root 'bindings/dotnet/tests/Gua.DistributionSmoke/Gua.DistributionSmoke.csproj') -Destination $consumer
$project = Join-Path $consumer 'Gua.DistributionSmoke.csproj'
if ((Get-Content $project -Raw) -match 'ProjectReference|Compile.*Include|HintPath') { throw 'Consumer must have package references only.' }
$escapedFeed = [Security.SecurityElement]::Escape($feed)
$onlineConfig = Join-Path $temporary 'seed.config'
$offlineConfig = Join-Path $temporary 'offline.config'
"<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources></configuration>" | Set-Content $onlineConfig
"<configuration><packageSources><clear/><add key=`"local`" value=`"$escapedFeed`"/></packageSources></configuration>" | Set-Content $offlineConfig
$variables = @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_HOME', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'GUA_NATIVE_DIR', 'GUA_RUNTIME_NATIVE_DIR', 'GUA_EXPECTED_COMMIT')
$previous = @{}
foreach ($variable in $variables) { $previous[$variable] = [Environment]::GetEnvironmentVariable($variable) }
try {
    $env:DOTNET_CLI_HOME = Join-Path $temporary 'dotnet-home'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:NUGET_PACKAGES = Join-Path $temporary 'seed-cache'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $temporary 'http-cache'
    $env:GUA_NATIVE_DIR = ''; $env:GUA_RUNTIME_NATIVE_DIR = ''; $env:GUA_EXPECTED_COMMIT = $SourceCommit
    # Only dependency acquisition may contact NuGet. All acceptance restores use the resulting local feed alone.
    Run-Dotnet -Arguments @('restore', $project, "-p:GuaPackageVersion=$Version", '-r', $Rid, '-p:SelfContained=true', '--configfile', $onlineConfig)
    foreach ($package in Get-ChildItem $env:NUGET_PACKAGES -Recurse -Filter '*.nupkg' -File) {
        $destination = Join-Path $feed $package.Name
        if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $package.FullName -Destination $destination }
    }
    & (Join-Path $PSScriptRoot 'write-distribution-manifest.ps1') -PackageDirectory $feed -Version $Version -SourceCommit $SourceCommit -OutputPath (Join-Path $output 'distribution-manifest.json')
    Copy-Item -LiteralPath (Join-Path $output 'dependency-licenses') -Destination (Join-Path $consumer 'notices') -Recurse
    $env:NUGET_PACKAGES = Join-Path $temporary 'empty-archive-cache'
    Run-Dotnet -Arguments @('restore', $project, "-p:GuaPackageVersion=$Version", '-r', $Rid, '-p:SelfContained=true', '--configfile', $offlineConfig, '--no-http-cache', '-p:NuGetAudit=false')
    Run-Dotnet -Arguments @('publish', $project, '-c', 'Release', '-r', $Rid, '--self-contained', '--no-restore', "-p:GuaPackageVersion=$Version", '-o', (Join-Path $temporary 'publish'))
    $archive = Join-Path $output "consumer-$Rid.zip"
    # ZipFile preserves relative directories when macOS resolves /var through /private/var.
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $temporary 'publish'), $archive)
    $extracted = Join-Path $temporary 'archive-extracted'
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    & (Join-Path $PSScriptRoot 'verify-native-dependency-closure.ps1') -NativeDirectory $extracted -Rid $Rid -OutputPath (Join-Path $output 'native-dependencies.json')
    $executable = Join-Path $extracted $(if ($Rid -eq 'win-x64') { 'Gua.DistributionSmoke.exe' } else { 'Gua.DistributionSmoke' })
    if ($Rid -ne 'win-x64') { & chmod +x $executable; if ($LASTEXITCODE -ne 0) { throw 'chmod failed.' } }
    Push-Location $extracted
    try {
        & $executable (Join-Path $output 'archive-run')
        if ($LASTEXITCODE -ne 0) { throw 'Extracted self-contained consumer failed.' }
        # Prove offline schema/Trace/Viewer paths work with no native libraries present.
        $nativeNames = @('gua.dll', 'gua_runtime.dll', 'libgua.so', 'libgua_runtime.so', 'libgua.dylib', 'libgua_runtime.dylib')
        $quarantine = Join-Path $temporary 'native-quarantine'
        New-Item -ItemType Directory $quarantine | Out-Null
        foreach ($name in $nativeNames) {
            $path = Join-Path $extracted $name
            if (Test-Path -LiteralPath $path) { Move-Item -LiteralPath $path -Destination $quarantine }
        }
        & $executable --offline (Join-Path $output 'offline-without-native')
        if ($LASTEXITCODE -ne 0) { throw 'Offline consumer required native libraries.' }
    } finally { Pop-Location }
    # Tool packing is a consumer artifact, never a public Gua product/version decision.
    Run-Dotnet -Arguments @('pack', $project, '-c', 'Release', '--no-restore', "-p:GuaPackageVersion=$Version", '-p:SelfContained=false', '-o', $feed)
    Copy-Item -LiteralPath (Join-Path $feed "Gua.DistributionSmoke.$Version.nupkg") -Destination $output
    $env:NUGET_PACKAGES = Join-Path $temporary 'empty-tool-cache'
    $toolPath = Join-Path $temporary 'tools'
    Run-Dotnet -Arguments @('tool', 'install', 'Gua.DistributionSmoke', '--version', $Version, '--tool-path', $toolPath, '--configfile', $offlineConfig, '--no-cache')
    $tool = Join-Path $toolPath $(if ($Rid -eq 'win-x64') { 'gua-distribution-smoke.exe' } else { 'gua-distribution-smoke' })
    Push-Location $temporary
    try {
        & $tool (Join-Path $output 'tool-run')
        if ($LASTEXITCODE -ne 0) { throw '.NET Tool package-only consumer failed.' }
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Destination (Join-Path $output 'project.assets.json')
    & (Join-Path $PSScriptRoot 'write-distribution-manifest.ps1') -PackageDirectory $feed -Version $Version -SourceCommit $SourceCommit -OutputPath (Join-Path $output 'distribution-manifest.json')
    $retainedFeed = Join-Path $output 'local-feed'
    New-Item -ItemType Directory -Force $retainedFeed | Out-Null
    Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $retainedFeed }
    Write-Host "Package-only archive, offline and .NET Tool acceptance passed for $Rid at $SourceCommit."
} finally {
    foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, $previous[$variable]) }
}
