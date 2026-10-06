"""Build the platform against local handoff packages with sibling references disabled."""
import json
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[1]
validation = root / 'artifacts/hierarchical-validation'
validation.mkdir(parents=True, exist_ok=True)
feed = root / 'artifacts/hierarchical-packages'
cache = root / 'artifacts/hierarchical-platform-package-cache'
flags = [f'-p:{name}=false' for name in (
    'UseLocalCSweetAgentSdk', 'UseLocalCSweetWorkManagementContracts',
    'UseLocalCSweetMemory', 'UseLocalOfficeContracts', 'UseLocalIsolation')]
flags += ['-p:NuGetAudit=false', f'-p:RestorePackagesPath={cache}']
results = []
for project in ('tests/CSweet.UnitTests/CSweet.UnitTests.csproj', 'src/CSweet.GitHost/CSweet.GitHost.csproj'):
    result = {'project': project}
    with (validation / (Path(project).stem + '-package-consumer.log')).open('w', encoding='utf-8') as log:
        commands = {
            'restore': ['dotnet', 'restore', project, '--source', str(feed), '--source',
                        str(Path.home() / '.nuget/packages'), '-m:1', *flags],
            'build': ['dotnet', 'build', project, '--no-restore', '-c', 'HierarchicalPackages', '-m:1', *flags],
        }
        for stage, command in commands.items():
            log.write(stage + '\n'); log.flush()
            result[stage] = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT).returncode
            if result[stage]: break
    if result.get('build') == 0:
        assets = json.loads((root / Path(project).parent / 'obj/project.assets.json').read_text(encoding='utf-8-sig'))
        for name, package in assets['libraries'].items():
            if name.startswith(('CSweet.Agent.SDK/', 'CSweet.WorkManagement.Contracts/', 'CSweet.Memory', 'CSweet.Office.Contracts/', 'CSweet.Isolation.')):
                assert package['type'] == 'package', (project, name, package)
    results.append(result)
    print(json.dumps(result), flush=True)
(validation / 'platform-package-consumers.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
raise SystemExit(1 if any(result.get('build', 1) for result in results) else 0)
