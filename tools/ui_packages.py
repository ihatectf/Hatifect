#!/usr/bin/env python3
"""Validate the deterministic Hatifect UI package graph and local feed."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import zipfile
from pathlib import Path
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "Hatifect.UI.Packages.json"


def load_contract() -> dict:
    document = json.loads(CONTRACT.read_text(encoding="utf-8"))
    errors: list[str] = []
    if document.get("FormatVersion") != 1:
        errors.append("FormatVersion must be 1")
    version = document.get("Version")
    if not isinstance(version, str) or not version.strip():
        errors.append("Version must be a nonempty string")
    packages = document.get("Packages")
    if not isinstance(packages, list) or not packages:
        errors.append("Packages must be a nonempty array")
        packages = []

    seen: set[str] = set()
    available: set[str] = set()
    expected_ids = {
        "Hatifect.UI.Language",
        "Hatifect.UI.Semantics",
        "Hatifect.UI.Experience",
        "Hatifect.UI.Planning",
        "Hatifect.UI.Runtime",
        "Hatifect.UI.Tooling",
        "Hatifect.UI.DevTools",
        "Hatifect.UI.Stardew",
    }
    for index, package in enumerate(packages):
        if not isinstance(package, dict):
            errors.append(f"Packages[{index}] must be an object")
            continue
        package_id = package.get("Id")
        project = package.get("Project")
        dependencies = package.get("Dependencies")
        if not isinstance(package_id, str) or not package_id:
            errors.append(f"Packages[{index}].Id must be nonempty")
            continue
        if package_id in seen:
            errors.append(f"duplicate package ID: {package_id}")
        seen.add(package_id)
        if not isinstance(project, str) or not project.endswith(".csproj"):
            errors.append(f"{package_id}: Project must name a .csproj")
        else:
            project_path = (ROOT / project).resolve()
            if not project_path.is_file() or ROOT not in project_path.parents:
                errors.append(f"{package_id}: project is missing or escapes the repository: {project}")
        if not isinstance(dependencies, list) or any(not isinstance(item, str) for item in dependencies):
            errors.append(f"{package_id}: Dependencies must be a string array")
            dependencies = []
        for dependency in dependencies:
            if dependency not in available:
                errors.append(
                    f"{package_id}: dependency {dependency} must exist earlier in topological order"
                )
        available.add(package_id)
    if seen != expected_ids:
        errors.append(
            "package identity set differs: "
            f"expected {sorted(expected_ids)}, found {sorted(seen)}"
        )
    if errors:
        raise ValueError("invalid Hatifect.UI.Packages.json:\n- " + "\n- ".join(errors))
    return document


def selected_packages(document: dict, *, host_free: bool = False) -> list[dict]:
    """Project the catalog for hosts without game references; never relax the full catalog."""
    return [
        package for package in document["Packages"]
        if not host_free or package["Id"] != "Hatifect.UI.Stardew"
    ]


def list_projects(document: dict, *, host_free: bool = False) -> None:
    for package in selected_packages(document, host_free=host_free):
        print(package["Project"])


def nuspec_document(archive: zipfile.ZipFile) -> ElementTree.Element:
    names = [name for name in archive.namelist() if name.lower().endswith(".nuspec")]
    if len(names) != 1:
        raise ValueError(f"package must contain exactly one nuspec; found {names}")
    return ElementTree.fromstring(archive.read(names[0]))


def child_text(root: ElementTree.Element, name: str) -> str | None:
    for child in root.iter():
        if child.tag.rsplit("}", 1)[-1] == name:
            return child.text
    return None


def dependency_map(root: ElementTree.Element) -> dict[str, str]:
    result: dict[str, str] = {}
    for child in root.iter():
        if child.tag.rsplit("}", 1)[-1] != "dependency":
            continue
        package_id = child.attrib.get("id")
        version = child.attrib.get("version")
        if package_id and version:
            result[package_id] = version
    return result


def _verify_payload_and_producer(
    package_id: str,
    package: dict,
    archive: zipfile.ZipFile,
    names: set[str],
) -> None:
    expected_dll = f"lib/net6.0/{package_id}.dll"
    if expected_dll not in names:
        raise ValueError(f"{package_id}: missing {expected_dll}")
    unexpected_binaries = sorted(
        name for name in names
        if name.lower().endswith((".dll", ".pdb")) and name != expected_dll
    )
    if unexpected_binaries:
        raise ValueError(f"{package_id}: unexpected binary payload {unexpected_binaries}")

    # Later packs may rebuild a previously packaged ProjectReference.
    # Audit the completed feed against the final normal Release outputs.
    producer = (
        ROOT / Path(package["Project"]).parent
        / "bin/Release/net6.0" / f"{package_id}.dll"
    )
    if not producer.is_file():
        raise ValueError(f"{package_id}: missing final Release producer: {producer}")
    packaged_bytes = archive.read(expected_dll)
    producer_bytes = producer.read_bytes()
    if packaged_bytes != producer_bytes:
        raise ValueError(
            f"{package_id}: packaged DLL differs from final Release producer: {producer}; "
            f"packaged SHA-256={hashlib.sha256(packaged_bytes).hexdigest()}, "
            f"producer SHA-256={hashlib.sha256(producer_bytes).hexdigest()}"
        )


def _verify_dependencies(
    package_id: str,
    package: dict,
    root: ElementTree.Element,
    version: str,
) -> None:
    actual_dependencies = dependency_map(root)
    expected_dependencies = set(package["Dependencies"])
    ui_dependencies = {
        dependency for dependency in actual_dependencies if dependency.startswith("Hatifect.UI.")
    }
    if ui_dependencies != expected_dependencies:
        raise ValueError(
            f"{package_id}: UI dependencies {sorted(ui_dependencies)} != "
            f"{sorted(expected_dependencies)}"
        )
    for dependency in expected_dependencies:
        if actual_dependencies[dependency] not in {version, f"[{version}]"}:
            raise ValueError(
                f"{package_id}: dependency {dependency} is not bound to {version}"
            )
    external_dependencies = {
        dependency
        for dependency in actual_dependencies
        if not dependency.startswith("Hatifect.UI.")
    }
    if external_dependencies:
        raise ValueError(
            f"{package_id}: build/runtime package dependency leaked: {sorted(external_dependencies)}"
        )


def verify_feed(document: dict, feed: Path, *, host_free: bool = False) -> None:
    feed = feed.resolve()
    if not feed.is_dir():
        raise ValueError(f"local UI feed does not exist: {feed}")
    packages = sorted(feed.glob("*.nupkg"))
    catalog = selected_packages(document, host_free=host_free)
    if len(packages) != len(catalog):
        raise ValueError(
            f"feed must contain {len(catalog)} nupkg files; found {len(packages)}"
        )

    expected = {package["Id"]: package for package in catalog}
    found: set[str] = set()
    repository_bytes = os.fsencode(str(ROOT.resolve()))
    for path in packages:
        with zipfile.ZipFile(path) as archive:
            root = nuspec_document(archive)
            package_id = child_text(root, "id")
            version = child_text(root, "version")
            if package_id not in expected:
                raise ValueError(f"unexpected package identity {package_id!r} in {path.name}")
            if package_id in found:
                raise ValueError(f"duplicate package identity {package_id!r}")
            found.add(package_id)
            if version != document["Version"]:
                raise ValueError(
                    f"{package_id}: version {version!r} != {document['Version']!r}"
                )

            names = set(archive.namelist())
            _verify_payload_and_producer(package_id, expected[package_id], archive, names)
            _verify_dependencies(package_id, expected[package_id], root, document["Version"])
            for name in names:
                if repository_bytes in archive.read(name):
                    raise ValueError(f"{package_id}: repository path leaked into {name}")

    if found != set(expected):
        raise ValueError(f"feed identities differ: expected {sorted(expected)}, found {sorted(found)}")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("verify-contract")
    projects_parser = subparsers.add_parser("list-projects")
    projects_parser.add_argument("--host-free", action="store_true")
    feed_parser = subparsers.add_parser("verify-feed")
    feed_parser.add_argument("feed", type=Path)
    feed_parser.add_argument("--host-free", action="store_true")
    args = parser.parse_args()

    try:
        document = load_contract()
        if args.command == "list-projects":
            list_projects(document, host_free=args.host_free)
        elif args.command == "verify-feed":
            verify_feed(document, args.feed, host_free=args.host_free)
        else:
            print("Hatifect UI package contract: PASS")
        return 0
    except (OSError, ValueError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
