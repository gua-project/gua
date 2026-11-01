param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote,
    [string]$ReleaseTemplate,
    [switch]$Editor
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'verify-remote-build.ps1') -Directory $PackageDirectory -Flavor $Flavor -Remote $Remote
if ($Editor -and $Flavor -ne 'development') { throw 'Editor acceptance uses the Development variant.' }
if (-not $Editor -and -not (Test-Path -LiteralPath $ReleaseTemplate -PathType Leaf)) { throw 'An existing Godot Release template is required.' }
$fixture = Join-Path $root ('build/godot-variant-' + $Flavor + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Force $fixture | Out-Null
Copy-Item -LiteralPath (Join-Path $PackageDirectory 'addons') -Destination $fixture -Recurse
Copy-Item -LiteralPath (Join-Path $root 'native/gua-godot/tests/build_variant_fixture.gd') -Destination (Join-Path $fixture 'fixture.gd')
@'
[gd_scene load_steps=2 format=3]
[ext_resource type="Script" path="res://fixture.gd" id="1"]
[node name="BuildVariant" type="Node"]
script = ExtResource("1")
'@ | Set-Content -LiteralPath (Join-Path $fixture 'main.tscn') -Encoding utf8NoBOM
@'
config_version=5
[application]
config/name="GuaBuildVariantAcceptance"
run/main_scene="res://main.tscn"
[rendering]
renderer/rendering_method="gl_compatibility"
[debug]
file_logging/enable_file_logging=true
'@ | Set-Content -LiteralPath (Join-Path $fixture 'project.godot') -Encoding utf8NoBOM
@{flavor=$Flavor;remote=($Remote -eq 'on')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'expected.json') -Encoding utf8NoBOM
$previousAppData = $env:APPDATA
$previousProfile = $env:GUA_OBSERVATION_PROFILE
try {
    $env:APPDATA = Join-Path $fixture 'userdata'
    $env:GUA_OBSERVATION_PROFILE = ''
    New-Item -ItemType Directory -Force $env:APPDATA | Out-Null
    if ($Editor) {
        $executable = $GodotExecutable
        $arguments = @('--headless','--path',"`"$fixture`"")
    } else {
        $template = [IO.Path]::GetFullPath($ReleaseTemplate).Replace('\','/')
        @"
[preset.0]
name="Windows"
platform="Windows Desktop"
runnable=true
export_filter="all_resources"
include_filter="expected.json"
exclude_filter=""
export_path="output/fixture.exe"
script_export_mode=2
[preset.0.options]
custom_template/release="$template"
binary_format/embed_pck=true
texture_format/bptc=false
"@ | Set-Content -LiteralPath (Join-Path $fixture 'export_presets.cfg') -Encoding utf8NoBOM
        New-Item -ItemType Directory -Force (Join-Path $fixture 'output') | Out-Null
        $lines = @(& $GodotExecutable --headless --recovery-mode --path $fixture --export-release Windows 2>&1)
        $lines | Set-Content -LiteralPath (Join-Path $fixture 'export.log')
        if ($LASTEXITCODE) { throw 'Godot Release export failed.' }
        $executable = Join-Path $fixture 'output/fixture.exe'
        $arguments = @('--headless')
    }
    $arguments += @('--log-file',"`"$(Join-Path $fixture 'consumer.log')`"")
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $fixture 'consumer.stdout.log') -RedirectStandardError (Join-Path $fixture 'consumer.stderr.log')
    if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Godot consumer timed out.' }
    $results = @(Get-ChildItem -LiteralPath $env:APPDATA -Recurse -Filter gua-variant-result.json -File)
    if ($process.ExitCode -ne 0 -or $results.Count -ne 1) { Get-Content -LiteralPath (Join-Path $fixture 'consumer.log') -ErrorAction SilentlyContinue; throw 'Actual Godot consumer acceptance failed.' }
    $result = Get-Content -LiteralPath $results[0].FullName -Raw | ConvertFrom-Json
    if ($result.passed -ne $true -or $result.flavor -ne $Flavor -or $result.remote -ne ($Remote -eq 'on')) { throw 'Consumer result does not match the requested variant.' }
    Write-Host "Godot $(if ($Editor) {'Editor'} else {'Release EXE/PCK'}) acceptance passed: $fixture"
} finally {
    $env:APPDATA = $previousAppData
    $env:GUA_OBSERVATION_PROFILE = $previousProfile
}
