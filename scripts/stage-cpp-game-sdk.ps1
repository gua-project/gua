param(
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$ShippingBuild,
    [Parameter(Mandatory)][string]$TestingBuild,
    [Parameter(Mandatory)][string]$PlayerBuild,
    [Parameter(Mandatory)][string]$PlayerTemplate,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(Test-Path $OutputDirectory){throw 'Stage into a new C++ SDK directory.'}
dotnet build (Join-Path $root 'tools/Gua.GameBuild') -c Release --nologo -v quiet -m:1 -nr:false
if($LASTEXITCODE){throw 'Cannot build the game build tool.'}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
Copy-Item (Join-Path $BundleRoot '*') -Destination $OutputDirectory -Recurse
# Use this producer's current generator, not a stale copied tool snapshot.
foreach($name in 'Gua.GameBuild.dll','Gua.GameBuild.deps.json','Gua.GameBuild.runtimeconfig.json','GameGuard.cs.txt') {Copy-Item (Join-Path $root "tools/Gua.GameBuild/bin/Release/net10.0/$name") -Destination (Join-Path $OutputDirectory 'tools') -Force}
foreach($case in @(@{mode='off';build=$ShippingBuild},@{mode='testing';build=$TestingBuild},@{mode='player';build=$PlayerBuild})) {
    $imports=$case.build
    if(!(Test-Path (Join-Path $imports 'cpp-imports.json'))) {
        $imports=Join-Path $OutputDirectory "import-input-$($case.mode)"
        & (Join-Path $PSScriptRoot 'stage-cpp-imports.ps1') -BuildDirectory $case.build -PackageDirectory (Join-Path $BundleRoot $case.mode) -OutputDirectory $imports
    }
    $identity=Get-Content (Join-Path $imports 'cpp-imports.json') -Raw | ConvertFrom-Json
    $native=Get-Content (Join-Path $BundleRoot "$($case.mode)/gua-build-manifest.json") -Raw | ConvertFrom-Json
    if($identity.nativeSourceCommit -ne $native.build.sourceCommit -or $identity.buildId -ne $native.build.buildId){throw 'C++ import artifact source/build mismatch.'}
    foreach($name in 'gua.lib','gua_runtime.lib') {
        $entry=@($identity.files | Where-Object path -CEQ $name)
        if($entry.Count -ne 1 -or (Get-FileHash (Join-Path $imports $name)).Hash.ToLowerInvariant() -ne $entry[0].sha256){throw 'C++ import artifact digest mismatch.'}
        Copy-Item (Join-Path $imports $name) -Destination (Join-Path $OutputDirectory $case.mode)
    }
    Copy-Item (Join-Path $imports 'cpp-imports.json') (Join-Path $OutputDirectory $case.mode)
    if($imports -ne $case.build) {
        # Exactly the three files generated above, never a recursive removal.
        foreach($name in 'gua.lib','gua_runtime.lib','cpp-imports.json'){Remove-Item -LiteralPath (Join-Path $imports $name)}
        Remove-Item -LiteralPath $imports
    }
}
New-Item -ItemType Directory -Force (Join-Path $OutputDirectory 'include') | Out-Null
foreach($component in 'gua-core','gua-runtime') {Copy-Item (Join-Path $root "native/$component/include/gua") -Destination (Join-Path $OutputDirectory 'include') -Recurse -Force}
Copy-Item $PlayerTemplate -Destination (Join-Path $OutputDirectory 'GamePolicyTemplate.dll')
Copy-Item (Join-Path $root 'bindings/cpp/game-build/*') -Destination $OutputDirectory
'include("${CMAKE_CURRENT_LIST_DIR}/GuaCppGame.cmake")' | Set-Content (Join-Path $OutputDirectory 'GuaGameBuildConfig.cmake') -Encoding utf8NoBOM
$stageRoot=[IO.Path]::GetFullPath($OutputDirectory)
$files=@(Get-ChildItem $stageRoot -File -Recurse | Sort-Object FullName | ForEach-Object {@{path=[IO.Path]::GetRelativePath($stageRoot,$_.FullName).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})
@{schemaVersion=1;producerCommit=(& git -C $root rev-parse HEAD).Trim();files=$files} | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $OutputDirectory 'cpp-sdk-files.json') -Encoding utf8NoBOM
Write-Host "Provider-built C++ game SDK: $OutputDirectory"
