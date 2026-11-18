param(
    [Parameter(Mandatory)][string]$ShippingPackage,
    [Parameter(Mandatory)][string]$TestingPackage,
    [Parameter(Mandatory)][string]$PlayerTemplate,
    [Parameter(Mandatory)][string]$EditorTemplate,
    [Parameter(Mandatory)][string]$CoreAssembly,
    [Parameter(Mandatory)][string]$RuntimeAssembly
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root ('build/native-game-policy-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
$data=Join-Path $output 'Player_Data'; $native=Join-Path $data 'Plugins/x86_64'; $managed=Join-Path $data 'Managed'
New-Item -ItemType Directory -Force $native,$managed | Out-Null
dotnet build (Join-Path $root 'tools/Gua.GameBuild') -c Release --nologo -v quiet -m:1 -nr:false
if($LASTEXITCODE) { throw 'Cannot build game policy data writer.' }
dotnet build (Join-Path $root 'tools/Gua.ProcessLoadProbe') -c Release --nologo -v quiet -m:1 -nr:false
if($LASTEXITCODE) { throw 'Cannot build independent load observer.' }
$tool=Join-Path $root 'tools/Gua.GameBuild/bin/Release/net10.0/Gua.GameBuild.dll'
$observer=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
$dotnet=(Get-Command dotnet).Source
Copy-Item -LiteralPath (Join-Path $ShippingPackage 'gua.dll'),(Join-Path $ShippingPackage 'gua_runtime.dll') -Destination $native
Copy-Item -LiteralPath $CoreAssembly -Destination (Join-Path $managed 'Gua.Core.dll')
Copy-Item -LiteralPath $RuntimeAssembly -Destination (Join-Path $managed 'Gua.Runtime.dll')
$identity=(Get-Content -LiteralPath (Join-Path $ShippingPackage 'gua-build-manifest.json') -Raw | ConvertFrom-Json).build
$files=@(
    @{path='Plugins/x86_64/gua.dll';source=(Join-Path $native 'gua.dll');native=$true},
    @{path='Plugins/x86_64/gua_runtime.dll';source=(Join-Path $native 'gua_runtime.dll');native=$true},
    @{path='Managed/Gua.Core.dll';source=(Join-Path $managed 'Gua.Core.dll');native=$false},
    @{path='Managed/Gua.Runtime.dll';source=(Join-Path $managed 'Gua.Runtime.dll');native=$false}
)
$policy=@{kind=1;mode=0;root='';sourceCommit=$identity.sourceCommit;buildId=$identity.buildId;files=$files}
$policyFile=Join-Path $output 'policy.json'
$policy | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $policyFile -Encoding utf8NoBOM
$module=Join-Path $native 'GameSecurity_fixture.dll'
& dotnet $tool embed-native $PlayerTemplate $policyFile $module
if($LASTEXITCODE) { throw 'Cannot embed game-owned Player expectations.' }
$positive=Join-Path $output 'positive.native-loads.json'
& dotnet $observer $dotnet $positive $tool check-native $module
if($LASTEXITCODE) { throw 'Independent observer failed.' }
$observed=Get-Content -LiteralPath $positive -Raw | ConvertFrom-Json
if($observed.childExitCode -ne 0 -or @($observed.guaNativeLoads).Count -ne 2) { throw 'Native game policy positive did not load its verified pair.' }
$results=[Collections.Generic.List[object]]::new()
$results.Add(@{case='player-policy-off';result='passed';evidence=$positive})
# The managed file is not loaded by this host. A denied overwrite therefore tests
# the verifier's lease, rather than Windows' ordinary loaded-image write lock.
$holdLog=Join-Path $output 'lease-host.log'
$hold=Start-Process -FilePath $dotnet -ArgumentList @("`"$tool`"",'check-native',"`"$module`"",'5000') -WindowStyle Hidden -PassThru -RedirectStandardOutput $holdLog
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while([DateTime]::UtcNow -lt $deadline -and !(Test-Path -LiteralPath $holdLog)) { Start-Sleep -Milliseconds 50 }
    while([DateTime]::UtcNow -lt $deadline -and ([string](Get-Content -LiteralPath $holdLog -Raw)) -notmatch '"status":1') { if($hold.HasExited) {throw 'Lease host failed before ready.'}; Start-Sleep -Milliseconds 50 }
    if(([string](Get-Content -LiteralPath $holdLog -Raw)) -notmatch '"status":1' -or $hold.HasExited) { throw 'Lease host did not report successful verification while alive.' }
    $denied=$false
    try { $attempt=[IO.File]::Open((Join-Path $managed 'Gua.Core.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite); $attempt.Dispose() } catch [IO.IOException] { $denied=($_.Exception.HResult -band 0xffff) -in 32,33 }
    if(!$denied) { throw 'The verified managed file remained writable while its game policy was alive.' }
    $results.Add(@{case='verified-managed-file-write';result='denied-while-process-alive';evidence=$holdLog})
} finally { if(!$hold.WaitForExit(10000)) { $hold.Kill($true) } }
Copy-Item -LiteralPath (Join-Path $TestingPackage 'gua.dll'),(Join-Path $TestingPackage 'gua_runtime.dll') -Destination $native -Force
$negative=Join-Path $output 'swap-negative.native-loads.json'
& dotnet $observer $dotnet $negative $tool check-native $module
if($LASTEXITCODE) { throw 'Negative observer failed.' }
$observed=Get-Content -LiteralPath $negative -Raw | ConvertFrom-Json
if($observed.childExitCode -ne 78 -or @($observed.guaNativeLoads).Count -ne 0) { throw 'Native game-owned policy did not reject ON replacement before SDK load.' }
$results.Add(@{case='off-to-on-native-set';result='rejected-before-sdk-load';evidence=$negative})
$preloaded=Join-Path $output 'preloaded-negative.native-loads.json'
& dotnet $observer $dotnet $preloaded $tool check-native-preloaded $module (Join-Path $native 'gua.dll')
if($LASTEXITCODE) { throw 'Preload observer failed.' }
$observed=Get-Content -LiteralPath $preloaded -Raw | ConvertFrom-Json
if($observed.childExitCode -ne 79 -or @($observed.guaNativeLoads).Count -ne 1) { throw 'Native game-owned policy did not reject an already loaded SDK module.' }
$results.Add(@{case='preloaded-sdk';result='rejected-before-sdk-initialization';evidence=$preloaded})
# This template intentionally has no policy resource. Its compile-time Editor
# behavior must return80 to the caller; never terminate a real Editor to test it.
$editorLog=Join-Path $output 'editor-malformed-policy.log'
& dotnet $tool check-native $EditorTemplate *> $editorLog
if($LASTEXITCODE -or (Get-Content -LiteralPath $editorLog -Raw) -notmatch '"status":80') { throw 'Editor template did not return a nonfatal policy error.' }
$results.Add(@{case='editor-template-malformed-policy';result='nonfatal-error-returned-to-host';evidence=$editorLog})
$results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "Native game policy template evidence: $output"
$global:LASTEXITCODE=0
