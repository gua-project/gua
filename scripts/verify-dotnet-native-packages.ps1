param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-PackageEntries([string]$PackageName, [string[]]$ExpectedEntries) {
    $path = Join-Path $PackageDirectory "$PackageName.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Native package is missing: $path"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $path))
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($expected in $ExpectedEntries) {
            if ($entries -notcontains $expected) {
                throw "$PackageName is missing native package entry: $expected"
            }
            $component = if ($PackageName -eq 'Gua.Core') {'core'} else {'runtime'}
            $manifestName = $expected.Substring(0,$expected.LastIndexOf('/')+1) + "gua-$component-build-manifest.json"
            $manifestEntry = @($archive.Entries | Where-Object FullName -CEQ $manifestName)
            if ($manifestEntry.Count -ne 1) { throw "$PackageName lacks a unique compiled identity: $manifestName" }
            $reader = [IO.StreamReader]::new($manifestEntry[0].Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            $identity = $manifest.build | ConvertTo-Json -Depth 12 -Compress
            if ($script:PackageBuildIdentity -and $script:PackageBuildIdentity -ne $identity) { throw 'NuGet native components have different compiled identities.' }
            $script:PackageBuildIdentity = $identity
            $nativeEntry = @($archive.Entries | Where-Object FullName -CEQ $expected)
            $hashEntry = @($manifest.files | Where-Object path -CEQ ([IO.Path]::GetFileName($expected)))
            if ($nativeEntry.Count -ne 1 -or $hashEntry.Count -ne 1) { throw 'Missing or duplicate NuGet native identity.' }
            $stream = $nativeEntry[0].Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $stream.Dispose(); $sha.Dispose() }
            if ($actual -ne $hashEntry[0].sha256) { throw "NuGet native bytes differ from manifest: $expected" }
        }
    } finally {
        $archive.Dispose()
    }
}

Assert-PackageEntries "Gua.Core" @(
    "runtimes/win-x64/native/gua.dll",
    "runtimes/linux-x64/native/libgua.so",
    "runtimes/osx-x64/native/libgua.dylib",
    "runtimes/osx-arm64/native/libgua.dylib"
)

Assert-PackageEntries "Gua.Runtime" @(
    "runtimes/win-x64/native/gua_runtime.dll",
    "runtimes/linux-x64/native/libgua_runtime.so",
    "runtimes/osx-x64/native/libgua_runtime.dylib",
    "runtimes/osx-arm64/native/libgua_runtime.dylib"
)

Write-Host "Verified native NuGet assets for $Version."
