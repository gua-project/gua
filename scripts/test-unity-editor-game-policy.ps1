param(
    [Parameter(Mandatory)][string]$UnityExecutable,
    [Parameter(Mandatory)][string]$ExistingGameFixture,
    [Parameter(Mandatory)][string]$NativePackage,
    [Parameter(Mandatory)][string]$EditorTemplate,
    [Parameter(Mandatory)][string]$OfficialUnityPackage
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root ('build/unity-editor-policy-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory $output | Out-Null
$writer=Join-Path $root 'tools/Gua.GameBuild/bin/Release/net10.0/Gua.GameBuild.dll'
$results=[Collections.Generic.List[object]]::new()
$beforeNegative=$env:GUA_EDITOR_EXPECT_REFUSAL; $beforeProfile=$env:GUA_OBSERVATION_PROFILE
try {
    $env:GUA_OBSERVATION_PROFILE=$null
    foreach($case in 'positive','official-swap') {
        $fixture=Join-Path $output $case; New-Item -ItemType Directory $fixture | Out-Null
        foreach($folder in 'Assets','Packages','ProjectSettings') {Copy-Item -LiteralPath (Join-Path $ExistingGameFixture $folder) -Destination $fixture -Recurse}
        $gameEditor=Join-Path $fixture 'Assets/GameSecurity/Editor'
        $checker=Join-Path $gameEditor 'Native~/EditorSecurity_fixture.dll'
        New-Item -ItemType Directory -Force (Split-Path $checker) | Out-Null
        $native=Join-Path $fixture 'Assets/Plugins/Native'
        $managed=Join-Path $fixture 'Assets/Plugins/Managed'
        # ExistingPlayer fixture can have its OUTPUT swapped, but Assets contain
        # the original OFF inputs. Explicitly restore the selected native set.
        foreach($name in 'gua.dll','gua_runtime.dll','gua-build-manifest.json') {Copy-Item -LiteralPath (Join-Path $NativePackage $name) -Destination $native -Force}
        $identity=(Get-Content -LiteralPath (Join-Path $NativePackage 'gua-build-manifest.json') -Raw | ConvertFrom-Json).build
        $files=@(@{path='Assets/Plugins/Native/gua.dll';source=(Join-Path $native 'gua.dll');native=$true},@{path='Assets/Plugins/Native/gua_runtime.dll';source=(Join-Path $native 'gua_runtime.dll');native=$true})
        foreach($file in Get-ChildItem -LiteralPath $managed -Filter '*.dll' -File) {$files+=@{path="Assets/Plugins/Managed/$($file.Name)";source=$file.FullName;native=$false}}
        $policy=@{kind=3;mode=0;root=$fixture;sourceCommit=$identity.sourceCommit;buildId=$identity.buildId;files=$files}
        $policyFile=Join-Path $fixture 'editor-policy.json'
        $policy | ConvertTo-Json -Depth 10 | Set-Content $policyFile -Encoding utf8NoBOM
        & dotnet $writer embed-native $EditorTemplate $policyFile $checker
        if($LASTEXITCODE) {throw 'Cannot generate the nonfatal game-owned Editor checker.'}
        $source=Get-Content -LiteralPath (Join-Path $root 'bindings/unity/game-build/GameIntegrityEditor.cs.txt') -Raw
        $source.Replace('/*CHECKER_PATH*/',($checker | ConvertTo-Json -Compress)).Replace('/*MODE*/','0') | Set-Content (Join-Path $gameEditor 'GameIntegrityEditor.cs') -Encoding utf8NoBOM
        Copy-Item -LiteralPath (Join-Path $root 'bindings/unity/Tests/Fixtures/GameIntegrityEditorFixture.cs') -Destination $gameEditor
        if($case -eq 'official-swap') {
            foreach($file in Get-ChildItem -LiteralPath (Join-Path $OfficialUnityPackage 'Runtime/Plugins/Managed') -Filter '*.dll' -File) {Copy-Item $file.FullName -Destination $managed -Force}
            foreach($name in 'gua.dll','gua_runtime.dll') {Copy-Item -LiteralPath (Join-Path $OfficialUnityPackage "Runtime/Plugins/Windows/x86_64/$name") -Destination $native -Force}
        }
        $env:GUA_EDITOR_EXPECT_REFUSAL=if($case -eq 'positive') {'0'} else {'1'}
        $log=Join-Path $fixture 'editor.log'
        # An owned disposable batch Editor exits through its ordinary Editor API
        # after returning from Play. Timeout never kills it or any other Editor.
        $editor=Start-Process -FilePath $UnityExecutable -ArgumentList @('-batchmode','-nographics','-projectPath',"`"$fixture`"",'-executeMethod','GameIntegrityEditorFixture.Run','-logFile',"`"$log`"") -WindowStyle Hidden -PassThru
        $deadline=[DateTime]::UtcNow.AddMinutes(5)
        while(!$editor.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) {}
        if(!$editor.HasExited) {throw "Editor remains pending, PID$($editor.Id); no termination attempted. Log: $log"}
        $result=Join-Path $fixture 'editor-policy-result.txt'
        if($editor.ExitCode -ne 0 -or !(Test-Path $result) -or [string](Get-Content $result -Raw) -notmatch '^PASS:') {throw "Editor policy test failed: $log"}
        $results.Add(@{case=$case;result=(Get-Content $result -Raw);unityVersion='6000.5.3f1';evidence=$log})
    }
} finally {$env:GUA_EDITOR_EXPECT_REFUSAL=$beforeNegative;$env:GUA_OBSERVATION_PROFILE=$beforeProfile}
$results | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "Safe Editor policy evidence: $output"
$global:LASTEXITCODE=0
