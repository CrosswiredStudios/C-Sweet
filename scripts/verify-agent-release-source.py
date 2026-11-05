"""Fail release builds on broken source or inconsistent package metadata."""
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET


def validate(root, files):
    errors = []
    for relative in files:
        path = root / relative
        if not path.is_file() or path.suffix not in {'.cs', '.json', '.csproj', '.props', '.targets', '.yml', '.yaml', '.md', '.ps1', '.py'}:
            continue
        try:
            content = path.read_text(encoding='utf-8-sig')
        except UnicodeDecodeError:
            continue
        if re.search(r'^(?:<{7}|>{7}|\|{7})(?:\s|$)', content, re.MULTILINE):
            errors.append(f'Unresolved merge conflict in {relative}')
    try:
        manifest = json.loads((root / 'csweet-plugin.json').read_text(encoding='utf-8-sig'))
        version = manifest['version']
        project = ET.parse(root / manifest['runtime']['projectPath'])
        if project.findtext('.//Version') != version:
            errors.append('Manifest and project versions differ')
        notes = root / 'releases' / f'{version}.md'
        if notes.read_text(encoding='utf-8-sig').splitlines()[0] != f'# {version}':
            errors.append('Release-note heading differs from manifest version')
    except (OSError, ValueError, KeyError, IndexError, ET.ParseError) as error:
        errors.append(f'Invalid release metadata: {error}')
    return errors


def self_test():
    from tempfile import TemporaryDirectory
    with TemporaryDirectory() as directory:
        root = Path(directory)
        (root / 'releases').mkdir()
        (root / 'csweet-plugin.json').write_text(json.dumps({'version': '1.2.3', 'runtime': {'projectPath': 'agent.csproj'}}))
        (root / 'agent.csproj').write_text('<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>')
        (root / 'releases/1.2.3.md').write_text('# 1.2.3\n')
        assert not validate(root, ['agent.csproj'])
        (root / 'broken.cs').write_text('<' * 7 + ' Updated upstream\nbroken\n' + '>' * 7 + ' Stashed changes\n')
        assert validate(root, ['broken.cs'])
        (root / 'releases/1.2.3.md').write_text('# 1.2.2\n')
        assert validate(root, [])
        (root / 'csweet-plugin.json').write_text('{ invalid')
        assert validate(root, [])
    print('Release-source regression checks passed')


if __name__ == '__main__':
    if '--self-test' in sys.argv:
        self_test()
    else:
        root = Path.cwd()
        files = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=root).decode().split('\0')
        errors = validate(root, files)
        if errors:
            sys.exit('\n'.join(errors))
        commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root).decode().strip()
        print(f'Release-source checks passed at {commit}; CI builds this checked-out source')
