param([Parameter(Mandatory)][string]$CandidateDirectory,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(Test-Path $OutputDirectory){throw 'Use a new candidate acceptance output directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
$delivery=Get-Content (Join-Path $CandidateDirectory 'game-sdk-delivery.json') -Raw | ConvertFrom-Json
$archive=@($delivery.files | Where-Object path -Like 'gua-game-build-cpp-win-x64-*.zip')
if($archive.Count -ne 1){throw 'Require exactly one delivered C++ SDK archive.'}
$zip=(Resolve-Path (Join-Path $CandidateDirectory $archive[0].path)).Path
if((Get-FileHash $zip).Hash.ToLowerInvariant() -ne $archive[0].sha256){throw 'C++ delivery archive digest mismatch.'}
$sdk=Join-Path $out 'sdk'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$sdk)
$inventory=Get-Content (Join-Path $sdk 'cpp-sdk-files.json') -Raw | ConvertFrom-Json
foreach($entry in $inventory.files){if((Get-FileHash (Join-Path $sdk $entry.path)).Hash.ToLowerInvariant() -ne $entry.sha256){throw 'C++ SDK archive roundtrip changed a file.'}}
$generator=& (Join-Path $PSScriptRoot 'get-msvc-generator.ps1')
foreach($tool in 'Gua.ProcessLoadProbe','Gua.EngineWireProbe') {
    & dotnet build (Join-Path $root "tools/$tool") -c Release --nologo -v quiet -m:1 -nr:false *> (Join-Path $out "$tool-build.log")
    if($LASTEXITCODE){throw "Cannot build $tool"}
}
$probe=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
$wire=Join-Path $root 'tools/Gua.EngineWireProbe/bin/Release/net10.0/Gua.EngineWireProbe.dll'
$results=[System.Collections.Generic.List[object]]::new()
foreach($mode in 'off','testing','player') {
    $dir=Join-Path $out $mode
    & cmake -S (Join-Path $root 'tests/cpp-game-policy') -B $dir -G $generator -A x64 "-DGuaGameBuild_DIR=$sdk" "-DGAME_MODE=$mode" *> (Join-Path $out "$mode-configure.log")
    if($LASTEXITCODE){throw "Candidate consumer configure failed: $mode"}
    & cmake --build $dir --config Release --parallel 1 -- /nr:false *> (Join-Path $out "$mode-build.log")
    if($LASTEXITCODE){throw "Candidate consumer build failed: $mode"}
    $observed=Join-Path $out "$mode-observed.json"
    $game=Join-Path $dir 'Release/game.exe'
    if($mode -eq 'off') {
        & dotnet $probe $game $observed
        if($LASTEXITCODE){throw 'OFF candidate observer failed.'}
    } else {
        $hostResult=Join-Path $out "$mode-host.json"
        $observer=Start-Process dotnet -ArgumentList @("`"$probe`"","`"$game`"","`"$observed`"",'--wire',"`"$hostResult`"") -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $out "$mode-observer.stdout.log") -RedirectStandardError (Join-Path $out "$mode-observer.stderr.log")
        try {
            $deadline=[DateTime]::UtcNow.AddSeconds(20)
            while(!(Test-Path $hostResult) -and !$observer.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 100}
            if(!(Test-Path $hostResult)){throw "Candidate did not reach guarded wire readiness: $mode"}
            & dotnet $wire ws://127.0.0.1:19573 $mode *> (Join-Path $out "$mode-wire.json")
            if($LASTEXITCODE){throw "Candidate external wire request failed: $mode"}
            if(!$observer.WaitForExit(20000) -or $observer.ExitCode -ne 0){throw "Candidate observer did not complete: $mode"}
            $hostValue=Get-Content $hostResult -Raw | ConvertFrom-Json
            if($hostValue.consumed -ne 1){throw 'Host did not consume exactly one real external input.'}
            $results.Add(@{case="$mode-external-input";consumed=1;wire=(Join-Path $out "$mode-wire.json");host=$hostResult})
        } finally {
            # Only this owned observer/fixture tree, never an Editor.
            if(!$observer.HasExited){$observer.Kill($true)}
        }
    }
    $value=Get-Content $observed -Raw | ConvertFrom-Json
    if($value.childExitCode -ne 0 -or @($value.guaNativeLoads).Count -ne 2 -or $value.timeline[0] -notlike 'load:GameSecurity_*'){throw "Candidate positive/load order assertion failed: $mode"}
    $results.Add(@{case="$mode-candidate";exitCode=0;nativeLoads=2;evidence=$observed})
}
$swap=Join-Path $out 'official-on-replacement'
New-Item -ItemType Directory $swap | Out-Null
Copy-Item (Join-Path $out 'off/Release/*') $swap -Recurse
foreach($dll in 'gua.dll','gua_runtime.dll'){Copy-Item (Join-Path $sdk "testing/$dll") $swap -Force}
& dotnet $probe (Join-Path $swap 'game.exe') (Join-Path $out 'swap-observed.json') --wire (Join-Path $out 'swap-host.json')
if($LASTEXITCODE){throw 'Swap observer failed.'}
$value=Get-Content (Join-Path $out 'swap-observed.json') -Raw | ConvertFrom-Json
if($value.childExitCode -ne 78 -or @($value.guaNativeLoads).Count -ne 0 -or (Test-Path (Join-Path $out 'swap-host.json'))){throw 'Packaged OFF-to-ON swap escaped pre-load refusal.'}
$results.Add(@{case='candidate-off-to-official-on';exitCode=78;nativeLoads=0;hostEntryReached=$false})
# The archive consumer still contains the specific eager-import deployment gate.
& dotnet (Join-Path $sdk 'tools/Gua.GameBuild.dll') check-cpp-imports (Join-Path $out 'off/Release/game.exe') (Join-Path $out 'off/Release/legacy.dll') *> (Join-Path $out 'candidate-legacy-rejection.log')
if(!$LASTEXITCODE -or (Get-Content (Join-Path $out 'candidate-legacy-rejection.log') -Raw) -notmatch 'Eager Gua import before verification'){throw 'Archived dependency checker lost its eager-import rejection.'}
$results.Add(@{case='candidate-import-gate';refused=$true})
@{archive=$zip;archiveSha256=$archive[0].sha256;roundtripFileCount=@($inventory.files).Count;cases=$results} | ConvertTo-Json -Depth 9 | Set-Content (Join-Path $out 'results.json') -Encoding utf8NoBOM
Write-Host "Packaged C++ candidate acceptance passed: $out"
$global:LASTEXITCODE=0
