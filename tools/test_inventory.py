#!/usr/bin/env python3
"""Current project graph shared by local validation and GitHub Actions."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
from pathlib import Path, PurePosixPath
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOLUTION = ROOT / "Hatifect.slnx"
PLATFORM_MARKERS = ("GamePath", "Stardew", "SMAPI", "MonoGame")
IGNORED_DIRECTORIES = frozenset({".git", "artifacts", "bin", "obj", ".smapi-test", "packages", ".nuget"})


class InventoryError(ValueError):
    """The declared build or test graph is incomplete or unsafe to evaluate."""


def normalize_relative_posix_path(raw_path: str, *, subject: str) -> str:
    value = raw_path.strip().replace("\\", "/")
    path = PurePosixPath(value)
    if (not value or path.is_absolute() or ".." in path.parts or path.suffix != ".csproj"
            or any(character in value for character in ('$', '*', '?', '"', '\r', '\n'))):
        raise InventoryError(f"{subject} must be a static repository-relative .csproj path: {raw_path!r}")
    return path.as_posix()


def _xml(path: Path) -> ET.Element:
    try:
        return ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise InventoryError(f"unreadable project metadata {path}: {error}") from error


def _tag(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def _contained(root: Path, path: Path) -> Path:
    resolved = path.resolve()
    if not resolved.is_relative_to(root) or not resolved.is_file():
        raise InventoryError(f"metadata is missing or escapes repository: {path}")
    return resolved


def solution_project_paths(solution_path: Path = DEFAULT_SOLUTION) -> tuple[str, ...]:
    solution = solution_path.resolve()
    document = _xml(solution)
    if _tag(document) != "Solution":
        raise InventoryError(f"solution must have a Solution root: {solution}")
    paths: list[str] = []
    for entry in document.iter():
        if _tag(entry) == "Project":
            relative = normalize_relative_posix_path(entry.get("Path", ""), subject="solution entry")
            _contained(solution.parent, solution.parent / relative)
            if relative in paths:
                raise InventoryError(f"duplicate solution project: {relative}")
            paths.append(relative)
    if not paths:
        raise InventoryError(f"solution contains no projects: {solution}")
    return tuple(paths)


def _metadata(root: Path, path: Path, seen: set[Path] | None = None) -> list[tuple[Path, ET.Element]]:
    """Inspect local imports conservatively; conditional references still belong to the graph."""
    seen = set() if seen is None else seen
    path = _contained(root, path)
    if path in seen:
        return []
    seen.add(path)
    document = _xml(path)
    elements = [(path, element) for element in document.iter()]
    for _, entry in tuple(elements):
        if _tag(entry) != "Import":
            continue
        include = entry.get("Project", "").replace("$(MSBuildThisFileDirectory)", str(path.parent) + "/")
        if not include or any(character in include for character in ("$", "*", "?")):
            raise InventoryError(f"local import must resolve statically: {path}: {include!r}")
        elements.extend(_metadata(root, path.parent / include.replace("\\", "/"), seen))
    return elements


@dataclass(frozen=True)
class Project:
    path: str
    name: str
    references: tuple[str, ...]
    packages: tuple[str, ...]
    is_test: bool
    platform_direct: bool

    @property
    def identifier(self) -> str:
        return re.sub(r"[^a-z0-9]+", "-", self.name.lower()).strip("-")

    @property
    def module(self) -> str:
        if self.name.startswith("Hatifect.UI."):
            return "ui"
        if self.name.startswith("Hatifect.Flow"):
            return "flow"
        if self.name.startswith("Hatifect.ChestsAnywhereOverlay"):
            return "ca"
        return "other"


class Inventory:
    def __init__(self, solution_path: Path = DEFAULT_SOLUTION):
        self.solution = solution_path.resolve()
        self.root = self.solution.parent
        self.projects: dict[str, Project] = {}
        package_catalog = self.root / "Hatifect.UI.Packages.json"
        self.package_projects: dict[str, str] = {}
        if package_catalog.is_file():
            try:
                for entry in json.loads(package_catalog.read_text(encoding="utf-8"))["Packages"]:
                    name = entry["Id"]
                    if name in self.package_projects:
                        raise InventoryError(f"duplicate first-party package: {name}")
                    self.package_projects[name] = normalize_relative_posix_path(entry["Project"], subject=name)
            except (OSError, ValueError, KeyError, TypeError) as error:
                raise InventoryError(f"invalid UI package project catalog: {error}") from error

        paths = solution_project_paths(self.solution)
        for relative in paths:
            path = self.root / relative
            elements = _metadata(self.root, path)
            for directory in reversed((path.parent, *path.parent.parents)):
                if directory.is_relative_to(self.root):
                    for filename in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
                        if (directory / filename).is_file():
                            elements.extend(_metadata(self.root, directory / filename))
            references: list[str] = []
            packages: set[str] = set()
            platform = False
            is_test = False
            name = path.stem
            for owner, element in elements:
                tag = _tag(element)
                if tag == "ProjectReference":
                    include = element.get("Include", "").replace("\\", "/")
                    if not include or any(character in include for character in ("$", "*", "?")):
                        raise InventoryError(f"project reference must resolve statically: {relative}: {include!r}")
                    reference = _contained(self.root, owner.parent / include).relative_to(self.root).as_posix()
                    if reference not in paths:
                        raise InventoryError(f"unlisted project reference: {relative} -> {reference}")
                    references.append(reference)
                elif tag == "PackageReference":
                    package = element.get("Include", "")
                    if package:
                        if any(character in package for character in ("$", "*", "?")):
                            raise InventoryError(f"package identity must resolve statically: {relative}: {package!r}")
                        packages.add(package)
                        is_test |= package == "Microsoft.NET.Test.Sdk"
                        platform |= any(marker in package for marker in PLATFORM_MARKERS)
                elif tag == "Reference":
                    text = " ".join([*element.attrib.values(), *(child.text or "" for child in element)])
                    platform |= any(marker in text for marker in PLATFORM_MARKERS)
                elif tag == "IsTestProject" and (element.text or "").strip().lower() == "true":
                    is_test = True
                elif tag == "AssemblyName" and element.text:
                    name = element.text.strip()
            self.projects[relative] = Project(relative, name, tuple(dict.fromkeys(references)), tuple(sorted(packages)), is_test, platform)

        identifiers = [project.identifier for project in self.projects.values()]
        if len(identifiers) != len(set(identifiers)):
            raise InventoryError("project assembly identities must be unique")
        self.dependencies: dict[str, tuple[str, ...]] = {}
        for project in self.projects.values():
            dependencies = list(project.references)
            for package in project.packages:
                if not package.startswith("Hatifect."):
                    continue
                producer = self.package_projects.get(package)
                if producer not in self.projects:
                    raise InventoryError(f"first-party package has no listed producer: {project.path}: {package}")
                dependencies.append(producer)
            self.dependencies[project.path] = tuple(dict.fromkeys(dependencies))
        self._platform: dict[str, bool] = {}
        for relative in self.projects:
            self._classify(relative, set())
        if not any(project.is_test for project in self.projects.values()):
            raise InventoryError("solution contains no test projects")
        self._audit_unlisted_projects()

    def _classify(self, relative: str, visiting: set[str]) -> bool:
        if relative in self._platform:
            return self._platform[relative]
        if relative in visiting:
            raise InventoryError(f"project reference cycle at {relative}")
        visiting.add(relative)
        linked = self.projects[relative].platform_direct
        for dependency in self.dependencies[relative]:
            linked |= self._classify(dependency, visiting)
        visiting.remove(relative)
        self._platform[relative] = linked
        return linked

    def _audit_unlisted_projects(self) -> None:
        for path in self.root.rglob("*.csproj"):
            relative = path.relative_to(self.root)
            if any(part in IGNORED_DIRECTORIES for part in relative.parts) or relative.as_posix() in self.projects:
                continue
            raise InventoryError(f"project exists outside the solution: {relative}")

    def select(self, *, tests: bool = False, scope: str = "all", platform: bool = False,
               project: str | None = None) -> tuple[Project, ...]:
        relative = None
        if project is not None:
            relative = normalize_relative_posix_path(project, subject="selected project")
            if relative not in self.projects:
                raise InventoryError(f"selected project is not in the solution: {relative}")
        if scope not in {"all", "ui", "flow", "ca"}:
            raise InventoryError(f"unknown project scope: {scope}")
        selected = tuple(item for item in self.projects.values()
                         if (not tests or item.is_test)
                         and (scope == "all" or item.module == scope)
                         and (relative is None or item.path == relative)
                         and (platform or not self._platform[item.path]))
        if not selected:
            raise InventoryError("selection contains no runnable projects; use --platform for game-linked tests")
        return selected

    def affected_tests(self, changed_paths: tuple[str, ...], *, platform: bool = False) -> tuple[Project, ...]:
        """Return tests that consume the projects owning the changed files.

        Unknown repository files conservatively select the full matrix. This
        keeps the opt-in iteration shortcut useful without allowing it to hide
        a change that the static project graph cannot classify.
        """
        if not changed_paths:
            raise InventoryError("affected-test selection has no changed files")
        owners: set[str] = set()
        project_directories = sorted(
            ((PurePosixPath(path).parent, path) for path in self.projects),
            key=lambda item: len(item[0].parts),
            reverse=True,
        )
        for raw_path in changed_paths:
            path = PurePosixPath(raw_path.replace("\\", "/"))
            if path.is_absolute() or ".." in path.parts:
                raise InventoryError(f"changed path escapes the repository: {raw_path!r}")
            owner = next(
                (
                    project
                    for directory, project in project_directories
                    if path == PurePosixPath(project) or directory.parts and path.is_relative_to(directory)
                ),
                None,
            )
            if owner is None:
                return self.select(tests=True, platform=platform)
            owners.add(owner)

        def consumes(relative: str, dependency: str, visited: set[str]) -> bool:
            if relative == dependency:
                return True
            if relative in visited:
                return False
            visited.add(relative)
            return any(consumes(item, dependency, visited) for item in self.dependencies[relative])

        selected = tuple(
            project
            for project in self.select(tests=True, platform=platform)
            if any(consumes(project.path, owner, set()) for owner in owners)
        )
        if not selected:
            raise InventoryError("changed projects have no affected runnable tests")
        return selected

    def is_platform(self, project: Project) -> bool:
        return self._platform[project.path]


def test_project_paths(solution_path: Path = DEFAULT_SOLUTION) -> tuple[str, ...]:
    return tuple(project.path for project in Inventory(solution_path).select(tests=True, platform=True))


def host_free_test_project_paths(solution_path: Path = DEFAULT_SOLUTION) -> tuple[str, ...]:
    return tuple(project.path for project in Inventory(solution_path).select(tests=True))


def platform_test_project_paths(solution_path: Path = DEFAULT_SOLUTION) -> tuple[str, ...]:
    inventory = Inventory(solution_path)
    return tuple(project.path for project in inventory.projects.values() if project.is_test and inventory.is_platform(project))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--solution", type=Path, default=DEFAULT_SOLUTION)
    parser.add_argument("--scope", choices=("all", "host-free", "platform"), default="all")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    paths = {"all": test_project_paths, "host-free": host_free_test_project_paths,
             "platform": platform_test_project_paths}[args.scope](args.solution)
    print(json.dumps(paths) if args.json else "\n".join(paths))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except InventoryError as error:
        print(f"RESULT: FAIL — {error}", file=sys.stderr)
        raise SystemExit(1)
