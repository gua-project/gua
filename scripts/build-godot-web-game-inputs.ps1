param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [string]$GodotCppSource,
    [string]$GodotBuildProfile,
    [string]$PythonExecutable
)
# Provider/CI producer only. A consumer selects these prebuilts at game export.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$GodotBuildProfile){$GodotBuildProfile=Join-Path $root 'bindings/godot/game-build/web-build-profile.json'}
if(Test-Path $OutputDirectory){throw 'Use a new provider output directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
$build=[IO.Path]::GetFullPath($BuildDirectory)
$source=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE){throw 'Cannot resolve provider source.'}
foreach($case in @(@{mode='off';flavor='shipping';remote='OFF'},@{mode='testing';flavor='qa';remote='ON'},@{mode='player';flavor='ai-player';remote='ON'})) {
    $config=@('-S',$root,'-B',$build,'-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DGUA_BUILD_EXAMPLES=OFF','-DGUA_BUILD_GODOT=ON','-DGUA_GODOT_CONFIGURATION=Release','-DGUA_STAGE_IN_SAMPLE=OFF',"-DGUA_DISTRIBUTION_FLAVOR=$($case.flavor)","-DGUA_ENABLE_REMOTE_AUTOMATION=$($case.remote)","-DGUA_BUILD_ID=$source")
    if($GodotCppSource){$config+="-DFETCHCONTENT_SOURCE_DIR_GODOT-CPP=$GodotCppSource"}
    if($GodotBuildProfile){$config+="-DGODOTCPP_BUILD_PROFILE=$GodotBuildProfile"}
    if($PythonExecutable){$config+="-DPython3_EXECUTABLE=$PythonExecutable"}
    & emcmake cmake @config *> (Join-Path $out "$($case.mode)-configure.log")
    if($LASTEXITCODE){throw 'Web GDE provider configure failed.'}
    cmake --build $build --target gua-godot gua-web-build-info gua-web-variant-tests --parallel 2 *> (Join-Path $out "$($case.mode)-build.log")
    if($LASTEXITCODE){throw 'Web GDE provider build failed.'}
    $identity=(& node (Join-Path $build 'native/gua-runtime/gua-web-build-info.js'))|ConvertFrom-Json
    if($LASTEXITCODE -or $identity.sourceCommit -ne $source -or $identity.flavor -ne $case.flavor -or $identity.remoteEnabled -ne ($case.remote -eq 'ON')){throw 'Actual linked Web identity mismatch.'}
    & node (Join-Path $build 'native/gua-runtime/gua-web-variant-tests.js') $case.flavor $case.remote.ToLowerInvariant() $identity.remotePermissionCeiling *> (Join-Path $out "$($case.mode)-acceptance.log")
    if($LASTEXITCODE){throw 'Actual linked Web profile/input/reset acceptance failed.'}
    $dest=(New-Item -ItemType Directory (Join-Path $out $case.mode)).FullName
    $files=[Collections.Generic.List[object]]::new()
    $wasm=Join-Path $build 'stage/addons/gua/bin/gua_godot.web.release.wasm32.wasm'
    if(!(Test-Path $wasm)){throw 'Missing Web GDE side module.'}
    Copy-Item $wasm (Join-Path $dest 'gua.wasm')
    $members=@('gua.wasm')
    foreach($name in 'gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd') {Copy-Item (Join-Path $root "examples/godot-gdscript/addons/gua/$name") $dest;$members+=$name}
    if($case.remote -eq 'ON'){Copy-Item (Join-Path $root 'examples/godot-gdscript/addons/gua/gua_webmcp_bridge.gd') $dest;$members+='gua_webmcp_bridge.gd'}
    foreach($name in $members){$files.Add(@{path=$name;sha256=(Get-FileHash (Join-Path $dest $name)).Hash.ToLowerInvariant()})}
    @{schemaVersion=1;engineBuildKind='release';build=$identity;files=$files}|ConvertTo-Json -Depth 12|Set-Content (Join-Path $dest 'gua-build-manifest.json') -Encoding utf8NoBOM
}
$global:LASTEXITCODE=0
