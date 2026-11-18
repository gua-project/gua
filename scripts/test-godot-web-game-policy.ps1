param(
    [Parameter(Mandatory)][string]$SdkDirectory,
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$ReleaseTemplate,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$OffOnly
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$sdk=(Resolve-Path $SdkDirectory).Path
$engine=(Resolve-Path $GodotExecutable).Path
$template=(Resolve-Path $ReleaseTemplate).Path.Replace('\','/')
if(Test-Path $OutputDirectory){throw 'Use a new Web acceptance directory.'}
$out=(New-Item -ItemType Directory $OutputDirectory).FullName
function Run-Editor($name,[string[]]$arguments) {
    $settings=@{FilePath=$engine;ArgumentList=$arguments;PassThru=$true;RedirectStandardOutput=(Join-Path $out "$name.stdout");RedirectStandardError=(Join-Path $out "$name.stderr")}
    if($IsWindows){$settings.WindowStyle='Hidden'}
    $p=Start-Process @settings
    if(!$p.WaitForExit(90000)){throw "Owned export Editor remains alive PID=$($p.Id); leave it running."}
    return $p.ExitCode
}
$cases=if($OffOnly){@('off')}else{@('off','testing','player','custom')}
foreach($case in $cases) {
    $mode=if($case -eq 'custom'){'off'}else{$case}
    $project=(New-Item -ItemType Directory (Join-Path $out "$case-project")).FullName
    Copy-Item (Join-Path $sdk 'addons') $project -Recurse
    Copy-Item (Join-Path $root 'tests/godot-web-game-policy/fixture.gd') $project
    @'
[gd_scene load_steps=2 format=3]
[ext_resource type="Script" path="res://fixture.gd" id="1"]
[node name="Fixture" type="Control"]
script=ExtResource("1")
'@|Set-Content (Join-Path $project 'Main.tscn')
    $setting=if($mode -eq 'off'){''}else{"[gua]`nremote_mode=`"$mode`""}
    @"
config_version=5
[application]
config/name="Gua Web game policy"
run/main_scene="res://Main.tscn"
config/features=PackedStringArray("4.7", "GL Compatibility")
[rendering]
renderer/rendering_method="gl_compatibility"
[editor_plugins]
enabled=PackedStringArray("res://addons/gua/plugin.cfg")
$setting
"@|Set-Content (Join-Path $project 'project.godot')
    $custom=if($case -eq 'custom'){'res://addons/gua/editor/web-custom-shell.html'}else{''}
    @"
[preset.0]
name="Web"
platform="Web"
runnable=true
export_filter="all_resources"
include_filter=""
exclude_filter=""
export_path=""
[preset.0.options]
custom_template/release="$template"
variant/extensions_support=true
variant/thread_support=false
html/custom_html_shell="$custom"
progressive_web_app/enabled=false
"@|Set-Content (Join-Path $project 'export_presets.cfg')
    $import=Run-Editor "$case-import" @('--headless','--editor','--path',('"'+$project+'"'),'--import')
    if($import -ne 0 -or (Get-Content (Join-Path $out "$case-import.stderr") -Raw) -match 'SCRIPT ERROR:'){throw "Owned Web import failed: $case"}
    $parse=Run-Editor "$case-parse" @('--headless','--path',('"'+$project+'"'),'--check-only','--script','res://fixture.gd')
    if($parse -ne 0 -or (Get-Content (Join-Path $out "$case-parse.stderr") -Raw) -match 'SCRIPT ERROR:'){throw "Actual Web fixture parser refused: $case"}
    $product=(New-Item -ItemType Directory (Join-Path $out $case)).FullName
    $html=Join-Path $product 'index.html'
    $export=Run-Editor "$case-export" @('--headless','--path',('"'+$project+'"'),'--export-release','Web',('"'+$html+'"'))
    if($export -ne 0 -or (Get-Content (Join-Path $out "$case-export.stderr") -Raw) -match 'SCRIPT ERROR:' -or !(Test-Path $html) -or !(Get-Content $html -Raw).Contains('new globalThis.__guaGameIntegrity.Engine(')){throw "Protected Web export failed: $case"}
    if((Get-FileHash (Join-Path $product 'gua.wasm')).Hash -ne (Get-FileHash (Join-Path $sdk "addons/gua/.web_variants/$mode/gua.wasm")).Hash){throw 'Wrong Web prebuilt packaged.'}
}
if(!$OffOnly) {
    # A refused rebuild must remove an older enabled HTML/PCK at the selected
    # destination. Require the specific diagnosis, not merely a CLI error.
    $originalProject=Join-Path $out 'off-project'
    foreach($case in @(
        @{name='unknown-custom';reason='Unverified custom HTML shell';from='html/custom_html_shell=""';to='html/custom_html_shell="res://unreviewed.html"'},
        @{name='threaded';reason='single-threaded Godot 4.7';from='variant/thread_support=false';to='variant/thread_support=true'},
        @{name='pwa';reason='single-threaded Godot 4.7';from='progressive_web_app/enabled=false';to='progressive_web_app/enabled=true'},
        @{name='reserved';reason='basename gua is reserved';from='';to=''}
    )) {
        $project=Join-Path $out ("refuse-"+$case.name+'-project')
        Copy-Item $originalProject $project -Recurse
        '<!doctype html><html><head><script>throw Error("Unreviewed")</script></head></html>' | Set-Content (Join-Path $project 'unreviewed.html')
        $preset=Join-Path $project 'export_presets.cfg'
        if($case.from){(Get-Content $preset -Raw).Replace($case.from,$case.to)|Set-Content $preset}
        $product=(New-Item -ItemType Directory (Join-Path $out ("refuse-"+$case.name))).FullName
        $basename=if($case.name -eq 'reserved'){'gua'}else{'index'}
        $html=Join-Path $product "$basename.html"
        Copy-Item (Join-Path $out 'testing/index.html') $html
        Copy-Item (Join-Path $out 'testing/index.pck') (Join-Path $product "$basename.pck")
        $null=Run-Editor ("refuse-"+$case.name) @('--headless','--path',('"'+$project+'"'),'--export-release','Web',('"'+$html+'"'))
        $diagnostic=(Get-Content (Join-Path $out ("refuse-"+$case.name+'.stderr')) -Raw)+(Get-Content (Join-Path $out ("refuse-"+$case.name+'.stdout')) -Raw)
        if(!$diagnostic.Contains($case.reason) -or (Test-Path $html) -or (Test-Path (Join-Path $product "$basename.pck"))){throw "Specific export refusal/stale-output removal failed: $($case.name)"}
    }
}
$global:LASTEXITCODE=0
