$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cases = @(
    @{Name='shipping-on';Args=@('-DGUA_DISTRIBUTION_FLAVOR=shipping','-DGUA_ENABLE_REMOTE_AUTOMATION=ON');Reason='shipping cannot contain'},
    @{Name='qa-off';Args=@('-DGUA_DISTRIBUTION_FLAVOR=qa','-DGUA_ENABLE_REMOTE_AUTOMATION=OFF');Reason='require GUA_ENABLE_REMOTE_AUTOMATION'},
    @{Name='player-off';Args=@('-DGUA_DISTRIBUTION_FLAVOR=ai-player','-DGUA_ENABLE_REMOTE_AUTOMATION=OFF');Reason='require GUA_ENABLE_REMOTE_AUTOMATION'},
    @{Name='legacy-transport-bypass';Args=@('-DGUA_DISTRIBUTION_FLAVOR=shipping','-DGUA_ENABLE_REMOTE_AUTOMATION=OFF','-DGUA_BUILD_WS_BRIDGE=ON');Reason='requires the explicit remote'},
    @{Name='unknown';Args=@('-DGUA_DISTRIBUTION_FLAVOR=unknown');Reason='Unknown GUA_DISTRIBUTION_FLAVOR'}
)
$evidence = Join-Path $root ('build/cfg-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory $evidence | Out-Null
$caseNumber = 0
$generator = & (Join-Path $PSScriptRoot 'get-msvc-generator.ps1')
foreach ($case in $cases) {
    $caseNumber++
    $lines = @(cmake -S $root -B (Join-Path $evidence $caseNumber) -G $generator -A x64 -DGUA_BUILD_GODOT=OFF -DGUA_BUILD_EXAMPLES=OFF @($case.Args) 2>&1)
    $exitCode = $LASTEXITCODE
    $lines | Set-Content -LiteralPath (Join-Path $evidence ($case.Name + '.log'))
    if ($exitCode -eq 0 -or ($lines -join "`n") -notmatch $case.Reason) { throw "Configuration rejection did not detect $($case.Name)." }
}
Write-Host "All five invalid configurations rejected: $evidence"
exit 0
