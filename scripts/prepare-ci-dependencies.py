"""Prefer exact NuGet packages; pack missing first-party packages at reviewed commits.

Keep this file and its tests identical in standalone agent repositories. See
docs/implementation/agent-ci-dependency-fallback.md in the C-Sweet repository.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


SOURCES = {
    'CSweet.Agent.SDK': 'CrosswiredStudios/CSweet.Agent.Sdk',
    'CSweet.WorkManagement.Contracts': 'CrosswiredStudios/CSweet.WorkManagement.Contracts',
}
VERSION = re.compile(r'\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?')
LOCAL_FLAGS = ['-p:UseLocalCSweetAgentSdk=false',
               '-p:UseLocalCSweetWorkManagementContracts=false']


def exact_version(value):
    # nuspec dependencies normally express a minimum as [x.y.z, ).
    value = (value or '').strip()
    match = re.fullmatch(r'\[(' + VERSION.pattern + r')\s*(?:\]|,\s*\))', value)
    if match:
        value = match[1].strip()
    if not VERSION.fullmatch(value):
        raise ValueError(f'Expected a literal package version, got {value!r}')
    return value


def dependencies(xml):
    result = set()
    for node in xml.iter():
        kind = node.tag.rsplit('}', 1)[-1]
        if kind == 'PackageReference':
            package = node.get('Include') or node.get('Update')
            version = node.get('Version') or node.findtext('Version')
        elif kind == 'dependency':
            package, version = node.get('id'), node.get('version')
        else:
            continue
        if package in SOURCES:
            result.add((package, exact_version(version)))
    return sorted(result)


def agent_dependencies(root):
    result = set()
    for folder in ('src', 'tests'):
        for project in (root / folder).rglob('*.csproj'):
            if {'bin', 'obj'}.intersection(project.relative_to(root).parts):
                continue
            result.update(dependencies(ET.parse(project).getroot()))
    if not any(package == 'CSweet.Agent.SDK' for package, _ in result):
        raise ValueError('No literal CSweet.Agent.SDK PackageReference found in src/ or tests/.')
    return sorted(result)


def download(url):
    """Only 404 means unpublished. Auth, rate limits and outages must fail CI."""
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={'User-Agent': 'csweet-ci-dependencies'})
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            if error.code not in (429, 500, 502, 503, 504) or attempt == 2:
                raise
        except (urllib.error.URLError, TimeoutError):
            if attempt == 2:
                raise
        time.sleep(attempt + 1)
    raise RuntimeError('Download retry limit exceeded')


def run(args, cwd=None, env=None):
    try:
        return subprocess.check_output([str(arg) for arg in args], cwd=cwd, env=env,
                                       text=True, stderr=subprocess.STDOUT).strip()
    except subprocess.CalledProcessError as error:
        output = error.output
        token = os.environ.get('CSWEET_DEPENDENCY_TOKEN', '')
        if token:
            output = output.replace(token, '***').replace(
                base64.b64encode(f'x-access-token:{token}'.encode()).decode(), '***')
        print(output, file=sys.stderr)
        raise


def package_metadata(path, package, version):
    with zipfile.ZipFile(path) as archive:
        specs = [name for name in archive.namelist() if name.endswith('.nuspec')]
        if len(specs) != 1:
            raise ValueError(f'{path.name} must contain one nuspec')
        xml = ET.fromstring(archive.read(specs[0]))
    values = {node.tag.rsplit('}', 1)[-1]: node.text for node in xml.iter()
              if node.tag.rsplit('}', 1)[-1] in ('id', 'version')}
    if values.get('id') != package or values.get('version') != version:
        raise ValueError(f'Package identity/version mismatch in {path.name}: {values}')
    return dependencies(xml)


class Resolver:
    def __init__(self, root, state, pins, fetch=download, execute=run):
        self.root, self.state, self.pins = root, state, pins
        self.fetch, self.execute = fetch, execute
        self.feed = state / 'feed'
        self.feed.mkdir(parents=True, exist_ok=True)
        self.resolved = {}
        self.resolving = set()
        self.base = None
        self.env = os.environ.copy()
        # Source-build subprocesses never inherit the repository read token.
        self.env.pop('CSWEET_DEPENDENCY_TOKEN', None)
        self.env.update(UseLocalCSweetAgentSdk='false',
                        UseLocalCSweetWorkManagementContracts='false',
                        NUGET_PACKAGES=str(state / 'packages'))

    def source_pin(self, package, version):
        pin = self.pins.get('packages', {}).get(package, {}).get(version, {})
        commit = pin.get('commit', '')
        if not re.fullmatch(r'[0-9a-f]{40}', commit):
            raise ValueError(f'{package} {version} is unpublished and has no reviewed full commit '
                             'in build/ci-dependencies.json. Update the pin with the dependency bump.')
        return commit

    def checkout(self, package, version):
        commit = self.source_pin(package, version)
        source_root = self.state / 'source'
        source_root.mkdir(parents=True, exist_ok=True)
        destination = Path(tempfile.mkdtemp(prefix=f'{package}-{version}-', dir=source_root))
        self.execute(['git', 'init', '--quiet', destination], env=self.env)
        url = f'https://github.com/{SOURCES[package]}.git'
        git_env = self.env.copy()
        token = os.environ.get('CSWEET_DEPENDENCY_TOKEN', '')
        if token:
            auth = base64.b64encode(f'x-access-token:{token}'.encode()).decode()
            git_env.update(GIT_CONFIG_COUNT='1',
                           GIT_CONFIG_KEY_0=f'http.{url}.extraheader',
                           GIT_CONFIG_VALUE_0=f'AUTHORIZATION: basic {auth}')
        # Fetch only this immutable revision; never fall back to main or another version.
        self.execute(['git', '-C', destination, 'fetch', '--quiet', '--depth=1', url, commit],
                     env=git_env)
        self.execute(['git', '-C', destination, 'checkout', '--quiet', '--detach', 'FETCH_HEAD'],
                     env=self.env)
        actual = self.execute(['git', '-C', destination, 'rev-parse', 'HEAD'], env=self.env)
        if actual != commit:
            raise ValueError(f'{package} checkout does not match pinned commit {commit}')
        return destination, commit

    def resolve(self, package, version):
        key = package, exact_version(version)
        if key in self.resolved:
            return
        if key in self.resolving:
            raise ValueError(f'Cyclic first-party dependency: {key}')
        self.resolving.add(key)
        if self.base is None:
            index = self.fetch('https://api.nuget.org/v3/index.json')
            if index is None:
                raise ValueError('NuGet service index is unavailable')
            self.base = next(resource['@id'] for resource in json.loads(index)['resources']
                             if resource['@type'] == 'PackageBaseAddress/3.0.0').rstrip('/')
            if not self.base.startswith('https://'):
                raise ValueError('NuGet package content endpoint must use HTTPS')
        filename = f'{package}.{version}.nupkg'
        artifact = self.feed / filename
        url = f'{self.base}/{package.lower()}/{version.lower()}/{filename.lower()}'
        content = self.fetch(url)
        commit = None
        if content is not None:
            artifact.write_bytes(content)
            required = package_metadata(artifact, package, version)
            origin = 'nuget.org'
        else:
            source, commit = self.checkout(package, version)
            project = source / 'src' / package / f'{package}.csproj'
            xml = ET.parse(project).getroot()
            if xml.findtext('.//Version') != version or xml.findtext('.//PackageId') != package:
                raise ValueError(f'{package} source at {commit} does not declare version {version}')
            required = dependencies(xml)
            # Contracts must be in the feed before packing the SDK that depends on them.
            for dependency in required:
                self.resolve(*dependency)
            self.execute(['dotnet', 'pack', project, '-c', 'Release', '-o', self.feed,
                          *LOCAL_FLAGS, f'-p:RestoreAdditionalProjectSources={self.feed}',
                          '-p:ManagePackageVersionsCentrally=false',
                          '-p:ContinuousIntegrationBuild=true'], cwd=source, env=self.env)
            packed_required = package_metadata(artifact, package, version)
            if packed_required != required:
                raise ValueError(f'{package} packed dependency metadata differs from its source pins')
            origin = 'source'
        for dependency in required:
            self.resolve(*dependency)
        record = {'package': package, 'version': version, 'origin': origin,
                  'sha256': hashlib.sha256(artifact.read_bytes()).hexdigest()}
        if commit:
            record.update(repository=SOURCES[package], commit=commit)
        self.resolved[key] = record
        self.resolving.remove(key)
        print(f'{package} {version}: {origin}' + (f' at {commit}' if commit else ''), flush=True)

    def prepare(self):
        for dependency in agent_dependencies(self.root):
            self.resolve(*dependency)
        records = sorted(self.resolved.values(), key=lambda row: (row['package'], row['version']))
        proof = self.root / '.artifacts' / 'ci-dependencies' / 'resolved.json'
        proof.parent.mkdir(parents=True, exist_ok=True)
        proof.write_text(json.dumps({'packages': records}, indent=2) + '\n', encoding='utf-8')
        values = {'RestoreAdditionalProjectSources': str(self.feed),
                  'UseLocalCSweetAgentSdk': 'false', 'UseLocalCSweetWorkManagementContracts': 'false',
                  'NUGET_PACKAGES': str(self.state / 'packages')}
        if os.environ.get('GITHUB_ENV'):
            with open(os.environ['GITHUB_ENV'], 'a', encoding='utf-8') as output:
                for name, value in values.items():
                    if '\n' in value or '\r' in value:
                        raise ValueError('Invalid newline in GitHub environment value')
                    output.write(f'{name}={value}\n')
        if os.environ.get('GITHUB_STEP_SUMMARY'):
            with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as summary:
                summary.write('### C-Sweet build dependencies\n\n| Package | Version | Origin | Commit |\n'
                              '| --- | --- | --- | --- |\n')
                for row in records:
                    summary.write(f"| {row['package']} | {row['version']} | {row['origin']} | "
                                  f"{row.get('commit', '')} |\n")
        return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path.cwd())
    parser.add_argument('--state', type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    pins = json.loads((root / 'build/ci-dependencies.json').read_text(encoding='utf-8-sig'))
    if pins.get('schemaVersion') != 1:
        raise ValueError('Unsupported build/ci-dependencies.json schemaVersion')
    state = args.state or Path(os.environ.get('RUNNER_TEMP', root / '.artifacts')) / 'csweet-ci-dependencies'
    Resolver(root, state.resolve(), pins).prepare()


if __name__ == '__main__':
    try:
        main()
    except subprocess.CalledProcessError as error:
        # Captured output may contain credential diagnostics; do not echo it.
        sys.exit(f'Dependency command failed (exit {error.returncode}): {error.cmd[0]}. '
                 'Check dependency pins, repository access, and package build requirements.')
    except (OSError, ValueError, KeyError, StopIteration, ET.ParseError, zipfile.BadZipFile) as error:
        sys.exit(f'CI dependency preparation failed: {error}')
