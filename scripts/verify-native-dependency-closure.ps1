param(
    [Parameter(Mandatory = $true)][string]$NativeDirectory,
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')][string]$Rid,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$files = switch ($Rid) {
    'win-x64' { @('gua.dll', 'gua_runtime.dll') }
    'linux-x64' { @('libgua.so', 'libgua_runtime.so') }
    default { @('libgua.dylib', 'libgua_runtime.dylib') }
}
if ($Rid -eq 'win-x64') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $dumpbin = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC/Tools/MSVC/**/bin/Hostx64/x64/dumpbin.exe' | Select-Object -First 1
    if (-not $dumpbin) { throw 'dumpbin is required for Windows dependency closure.' }
}
$evidence = foreach ($name in $files) {
    $path = (Resolve-Path -LiteralPath (Join-Path $NativeDirectory $name)).Path
    $details = switch ($Rid) {
        'win-x64' { (& $dumpbin /dependents $path) -join "`n" }
        'linux-x64' { (& ldd $path) -join "`n" }
        default { (& otool -L $path) -join "`n" }
    }
    if ($LASTEXITCODE -ne 0) { throw "Dependency inspection failed: $name" }
    if ($Rid -eq 'win-x64') {
        $dependencies = @([regex]::Matches($details, '(?im)^\s+([a-z0-9_.-]+\.dll)\s*$') | ForEach-Object { $_.Groups[1].Value })
        if ($dependencies.Count -eq 0) { throw "No native dependencies inspected: $name" }
        foreach ($dependency in $dependencies) {
            if ($dependency -notmatch '(?i)^(KERNEL32|WS2_32|ADVAPI32|USER32|MSVCP140(?:_\d+)?|VCRUNTIME140(?:_\d+)?|ucrtbase|api-ms-win-[a-z0-9-]+)\.dll$') {
                throw "Undeclared Windows native dependency: $dependency"
            }
        }
    } elseif ($Rid -eq 'linux-x64') {
        if ($details -match 'not found') { throw "Unresolved Linux native dependency: $name" }
        $dependencies = @([regex]::Matches($details, '(?m)^\s*(\S+)\s+(?:=>|\()') | ForEach-Object { $_.Groups[1].Value })
        foreach ($dependency in $dependencies) {
            if ($dependency -notmatch '^(linux-vdso\.so\.1|lib(?:c|m|pthread|rt|dl)\.so\.\d+|libstdc\+\+\.so\.6|libgcc_s\.so\.1|/[^\s]+/ld-linux-x86-64\.so\.2)$') {
                throw "Undeclared Linux native dependency: $dependency"
            }
        }
    } else {
        $dependencies = @([regex]::Matches($details, '(?m)^\s+(\S+)\s+\(') | ForEach-Object { $_.Groups[1].Value })
        foreach ($dependency in $dependencies) {
            if ($dependency -notmatch '^(/usr/lib/|/System/Library/|@rpath/libgua(?:_runtime)?\.dylib$)') {
                throw "Undeclared macOS native dependency: $dependency"
            }
        }
    }
    [ordered]@{ file = $name; sha256 = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant(); dependencies = $dependencies; inspection = $details }
}
[ordered]@{ rid = $Rid; files = @($evidence) } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
