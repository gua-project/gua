param(
    [Parameter(Mandatory)][string]$MatrixDirectory,
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$ReleaseTemplate,
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$OfficialCoreAssembly,
    [Parameter(Mandatory)][string]$OfficialRuntimeAssembly
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$matrix=(Resolve-Path $MatrixDirectory).Path
$engine=(Resolve-Path $GodotExecutable).Path
$template=(Resolve-Path $ReleaseTemplate).Path.Replace('\','/')
$sdk=(Resolve-Path $BundleRoot).Path
if(Test-Path $OutputDirectory){throw 'Use an empty export acceptance directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
$probe=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
$wire=Join-Path $root 'tools/Gua.EngineWireProbe/bin/Release/net10.0/Gua.EngineWireProbe.dll'
$results=[Collections.Generic.List[object]]::new()
$old=@{}
foreach($key in 'GUA_GODOT_TEST_RESULT','GUA_GODOT_TEST_PORT','GUA_OBSERVATION_PROFILE'){$old[$key]=[Environment]::GetEnvironmentVariable($key)}
try {
    $env:GUA_OBSERVATION_PROFILE=$null;$env:GUA_GODOT_TEST_PORT='19588'
    foreach($mode in 'off','testing','player') {
        $project=Join-Path $out "$mode-project"
        Copy-Item (Join-Path $matrix $mode) $project -Recurse
        dotnet new sln --name GuardedGodotGame --format sln --output $project *> (Join-Path $out "$mode-solution.log")
        if($LASTEXITCODE){throw 'Cannot create the actual Godot export solution.'}
        dotnet sln (Join-Path $project 'GuardedGodotGame.sln') add (Join-Path $project 'GuardedGodotGame.csproj') >> (Join-Path $out "$mode-solution.log")
        if($LASTEXITCODE){throw 'Cannot add the game to its export solution.'}
        @"
[preset.0]
name="Windows Desktop"
platform="Windows Desktop"
runnable=true
export_filter="all_resources"
include_filter=""
exclude_filter=""
export_path=""
[preset.0.options]
custom_template/release="$template"
binary_format/architecture="x86_64"
binary_format/embed_pck=false
dotnet/include_scripts_content=false
"@|Set-Content (Join-Path $project 'export_presets.cfg')
        $product=(New-Item -ItemType Directory (Join-Path $out $mode)).FullName
        $exe=Join-Path $product 'GuardedGame.exe'
        # Use the actual engine, not its console relay, which can retain a
        # compiler's inherited output pipe after the engine has already exited.
        $exporter=Start-Process $engine -ArgumentList @('--headless','--path',('"'+$project+'"'),'--export-release','"Windows Desktop"',('"'+$exe+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $out "$mode-export.stdout") -RedirectStandardError (Join-Path $out "$mode-export.stderr")
        if(!$exporter.WaitForExit(90000)){throw "Export Editor remains alive PID=$($exporter.Id); do not terminate it."}
        if($exporter.ExitCode -ne 0){throw 'Godot export failed.'}
        $data=Join-Path $product 'data_GuardedGodotGame_windows_x86_64'
        foreach($file in 'GuardedGodotGame.dll','Gua.Core.dll','Gua.Runtime.dll','gua.dll','gua_runtime.dll') {
            if(!(Test-Path (Join-Path $data $file))){throw "Export omitted required payload: $file"}
        }
        # A zero Godot CLI exit alone does not establish successful export.
        $result=Join-Path $out "$mode-game.json";$observed=Join-Path $out "$mode-native.json"
        $env:GUA_GODOT_TEST_RESULT=$result
        $observer=Start-Process dotnet -ArgumentList @(('"'+$probe+'"'),('"'+$exe+'"'),('"'+$observed+'"'),'--headless','--max-fps','60','--quit-after','1800') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $out "$mode-game.stdout") -RedirectStandardError (Join-Path $out "$mode-game.stderr")
        try {
            if($mode -ne 'off') {
                $deadline=[DateTime]::UtcNow.AddSeconds(25)
                while(!(Test-Path $result) -and !$observer.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 100}
                if(!(Test-Path $result)){throw 'Exported game did not reach its guarded UI.'}
                & dotnet $wire ws://127.0.0.1:19588 $mode *> (Join-Path $out "$mode-wire.json")
                if($LASTEXITCODE){throw 'Exported game external input failed.'}
            }
            if(!$observer.WaitForExit(25000) -or $observer.ExitCode -ne 0){throw 'Owned exported game observer failed.'}
        } finally {if(!$observer.HasExited){$observer.Kill($true)}} # Owned GAME only.
        $game=Get-Content $result -Raw|ConvertFrom-Json
        $native=Get-Content $observed -Raw|ConvertFrom-Json
        if(!$game.ready -or $game.mode -ne $mode -or $game.remote -ne ($mode -ne 'off') -or $game.consumed -ne $(if($mode -eq 'off'){0}else{1}) -or $native.childExitCode -ne 0 -or @($native.guaNativeLoads).Count -ne 2){throw 'Wrong actual exported game behavior.'}
        $checked=[Array]::IndexOf(@($native.timeline),'GamePolicy: verification succeeded before SDK initialization.')
        if($checked -lt 0 -or [Array]::IndexOf(@($native.timeline),'load:gua.dll') -lt $checked){throw 'Exported native load preceded game verification.'}
        if([IO.Path]::GetDirectoryName($game.gameAssembly) -ine $data){throw 'Wrong game-owned verification root.'}
        foreach($path in $native.guaNativePaths){if([IO.Path]::GetDirectoryName($path.Replace('\\?\','')) -ine $data){throw 'Unverified export native path.'}}
        $results.Add(@{mode=$mode;ready=$true;consumed=$game.consumed;observed=$observed})
    }
    # Preserve the GAME assembly/policy, replace the exchangeable native pair.
    $swap=Join-Path $out 'swap';Copy-Item (Join-Path $out 'off') $swap -Recurse
    $data=Join-Path $swap 'data_GuardedGodotGame_windows_x86_64'
    foreach($file in 'gua.dll','gua_runtime.dll'){Copy-Item (Join-Path $sdk "testing/$file") $data -Force}
    $env:GUA_GODOT_TEST_RESULT=Join-Path $out 'swap-unreachable.json'
    $observed=Join-Path $out 'swap-native.json'
    & dotnet $probe (Join-Path $swap 'GuardedGame.exe') $observed --headless --quit-after 300
    if($LASTEXITCODE){throw 'Export substitution observer failed.'}
    $native=Get-Content $observed -Raw|ConvertFrom-Json
    if($native.childExitCode -ne 78 -or @($native.guaNativeLoads).Count -ne 0 -or 'GamePolicy: integrity verification refused.' -notin $native.timeline -or (Test-Path $env:GUA_GODOT_TEST_RESULT)){throw 'Exported OFF game accepted an ON substitution.'}
    $results.Add(@{mode='off-on-swap';exitCode=78;nativeLoads=0;observed=$observed})
    Copy-Item $OfficialCoreAssembly (Join-Path $data 'Gua.Core.dll') -Force
    Copy-Item $OfficialRuntimeAssembly (Join-Path $data 'Gua.Runtime.dll') -Force
    Copy-Item (Join-Path $sdk 'testing/gua-build-manifest.json') $data -Force
    $observed=Join-Path $out 'all-sdk-swap-native.json'
    & dotnet $probe (Join-Path $swap 'GuardedGame.exe') $observed --headless --quit-after 300
    if($LASTEXITCODE){throw 'Whole SDK substitution observer failed.'}
    $native=Get-Content $observed -Raw|ConvertFrom-Json
    if($native.childExitCode -ne 78 -or @($native.guaNativeLoads).Count -ne 0 -or 'GamePolicy: integrity verification refused.' -notin $native.timeline -or (Test-Path $env:GUA_GODOT_TEST_RESULT)){throw 'Exported OFF game accepted exchanged native and managed SDK files.'}
    $results.Add(@{mode='off-all-sdk-swap';exitCode=78;nativeLoads=0;observed=$observed})
    # Native mismatch must not mask a missing managed-file check.
    foreach($file in 'gua.dll','gua_runtime.dll'){Copy-Item (Join-Path $sdk "off/$file") $data -Force}
    $observed=Join-Path $out 'managed-only-swap-native.json'
    & dotnet $probe (Join-Path $swap 'GuardedGame.exe') $observed --headless --quit-after 300
    if($LASTEXITCODE){throw 'Managed-only substitution observer failed.'}
    $native=Get-Content $observed -Raw|ConvertFrom-Json
    if($native.childExitCode -ne 78 -or @($native.guaNativeLoads).Count -ne 0 -or 'GamePolicy: integrity verification refused.' -notin $native.timeline -or (Test-Path $env:GUA_GODOT_TEST_RESULT)){throw 'Exported OFF game accepted exchanged managed SDK files.'}
    $results.Add(@{mode='off-managed-only-swap';exitCode=78;nativeLoads=0;observed=$observed})
    $results|ConvertTo-Json -Depth 6|Set-Content (Join-Path $out 'results.json')
} finally {foreach($key in $old.Keys){[Environment]::SetEnvironmentVariable($key,$old[$key])}}
$global:LASTEXITCODE=0
