param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$noticeDirectory = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))) 'dependency-licenses'
New-Item -ItemType Directory -Force $noticeDirectory | Out-Null
$packages = foreach ($file in Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File | Sort-Object Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entry = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entry.Count -ne 1) { throw "Invalid package metadata: $($file.Name)" }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $xml.SelectSingleNode('//*[local-name()="metadata"]')
        $id = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
        $packageVersion = $metadata.SelectSingleNode('*[local-name()="version"]').InnerText
        $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        if ($null -eq $license) { throw "Package license metadata is missing: $id" }
        if ($id -like 'Gua.*' -and $id -ne 'Gua.DistributionSmoke') {
            if ($packageVersion -ne $Version -or $null -eq $repository -or $repository.commit -ne $SourceCommit) { throw "Package provenance mismatch: $id" }
            foreach ($dependency in $metadata.SelectNodes('.//*[local-name()="dependency"]')) {
                if ($dependency.id -like 'Gua.*' -and $dependency.version -ne $Version -and $dependency.version -ne "[$Version]") {
                    throw "Mixed Gua package versions: $id -> $($dependency.id) $($dependency.version)"
                }
            }
        }
        if ($license.type -eq 'file') {
            $licenseEntry = $zip.GetEntry($license.InnerText.Replace('\', '/'))
            if ($null -eq $licenseEntry) { throw "Packaged license file is absent: $id" }
            $licenseReader = [IO.StreamReader]::new($licenseEntry.Open())
            try { $licenseReader.ReadToEnd() | Set-Content -LiteralPath (Join-Path $noticeDirectory "$id-$packageVersion.txt") -Encoding utf8NoBOM }
            finally { $licenseReader.Dispose() }
        }
        if ($id -eq 'Gua.Testing') {
            foreach ($source in Get-ChildItem -LiteralPath (Join-Path $root 'protocol/schema') -File | Where-Object { $_.Name -like '*.schema.json' -or $_.Extension -eq '.mjs' }) {
                $schemaEntry = $zip.GetEntry("schemas/$($source.Name)")
                if ($null -eq $schemaEntry) { throw "Missing packaged schema/helper: $($source.Name)" }
                $stream = $schemaEntry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)) } finally { $stream.Dispose(); $sha.Dispose() }
                if ($hash -ne (Get-FileHash -LiteralPath $source.FullName).Hash) { throw "Packaged schema differs from source: $($source.Name)" }
            }
            foreach ($name in 'index.html', 'viewer.js', 'version.json', 'Viewer.LICENSES.txt', 'LICENSE') {
                if ($null -eq $zip.GetEntry("trace/$name")) { throw "Viewer distribution entry is absent: $name" }
            }
        }
        [ordered]@{ id = $id; version = $packageVersion; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();
            licenseType = $license.type; license = $license.InnerText; repositoryCommit = $(if ($repository) { $repository.commit } else { $null });
            dependencies = @($metadata.SelectNodes('.//*[local-name()="dependency"]') | ForEach-Object { @{ id = $_.id; version = $_.version } }) }
    } finally { $zip.Dispose() }
}
[ordered]@{ sourceCommit = $SourceCommit; candidateVersion = $Version; packages = @($packages); publication = 'none' } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
