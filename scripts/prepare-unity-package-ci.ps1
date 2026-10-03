param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [Alias("NativeDirectory")]
    [string]$NativeAssetsRoot,

    [string]$Configuration = "Release",
    [switch]$DesktopOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$plugins = Join-Path $root "examples/unity-smoke/Assets/Plugins/Gua"
$managed = Join-Path $plugins "Managed"
$windowsNative = Join-Path $plugins "Windows/x86_64"
$linuxNative = Join-Path $plugins "Linux/x86_64"
$macNative = Join-Path $plugins "macOS"
$webManaged = Join-Path $plugins "WebGL/Managed"
$webManagedBuild = Join-Path $root "artifacts/unity-web-managed"

dotnet restore (Join-Path $root "bindings/dotnet/src/Gua.Runtime/Gua.Runtime.csproj") -p:RestoreEnablePackagePruning=false --force-evaluate
if ($LASTEXITCODE -ne 0) { throw "Failed to restore the managed Unity package closure." }
dotnet build (Join-Path $root "bindings/dotnet/src/Gua.Core/Gua.Core.csproj") -c $Configuration -f netstandard2.1 --no-restore -p:Version=$Version -p:RestoreEnablePackagePruning=false
if ($LASTEXITCODE -ne 0) { throw "Failed to build Gua.Core for the Unity package." }
dotnet build (Join-Path $root "bindings/dotnet/src/Gua.Runtime/Gua.Runtime.csproj") -c $Configuration -f netstandard2.1 --no-restore -p:Version=$Version -p:RestoreEnablePackagePruning=false
if ($LASTEXITCODE -ne 0) { throw "Failed to build Gua.Runtime for the Unity package." }
if (-not $DesktopOnly) {
    dotnet build (Join-Path $root "bindings/dotnet/src/Gua.Core/Gua.Core.csproj") -c $Configuration -f netstandard2.1 --no-restore -p:Version=$Version -p:RestoreEnablePackagePruning=false -p:DefineConstants=GUA_STATIC_LINK -o $webManagedBuild
    if ($LASTEXITCODE -ne 0) { throw "Failed to build the WebGL Gua.Core assembly." }
    dotnet build (Join-Path $root "bindings/dotnet/src/Gua.Runtime/Gua.Runtime.csproj") -c $Configuration -f netstandard2.1 --no-restore -p:Version=$Version -p:RestoreEnablePackagePruning=false -p:DefineConstants=GUA_STATIC_LINK -o $webManagedBuild
    if ($LASTEXITCODE -ne 0) { throw "Failed to build the WebGL Gua.Runtime assembly." }
}

New-Item -ItemType Directory -Force $managed, $windowsNative, $linuxNative, $macNative, $webManaged | Out-Null
Copy-Item (Join-Path $root "bindings/dotnet/src/Gua.Core/bin/$Configuration/netstandard2.1/Gua.Core.dll") $managed -Force
Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Core.dll.meta") $managed -Force
Copy-Item (Join-Path $root "bindings/dotnet/src/Gua.Runtime/bin/$Configuration/netstandard2.1/Gua.Runtime.dll") $managed -Force
Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Runtime.dll.meta") $managed -Force
if (-not $DesktopOnly) {
    Copy-Item (Join-Path $webManagedBuild "Gua.Core.dll") $webManaged -Force
    Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Core.WebGL.dll.meta") (Join-Path $webManaged "Gua.Core.dll.meta") -Force
    Copy-Item (Join-Path $webManagedBuild "Gua.Runtime.dll") $webManaged -Force
    Copy-Item (Join-Path $root "scripts/unity-meta/Gua.Runtime.WebGL.dll.meta") (Join-Path $webManaged "Gua.Runtime.dll.meta") -Force
}

& (Join-Path $PSScriptRoot "copy-unity-managed-closure.ps1") `
    -AssetsFile (Join-Path $root "bindings/dotnet/src/Gua.Runtime/obj/project.assets.json") `
    -TargetFramework "netstandard2.1" `
    -Destination $managed

foreach ($asset in @(
    @{ Rid = "win-x64"; File = "gua.dll"; Destination = $windowsNative; Meta = "gua.dll.meta" },
    @{ Rid = "win-x64"; File = "gua_runtime.dll"; Destination = $windowsNative; Meta = "gua_runtime.dll.meta" },
    @{ Rid = "linux-x64"; File = "libgua.so"; Destination = $linuxNative; Meta = "libgua.so.meta" },
    @{ Rid = "linux-x64"; File = "libgua_runtime.so"; Destination = $linuxNative; Meta = "libgua_runtime.so.meta" },
    @{ Rid = "osx-universal"; File = "libgua.dylib"; Destination = $macNative; Meta = "libgua.dylib.meta" },
    @{ Rid = "osx-universal"; File = "libgua_runtime.dylib"; Destination = $macNative; Meta = "libgua_runtime.dylib.meta" }
)) {
    $source = Join-Path (Join-Path $NativeAssetsRoot $asset.Rid) $asset.File
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing Unity native asset '$source'." }
    Copy-Item -LiteralPath $source -Destination $asset.Destination -Force
    Copy-Item -LiteralPath (Join-Path $root "scripts/unity-meta/$($asset.Meta)") -Destination $asset.Destination -Force
}

Write-Host "Prepared Unity project dependencies for Gua $Version."
