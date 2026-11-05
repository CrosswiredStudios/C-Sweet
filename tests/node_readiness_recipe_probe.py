"""Exercise the SDK's real npm readiness command in a networkless disposable container."""
import json
from pathlib import Path
import re
import subprocess

source = Path(__file__).resolve().parents[2] / 'CSweet.Agent.Sdk/src/CSweet.Agent.SDK/DeliveryComputeTest.cs'
recipe = re.search(r"sh -c '([^']+)' > validation.log", source.read_text(encoding='utf-8-sig')).group(1)
script = r'''
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),cp=require('node:child_process');
const recipe=RECIPE;
for(const kind of ['absent','passing','failing']){
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'node-readiness-'));
 const scripts={build:`node -e "require('node:fs').mkdirSync('dist');require('node:fs').writeFileSync('dist/index.html','<html>built</html>')"`};
 if(kind!=='absent')scripts.test=`node -e "require('node:fs').writeFileSync('test-ran','yes');process.exit(${kind==='failing'?7:0})"`;
 fs.writeFileSync(path.join(dir,'package.json'),JSON.stringify({scripts}));
 const run=cp.spawnSync('/bin/sh',['-c',recipe],{cwd:dir,encoding:'utf8',env:{...process.env,CI:'true',NPM_CONFIG_UPDATE_NOTIFIER:'false'}});
 const built=fs.existsSync(path.join(dir,'dist/index.html')),ran=fs.existsSync(path.join(dir,'test-ran'));
 if((run.status===0)!==(kind!=='failing') || built!==(kind!=='failing') || ran!==(kind!=='absent'))throw Error(kind+': '+run.stderr);
 console.log(JSON.stringify({case:kind,state:'Passed',exitCode:run.status,built,testRan:ran}));
}
'''.replace('RECIPE', json.dumps(recipe))
result = subprocess.run(['docker','run','--rm','-i','--network','none','--entrypoint','node',
                         'node:24.15.0-bookworm-slim','-'],input=script,text=True,capture_output=True)
print(result.stdout,end='')
if result.returncode:
    raise RuntimeError(result.stderr)
