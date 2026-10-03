param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = '0.0.0-ci'
)
$ErrorActionPreference = 'Stop'
if ($SourceCommit -notmatch '^[a-f0-9]{40}$') { throw 'Full source commit is required.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$thin = Join-Path ([IO.Path]::GetTempPath()) ('gua-unity-thin-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $thin | Out-Null
foreach ($package in @(@{ Id='Gua.Core'; Windows='gua.dll'; Unix='libgua' }, @{ Id='Gua.Runtime'; Windows='gua_runtime.dll'; Unix='libgua_runtime' })) {
    $path = Join-Path $PackageDirectory "$($package.Id).$Version.nupkg"
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry("$($package.Id).nuspec").Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $xml.SelectSingleNode('//*[local-name()="metadata"]')
        if ($metadata.SelectSingleNode('*[local-name()="repository"]').commit -ne $SourceCommit -or
            $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -ne $Version) { throw 'Candidate package identity mismatch.' }
        foreach ($rid in 'win-x64','linux-x64','osx-x64','osx-arm64') {
            $file = if ($rid -eq 'win-x64') { $package.Windows } elseif ($rid -eq 'linux-x64') { "$($package.Unix).so" } else { "$($package.Unix).dylib" }
            $directory = if ($rid.StartsWith('osx-')) { Join-Path $thin $rid } else { Join-Path $OutputDirectory $rid }
            New-Item -ItemType Directory -Force $directory | Out-Null
            $entry = $zip.GetEntry("runtimes/$rid/native/$file")
            if ($null -eq $entry -or $entry.Length -eq 0) { throw "Candidate native asset is absent: $rid/$file" }
            $input = $entry.Open(); $output = [IO.File]::Create((Join-Path $directory $file))
            try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
        }
        $universal = Join-Path $OutputDirectory 'osx-universal'
        New-Item -ItemType Directory -Force $universal | Out-Null
        $file = "$($package.Unix).dylib"
        & lipo -create (Join-Path $thin "osx-x64/$file") (Join-Path $thin "osx-arm64/$file") -output (Join-Path $universal $file)
        if ($LASTEXITCODE -ne 0) { throw "Universal 2 assembly failed: $file" }
        & lipo -verify_arch x86_64 arm64 (Join-Path $universal $file)
        if ($LASTEXITCODE -ne 0) { throw "Universal 2 verification failed: $file" }
    } finally { $zip.Dispose() }
}
$files = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | ForEach-Object {
    @{ path=[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($OutputDirectory),$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
@{ sourceCommit=$SourceCommit; version=$Version; files=@($files) } | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'native-manifest.json') -Encoding utf8NoBOM
