#!/usr/bin/env python3
"""Validate the current semantic UI, Flowline, and CA Overlay dependency boundaries."""
from __future__ import annotations

import argparse
from pathlib import Path
import re
import sys

from test_inventory import Inventory, InventoryError

REQUIRED_PROJECTS = frozenset({
    "Hatifect.UI.Language", "Hatifect.UI.Semantics", "Hatifect.UI.Experience", "Hatifect.UI.Planning",
    "Hatifect.UI.Runtime", "Hatifect.UI.Tooling", "Hatifect.UI.Tooling.Server", "Hatifect.UI.DevTools",
    "Hatifect.UI.Stardew", "Hatifect.UI.Examples",
    "Hatifect.Flow.Core", "Hatifect.Flow.Persistence", "Hatifect.Flow.UI.Semantic", "Hatifect.Flow",
    "Hatifect.ChestsAnywhereOverlay", "Hatifect.ChestsAnywhereOverlay.UI.Semantic",
    "Hatifect.UI.DevTools.Tests", "Hatifect.UI.Planning.Tests", "Hatifect.UI.Runtime.Tests", "Hatifect.UI.Stardew.Tests",
    "Hatifect.UI.Semantics.Tests", "Hatifect.UI.Tooling.Tests", "Hatifect.UI.Tooling.Server.Tests",
    "Hatifect.Flow.Tests", "Hatifect.Flow.Stardew.Tests", "Hatifect.ChestsAnywhereOverlay.Tests",
})
ALLOWED_DEPENDENCIES = {
    "Hatifect.UI.Language": set(),
    "Hatifect.UI.Semantics": {"Hatifect.UI.Language"},
    "Hatifect.UI.Experience": {"Hatifect.UI.Semantics"},
    "Hatifect.UI.Planning": {"Hatifect.UI.Experience", "Hatifect.UI.Semantics"},
    "Hatifect.UI.Runtime": {"Hatifect.UI.Experience", "Hatifect.UI.Planning", "Hatifect.UI.Semantics"},
    "Hatifect.UI.Tooling": {"Hatifect.UI.Language", "Hatifect.UI.Semantics"},
    "Hatifect.UI.Tooling.Server": {"Hatifect.UI.Tooling", "Hatifect.UI.Planning", "Hatifect.UI.Semantics", "Hatifect.UI.Experience"},
    "Hatifect.UI.DevTools": {
        "Hatifect.UI.Experience", "Hatifect.UI.Planning", "Hatifect.UI.Runtime",
        "Hatifect.UI.Semantics", "Hatifect.UI.Tooling",
    },
    "Hatifect.Flow.Core": set(),
    "Hatifect.Flow.Persistence": {"Hatifect.Flow.Core"},
    "Hatifect.Flow": {"Hatifect.Flow.Core", "Hatifect.Flow.Persistence", "Hatifect.Flow.UI.Semantic", "Hatifect.UI.Experience"},
    "Hatifect.Flow.UI.Semantic": {"Hatifect.Flow.Core", "Hatifect.UI.Experience"},
    "Hatifect.ChestsAnywhereOverlay.UI.Semantic": {"Hatifect.UI.Experience"},
    "Hatifect.ChestsAnywhereOverlay": {
        "Hatifect.UI.Experience", "Hatifect.ChestsAnywhereOverlay.UI.Semantic",
    },
}
PLATFORM_PROJECTS = frozenset({
    "Hatifect.UI.Stardew", "Hatifect.UI.Stardew.Tests", "Hatifect.Flow", "Hatifect.Flow.Stardew.Tests",
    "Hatifect.ChestsAnywhereOverlay", "Hatifect.ChestsAnywhereOverlay.Tests",
})
RETIRED_DIRECTORIES = (
    "Hatifect Terminal", "Hatifect Flow", "Hatifect",
    "Hatifect UI/Hatifect.UI.Runtime", "Hatifect UI/Hatifect.UI.Experience",
    "Hatifect UI/tests/Hatifect.UI.Runtime.Tests",
)


def dependency_issues(inventory: Inventory) -> list[str]:
    issues: list[str] = []
    for project in inventory.projects.values():
        if re.fullmatch(r"Hatifect(?:\.[A-Za-z0-9]+)+", project.name) is None:
            issues.append(f"assembly name must use Hatifect.*: {project.path}: {project.name}")
        names = {inventory.projects[reference].name for reference in inventory.dependencies[project.path]}
        allowed = ALLOWED_DEPENDENCIES.get(project.name)
        if allowed is not None and names - allowed:
            issues.append(f"forbidden dependency: {project.name} -> {sorted(names - allowed)}")
        if project.name.startswith("Hatifect.UI.") and any(not name.startswith("Hatifect.UI.") for name in names):
            issues.append(f"UI framework depends on a consumer: {project.name}")
        if inventory.is_platform(project) and project.name not in PLATFORM_PROJECTS:
            issues.append(f"host-free project reaches game/platform references: {project.name}")
        if project.module == "ca":
            foreign_sources = [inventory.projects[path].name for path in project.references
                               if inventory.projects[path].module != "ca"]
            if foreign_sources:
                issues.append(f"CA must consume the versioned UI package boundary: {project.name}: {foreign_sources}")
    return issues


def validate_architecture(root: Path) -> tuple[int, list[str], dict]:
    inventory = Inventory(root / "Hatifect.slnx")
    issues = dependency_issues(inventory)
    names = {project.name for project in inventory.projects.values()}
    for missing in sorted(REQUIRED_PROJECTS - names):
        issues.append(f"required current project is missing: {missing}")
    for relative in RETIRED_DIRECTORIES:
        if (root / relative).exists():
            issues.append(f"retired implementation directory remains: {relative}")
    return 5, issues, inventory.projects


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args(argv)
    try:
        checks, issues, projects = validate_architecture(args.root.resolve())
    except InventoryError as error:
        print(f"RESULT: FAIL — {error}", file=sys.stderr)
        return 1
    for issue in issues:
        print(f"FAIL: {issue}", file=sys.stderr)
    print(f"Projects: {len(projects)}; architecture rule groups: {checks}")
    print("RESULT: FAIL" if issues else "RESULT: PASS")
    return 1 if issues else 0


if __name__ == "__main__":
    raise SystemExit(main())
