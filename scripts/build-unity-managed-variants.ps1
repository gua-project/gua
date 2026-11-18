param(
    [Parameter(Mandatory)][string]$ReferencesDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$references=[IO.Path]::GetFullPath($ReferencesDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $output) {throw 'Produce managed variants in a new directory.'}
$editorVersion=(Get-Content (Join-Path $references 'unity-version.txt') -Raw).Trim()
if($editorVersion -ne '6000.5.3f1') {throw 'The managed producer is validated against the existing pinned Unity6000.5.3f1 only.'}
dotnet build (Join-Path $root 'tools/Gua.ArtifactProbe') -c Release --nologo -v quiet -m:1 -nr:false
if($LASTEXITCODE) {throw 'Cannot build managed IL inspector.'}
$probe=Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
$results=@()
$compileRoot=Join-Path $root ('build/unity-managed-compilation-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
foreach($mode in 'off','testing','player') {
    $destination=Join-Path $output $mode
    $compile=Join-Path $compileRoot $mode
    $remote=if($mode -eq 'off') {'false'} else {'true'}
    $player=if($mode -eq 'player') {'true'} else {'false'}
    $log=Join-Path $root ('build/unity-managed-producer-'+$mode+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'.log')
    dotnet build (Join-Path $root 'tools/Gua.UnityVariantCompile') -c Release --nologo -v quiet -m:1 -nr:false "-p:UnityManagedDirectory=$(Join-Path $references 'managed')" "-p:UnityScriptAssemblies=$(Join-Path $references 'script-assemblies')" "-p:GuaEnableRemoteAutomation=$remote" "-p:GuaPlayerOnly=$player" "-p:Version=$Version" -o $compile *> $log
    if($LASTEXITCODE) {throw "Managed variant compilation failed: $log"}
    New-Item -ItemType Directory -Force $destination | Out-Null
    foreach($name in 'Gua.Core.dll','Gua.Runtime.dll','Gua.Unity.dll') {Copy-Item (Join-Path $compile $name) -Destination $destination}
    $identity=(& dotnet $probe managed (Join-Path $destination 'Gua.Unity.dll')) | ConvertFrom-Json
    if($LASTEXITCODE -or $identity.RemoteEnabled -ne ($mode -ne 'off') -or $identity.PlayerOnly -ne ($mode -eq 'player') -or $identity.remoteStartReference -ne ($mode -ne 'off')) {throw 'Produced managed IL does not match its explicit mode.'}
    $results+=@{mode=$mode;compiled=$identity;files=@(foreach($name in 'Gua.Core.dll','Gua.Runtime.dll','Gua.Unity.dll') {@{name=$name;sha256=(Get-FileHash (Join-Path $destination $name)).Hash.ToLowerInvariant()}})}
}
$results | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'managed-variants.json') -Encoding utf8NoBOM
Write-Host "Provider managed variants ($editorVersion): $output"
$global:LASTEXITCODE=0
