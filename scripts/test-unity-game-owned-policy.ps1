param(
    [Parameter(Mandatory)][string]$UnityExecutable,
    [Parameter(Mandatory)][string]$ExistingFixture,
    [Parameter(Mandatory)][string]$ManagedDirectory,
    [Parameter(Mandatory)][string]$NativePackage,
    [Parameter(Mandatory)][string]$PlayerTemplate,
    [ValidateSet('off','testing','player')][string]$Mode='off',
    [string]$OfficialUnityPackage
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('build/unity-game-policy-'+$Mode+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Path $fixture | Out-Null
# Reuse only the known local fixture's sources/settings and embedded package
# copies. No registry/package/Editor upgrade or external fixture is introduced.
foreach($folder in 'Assets','Packages','ProjectSettings') { Copy-Item -LiteralPath (Join-Path $ExistingFixture $folder) -Destination $fixture -Recurse }
$managed=Join-Path $fixture 'Assets/Plugins/Managed'; $native=Join-Path $fixture 'Assets/Plugins/Native'
$gameNative=Join-Path $fixture 'Assets/GameSecurity/Plugins/x86_64'
New-Item -ItemType Directory -Force $gameNative | Out-Null
foreach($name in 'Gua.Core.dll','Gua.Runtime.dll','Gua.Unity.dll') { Copy-Item -LiteralPath (Join-Path $ManagedDirectory $name) -Destination $managed -Force }
foreach($name in 'gua.dll','gua_runtime.dll','gua-build-manifest.json') {Copy-Item -LiteralPath (Join-Path $NativePackage $name) -Destination $native -Force}
# This is a game-owned module, not part of the Gua runtime DLL set. Its policy is
# embedded AFTER Unity has emitted the actual final managed/native game files.
Copy-Item -LiteralPath $PlayerTemplate -Destination (Join-Path $gameNative 'GameSecurity_fixture.dll')
$meta=(Get-Content -LiteralPath (Join-Path $root 'scripts/unity-meta/gua.dll.meta') -Raw).Replace('isPreloaded: 0','isPreloaded: 1')
$meta=[regex]::Replace($meta,'(?m)^guid:.*$', 'guid: 2d4b941517f24657b50141a3222687d4')
$meta=[regex]::Replace($meta,'(?ms)(Editor: Editor\r?\n\s+second:\r?\n\s+enabled:) 1','$1 0')
if($meta -match '(?ms)Editor: Editor\r?\n\s+second:\r?\n\s+enabled: 1') {throw 'Player verifier must never be compatible with Editor.'}
$meta | Set-Content -LiteralPath (Join-Path $gameNative 'GameSecurity_fixture.dll.meta') -Encoding utf8NoBOM
Remove-Item -LiteralPath (Join-Path $fixture 'Assets/GuaVariantConsumer.cs')
Copy-Item -LiteralPath (Join-Path $root 'bindings/unity/Tests/Fixtures/GameOwnedPolicyConsumer.cs') -Destination (Join-Path $fixture 'Assets/GameOwnedPolicyConsumer.cs')
if($Mode -ne 'off') {
    $defines=if($Mode -eq 'player') {'-define:GUA_REMOTE_AUTOMATION,GUA_PLAYER_ONLY'} else {'-define:GUA_REMOTE_AUTOMATION'}
    $defines | Set-Content -LiteralPath (Join-Path $fixture 'Assets/csc.rsp') -Encoding utf8NoBOM
}
$beforeVariant=$env:GUA_BUILD_VARIANT; $beforeProfile=$env:GUA_OBSERVATION_PROFILE; $beforePort=$env:GUA_BRIDGE_PORT
try {
    $env:GUA_BUILD_VARIANT=if($Mode -eq 'off'){'shipping'}elseif($Mode -eq 'player'){'ai-player'}else{'qa'}
    $env:GUA_OBSERVATION_PROFILE=$null; $env:GUA_BRIDGE_PORT='19473'
    $editorLog=Join-Path $fixture 'editor.log'
    $editor=Start-Process -FilePath $UnityExecutable -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',"`"$fixture`"",'-executeMethod','GuaVariantFixtureBuild.Run','-logFile',"`"$editorLog`"") -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddMinutes(10)
    while(!$editor.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) { }
    if(!$editor.HasExited) {throw "Editor build remains pending (PID$($editor.Id)); no forced Editor termination was performed. Log: $editorLog"}
    if($editor.ExitCode -ne 0) {throw "Unity build failed: $editorLog"}
    $player=Join-Path $fixture 'Player/Consumer.exe'
    if(!(Test-Path -LiteralPath $player)) {throw 'Unity did not emit the Player.'}
    $data=Join-Path $fixture 'Player/Consumer_Data'
    $nativeOutput=Join-Path $data 'Plugins/x86_64'
    $identity=(Get-Content -LiteralPath (Join-Path $NativePackage 'gua-build-manifest.json') -Raw | ConvertFrom-Json).build
    $files=@(
        @{path='Plugins/x86_64/gua.dll';source=(Join-Path $nativeOutput 'gua.dll');native=$true},
        @{path='Plugins/x86_64/gua_runtime.dll';source=(Join-Path $nativeOutput 'gua_runtime.dll');native=$true}
    )
    # Capture final SDK-managed bytes after Unity's copy/compile/strip phases.
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $data 'Managed') -Filter Gua.*.dll -File) {
        $files+=@{path="Managed/$($file.Name)";source=$file.FullName;native=$false}
    }
    $policy=@{kind=1;mode=$(if($Mode -eq 'off'){0}elseif($Mode -eq 'player'){2}else{1});root='';sourceCommit=$identity.sourceCommit;buildId=$identity.buildId;files=$files}
    $policyFile=Join-Path $fixture 'game-policy.json'
    $policy | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $policyFile -Encoding utf8NoBOM
    dotnet build (Join-Path $root 'tools/Gua.GameBuild') -c Release --nologo -v quiet -m:1 -nr:false
    if($LASTEXITCODE) {throw 'Cannot build game policy writer.'}
    $writer=Join-Path $root 'tools/Gua.GameBuild/bin/Release/net10.0/Gua.GameBuild.dll'
    & dotnet $writer embed-native $PlayerTemplate $policyFile (Join-Path $nativeOutput 'GameSecurity_fixture.dll')
    if($LASTEXITCODE) {throw 'Cannot embed the final game policy.'}
    dotnet build (Join-Path $root 'tools/Gua.ProcessLoadProbe') -c Release --nologo -v quiet -m:1 -nr:false
    if($LASTEXITCODE) {throw 'Cannot build native observer.'}
    $observer=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
    $positive=Join-Path $fixture 'positive.native-loads.json'; $playerLog=Join-Path $fixture 'player.log'
    if($Mode -eq 'off') {
        & dotnet $observer $player $positive -batchmode -nographics -logFile $playerLog
        if($LASTEXITCODE) {throw 'Unity Player observer failed.'}
    } else {
        dotnet build (Join-Path $root 'tools/Gua.EngineWireProbe') -c Release --nologo -v quiet -m:1 -nr:false
        if($LASTEXITCODE) {throw 'Cannot build the external engine wire probe.'}
        $wire=Join-Path $root 'tools/Gua.EngineWireProbe/bin/Release/net10.0/Gua.EngineWireProbe.dll'
        $observeProcess=Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @("`"$observer`"","`"$player`"","`"$positive`"",'-batchmode','-nographics','-logFile',"`"$playerLog`"") -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixture 'observer.stdout.log') -RedirectStandardError (Join-Path $fixture 'observer.stderr.log')
        try {
            $readyDeadline=[DateTime]::UtcNow.AddSeconds(30)
            $ready=$false
            while([DateTime]::UtcNow -lt $readyDeadline -and !$observeProcess.HasExited) {
                $ready=(Test-Path -LiteralPath $playerLog) -and ([string](Get-Content -LiteralPath $playerLog -Raw)) -match 'GAME_OWNED_UNITY_READY'
                if($ready) {break}
                Start-Sleep -Milliseconds 100
            }
            if(!$ready) {throw "The guarded Unity Player did not become ready: $playerLog"}
            & dotnet $wire ws://127.0.0.1:19473 $Mode *> (Join-Path $fixture 'wire-probe.log')
            if($LASTEXITCODE) {Get-Content -LiteralPath (Join-Path $fixture 'wire-probe.log'); throw 'Actual external Unity wire probe failed.'}
            $finishDeadline=[DateTime]::UtcNow.AddSeconds(30)
            while(!$observeProcess.WaitForExit(1000) -and [DateTime]::UtcNow -lt $finishDeadline) { }
            if(!$observeProcess.HasExited -or $observeProcess.ExitCode -ne 0) {throw 'The observed Unity Player did not complete its accepted action.'}
        } finally {
            # Only this owned observer/Player tree; never terminate an Editor.
            if(!$observeProcess.HasExited) {$observeProcess.Kill($true)}
        }
    }
    $observed=Get-Content -LiteralPath $positive -Raw | ConvertFrom-Json
    $events=@($observed.nativeModuleNames)
    $rootLoad=[Array]::IndexOf($events,'GameSecurity_fixture.dll')
    $sdkLoad=[Array]::IndexOf($events,'gua.dll')
    $playerText=[string](Get-Content -LiteralPath $playerLog -Raw)
    if($observed.childExitCode -ne 0 -or @($observed.guaNativeLoads).Count -ne 2 -or $rootLoad -lt 0 -or $sdkLoad -le $rootLoad -or $playerText -notmatch 'GAME_POLICY_MODULE_PRESENT_BEFORE_CALL' -or $playerText -notmatch 'GAME_OWNED_UNITY_SUCCESS') {Get-Content -LiteralPath $playerLog -Tail 45; throw "Unity native preloading/positive contract failed: $positive"}
    $results=[Collections.Generic.List[object]]::new()
    $results.Add(@{case=$Mode;result='passed';unityVersion='6000.5.3f1';backend='Mono';evidence=$positive;playerLog=$playerLog})
    if($Mode -eq 'off' -and $OfficialUnityPackage) {
        $officialManaged=Join-Path $OfficialUnityPackage 'Runtime/Plugins/Managed'
        $officialNative=Join-Path $OfficialUnityPackage 'Runtime/Plugins/Windows/x86_64'
        Copy-Item -LiteralPath (Join-Path $officialNative 'gua.dll'),(Join-Path $officialNative 'gua_runtime.dll') -Destination $nativeOutput -Force
        foreach($file in Get-ChildItem -LiteralPath $officialManaged -Filter '*.dll' -File) {Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $data 'Managed') -Force}
        $negative=Join-Path $fixture 'official-swap-negative.native-loads.json'
        & dotnet $observer $player $negative -batchmode -nographics -logFile (Join-Path $fixture 'swap-player.log')
        if($LASTEXITCODE) {throw 'Unity substitution observer failed.'}
        $observed=Get-Content -LiteralPath $negative -Raw | ConvertFrom-Json
        if($observed.childExitCode -ne 78 -or @($observed.guaNativeLoads).Count -ne 0 -or @($observed.nativeModuleNames) -notcontains 'GameSecurity_fixture.dll') {throw 'Complete official ON DLL substitution was not refused before SDK native load.'}
        $results.Add(@{case='off-to-official-full-native-managed-set';result='rejected-before-sdk-native-load';evidence=$negative})
    }
    $results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixture 'results.json') -Encoding utf8NoBOM
    Write-Host "Actual Unity game-owned policy evidence: $fixture"
} finally {$env:GUA_BUILD_VARIANT=$beforeVariant;$env:GUA_OBSERVATION_PROFILE=$beforeProfile;$env:GUA_BRIDGE_PORT=$beforePort}
$global:LASTEXITCODE=0
