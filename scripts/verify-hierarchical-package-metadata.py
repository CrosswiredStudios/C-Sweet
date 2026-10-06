"""Verify the local handoff feed against final manifests; never publish packages."""
import json
import hashlib
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

root = Path(__file__).resolve().parents[1]
feed = root / 'artifacts/hierarchical-packages'
validation = root / 'artifacts/hierarchical-validation'
agents = json.loads((validation / 'agents.json').read_text(encoding='utf-8'))
packages = []
for path in sorted(feed.glob('*.nupkg')):
    with ZipFile(path) as archive:
        metadata = ET.fromstring(archive.read(next(n for n in archive.namelist() if n.endswith('.nuspec'))))
        fields = {el.tag.split('}')[-1]: el.text for el in metadata.iter() if el.tag.split('}')[-1] in ('id', 'version')}
        assert path.name.lower() == f"{fields['id']}.{fields['version']}.nupkg".lower(), path
        packages.append({'file': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(), **fields})
        for dependency in metadata.iter():
            if dependency.tag.split('}')[-1] == 'dependency':
                expected = {'CSweet.Agent.SDK': '3.59.0', 'CSweet.WorkManagement.Contracts': '3.25.0'}.get(dependency.attrib['id'])
                if expected:
                    assert expected in dependency.attrib['version'], (path, dependency.attrib)
for agent in agents:
    repository = root.parent / agent['repository']
    manifest = json.loads((repository / 'csweet-plugin.json').read_text(encoding='utf-8-sig'))
    assert manifest['version'] == agent['version']
    assert all(agent.get(stage) == 0 for stage in ('restore', 'test', 'self-test', 'pack')), agent
    assert any(package['version'] == manifest['version'] and package['id'].lower().endswith(agent['repository'].removeprefix('CSweet.Agent.').lower()) for package in packages), agent
    assert manifest['version'] in (repository / 'releases' / (manifest['version'] + '.md')).read_text(encoding='utf-8-sig').splitlines()[0]
    provenance = repository / 'extensions/video-game/extension.json'
    if provenance.exists():
        extension = json.loads(provenance.read_text(encoding='utf-8-sig'))
        assert extension['version'] == '1.1.0', repository
        for filename, digest in extension['files'].items():
            assert hashlib.sha256((provenance.parent / filename).read_bytes()).hexdigest() == digest, (repository, filename)
assert any(p['id'] == 'CSweet.Agent.SDK' and p['version'] == '3.59.0' for p in packages)
assert any(p['id'] == 'CSweet.WorkManagement.Contracts' and p['version'] == '3.25.0' for p in packages)
(validation / 'packages.json').write_text(json.dumps(packages, indent=2), encoding='utf-8')
print(f'Verified {len(packages)} local packages and {len(agents)} version-matched agents.')
