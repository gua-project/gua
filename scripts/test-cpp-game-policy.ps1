param([Parameter(Mandatory)][string]$SdkDirectory,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$sdk=(Resolve-Path $SdkDirectory).Path
if(Test-Path $OutputDirectory){throw 'Use a new test output directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
$generator=& (Join-Path $PSScriptRoot 'get-msvc-generator.ps1')
$probe=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
$results=[System.Collections.Generic.List[object]]::new()
function Build-Consumer($name,$mode,$mutation='') {
  $dir=Join-Path $out $name
  $options=@('-S',(Join-Path $root 'tests/cpp-game-policy'),'-B',$dir,'-G',$generator,'-A','x64',"-DGuaGameBuild_DIR=$sdk","-DGAME_MODE=$mode")
  if($mutation){$options+="-D$mutation=ON"}
  & cmake @options *> (Join-Path $out "$name-configure.log")
  if($LASTEXITCODE){throw "Configure failed: $name"}
  & cmake --build $dir --config Release --parallel 1 -- /nr:false *> (Join-Path $out "$name-build.log")
  $code=$LASTEXITCODE
  $exe=Join-Path $dir 'Release/game.exe'
  if($mutation -like 'EAGER*') {
    $log=Get-Content (Join-Path $out "$name-build.log") -Raw
    if(!$code -or (Test-Path $exe) -or $log -notmatch 'Eager Gua import before verification'){throw "Import violation escaped the deployment gate: $name"}
    $results.Add(@{case=$name;stage='build import closure';refused=$true;executableAbsent=$true})
  } elseif($code){throw "Build failed: $name"}
  return $dir
}
function Observe($name,$directory,$exitCode,$loads) {
  $result=Join-Path $out "$name-observed.json"
  & dotnet $probe (Join-Path $directory 'game.exe') $result
  if($LASTEXITCODE){throw "Observer failed: $name"}
  $value=Get-Content $result -Raw | ConvertFrom-Json
  if($value.childExitCode -ne $exitCode -or @($value.guaNativeLoads).Count -ne $loads){throw "Wrong refusal stage/load count: $name"}
  if($loads -gt 0 -and $value.timeline[0] -notlike 'load:GameSecurity_*'){throw "SDK loaded before game verifier: $name"}
  $results.Add(@{case=$name;exitCode=$value.childExitCode;nativeLoads=@($value.guaNativeLoads).Count;evidence=$result})
}
foreach($mode in 'off','testing','player') {
  $dir=Build-Consumer $mode $mode
  Observe $mode (Join-Path $dir 'Release') 0 2
}
$off=Join-Path $out 'off/Release'
foreach($case in 'official-on-replacement','missing-sdk','missing-checker') {
  $dir=Join-Path $out $case
  New-Item -ItemType Directory $dir | Out-Null
  Copy-Item (Join-Path $off '*') $dir -Recurse
  if($case -eq 'official-on-replacement'){
    foreach($dll in 'gua.dll','gua_runtime.dll'){Copy-Item (Join-Path $sdk "testing/$dll") $dir -Force}
    Observe $case $dir 78 0
  } elseif($case -eq 'missing-sdk') {
    Remove-Item -LiteralPath (Join-Path $dir 'gua_runtime.dll')
    Observe $case $dir 81 0
  } else {
    Get-ChildItem $dir -Filter 'GameSecurity_*.dll' | Remove-Item
    Observe $case $dir 81 0
  }
}
$null=Build-Consumer 'eager-game' off EAGER_GAME
$null=Build-Consumer 'eager-transitive-plugin' off EAGER_PLUGIN
$early=Build-Consumer 'early-plugin-initializer' off EARLY_PLUGIN
Observe 'early-plugin-initializer' (Join-Path $early 'Release') 86 0
$hooks=Build-Consumer 'unrelated-delay-fallback' off LEGACY_HOOK
Observe 'unrelated-delay-fallback' (Join-Path $hooks 'Release') 0 2
# An external DLL cannot silently retain ordinary Gua imports either.
& dotnet (Join-Path $sdk 'tools/Gua.GameBuild.dll') check-cpp-imports (Join-Path $off 'game.exe') (Join-Path $off 'legacy.dll') *> (Join-Path $out 'external-legacy.log')
if(!$LASTEXITCODE -or (Get-Content (Join-Path $out 'external-legacy.log') -Raw) -notmatch 'Eager Gua import before verification'){throw 'External legacy dependency was not detected.'}
$results.Add(@{case='external-legacy';stage='declared external module import closure';refused=$true})
$results | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out 'results.json') -Encoding utf8NoBOM
Write-Host "C++ consumer regression passed: $out"
$global:LASTEXITCODE=0 # The final expected-failure probe must not become our exit code.
