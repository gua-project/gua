param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote
)
$ErrorActionPreference = 'Stop'
if (($Flavor -eq 'shipping' -and $Remote -ne 'off') -or ($Flavor -in 'qa','ai-player' -and $Remote -ne 'on')) { throw 'Invalid purpose/opt-in combination.' }
$directoryPath = [IO.Path]::GetFullPath($Directory)
$manifest = Get-Content -LiteralPath (Join-Path $directoryPath 'gua-build-manifest.json') -Raw | ConvertFrom-Json
$ceiling = if ($Remote -eq 'off') { 'none' } elseif ($Flavor -eq 'ai-player') { 'player' } else { 'debug' }
if ($manifest.schemaVersion -ne 1 -or $manifest.build.flavor -ne $Flavor -or $manifest.build.remoteEnabled -ne ($Remote -eq 'on') -or $manifest.build.remotePermissionCeiling -ne $ceiling) { throw 'Unexpected package variant.' }
if ($Flavor -ne 'development' -and ($manifest.build.configuration -ne 'Release' -or $manifest.engineBuildKind -ne 'release')) { throw 'Wrong distribution configuration.' }
if (@($manifest.files).Count -lt 3) { throw 'Incomplete package manifest.' }
$root = Split-Path -Parent $PSScriptRoot
$probe = Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Build Gua.ArtifactProbe before package verification.' }
$coreCount = 0; $runtimeCount = 0
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest.files) {
    if ([IO.Path]::IsPathRooted($file.path) -or $file.path -match '(^|[\\/])\.\.([\\/]|$)' -or -not $seen.Add($file.path)) { throw 'Invalid or duplicate manifest path.' }
    $path = [IO.Path]::GetFullPath((Join-Path $directoryPath $file.path))
    if (-not $path.StartsWith($directoryPath + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped package.' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Packaged bytes differ from the verified build: $($file.path)" }
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package files must not be links.' }
    $symbol = $null
    if ($file.path -match '^(gua\.dll|libgua\.(so|dylib))$') { $symbol = 'gua_copy_build_info_json'; $coreCount++ }
    if ($file.path -match '^(gua_runtime\.dll|libgua_runtime\.(so|dylib))$') { $symbol = 'gua_runtime_copy_build_info_json'; $runtimeCount++ }
    if ($file.path -match '^addons/gua/bin/gua_godot\.') { $symbol = 'gua_godot_copy_build_info_json' }
    if ($symbol) {
        $identity = (& dotnet $probe native $path $symbol) | ConvertFrom-Json
        if ($LASTEXITCODE -or ($identity | ConvertTo-Json -Compress -Depth 10) -ne ($manifest.build | ConvertTo-Json -Compress -Depth 10)) { throw 'Packaged binary identity differs from manifest.' }
    }
    if ($file.path -eq 'Gua.Unity.dll') {
        $identity = (& dotnet $probe managed $path) | ConvertFrom-Json
        if ($LASTEXITCODE -or $identity.RemoteEnabled -ne ($Remote -eq 'on') -or $identity.PlayerOnly -ne ($Flavor -eq 'ai-player') -or
            $identity.remoteCommandHandler -ne ($Remote -eq 'on') -or $identity.remoteStartReference -ne ($Remote -eq 'on') -or $identity.webInstallImport -ne ($Remote -eq 'on')) { throw 'Packaged Unity managed IL has the wrong remote variant.' }
    }
    if ($Remote -eq 'off' -and $file.path -match '(?i)(gua_webmcp_bridge\.gd|GuaWebMcp\.jslib|gua[-_]ws[-_]bridge)') { throw 'Remote implementation found in a disabled package.' }
}
if ($coreCount -ne 1 -or $runtimeCount -ne 1) { throw 'Package needs exactly one core/runtime native pair.' }
foreach ($file in Get-ChildItem -LiteralPath $directoryPath -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($directoryPath,$file.FullName).Replace('\','/')
    if ($relative -ne 'gua-build-manifest.json' -and -not $seen.Contains($relative)) { throw "Unmanifested package file: $relative" }
}
Write-Host "Verified $Flavor / remote $Remote package: $directoryPath"
