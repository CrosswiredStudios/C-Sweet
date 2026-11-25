"""Audit CI dependency fallback copies, commit pins, workflows and release metadata."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('prepare', ROOT / 'scripts/prepare-ci-dependencies.py')
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)
extension_spec = importlib.util.spec_from_file_location('extension', ROOT / 'scripts/verify-bundled-extension.py')
extension = importlib.util.module_from_spec(extension_spec)
extension_spec.loader.exec_module(extension)


def discover():
    roots = [*ROOT.parent.glob('CSweet.Agent.*')]
    if Path('D:/Repositories').is_dir():
        roots.extend(Path('D:/Repositories').glob('CSweet.Agent.*'))
    return sorted({path.resolve() for path in roots if (path / 'csweet-plugin.json').is_file()
                   and any((path / '.github/workflows').glob('*.y*ml'))})


def audit(repo):
    errors = []
    has_extension = (repo / 'extensions/video-game/extension.json').is_file()
    if has_extension:
        name = 'verify-bundled-extension.py'
        if (repo / 'scripts' / name).read_text(encoding='utf-8-sig') != (ROOT / 'scripts' / name).read_text(encoding='utf-8-sig'):
            errors.append(f'{name} differs from the canonical copy')
        extension.validate(repo)
    for name in ('prepare-ci-dependencies.py', 'test-ci-dependencies.py'):
        if (repo / 'scripts' / name).read_text(encoding='utf-8-sig') != (ROOT / 'scripts' / name).read_text(encoding='utf-8-sig'):
            errors.append(f'{name} differs from the canonical copy')
    pins = json.loads((repo / 'build/ci-dependencies.json').read_text())
    if pins.get('schemaVersion') != 1:
        errors.append('Unsupported pin schema version')
    def read_project(package, commit):
        source = ROOT.parent / prepare.SOURCES[package].split('/')[-1]
        project = f'src/{package}/{package}.csproj'
        return ET.fromstring(subprocess.check_output(
            ['git', '-C', str(source), 'show', f'{commit}:{project}'], text=True))

    with tempfile.TemporaryDirectory() as state:
        prepare.Resolver(repo, Path(state), pins).verify_pins(read_project)
    manifest = json.loads((repo / 'csweet-plugin.json').read_text())
    version = manifest['version']
    if ET.parse(repo / manifest['runtime']['projectPath']).findtext('.//Version') != version:
        errors.append('Manifest/project version mismatch')
    heading = (repo / 'releases' / f'{version}.md').read_text().splitlines()[0]
    expected_heading = (f'# OpenHands Developer {version}'
                        if repo.name == 'CSweet.Agent.SoftwareDeveloper.OpenHands' else f'# {version}')
    if heading != expected_heading:
        errors.append('Missing or mismatched release notes')
    pin_workflow = (repo / '.github/workflows/dependency-pins.yml').read_text()
    if 'pull_request:' not in pin_workflow or 'merge_group:' not in pin_workflow:
        errors.append('Dependency pin workflow must cover pull requests and merge queues')
    if 'run: python3 -B scripts/prepare-ci-dependencies.py --verify-pins' not in pin_workflow:
        errors.append('Dependency pin workflow does not verify source projects')
    count = 0
    for workflow in (repo / '.github/workflows').glob('*.y*ml'):
        text = workflow.read_text()
        if 'actions/setup-dotnet@' not in text or 'dotnet ' not in text:
            continue
        count += 1
        if has_extension and text.count('run: python3 scripts/verify-bundled-extension.py --self-test') != 1:
            errors.append(f'{workflow.name} does not run exact extension provenance and checkout regression checks')
        prepare_step = 'run: python3 scripts/prepare-ci-dependencies.py --require-pins'
        if text.count(prepare_step) != 1:
            errors.append(f'{workflow.name} must validate pins and prepare dependencies exactly once')
        if text.count('run: python3 scripts/test-ci-dependencies.py') != 1:
            errors.append(f'{workflow.name} does not run the fallback regression suite')
        if text.find(prepare_step) > text.find('dotnet test'):
            errors.append(f'{workflow.name} prepares dependencies after testing')
        if 'path: .artifacts/ci-dependencies/resolved.json' not in text:
            errors.append(f'{workflow.name} does not retain dependency provenance')
    if errors:
        raise ValueError(f'{repo.name}: ' + '; '.join(errors))
    return count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('repositories', nargs='*', type=Path)
    args = parser.parse_args()
    repos = args.repositories or discover()
    if not repos:
        raise ValueError('No standalone agent workflows found')
    count = 0
    errors = []
    for repo in repos:
        try:
            count += audit(repo.resolve())
        except (OSError, ValueError, KeyError, subprocess.CalledProcessError, ET.ParseError) as error:
            errors.append(f'{repo.name}: {error}')
    if errors:
        raise ValueError('\n'.join(errors))
    print(f'CI dependency fallback audit passed: {len(repos)} agents, {count} build workflows.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
