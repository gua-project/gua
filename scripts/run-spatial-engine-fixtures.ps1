param(
    [string]$GodotExecutable = $env:GODOT_EXECUTABLE,
    [string]$UnityExecutable = 'C:\Program Files\Unity\Hub\Editor\6000.5.3f1\Editor\Unity.exe',
    [string]$GeneratorInstance = 'C:/Program Files/Microsoft Visual Studio/18/Community,version=18.10.12217.157',
    [string]$GodotCppSource = '',
    [string]$GodotBackend = "GodotPhysics3D",
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!(Test-Path -LiteralPath $GodotExecutable) -or !(Test-Path -LiteralPath $UnityExecutable)) { throw 'Both pinned real engine executables are required.' }
if ($TimeoutSeconds -le 0) { throw 'Timeout must be positive.' }
Push-Location $root
try {
    $cmakeArgs = @('-S','.', '-B','build/spatial-godot','-G','Visual Studio 18 2026','-A','x64',
        "-DCMAKE_GENERATOR_INSTANCE=$GeneratorInstance",'-DGUA_BUILD_GODOT=ON','-DGUA_BUILD_WS_BRIDGE=OFF',
        '-DGUA_BUILD_EXAMPLES=OFF','-DGUA_GODOT_CONFIGURATION=Release')
    if ($GodotCppSource) { $cmakeArgs += "-DFETCHCONTENT_SOURCE_DIR_GODOT-CPP=$GodotCppSource" }
    & cmake @cmakeArgs
    if ($LASTEXITCODE) { throw 'CMake configure failed.' }
    & cmake --build build/spatial-godot --config Release --target gua gua-godot --parallel 4
    if ($LASTEXITCODE) { throw 'Native engine fixture build failed.' }
    & dotnet build bindings/dotnet/src/Gua.Core/Gua.Core.csproj -c Release -f netstandard2.1
    if ($LASTEXITCODE) { throw 'Managed fixture build failed.' }
    $godot = Join-Path $root 'artifacts/godot-spatial'
    $unity = Join-Path $root 'artifacts/unity-spatial'
    New-Item -ItemType Directory -Force "$godot/addons/gua/bin", "$unity/Assets/Editor", "$unity/Assets/Plugins", "$unity/Packages", "$unity/ProjectSettings" | Out-Null
    foreach ($prior in "$godot/evidence.json", "$unity/evidence.json", "$root/artifacts/godot-run.log", "$root/artifacts/unity-spatial.log") {
        if (Test-Path -LiteralPath $prior) { Remove-Item -LiteralPath $prior -Force }
    }
    Copy-Item examples/spatial-fixtures/godot/* $godot -Force
    $project = Get-Content "$godot/project.godot" -Raw
    $project.Replace('3d/physics_engine="GodotPhysics3D"', "3d/physics_engine=`"$GodotBackend`"") | Set-Content "$godot/project.godot"
    Copy-Item protocol/fixtures/spatial-engine-r1.json $godot -Force
    Copy-Item examples/godot-gdscript/addons/gua/gua_spatial.gd "$godot/addons/gua" -Force
    Copy-Item examples/godot-gdscript/addons/gua/gua.gdextension "$godot/addons/gua" -Force
    $library = 'examples/godot-gdscript/addons/gua/bin/gua_godot.windows.release.x86_64.dll'
    Copy-Item $library "$godot/addons/gua/bin" -Force
    # Headless editor import uses the debug descriptor, with the same release ABI.
    Copy-Item $library "$godot/addons/gua/bin/gua_godot.windows.debug.x86_64.dll" -Force
    Copy-Item bindings/unity/Runtime/GuaUnitySpatial.cs "$unity/Assets" -Force
    Copy-Item examples/spatial-fixtures/unity/SpatialProfile.cs "$unity/Assets" -Force
    Copy-Item examples/spatial-fixtures/unity/SpatialFixture.cs "$unity/Assets/Editor" -Force
    Copy-Item protocol/fixtures/spatial-engine-r1.json $unity -Force
    Copy-Item bindings/dotnet/src/Gua.Core/bin/Release/netstandard2.1/Gua.Core.dll "$unity/Assets/Plugins" -Force
    Copy-Item build/spatial-godot/native/gua-core/Release/gua.dll "$unity/Assets/Plugins" -Force
    & "$PSScriptRoot/copy-unity-managed-closure.ps1" -AssetsFile bindings/dotnet/src/Gua.Core/obj/project.assets.json -TargetFramework netstandard2.1 -Destination "$unity/Assets/Plugins"
    '{"dependencies":{"com.unity.modules.physics":"1.0.0"}}' | Set-Content "$unity/Packages/manifest.json"
    'm_EditorVersion: 6000.5.3f1' | Set-Content "$unity/ProjectSettings/ProjectVersion.txt"
    function Invoke-Engine([string]$Executable, [string[]]$Arguments, [string]$Log) {
        $engineProcess = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
        if (!$engineProcess.WaitForExit($TimeoutSeconds * 1000)) {
            & "$env:SystemRoot/System32/taskkill.exe" /PID $engineProcess.Id /T /F | Out-Null
            throw "Engine timed out. See $Log"
        }
        if ($engineProcess.ExitCode -ne 0) { throw "Engine failed ($($engineProcess.ExitCode)). See $Log" }
    }
    Invoke-Engine $GodotExecutable @('--headless','--path',"`"$godot`"",'--editor','--import','--quit','--log-file',"`"$root/artifacts/godot-import.log`"") "$root/artifacts/godot-import.log"
    Invoke-Engine $GodotExecutable @('--headless','--path',"`"$godot`"",'--max-fps','60','--quit-after','10000','--log-file',"`"$root/artifacts/godot-run.log`"") "$root/artifacts/godot-run.log"
    Invoke-Engine $UnityExecutable @('-batchmode','-nographics','-projectPath',"`"$unity`"",'-executeMethod','SpatialFixture.Run','-logFile',"`"$root/artifacts/unity-spatial.log`"") "$root/artifacts/unity-spatial.log"
    foreach ($output in "$godot/evidence.json", "$unity/evidence.json") {
        if (!(Test-Path -LiteralPath $output)) { throw "Missing real-engine evidence: $output" }
        $evidence = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
        if ($evidence.results.Count -ne (2 * (Get-Content protocol/fixtures/spatial-engine-r1.json -Raw | ConvertFrom-Json).cases.Count + 1)) { throw "Incomplete engine fixture evidence: $output" }
        if ($evidence.profile.Count -ne 360) { throw "Incomplete real physics-callback profile: $output" }
        if ($evidence.leaseRaces.Count -ne ((Get-Content protocol/fixtures/spatial-engine-r1.json -Raw | ConvertFrom-Json).cases.Count + 1)) { throw "Missing selective lease race evidence: $output" }
        Write-Host "Real engine evidence: $output"
    }
} finally { Pop-Location }
