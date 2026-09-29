param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][ValidateSet('gua','__Internal')][string]$ExpectedLibrary
)
$ErrorActionPreference = 'Stop'
# Inspect metadata only: no native import or Unity runtime is executed.
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $AssemblyPath).Path)
$native = $assembly.GetType('Gua.Core.Native', $true)
$imports = @($native.GetMethods([Reflection.BindingFlags]'Static,NonPublic') |
    Where-Object { $_.Name.StartsWith('gua_observe_') })
if ($imports.Count -eq 0) { throw 'No Observe native imports were found.' }
foreach ($method in $imports) {
    $import = $method.GetCustomAttributes([Runtime.InteropServices.DllImportAttribute], $false)
    if ($import.Count -ne 1 -or $import[0].Value -ne $ExpectedLibrary) {
        throw "Incorrect native library for $($method.Name): expected $ExpectedLibrary."
    }
}
Write-Output "Validated $($imports.Count) Observe imports targeting $ExpectedLibrary."
