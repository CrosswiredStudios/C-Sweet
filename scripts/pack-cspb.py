#!/usr/bin/env python3
"""Pack a prebuilt Linux service or connector as a C-Sweet plugin bundle (.cspb)."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import tarfile

MAX_BYTES = 512 * 1024 * 1024


def safe_path(name):
    path = PurePosixPath(name)
    if not name or "\\" in name or path.is_absolute() or any(p in ("", ".", "..") for p in name.split("/")) or any(ord(c) < 32 for c in name):
        raise ValueError(f"Invalid bundle path: {name}")
    return path


def add(writer, name, content, mode=0o644):
    safe_path(name)
    info = tarfile.TarInfo(name)
    info.size = len(content)
    info.mode = mode
    info.uid = info.gid = info.mtime = 0
    writer.addfile(info, io.BytesIO(content))


def pack(args):
    manifest_path = args.manifest.resolve(strict=True)
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes.decode("utf-8-sig"))
    if manifest.get("manifestVersion") != "2.0" or manifest.get("kind") not in ("service", "connector"):
        raise ValueError("A .cspb must contain a version 2.0 service or connector manifest.")
    executable = PurePosixPath(manifest["runtime"]["projectPath"].replace("\\", "/")).stem
    publish = args.publish_dir.resolve(strict=True)
    if not publish.is_dir() or not (publish / executable).is_file():
        raise ValueError(f"Publish self-contained Linux output first; missing executable '{executable}'.")
    output = args.out.resolve()
    if output.suffix.lower() != ".cspb" or output == publish or publish in output.parents:
        raise ValueError("Choose a .cspb output path outside the publish directory.")
    if output.exists():
        raise ValueError("Output already exists; choose a new file name.")
    files = {}
    for file in sorted(publish.rglob("*")):
        if file.is_symlink() or file.is_junction():
            raise ValueError(f"Publish output contains a link: {file}")
        if file.is_file():
            name = file.relative_to(publish).as_posix()
            safe_path(name)
            if name == "csweet-plugin.json":
                continue
            files[name] = file
    for group in ("workstreamProfiles", "toolchainAdapters"):
        for contribution in manifest.get(group, {}).get("provides", []):
            resource = str(safe_path(contribution["definitionResource"]))
            if resource not in files:
                source = (manifest_path.parent / resource).resolve(strict=True)
                if manifest_path.parent not in source.parents:
                    raise ValueError("Contribution resource must stay inside the plugin repository.")
                files[resource] = source
    names = ["csweet-plugin.json", *files]
    if len(names) > 9999 or len(set(n.casefold() for n in names)) != len(names):
        raise ValueError("Too many files or case-colliding payload paths.")
    if len(manifest_bytes) > 1024 * 1024 or sum(f.stat().st_size for f in files.values()) + len(manifest_bytes) > MAX_BYTES:
        raise ValueError("Manifest or payload exceeds the upload limits.")
    metadata = json.dumps({"formatVersion": "1.0", "operatingSystem": "linux", "architecture": args.arch, "entrypoint": [executable]}, separators=(",", ":")).encode()
    created = False
    try:
        with output.open("xb") as target:
            created = True
            with tarfile.open(fileobj=target, mode="w", format=tarfile.PAX_FORMAT) as writer:
                add(writer, "artifact.json", metadata)
                add(writer, "payload/csweet-plugin.json", manifest_bytes)
                for name, file in sorted(files.items()):
                    add(writer, "payload/" + name, file.read_bytes(), 0o755 if name == executable or name.endswith(".sh") else 0o644)
        if output.stat().st_size > MAX_BYTES:
            raise ValueError("Bundle exceeds the 512 MB upload limit.")
    except BaseException:
        if created and output.exists():
            output.unlink()
        raise
    with output.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    print(f"sha256:{digest}  {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--arch", choices=("x64", "arm64"), default="x64")
    parser.add_argument("--out", type=Path, required=True)
    try:
        pack(parser.parse_args())
    except (ValueError, OSError, KeyError, tarfile.TarError) as error:
        parser.exit(1, f"error: {error}\n")
