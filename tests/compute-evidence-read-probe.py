"""Run the exact exported guest command in a disposable offline Linux fixture.
Export with CSWEET_EVIDENCE_PROBE_EXPORT while running ComputeEvidenceTransferTests.
"""
import json
import subprocess
import sys
from pathlib import Path

probe = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
fixture = """import json,os,subprocess,sys
p=json.loads(PROBE)
os.makedirs(os.path.dirname(p['path']),exist_ok=True)
with open(p['path'],'wb') as f:f.write(b'B'*p['length'])
r=subprocess.run([sys.executable,'-c',p['script']],capture_output=True)
assert r.returncode==0,(r.returncode,r.stderr)
assert r.stdout==b'B'*min(262144,p['length'])
os.unlink(p['path']);os.symlink('/etc/passwd',p['path'])
r=subprocess.run([sys.executable,'-c',p['script']],capture_output=True);assert r.returncode!=0 and not r.stdout
os.unlink(p['path'])
with open(p['path'],'wb') as f:f.write(b'B'*(p['length']+1))
r=subprocess.run([sys.executable,'-c',p['script']],capture_output=True);assert r.returncode!=0 and not r.stdout
parent=os.path.dirname(p['path']);os.rename(parent,parent+'-real');os.symlink(parent+'-real',parent)
r=subprocess.run([sys.executable,'-c',p['script']],capture_output=True);assert r.returncode!=0 and not r.stdout
print('PASS: exact guest read, file/directory symlink rejection, wrong-size rejection')
""".replace("PROBE", repr(json.dumps(probe)))
result = subprocess.run(["docker", "run", "--rm", "-i", "--network=none", "--read-only",
    "--tmpfs", "/var/lib/csweet-compute/work:rw,size=16m", "--entrypoint", "python3",
    "csweet/browser-tests:1.64.0", "-"], input=fixture.encode(), capture_output=True, timeout=45)
print(result.stdout.decode())
if result.returncode:
    print(result.stderr.decode()[-2000:])
raise SystemExit(result.returncode)
