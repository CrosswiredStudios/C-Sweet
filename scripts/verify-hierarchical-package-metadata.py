"""Verify the local handoff feed against final manifests; never publish packages."""
import json
import hashlib
import subprocess
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
    note = Path('releases') / (manifest['version'] + '.md')
    assert (repository / note).read_text(encoding='utf-8-sig').splitlines()[0] == '# ' + manifest['version'], (repository, note)
    # A local file can pass content checks while an ignore rule silently excludes
    # it from the commit used by C-Sweet to fetch release notes. Check even tracked
    # files so a future version cannot disappear under the same ignore rule.
    ignored = subprocess.run(['git', 'check-ignore', '--no-index', note.as_posix()],
                             cwd=repository, capture_output=True, text=True)
    assert ignored.returncode == 1, (repository, note, 'Release notes must not be ignored', ignored.stdout, ignored.stderr)
    visible = subprocess.run(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '--', note.as_posix()],
                             cwd=repository, capture_output=True, text=True)
    assert visible.returncode == 0 and note.as_posix() in visible.stdout.splitlines(), (repository, note, 'Release notes must be tracked or visible for addition', visible.stderr)
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
