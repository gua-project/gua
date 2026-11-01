param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('editor','debug','release')][string]$EngineBuildKind = 'release',
    [string]$GodotExtension,
    [string]$UnityAssembly,
    [string]$ManagedDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = [IO.Path]::GetFullPath($BuildDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count) { throw 'Variant staging must start in an empty directory.' }
if ($Flavor -ne 'development' -and ($Configuration -ne 'Release' -or $EngineBuildKind -ne 'release')) { throw 'Distribution variants require native/engine Release.' }
if (($Flavor -eq 'shipping' -and $Remote -ne 'off') -or ($Flavor -in 'qa','ai-player' -and $Remote -ne 'on')) { throw 'Inconsistent purpose and explicit opt-in.' }
$probeProject = Join-Path $root 'tools/Gua.ArtifactProbe/Gua.ArtifactProbe.csproj'
dotnet build $probeProject -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Cannot build artifact probe.' }
$probe = Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
function Read-NativeIdentity([string]$Path,[string]$Symbol) {
    $lines = @(& dotnet $probe native $Path $Symbol)
    if ($LASTEXITCODE) { throw "Cannot read native identity: $Path" }
    return ($lines -join "`n") | ConvertFrom-Json
}
function Assert-Identity($Identity) {
    $ceiling = if ($Remote -eq 'off') { 'none' } elseif ($Flavor -eq 'ai-player') { 'player' } else { 'debug' }
    if ($Identity.schemaVersion -ne 1 -or $Identity.flavor -ne $Flavor -or $Identity.configuration -ne $Configuration -or
        $Identity.remoteEnabled -ne ($Remote -eq 'on') -or $Identity.remotePermissionCeiling -ne $ceiling -or
        $Identity.sourceCommit -notmatch '^[0-9a-f]{40}$' -or [string]::IsNullOrWhiteSpace($Identity.buildId)) { throw 'Native variant identity does not match the requested package.' }
}
$coreName = if ($IsWindows) { 'gua.dll' } elseif ($IsMacOS) { 'libgua.dylib' } else { 'libgua.so' }
$runtimeName = if ($IsWindows) { 'gua_runtime.dll' } elseif ($IsMacOS) { 'libgua_runtime.dylib' } else { 'libgua_runtime.so' }
$nativeCore = Join-Path $build "native/gua-core/$(if ($IsWindows) { "$Configuration/" })$coreName"
$nativeRuntime = Join-Path $build "native/gua-runtime/$(if ($IsWindows) { "$Configuration/" })$runtimeName"
$identity = Read-NativeIdentity $nativeCore 'gua_copy_build_info_json'
Assert-Identity $identity
$runtimeIdentity = Read-NativeIdentity $nativeRuntime 'gua_runtime_copy_build_info_json'
Assert-Identity $runtimeIdentity
if (($identity | ConvertTo-Json -Compress -Depth 10) -ne ($runtimeIdentity | ConvertTo-Json -Compress -Depth 10)) { throw 'Core and runtime are from different builds.' }
if ($GodotExtension) {
    $godotIdentity = Read-NativeIdentity ([IO.Path]::GetFullPath($GodotExtension)) 'gua_godot_copy_build_info_json'
    Assert-Identity $godotIdentity
    if (($identity | ConvertTo-Json -Compress -Depth 10) -ne ($godotIdentity | ConvertTo-Json -Compress -Depth 10)) { throw 'Godot runtime build differs from core/runtime.' }
}
if ($UnityAssembly) {
    $managedIdentity = (& dotnet $probe managed $UnityAssembly) | ConvertFrom-Json
    if ($LASTEXITCODE -or $managedIdentity.RemoteEnabled -ne ($Remote -eq 'on') -or $managedIdentity.PlayerOnly -ne ($Flavor -eq 'ai-player') -or $managedIdentity.remoteCommandHandler -ne ($Remote -eq 'on')) { throw 'Unity managed IL has the wrong remote variant.' }
}
# All identity checks happen before any distribution file is written.
New-Item -ItemType Directory -Force $output | Out-Null
Copy-Item -LiteralPath $nativeCore,$nativeRuntime -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $output
if ($ManagedDirectory) {
    $managedTarget = Join-Path $output 'managed'
    New-Item -ItemType Directory $managedTarget | Out-Null
    foreach ($name in 'Gua.Core.dll','Gua.Runtime.dll') { Copy-Item -LiteralPath (Join-Path $ManagedDirectory $name) -Destination $managedTarget }
}
if ($UnityAssembly) {
    Copy-Item -LiteralPath $UnityAssembly -Destination $output
    if ($Remote -eq 'on') { Copy-Item -LiteralPath (Join-Path $root 'bindings/unity/Runtime/Plugins/WebGL/GuaWebMcp.jslib') -Destination $output }
}
if ($GodotExtension) {
    $addon = Join-Path $output 'addons/gua'
    New-Item -ItemType Directory -Force (Join-Path $addon 'bin') | Out-Null
    $source = Join-Path $root 'examples/godot-gdscript/addons/gua'
    foreach ($name in 'gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd','plugin.cfg','plugin.gd') {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $addon
    }
    if ($Remote -eq 'on') { Copy-Item -LiteralPath (Join-Path $source 'gua_webmcp_bridge.gd') -Destination $addon }
    else {
        $scriptPath = Join-Path $addon 'gua_auto_adapter.gd'
        $script = Get-Content -LiteralPath $scriptPath -Raw
        $pattern = '(?ms)^\tif _ensure_context\(\) and OS.has_feature\("web"\).*?\t\t\twebmcp_bridge.attach\(self\)\r?\n'
        if ([regex]::Matches($script,$pattern).Count -ne 1) { throw 'Cannot isolate the WebMCP bootstrap.' }
        [regex]::Replace($script,$pattern,'') | Set-Content -LiteralPath $scriptPath -Encoding utf8NoBOM -NoNewline
    }
    $name = [IO.Path]::GetFileName($GodotExtension)
    Copy-Item -LiteralPath $GodotExtension -Destination (Join-Path $addon 'bin')
    if ($name -notmatch '^gua_godot\.(windows|linux|macos)\.(debug|release)\.(x86_64|arm64)\.(dll|so|dylib)$') { throw 'Unexpected Godot extension identity.' }
    $platform = $Matches[1]; $arch = $Matches[3]
    $engineTag = if ($EngineBuildKind -eq 'release') { 'release' } else { 'debug' }
    @"
[configuration]
entry_symbol = "gua_godot_library_init"
compatibility_minimum = "4.7"
[libraries]
$platform.$engineTag.$arch = "res://addons/gua/bin/$name"
"@ | Set-Content -LiteralPath (Join-Path $addon 'gua.gdextension') -Encoding utf8NoBOM
}
$files = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ schemaVersion = 1; build = $identity; engineBuildKind = $EngineBuildKind; files = $files } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'gua-build-manifest.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'verify-remote-build.ps1') -Directory $output -Flavor $Flavor -Remote $Remote
