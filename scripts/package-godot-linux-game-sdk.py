"""Package a build-only Linux candidate, without publishing a release."""
import argparse
import hashlib
import json
from pathlib import Path,PurePosixPath
import re
import zipfile

def digest(path):
    with path.open('rb') as source:return hashlib.file_digest(source,'sha256').hexdigest()

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk',required=True,type=Path)
    parser.add_argument('--version',required=True)
    parser.add_argument('--source',required=True)
    parser.add_argument('--output',required=True,type=Path)
    parser.add_argument('--allow-dirty-local',action='store_true',help='Local evidence only; keep the dirty producer marker.')
    args=parser.parse_args();sdk=args.sdk.resolve(strict=True)
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?',args.version) or not re.fullmatch('[a-f0-9]{40}',args.source):raise ValueError('Explicit version/source required.')
    if args.output.exists():raise ValueError('Use a new candidate directory.')
    expected_files={'LICENSE','addons/gua/plugin.cfg','addons/gua/.linux_variants/.gdignore'}
    scripts=['gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd']
    expected_files.update('addons/gua/'+name for name in scripts)
    editor_members=['plugin.gd','export.gd','policy.gd.txt','native_guard.gd','web_export.gd','web_guard.js.txt','web_policy.gd.txt','web-custom-shell.html','.gdignore','GodotPolicyTemplate.so','linux-guard-template.json']
    expected_files.update('addons/gua/editor/'+name for name in editor_members)
    for mode,flavor,ceiling in [('off','shipping','none'),('testing','qa','debug'),('player','ai-player','player')]:
        package=sdk/'addons/gua/.linux_variants'/mode
        manifest=json.loads((package/'gua-build-manifest.json').read_text());build=manifest['build']
        if (manifest.get('schemaVersion')!=1 or manifest.get('engineBuildKind')!='release' or build.get('configuration')!='Release'
            or build.get('sourceCommit')!=args.source or build.get('flavor')!=flavor or build.get('remoteEnabled')!=(mode!='off')
            or build.get('remotePermissionCeiling')!=ceiling):raise ValueError('Candidate variant/source mismatch.')
        required={'addons/gua/bin/gua_godot.linux.release.x86_64.so',*(f'addons/gua/{name}' for name in ['gua_auto_adapter.gd','gua_observe_owner.gd','gua_spatial.gd'])}
        seen=set()
        for entry in manifest['files']:
            path=PurePosixPath(entry['path'])
            if path.is_absolute() or '..' in path.parts or '\\' in entry['path'] or entry['path'] not in required or entry['path'] in seen:raise ValueError('Invalid candidate member.')
            member=package/entry['path']
            if member.is_symlink() or digest(member)!=entry['sha256']:raise ValueError('Modified candidate bytes.')
            seen.add(entry['path'])
        if seen!=required:raise ValueError('Incomplete candidate mode.')
        actual={p.relative_to(package).as_posix() for p in package.rglob('*') if p.is_file()}
        if actual!=required|{'gua-build-manifest.json'}:raise ValueError('Unmanifested mode member.')
        expected_files.update('addons/gua/.linux_variants/'+mode+'/'+name for name in actual)
    metadata=json.loads((sdk/'addons/gua/editor/linux-guard-template.json').read_text())
    template=sdk/'addons/gua/editor/GodotPolicyTemplate.so'
    if metadata.get('schemaVersion')!=1 or digest(template)!=metadata.get('sha256') or metadata.get('producerCommit')!=args.source:raise ValueError('Modified/mixed-source game verifier template.')
    if metadata.get('producerHasUncommittedChanges') is not False and not args.allow_dirty_local:raise ValueError('Candidate producer is not clean; local-only override must be explicit.')
    actual_files={p.relative_to(sdk).as_posix() for p in sdk.rglob('*') if p.is_file()}
    if actual_files!=expected_files:raise ValueError('Unexpected/missing SDK candidate member.')
    args.output.mkdir(parents=True)
    archive=args.output/f'gua-game-build-godot-linux-x64-{args.version}.zip'
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as zipped:
        for file in sorted(sdk.rglob('*')):
            if file.is_symlink():raise ValueError('Linked SDK candidate member.')
            if file.is_file():zipped.write(file,file.relative_to(sdk).as_posix())
    delivery={'schemaVersion':1,'purpose':'build-only candidate; not a public release','nativeSourceCommit':args.source,
              'producerCommit':metadata.get('producerCommit'),'producerHasUncommittedChanges':metadata.get('producerHasUncommittedChanges'),
              'files':[{'path':archive.name,'sha256':digest(archive),'bytes':archive.stat().st_size}]}
    (args.output/'game-sdk-delivery-linux.json').write_text(json.dumps(delivery,indent=2)+'\n')
    print(json.dumps(delivery))

if __name__=='__main__':main()
