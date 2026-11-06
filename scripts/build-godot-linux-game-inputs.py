"""Provider producer only; consumers use the prebuilts during normal export."""
import argparse
import importlib.util
import json
import os
import platform
from pathlib import Path
import re
import shutil
import subprocess
import sys
_stage_spec=importlib.util.spec_from_file_location('gua_linux_stage',Path(__file__).with_name('stage-godot-linux-game-sdk.py'))
_stage=importlib.util.module_from_spec(_stage_spec)
_stage_spec.loader.exec_module(_stage)
identity=_stage.identity

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--godot-cpp-source',type=Path)
    parser.add_argument('--product-version')
    parser.add_argument('--parallel',type=int,default=2)
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    if sys.platform!='linux' or os.uname().machine!='x86_64':raise ValueError('This producer targets Linux x86_64.')
    if args.output.exists():raise ValueError('Use a new producer output directory.')
    if not 1<=args.parallel<=8:raise ValueError('Invalid build parallelism.')
    if args.product_version and not re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?',args.product_version):raise ValueError('Explicit semantic version required.')
    source=subprocess.check_output(['git','-C',str(root),'rev-parse','HEAD'],text=True).strip()
    build=args.build.resolve();out=args.output.resolve();out.mkdir(parents=True)
    env=os.environ.copy();env['PYTHONDONTWRITEBYTECODE']='1'
    info=[]
    for mode,flavor,remote,ceiling in [('off','shipping','OFF','none'),('testing','qa','ON','debug'),('player','ai-player','ON','player')]:
        config=['cmake','-S',str(root),'-B',str(build),'-G','Unix Makefiles','-DCMAKE_BUILD_TYPE=Release',
                '-DGUA_BUILD_GODOT=ON','-DGUA_BUILD_EXAMPLES=OFF','-DGUA_GODOT_CONFIGURATION=Release','-DGUA_STAGE_IN_SAMPLE=OFF',
                '-DGUA_DISTRIBUTION_FLAVOR='+flavor,'-DGUA_ENABLE_REMOTE_AUTOMATION='+remote,'-DGUA_BUILD_WS_BRIDGE='+remote,
                '-DGUA_BUILD_ID='+source,'-DGODOTCPP_BUILD_PROFILE='+str(root/'bindings/godot/game-build/web-build-profile.json')]
        if args.godot_cpp_source:config.append('-DFETCHCONTENT_SOURCE_DIR_GODOT-CPP='+str(args.godot_cpp_source.resolve()))
        if args.product_version:config+=['-DGUA_VERSION='+args.product_version,'-DGUA_GODOT_PLUGIN_VERSION='+args.product_version]
        for name,command in [('configure',config),('build',['cmake','--build',str(build),'--target','gua-godot','gua-godot-policy-template','--parallel',str(args.parallel)])]:
            with (out/(mode+'-'+name+'.log')).open('w') as log:
                subprocess.run(command,env=env,stdout=log,stderr=subprocess.STDOUT,check=True)
        native=build/'stage/addons/gua/bin/gua_godot.linux.release.x86_64.so'
        actual=identity(native)
        if (actual.get('sourceCommit')!=source or actual.get('buildId')!=source or actual.get('flavor')!=flavor
            or actual.get('configuration')!='Release' or actual.get('remoteEnabled')!=(remote=='ON')
            or actual.get('remotePermissionCeiling')!=ceiling):raise ValueError('Actual Linux GDE variant differs from requested producer configuration.')
        shutil.copyfile(native,out/(mode+'.so'));info.append({'mode':mode,'build':actual})
    shutil.copyfile(build/'native/gua-game-policy/GodotPolicyTemplate.so',out/'GodotPolicyTemplate.so')
    (out/'identities.json').write_text(json.dumps(info,indent=2)+'\n')
    environment={'os':platform.platform(),'machine':platform.machine(),'python':sys.version,
                 'compiler':subprocess.check_output(['c++','--version'],text=True).splitlines()[0],
                 'cmake':subprocess.check_output(['cmake','--version'],text=True).splitlines()[0]}
    (out/'environment.json').write_text(json.dumps(environment,indent=2)+'\n')
    print(json.dumps({'result':'pass','nativeSourceCommit':source,'modes':3,'output':str(out)}))

if __name__=='__main__':main()
