param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$WindowsAssetsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$NativeAssetsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$GodotAddonDirectory,

    [Parameter(Mandatory = $true)]
    [string]$UnityPackageDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
function Require-File([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release asset is missing: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

if (Test-Path -LiteralPath $OutputDirectory) {
    $existingOutput = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
    if ($existingOutput.Count -gt 0) {
        throw "Release output directory must be empty: $OutputDirectory"
    }
} else {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
}

$godotArchive = Require-File (Join-Path $GodotAddonDirectory "gua-godot-addon-v$Version.zip")
$legacyWindowsGodotArchive = Require-File (Join-Path $WindowsAssetsDirectory "gua-godot-plugin-windows-v$Version.zip")
& (Join-Path $PSScriptRoot 'verify-godot-addon-archive.ps1') -ArchivePath $godotArchive
& (Join-Path $PSScriptRoot 'verify-godot-addon-archive.ps1') -ArchivePath $legacyWindowsGodotArchive -Rid win-x64
$unityArchive = Require-File (Join-Path $UnityPackageDirectory "com.link1345.gua-$Version.tgz")

Copy-Item -LiteralPath $godotArchive -Destination $OutputDirectory
Copy-Item -LiteralPath $legacyWindowsGodotArchive -Destination $OutputDirectory
Copy-Item -LiteralPath $unityArchive -Destination $OutputDirectory

foreach ($rid in "linux-x64", "osx-x64", "osx-arm64") {
    $nativeArchive = Require-File (Join-Path $NativeAssetsDirectory "gua-native-$rid-v$Version.zip")
    Copy-Item -LiteralPath $nativeArchive -Destination $OutputDirectory
}
$windowsNativeArchive = Require-File (Join-Path $WindowsAssetsDirectory "gua-native-win-x64-v$Version.zip")
Copy-Item -LiteralPath $windowsNativeArchive -Destination $OutputDirectory

$inspectorFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $WindowsAssetsDirectory "inspector") -Recurse -File |
        Where-Object { $_.Extension -in ".exe", ".msi" }
)
if ($inspectorFiles.Count -eq 0) {
    throw "No Inspector installer was found below $WindowsAssetsDirectory"
}
foreach ($inspectorFile in $inspectorFiles) {
    $destination = Join-Path $OutputDirectory $inspectorFile.Name
    if (Test-Path -LiteralPath $destination) {
        throw "Inspector installer names must be unique: $($inspectorFile.Name)"
    }
    Copy-Item -LiteralPath $inspectorFile.FullName -Destination $destination
}

$publishedFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name)
Write-Host "GitHub Release assets:"
$publishedFiles | ForEach-Object { Write-Host "- $($_.Name)" }
