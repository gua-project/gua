param(
    [Parameter(Mandatory)][string]$MatrixDirectory,
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$matrix=(Resolve-Path $MatrixDirectory).Path
$engine=(Resolve-Path $GodotExecutable).Path
$sdk=(Resolve-Path $BundleRoot).Path
if(Test-Path $OutputDirectory){throw 'Use a new owned Editor acceptance directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
$old=$env:GUA_EDITOR_FIXTURE_ACTION
$results=[Collections.Generic.List[object]]::new()
try {
    foreach($case in 'rebuild','refuse') {
        $project=Join-Path $out $case;Copy-Item (Join-Path $matrix 'off') $project -Recurse
        dotnet new sln --name GuardedGodotGame --format sln --output $project *> (Join-Path $out "$case-solution.log")
        if($LASTEXITCODE){throw 'Cannot create the actual Godot solution.'}
        dotnet sln (Join-Path $project 'GuardedGodotGame.sln') add (Join-Path $project 'GuardedGodotGame.csproj') >> (Join-Path $out "$case-solution.log")
        if($LASTEXITCODE){throw 'Cannot add the game project.'}
        $plugin=(New-Item -ItemType Directory (Join-Path $project 'addons/owned_driver') -Force).FullName
        Copy-Item (Join-Path $root 'tests/godot-dotnet-game-policy/editor-driver.gd') $plugin
        @'
[plugin]
name="Owned Gua Editor fixture"
description="Normal build/reload/quit; never terminates an Editor."
author="Gua test"
version="1"
script="editor-driver.gd"
'@|Set-Content (Join-Path $plugin 'plugin.cfg')
        Add-Content (Join-Path $project 'project.godot') "`n[editor_plugins]`nenabled=PackedStringArray(`"res://addons/owned_driver/plugin.cfg`")"
        if($case -eq 'refuse') {
            foreach($file in 'gua.dll','gua_runtime.dll'){Copy-Item (Join-Path $sdk "testing/$file") (Join-Path $project '.godot/mono/temp/bin/Debug') -Force}
        }
        $env:GUA_EDITOR_FIXTURE_ACTION=$case
        $stdout=Join-Path $out "$case.stdout";$stderr=Join-Path $out "$case.stderr"
        $editor=Start-Process $engine -ArgumentList @('--headless','--editor','--language','en','--path',('"'+$project+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $deadline=[DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 200
            $errorText=Get-Content $stderr -Raw
        } while($case -eq 'refuse' -and !$editor.HasExited -and $errorText -notmatch 'Gua Godot game integrity refused' -and [DateTime]::UtcNow -lt $deadline)
        if($case -eq 'refuse') {
            if($editor.HasExited -or $errorText -notmatch 'stage=check-gua.dll'){throw 'Specific refusal and surviving Editor were not both observed.'}
            $native=@($editor.Modules|Where-Object ModuleName -In 'gua.dll','gua_runtime.dll')
            if($native.Count -ne 0){throw 'A refused Editor loaded the exchanged Gua native pair.'}
        }
        # Never Kill/Stop-Process an Editor, including on timeout or assertion.
        if(!$editor.WaitForExit(65000)){throw "Owned Editor still alive PID=$($editor.Id); leave it running and investigate."}
        $outputText=Get-Content $stdout -Raw;$errorText=Get-Content $stderr -Raw
        if($editor.ExitCode -ne 0 -or $outputText -notmatch 'GUA_EDITOR_NORMAL_QUIT'){throw 'Editor did not exit normally through its fixture plugin.'}
        if($case -eq 'rebuild' -and (($outputText -split 'GUA_DOTNET_EDITOR_READY').Length -lt 3 -or $outputText -notmatch 'Assembly load context unloaded successfully' -or $errorText -match '(?m)^ERROR:|Gua Godot game integrity refused')){throw 'Normal Editor rebuild/reload was not proven.'}
        $results.Add(@{case=$case;normalExit=$true;stderr=$stderr;stdout=$stdout})
    }
    $results|ConvertTo-Json|Set-Content (Join-Path $out 'results.json')
} finally {$env:GUA_EDITOR_FIXTURE_ACTION=$old}
$global:LASTEXITCODE=0
