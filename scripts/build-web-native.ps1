param(
    [string]$BuildDirectory = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$RuntimeOnly,
    [string]$Version = "0.0.0",
    [string]$BuildId = "development",
    [string]$Generator = "",
    [ValidateSet('shipping','development','qa','ai-player')][string]$Flavor = 'development',
    [ValidateSet('on','off')][string]$Remote = 'on'
)

$ErrorActionPreference = "Stop"
if ($Flavor -ne 'development' -and $Configuration -ne 'Release') { throw 'Distribution Web variants require Release.' }
if (($Flavor -eq 'shipping' -and $Remote -ne 'off') -or ($Flavor -in 'qa','ai-player' -and $Remote -ne 'on')) { throw 'Invalid Web variant opt-in.' }
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = "build/web-$($Configuration.ToLowerInvariant())"
}
$build = Join-Path $root $BuildDirectory
$emcmake = Get-Command emcmake.bat -ErrorAction SilentlyContinue
if ($null -eq $emcmake) { $emcmake = Get-Command emcmake -ErrorAction Stop }

$makeProgram = $null
if ([string]::IsNullOrWhiteSpace($Generator)) {
    if ($null -ne (Get-Command ninja -ErrorAction SilentlyContinue)) {
        $Generator = "Ninja"
    } elseif ($null -ne (Get-Command nmake -ErrorAction SilentlyContinue)) {
        $Generator = "NMake Makefiles"
    } else {
        $makeProgram = Get-ChildItem "${env:ProgramFiles}\Microsoft Visual Studio" -Recurse -Filter nmake.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1
        if ($null -eq $makeProgram) { throw "Neither Ninja nor nmake was found for the Web native build." }
        $Generator = "NMake Makefiles"
    }
}

$configureArguments = @(
    "-S", $root, "-B", $build, "-G", $Generator,
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DGUA_BUILD_EXAMPLES=OFF",
    "-DGUA_DISTRIBUTION_FLAVOR=$Flavor",
    "-DGUA_ENABLE_REMOTE_AUTOMATION=$(if ($Remote -eq 'on') {'ON'} else {'OFF'})",
    "-DGUA_STAGE_IN_SAMPLE=$(if ($Flavor -eq 'development') {'ON'} else {'OFF'})",
    "-DGUA_BUILD_GODOT=$(if ($RuntimeOnly) { 'OFF' } else { 'ON' })",
    "-DGUA_GODOT_CONFIGURATION=$Configuration",
    "-DGUA_VERSION=$Version",
    "-DGUA_GODOT_PLUGIN_VERSION=$Version",
    "-DGUA_BUILD_ID=$BuildId"
)
if ($null -ne $makeProgram) { $configureArguments += "-DCMAKE_MAKE_PROGRAM=$($makeProgram.FullName)" }

& $emcmake.Source cmake @configureArguments
if ($LASTEXITCODE -ne 0) { throw "Failed to configure the Gua Web native build." }

if (-not $RuntimeOnly) {
    $expectedGodotTarget = "template_$($Configuration.ToLowerInvariant())"
    $cmakeCache = Get-Content -LiteralPath (Join-Path $build "CMakeCache.txt") -Raw
    if ($cmakeCache -notmatch "(?m)^GODOTCPP_TARGET:STRING=$([regex]::Escape($expectedGodotTarget))\r?$") {
        throw "godot-cpp target does not match $Configuration (expected $expectedGodotTarget)."
    }
    if ($cmakeCache -notmatch "(?m)^GODOTCPP_THREADS:BOOL=OFF\r?$") {
        throw "godot-cpp threading must be OFF for the single-threaded Godot Web extension."
    }
}

$targets = @("gua-runtime", "gua-web-build-info")
if (-not $RuntimeOnly) { $targets += "gua-godot" }
cmake --build $build --target @targets
if ($LASTEXITCODE -ne 0) { throw "Failed to build the Gua Web native targets." }

$identity = (& node (Join-Path $build 'native/gua-runtime/gua-web-build-info.js')) | ConvertFrom-Json
if ($LASTEXITCODE -or $identity.flavor -ne $Flavor -or $identity.remoteEnabled -ne ($Remote -eq 'on')) { throw 'Actual linked Web build identity mismatch.' }
$stage = Join-Path $build 'stage/unity-web'
New-Item -ItemType Directory -Force $stage | Out-Null
$files = @()
foreach ($entry in @(@{dir='gua-core';name='libgua-core.a'},@{dir='gua-runtime';name='libgua_runtime.a'})) {
    $sourceFile = Join-Path $build "native/$($entry.dir)/$($entry.name)"
    Copy-Item -LiteralPath $sourceFile -Destination $stage -Force
    $files += @{path=$entry.name;sha256=(Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()}
}
@{schemaVersion=1;build=$identity;files=$files} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $stage 'gua-build-manifest.json') -Encoding utf8NoBOM

Write-Host "Unity WebGL runtime: $(Join-Path $build 'native/gua-runtime/libgua_runtime.a')"
Write-Host "Unity WebGL core: $(Join-Path $build 'native/gua-core/libgua-core.a')"
if (-not $RuntimeOnly) {
    $godotConfiguration = $Configuration.ToLowerInvariant()
    $addon = if ($Flavor -eq 'development') { Join-Path $root 'examples/godot-gdscript/addons/gua' } else { Join-Path $build 'stage/addons/gua' }
    $godotExtension = Join-Path $addon "bin/gua_godot.web.$godotConfiguration.wasm32.wasm"
    if (-not (Test-Path -LiteralPath $godotExtension -PathType Leaf)) {
        throw "Godot Web extension was not produced at the configuration-specific path: $godotExtension"
    }
    Write-Host "Godot Web extension: $godotExtension"
}
