"""Offline dependency-resolution regression tests; no GitHub/NuGet credentials required."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import xml.etree.ElementTree as ET
import zipfile

spec = importlib.util.spec_from_file_location('prepare', Path(__file__).with_name('prepare-ci-dependencies.py'))
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)
SDK, CONTRACTS = 'CSweet.Agent.SDK', 'CSweet.WorkManagement.Contracts'
TESTING = 'CSweet.Agent.Testing'
SDK_COMMIT, CONTRACTS_COMMIT = 'a' * 40, 'b' * 40
INDEX = json.dumps({'resources': [{'@type': 'PackageBaseAddress/3.0.0',
                                 '@id': 'https://fixture.invalid/packages/'}]}).encode()


def package(package_id, version, deps=()):
    stream = io.BytesIO()
    xml = ET.Element('package', xmlns='http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd')
    metadata = ET.SubElement(xml, 'metadata')
    ET.SubElement(metadata, 'id').text = package_id
    ET.SubElement(metadata, 'version').text = version
    group = ET.SubElement(ET.SubElement(metadata, 'dependencies'), 'group')
    for dependency, pin in deps:
        ET.SubElement(group, 'dependency', id=dependency, version=f'[{pin}, )')
    with zipfile.ZipFile(stream, 'w') as archive:
        archive.writestr(f'{package_id}.nuspec', ET.tostring(xml))
    return stream.getvalue()


class ResolutionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'src').mkdir()
        self.project = self.root / 'src/agent.csproj'
        self.project.write_text(f'<Project><ItemGroup><PackageReference Include="{SDK}" '
                                'Version="5.28.0" /></ItemGroup></Project>')
        self.pins = {'schemaVersion': 1, 'packages': {
            SDK: {'5.28.0': {'commit': SDK_COMMIT}},
            CONTRACTS: {'5.7.0': {'commit': CONTRACTS_COMMIT}},
        }}
        self.available = {SDK: package(SDK, '5.28.0', [(CONTRACTS, '5.7.0')]),
                          CONTRACTS: package(CONTRACTS, '5.7.0')}
        self.commands = []
        self.environ = patch.dict(os.environ, {'GITHUB_ENV': str(self.root / 'env'),
                                               'GITHUB_STEP_SUMMARY': str(self.root / 'summary')})
        self.environ.start()
        self.addCleanup(self.environ.stop)

    def fetch(self, url):
        if url.endswith('/index.json'):
            return INDEX
        for name, content in self.available.items():
            if f'/{name.lower()}/' in url:
                return content
        raise AssertionError(f'Unexpected URL {url}')

    def execute(self, args, cwd=None, env=None):
        args = [str(arg) for arg in args]
        self.commands.append((args, env))
        if args[:2] == ['dotnet', 'pack']:
            project = Path(args[2])
            name = project.stem
            version = '5.7.0' if name == CONTRACTS else '5.28.0'
            output = Path(args[args.index('-o') + 1]) / f'{name}.{version}.nupkg'
            deps = {SDK: [(CONTRACTS, '5.7.0')], TESTING: [(SDK, '5.28.0')]}
            output.write_bytes(package(name, version, deps.get(name, [])))
        if args[-2:] == ['rev-parse', 'HEAD']:
            return CONTRACTS_COMMIT if CONTRACTS in args[2] else SDK_COMMIT
        return ''

    def resolver(self):
        resolver = prepare.Resolver(self.root, self.root / 'state', self.pins, self.fetch, self.execute)
        checkout = resolver.checkout

        def fixture_checkout(name, version):
            source, commit = checkout(name, version)
            projects = {CONTRACTS: ''} if name == CONTRACTS else {
                SDK: f'<PackageReference Include="{CONTRACTS}" Version="5.7.0" />',
                TESTING: f'<ProjectReference Include="../{SDK}/{SDK}.csproj" />',
            }
            for project, refs in projects.items():
                folder = source / 'src' / project
                if folder.exists():
                    continue
                folder.mkdir(parents=True)
                (folder / f'{project}.csproj').write_text(
                    f'<Project><PropertyGroup><PackageId>{project}</PackageId><Version>{version}</Version>'
                    f'</PropertyGroup><ItemGroup>{refs}</ItemGroup></Project>')
            return source, commit

        resolver.checkout = fixture_checkout
        return resolver

    def add_testing_reference(self):
        self.project.write_text(self.project.read_text().replace('</ItemGroup>',
                                f'<PackageReference Include="{TESTING}" Version="5.28.0" /></ItemGroup>'))

    def test_unpublished_testing_uses_the_same_sdk_checkout_and_pin(self):
        self.add_testing_reference()
        self.available = {SDK: None, TESTING: None, CONTRACTS: None}
        resolver = self.resolver()
        resolver.verify_pins()
        resolver.prepare()
        packs = [args for args, _ in self.commands if args[:2] == ['dotnet', 'pack']]
        self.assertEqual([CONTRACTS, SDK, TESTING], [Path(args[2]).stem for args in packs])
        self.assertEqual(Path(packs[1][2]).parents[2], Path(packs[2][2]).parents[2])
        self.assertEqual(SDK_COMMIT, resolver.resolved[TESTING, '5.28.0']['commit'])
        self.assertEqual(2, sum('fetch' in args for args, _ in self.commands))

    def test_testing_fallback_can_use_a_published_sdk_package(self):
        self.add_testing_reference()
        self.available[TESTING] = None
        resolver = self.resolver()
        resolver.prepare()
        self.assertEqual('nuget.org', resolver.resolved[SDK, '5.28.0']['origin'])
        self.assertEqual(SDK_COMMIT, resolver.resolved[TESTING, '5.28.0']['commit'])

    def test_pin_audit_checks_transitive_contracts_before_querying_nuget(self):
        self.add_testing_reference()
        self.pins['packages'][CONTRACTS].clear()
        resolver = self.resolver()
        resolver.fetch = lambda url: self.fail(f'Pin audit should not query {url}')
        with self.assertRaisesRegex(ValueError, r'CSweet.WorkManagement.Contracts 5.7.0 has no reviewed'):
            resolver.verify_pins()
        self.assertFalse(any(args[:2] == ['dotnet', 'pack'] for args, _ in self.commands))

    def test_published_packages_never_checkout_or_pack(self):
        resolver = self.resolver()
        values = resolver.prepare()
        self.assertEqual([], self.commands)
        self.assertEqual('false', values['UseLocalCSweetAgentSdk'])
        self.assertIn('RestoreAdditionalProjectSources=', (self.root / 'env').read_text())
        records = json.loads((self.root / '.artifacts/ci-dependencies/resolved.json').read_text())['packages']
        self.assertEqual(2, len(records))
        self.assertTrue(all(row['origin'] == 'nuget.org' and len(row['sha256']) == 64 for row in records))

    def test_both_missing_packs_contracts_before_sdk(self):
        self.available = {SDK: None, CONTRACTS: None}
        resolver = self.resolver()
        resolver.prepare()
        packs = [args for args, _ in self.commands if args[:2] == ['dotnet', 'pack']]
        self.assertEqual([CONTRACTS, SDK], [Path(args[2]).stem for args in packs])
        self.assertTrue(all(flag in args for args in packs for flag in prepare.LOCAL_FLAGS))
        self.assertEqual({SDK_COMMIT, CONTRACTS_COMMIT},
                         {row['commit'] for row in resolver.resolved.values()})
        fetches = [args for args, _ in self.commands if 'fetch' in args]
        self.assertTrue(all(args[-1] in {SDK_COMMIT, CONTRACTS_COMMIT} for args in fetches))

    def test_missing_sdk_uses_published_contracts(self):
        self.available[SDK] = None
        resolver = self.resolver()
        resolver.prepare()
        self.assertEqual('source', resolver.resolved[SDK, '5.28.0']['origin'])
        self.assertEqual('nuget.org', resolver.resolved[CONTRACTS, '5.7.0']['origin'])

    def test_transitive_missing_contracts_with_published_sdk(self):
        self.available[CONTRACTS] = None
        resolver = self.resolver()
        resolver.prepare()
        packs = [args for args, _ in self.commands if args[:2] == ['dotnet', 'pack']]
        self.assertEqual([CONTRACTS], [Path(args[2]).stem for args in packs])

    def test_duplicate_direct_and_transitive_pins_resolve_once(self):
        self.project.write_text(self.project.read_text().replace('</ItemGroup>',
                               f'<PackageReference Include="{CONTRACTS}" Version="5.7.0" /></ItemGroup>'))
        resolver = self.resolver()
        resolver.prepare()
        self.assertEqual(2, len(resolver.resolved))

    def test_missing_pin_fails_without_fetching_main(self):
        self.available[SDK] = None
        self.pins['packages'][SDK] = {}
        with self.assertRaisesRegex(ValueError, 'no reviewed full commit'):
            self.resolver().prepare()
        self.assertEqual([], self.commands)

    def test_source_version_mismatch_fails_before_pack(self):
        self.available[SDK] = None
        resolver = self.resolver()
        checkout = resolver.checkout

        def mismatch(name, version):
            source, commit = checkout(name, version)
            file = source / 'src' / name / f'{name}.csproj'
            file.write_text(file.read_text().replace('<Version>5.28.0', '<Version>5.29.0'))
            return source, commit

        resolver.checkout = mismatch
        with self.assertRaisesRegex(ValueError, 'does not declare version'):
            resolver.prepare()
        self.assertFalse(any(args[:2] == ['dotnet', 'pack'] for args, _ in self.commands))

    def test_published_identity_mismatch_fails(self):
        self.available[SDK] = package(SDK, '5.29.0')
        with self.assertRaisesRegex(ValueError, 'identity/version mismatch'):
            self.resolver().prepare()

    def test_token_only_passed_to_git_fetch(self):
        self.available[SDK] = None
        with patch.dict(os.environ, {'CSWEET_DEPENDENCY_TOKEN': 'fixture-secret'}):
            self.resolver().prepare()
        for args, env in self.commands:
            self.assertNotIn('CSWEET_DEPENDENCY_TOKEN', env)
            self.assertEqual('fetch' in args, 'GIT_CONFIG_VALUE_0' in env)
            self.assertNotIn('fixture-secret', ' '.join(args))

    def test_nuget_outage_does_not_start_source_fallback(self):
        with patch('urllib.request.urlopen', side_effect=urllib.error.HTTPError('https://fixture', 503, 'outage', {}, None)), \
             patch('time.sleep'):
            with self.assertRaises(urllib.error.HTTPError):
                prepare.download('https://fixture')

    def test_only_404_means_unpublished(self):
        with patch('urllib.request.urlopen', side_effect=urllib.error.HTTPError('https://fixture', 404, 'missing', {}, None)):
            self.assertIsNone(prepare.download('https://fixture'))
        for status in [401, 403, 429]:
            with patch('urllib.request.urlopen', side_effect=urllib.error.HTTPError('https://fixture', status, 'error', {}, None)), \
                 patch('time.sleep'):
                with self.assertRaises(urllib.error.HTTPError):
                    prepare.download('https://fixture')

    def test_rejects_floating_or_unbounded_version_requests(self):
        for value in ['5.*', '$(SdkVersion)', '[5.0.0,6.0.0)', '(,5.0.0]', '[5.0.0', '']:
            with self.assertRaises(ValueError):
                prepare.exact_version(value)
        self.assertEqual('5.7.0', prepare.exact_version('[5.7.0, )'))


@unittest.skipUnless(os.environ.get('CSWEET_RUN_DEPENDENCY_INTEGRATION') == '1',
                     'Set CSWEET_RUN_DEPENDENCY_INTEGRATION=1 to run real dotnet/git fixture')
class IntegrationTests(unittest.TestCase):
    def test_clean_package_consumer_builds_with_sdk_testing_and_contracts_unpublished(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            state = root / 'state'
            feed = state / 'feed'
            feed.mkdir(parents=True)
            pins = {'schemaVersion': 1, 'packages': {}}
            repositories = {}
            for name, version in [(CONTRACTS, '5.7.0'), (SDK, '5.28.0')]:
                repo = root / name
                project = repo / 'src' / name
                project.mkdir(parents=True)
                (repo / 'NuGet.Config').write_text(f'<configuration><packageSources><clear/>'
                                                  f'<add key="fixture" value="{feed}" />'
                                                  '</packageSources></configuration>')
                reference = f'<ItemGroup><PackageReference Include="{CONTRACTS}" Version="5.7.0" /></ItemGroup>' if name == SDK else ''
                (project / f'{name}.csproj').write_text(
                    f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                    f'<PackageId>{name}</PackageId><Version>{version}</Version><NuGetAudit>false</NuGetAudit>'
                    f'</PropertyGroup>{reference}</Project>')
                (project / 'Value.cs').write_text('namespace Fixture; public class Contracts { public const int Value = 42; }' if name == CONTRACTS
                                                else 'namespace Fixture; public class Sdk { public static int Value => Contracts.Value; }')
                if name == SDK:
                    testing = repo / 'src' / TESTING
                    testing.mkdir(parents=True)
                    (testing / f'{TESTING}.csproj').write_text(
                        f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                        f'<PackageId>{TESTING}</PackageId><Version>{version}</Version><NuGetAudit>false</NuGetAudit>'
                        f'</PropertyGroup><ItemGroup><ProjectReference Include="../{SDK}/{SDK}.csproj" />'
                        '</ItemGroup></Project>')
                    (testing / 'Testing.cs').write_text(
                        'namespace Fixture; public class Testing { public static int Value => Sdk.Value; }')
                prepare.run(['git', 'init', '--quiet', repo])
                prepare.run(['git', '-C', repo, 'add', '.'])
                prepare.run(['git', '-C', repo, '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                             'commit', '--quiet', '-m', 'Fixture dependency'])
                commit = prepare.run(['git', '-C', repo, 'rev-parse', 'HEAD'])
                pins['packages'][name] = {version: {'commit': commit}}
                repositories[name] = repo

            consumer = root / 'consumer'
            project = consumer / 'src/Agent'
            project.mkdir(parents=True)
            (consumer / 'NuGet.Config').write_text(f'<configuration><packageSources><clear/>'
                                                  f'<add key="fixture" value="{feed}" />'
                                                  '</packageSources></configuration>')
            (project / 'Agent.csproj').write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                '<OutputType>Exe</OutputType><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>'
                f'<PackageReference Include="{SDK}" Version="5.28.0" />'
                f'<PackageReference Include="{TESTING}" Version="5.28.0" /></ItemGroup></Project>')
            (project / 'Program.cs').write_text('System.Console.WriteLine(Fixture.Testing.Value);')

            def execute(args, cwd=None, env=None):
                args = list(args)
                if 'fetch' in args:
                    args[-2] = next(repo for name, repo in repositories.items()
                                    if prepare.SOURCES[name] in str(args[-2]))
                return prepare.run(args, cwd=cwd, env=env)

            resolver = prepare.Resolver(consumer, state, pins,
                                        fetch=lambda url: INDEX if url.endswith('/index.json') else None,
                                        execute=execute)
            resolver.verify_pins()
            values = resolver.prepare()
            env = os.environ.copy()
            env.update(values)
            (root / 'empty-feed').mkdir()
            prepare.run(['dotnet', 'build', project, '-c', 'Release',
                         f'-p:RestoreSources={root / "empty-feed"}'], env=env)
            output = prepare.run(['dotnet', project / 'bin/Release/net10.0/Agent.dll'], env=env)
            self.assertEqual('42', output)
            self.assertEqual(3, len(resolver.resolved))
            self.assertTrue(all(row['origin'] == 'source' for row in resolver.resolved.values()))


if __name__ == '__main__':
    unittest.main()
