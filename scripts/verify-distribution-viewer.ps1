param([Parameter(Mandatory = $true)][string]$ChromeExecutable, [Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$profile = Join-Path ([IO.Path]::GetTempPath()) ('gua-viewer-browser-' + [guid]::NewGuid().ToString('N'))
$arguments = @('--headless=new', '--no-first-run', "--remote-debugging-port=$port", "--user-data-dir=`"$profile`"", 'about:blank')
$start = @{ FilePath = $ChromeExecutable; ArgumentList = $arguments; PassThru = $true }
if ($IsWindows) { $start.WindowStyle = 'Hidden' }
$browser = Start-Process @start
try {
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        try { $null = Invoke-RestMethod "http://127.0.0.1:$port/json/version" -TimeoutSec 1; $ready = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Headless browser did not start.' }
    & bun (Join-Path $PSScriptRoot 'verify-distribution-viewer-browser.ts') ([IO.Path]::GetFullPath($OutputDirectory)) $port
    if ($LASTEXITCODE -ne 0) { throw 'Packaged Viewer browser acceptance failed.' }
} finally { if (-not $browser.HasExited) { $browser.Kill() } }
