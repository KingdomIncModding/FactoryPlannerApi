from pathlib import Path
import hashlib, json, zipfile, re, subprocess
import xml.etree.ElementTree as ET
project=Path(__file__).resolve().parents[1]
out=project/'Dist';out.mkdir(exist_ok=True)
dll=project/'bin/Release/netstandard2.1/KingdomFactoryApi.dll'
plugin='BepInEx/plugins/KingdomFactoryApi/KingdomFactoryApi.dll'
version=ET.parse(project/'KingdomFactoryApi.csproj').getroot().findtext('PropertyGroup/Version')
assert version == re.search(r'Version = "([^"]+)"', (project/'Core/ApiBuild.cs').read_text()).group(1)
with zipfile.ZipFile(out/f'KingdomFactoryApi-{version}.zip','w',zipfile.ZIP_DEFLATED) as archive:
    archive.write(dll,plugin)
    for file in ['README.md','openapi.json','Web/client.js']:
        archive.write(project/file,'FactoryApi/'+file)
with zipfile.ZipFile(out/f'KingdomFactoryApi-{version}.zip') as archive:
    assert archive.testzip() is None
    assert archive.read(plugin)==dll.read_bytes()
with zipfile.ZipFile(out/f'KingdomFactoryApi-{version}-source.zip','w',zipfile.ZIP_DEFLATED) as archive:
    # Use the repository's publication rules for developer downloads as well.
    # A recursive walk can otherwise include .git, local secrets and AI notes.
    eligible = subprocess.check_output([
        'git', '-C', str(project), 'ls-files', '--cached', '--others',
        '--exclude-standard', '-z'
    ]).decode('utf-8').split('\0')
    for name in sorted(set(eligible) - {''}):
        path = project / name
        if path.is_file():
            archive.write(path,'FactoryPlannerApi/'+name)
manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in out.glob('*.zip')}
(out/'SHA256.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps(manifest,indent=2))
