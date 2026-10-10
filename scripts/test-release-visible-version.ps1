$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([System.IO.Path]::GetTempPath()) ('gua-version-' + [guid]::NewGuid())
$configRelative = 'packages/inspector/src-tauri/tauri.conf.json'
$configPath = Join-Path $fixture $configRelative
New-Item -ItemType Directory -Force (Split-Path -Parent $configPath) | Out-Null
Copy-Item (Join-Path $root $configRelative) $configPath
$original = Get-Content (Join-Path $root 'examples/godot-gdscript/addons/gua/plugin.cfg') -Raw
foreach ($version in '1.1.1', '2.34.56') {
    $env:GITHUB_OUTPUT = Join-Path $fixture 'tag-output'
    & (Join-Path $root '.github/scripts/release-version-from-tag.ps1') -Tag "gua-v$version"
    $resolved = (Get-Content $env:GITHUB_OUTPUT | Where-Object { $_ -like 'version=*' } | Select-Object -Last 1).Substring(8)
    & (Join-Path $PSScriptRoot 'set-inspector-release-version.ps1') -Version $resolved -RepositoryRoot $fixture
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($config.version -ne $version -or $config.productName -ne 'Gua Inspector' -or $config.identifier -ne 'dev.gua.inspector') { throw 'Inspector metadata mismatch.' }
    $addon = Join-Path $fixture "addon-$version"
    & (Join-Path $PSScriptRoot 'stage-godot-addon-sources.ps1') -AddonDirectory $addon -Version $resolved
    $plugin = Get-Content (Join-Path $addon 'plugin.cfg') -Raw
    if ($plugin -notmatch "(?m)^version=`"$([regex]::Escape($version))`"`r?$" -or $plugin -notmatch 'script="plugin.gd"') { throw 'Godot metadata mismatch.' }
    Compress-Archive -Path $addon -DestinationPath (Join-Path $fixture "$version.zip")
    Expand-Archive -Path (Join-Path $fixture "$version.zip") -DestinationPath (Join-Path $fixture "expanded-$version")
    if ((Get-Content (Join-Path $fixture "expanded-$version/addon-$version/plugin.cfg") -Raw) -ne $plugin) { throw 'Packaged plugin metadata mismatch.' }
}
$local = Join-Path $fixture 'local-addon'
& (Join-Path $PSScriptRoot 'stage-godot-addon-sources.ps1') -AddonDirectory $local
if ((Get-Content (Join-Path $local 'plugin.cfg') -Raw) -ne $original) { throw 'Local addon defaults changed.' }
foreach ($invalid in '1.2', '01.2.3', '1.2.3-invalid') {
    $before = Get-Content $configPath -Raw
    $failed = $false
    try { & (Join-Path $PSScriptRoot 'set-inspector-release-version.ps1') -Version $invalid -RepositoryRoot $fixture } catch { $failed = $true }
    if (-not $failed -or (Get-Content $configPath -Raw) -ne $before) { throw 'Invalid Inspector version was not rejected before writing.' }
    $failed = $false
    $invalidAddon = Join-Path $fixture 'invalid-addon'
    try { & (Join-Path $PSScriptRoot 'stage-godot-addon-sources.ps1') -AddonDirectory $invalidAddon -Version $invalid } catch { $failed = $true }
    if (-not $failed -or (Test-Path $invalidAddon)) { throw 'Invalid Godot version was not rejected before staging.' }
}
$workflow = Get-Content (Join-Path $root '.github/workflows/gua-release.yml') -Raw
if ($workflow -notmatch 'set-inspector-release-version.ps1 -Version' -or @($workflow -split "`n" | Where-Object { $_ -match 'stage-godot-addon-sources.ps1' -and $_ -notmatch '-Version' }).Count -ne 0) { throw 'Release workflow omits visible version injection.' }
Write-Host "Release visible-version regression checks passed. Fixtures: $fixture"
