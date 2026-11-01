param(
    [Parameter(Mandatory)][ValidateSet('shipping','development','qa','ai-player')][string]$Flavor,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$WithGodot,
    [string]$GodotCppSource,
    [string]$GodotBuildProfile,
    [string]$PythonExecutable,
    [string]$BuildDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $root "build/remote-$Flavor-$Remote-$Configuration" }
$build = [IO.Path]::GetFullPath($BuildDirectory)
$remoteValue = if ($Remote -eq 'on') { 'ON' } else { 'OFF' }
$generator = & (Join-Path $PSScriptRoot 'get-msvc-generator.ps1')
$configure = @('-S',$root,'-B',$build,'-G',$generator,'-A','x64',
    "-DGUA_DISTRIBUTION_FLAVOR=$Flavor","-DGUA_ENABLE_REMOTE_AUTOMATION=$remoteValue",
    '-DGUA_STAGE_IN_SAMPLE=OFF','-DGUA_BUILD_EXAMPLES=OFF','-DBUILD_TESTING=ON',
    "-DGUA_BUILD_GODOT=$(if ($WithGodot) { 'ON' } else { 'OFF' })","-DGUA_GODOT_CONFIGURATION=$Configuration")
if ($GodotCppSource) { $configure += "-DFETCHCONTENT_SOURCE_DIR_GODOT-CPP=$GodotCppSource" }
if ($GodotBuildProfile) { $configure += "-DGODOTCPP_BUILD_PROFILE=$GodotBuildProfile" }
if ($PythonExecutable) { $configure += "-DPython3_EXECUTABLE=$PythonExecutable" }
$sourceCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE) { throw 'Cannot resolve source identity.' }
$configure += "-DGUA_BUILD_ID=$sourceCommit"
cmake @configure
if ($LASTEXITCODE) { throw 'Variant configure failed.' }
$targets = @('gua','gua-build-variant-tests')
if ($WithGodot) { $targets += 'gua-godot' }
cmake --build $build --config $Configuration --target @targets --parallel 1 -- /nr:false
if ($LASTEXITCODE) { throw 'Variant compilation failed.' }
$previousPath = $env:PATH
try {
    $env:PATH = (Join-Path $build "native/gua-runtime/$Configuration") + [IO.Path]::PathSeparator + $env:PATH
    ctest --test-dir $build -C $Configuration -R '^gua-build-variant-tests$' --output-on-failure
    if ($LASTEXITCODE) { throw 'Native variant acceptance failed.' }
} finally { $env:PATH = $previousPath }
& (Join-Path $PSScriptRoot 'verify-remote-build-graph.ps1') -BuildDirectory $build -Configuration $Configuration -Remote $Remote
$project = Join-Path $root 'bindings/dotnet/tests/Gua.BuildVariants/Gua.BuildVariants.csproj'
dotnet build $project -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Managed variant test build failed.' }
dotnet (Join-Path $root 'bindings/dotnet/tests/Gua.BuildVariants/bin/Release/net10.0/Gua.BuildVariants.dll') `
    (Join-Path $build "native/gua-core/$Configuration") (Join-Path $build "native/gua-runtime/$Configuration") $Flavor $Remote
if ($LASTEXITCODE) { throw 'Managed/real WebSocket acceptance failed.' }
$stageArgs = @{ BuildDirectory = $build; OutputDirectory = (Join-Path $build ('package-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))); Flavor = $Flavor; Remote = $Remote; Configuration = $Configuration;
    EngineBuildKind = $(if ($Configuration -eq 'Debug') { 'debug' } else { 'release' }) }
if ($WithGodot) { $stageArgs.GodotExtension = Join-Path $build "stage/addons/gua/bin/gua_godot.windows.$($Configuration.ToLowerInvariant()).x86_64.dll" }
& (Join-Path $PSScriptRoot 'stage-remote-build.ps1') @stageArgs
Write-Host "Completed $Flavor / remote $Remote / $Configuration acceptance."
