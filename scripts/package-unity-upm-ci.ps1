param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$WebNativeDirectory,
    [switch]$DesktopOnly
)

$ErrorActionPreference = "Stop"
if (-not $DesktopOnly -and [string]::IsNullOrWhiteSpace($WebNativeDirectory)) { throw 'WebNativeDirectory is required unless DesktopOnly is selected.' }
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "examples/unity-smoke"
$plugins = Join-Path $project "Assets/Plugins/Gua"
$artifact = Join-Path $OutputDirectory "com.link1345.gua"

if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Recurse -Force }
New-Item -ItemType Directory -Force `
    (Join-Path $artifact "Runtime/Plugins/Managed"), `
    (Join-Path $artifact "Runtime/Plugins/Windows/x86_64"), `
    (Join-Path $artifact "Runtime/Plugins/Linux/x86_64"), `
    (Join-Path $artifact "Runtime/Plugins/macOS"), `
    (Join-Path $artifact "Runtime/Plugins/WebGL"), `
    (Join-Path $artifact "Runtime/Plugins/WebGL/Managed"), `
    (Join-Path $artifact "Editor"), `
    (Join-Path $artifact "Documentation~") | Out-Null

$package = Get-Content -LiteralPath (Join-Path $root "bindings/unity/package.json") -Raw | ConvertFrom-Json
$package.version = $Version
$package | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifact "package.json") -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot "copy-unity-package-notices.ps1") -AssetsFile (Join-Path $root "bindings/dotnet/src/Gua.Runtime/obj/project.assets.json") -OutputDirectory $artifact
Copy-Item (Join-Path $root "bindings/unity/Documentation~/index.md") (Join-Path $artifact "Documentation~")
Copy-Item (Join-Path $root "bindings/unity/Samples~") $artifact -Recurse
Copy-Item (Join-Path $root "bindings/unity/Runtime/link.xml") (Join-Path $artifact "Runtime")
Copy-Item (Join-Path $plugins "Managed/*.dll") (Join-Path $artifact "Runtime/Plugins/Managed")
Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Core.dll.meta") (Join-Path $artifact "Runtime/Plugins/Managed")
Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Runtime.dll.meta") (Join-Path $artifact "Runtime/Plugins/Managed")
if (-not $DesktopOnly) {
    Copy-Item (Join-Path $plugins "WebGL/Managed/Gua.Core.dll") (Join-Path $artifact "Runtime/Plugins/WebGL/Managed")
    Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Core.WebGL.dll.meta") (Join-Path $artifact "Runtime/Plugins/WebGL/Managed/Gua.Core.dll.meta")
    Copy-Item (Join-Path $plugins "WebGL/Managed/Gua.Runtime.dll") (Join-Path $artifact "Runtime/Plugins/WebGL/Managed")
    Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Runtime.WebGL.dll.meta") (Join-Path $artifact "Runtime/Plugins/WebGL/Managed/Gua.Runtime.dll.meta")
}
Copy-Item (Join-Path $plugins "Windows/x86_64/*") (Join-Path $artifact "Runtime/Plugins/Windows/x86_64")
Copy-Item (Join-Path $plugins "Linux/x86_64/*") (Join-Path $artifact "Runtime/Plugins/Linux/x86_64")
Copy-Item (Join-Path $plugins "macOS/*") (Join-Path $artifact "Runtime/Plugins/macOS")
if (-not $DesktopOnly) {
    Copy-Item (Join-Path $root "bindings/unity/Runtime/Plugins/WebGL/GuaWebMcp.jslib") (Join-Path $artifact "Runtime/Plugins/WebGL")
    Copy-Item (Join-Path $root "bindings/unity/Runtime/Plugins/WebGL/GuaWebMcp.jslib.meta") (Join-Path $artifact "Runtime/Plugins/WebGL")
    foreach ($webLibrary in "libgua_runtime.a", "libgua-core.a") {
        $webLibraries = @(Get-ChildItem -LiteralPath $WebNativeDirectory -Recurse -File -Filter $webLibrary)
        if ($webLibraries.Count -ne 1) { throw "Expected one $webLibrary below '$WebNativeDirectory', found $($webLibraries.Count)." }
        Copy-Item -LiteralPath $webLibraries[0].FullName -Destination (Join-Path $artifact "Runtime/Plugins/WebGL")
        Copy-Item (Join-Path $root "scripts/unity-meta/$webLibrary.meta") (Join-Path $artifact "Runtime/Plugins/WebGL")
    }
}

$scriptAssemblies = Join-Path $project "Library/ScriptAssemblies"
foreach ($assembly in "Gua.Unity.dll", "Gua.Unity.Bootstrap.dll", "Gua.Unity.TMP.dll") {
    $source = Join-Path $scriptAssemblies $assembly
    if (-not (Test-Path -LiteralPath $source)) { throw "Unity script assembly is missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $artifact "Runtime/Plugins/Managed")
    Copy-Item (Join-Path $root "scripts/unity-meta/$assembly.meta") (Join-Path $artifact "Runtime/Plugins/Managed")
}
$editorAssembly = Join-Path $scriptAssemblies "Gua.Unity.Editor.dll"
if (-not (Test-Path -LiteralPath $editorAssembly)) { throw "Unity editor assembly is missing: $editorAssembly" }
Copy-Item -LiteralPath $editorAssembly -Destination (Join-Path $artifact "Editor")
Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Unity.Editor.dll.meta") (Join-Path $artifact "Editor")

$staging = Join-Path $OutputDirectory "tgz-staging"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item -LiteralPath $artifact -Destination (Join-Path $staging "package") -Recurse
$archive = Join-Path $OutputDirectory "com.link1345.gua-$Version.tgz"
tar -czf $archive -C $staging package
if ($LASTEXITCODE -ne 0) { throw "Failed to create Unity UPM archive $archive" }
Remove-Item -LiteralPath $staging -Recurse -Force

Write-Host "Unity UPM release archive: $archive"
