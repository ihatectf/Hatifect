#!/usr/bin/env python3
"""Compute the exact Hatifect UI runtime fingerprint used by real-host acceptance evidence."""
from __future__ import annotations

import hashlib
from pathlib import Path
import sys

ALGORITHM = 'sha256-runtime-v2'
REQUIRED_RUNTIME_DLLS = (
    'Hatifect.UI.Stardew.dll',
    'Hatifect.UI.Experience.dll',
    'Hatifect.UI.Language.dll',
    'Hatifect.UI.Planning.dll',
    'Hatifect.UI.Runtime.dll',
    'Hatifect.UI.Semantics.dll',
    'Hatifect.UI.Tooling.dll',
    'Hatifect.UI.DevTools.dll',
)
REQUIRED_ROOT_FILES = (*REQUIRED_RUNTIME_DLLS, 'manifest.json')


def _runtime_files(root: Path) -> list[Path]:
    missing = [name for name in REQUIRED_ROOT_FILES if not (root / name).is_file()]
    if missing:
        raise ValueError('missing required Hatifect UI runtime files: ' + ', '.join(missing))

    files = [root / name for name in REQUIRED_ROOT_FILES]
    assets = root / 'assets'
    if assets.exists() and not assets.is_dir():
        raise ValueError('Hatifect UI runtime assets path must be a directory')
    if assets.is_dir():
        files.extend(path for path in assets.rglob('*') if path.is_file())
    return sorted(files, key=lambda path: path.relative_to(root).as_posix())


def compute_runtime_fingerprint(root: Path) -> str:
    root = root.resolve()
    if not root.is_dir():
        raise ValueError(f'Hatifect UI runtime root does not exist: {root}')

    lines: list[str] = []
    for path in _runtime_files(root):
        relative = path.relative_to(root).as_posix()
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        lines.append(f'{relative}\t{digest}')
    payload = ('\n'.join(lines) + '\n').encode('utf-8')
    return hashlib.sha256(payload).hexdigest()


def main() -> int:
    if len(sys.argv) != 2:
        print('usage: runtime_fingerprint.py <Hatifect UI runtime root>', file=sys.stderr)
        return 2
    try:
        print(compute_runtime_fingerprint(Path(sys.argv[1])))
        return 0
    except (OSError, ValueError) as exc:
        print(f'Hatifect UI runtime fingerprint FAILED: {exc}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
