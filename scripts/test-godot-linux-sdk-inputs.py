"""Fail-closed producer/package tests using real prebuilt native inputs."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import struct
import sys

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ['sdk','inputs','template','output']:
        parser.add_argument('--'+name,required=True,type=Path)
    args=parser.parse_args();root=Path(__file__).resolve().parent
    sdk=args.sdk.resolve();inputs=args.inputs.resolve();template=args.template.resolve();out=args.output.resolve()
    if out.exists():raise ValueError('Use a new input acceptance directory.')
    out.mkdir(parents=True);results=[]
    stage=[sys.executable,str(root/'stage-godot-linux-game-sdk.py'),'--off',str(inputs/'off.so'),'--testing',str(inputs/'testing.so'),'--player',str(inputs/'player.so'),'--template',str(template)]
    source=json.loads((sdk/'addons/gua/.linux_variants/off/gua-build-manifest.json').read_text())['build']['sourceCommit']
    def refusal(name,command,expected,target):
        run=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,timeout=90)
        (out/(name+'.log')).write_text(run.stdout)
        assert run.returncode!=0 and expected in run.stdout and not target.exists(),(name,run.returncode,run.stdout)
        results.append({'case':name,'result':'refused','outputCreated':False})
    bad=stage.copy();bad[bad.index('--off')+1]=str(inputs/'testing.so')
    target=out/'wrong-mode-output';refusal('wrong-off-mode',bad+['--output',str(target)],'Linux provider identities/modes do not match',target)
    def add_runpath(original):
        image=bytearray(original.read_bytes())
        phoff=struct.unpack_from('<Q',image,32)[0];size,count=struct.unpack_from('<HH',image,54)
        assert size==56
        entries=[struct.unpack_from('<IIQQQQQQ',image,phoff+i*size) for i in range(count)]
        dynamic=next(entry for entry in entries if entry[0]==2)
        pairs=[(offset,*struct.unpack_from('<QQ',image,offset)) for offset in range(dynamic[2],dynamic[2]+dynamic[5],16)]
        # RELACOUNT is an optional GNU optimization. Replace it with a valid
        # RUNPATH using an existing string-table offset; keep ELF/identity intact.
        string=next(value for _offset,tag,value in pairs if tag==1)
        offset=next(offset for offset,tag,_value in pairs if tag==0x6ffffff9)
        struct.pack_into('<QQ',image,offset,29,string)
        return image
    for name,option,original in [('native-runpath','--off',inputs/'off.so'),('template-runpath','--template',template)]:
        path=out/(name+'.so');path.write_bytes(add_runpath(original))
        bad=stage.copy();bad[bad.index(option)+1]=str(path);target=out/(name+'-output')
        refusal(name,bad+['--output',str(target)],'SDK has an unconstrained ELF loader path/hook',target)
    for name,change in [('template-architecture',lambda data:data.__setitem__(slice(18,20),(183).to_bytes(2,'little'))),('template-duplicate-slot',lambda data:data.extend(b'GUAGDPOLICYSLOT1'))]:
        image=bytearray(template.read_bytes());change(image);path=out/(name+'.so');path.write_bytes(image)
        bad=stage.copy();bad[bad.index('--template')+1]=str(path);target=out/(name+'-output')
        reason='Expected an ELF64 x86_64 shared verifier template' if name=='template-architecture' else 'Missing/ambiguous template slot'
        refusal(name,bad+['--output',str(target)],reason,target)
    for name,reason in [('package-native-bytes','Modified candidate bytes'),('package-extra-native','Unexpected/missing SDK candidate member'),('package-source','Modified/mixed-source game verifier template'),('package-dirty','Candidate producer is not clean')]:
        candidate=out/name;shutil.copytree(sdk,candidate)
        if name=='package-native-bytes':
            file=candidate/'addons/gua/.linux_variants/off/addons/gua/bin/gua_godot.linux.release.x86_64.so'
            image=bytearray(file.read_bytes());image[-1]^=1;file.write_bytes(image)
        elif name=='package-extra-native':shutil.copyfile(inputs/'testing.so',candidate/'addons/gua/unexpected-enabled.so')
        else:
            metadata=candidate/'addons/gua/editor/linux-guard-template.json';value=json.loads(metadata.read_text())
            value['producerCommit' if name=='package-source' else 'producerHasUncommittedChanges']='0'*40 if name=='package-source' else True
            metadata.write_text(json.dumps(value))
        target=out/(name+'-output')
        command=[sys.executable,str(root/'package-godot-linux-game-sdk.py'),'--sdk',str(candidate),'--version','0.0.0-ci','--source',source,'--output',str(target)]
        if name!='package-dirty':command+=['--allow-dirty-local']
        refusal(name,command,reason,target)
    (out/'results.json').write_text(json.dumps(results,indent=2)+'\n')
    print(json.dumps({'result':'pass','cases':len(results),'output':str(out)}))

if __name__=='__main__':main()
