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
    Copy-Item (Join-Path $case.build 'native/gua-core/Release/gua.lib') -Destination (Join-Path $OutputDirectory $case.mode)
    Copy-Item (Join-Path $case.build 'native/gua-runtime/Release/gua_runtime.lib') -Destination (Join-Path $OutputDirectory $case.mode)
}
New-Item -ItemType Directory -Force (Join-Path $OutputDirectory 'include') | Out-Null
foreach($component in 'gua-core','gua-runtime') {Copy-Item (Join-Path $root "native/$component/include/gua") -Destination (Join-Path $OutputDirectory 'include') -Recurse -Force}
Copy-Item $PlayerTemplate -Destination (Join-Path $OutputDirectory 'GamePolicyTemplate.dll')
Copy-Item (Join-Path $root 'bindings/cpp/game-build/*') -Destination $OutputDirectory
'include("${CMAKE_CURRENT_LIST_DIR}/GuaCppGame.cmake")' | Set-Content (Join-Path $OutputDirectory 'GuaGameBuildConfig.cmake') -Encoding utf8NoBOM
Write-Host "Provider-built C++ game SDK: $OutputDirectory"
