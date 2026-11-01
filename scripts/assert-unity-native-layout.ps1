param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('Windows','Linux','macOS','WebGL')][string]$Platform,
    [Parameter(Mandatory)][ValidateSet('on','off')][string]$Remote
)
$ErrorActionPreference='Stop'
$names = switch ($Platform) {
    Windows { @('gua.dll','gua_runtime.dll') }
    Linux { @('libgua.so','libgua_runtime.so') }
    macOS { @('libgua.dylib','libgua_runtime.dylib') }
    WebGL { @('libgua-core.a','libgua_runtime.a') }
}
$allowed=@('gua-build-manifest.json','gua-build-manifest.json.meta') + $names + @($names | ForEach-Object { "$_.meta" })
if ($Platform -eq 'WebGL') {
    $allowed += @('Managed.meta','Managed/Gua.Core.dll','Managed/Gua.Runtime.dll','Managed/Gua.Core.dll.meta','Managed/Gua.Runtime.dll.meta')
    if ($Remote -eq 'on') { $allowed += @('GuaWebMcp.jslib','GuaWebMcp.jslib.meta') }
}
$nativeRoot=[IO.Path]::GetFullPath($Directory)
if ((Get-Item -LiteralPath $nativeRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unity native package cannot contain links.' }
foreach ($item in Get-ChildItem -LiteralPath $nativeRoot -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unity native package cannot contain links.' }
    if ($item.PSIsContainer) { continue }
    $relative=[IO.Path]::GetRelativePath($nativeRoot,$item.FullName).Replace('\','/')
    if ($allowed -cnotcontains $relative) { throw "Unexpected Unity native package file: $relative" }
}
