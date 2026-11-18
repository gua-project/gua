param([Parameter(Mandatory)][string]$BuildDirectory,[Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Use a new C++ import artifact directory.'}
$manifest=Get-Content (Join-Path $PackageDirectory 'gua-build-manifest.json') -Raw | ConvertFrom-Json
$inputs=@()
foreach($component in @(@{dir='gua-core';name='gua'},@{dir='gua-runtime';name='gua_runtime'})) {
  $prefix=Join-Path $BuildDirectory "native/$($component.dir)/Release/$($component.name)"
  $entry=@($manifest.files | Where-Object path -CEQ "$($component.name).dll")
  if($entry.Count -ne 1 -or (Get-FileHash "$prefix.dll").Hash.ToLowerInvariant() -ne $entry[0].sha256){throw 'Import library build does not match the accepted native package.'}
  $inputs+=@{path="$($component.name).lib";source="$prefix.lib";sha256=(Get-FileHash "$prefix.lib").Hash.ToLowerInvariant()}
}
New-Item -ItemType Directory $OutputDirectory | Out-Null
foreach($item in $inputs){Copy-Item $item.source (Join-Path $OutputDirectory $item.path)}
@{schemaVersion=1;nativeSourceCommit=$manifest.build.sourceCommit;buildId=$manifest.build.buildId;files=@($inputs | ForEach-Object {@{path=$_.path;sha256=$_.sha256}})} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'cpp-imports.json') -Encoding utf8NoBOM
