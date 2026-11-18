param(
    [Parameter(Mandatory)][string]$ShippingPackage,
    [Parameter(Mandatory)][string]$TestingPackage,
    [Parameter(Mandatory)][string]$PlayerPackage
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root ('build/game-owned-native-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Path $output | Out-Null
dotnet build (Join-Path $root 'tools/Gua.GameBuild') -c Release --nologo -v quiet -m:1 -nr:false
if ($LASTEXITCODE) { throw 'Game policy generator build failed.' }
$generator = Join-Path $root 'tools/Gua.GameBuild/bin/Release/net10.0/Gua.GameBuild.dll'
$results = [Collections.Generic.List[object]]::new()
foreach ($case in @(
    @{Mode='off'; Flavor='shipping'; Remote='off'; Package=$ShippingPackage},
    @{Mode='testing'; Flavor='qa'; Remote='on'; Package=$TestingPackage},
    @{Mode='player'; Flavor='ai-player'; Remote='on'; Package=$PlayerPackage}
)) {
    $directory = Join-Path $output $case.Mode
    $provider = Join-Path $directory 'provider'
    New-Item -ItemType Directory -Path $provider -Force | Out-Null
    # Component packages select their own closure. Do not equate the combined
    # manifest with a requirement to ship unrelated engine DLLs or scripts.
    $manifest = Get-Content -LiteralPath (Join-Path $case.Package 'gua-build-manifest.json') -Raw | ConvertFrom-Json
    $manifest.files = @($manifest.files | Where-Object { $_.path -in 'gua.dll','gua_runtime.dll','LICENSE' })
    foreach ($entry in $manifest.files) { Copy-Item -LiteralPath (Join-Path $case.Package $entry.path) -Destination $provider }
    $manifest | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $provider 'gua-build-manifest.json') -Encoding utf8NoBOM
    $policy = Join-Path $directory 'GamePolicy.cs'
    dotnet $generator generate $provider $case.Mode $policy
    if ($LASTEXITCODE) { throw 'Game policy generation failed.' }
    $consumer = Join-Path $directory 'consumer'
    dotnet build (Join-Path $root 'bindings/dotnet/tests/Gua.GameBuildConsumer') -c Release --nologo -v quiet -m:1 -nr:false "-p:GuaGamePolicySource=$policy" -o $consumer
    if ($LASTEXITCODE) { throw 'Game consumer build failed.' }
    Copy-Item -LiteralPath (Join-Path $provider 'gua.dll'),(Join-Path $provider 'gua_runtime.dll') -Destination $consumer
    $log = Join-Path $directory 'positive.log'
    & dotnet (Join-Path $consumer 'Gua.GameBuildConsumer.dll') $consumer $consumer $case.Flavor $case.Remote *> $log
    if ($LASTEXITCODE) { Get-Content -LiteralPath $log; throw "Positive case failed: $($case.Mode)" }
    $results.Add(@{case=$case.Mode;result='passed';evidence=$log})
    if ($case.Mode -eq 'off') {
        # Replace the complete native Gua DLL set, leaving the compiled game policy
        # untouched. A runtime manifest replacement must not change expectations.
        Copy-Item -LiteralPath (Join-Path $TestingPackage 'gua.dll'),(Join-Path $TestingPackage 'gua_runtime.dll') -Destination $consumer -Force
        Copy-Item -LiteralPath (Join-Path $TestingPackage 'gua-build-manifest.json') -Destination $consumer -Force
        $negative = Join-Path $directory 'swap-negative.log'
        & dotnet (Join-Path $consumer 'Gua.GameBuildConsumer.dll') $consumer $consumer qa on *> $negative
        if ($LASTEXITCODE -ne 78 -or (Get-Content -LiteralPath $negative -Raw) -notmatch 'Gua game integrity verification failed; nativeLoaded=false') {
            throw 'OFF-to-ON replacement did not fail in the game-owned verifier.'
        }
        $results.Add(@{case='off-to-testing-native-set-and-manifest';result='rejected-before-native-load';exitCode=78;evidence=$negative})
        # Deliberately break load ordering in a disposable generated game policy.
        # The same no-load acceptance condition must detect the broken behavior.
        $mutantPolicy = Join-Path $directory 'EarlyLoadPolicy.cs'
        $originalPolicy = (Get-Content -LiteralPath $policy -Raw).Replace("`r`n", "`n")
        $marker = 'private static void VerifyAndLoad()' + "`n    {"
        $injection = $marker + "`n        LoadLibraryEx(Path.Combine(AppContext.BaseDirectory, `"gua.dll`"), 0, 0x800);"
        if (-not $originalPolicy.Contains($marker)) { throw 'Cannot apply the bounded early-load mutation.' }
        $originalPolicy.Replace($marker,$injection) | Set-Content -LiteralPath $mutantPolicy -Encoding utf8NoBOM
        $mutant = Join-Path $directory 'early-load-mutation'
        dotnet build (Join-Path $root 'bindings/dotnet/tests/Gua.GameBuildConsumer') -c Release --nologo -v quiet -m:1 -nr:false "-p:GuaGamePolicySource=$mutantPolicy" -o $mutant
        if ($LASTEXITCODE) { throw 'Early-load mutation did not build; this is not detection evidence.' }
        Copy-Item -LiteralPath (Join-Path $TestingPackage 'gua.dll'),(Join-Path $TestingPackage 'gua_runtime.dll') -Destination $mutant
        $mutationLog = Join-Path $directory 'early-load-mutation.log'
        & dotnet (Join-Path $mutant 'Gua.GameBuildConsumer.dll') $mutant $mutant qa on *> $mutationLog
        if ($LASTEXITCODE -ne 79 -or (Get-Content -LiteralPath $mutationLog -Raw) -notmatch 'nativeLoaded=true') {
            throw 'No-load assertion did not detect the deliberately early native load.'
        }
        $results.Add(@{case='early-load-order-mutation';result='no-load-contract-violation-detected';exitCode=79;evidence=$mutationLog})
    }
}
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "Game-owned native policy evidence: $output"
