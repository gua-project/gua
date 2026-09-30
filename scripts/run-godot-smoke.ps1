param(
    [string]$GodotExecutable = $env:GODOT_EXECUTABLE,
    [TimeSpan]$Timeout = [TimeSpan]::FromMinutes(2)
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($GodotExecutable)) {
    $command = Get-Command "Godot_v4.7-stable_win64_console.exe" -ErrorAction SilentlyContinue
    if ($null -eq $command) { $command = Get-Command "godot" -ErrorAction SilentlyContinue }
    if ($null -ne $command) { $GodotExecutable = $command.Source }
}
if ($Timeout -le [TimeSpan]::Zero) { throw "Timeout must be greater than zero." }
if ([string]::IsNullOrWhiteSpace($GodotExecutable) -or -not (Test-Path -LiteralPath $GodotExecutable)) {
    throw "Godot executable was not found. Set GODOT_EXECUTABLE or pass -GodotExecutable."
}

function Stop-ProcessTree([System.Diagnostics.Process]$Process) {
    if ($env:OS -eq "Windows_NT") {
        & (Join-Path $env:SystemRoot "System32\taskkill.exe") /PID $Process.Id /T /F | Out-Null
        if ($LASTEXITCODE -eq 0) { return }
    }
    try { $Process.Kill($true) } catch { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue }
}

$project = Join-Path $root "examples\godot-gdscript"
$artifactRoot = Join-Path $root "artifacts"
$userDataRoot = Join-Path $root "build\godot-smoke-appdata"
$log = Join-Path $artifactRoot "godot-smoke.log"
$stdoutLog = Join-Path $artifactRoot "godot-smoke.stdout.log"
$stderrLog = Join-Path $artifactRoot "godot-smoke.stderr.log"
New-Item -ItemType Directory -Force $artifactRoot, $userDataRoot | Out-Null
Remove-Item -LiteralPath $log, $stdoutLog, $stderrLog -Force -ErrorAction SilentlyContinue

$arguments = @(
    "--disable-crash-handler",
    "--headless",
    "--path", "`"$project`"",
    "--scene", "res://GuaSmoke.tscn"
)
$startParameters = @{
    FilePath = $GodotExecutable
    ArgumentList = $arguments
    PassThru = $true
    RedirectStandardOutput = $stdoutLog
    RedirectStandardError = $stderrLog
}
if ($env:OS -eq "Windows_NT") { $startParameters.WindowStyle = "Hidden" }

# Godot 4.7 can access-violate during startup when its ordinary APPDATA-backed
# user:// location is unavailable. The child inherits this ignored writable path.
$previousAppData = $env:APPDATA
try {
    $env:APPDATA = $userDataRoot
    $process = Start-Process @startParameters
}
finally {
    if ($null -eq $previousAppData) { Remove-Item Env:APPDATA -ErrorAction SilentlyContinue }
    else { $env:APPDATA = $previousAppData }
}

if (-not $process.WaitForExit([int][Math]::Min([int]::MaxValue, $Timeout.TotalMilliseconds))) {
    Stop-ProcessTree $process
    throw "Godot smoke timed out after $Timeout. Logs: $stdoutLog, $stderrLog"
}
$godotOutput = @(
    @(Get-Content -LiteralPath $stdoutLog -ErrorAction SilentlyContinue)
    @(Get-Content -LiteralPath $stderrLog -ErrorAction SilentlyContinue)
)
Set-Content -LiteralPath $log -Value $godotOutput
if ($process.ExitCode -ne 0) {
    $godotOutput | Write-Host
    throw "Godot smoke failed with exit code $($process.ExitCode). Log: $log"
}

# A restricted Windows host may deny the system CA store. This offline smoke
# does not use TLS, so suppress only Godot's exact fallback diagnostic.
$filteredOutput = [System.Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $godotOutput.Count; $index++) {
    if ($godotOutput[$index] -eq "ERROR: Failed to read the root certificate store." -and
        $index + 1 -lt $godotOutput.Count -and
        $godotOutput[$index + 1].Trim() -like "at: get_system_ca_certificates*") {
        $index++
        continue
    }
    $filteredOutput.Add($godotOutput[$index])
}

if (-not ($filteredOutput -contains "Gua GDScript smoke passed.")) {
    $godotOutput | Write-Host
    throw "Godot GDScript smoke did not report successful completion. Log: $log"
}
if ($filteredOutput | Where-Object { $_ -match "^(ERROR:|SCRIPT ERROR:)" }) {
    $godotOutput | Write-Host
    throw "Godot GDScript smoke emitted an unexpected error. Log: $log"
}
$filteredOutput | Write-Host
