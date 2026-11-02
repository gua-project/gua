param(
    [Parameter(Mandatory)][string]$DotnetSdkDirectory,
    [Parameter(Mandatory)][string]$GodotSdkDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$RuntimeVersion,
    [Parameter(Mandatory)][string]$ExpectedNativeSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$AllowWorkingTreeValidation
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if($ExpectedNativeSource -notmatch '^[0-9a-fA-F]{40}$') {throw 'Require an explicit expected native source identity.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $output) {throw 'Package candidate SDKs into a new output directory.'}
$producer=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE) {throw 'Cannot identify SDK producer source.'}
$dirty=![string]::IsNullOrWhiteSpace([string](& git -C $root status --porcelain))
if($dirty -and !$AllowWorkingTreeValidation) {throw 'Candidate delivery requires a clean producer checkout; local working-tree validation must be explicit.'}
foreach($mode in 'off','testing','player') {
    foreach($manifestPath in (Join-Path $DotnetSdkDirectory "$mode/gua-build-manifest.json"),(Join-Path $GodotSdkDirectory "addons/gua/.variants/$mode/gua-build-manifest.json")) {
        $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if($manifest.build.sourceCommit -ne $ExpectedNativeSource) {throw 'SDK inputs came from a different native source.'}
    }
}
New-Item -ItemType Directory $output | Out-Null
& (Join-Path $PSScriptRoot 'pack-game-build-nuget.ps1') -BundleRoot $DotnetSdkDirectory -Version $Version -RuntimeVersion $RuntimeVersion -OutputDirectory $output
$dotnetArchive=Join-Path $output "gua-game-build-dotnet-win-x64-$Version.zip"
$godotArchive=Join-Path $output "gua-game-build-godot-windows-$Version.zip"
# ZipFile preserves .variants/.gdignore, including on hosts where archive
# helpers treat dot-prefixed inputs as hidden. These are developer SDK inputs;
# the Godot exporter, not this SDK archive, excludes unused modes from a game.
[IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($DotnetSdkDirectory),$dotnetArchive)
[IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($GodotSdkDirectory),$godotArchive)
$zip=[IO.Compression.ZipFile]::OpenRead($godotArchive)
try {
    foreach($required in 'addons/gua/.variants/.gdignore','addons/gua/editor/.gdignore','addons/gua/.variants/off/gua-build-manifest.json','addons/gua/.variants/testing/gua-build-manifest.json','addons/gua/.variants/player/gua-build-manifest.json') {
        if(!$zip.GetEntry($required)) {throw "SDK archive lost a required provider input: $required"}
    }
} finally {$zip.Dispose()}
Copy-Item (Join-Path $root 'LICENSE') -Destination $output
$files=@(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {@{path=$_.Name;bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
@{schemaVersion=1;purpose='build-only candidate; not a public release';producerCommit=$producer;producerHasUncommittedChanges=$dirty;nativeSourceCommit=$ExpectedNativeSource;version=$Version;runtimePackageVersion=$RuntimeVersion;packages=@('standalone Windows x64 .NET10','Windows x64 Godot4.7 GDScript');files=$files} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'game-sdk-delivery.json') -Encoding utf8NoBOM
Write-Host "Build-only SDK candidates: $output"
$global:LASTEXITCODE=0
