"""Offline package-consumer verification; logs each independent agent without publishing."""
import json, subprocess, sys
from pathlib import Path
root = Path(__file__).resolve().parents[2]
output = root / 'csweet/artifacts/hierarchical-validation'
output.mkdir(parents=True, exist_ok=True)
feed = root / 'csweet/artifacts/hierarchical-packages'
cache = root / 'csweet/artifacts/hierarchical-consumer-cache'
repos = ['CreativeDirector.VideoGame','Producer.VideoGame','TechnicalDirector.VideoGame','Engineer.VideoGame','QA.VideoGame','BuildReleaseEngineer.VideoGame','ArtDirector.VideoGame','Artist.VideoGame','AudioDesigner.VideoGame','GameDesigner','LevelDesigner.VideoGame','NarrativeDesigner.VideoGame','PlaytestResearcher.VideoGame','TechnicalArtist.VideoGame','UiUxAccessibilityDesigner.VideoGame','SoftwareProductManager','SoftwareArchitect','SoftwareDeveloper','SoftwareQA']
flags = ['-p:UseLocalCSweetAgentSdk=false','-p:UseLocalCSweetWorkManagementContracts=false','-p:NuGetAudit=false',f'-p:RestorePackagesPath={cache}']
results = []
for name in repos:
    repo = root / ('CSweet.Agent.'+name)
    manifest = json.loads((repo/'csweet-plugin.json').read_text(encoding='utf-8-sig'))
    project = repo / manifest['runtime']['projectPath']
    solution = next(repo.glob('*.sln*'))
    logpath = output / (name+'.log')
    result = {'repository':repo.name,'version':manifest['version']}
    with logpath.open('w',encoding='utf-8') as log:
        commands = {
            'restore':['dotnet','restore',str(solution),'--source',str(feed),'--source',str(Path.home()/'.nuget/packages'),*flags],
            'test':['dotnet','test',str(solution),'--no-restore','-c','Release','-m:1',*flags],
            'self-test':['dotnet','run','--project',str(project),'--no-build','--no-restore','-c','Release','--','--self-test'],
            'pack':['dotnet','pack',str(project),'--no-build','--no-restore','-c','Release','-o',str(feed),*flags],
        }
        for stage, command in commands.items():
            log.write('\n'+stage+'\n'); log.flush()
            code = subprocess.run(command,cwd=repo,stdout=log,stderr=subprocess.STDOUT).returncode
            result[stage] = code
            if code: break
    results.append(result)
    (output/'agents.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
    print(json.dumps(result),flush=True)
sys.exit(1 if any(any(x.get(stage,1) for stage in ['restore','test','self-test','pack']) for x in results) else 0)
