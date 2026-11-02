param([Parameter(Mandatory)][string]$BundleRoot)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$bundle=[IO.Path]::GetFullPath($BundleRoot)
$output=Join-Path $root ('build/nuget-game-consumer-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
$feed=Join-Path $output 'feed'; $managed=Join-Path $output 'managed'; $version='0.0.0-integrity-fixture'
New-Item -ItemType Directory -Force $feed | Out-Null
dotnet build (Join-Path $root 'bindings/dotnet/src/Gua.Runtime') -c Release -f net10.0 -o $managed --nologo -v quiet -m:1 -nr:false
if($LASTEXITCODE) {throw 'Cannot build the current managed fixture assemblies.'}
# These two dependency packages are TEST fixtures made from actual current
# assemblies. They deliberately include legacy ON native assets so the consumer
# must exclude them. They are not provider release packages or public artifacts.
foreach($component in 'Core','Runtime') {
    $dependency=if($component -eq 'Runtime') {"<dependencies><group targetFramework=`"net10.0`"><dependency id=`"Gua.Core`" version=`"[$version]`" /></group></dependencies>"} else {''}
    $native=if($component -eq 'Core') {'gua.dll'} else {'gua_runtime.dll'}
    $source=[Security.SecurityElement]::Escape((Join-Path $managed "Gua.$component.dll"))
    $legacy=[Security.SecurityElement]::Escape((Join-Path $bundle "testing/$native"))
    $nuspec=Join-Path $output "Gua.$component.nuspec"
    @"
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Gua.$component</id><version>$version</version><authors>Gua test fixture</authors><description>Private local integrity test fixture; not for release.</description>$dependency</metadata><files><file src="$source" target="lib/net10.0" /><file src="$legacy" target="runtimes/win-x64/native" /></files></package>
"@ | Set-Content -LiteralPath $nuspec -Encoding utf8NoBOM
    dotnet pack (Join-Path $root 'tools/Gua.GameBuild') -c Release --no-build --no-restore --nologo -v quiet "-p:NuspecFile=$nuspec" "-p:PackageOutputPath=$feed"
    if($LASTEXITCODE) {throw 'Cannot pack the private managed dependency fixture.'}
}
& (Join-Path $PSScriptRoot 'pack-game-build-nuget.ps1') -BundleRoot $bundle -Version $version -RuntimeVersion $version -OutputDirectory $feed
dotnet build (Join-Path $root 'tools/Gua.ArtifactProbe') -c Release --nologo -v quiet -m:1 -nr:false
if($LASTEXITCODE) {throw 'Cannot build the independent IL probe.'}
$probe=Join-Path $root 'tools/Gua.ArtifactProbe/bin/Release/net10.0/Gua.ArtifactProbe.dll'
$results=[Collections.Generic.List[object]]::new()
$beforeProfile=$env:GUA_OBSERVATION_PROFILE
try {
    $env:GUA_OBSERVATION_PROFILE=$null
    foreach($mode in 'off','testing','player') {
        $project=Join-Path $output $mode; New-Item -ItemType Directory $project | Out-Null
        $setting=if($mode -eq 'off') {''} else {"<GuaRemoteMode>$mode</GuaRemoteMode>"}
        @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><RuntimeIdentifier>win-x64</RuntimeIdentifier>$setting</PropertyGroup><ItemGroup><PackageReference Include="Gua.GameBuild" Version="[$version]" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $project 'Game.csproj') -Encoding utf8NoBOM
        Copy-Item -LiteralPath (Join-Path $root 'bindings/dotnet/tests/Gua.AutomaticGameBuild/Program.cs') -Destination $project
        $feedXml=[Security.SecurityElement]::Escape($feed)
        "<configuration><packageSources><clear/><add key=`"local-fixtures`" value=`"$feedXml`"/></packageSources></configuration>" | Set-Content -LiteralPath (Join-Path $project 'NuGet.config') -Encoding utf8NoBOM
        $publish=Join-Path $project 'published'
        dotnet publish (Join-Path $project 'Game.csproj') -c Release --self-contained false -o $publish --packages (Join-Path $output 'packages') --nologo -v quiet -m:1 -nr:false
        if($LASTEXITCODE) {throw "PackageReference-only $mode game publish failed."}
        $nativeFiles=@(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {$_.Name -in 'gua.dll','gua_runtime.dll'})
        if($nativeFiles.Count -ne 2 -or @($nativeFiles | Where-Object {$_.DirectoryName -ne $publish}).Count) {throw 'Legacy SDK native assets leaked into the published game.'}
        foreach($file in $nativeFiles) {if((Get-FileHash $file.FullName).Hash -ne (Get-FileHash (Join-Path $bundle "$mode/$($file.Name)")).Hash) {throw 'Published native bytes differ from the selected prebuilt mode.'}}
        $app=Join-Path $publish 'Game.dll'
        $il=(& dotnet $probe game $app) | ConvertFrom-Json
        if($LASTEXITCODE -or !$il.gameOwnedPolicy -or $il.remoteStartReference -ne ($mode -ne 'off')) {throw 'Published game IL does not match its mode.'}
        $log=Join-Path $project 'positive.log'
        & dotnet $app *> $log
        if($LASTEXITCODE -or [string](Get-Content $log -Raw) -notmatch "game-owned policy: $mode") {throw "Published $mode game failed."}
        $results.Add(@{case=$mode;result='package-reference-publish-run-passed';gameIl=$il;evidence=$log})
        if($mode -eq 'off') {
            Copy-Item (Join-Path $bundle 'testing/gua.dll'),(Join-Path $bundle 'testing/gua_runtime.dll') -Destination $publish -Force
            $negative=Join-Path $project 'swap-negative.log'
            & dotnet $app *> $negative
            if($LASTEXITCODE -ne 78 -or [string](Get-Content $negative -Raw) -notmatch 'nativeLoaded=false') {throw 'NuGet consumer accepted the ON replacement or rejected only after native load.'}
            $results.Add(@{case='off-to-on';result='refused-before-native-load';evidence=$negative})
        }
    }
    $unsupported=Join-Path $output 'off'
    'config_version=5' | Set-Content (Join-Path $unsupported 'project.godot')
    $unsupportedLog=Join-Path $unsupported 'unsupported-godot-host.log'
    dotnet build (Join-Path $unsupported 'Game.csproj') -c Release --no-restore --nologo -v quiet -m:1 -nr:false *> $unsupportedLog
    if($LASTEXITCODE -eq 0 -or [string](Get-Content $unsupportedLog -Raw) -notmatch 'Godot .NET needs its own verified host') {throw 'The standalone package silently claimed to protect a Godot host.'}
    $results.Add(@{case='unsupported-godot-host';result='build-refused';evidence=$unsupportedLog})
} finally {$env:GUA_OBSERVATION_PROFILE=$beforeProfile}
$results | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "NuGet-only consumer evidence: $output"
$global:LASTEXITCODE=0
