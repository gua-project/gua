param(
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$RuntimeVersion,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or $RuntimeVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Expected explicit package versions.' }
$bundle=[IO.Path]::GetFullPath($BundleRoot)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$stage=Join-Path $root ('build/game-nuget-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Force $stage,$output | Out-Null
Set-Content -LiteralPath (Join-Path $stage '_._') -Value '' -NoNewline
# The provider validates ALL shipped modes before making a consumer package.
# A runtime manifest is never used to regenerate expectations at game startup.
$identity=$null
foreach($mode in 'off','testing','player') {
    $manifest=Get-Content -LiteralPath (Join-Path $bundle "$mode/gua-build-manifest.json") -Raw | ConvertFrom-Json
    $identity ??= $manifest.build.sourceCommit
    if($manifest.build.sourceCommit -ne $identity) {throw 'Provider modes have different source identities.'}
    $check=Join-Path $stage "$mode.cs"
    & dotnet (Join-Path $bundle 'tools/Gua.GameBuild.dll') generate (Join-Path $bundle $mode) $mode $check
    if($LASTEXITCODE) {throw "The prebuilt $mode provider mode failed validation."}
}
$bundleXml=[Security.SecurityElement]::Escape($bundle)
$wrapperXml=[Security.SecurityElement]::Escape((Join-Path $root 'bindings/dotnet/game-build/Gua.GameBuild.targets'))
$emptyXml=[Security.SecurityElement]::Escape((Join-Path $stage '_._'))
@"
<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Gua.GameBuild</id><version>$Version</version><authors>Gua contributors</authors>
    <description>Game-owned native integrity verification and automatic prebuilt mode selection for Windows x64 .NET games.</description>
    <license type="expression">MIT</license>
    <dependencies><group targetFramework="net10.0"><dependency id="Gua.Runtime" version="[$RuntimeVersion]" /></group></dependencies>
  </metadata>
  <files>
    <file src="$bundleXml\**\*" target="game-build\win-x64" />
    <file src="$wrapperXml" target="buildTransitive\Gua.GameBuild.targets" />
    <file src="$emptyXml" target="lib\net10.0\_._" />
  </files>
</package>
"@ | Set-Content -LiteralPath (Join-Path $stage 'Gua.GameBuild.nuspec') -Encoding utf8NoBOM
# Native variants and the build tool deliberately are not reference assemblies.
dotnet pack (Join-Path $root 'tools/Gua.GameBuild/Gua.GameBuild.csproj') -c Release --no-build --no-restore --nologo -v quiet -p:NoWarn=NU5100 "-p:NuspecFile=$(Join-Path $stage 'Gua.GameBuild.nuspec')" "-p:PackageOutputPath=$output"
if($LASTEXITCODE) {throw 'Cannot pack the game-build NuGet SDK.'}
Write-Host "Created local provider package: $(Join-Path $output "Gua.GameBuild.$Version.nupkg")"
