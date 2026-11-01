param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$build = [IO.Path]::GetFullPath($BuildDirectory)
$header = Get-Content -LiteralPath (Join-Path $build 'generated/gua/build_config.h') -Raw
$expected = if ($Remote -eq 'on') { 1 } else { 0 }
if ($header -notmatch "(?m)^#define GUA_COMPILED_REMOTE $expected\r?$") { throw 'Wrong generated compile gate.' }
$projects = @(Get-ChildItem -LiteralPath $build -Recurse -Filter '*.vcxproj')
if (-not $projects.Count) { throw 'This object inspection currently requires the Windows MSVC generator.' }
$bridge = @($projects | Where-Object Name -EQ 'gua-ws-bridge.vcxproj')
if (($bridge.Count -eq 1) -ne ($Remote -eq 'on')) { throw 'Wrong transport target/link graph (including stale targets).' }
$runtimeProjects = @($projects | Where-Object Name -In 'gua-runtime.vcxproj','gua-runtime-godot.vcxproj')
if (-not $runtimeProjects.Count) { throw 'Missing native runtime target.' }
foreach ($project in $runtimeProjects) {
    $text = Get-Content -LiteralPath $project.FullName -Raw
    if ($text -notmatch "GUA_RUNTIME_WITH_WS=$expected(?:;|&|<)") { throw "Wrong runtime compile gate: $($project.Name)" }
    if ($Remote -eq 'off' -and $text -match 'gua-ws-bridge') { throw 'Transport linked into Shipping runtime.' }
}
$compilerFile = Get-ChildItem -LiteralPath (Join-Path $build 'CMakeFiles') -Recurse -Filter CMakeCXXCompiler.cmake | Select-Object -First 1
$compilerInfo = Get-Content -LiteralPath $compilerFile.FullName -Raw
if ($compilerInfo -notmatch 'set\(CMAKE_CXX_COMPILER "([^"]+)"\)') { throw 'Cannot find the actual MSVC toolchain.' }
$dumpbin = Join-Path ([IO.Path]::GetDirectoryName($Matches[1])) 'dumpbin.exe'
$evidence = Join-Path $build 'remote-build-evidence'
New-Item -ItemType Directory -Force $evidence | Out-Null
foreach ($project in $runtimeProjects) {
    $targetName = [IO.Path]::GetFileNameWithoutExtension($project.Name)
    $object = Join-Path $project.DirectoryName "$targetName.dir/$Configuration/runtime.obj"
    if (-not (Test-Path -LiteralPath $object -PathType Leaf)) { throw "Missing compiled runtime object: $object" }
    $symbols = @(& $dumpbin /symbols $object)
    if ($LASTEXITCODE) { throw 'Object inspection failed.' }
    $symbols | Set-Content -LiteralPath (Join-Path $evidence "$targetName-symbols.txt")
    if ($Remote -eq 'off' -and ($symbols -match 'BridgeServer').Count) { throw 'Remote server reference present in disabled object.' }
    Copy-Item -LiteralPath $project.FullName -Destination $evidence -Force
}
Copy-Item -LiteralPath (Join-Path $build 'generated/gua/build_config.h') -Destination $evidence -Force
Write-Host "Verified compile/link graph and actual runtime objects: remote $Remote"
