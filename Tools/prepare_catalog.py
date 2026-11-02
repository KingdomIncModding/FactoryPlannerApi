"""Bundle version-pinned reference data; runtime definitions remain authoritative."""
from pathlib import Path
import hashlib, json, zipfile

project = Path(__file__).resolve().parents[1]
game = project.parents[1]
data = game / 'Extracted' / 'CalculatorData'
output = project / 'Resources'
output.mkdir(exist_ok=True)
def clean(value):
    if isinstance(value, dict):
        return {k: clean(v) for k, v in value.items() if not k.endswith('_csharp')}
    if isinstance(value, list): return [clean(v) for v in value]
    return value
files = ['items.json', 'recipes.json', 'nodes.json', 'planner_rules.json', 'combat_rules.json', 'localization_en-US.json']
catalog = {name.removesuffix('.json'): clean(json.loads((data/name).read_text(encoding='utf-8'))) for name in files}
(output/'catalog-index.json').write_text(json.dumps(catalog, separators=(',', ':'), ensure_ascii=True)+'\n', encoding='utf-8')
paths = [game/'KingdomInc_Data'/'Managed'/'Assembly-CSharp.dll', *[data/name for name in files]]
manifest = {'source': 'Installed game and reusable extraction', 'files': {
    p.relative_to(game).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}}
(output/'provenance.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
assets = json.loads((game/'Extracted/asset_index.json').read_text(encoding='utf-8'))
icons = {asset['name']: asset['exports'][0] for asset in assets if asset['type']=='Sprite' and asset.get('exports') and asset['name'].startswith('Icons')}
with zipfile.ZipFile(output/'icons.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for name, relative in sorted(icons.items()): archive.write(game/'Extracted'/relative, name+'.png')
print('Bundled icons:', len(icons))
print('Prepared reference catalog:', (output/'catalog-index.json').stat().st_size, 'bytes')
