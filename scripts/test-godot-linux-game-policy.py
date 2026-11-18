"""Actual Linux Release exports, wire input and SDK-only substitution tests."""
import argparse
import fcntl
import hashlib
import json
import os
import platform
from pathlib import Path
import shutil
import subprocess
from linux_game_load_observer import observe

def digest(path):
    with path.open('rb') as source:return hashlib.file_digest(source,'sha256').hexdigest()

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ['sdk','godot-editor','release-template','output']:
        parser.add_argument('--'+name,required=True,type=Path)
    args=parser.parse_args()
    sdk=args.sdk.resolve(strict=True);editor=args.godot_editor.resolve(strict=True)
    template=args.release_template.resolve(strict=True);out=args.output.resolve()
    if out.exists():raise ValueError('Use a new acceptance directory.')
    out.mkdir(parents=True)
    (out/'environment.json').write_text(json.dumps({'os':platform.platform(),'machine':platform.machine(),
        'editor':str(editor),'editorHost':'Windows cross-export' if editor.suffix.lower()=='.exe' else 'Linux native',
        'releaseTemplate':str(template)},indent=2)+'\n')
    root=Path(__file__).resolve().parents[1]
    def engine_path(path):
        if editor.suffix.lower()=='.exe':
            return subprocess.check_output(['wslpath','-w',str(path)],text=True).strip().replace('\\','/')
        return str(path)
    def editor_run(project,tail,path):
        with path.open('w') as log:
            process=subprocess.Popen([str(editor),'--headless','--path',engine_path(project),*tail],stdout=log,stderr=subprocess.STDOUT)
            try:return process.wait(timeout=120)
            except subprocess.TimeoutExpired:
                # Never force an Editor to exit, including an owned headless
                # fixture. Leave it alive and report the exact process/log.
                raise RuntimeError(f'Owned Editor still running; not terminated; PID={process.pid}; log={path}') from None
    def export(project):
        for name,tail in [('import',['--editor','--import','--quit']),('export',['--export-release','Linux'])]:
            result=editor_run(project,tail,project/(name+'.log'))
            if result:raise RuntimeError('Godot '+name+' failed; see '+str(project/(name+'.log')))
    def environment(project,mode):
        env=os.environ.copy()
        env.update(XDG_DATA_HOME=str(project/'userdata'),XDG_CONFIG_HOME=str(project/'config'),XDG_CACHE_HOME=str(project/'cache'))
        env.pop('GUA_OBSERVATION_PROFILE',None) # Compile-time mode supplies defaults.
        return env
    def run(project,mode,before=None):
        result=observe(project/'out/game.x86_64',project/'game.log',environment(project,mode),before_sdk_load=before)
        (project/'observation.json').write_text(json.dumps(result,indent=2)+'\n')
        return result,(project/'game.log').read_text(errors='replace')
    fixture=(root/'native/gua-godot/tests/game_owned_policy_fixture.gd').read_text().replace('19469','19589')
    fixture=fixture.replace('_check(GuaGamePolicy.verified)','print("LINUX_GAME_CALLBACK_ENTERED")\n\t_check(GuaGamePolicy.verified)\n\tvar save := FileAccess.open("user://local-save.txt", FileAccess.WRITE)\n\t_check(save != null)\n\tsave.store_string("local-save-ok")\n\tsave.close()\n\t_check(FileAccess.get_file_as_string("user://local-save.txt") == "local-save-ok")')
    fixture=fixture.replace('var started: bool = adapter.start_inspector_bridge(19589)','adapter.update("linux-policy-ready")\n\tvar started: bool = adapter.context.start_inspector_bridge(19589)')
    # The same compatibility caller must stay OFF even with SDK substitution.
    # Delay this assertion until after real wire input so the old-path mutation
    # detects the actual contract violation, rather than an unrelated failure.
    fixture=fixture.replace('_check(started == (GuaGamePolicy.REMOTE_MODE != "off"))','print("LINUX_REMOTE_START=", started)')
    fixture=fixture.replace('adapter.dispose()','_check(started == (GuaGamePolicy.REMOTE_MODE != "off"))\n\tadapter.dispose()')
    fixture=fixture.replace('context.emit_action_result(', 'print("LINUX_WIRE_INPUT_CONSUMED=1")\n\t\tcontext.emit_action_result(')
    fixture=fixture.replace('if GuaGamePolicy.REMOTE_MODE == "player":','if GuaGamePolicy.REMOTE_MODE == "player":\n\t\tcontext.set_observation_profile(0)')
    fixture=fixture.replace('_check(not DirAccess.dir_exists_absolute("res://addons/gua/.variants"))','_check(not DirAccess.dir_exists_absolute("res://addons/gua/.variants"))\n\t\t_check(not DirAccess.dir_exists_absolute("res://addons/gua/.linux_variants"))')
    results=[]
    for mode in ['off','testing','player']:
        project=out/mode;(project/'out').mkdir(parents=True)
        shutil.copytree(sdk/'addons',project/'addons')
        (project/'fixture.gd').write_text(fixture)
        (project/'project.godot').write_text(f'''config_version=5
[application]
config/name="Gua Linux policy {mode}"
run/main_scene="res://fixture.tscn"
run/max_fps=120
[editor_plugins]
enabled=PackedStringArray("res://addons/gua/plugin.cfg")
[gua]
remote_mode="{mode}"
[rendering]
renderer/rendering_method="gl_compatibility"
''')
        (project/'fixture.tscn').write_text('[gd_scene load_steps=2 format=3]\n[ext_resource type="Script" path="res://fixture.gd" id="1"]\n[node name="Fixture" type="Control"]\nscript=ExtResource("1")\n')
        (project/'export_presets.cfg').write_text(f'''[preset.0]
name="Linux"
platform="Linux"
runnable=true
export_filter="all_resources"
include_filter=""
exclude_filter=""
export_path="out/game.x86_64"
[preset.0.options]
custom_template/release={json.dumps(engine_path(template))}
binary_format/architecture="x86_64"
binary_format/embed_pck=false
''')
        export(project)
        game=project/'out/game.x86_64';game.chmod(game.stat().st_mode|0o100)
        result,text=run(project,mode)
        expected=digest(project/'out/gua-native/gua.so')
        assert result['exitCode']==0 and result['sdkMappings']==1 and result['sdkHashes']==[expected],(mode,result,text)
        assert 'GAME_OWNED_GODOT_SUCCESS: '+mode in text and 'GamePolicy: sealed files verified before SDK load.' in text
        assert ('LINUX_REMOTE_START=true' in text)==(mode!='off')
        results.append({'case':mode,**result,'wireInputCount':0 if mode=='off' else 1,'localSave':True})
    on=sdk/'addons/gua/.linux_variants/testing/addons/gua/bin/gua_godot.linux.release.x86_64.so'
    for name,racing in [('swap-before-start',False),('swap-after-verification',True)]:
        project=out/name;shutil.copytree(out/'off/out',project/'out')
        target=project/'out/gua-native/gua.so';original=digest(target);denied=[]
        if not racing:shutil.copyfile(on,target)
        def swap(pid,fd):
            maps=Path(f'/proc/{pid}/maps').read_text()
            assert '/memfd:gua-game-verified' not in maps
            descriptor=os.open(fd,os.O_RDWR)
            try:
                assert fcntl.fcntl(descriptor,fcntl.F_GET_SEALS)&15==15
                for operation,mutate in [('write',lambda:os.pwrite(descriptor,b'x',0)),('shrink',lambda:os.ftruncate(descriptor,0)),('grow',lambda:os.ftruncate(descriptor,target.stat().st_size+1))]:
                    try:mutate()
                    except PermissionError:denied.append(operation)
                    else:raise AssertionError('Immutable SDK mutation allowed: '+operation)
            finally:os.close(descriptor)
            shutil.copyfile(on,target) # Original pathname replacement succeeds.
            assert digest(target)==digest(on)
        result,text=run(project,'off',swap if racing else None)
        if racing:
            assert result['exitCode']==0 and result['sdkMappings']==1 and result['sdkHashes']==[original] and result['loaderBarriers']==1,(result,text)
            assert set(denied)=={'write','shrink','grow'} and 'GAME_OWNED_GODOT_SUCCESS: off' in text and 'LINUX_REMOTE_START=false' in text
        else:
            assert result['exitCode']!=0 and result['sdkMappings']==0,(result,text)
            assert 'GamePolicy: integrity verification refused.' in text and 'LINUX_GAME_CALLBACK_ENTERED' not in text
        results.append({'case':name,**result,'sealedMutationsDenied':denied})
    # Violation oracle: only this disposable GAME verifier is changed to load
    # the original mutable pathname after checking/sealing. The same OFF game
    # must detect real wire input becoming possible under a genuine ON swap.
    project=out/'old-path-oracle';shutil.copytree(out/'off/out',project/'out')
    target=project/'out/gua-native/gua.so'
    guard=next((project/'out/gua-native').glob('GameSecurity_Godot_*.so'))
    source=(root/'native/gua-game-policy/godot_policy_linux.cpp').read_text()
    marker='std::string fdpath="/proc/self/fd/"+std::to_string(pinned);'
    assert source.count(marker)==1
    mutant=project/'mutant.cpp';mutant.write_text(source.replace(marker,'std::string fdpath=path;'))
    compiled=project/'mutant.so'
    subprocess.run(['g++','-D_GNU_SOURCE','-std=c++20','-O2','-fPIC','-shared','-fvisibility=hidden','-static-libgcc','-static-libstdc++',str(mutant),'-ldl','-Wl,--exclude-libs,ALL','-Wl,-z,now','-o',str(compiled)],check=True,timeout=60)
    tag=b'GUAGDPOLICYSLOT1';original=guard.read_bytes();image=bytearray(compiled.read_bytes())
    before=original.find(tag);after=image.find(tag)
    assert before>=0 and after>=0 and original.count(tag)==1 and image.count(tag)==1
    image[after:after+65536]=original[before:before+65536];guard.write_bytes(image)
    def bad_swap(_pid,_fd):shutil.copyfile(on,target)
    result=observe(project/'out/game.x86_64',project/'game.log',environment(project,'off'),before_sdk_load=bad_swap,original_loader_path=target)
    text=(project/'game.log').read_text(errors='replace')
    assert result['exitCode']!=0 and result['loaderBarriers']==1 and result['sdkMappings']==1 and result['sdkHashes']==[digest(on)],(result,text)
    assert 'LINUX_REMOTE_START=true' in text and 'LINUX_WIRE_INPUT_CONSUMED=1' in text and 'GAME_OWNED_GODOT_SUCCESS: off' not in text
    (project/'observation.json').write_text(json.dumps(result,indent=2)+'\n')
    results.append({'case':'old-path-oracle',**result,'realForbiddenWireInputObserved':True,'normalAcceptanceFailedAsRequired':True})
    project=out/'preloaded-alias';shutil.copytree(out/'off/out',project/'out')
    alias=project/'legitimate-renamed.so';shutil.copyfile(on,alias)
    env=environment(project,'off');env['LD_PRELOAD']=str(alias)
    result=observe(project/'out/game.x86_64',project/'game.log',env)
    text=(project/'game.log').read_text(errors='replace')
    assert result['exitCode']!=0 and result['sdkMappings']==1 and result['sdkHashes']==[digest(on)],(result,text)
    assert 'GamePolicy: integrity verification refused.' in text and 'LINUX_GAME_CALLBACK_ENTERED' not in text
    # A memfd-only observer would incorrectly report native0 here. The actual
    # renamed pathname load MUST count, even though verification later refuses.
    (project/'observation.json').write_text(json.dumps(result,indent=2)+'\n')
    results.append({'case':'preloaded-alias',**result,'preexistingSdkLoadDetected':True,'gameCallbackReached':False})
    project=out/'player-debug-environment';shutil.copytree(out/'player/out',project/'out')
    env=environment(project,'player');env['GUA_OBSERVATION_PROFILE']='debug'
    result=observe(project/'out/game.x86_64',project/'game.log',env)
    text=(project/'game.log').read_text(errors='replace')
    assert result['sdkMappings']==1 and result['sdkHashes']==[digest(project/'out/gua-native/gua.so')],(result,text)
    if result['exitCode']==0:
        # A future implementation may clamp rather than refuse. The same
        # Player fixture checks API/profile, hidden UI and remote clock denial.
        assert 'GAME_OWNED_GODOT_SUCCESS: player' in text
        behavior='player-ceiling-maintained'
    else:
        assert 'GuaContext failed to create a Gua runtime' in text
        assert 'Gua WebSocket bridge listening' not in text and 'LINUX_WIRE_INPUT_CONSUMED=1' not in text
        behavior='runtime-initialization-refused'
    (project/'observation.json').write_text(json.dumps(result,indent=2)+'\n')
    results.append({'case':'player-debug-environment',**result,'behavior':behavior})
    project=out/'failed-off-over-enabled';shutil.copytree(out/'testing',project)
    settings=project/'project.godot';settings.write_text(settings.read_text().replace('remote_mode="testing"','remote_mode="off"'))
    (project/'addons/gua/.linux_variants/off/addons/gua/bin/gua_godot.linux.release.x86_64.so').unlink()
    failed=editor_run(project,['--export-release','Linux'],project/'failed-export.log')
    assert failed!=0,'Linux export incorrectly succeeded after failed OFF generation.'
    game=project/'out/game.x86_64'
    if game.exists():
        result,text=run(project,'off')
        assert result['sdkMappings']==0 and 'LINUX_GAME_CALLBACK_ENTERED' not in text and 'GAME_OWNED_GODOT_SUCCESS' not in text,(result,text)
        behavior='invalid-game-refused-before-sdk'
    else:
        assert not (project/'out/game.pck').exists()
        behavior='stale-game-removed'
    results.append({'case':'failed-off-over-enabled','exportExitCode':failed,'behavior':behavior})
    (out/'results.json').write_text(json.dumps(results,indent=2)+'\n')
    print(json.dumps({'result':'pass','cases':len(results),'output':str(out)}))

if __name__=='__main__':main()
