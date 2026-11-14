"""Verify exact bundled-extension bytes and exercise Git checkout normalization."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tempfile


def validate(root):
    extension = root / 'extensions/video-game'
    manifest_bytes = (extension / 'extension.json').read_bytes()
    if b'\r' in manifest_bytes:
        raise ValueError('Extension manifest must use LF line endings: extension.json')
    manifest = json.loads(manifest_bytes.decode('utf-8-sig'))
    files = manifest['files']
    if not isinstance(files, dict) or 'VideoGameAgentKit.cs' not in files:
        raise ValueError('The extension manifest must pin VideoGameAgentKit.cs')
    for name, expected in files.items():
        relative = PurePosixPath(name)
        if relative.is_absolute() or '..' in relative.parts or '\\' in name or ':' in name:
            raise ValueError(f'Invalid extension path: {name}')
        if not isinstance(expected, str) or not re.fullmatch('[0-9a-f]{64}', expected):
            raise ValueError(f'Invalid extension SHA-256: {name}')
        content = (extension / name).read_bytes()
        actual = hashlib.sha256(content).hexdigest()
        if actual != expected:
            raise ValueError(f'Extension hash mismatch: {name}; expected {expected}, got {actual}')
        if relative.suffix in {'.cs', '.md', '.json'} and b'\r' in content:
            raise ValueError(f'Extension text must use LF line endings: {name}')
    return files


def git(args, cwd):
    return subprocess.check_output(['git', *args], cwd=cwd, text=True, stderr=subprocess.STDOUT)


def self_test(root):
    """Stage Windows text, then verify exact checkout bytes for all autocrlf modes."""
    files = validate(root)
    with tempfile.TemporaryDirectory(prefix='csweet-extension-checkout-') as directory:
        temporary = Path(directory)
        source = temporary / 'source'
        extension = source / 'extensions/video-game'
        extension.mkdir(parents=True)
        # Test the repository's real attributes, manifest and role-adapted source.
        (source / '.gitattributes').write_bytes((root / '.gitattributes').read_bytes())
        for name in [*files, 'extension.json']:
            path = extension / name
            path.parent.mkdir(parents=True, exist_ok=True)
            content = (root / 'extensions/video-game' / name).read_bytes()
            path.write_bytes(content.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n'))
        git(['init', '--quiet'], source)
        git(['-c', 'core.autocrlf=true', '-c', 'core.safecrlf=false',
             'add', '--', '.gitattributes', 'extensions/video-game'], source)
        for mode in ('true', 'false', 'input'):
            checkout = temporary / mode
            checkout.mkdir()
            git(['-c', f'core.autocrlf={mode}', 'checkout-index', '--all',
                 f'--prefix={checkout.as_posix()}/'], source)
            validate(checkout)
        target = checkout / 'extensions/video-game/VideoGameAgentKit.cs'
        target.write_bytes(target.read_bytes() + b'\n// Changed content must fail provenance.\n')
        try:
            validate(checkout)
        except ValueError as error:
            if 'Extension hash mismatch' not in str(error):
                raise
        else:
            raise AssertionError('Modified extension content passed provenance validation')
        target.unlink()
        try:
            validate(checkout)
        except FileNotFoundError:
            pass
        else:
            raise AssertionError('Missing extension content passed provenance validation')
    print('Extension checkout regression passed: autocrlf=true/false/input, tampering and missing files.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    root = args.root.resolve()
    files = validate(root)
    if args.self_test:
        self_test(root)
    print(f'Bundled extension provenance passed: {len(files)} exact file hashes.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, AssertionError, subprocess.CalledProcessError) as error:
        sys.exit(f'Bundled extension verification failed: {error}')
