param(
    [Parameter(Mandatory)][string]$DotnetSdkDirectory,
    [Parameter(Mandatory)][string]$GodotSdkDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$RuntimeVersion,
    [Parameter(Mandatory)][string]$ExpectedNativeSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$AllowWorkingTreeValidation,
    [string]$UnitySdkDirectory,
    [string]$CppSdkDirectory
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
    $webManifest=Join-Path $GodotSdkDirectory "addons/gua/.web_variants/$mode/gua-build-manifest.json"
    if(Test-Path $webManifest){
        $manifest=Get-Content $webManifest -Raw|ConvertFrom-Json
        if($manifest.build.sourceCommit -ne $ExpectedNativeSource){throw 'Godot Web candidate source differs from the native candidate.'}
        foreach($entry in $manifest.files){
            if((Get-FileHash (Join-Path (Split-Path $webManifest) $entry.path)).Hash.ToLowerInvariant() -ne $entry.sha256){throw 'Godot Web candidate input digest mismatch.'}
        }
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
if($UnitySdkDirectory) {
    foreach($mode in 'off','testing','player') {
        $manifest=Get-Content (Join-Path $UnitySdkDirectory "Provider~/windows/$mode/gua-build-manifest.json") -Raw | ConvertFrom-Json
        if($manifest.build.sourceCommit -ne $ExpectedNativeSource) {throw 'The Unity SDK has a different native source.'}
    }
    $tarRoot=Join-Path $root ('build/game-sdk-tar-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
    New-Item -ItemType Directory $tarRoot | Out-Null
    Copy-Item -LiteralPath $UnitySdkDirectory -Destination (Join-Path $tarRoot 'package') -Recurse
    & tar -czf (Join-Path $output "com.link1345.gua-game-build-win-x64-$Version.tgz") -C $tarRoot package
    if($LASTEXITCODE) {throw 'Cannot package the Windows Unity candidate.'}
}
if($CppSdkDirectory) {
    $cppRoot=[IO.Path]::GetFullPath($CppSdkDirectory)
    $inventory=Get-Content (Join-Path $cppRoot 'cpp-sdk-files.json') -Raw | ConvertFrom-Json
    $actual=@(Get-ChildItem $cppRoot -File -Recurse | Where-Object FullName -CNE (Join-Path $cppRoot 'cpp-sdk-files.json'))
    if($actual.Count -ne @($inventory.files).Count){throw 'C++ candidate inventory/file count mismatch.'}
    foreach($file in $actual) {
        $relative=[IO.Path]::GetRelativePath($cppRoot,$file.FullName).Replace('\','/')
        $entry=@($inventory.files | Where-Object path -CEQ $relative)
        if($entry.Count -ne 1 -or (Get-FileHash $file.FullName).Hash.ToLowerInvariant() -ne $entry[0].sha256){throw 'C++ candidate inventory digest mismatch.'}
    }
    foreach($required in 'GuaGameBuildConfig.cmake','GuaCppGame.cmake','delay_guard.cpp','GamePolicyTemplate.dll','tools/Gua.GameBuild.dll','include/gua/runtime.h') {
        if(!(Test-Path (Join-Path $CppSdkDirectory $required))){throw "C++ candidate is missing $required"}
    }
    foreach($mode in 'off','testing','player') {
        $native=Get-Content (Join-Path $CppSdkDirectory "$mode/gua-build-manifest.json") -Raw | ConvertFrom-Json
        $imports=Get-Content (Join-Path $CppSdkDirectory "$mode/cpp-imports.json") -Raw | ConvertFrom-Json
        if($native.build.sourceCommit -ne $ExpectedNativeSource -or $imports.nativeSourceCommit -ne $ExpectedNativeSource -or $imports.buildId -ne $native.build.buildId){throw 'C++ candidate input identity mismatch.'}
        foreach($name in 'gua.lib','gua_runtime.lib') {
            $entry=@($imports.files | Where-Object path -CEQ $name)
            if($entry.Count -ne 1 -or (Get-FileHash (Join-Path $CppSdkDirectory "$mode/$name")).Hash.ToLowerInvariant() -ne $entry[0].sha256){throw 'C++ import digest mismatch.'}
        }
        if(@($imports.files).Count -ne 2){throw 'C++ candidate requires both import libraries.'}
    }
    [IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($CppSdkDirectory),(Join-Path $output "gua-game-build-cpp-win-x64-$Version.zip"))
}
Copy-Item (Join-Path $root 'LICENSE') -Destination $output
$files=@(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object {@{path=$_.Name;bytes=$_.Length;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
$hosts=@('standalone Windows x64 .NET10','Windows x64 Godot4.7 GDScript');if($UnitySdkDirectory){$hosts+='Windows x64 Unity Mono (candidate; hosted engine acceptance pending)'}
if(Test-Path (Join-Path $GodotSdkDirectory 'addons/gua/.web_variants')){$hosts+='Godot4.7 single-thread Web; standard and supplied reviewed custom shell'}
if($CppSdkDirectory){$hosts+='Windows x64 MSVC C++ (candidate; local acceptance)'}
@{schemaVersion=1;purpose='build-only candidate; not a public release';producerCommit=$producer;producerHasUncommittedChanges=$dirty;nativeSourceCommit=$ExpectedNativeSource;version=$Version;runtimePackageVersion=$RuntimeVersion;packages=$hosts;files=$files} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'game-sdk-delivery.json') -Encoding utf8NoBOM
Write-Host "Build-only SDK candidates: $output"
$global:LASTEXITCODE=0
