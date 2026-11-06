param(
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$DependencyFeed,
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version='0.0.0-integrity-fixture'
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$sdk=(Resolve-Path $BundleRoot).Path
$feed=(Resolve-Path $DependencyFeed).Path
$engine=(Resolve-Path $GodotExecutable).Path
$godotFeed=Join-Path (Split-Path $engine) 'GodotSharp/Tools/nupkgs'
if(!(Test-Path $godotFeed)){throw 'Require the official portable Godot .NET engine and its local SDK feed.'}
if(Test-Path $OutputDirectory){throw 'Use a new Godot .NET acceptance directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
foreach($tool in 'Gua.ProcessLoadProbe','Gua.EngineWireProbe','Gua.ArtifactProbe') {
    dotnet build (Join-Path $root "tools/$tool") -c Release --nologo -v quiet -m:1 -nr:false *> (Join-Path $out "$tool-build.log")
    if($LASTEXITCODE){throw "Cannot build $tool"}
}
$probe=Join-Path $root 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
$wire=Join-Path $root 'tools/Gua.EngineWireProbe/bin/Release/net10.0/Gua.EngineWireProbe.dll'
$ilProbe=Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
$results=[Collections.Generic.List[object]]::new()
function Assert-Observed($name,$path,$exitCode,$count,$expectedDirectory) {
    $v=Get-Content $path -Raw|ConvertFrom-Json
    if($v.childExitCode -ne $exitCode -or @($v.guaNativeLoads).Count -ne $count){throw "Unexpected exit/native load count: $name"}
    $before=[Array]::IndexOf(@($v.timeline),'GameSource: before first policy call.')
    if($before -lt 0){throw "Game-owned policy did not execute: $name"}
    if($count -gt 0) {
        $checked=[Array]::IndexOf(@($v.timeline),'GamePolicy: verification succeeded before SDK initialization.')
        $loaded=[Array]::IndexOf(@($v.timeline),'load:gua.dll')
        if($checked -lt $before -or $loaded -lt $checked){throw "Hash verification/load ordering violated: $name"}
        foreach($file in $v.guaNativePaths) {
            $normalized=if($file.StartsWith('\\?\')){$file.Substring(4)}else{$file}
            if([IO.Path]::GetDirectoryName($normalized) -ine $expectedDirectory){throw "A native environment/search override replaced the verified module: $name"}
        }
    } elseif('GamePolicy: integrity verification refused.' -notin $v.timeline){throw 'Missing specific integrity refusal evidence.'}
    $results.Add(@{case=$name;exitCode=$v.childExitCode;nativeLoads=@($v.guaNativeLoads).Count;observed=$path})
}
function Observe-Game($name,$project,$mode) {
    $observed=Join-Path $out "$name-native.json"
    $result=Join-Path $out "$name-game.json"
    $env:GUA_GODOT_TEST_RESULT=$result
    $arguments=@('--headless','--path',$project,'--max-fps','60','--quit-after','1800')
    if($mode -eq 'off') {
        & dotnet $probe $engine $observed @arguments
        if($LASTEXITCODE){throw 'Owned observer failed.'}
    } else {
        $argsList=@("`"$probe`"","`"$engine`"","`"$observed`"",'--headless','--path',"`"$project`"",'--max-fps','60','--quit-after','1800')
        $child=Start-Process dotnet -ArgumentList $argsList -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $out "$name-observer.stdout") -RedirectStandardError (Join-Path $out "$name-observer.stderr")
        try {
            $deadline=[DateTime]::UtcNow.AddSeconds(20)
            while(!(Test-Path $result) -and !$child.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 100}
            if(!(Test-Path $result)){throw "Godot .NET did not reach guarded readiness: $name"}
            & dotnet $wire ws://127.0.0.1:19587 $mode *> (Join-Path $out "$name-wire.json")
            if($LASTEXITCODE){throw "External input failed: $name"}
            if(!$child.WaitForExit(20000) -or $child.ExitCode -ne 0){throw 'Observed game did not complete.'}
        } finally {
            # Only an owned GAME/observer tree. Never used for an Editor.
            if(!$child.HasExited){$child.Kill($true)}
        }
    }
    $value=Get-Content $result -Raw|ConvertFrom-Json
    if($value.mode -ne $mode -or !$value.ready -or $value.remote -ne ($mode -ne 'off') -or $value.consumed -ne $(if($mode -eq 'off'){0}else{1})){throw "Wrong real game result: $name"}
    Assert-Observed $name $observed 0 2 (Join-Path $project '.godot/mono/temp/bin/Debug').Replace('/','\')
    $results.Add(@{case="$name-input";mode=$mode;consumed=$value.consumed;game=$result})
}
$old=@{}
foreach($key in 'GUA_GODOT_TEST_RESULT','GUA_GODOT_TEST_PORT','GUA_GODOT_EXPECT_PROFILE_REFUSAL','GUA_NATIVE_DIR','GUA_RUNTIME_NATIVE_DIR','GUA_OBSERVATION_PROFILE'){$old[$key]=[Environment]::GetEnvironmentVariable($key)}
try {
    $env:GUA_GODOT_TEST_PORT='19587'
    # Hostile runtime settings must not select another native pair/ceiling.
    $env:GUA_NATIVE_DIR=Join-Path $sdk 'testing'
    $env:GUA_RUNTIME_NATIVE_DIR=Join-Path $sdk 'testing'
    $env:GUA_OBSERVATION_PROFILE='debug'
    foreach($mode in 'off','testing','player') {
        $env:GUA_OBSERVATION_PROFILE=if($mode -eq 'player'){$null}else{'debug'}
        $env:GUA_GODOT_EXPECT_PROFILE_REFUSAL=$null
        $project=(New-Item -ItemType Directory (Join-Path $out $mode)).FullName
        Copy-Item (Join-Path $root 'tests/godot-dotnet-game-policy/*') $project
        $setting=if($mode -eq 'off'){''}else{"<GuaRemoteMode>$mode</GuaRemoteMode>"}
        @"
<Project Sdk="Godot.NET.Sdk/4.7.0"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>GuardedGodotGame</AssemblyName><EnableDynamicLoading>true</EnableDynamicLoading><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><RestorePackagesPath>$out/packages</RestorePackagesPath>$setting</PropertyGroup><ItemGroup><PackageReference Include="Gua.GameBuild" Version="[$Version]" /></ItemGroup></Project>
"@|Set-Content (Join-Path $project 'GuardedGodotGame.csproj') -Encoding utf8NoBOM
        $f=[Security.SecurityElement]::Escape($feed);$g=[Security.SecurityElement]::Escape($godotFeed)
        "<configuration><packageSources><clear/><add key=`"private-gua`" value=`"$f`"/><add key=`"official-godot`" value=`"$g`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources><packageSourceMapping><packageSource key=`"private-gua`"><package pattern=`"Gua.*`"/></packageSource><packageSource key=`"official-godot`"><package pattern=`"Godot*`"/></packageSource><packageSource key=`"nuget`"><package pattern=`"*`"/></packageSource></packageSourceMapping></configuration>"|Set-Content (Join-Path $project 'NuGet.Config') -Encoding utf8NoBOM
        dotnet build (Join-Path $project 'GuardedGodotGame.csproj') --nologo -v quiet -m:1 -nr:false *> (Join-Path $out "$mode-build.log")
        if($LASTEXITCODE){throw "PackageReference-only Godot game build failed: $mode"}
        $binary=Join-Path $project '.godot/mono/temp/bin/Debug'
        $native=@(Get-ChildItem $binary -Recurse -File|Where-Object Name -In 'gua.dll','gua_runtime.dll')
        if($native.Count -ne 2){throw 'Legacy native package assets leaked.'}
        foreach($file in $native){if((Get-FileHash $file.FullName).Hash -ne (Get-FileHash (Join-Path $sdk "$mode/$($file.Name)")).Hash){throw 'Wrong native prebuilt selected.'}}
        $il=(& dotnet $ilProbe game (Join-Path $binary 'GuardedGodotGame.dll'))|ConvertFrom-Json
        if($LASTEXITCODE -or !$il.gameOwnedPolicy -or $il.remoteStartReference -ne ($mode -ne 'off')){throw 'Godot game IL has the wrong compiled remote-start boundary.'}
        Observe-Game $mode $project $mode
    }
    $player=Join-Path $out 'player'
    $env:GUA_OBSERVATION_PROFILE='debug'
    $env:GUA_GODOT_EXPECT_PROFILE_REFUSAL='1'
    $env:GUA_GODOT_TEST_RESULT=Join-Path $out 'player-debug-env-refused.json'
    $profileObserved=Join-Path $out 'player-debug-env-native.json'
    & dotnet $probe $engine $profileObserved --headless --path $player --max-fps 60 --quit-after 600
    if($LASTEXITCODE){throw 'Player profile mutation observer failed.'}
    Assert-Observed 'player-debug-env' $profileObserved 72 2 (Join-Path $player '.godot/mono/temp/bin/Debug').Replace('/','\')
    $refusal=Get-Content $env:GUA_GODOT_TEST_RESULT -Raw|ConvertFrom-Json
    if(!$refusal.profileRefused -or $refusal.ready -or $refusal.remote -or $refusal.consumed -ne 0){throw 'Player debug profile override escaped.'}
    $env:GUA_OBSERVATION_PROFILE=$null;$env:GUA_GODOT_EXPECT_PROFILE_REFUSAL=$null
    $off=Join-Path $out 'off'
    $swap=(New-Item -ItemType Directory (Join-Path $out 'off-on-swap')).FullName
    Copy-Item (Join-Path $off '*') $swap -Recurse -Force
    # Include .godot explicitly on hosts where dot-prefixed inputs are hidden.
    if(!(Test-Path (Join-Path $swap '.godot'))){Copy-Item (Join-Path $off '.godot') $swap -Recurse -Force}
    $binary=Join-Path $swap '.godot/mono/temp/bin/Debug'
    foreach($file in 'gua.dll','gua_runtime.dll'){Copy-Item (Join-Path $sdk "testing/$file") $binary -Force}
    $env:GUA_GODOT_TEST_RESULT=Join-Path $out 'swap-unreachable-game.json'
    $observed=Join-Path $out 'swap-native.json'
    & dotnet $probe $engine $observed --headless --path $swap --max-fps 60 --quit-after 600
    if($LASTEXITCODE){throw 'Swap observer failed.'}
    Assert-Observed 'off-to-legitimate-on' $observed 78 0 ''
    if(Test-Path $env:GUA_GODOT_TEST_RESULT){throw 'Game callbacks ran after failed verification.'}
    $invalid=Join-Path $out 'host-refusal';Copy-Item $off $invalid -Recurse
    function Assert-HostRefusal($name,$message,[string[]]$settings=@()) {
        $log=Join-Path $out "$name-build-refused.log"
        dotnet build (Join-Path $invalid 'GuardedGodotGame.csproj') --no-restore --nologo -v quiet -m:1 -nr:false @settings *> $log
        if($LASTEXITCODE -eq 0 -or (Get-Content $log -Raw) -notmatch [Regex]::Escape($message)){throw "Missing specific host refusal: $name"}
        $results.Add(@{case=$name;result='build-refused';evidence=$log})
    }
    $hidden=(New-Item -ItemType Directory (Join-Path $invalid '.hidden')).FullName
    'entry_symbol="gua_godot_library_init"'|Set-Content (Join-Path $hidden 'legacy.gdextension')
    'res://.hidden/legacy.gdextension'|Set-Content (Join-Path $invalid '.godot/extension_list.cfg')
    Assert-HostRefusal 'cached-preloaded-gua-extension' 'not a preloaded Gua GDExtension'
    Remove-Item -LiteralPath (Join-Path $invalid '.godot/extension_list.cfg'),(Join-Path $hidden 'legacy.gdextension')
    $legacy=(New-Item -ItemType Directory (Join-Path $invalid 'addons/gua') -Force).FullName
    '// legacy duplicate fixture'|Set-Content (Join-Path $legacy 'GuaGodotRuntime.cs')
    Assert-HostRefusal 'duplicate-legacy-managed-adapter' 'Remove the legacy addons/gua/GuaGodotRuntime.cs'
    Remove-Item -LiteralPath (Join-Path $legacy 'GuaGodotRuntime.cs')
    Add-Content (Join-Path $invalid 'project.godot') "`n[autoload]`nUnrelated=PackedStringArray(`"[value]`")`nGuaGamePolicy=`"*res://legacy.gd`""
    Assert-HostRefusal 'mixed-gdscript-policy' 'Remove the separate GDScript GuaGamePolicy autoload'
    Copy-Item (Join-Path $off 'project.godot') $invalid -Force
    Assert-HostRefusal 'unsupported-engine-loader-version' 'Godot.NET.Sdk 4.7.0 only' @('-p:PackageVersion_Godot_NET_Sdk=4.8.0')
    Assert-HostRefusal 'unsupported-godot-platform' 'net10.0 and Windows x64' @('-p:GodotTargetPlatform=linuxbsd')
    $results|ConvertTo-Json -Depth 8|Set-Content (Join-Path $out 'results.json') -Encoding utf8NoBOM
    Write-Host "Godot .NET game policy evidence: $out"
} finally {
    foreach($key in $old.Keys){[Environment]::SetEnvironmentVariable($key,$old[$key])}
}
$global:LASTEXITCODE=0
