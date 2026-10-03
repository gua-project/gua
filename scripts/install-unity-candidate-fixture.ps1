param([Parameter(Mandatory=$true)][string]$ArchiveDirectory,[Parameter(Mandatory=$true)][string]$ProjectDirectory,[Parameter(Mandatory=$true)][string]$SourceCommit,[ValidateSet('editor','spatial')][string]$Kind)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$archive=Join-Path $ArchiveDirectory 'com.link1345.gua-0.0.0-ci.tgz'
$provenance=Get-Content (Join-Path $ArchiveDirectory 'artifact-provenance.json') -Raw | ConvertFrom-Json
if($provenance.sourceCommit -ne $SourceCommit -or $provenance.version -ne '0.0.0-ci' -or $provenance.sha256 -ne (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()) {throw 'Actual UPM source/version/hash mismatch'}
if(Test-Path $ProjectDirectory) {throw 'Require a fresh fixture project'}
New-Item -ItemType Directory -Force "$ProjectDirectory/Assets/Editor","$ProjectDirectory/Packages" | Out-Null
Copy-Item "$root/examples/unity-smoke/ProjectSettings" "$ProjectDirectory/ProjectSettings" -Recurse
$extracted=Join-Path ([IO.Path]::GetTempPath()) ('gua-upm-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $extracted | Out-Null
tar -xzf $archive -C $extracted
if($LASTEXITCODE -ne 0) {throw 'Actual UPM extraction failed'}
Copy-Item "$extracted/package" "$ProjectDirectory/Packages/com.link1345.gua" -Recurse
if(Get-ChildItem "$ProjectDirectory/Packages/com.link1345.gua/Runtime" -Recurse -Filter '*.cs') {throw 'UPM contains implementation source'}
$manifest=Get-Content "$root/examples/unity-smoke/Packages/manifest.json" -Raw | ConvertFrom-Json
$manifest.dependencies.PSObject.Properties.Remove('com.link1345.gua')
$manifest | ConvertTo-Json -Depth 20 | Set-Content "$ProjectDirectory/Packages/manifest.json" -Encoding utf8NoBOM
if($Kind -eq 'editor') {
    Copy-Item "$root/examples/unity-smoke/Assets/*" "$ProjectDirectory/Assets" -Recurse -Force
    Copy-Item "$root/examples/spatial-fixtures/unity/EditorCandidateRun.cs" "$ProjectDirectory/Assets/Editor"
} else {
    foreach($file in 'SpatialFixture.cs','SpatialProfile.cs','SpatialGeometryHost.cs','SpatialTransportHost.cs') {Copy-Item "$root/examples/spatial-fixtures/unity/$file" "$ProjectDirectory/Assets"}
    Copy-Item "$root/examples/spatial-fixtures/unity/SpatialTransportBuild.cs" "$ProjectDirectory/Assets/Editor"
    Copy-Item "$root/protocol/fixtures/spatial-engine-r1.json" $ProjectDirectory
}
$provenance | ConvertTo-Json -Depth 20 | Set-Content "$ProjectDirectory/artifact-provenance.json" -Encoding utf8NoBOM
