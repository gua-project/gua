param(
    [Parameter(Mandatory)][string]$ShippingPackage,
    [Parameter(Mandatory)][string]$TestingPackage,
    [Parameter(Mandatory)][string]$PlayerPackage,
    [Parameter(Mandatory)][string]$ManagedOff,
    [Parameter(Mandatory)][string]$ManagedTesting,
    [Parameter(Mandatory)][string]$ManagedPlayer,
    [Parameter(Mandatory)][string]$PlayerTemplate,
    [Parameter(Mandatory)][string]$EditorTemplate,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $output) {throw 'Stage into a new Unity SDK directory.'}
New-Item -ItemType Directory -Force (Join-Path $output 'Provider~'),(Join-Path $output 'Editor'),(Join-Path $output 'Runtime/Plugins/Managed') | Out-Null
$source=$null
$cases=@(@{Mode='off';Flavor='shipping';Remote='off';Package=$ShippingPackage;Managed=$ManagedOff;Flags=@('!GUA_REMOTE_AUTOMATION')},@{Mode='testing';Flavor='qa';Remote='on';Package=$TestingPackage;Managed=$ManagedTesting;Flags=@('GUA_REMOTE_AUTOMATION','!GUA_PLAYER_ONLY')},@{Mode='player';Flavor='ai-player';Remote='on';Package=$PlayerPackage;Managed=$ManagedPlayer;Flags=@('GUA_REMOTE_AUTOMATION','GUA_PLAYER_ONLY')})
foreach($case in $cases) {
    & (Join-Path $PSScriptRoot 'verify-remote-build.ps1') -Directory $case.Package -Flavor $case.Flavor -Remote $case.Remote
    $manifest=Get-Content (Join-Path $case.Package 'gua-build-manifest.json') -Raw | ConvertFrom-Json
    $source ??= $manifest.build.sourceCommit
    if($source -ne $manifest.build.sourceCommit) {throw 'Provider Unity modes have different source identities.'}
    $native=Join-Path $output "Provider~/windows/$($case.Mode)"
    $managed=Join-Path $output "Runtime/Plugins/Managed/$($case.Mode)"
    New-Item -ItemType Directory -Force $native,$managed | Out-Null
    foreach($name in 'gua.dll','gua_runtime.dll') {
        Copy-Item -LiteralPath (Join-Path $case.Package $name) -Destination $native
    }
    $manifest.files=@($manifest.files | Where-Object {$_.path -in 'gua.dll','gua_runtime.dll'})
    $manifest | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $native 'gua-build-manifest.json') -Encoding utf8NoBOM
    Copy-Item (Join-Path $case.Managed 'Gua.Unity.dll') -Destination $managed
    @"
fileFormatVersion: 2
guid: $([guid]::NewGuid().ToString('N'))
PluginImporter:
  serializedVersion: 2
  defineConstraints:
$($case.Flags | ForEach-Object {"  - '$_'"} | Join-String -Separator "`n")
  isPreloaded: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {}
"@ | Set-Content (Join-Path $managed 'Gua.Unity.dll.meta') -Encoding utf8NoBOM
}
# Common prebuilt managed dependency closure. Only the adapter has mode-specific
# compilation. A mismatch must be resolved by the provider, never by consumers.
foreach($file in @('Gua.Core.dll','Gua.Runtime.dll') | ForEach-Object {Get-Item -LiteralPath (Join-Path $ManagedOff $_)}) {
    foreach($other in $ManagedTesting,$ManagedPlayer) {
        if((Get-FileHash (Join-Path $other $file.Name)).Hash -ne (Get-FileHash $file.FullName).Hash) {throw "Mode-specific common managed dependency: $($file.Name)"}
    }
    Copy-Item $file.FullName -Destination (Join-Path $output 'Runtime/Plugins/Managed')
}
& (Join-Path $PSScriptRoot 'copy-unity-managed-closure.ps1') -AssetsFile (Join-Path $root 'bindings/dotnet/src/Gua.Runtime/obj/project.assets.json') -TargetFramework netstandard2.1 -Destination (Join-Path $output 'Runtime/Plugins/Managed')
foreach($adapter in 'Bootstrap','TMP') {
    $destination=Join-Path $output "Runtime/$adapter"
    New-Item -ItemType Directory $destination | Out-Null
    foreach($file in Get-ChildItem (Join-Path $root "bindings/unity/Runtime/$adapter") -Filter '*.cs' -File) {Copy-Item $file.FullName -Destination $destination}
    $references=if($adapter -eq 'TMP') {'["Unity.TextMeshPro"]'} else {'[]'}
    "{`"name`":`"Gua.Unity.$adapter`",`"references`":$references,`"autoReferenced`":true}" | Set-Content (Join-Path $destination "Gua.Unity.$adapter.asmdef") -Encoding utf8NoBOM
}
Copy-Item $PlayerTemplate -Destination (Join-Path $output 'Provider~/GamePolicyTemplate.dll')
Copy-Item $EditorTemplate -Destination (Join-Path $output 'Provider~/EditorPolicyTemplate.dll')
foreach($name in 'GameBootstrap.cs.txt','GameIntegrityEditor.cs.txt') {Copy-Item (Join-Path $root "bindings/unity/game-build/$name") -Destination (Join-Path $output 'Provider~')}
$playerMeta=Get-Content (Join-Path $PSScriptRoot 'unity-meta/gua.dll.meta') -Raw
$playerMeta=[regex]::Replace($playerMeta,'(?m)^guid:.*$','guid: /*GUID*/')
$playerMeta=[regex]::Replace($playerMeta,'(?ms)(Editor: Editor\r?\n\s+second:\r?\n\s+enabled:) 1','$1 0')
$playerMeta.Replace('isPreloaded: 0','isPreloaded: 1') | Set-Content (Join-Path $output 'Provider~/player-policy.meta.txt') -Encoding utf8NoBOM
Copy-Item (Join-Path $root 'bindings/game-build/NativePolicyWriter.cs') -Destination (Join-Path $output 'Editor')
Copy-Item (Join-Path $root 'bindings/unity/game-build/GuaGameBuildSetup.cs') -Destination (Join-Path $output 'Editor')
Copy-Item (Join-Path $root 'bindings/unity/Editor/GuaUnityEditorCommands.cs') -Destination (Join-Path $output 'Editor')
'{"name":"Gua.GameBuild.Editor","includePlatforms":["Editor"],"references":["Unity.TextMeshPro"],"autoReferenced":true}' | Set-Content (Join-Path $output 'Editor/Gua.GameBuild.Editor.asmdef') -Encoding utf8NoBOM
$package=Get-Content (Join-Path $root 'bindings/unity/package.json') -Raw | ConvertFrom-Json
$package.displayName='Gua game-build SDK (Windows Mono validation)'
$package.description='Provider-built modes selected by game compile flags, with game-owned native integrity verification. Windows Mono validation SDK; unsupported targets fail closed.'
$package.PSObject.Properties.Remove('samples')
$package | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'package.json') -Encoding utf8NoBOM
Copy-Item (Join-Path $root 'LICENSE') -Destination $output
Write-Host "Staged provider Unity game SDK: $output"
