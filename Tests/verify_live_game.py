"""Read-only audit of an already running kingdom. Never starts/stops or changes the game."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import statistics
import time
from urllib.request import urlopen

PROJECT = Path(__file__).resolve().parents[1]

def audit(base, count):
    def request(path):
        started = time.perf_counter()
        with urlopen(base + path, timeout=8) as response:
            raw = response.read()
        return json.loads(raw), len(raw), (time.perf_counter() - started) * 1000
    discovery, _, _ = request('/api/v1')
    catalog, _, _ = request('/api/v1/catalog')
    items = {row['id'] for row in catalog['items']}
    recipes = {row['id'] for row in catalog['recipes']}
    techs = {row['id'] for row in catalog['research']}
    classes = set(catalog['buildingTypes'])
    samples, checks, cursor, previous_instance = [], 0, None, None
    for index in range(count):
        health, _, _ = request('/api/v1/health')
        assert health['status'] == 'live' and not health['stale'], health
        snapshot, size, latency = request('/api/v1/snapshot')
        data = {key: value['data'] for key, value in snapshot['sections'].items()}
        assert data['session']['loaded'] and snapshot['sessionId'] == data['session']['id']
        assert {t['id'] for t in data['research'] if t['purchased']} == set(data['native']['Tech'])
        assert {t['id'] for t in data['research']} == techs
        assert set(data['unlocks']['itemIds']) <= items
        assert set(data['unlocks']['recipeIds']) <= recipes
        assert set(data['unlocks']['buildingTypes']) <= classes
        assert data['economy']['currencies'] == data['native']['Currency']
        assert all(isinstance(value, str) and str(int(value)) == value for value in data['economy']['currencies'].values())
        assert data['upgrades']['slotsByBuildingType'] == data['native']['UpgradeCounts']
        assert data['upgrades']['resourceUpgradeCounts'] == data['native']['ResourceUpgradeCount']
        checks += 10
        flows = data['factory']['flows']
        assert len(flows) == len(data['native']['Flows'])
        ids = set()
        buildings = connections = 0
        for flow, native in zip(flows, data['native']['Flows']):
            native_buildings = {b['Guid']: b for b in native['Elements']}
            lookup = {b['id']: b for b in flow['buildings']}
            assert set(lookup) == set(native_buildings)
            assert len(flow['connections']) == len(native['Pipes'])
            for building in flow['buildings']:
                assert building['id'] not in ids
                ids.add(building['id'])
                assert building['state'] == native_buildings[building['id']]
                assert building['type'] in classes
                assert building['recipeId'] is None or building['recipeId'] in recipes
                assert len(building['installedUpgrades']) == building['upgradeSlots']
                assert len(building['inputs']) == len(building['state']['Inputs'])
                assert len(building['outputs']) == len(building['state']['Outputs'])
                for side in ['inputs', 'outputs']:
                    for port in building[side]:
                        assert port['buffered'] == len(port['inventory'])
                        # Batch/quest results may legitimately exceed the nominal buffer
                        # threshold. Preserve actual inventory rather than clipping it.
                        assert port['buffered'] >= 0 and port['capacity'] >= 0
                        assert port['itemId'] is None or port['itemId'] in items
                        checks += 3
                checks += 7
            for edge in flow['connections']:
                assert edge['sourceBuildingId'] in lookup and edge['targetBuildingId'] in lookup
                assert 0 <= edge['sourcePort'] < len(lookup[edge['sourceBuildingId']]['outputs'])
                assert 0 <= edge['targetPort'] < len(lookup[edge['targetBuildingId']]['inputs'])
                checks += 3
            buildings += len(lookup); connections += len(flow['connections']); checks += 2
        for resource in data['resources']['quotas']:
            assert resource['itemId'] in items
            assert abs(resource['sparePerSecond'] - max(0, resource['availablePerSecond'] - resource['demandPerSecond'])) < 1e-7
            assert abs(resource['shortfallPerSecond'] - max(0, resource['demandPerSecond'] - resource['availablePerSecond'])) < 1e-7
            checks += 3
        if cursor is not None and previous_instance == snapshot['instanceId']:
            feed, _, _ = request(f'/api/v1/changes?after={cursor}&instanceId={previous_instance}')
            assert feed['revision'] >= snapshot['revision']
            assert not feed['resyncRequired']
            checks += 2
        cursor, previous_instance = snapshot['revision'], snapshot['instanceId']
        samples.append({'revision': cursor, 'sessionId': snapshot['sessionId'], 'gameTimeSeconds': data['session']['gameTimeSeconds'],
                        'flows': len(flows), 'buildings': buildings, 'connections': connections,
                        'bytes': size, 'requestMs': round(latency, 2)})
        if index + 1 < count: time.sleep(.6)
    report = {'passed': True, 'testedAtUtc': datetime.now(timezone.utc).isoformat(), 'readOnly': True,
              'apiPluginVersion': discovery.get('pluginVersion'), 'gameVersion': catalog['gameVersion'], 'referenceMatchesRuntime': catalog['referenceMatchesRuntime'],
              'samples': samples, 'checks': checks,
              'medianSnapshotRequestMs': round(statistics.median(s['requestMs'] for s in samples), 2),
              'observedAdvancingState': len({s['revision'] for s in samples}) > 1,
              'scope': 'Already loaded kingdom; no research, purchase, save, load, or process control actions.'}
    (PROJECT/'Verification').mkdir(exist_ok=True)
    (PROJECT/'Verification/live-game-tests.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, indent=2))

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--base', default='http://127.0.0.1:17840')
    parser.add_argument('--samples', type=int, default=8)
    args = parser.parse_args()
    audit(args.base, max(2, min(args.samples, 30)))
