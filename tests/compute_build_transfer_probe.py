"""Run exported ComputeBuildTransferTests commands in a disposable, networkless Linux container."""
import base64
import json
from pathlib import Path
import subprocess
import sys

probe = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
payload = base64.b64encode(json.dumps(probe).encode()).decode()
script = r'''
import base64,hashlib,json,os,pathlib,re,subprocess,tempfile
p=json.loads(base64.b64decode("PAYLOAD"))
original=re.search(r"for part in '([^']+)'",p['commands'][0]).group(1)
root=pathlib.Path(tempfile.mkdtemp(prefix='build-transfer-probe-'))
def commands(case):
    target=root/case
    return target,[s.replace(original,str(target).lstrip('/')) for s in p['commands']]
def run(s,ok=True):
    r=subprocess.run(['/usr/bin/python3','-c',s],capture_output=True)
    assert (r.returncode==0)==ok,(r.returncode,r.stderr.decode()[:300])
    return r.stdout.decode().strip()
def verify(target):
    b=(target/'build.zip').read_bytes()
    assert len(b)==p['length'] and hashlib.sha256(b).hexdigest()==p['sha256']
    assert (target/'evidence').is_dir()
t,c=commands('complete')
for s in c: run(s)
verify(t)
assert run(c[0])=='complete'
os.rmdir(t/'evidence')
assert run(c[0])=='complete'
verify(t)
t,c=commands('interrupted')
for s in c[:5]: run(s)
for s in c: run(s)
verify(t)
t,c=commands('conflict')
run(c[0]);run(c[1]);f=t/'.build-upload'
b=f.read_bytes();f.write_bytes(bytes([b[0]^1])+b[1:]);run(c[1],False)
assert not (t/'build.zip').exists()
t,c=commands('symlink-part')
run(c[0]);outside=root/'outside';outside.write_bytes(b'untouched')
(t/'.build-upload').symlink_to(outside);run(c[1],False)
assert outside.read_bytes()==b'untouched'
t,c=commands('symlink-directory')
t.mkdir();(t/'evidence').symlink_to(root,target_is_directory=True);run(c[0],False)
print(json.dumps({'state':'Passed','cases':6,'bytes':p['length'],'sha256':p['sha256']}))
'''.replace("PAYLOAD", payload)
result = subprocess.run(["docker", "run", "--rm", "-i", "--network", "none", "--entrypoint", "/usr/bin/python3",
                         "csweet/browser-tests:1.64.0", "-"], input=script, text=True, capture_output=True)
print(result.stdout, end="")
if result.returncode:
    print(result.stderr, file=sys.stderr)
sys.exit(result.returncode)
