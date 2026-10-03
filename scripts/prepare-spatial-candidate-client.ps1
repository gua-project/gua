param([Parameter(Mandatory=$true)][string]$PackageDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[Parameter(Mandatory=$true)][string]$SourceCommit)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$feed=(Resolve-Path $PackageDirectory).Path
$project=Join-Path ([IO.Path]::GetTempPath()) ('gua-spatial-client-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $project,$OutputDirectory | Out-Null
Copy-Item "$root/examples/spatial-fixtures/client/*" $project
$escaped=[Security.SecurityElement]::Escape($feed)
"<configuration><packageSources><clear/><add key=`"local`" value=`"$escaped`"/></packageSources></configuration>" | Set-Content "$project/NuGet.Config" -Encoding utf8NoBOM
$previous=$env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES="$project/empty-cache"
    dotnet build "$project/SpatialClient.csproj" -c Release -o "$OutputDirectory/client" -p:GuaUsePackages=true '-p:GuaPackageVersion=[0.0.0-ci]' "-p:RestoreConfigFile=$project/NuGet.Config" -p:NuGetAudit=false
    if($LASTEXITCODE -ne 0) {throw 'Spatial package-only client build failed'}
    $assets=Get-Content "$project/obj/project.assets.json" -Raw | ConvertFrom-Json -AsHashtable
    foreach($name in 'Gua.Core','Gua.Testing') {if(-not $assets.libraries.ContainsKey("$name/0.0.0-ci")) {throw "Candidate library missing: $name"}}
    foreach($key in $assets.libraries.Keys) {if($assets.libraries[$key].type -eq 'project' -or ($key -like 'Gua.*' -and -not $key.EndsWith('/0.0.0-ci'))) {throw "Source/mixed-version library: $key"}}
    scripts/write-distribution-manifest.ps1 -PackageDirectory $feed -Version 0.0.0-ci -SourceCommit $SourceCommit -OutputPath "$OutputDirectory/package-manifest.json"
    Copy-Item "$project/obj/project.assets.json" $OutputDirectory
    @{project=$project;sourceCommit=$SourceCommit;recipeCommit=$env:GUA_RECIPE_ID;projectLibraries=0;nativeOverrides='absent';feed=$feed} | ConvertTo-Json | Set-Content "$OutputDirectory/client-provenance.json" -Encoding utf8NoBOM
} finally {$env:NUGET_PACKAGES=$previous}
