param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Use a new portable test-tools directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
$editor=if($IsWindows){@{name='Godot_v4.7-stable_win64.exe.zip';hash='02a5312236f4e0209c78bcb2f52135b1963e6b8888c873c9cee81459e60bcd71'}}else{@{name='Godot_v4.7-stable_linux.x86_64.zip';hash='0b1a6c54c2c619c12e169fe9241edda4b81080b519451cec2984bf0d2c6cb73c'}}
$inputs=@($editor,@{name='Godot_v4.7-stable_export_templates.tpz';hash='9714459dc071907c0f3d5f17d608faf69e7cda21331fc5d39c4503ffa4e99eec'})
foreach($entry in $inputs){
    $path=Join-Path $out $entry.name
    Invoke-WebRequest "https://github.com/godotengine/godot-builds/releases/download/4.7-stable/$($entry.name)" -OutFile $path
    if((Get-FileHash $path).Hash.ToLowerInvariant() -ne $entry.hash){throw 'Official Web test input digest mismatch.'}
}
Expand-Archive (Join-Path $out $editor.name) (Join-Path $out 'editor')
if(!$IsWindows){& chmod +x (Join-Path $out 'editor/Godot_v4.7-stable_linux.x86_64');if($LASTEXITCODE){throw 'Cannot mark the owned portable Editor executable.'}}
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $out $inputs[1].name))
try{
    $entries=@($archive.Entries|Where-Object FullName -CEQ 'templates/web_dlink_nothreads_release.zip')
    if($entries.Count -ne 1){throw 'Missing/duplicate official single-thread Web template.'}
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],(Join-Path $out 'web-release-dlink.zip'))
}finally{$archive.Dispose()}
