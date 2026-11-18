param(
    [Parameter(Mandatory)][string]$UnityExecutable,
    [Parameter(Mandatory)][string]$ExistingFixture
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('build/unity-source-layout-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Force (Join-Path $fixture 'Assets/Editor'),(Join-Path $fixture 'Assets/Plugins/Managed') | Out-Null
foreach($folder in 'Packages','ProjectSettings') {Copy-Item (Join-Path $ExistingFixture $folder) -Destination $fixture -Recurse}
Copy-Item (Join-Path $ExistingFixture 'Assets/Plugins/Native') -Destination (Join-Path $fixture 'Assets/Plugins') -Recurse
foreach($file in Get-ChildItem (Join-Path $ExistingFixture 'Assets/Plugins/Managed') -Filter '*.dll*' -File | Where-Object {$_.Name -notlike 'Gua.Unity*'}) {Copy-Item $file.FullName -Destination (Join-Path $fixture 'Assets/Plugins/Managed')}
$package=Join-Path $fixture 'Packages/com.link1345.gua'
Copy-Item -LiteralPath (Join-Path $root 'bindings/unity') -Destination $package -Recurse
$manifest=Join-Path $fixture 'Packages/manifest.json'
$data=Get-Content $manifest -Raw | ConvertFrom-Json
$data.dependencies | Add-Member -NotePropertyName 'com.link1345.gua' -NotePropertyValue 'file:com.link1345.gua' -Force
$data | ConvertTo-Json -Depth 10 | Set-Content $manifest -Encoding utf8NoBOM
@'
using System;
using System.IO;
using System.Linq;
using Gua.Runtime;
using Gua.Core;
using Gua.Unity;
public static class SourcePackageLayoutFixture {
    public static void Verify() {
        foreach(string name in new[]{"GameOwnedPolicyConsumer","GameIntegrityEditorFixture","Gua.Unity.Editor.GuaGameBuildSetup"})
            if(AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetType(name,false)!=null)) throw new Exception("Provider/test code was imported into the normal source package: "+name);
        if(GuaUnityCompiledBuild.IsRemoteEnabled()) throw new Exception("Unexpected source-package game flag.");
        using(var runtime=new GuaRuntime()) {
            runtime.BeginFrame("layout");
            runtime.RegisterNode(new GuaNodeDescriptor("local","button","Source SDK local API",new GuaBounds(0,0,10,10)));
            runtime.EndFrame();
            if(!runtime.GetUiTreeJson().Contains("Source SDK local API")) throw new Exception("Source SDK local regression.");
        }
        File.WriteAllText("source-layout-result.txt","PASS: normal source SDK imported; provider/test types absent; local API works.");
    }
}
'@ | Set-Content (Join-Path $fixture 'Assets/Editor/SourcePackageLayoutFixture.cs') -Encoding utf8NoBOM
function Invoke-LayoutEditor([string]$Log) {
    $editor=Start-Process -FilePath $UnityExecutable -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',"`"$fixture`"",'-executeMethod','SourcePackageLayoutFixture.Verify','-logFile',"`"$Log`"") -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddMinutes(4)
    while(!$editor.WaitForExit(1000) -and [DateTime]::UtcNow -lt $deadline) {}
    if(!$editor.HasExited) {throw "Editor pending PID$($editor.Id); no termination attempted. $Log"}
    return $editor.ExitCode
}
$positive=Join-Path $fixture 'positive.log'
if((Invoke-LayoutEditor $positive) -ne 0 -or !(Test-Path (Join-Path $fixture 'source-layout-result.txt'))) {throw "Normal source package layout failed: $positive"}
# Deliberately violate the assembly boundary in this disposable package.
# A bare package-root script is ignored by Unity when it has no asmdef. Put the
# provider source inside the actual Runtime asmdef so this mutation is imported.
Copy-Item (Join-Path $package 'game-build~/GuaGameBuildSetup.cs') -Destination (Join-Path $package 'Runtime/GuaGameBuildSetupRegression.cs')
$negative=Join-Path $fixture 'import-mutation.log'
if((Invoke-LayoutEditor $negative) -eq 0 -or [string](Get-Content $negative -Raw) -notmatch 'CS0234.*GameBuild|CS0103.*GameIntegrityEditor') {throw 'The source-layout check did not detect the accidental provider-source import.'}
@(@{case='source-package';result='normal-source-import-and-local-api-passed';evidence=$positive},@{case='provider-source-import-mutation';result='intended-compiler-boundary-violation-detected';evidence=$negative}) | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $fixture 'results.json') -Encoding utf8NoBOM
Write-Host "Unity source-layout evidence: $fixture"
$global:LASTEXITCODE=0
