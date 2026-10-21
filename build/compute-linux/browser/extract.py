import hashlib
import json
import pathlib
import stat
import subprocess
import sys
import zipfile


def extract(archive_path, destination, expected_hash):
    archive_path = pathlib.Path(archive_path)
    if archive_path.stat().st_size > 16 * 1024 * 1024:
        raise ValueError("build-archive-too-large")
    if hashlib.sha256(archive_path.read_bytes()).hexdigest() != expected_hash:
        raise ValueError("build-digest-mismatch")
    seen = set()
    total = 0
    with zipfile.ZipFile(archive_path) as archive:
        if not 1 <= len(archive.infolist()) <= 10000:
            raise ValueError("build-entry-limit")
        for entry in archive.infolist():
            name = entry.orig_filename
            parts = name.split("/")
            if (not name or name.startswith("/") or "\\" in name or ":" in name
                    or any(part in ("", ".", "..") for part in parts)
                    or any(ord(char) < 32 for char in name)
                    or stat.S_ISLNK(entry.external_attr >> 16)
                    or name.casefold() in seen or entry.is_dir()):
                raise ValueError("unsafe-build-path")
            seen.add(name.casefold())
            total += entry.file_size
            if total > 64 * 1024 * 1024:
                raise ValueError("expanded-build-too-large")
            target = destination.joinpath(*parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, target.open("xb") as output:
                remaining = entry.file_size
                while remaining:
                    chunk = source.read(min(65536, remaining))
                    if not chunk:
                        raise ValueError("truncated-build")
                    output.write(chunk)
                    remaining -= len(chunk)
    if not destination.joinpath("index.html").is_file():
        raise ValueError("missing-build-index")


def main():
    request = json.loads(sys.argv[1])
    build = pathlib.Path("/tmp/build")
    build.mkdir()
    extract("/input/build.zip", build, request["buildSha256"])
    result = subprocess.run(["node", "/opt/browser/runner.cjs", json.dumps(request)],
                            timeout=25, check=False)
    if result.returncode:
        raise ValueError("browser-runner-failed")
    evidence = pathlib.Path("/evidence")
    report = json.loads(evidence.joinpath("result.json").read_text())
    with zipfile.ZipFile(evidence / "evidence.zip", "w", zipfile.ZIP_DEFLATED) as bundle:
        total = 0
        for path in sorted(evidence.iterdir()):
            if path.name == "evidence.zip":
                continue
            total += path.stat().st_size
            if total > 4 * 1024 * 1024:
                raise ValueError("browser-evidence-too-large")
            bundle.write(path, path.name)
    payload = evidence.joinpath("evidence.zip").read_bytes()
    report["evidenceSha256"] = hashlib.sha256(payload).hexdigest()
    report["evidenceBytes"] = len(payload)
    print(json.dumps(report, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"errorCode": type(error).__name__, "message": str(error)[:256]}))
        sys.exit(1)
