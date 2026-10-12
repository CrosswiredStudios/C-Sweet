from pathlib import Path
import subprocess, json, xml.etree.ElementTree as ET, zipfile, re, uuid, sys, hashlib

root = Path(__file__).resolve().parents[1]
parent = root.parent
output = root/'artifacts'/'delegation-validation'
feed = output/'feed'
feed.mkdir(parents=True, exist_ok=True)
cache = output/('packages-' + uuid.uuid4().hex[:12])
if '--inspect-only' not in sys.argv:
    (output/'validation-cache.txt').write_text(str(cache), encoding='utf-8')
config = output/'NuGet.config'
config.write_text(f'<configuration><packageSources><clear/><add key="delegation" value="{feed}"/><add key="cache" value="{Path.home()/".nuget"/"packages"}"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>', encoding='utf-8')
summary = []

def run(label, args, cwd):
    print(label, flush=True)
    logfile = output/(re.sub(r'[^a-zA-Z0-9.-]', '-', label)+'.log')
    with logfile.open('w', encoding='utf-8') as log:
        result = subprocess.run(args, cwd=cwd, stdout=log, stderr=subprocess.STDOUT)
    summary.append({'check': label, 'exitCode': result.returncode, 'log': str(logfile)})
    (output/'results.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(f'{label}: {result.returncode}', flush=True)
    return result.returncode == 0

def package_only(project, label, test=False):
    repo = next(p for p in project.parents if p.parent == parent)
    args = ['-p:UseLocalCSweetAgentSdk=false','-p:UseLocalCSweetWorkManagementContracts=false',f'-p:ArtifactsPath={repo/"artifacts"/"delegation-validation"}']
    if not run(label+' restore', ['dotnet','restore',str(project),*args,'--packages',str(cache),
        '--configfile',str(config)], project.parent): return False
    return run(label, ['dotnet','test' if test else 'build',str(project),'-c','Release','--no-restore',*args], project.parent)

def inspect_packages():
    expected = {}
    selected = [parent/'CSweet.Agent.Sdk/src/CSweet.Agent.SDK/CSweet.Agent.SDK.csproj',
                parent/'CSweet.WorkManagement.Contracts/src/CSweet.WorkManagement.Contracts/CSweet.WorkManagement.Contracts.csproj',
                parent/'CSweet.Memory/src/CSweet.Memory.Broker/CSweet.Memory.Broker.csproj']
    agent_count = 0
    for repo in sorted(parent.glob('CSweet.Agent.*')):
        manifest = repo/'csweet-plugin.json'
        if manifest.exists() and any(x.get('name') == 'work.delegation.create.v1' for x in json.loads(manifest.read_text(encoding='utf-8-sig')).get('requires', [])):
            selected.extend((repo/'src').rglob('*.csproj'))
            agent_count += 1
    assert agent_count == 23, agent_count
    for project in selected:
        doc = ET.parse(project)
        expected[doc.findtext('.//PackageId') or project.stem] = doc.findtext('.//Version')
    packages = []
    for package in sorted(feed.glob('*.nupkg')):
        with zipfile.ZipFile(package) as z:
            nuspec = ET.fromstring(z.read(next(n for n in z.namelist() if n.endswith('.nuspec'))))
            fields = {e.tag.split('}')[-1]:e.text for e in nuspec.iter() if e.tag.split('}')[-1] in ('id','version')}
            assert expected.get(fields['id']) == fields['version'], fields
            if fields['id'] in ('CSweet.Agent.SDK', 'CSweet.WorkManagement.Contracts'):
                for name in z.namelist():
                    if name.startswith('lib/') and name.endswith('.dll'):
                        installed = cache/fields['id'].lower()/fields['version']/name
                        assert hashlib.sha256(z.read(name)).digest() == hashlib.sha256(installed.read_bytes()).digest(), installed
            packages.append({**fields, 'sha256': hashlib.sha256(package.read_bytes()).hexdigest()})
    assert set(x['id'] for x in packages) == set(expected), (packages, expected)
    (output/'packages.json').write_text(json.dumps(packages, indent=2), encoding='utf-8')
    print('Verified',len(packages),'package versions and final SDK/contracts consumer binary hashes',flush=True)

if '--inspect-only' in sys.argv:
    cache = Path((output/'validation-cache.txt').read_text(encoding='utf-8'))
    inspect_packages()
    sys.exit(0)

contracts = parent/'CSweet.WorkManagement.Contracts'
run('Contracts tests', ['dotnet','test',str(next((contracts/'tests').rglob('*.csproj'))),'-c','Release'], contracts)
run('Contracts pack', ['dotnet','pack',str(contracts/'src'/'CSweet.WorkManagement.Contracts'/'CSweet.WorkManagement.Contracts.csproj'),'-c','Release','-o',str(feed)], contracts)
sdk = parent/'CSweet.Agent.Sdk'
package_only(sdk/'tests'/'CSweet.Agent.SDK.Tests'/'CSweet.Agent.SDK.Tests.csproj', 'SDK package-only tests', True)
run('SDK pack', ['dotnet','pack',str(sdk/'src'/'CSweet.Agent.SDK'/'CSweet.Agent.SDK.csproj'),'-c','Release','--no-restore','-o',str(feed),'-p:UseLocalCSweetWorkManagementContracts=false',f'-p:ArtifactsPath={sdk/"artifacts"/"delegation-validation"}'], sdk)

memory = parent/'CSweet.Memory'
broker = memory/'src'/'CSweet.Memory.Broker'/'CSweet.Memory.Broker.csproj'
if package_only(broker, 'Memory broker package-only build'):
    run('Memory broker pack', ['dotnet','pack',str(broker),'-c','Release','--no-restore','-o',str(feed),
        '-p:UseLocalCSweetAgentSdk=false','-p:UseLocalCSweetWorkManagementContracts=false',f'-p:ArtifactsPath={memory/"artifacts"/"delegation-validation"}'], memory)

for repo in sorted(parent.glob('CSweet.Agent.*')):
    manifest_path = repo/'csweet-plugin.json'
    if not manifest_path.exists(): continue
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    if not any(x.get('name') == 'work.delegation.create.v1' for x in manifest.get('requires',[])): continue
    version = manifest['version']
    note = repo/'releases'/f'{version}.md'
    assert note.exists() and version in note.read_text(encoding='utf-8').splitlines()[0], (repo,version)
    projects = list((repo/'src').rglob('*.csproj'))
    for project in projects:
        doc = ET.parse(project)
        own = doc.find('.//Version')
        if own is not None: assert own.text == version, (project,own.text,version)
    tests = list((repo/'tests').rglob('*.csproj'))
    for project in tests: package_only(project, repo.name+' package-only tests', True)
    for project in projects:
        if package_only(project, repo.name+' package-only build'):
            run(repo.name+' pack', ['dotnet','pack',str(project),'-c','Release','--no-restore','-o',str(feed),
                '-p:UseLocalCSweetAgentSdk=false','-p:UseLocalCSweetWorkManagementContracts=false',f'-p:ArtifactsPath={repo/"artifacts"/"delegation-validation"}'], repo)
    # Agent runner self-tests are a package release requirement.
    for project in projects:
        run(repo.name+' self-test', ['dotnet','run','--project',str(project),'-c','Release','--no-build',f'-p:ArtifactsPath={repo/"artifacts"/"delegation-validation"}','--','--self-test'], repo)

inspect_packages()
print('Completed', len(summary), 'checks; failures:', sum(x['exitCode'] != 0 for x in summary), flush=True)

sys.exit(1 if any(x["exitCode"] != 0 for x in summary) else 0)
