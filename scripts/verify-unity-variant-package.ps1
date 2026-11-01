param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote,
    [switch]$DesktopOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'tools/Gua.ArtifactProbe/Gua.ArtifactProbe.csproj') -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Cannot build Unity IL probe.' }
$probe = Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
$compiled = (& dotnet $probe managed (Join-Path $Directory 'Runtime/Plugins/Managed/Gua.Unity.dll')) | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Cannot inspect Unity IL.' }
$enabled = $Remote -eq 'on'; $ceiling = if (-not $enabled) {'none'} elseif ($Flavor -eq 'ai-player') {'player'} else {'debug'}
if (($Flavor -eq 'shipping' -and $enabled) -or ($Flavor -in 'qa','ai-player' -and -not $enabled) -or
    $compiled.RemoteEnabled -ne $enabled -or $compiled.PlayerOnly -ne ($ceiling -eq 'player') -or
    $compiled.remoteCommandHandler -ne $enabled -or $compiled.remoteStartReference -ne $enabled -or $compiled.webInstallImport -ne $enabled) { throw 'Unity IL has a different compiled capability.' }
$layouts = @(@{path='Windows/x86_64';names=@('gua.dll','gua_runtime.dll')},@{path='Linux/x86_64';names=@('libgua.so','libgua_runtime.so')},@{path='macOS';names=@('libgua.dylib','libgua_runtime.dylib')})
if (-not $DesktopOnly) { $layouts += @{path='WebGL';names=@('libgua-core.a','libgua_runtime.a')} }
$source = $null; $build = $null
foreach ($layout in $layouts) {
    $native = Join-Path $Directory "Runtime/Plugins/$($layout.path)"
    $manifest = Get-Content -LiteralPath (Join-Path $native 'gua-build-manifest.json') -Raw | ConvertFrom-Json
    $info = $manifest.build
    if ($manifest.schemaVersion -ne 1 -or $info.flavor -ne $Flavor -or $info.remoteEnabled -ne $enabled -or
        $info.remotePermissionCeiling -ne $ceiling -or $info.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]::IsNullOrWhiteSpace($info.buildId) -or ($Flavor -ne 'development' -and $info.configuration -ne 'Release')) { throw "Wrong Unity native variant: $($layout.path)" }
    if ($source -and ($info.sourceCommit -ne $source -or $info.buildId -ne $build)) { throw 'Unity platform assets came from different builds.' }
    $source=$info.sourceCommit; $build=$info.buildId
    foreach ($name in $layout.names) {
        $entry=@($manifest.files | Where-Object path -CEQ $name)
        if ($entry.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $native $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry[0].sha256) { throw "Unity native hash mismatch: $($layout.path)/$name" }
    }
}
$js = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter '*.jslib')
if (($DesktopOnly -or -not $enabled) -and $js.Count -ne 0) { throw 'This Unity package cannot include remote WebMCP JavaScript.' }
if (-not $DesktopOnly -and $enabled -and ($js.Count -ne 1 -or $js[0].Name -ne 'GuaWebMcp.jslib')) { throw 'Missing or duplicate Unity WebMCP JavaScript.' }
Write-Host "Verified Unity IL, native identities and hashes: $Flavor / $Remote"
