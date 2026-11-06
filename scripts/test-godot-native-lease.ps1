param(
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$sdk=(Resolve-Path $SdkDirectory).Path
$engine=(Resolve-Path $GodotExecutable).Path
if(Test-Path $OutputDirectory){throw 'Use a new race acceptance directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
dotnet build (Join-Path $repo 'tools/Gua.ProcessLoadProbe') -c Release --nologo -v quiet -m:1 -nr:false
if($LASTEXITCODE){throw 'Independent load observer build failed.'}
$probe=Join-Path $repo 'tools/Gua.ProcessLoadProbe/bin/Release/net10.0/Gua.ProcessLoadProbe.dll'
$dotnet=(Get-Command dotnet).Source
$results=[Collections.Generic.List[object]]::new()
foreach($case in 'old-window-oracle','locked-off','locked-testing','locked-player','locked-namespace','preexisting-writer') {
    $mode=if($case -eq 'locked-testing'){'testing'}elseif($case -eq 'locked-player'){'player'}else{'off'}
    $project=(New-Item -ItemType Directory (Join-Path $out $case)).FullName
    Copy-Item (Join-Path $sdk 'addons') $project -Recurse
    # This existing compatibility caller is identical in every scenario. In OFF
    # the native start API is a failure stub. It must not gain functionality merely
    # because a legitimate ON DLL replaces the checked bytes during startup.
    $fixture=(Get-Content (Join-Path $repo 'native/gua-godot/tests/game_owned_policy_fixture.gd') -Raw).Replace("`r`n","`n")
    $fixture=$fixture.Replace('var started: bool = adapter.start_inspector_bridge(19469)', 'adapter.update("lease-ready")'+"`n`t"+'var context: Object = adapter.context'+"`n`t"+'var started: bool = context.start_inspector_bridge(19583)')
    $fixture=$fixture.Replace('_check(started == (GuaGamePolicy.REMOTE_MODE != "off"))'+"`n`t"+'var context: Object = adapter.context','_check(started == bool(JSON.parse_string(context.get_build_info_json()).remoteEnabled))')
    $fixture=$fixture.Replace('ws://127.0.0.1:19469','ws://127.0.0.1:19583')
    $fixture=$fixture.Replace('print("GAME_OWNED_GODOT_SUCCESS: ", GuaGamePolicy.REMOTE_MODE)', 'print("LEASE_GAME_REMOTE=", started)')
    $fixture=$fixture.Replace('adapter.attach(self)', 'adapter.attach(self)'+"`n`t"+'var save := FileAccess.open("user://lease-local-save.txt", FileAccess.WRITE)'+"`n`t"+'_check(save != null)'+"`n`t"+'save.store_string("local-save-preserved")'+"`n`t"+'save.close()'+"`n`t"+'_check(FileAccess.get_file_as_string("user://lease-local-save.txt") == "local-save-preserved")')
    Set-Content (Join-Path $project 'fixture.gd') $fixture -Encoding utf8NoBOM
    @"
config_version=5
[application]
config/name="Gua lease $case"
run/main_scene="res://fixture.tscn"
[editor_plugins]
enabled=PackedStringArray("res://addons/gua/plugin.cfg")
[gua]
remote_mode="$mode"
[rendering]
renderer/rendering_method="gl_compatibility"
"@ | Set-Content (Join-Path $project 'project.godot')
    @'
[gd_scene load_steps=2 format=3]
[ext_resource type="Script" path="res://fixture.gd" id="1"]
[node name="Fixture" type="Control"]
script=ExtResource("1")
'@ | Set-Content (Join-Path $project 'fixture.tscn')
    $environment=@{APPDATA=(Join-Path $project 'userdata');LOCALAPPDATA=(Join-Path $project 'localdata')}
    New-Item -ItemType Directory -Force $environment.APPDATA,$environment.LOCALAPPDATA|Out-Null
    $editor=Start-Process $engine -ArgumentList @('--headless','--editor','--path',$project,'--import') -Environment $environment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $project 'import.stdout') -RedirectStandardError (Join-Path $project 'import.stderr')
    if(!$editor.WaitForExit(90000)){throw "Editor remains alive PID=$($editor.Id); leave it running."}
    $importText=(Get-Content (Join-Path $project 'import.stdout') -Raw)+(Get-Content (Join-Path $project 'import.stderr') -Raw)
    if($editor.ExitCode -ne 0 -or $importText -match 'SCRIPT ERROR|Parse Error'){throw 'Actual Godot import failed.'}
    $target=Join-Path $project 'gua-native/gua.dll'
    $ready=Join-Path $project 'ready';$resume=Join-Path $project 'resume'
    $replacementRoot=Join-Path $project 'addons/gua/.variants/testing'
    $manifest=Get-Content (Join-Path $replacementRoot 'gua-build-manifest.json') -Raw|ConvertFrom-Json
    $members=@($manifest.files|Where-Object path -Like '*.windows.release.x86_64.dll')
    if($members.Count -ne 1){throw 'Require the one real ON extension.'}
    $replacement=Join-Path $replacementRoot $members[0].path
    $writer=$null
    $childArgs=@('--headless','--path',$project,'--quit-after','1200')
    $observerArgs=@($probe,$engine,(Join-Path $project 'observed.json'))
    if($case -eq 'old-window-oracle') {
        # Deliberate isolated mutation: restore the former hash-then-path-load
        # sequence and widen only that gap. No bypass hook ships in the product.
        $policyPath=Join-Path $project 'gua_generated/policy.gd'
        $policy=(Get-Content $policyPath -Raw).Replace("`r`n","`n")
        $check="`tif FileAccess.get_sha256(`"res://gua-native/gua.dll`") != EXPECTED_SHA256:`n`t`t_refuse(`"test original hash did not match`")`n`t`treturn`n"
        $check+="`tvar signal_file := FileAccess.open(`"res://ready`", FileAccess.WRITE)`n`tsignal_file.store_string(`"checked-before-sdk`")`n`tsignal_file.close()`n`tvar deadline := Time.get_ticks_msec() + 15000`n`twhile not FileAccess.file_exists(`"res://resume`") and Time.get_ticks_msec() < deadline:`n`t`tOS.delay_msec(10)`n"
        $policy=$policy.Replace("`tif GDExtensionManager.load_extension(DESCRIPTOR)",$check+"`tif GDExtensionManager.load_extension(DESCRIPTOR)")
        Set-Content $policyPath $policy -Encoding utf8NoBOM
        '[configuration]'+"`n"+'entry_symbol="gua_godot_library_init"'+"`n"+'compatibility_minimum="4.7"'+"`n"+'[libraries]'+"`n"+'windows.x86_64="res://gua-native/gua.dll"'|Set-Content (Join-Path $project 'gua_generated/native/gua.gdextension')
    } elseif($case -eq 'preexisting-writer') {
        $writer=[IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    } else {$observerArgs+=@($(if($case -eq 'locked-namespace'){'--pause-before-sdk-open'}else{'--pause-before-sdk'}),$ready,$resume)}
    $observerArgs+=$childArgs
    $log=Join-Path $project 'game.log'
    $observerArgs+=@('--log-file',$log)
    try {
        $process=Start-Process $dotnet -ArgumentList $observerArgs -Environment $environment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $project 'probe.stdout') -RedirectStandardError (Join-Path $project 'probe.stderr')
        $denied=[Collections.Generic.List[string]]::new()
        if($case -ne 'preexisting-writer') {
            $deadline=[DateTime]::UtcNow.AddSeconds(20)
            while(!(Test-Path $ready) -and !$process.HasExited -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 20}
            if(!(Test-Path $ready)){throw 'The specific verification/load boundary was not reached.'}
            if($case -eq 'old-window-oracle'){Copy-Item $replacement $target -Force}
            else {
                $barrier=Get-Content $ready -Raw|ConvertFrom-Json
                $stage=if($case -eq 'locked-namespace'){'namespace-before-sdk'}else{'locked-before-sdk'}
                if($barrier.nativeLoads -ne 0 -or $barrier.stage -ne $stage){throw 'SDK loaded before the controlled lock boundary.'}
                $operations=if($case -eq 'locked-namespace'){@('rename-sdk-folder','rename-project-folder')}else{@('overwrite','rename-file','rename-sdk-folder')}
                foreach($operation in $operations) {
                    $blocked=$false
                    try {
                        switch($operation){
                            'overwrite' {Copy-Item $replacement $target -Force}
                            'rename-file' {[IO.File]::Move($target,$target+'.moved');[IO.File]::Move($target+'.moved',$target)}
                            'rename-sdk-folder' {$folder=Split-Path $target;[IO.Directory]::Move($folder,$folder+'.moved');[IO.Directory]::Move($folder+'.moved',$folder)}
                            'rename-project-folder' {[IO.Directory]::Move($project,$project+'.moved');[IO.Directory]::Move($project+'.moved',$project)}
                        }
                    }catch [IO.IOException]{$blocked=$true}catch [UnauthorizedAccessException]{$blocked=$true}
                    if(!$blocked){throw "Protected SDK mutation was not denied: $operation"}
                    $denied.Add($operation)
                }
                # Before the SDK file is opened, a data replacement is allowed
                # but must then fail the subsequent hash check, not initialize it.
                if($case -eq 'locked-namespace'){Copy-Item $replacement $target -Force}
            }
            Set-Content $resume 'continue'
        }
        if(!$process.WaitForExit(70000)){throw 'Owned game probe did not finish.'}
        if($process.ExitCode){throw 'Independent probe failed.'}
        $observed=Get-Content (Join-Path $project 'observed.json') -Raw|ConvertFrom-Json
        $text=Get-Content $log -Raw
        if($case -in 'preexisting-writer','locked-namespace') {
            if(@($observed.guaNativeLoads).Count -ne 0 -or $observed.timeline -notcontains 'GamePolicy: integrity verification refused.' -or $text.Contains('LEASE_GAME_REMOTE=')){throw 'Pre-existing writer was not refused before SDK load/game callback.'}
        } else {
            $remote=if($case -in 'old-window-oracle','locked-testing','locked-player'){'true'}else{'false'}
            if($observed.childExitCode -ne 0 -or @($observed.guaNativeLoads).Count -ne 1 -or !$text.Contains('LEASE_GAME_REMOTE='+$remote) -or $text -match 'SCRIPT ERROR|ERROR:'){throw 'The required real game/wire result was not reached.'}
        }
        $results.Add(@{case=$case;nativeLoads=@($observed.guaNativeLoads).Count;denied=@($denied);observed=(Join-Path $project 'observed.json');log=$log})
    } finally {
        if($writer){$writer.Dispose()}
        # On an assertion failure release only the owned GAME's debugger barrier;
        # never leave it paused or terminate a user's Editor.
        if($process -and !$process.HasExited -and (Test-Path $ready) -and !(Test-Path $resume)) {
            Set-Content $resume 'controller-cleanup'
            $null=$process.WaitForExit(30000)
        }
    }
}
$results|ConvertTo-Json -Depth 10|Set-Content (Join-Path $out 'results.json') -Encoding utf8NoBOM
Write-Host "Godot native lease/race acceptance passed: $out"
$global:LASTEXITCODE=0
