param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Use a new portable test-tools directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
$base='https://github.com/godotengine/godot-builds/releases/download/4.7-stable'
$inputs=@(
    @{Name='Godot_v4.7-stable_mono_win64.zip';Hash='73087f2ef4940be2c0bff358280053912182aca82b85891d6e42d9ebc5c26880'},
    @{Name='Godot_v4.7-stable_mono_export_templates.tpz';Hash='4c02a0b99ad9c5bc243c2e79468628db3df89350d9db8fc995988f69d126e069'}
)
foreach($inputFile in $inputs) {
    $path=Join-Path $out $inputFile.Name
    Invoke-WebRequest "$base/$($inputFile.Name)" -OutFile $path
    if((Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $inputFile.Hash){throw 'Official Godot .NET test input digest mismatch.'}
}
Expand-Archive (Join-Path $out $inputs[0].Name) (Join-Path $out 'editor')
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $out $inputs[1].Name))
try {
    foreach($name in 'windows_release_x86_64.exe','windows_debug_x86_64.exe') {
        $entries=@($archive.Entries|Where-Object FullName -EQ "templates/$name")
        if($entries.Count -ne 1){throw 'Missing/duplicate official Windows template.'}
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],(Join-Path $out $name))
    }
} finally {$archive.Dispose()}
Write-Host "Official portable Godot .NET 4.7 test inputs: $out"
