#!/usr/bin/env python3
"""Compute the exact semantic UI, CA Overlay and Flowline candidate fingerprint."""

from __future__ import annotations

import hashlib
from pathlib import Path, PurePosixPath
import sys


ALGORITHM = "sha256-candidate-v2"
REQUIRED_RUNTIME_PATHS = (
    "Hatifect UI/Hatifect.UI.Stardew.dll",
    "Hatifect UI/Hatifect.UI.Experience.dll",
    "Hatifect UI/Hatifect.UI.Language.dll",
    "Hatifect UI/Hatifect.UI.Planning.dll",
    "Hatifect UI/Hatifect.UI.Runtime.dll",
    "Hatifect UI/Hatifect.UI.Semantics.dll",
    "Hatifect UI/Hatifect.UI.Tooling.dll",
    "Hatifect UI/Hatifect.UI.DevTools.dll",
    "Hatifect UI/manifest.json",
    "Hatifect Flow/Hatifect.Flow.dll",
    "Hatifect Flow/Hatifect.Flow.Core.dll",
    "Hatifect Flow/Hatifect.Flow.Persistence.dll",
    "Hatifect Flow/manifest.json",
    "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay/"
    "Hatifect.ChestsAnywhereOverlay.dll",
    "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay/"
    "Hatifect.ChestsAnywhereOverlay.UI.Semantic.dll",
    "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay/manifest.json",
    "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay/config.json",
    "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay/i18n/default.json",
    "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay/i18n/ru.json",
)


def _validated_paths(package_root: Path) -> list[tuple[PurePosixPath, Path]]:
    root = package_root.resolve()
    if not root.is_dir():
        raise ValueError(f"Hatifect candidate root does not exist: {root}")
    if root.name != "Hatifect":
        raise ValueError(f"candidate root must be named 'Hatifect'; found {root.name!r}")

    files: list[tuple[PurePosixPath, Path]] = []
    for relative_text in REQUIRED_RUNTIME_PATHS:
        relative = PurePosixPath(relative_text)
        path = root.joinpath(*relative.parts)
        if path.is_symlink():
            raise ValueError(f"candidate runtime file must not be a symlink: {relative}")
        if not path.is_file():
            raise ValueError(f"missing required candidate runtime file: {relative}")
        files.append((relative, path))
    return sorted(files, key=lambda item: item[0].as_posix())


def compute_candidate_fingerprint(package_root: Path) -> str:
    lines: list[str] = []
    for relative, path in _validated_paths(package_root):
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        lines.append(f"{relative.as_posix()}\t{digest}")
    payload = ("\n".join(lines) + "\n").encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: candidate_fingerprint.py <Hatifect package root>", file=sys.stderr)
        return 2
    try:
        print(compute_candidate_fingerprint(Path(sys.argv[1])))
        return 0
    except (OSError, ValueError) as error:
        print(f"Hatifect candidate fingerprint FAILED: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
