"""Provider-only staging. Games consume these prebuilts through normal export."""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()

def identity(path):
    # Dedicated short-lived probe: staging never initializes Godot/the bridge.
    code='''import ctypes,json,sys
lib=ctypes.CDLL(sys.argv[1]);f=lib.gua_godot_copy_build_info_json
f.argtypes=[ctypes.c_void_p,ctypes.c_int];f.restype=ctypes.c_int
b=ctypes.create_string_buffer(16384);n=f(b,len(b))
assert 0<n<len(b)
print(b.value.decode())
'''
    return json.loads(subprocess.check_output([sys.executable,'-c',code,str(path)],text=True,timeout=30))

def verify_dependencies(path):
    # These are OS/compiler runtime dependencies, not exchangeable Gua modules.
    # No SDK $ORIGIN/search-path dependency or loader audit/filter is permitted.
    env=os.environ.copy();env['LC_ALL']='C'
    dynamic=subprocess.check_output(['readelf','--dynamic','--wide',str(path)],env=env,text=True,timeout=30)
    forbidden=['RPATH','RUNPATH','AUDIT','DEPAUDIT','FILTER','AUXILIARY']
    if any('('+tag+')' in dynamic for tag in forbidden):raise ValueError('SDK has an unconstrained ELF loader path/hook.')
    allowed={'libstdc++.so.6','libm.so.6','libgcc_s.so.1','libc.so.6','ld-linux-x86-64.so.2','libpthread.so.0','librt.so.1'}
    needed=re.findall(r'\(NEEDED\).*?\[([^\]]+)\]',dynamic)
    if not needed or any(name not in allowed for name in needed):raise ValueError('Unexpected Linux SDK dependency.')
    return needed

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for mode in ['off','testing','player']:
        parser.add_argument('--'+mode,required=True,type=Path)
    parser.add_argument('--template',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    if not sys.platform.startswith('linux'):
        raise ValueError('Stage Linux native inputs on Linux so binary identity can be checked.')
    if args.output.exists():raise ValueError('Use a new SDK staging directory.')
    root=Path(__file__).resolve().parents[1]
    inputs=[];source=None;build_id=None
    for mode,flavor,ceiling in [('off','shipping','none'),('testing','qa','debug'),('player','ai-player','player')]:
        raw=getattr(args,mode)
        if raw.is_symlink():raise ValueError('Linked provider input.')
        path=raw.resolve(strict=True)
        if not path.is_file():raise ValueError('Not a plain provider input.')
        verify_dependencies(path)
        build=identity(path)
        source=source or build['sourceCommit']
        build_id=build_id or build['buildId']
        if (build.get('schemaVersion')!=1 or build.get('configuration')!='Release' or build.get('flavor')!=flavor
            or build.get('remoteEnabled')!=(mode!='off') or build.get('remotePermissionCeiling')!=ceiling
            or build.get('sourceCommit')!=source or len(source)!=40 or any(c not in '0123456789abcdef' for c in source)
            or not build.get('buildId') or build.get('buildId')!=build_id):raise ValueError('Linux provider identities/modes do not match.')
        inputs.append((mode,path,build))
    template=args.template.resolve(strict=True)
    image=template.read_bytes()
    if image[:6]!=b'\x7fELF\x02\x01' or int.from_bytes(image[16:18],'little')!=3 or int.from_bytes(image[18:20],'little')!=62:
        raise ValueError('Expected an ELF64 x86_64 shared verifier template.')
    marker=b'GUAGDPOLICYSLOT1';slot=image.find(marker)
    if slot<0 or image.count(marker)!=1 or slot+65536>len(image):raise ValueError('Missing/ambiguous template slot.')
    verify_dependencies(template)
    # No staging output is written until all identities are known coherent.
    addon=args.output/'addons/gua';editor=addon/'editor';editor.mkdir(parents=True)
    variants=addon/'.linux_variants';variants.mkdir();(variants/'.gdignore').write_text('')
    for mode,path,build in inputs:
        package=variants/mode;native=package/'addons/gua/bin/gua_godot.linux.release.x86_64.so'
        native.parent.mkdir(parents=True);shutil.copyfile(path,native)
        if identity(native)!=build:raise ValueError('Copied SDK identity changed during staging.')
        for name in ['gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd']:
            shutil.copyfile(root/'examples/godot-gdscript/addons/gua'/name,package/'addons/gua'/name)
        files=[{'path':file.relative_to(package).as_posix(),'sha256':sha(file)} for file in sorted(package.rglob('*')) if file.is_file()]
        manifest={'schemaVersion':1,'build':build,'engineBuildKind':'release','files':files}
        (package/'gua-build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    for name in ['plugin.gd','export.gd','policy.gd.txt','native_guard.gd','web_export.gd','web_guard.js.txt','web_policy.gd.txt','web-custom-shell.html']:
        shutil.copyfile(root/'bindings/godot/game-build'/name,editor/name)
    (editor/'.gdignore').write_text('')
    shutil.copyfile(template,editor/'GodotPolicyTemplate.so')
    producer=subprocess.check_output(['git','-C',str(root),'rev-parse','HEAD'],text=True).strip()
    dirty=bool(subprocess.check_output(['git','-C',str(root),'status','--porcelain'],text=True).strip())
    (editor/'linux-guard-template.json').write_text(json.dumps({'schemaVersion':1,'sha256':sha(template),'producerCommit':producer,'producerHasUncommittedChanges':dirty})+'\n')
    for name in ['gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd']:
        shutil.copyfile(root/'examples/godot-gdscript/addons/gua'/name,addon/name)
    (addon/'plugin.cfg').write_text('[plugin]\nname="Gua game build"\ndescription="Select prebuilt Gua modes and generate game-owned verification."\nauthor="Gua"\nversion="0.1.0"\nscript="editor/plugin.gd"\n')
    shutil.copyfile(root/'LICENSE',args.output/'LICENSE')
    print(json.dumps({'sdk':str(args.output),'nativeSourceCommit':source,'modes':3,'templateSha256':sha(template)}))

if __name__=='__main__':main()
