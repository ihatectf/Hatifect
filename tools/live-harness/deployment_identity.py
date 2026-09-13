#!/usr/bin/env python3
"""Bind an isolated Hatifect deployment to one exact checkout and release-contract file set."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import re
import stat
import tempfile
from pathlib import Path, PurePosixPath
from typing import Any

FORMAT_VERSION = 1
HEAD = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^[0-9a-f]{64}$")
MARKER_FIELDS = {
    "formatVersion",
    "preparedAtUtc",
    "repositoryHead",
    "deploymentFingerprint",
    "fileCount",
}


class DeploymentIdentityError(ValueError):
    pass


def _validate_head(value: Any) -> str:
    if not isinstance(value, str) or HEAD.fullmatch(value) is None:
        raise DeploymentIdentityError(
            "Deployment identity requires an exact lowercase 40-hex Git SHA."
        )
    return value


def _parse_timestamp(value: Any) -> None:
    if not isinstance(value, str) or not value:
        raise DeploymentIdentityError("Deployment identity timestamp is missing.")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise DeploymentIdentityError("Deployment identity timestamp is invalid.") from error
    if parsed.tzinfo is None:
        raise DeploymentIdentityError(
            "Deployment identity timestamp must include an offset."
        )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _relative(value: Any, field: str) -> PurePosixPath:
    if not isinstance(value, str) or not value or "\\" in value or "\0" in value:
        raise DeploymentIdentityError(f"Release contract has invalid {field}: {value!r}")
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or str(path) != value or value == ".":
        raise DeploymentIdentityError(f"Release contract has invalid {field}: {value!r}")
    return path


def _load_contract(contract_path: Path) -> tuple[set[PurePosixPath], set[PurePosixPath]]:
    try:
        document = json.loads(contract_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise DeploymentIdentityError(f"Could not read release contract: {error}") from error
    if not isinstance(document, dict) or document.get("FormatVersion") != 1:
        raise DeploymentIdentityError("Deployment identity requires release contract format v1.")
    if document.get("PackageRoot") != "Hatifect":
        raise DeploymentIdentityError("Deployment identity requires PackageRoot 'Hatifect'.")
    modules = document.get("Modules")
    if not isinstance(modules, list) or not modules:
        raise DeploymentIdentityError("Release contract has no runtime modules.")

    expected: set[PurePosixPath] = set()
    mutable_config: set[PurePosixPath] = set()
    module_paths: set[PurePosixPath] = set()
    for module in modules:
        if not isinstance(module, dict):
            raise DeploymentIdentityError("Release contract module is not an object.")
        module_path = _relative(module.get("Path"), "module Path")
        if module_path in module_paths:
            raise DeploymentIdentityError("Release contract repeats a module Path.")
        module_paths.add(module_path)
        root_files = module.get("AllowedRootFiles")
        runtime_files = module.get("AllowedRuntimeFiles", [])
        if not isinstance(root_files, list) or not isinstance(runtime_files, list):
            raise DeploymentIdentityError("Release contract module file inventory is invalid.")
        for name in root_files:
            relative = module_path / _relative(name, "AllowedRootFiles entry")
            if relative in expected:
                raise DeploymentIdentityError("Release contract repeats a runtime file.")
            expected.add(relative)
        for name in runtime_files:
            relative = module_path / _relative(name, "AllowedRuntimeFiles entry")
            if relative in expected:
                raise DeploymentIdentityError("Release contract repeats a runtime file.")
            expected.add(relative)
        # SMAPI config written next to a module is mutable runtime state. If a module
        # ships config.json itself, the exact package copy remains fingerprinted above.
        config = module_path / "config.json"
        if config not in expected:
            mutable_config.add(config)
    return expected, mutable_config


def _allowed_runtime_mutation(
    relative: PurePosixPath, mutable_config: set[PurePosixPath]
) -> bool:
    return relative in mutable_config or ".acceptance" in relative.parts


def deployment_fingerprint(
    mods_root: Path, contract_path: Path
) -> tuple[str, int]:
    root = mods_root.resolve(strict=True)
    hatifect_path = root / "Hatifect"
    info = hatifect_path.lstat()
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode):
        raise DeploymentIdentityError("Isolated Hatifect deployment must be a real directory.")
    hatifect = hatifect_path.resolve(strict=True)
    if hatifect.parent != root:
        raise DeploymentIdentityError("Isolated Hatifect deployment escapes the Mods root.")

    expected, mutable_config = _load_contract(contract_path.resolve(strict=True))
    entries: list[tuple[str, str]] = []
    observed: set[PurePosixPath] = set()
    for candidate in sorted(hatifect.rglob("*"), key=lambda path: path.as_posix()):
        relative = PurePosixPath(candidate.relative_to(hatifect).as_posix())
        candidate_info = candidate.lstat()
        if stat.S_ISLNK(candidate_info.st_mode):
            raise DeploymentIdentityError(
                f"Isolated deployment contains a symlink: {relative.as_posix()}"
            )
        if stat.S_ISDIR(candidate_info.st_mode):
            continue
        if not stat.S_ISREG(candidate_info.st_mode):
            raise DeploymentIdentityError(
                f"Isolated deployment contains a non-regular file: {relative.as_posix()}"
            )
        if relative in expected:
            observed.add(relative)
            entries.append((relative.as_posix(), _sha256(candidate)))
            continue
        if _allowed_runtime_mutation(relative, mutable_config):
            continue
        raise DeploymentIdentityError(
            f"Isolated deployment contains an unexpected non-runtime file: {relative.as_posix()}"
        )

    missing = sorted(path.as_posix() for path in expected - observed)
    if missing:
        raise DeploymentIdentityError(
            "Isolated deployment is missing release-contract file(s): " + ", ".join(missing)
        )
    if not entries:
        raise DeploymentIdentityError("Isolated Hatifect deployment contains no release files.")
    digest = hashlib.sha256()
    for relative, file_digest in entries:
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(file_digest.encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest(), len(entries)


def _atomic_write(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory_descriptor = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def write_identity(
    marker: Path, repository_head: str, mods_root: Path, contract_path: Path
) -> dict[str, Any]:
    head = _validate_head(repository_head)
    fingerprint, file_count = deployment_fingerprint(mods_root, contract_path)
    payload = {
        "formatVersion": FORMAT_VERSION,
        "preparedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "repositoryHead": head,
        "deploymentFingerprint": fingerprint,
        "fileCount": file_count,
    }
    _atomic_write(marker, payload)
    return payload


def validate_identity(
    marker: Path, repository_head: str, mods_root: Path, contract_path: Path
) -> dict[str, Any]:
    head = _validate_head(repository_head)
    try:
        document = json.loads(marker.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise DeploymentIdentityError(f"Could not read deployment identity marker: {error}") from error
    if not isinstance(document, dict) or set(document) != MARKER_FIELDS:
        raise DeploymentIdentityError("Deployment identity marker has an invalid field set.")
    if document["formatVersion"] != FORMAT_VERSION:
        raise DeploymentIdentityError(
            "Deployment identity marker has an unsupported format version."
        )
    _parse_timestamp(document["preparedAtUtc"])
    marker_head = _validate_head(document["repositoryHead"])
    if marker_head != head:
        raise DeploymentIdentityError(
            f"Deployment belongs to checkout {marker_head}, current checkout is {head}."
        )
    expected_fingerprint = document["deploymentFingerprint"]
    if not isinstance(expected_fingerprint, str) or DIGEST.fullmatch(expected_fingerprint) is None:
        raise DeploymentIdentityError("Deployment identity fingerprint is invalid.")
    expected_count = document["fileCount"]
    if type(expected_count) is not int or expected_count <= 0:
        raise DeploymentIdentityError("Deployment identity file count is invalid.")
    fingerprint, file_count = deployment_fingerprint(mods_root, contract_path)
    if file_count != expected_count or fingerprint != expected_fingerprint:
        raise DeploymentIdentityError(
            "Deployed Hatifect release files no longer match the prepared deployment fingerprint."
        )
    return document


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    for name in ("write", "validate"):
        command = subparsers.add_parser(name)
        command.add_argument("marker")
        command.add_argument("--repository-head", required=True)
        command.add_argument("--mods-root", required=True)
        command.add_argument("--contract", required=True)
    args = parser.parse_args()
    try:
        if args.command == "write":
            write_identity(
                Path(args.marker), args.repository_head, Path(args.mods_root), Path(args.contract)
            )
        else:
            validate_identity(
                Path(args.marker), args.repository_head, Path(args.mods_root), Path(args.contract)
            )
    except (DeploymentIdentityError, OSError) as error:
        print(f"Invalid isolated deployment identity: {error}", file=os.sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
