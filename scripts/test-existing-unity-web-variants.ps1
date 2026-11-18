param([Parameter(Mandatory)][string]$UnityExecutable)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$sdk=Join-Path (Split-Path ([IO.Path]::GetFullPath($UnityExecutable))) 'Data/PlaybackEngines/WebGLSupport/BuildTools/Emscripten'
foreach($file in '.emscripten','emscripten/emcmake.bat','python/python.exe','node/node.exe') {if(!(Test-Path (Join-Path $sdk $file))) {throw 'The selected existing Unity installation has no complete bundled Web toolchain.'}}
$ninja=Get-Command ninja -ErrorAction SilentlyContinue
$ninjaDirectory=if($ninja) {Split-Path $ninja.Source} else {Join-Path (Split-Path (Split-Path (Split-Path (Get-Command cmake).Source))) 'Ninja'}
if(!(Test-Path (Join-Path $ninjaDirectory 'ninja.exe'))) {throw 'An existing Ninja executable is required; this script does not install tools.'}
$relative='build/existing-unity-web-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')
$output=Join-Path $root $relative
$temporary=[IO.Path]::GetFullPath((Join-Path $output 'temporary'))
if(!$temporary.StartsWith([IO.Path]::GetFullPath($root)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Temporary files must stay within the workspace.'}
New-Item -ItemType Directory -Force $temporary | Out-Null
$saved=@{};foreach($name in 'PATH','EM_CONFIG','EM_FROZEN_CACHE','EMSDK_PYTHON','TEMP','TMP') {$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$source=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE) {throw 'Cannot identify native source.'}
$results=[Collections.Generic.List[object]]::new()
try {
    $env:PATH="$sdk/emscripten;$sdk/python;$sdk/node;$ninjaDirectory;"+$env:PATH
    $env:EM_CONFIG=Join-Path $sdk '.emscripten';$env:EM_FROZEN_CACHE='1'
    $env:EMSDK_PYTHON=Join-Path $sdk 'python/python.exe';$env:TEMP=$temporary;$env:TMP=$temporary
    foreach($case in @(@{mode='off';flavor='shipping';remote='off'},@{mode='testing';flavor='qa';remote='on'},@{mode='player';flavor='ai-player';remote='on'})) {
        $log=Join-Path $output ($case.mode+'.log')
        & (Join-Path $PSScriptRoot 'build-web-native.ps1') -BuildDirectory "$relative/$($case.mode)" -Configuration Release -RuntimeOnly -Version 0.0.0-ci -BuildId $source -Generator Ninja -Flavor $case.flavor -Remote $case.remote *> $log
        if($LASTEXITCODE) {throw "Existing Unity Web native acceptance failed: $log"}
        $manifest=Join-Path $output "$($case.mode)/stage/unity-web/gua-build-manifest.json"
        if(!(Test-Path $manifest)) {throw 'The accepted Web producer did not emit its manifest.'}
        $results.Add(@{case=$case.mode;result='linked-wasm-native-profile-input-reset-passed';evidence=$log;manifest=$manifest})
    }
} finally {foreach($name in $saved.Keys) {[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}}
@{scope='native linked WebAssembly only; not browser/Unity host startup or SDK-swap verification';sourceCommit=$source;toolchainVersion=(Get-Content (Join-Path $sdk 'emscripten/emscripten-version.txt') -Raw).Trim();results=$results} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "Existing Unity Web native evidence: $output"
$global:LASTEXITCODE=0
