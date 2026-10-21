import hashlib
import json
import pathlib
import subprocess
import tempfile
import uuid
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
artifacts = root / "artifacts/browser-compute"
artifacts.mkdir(parents=True, exist_ok=True)
fixture = "<html><head><link rel='icon' href='data:,'></head><body><canvas width='64' height='64'></canvas><script>const context=document.querySelector('canvas').getContext('webgl');context.clearColor(0,1,0,1);context.clear(context.COLOR_BUFFER_BIT);</script></body></html>"
cases = [("webgl", fixture, True), ("runtime-error", fixture + "<script>throw Error('fixture-runtime-error')</script>", False),
         ("missing-asset", fixture + "<script src='/missing.js'></script>", False),
         ("no-webgl", "<html><link rel='icon' href='data:,'><canvas></canvas></html>", False),
         ("external-request", fixture + "<script src='http://192.0.2.1/blocked.js'></script>", False)]
results = []
for name, html, expected in cases:
    with tempfile.TemporaryDirectory(prefix="fixture-", dir=artifacts) as temporary:
        directory = pathlib.Path(temporary)
        evidence = directory / "evidence"
        evidence.mkdir()
        with zipfile.ZipFile(directory / "build.zip", "w") as archive:
            archive.writestr("index.html", html)
        build_hash = hashlib.sha256(directory.joinpath("build.zip").read_bytes()).hexdigest()
        request = {"requestId": str(uuid.uuid4()), "sourceCommitSha": "a" * 40, "buildSha256": build_hash,
                   "viewportWidths": [375, 1280], "requireWebGl": True}
        process = subprocess.run(["docker", "run", "--rm", "--init", "--network=none", "--read-only",
                                  "--cap-drop=ALL", "--security-opt=no-new-privileges", "--memory=1536m", "--cpus=2",
                                  "--pids-limit=256", "--shm-size=256m", "--tmpfs=/tmp:rw,size=256m",
                                  "--mount", f"type=bind,src={directory},dst=/input,readonly",
                                  "--mount", f"type=bind,src={evidence},dst=/evidence",
                                  "csweet/browser-tests:1.64.0", json.dumps(request)],
                                 capture_output=True, text=True, timeout=40, check=False)
        if process.returncode:
            raise AssertionError(name + ": " + process.stdout + process.stderr)
        report = json.loads(process.stdout)
        assert report["passed"] == expected, (name, report)
        assert report["buildSha256"] == build_hash
        assert report["sourceCommitSha"] == request["sourceCommitSha"]
        assert [viewport["width"] for viewport in report["viewports"]] == [375, 1280]
        payload = evidence.joinpath("evidence.zip").read_bytes()
        assert hashlib.sha256(payload).hexdigest() == report["evidenceSha256"]
        with zipfile.ZipFile(evidence / "evidence.zip") as bundle:
            assert all(f"viewport-{width}.png" in bundle.namelist() and f"trace-{width}.zip" in bundle.namelist()
                       for width in [375, 1280])
        results.append({"fixture": name, "expectedBrowserPass": expected, "report": report})
        artifacts.joinpath(name + ".zip").write_bytes(payload)
        print(name + ": passed", flush=True)
artifacts.joinpath("results.json").write_text(json.dumps(results, indent=2))
print("All five real Chromium fixtures passed.")
