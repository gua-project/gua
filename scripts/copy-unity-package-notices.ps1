param(
    [Parameter(Mandatory = $true)][string]$AssetsFile,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$notices = Join-Path $OutputDirectory 'notices'
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE') -Force
$manifest = foreach ($key in @($assets.libraries.Keys | Sort-Object)) {
    $library = $assets.libraries[$key]
    if ($library.type -ne 'package') { continue }
    $id, $version = $key.Split('/')
    $archive = $null
    foreach ($packageRoot in $assets.packageFolders.Keys) {
        $candidate = Join-Path (Join-Path $packageRoot $library.path) "$($id.ToLowerInvariant()).$version.nupkg"
        if (Test-Path -LiteralPath $candidate) { $archive = $candidate; break }
    }
    if ($null -eq $archive) { throw "Restored dependency archive is missing: $key" }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
        if ($entry.Count -ne 1) { throw "Invalid dependency metadata: $key" }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml]$xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $xml.SelectSingleNode('//*[local-name()="metadata"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        if ($null -eq $license) { throw "Dependency license metadata is absent: $key" }
        $entries = @($zip.Entries | Where-Object FullName -Match '(?i)(^|/)(LICENSE(?:\.txt|\.md)?|THIRD-PARTY-NOTICES\.TXT)$')
        if ($license.type -eq 'file') {
            $file = $zip.GetEntry($license.InnerText.Replace('\', '/'))
            if ($null -eq $file) { throw "Dependency license file is absent: $key" }
            $entries = @($entries + $file | Sort-Object FullName -Unique)
        }
        $names = foreach ($notice in $entries) {
            $name = "$id-$version-$($notice.FullName -replace '[\\/]', '_')"
            $noticeReader = [IO.StreamReader]::new($notice.Open())
            try { $text = $noticeReader.ReadToEnd() } finally { $noticeReader.Dispose() }
            if ([string]::IsNullOrWhiteSpace($text)) { throw "Dependency notice is empty: $key" }
            $text | Set-Content -LiteralPath (Join-Path $notices $name) -Encoding utf8NoBOM
            $name
        }
        if ($id -notlike 'Gua.*' -and $license.type -eq 'expression' -and $license.InnerText -eq 'MIT') {
            $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
            if ($null -ne $copyright -and -not [string]::IsNullOrWhiteSpace($copyright.InnerText)) {
                $guaLicense = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw
                $terms = $guaLicense.Substring($guaLicense.IndexOf('Permission is hereby granted'))
                $name = "$id-$version-MIT.txt"
                "$id $version`n$($copyright.InnerText)`n`n$terms" | Set-Content -LiteralPath (Join-Path $notices $name) -Encoding utf8NoBOM
                $names = @($names) + $name
            } elseif (-not @($entries | Where-Object FullName -Match '(?i)^LICENSE(?:\.txt|\.md)?$')) {
                throw "Own MIT attribution is unavailable: $key"
            }
        } elseif (@($names).Count -eq 0 -and $id -notlike 'Gua.*') {
            throw "Full redistribution notice is unavailable: $key"
        }
        @{ package = $key; license = $license.InnerText; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); notices = @($names) }
    } finally { $zip.Dispose() }
}
@{ packages = @($manifest); guaLicense = 'LICENSE' } | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $notices 'managed-dependencies.json') -Encoding utf8NoBOM
