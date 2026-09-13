#!/usr/bin/env python3
"""Deterministic progressive regression selection for local Hatifect work."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import signal
import subprocess
import sys
import tempfile
import threading
import time
from types import ModuleType
from typing import Any, Callable, Iterable

from test_inventory import Inventory, InventoryError, Project


ROOT = Path(__file__).resolve().parents[1]
MAPPING_PATH = Path(__file__).with_name("regression-selection.json")
SCENARIO_PATH = ROOT / "tools/live-harness/scenarios.json"
CONTEXT_PATH = ROOT / "tools/live-harness/diagnostic-context.json"
VALIDATOR_PATH = ROOT / "tools/live-harness/validate.py"
MAX_MAPPING_BYTES = 128 * 1024
MAX_REPORT_BYTES = 256 * 1024
MAX_CAPTURE_BYTES = 64 * 1024
MAX_STAGE_SECONDS = 1200
TERMINATION_GRACE_SECONDS = 5
MAX_CHANGED_PATHS = 64
MAX_CHANGE_LIST_BYTES = 64 * 1024
MAX_TESTS = 16
MAX_TEXT = 512
PYTHON_TEST_RE = re.compile(
    r"^python:tools\.tests\.test_[A-Za-z0-9_]+"
    r"\.[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$"
)
DOTNET_TEST_RE = re.compile(
    r"^dotnet:(?P<project>[^:]+\.csproj)::(?P<name>[A-Za-z_][A-Za-z0-9_.+`]*)$"
)
HEAD_RE = re.compile(r"^[0-9a-f]{40}$")
UNITTEST_COUNT_RE = re.compile(r"^Ran (?P<count>[0-9]+) tests?\b", re.MULTILINE)
PYTHON_SUITE_COUNT_RE = re.compile(
    r"^Python tests: (?P<count>[0-9]+); failures: [0-9]+; skipped: [0-9]+$",
    re.MULTILINE,
)
DOTNET_COUNT_RE = re.compile(
    r"^[^\r\n:]+: Passed: (?P<count>[0-9]+), Failed: [0-9]+, Skipped: [0-9]+$",
    re.MULTILINE,
)
INTEGRATION_COMMANDS = {
    "ca": (["./tools/hatifect-test", "ca", "--platform"], "ca-platform"),
    "flow": (["./tools/hatifect-test", "flow"], "flow-host-free"),
    "tooling": (["./tools/hatifect-test", "tools"], "python-tooling"),
    "ui": (["./tools/hatifect-test", "ui"], "ui-host-free"),
}


class RegressionSelectionError(ValueError):
    """The requested regression selection cannot be made safely."""


def _load_module(name: str, path: Path) -> ModuleType:
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RegressionSelectionError(f"cannot load canonical module: {path.name}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read_json(path: Path, maximum: int, description: str) -> dict[str, Any]:
    try:
        info = path.lstat()
        if not path.is_file() or path.is_symlink() or info.st_size > maximum:
            raise RegressionSelectionError(f"{description} is not a bounded regular file")
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise RegressionSelectionError(f"cannot read {description}: {error}") from error
    if not isinstance(payload, dict):
        raise RegressionSelectionError(f"{description} must be a JSON object")
    return payload


def _canonical_bytes(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")


def _bounded_text(value: Any) -> str:
    text = " ".join(str(value).split())
    return text if len(text) <= MAX_TEXT else text[: MAX_TEXT - 3] + "..."


def _repository_head(root: Path = ROOT) -> str:
    try:
        result = subprocess.run(
            ["git", "-C", str(root), "rev-parse", "--verify", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        )
    except (OSError, subprocess.CalledProcessError) as error:
        raise RegressionSelectionError("cannot determine repository HEAD") from error
    head = result.stdout.strip()
    if HEAD_RE.fullmatch(head) is None:
        raise RegressionSelectionError("repository HEAD is invalid")
    return head


def _bounded_git_names(arguments: list[str], root: Path) -> list[str]:
    try:
        process = subprocess.Popen(
            ["git", "-C", str(root), *arguments],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        assert process.stdout is not None
        payload = process.stdout.read(MAX_CHANGE_LIST_BYTES + 1)
        if len(payload) > MAX_CHANGE_LIST_BYTES:
            process.kill()
            process.communicate()
            raise RegressionSelectionError("worktree change list exceeds its byte bound")
        _, error = process.communicate()
    except OSError as exception:
        raise RegressionSelectionError("cannot inspect repository worktree") from exception
    if process.returncode != 0:
        detail = _bounded_text(error.decode("utf-8", errors="replace"))
        raise RegressionSelectionError(f"cannot inspect repository worktree: {detail}")
    try:
        return [item.decode("utf-8") for item in payload.split(b"\0") if item]
    except UnicodeDecodeError as exception:
        raise RegressionSelectionError("worktree contains a non-UTF-8 path") from exception


def _worktree_changes(root: Path = ROOT) -> list[str]:
    paths = [
        *_bounded_git_names(["diff", "--name-only", "-z", "HEAD", "--"], root),
        *_bounded_git_names(["ls-files", "--others", "--exclude-standard", "-z"], root),
    ]
    normalized = sorted(dict.fromkeys(_normalize_path(path, root=root) for path in paths))
    if len(normalized) > MAX_CHANGED_PATHS:
        raise RegressionSelectionError("worktree change list exceeds its count bound")
    return normalized


def _normalize_path(raw: str, *, root: Path = ROOT) -> str:
    value = raw.strip().replace("\\", "/")
    path = PurePosixPath(value)
    if (
        not value
        or path.is_absolute()
        or any(part in {"", ".", ".."} for part in path.parts)
        or "\x00" in value
    ):
        raise RegressionSelectionError(f"changed path is not repository-relative: {raw!r}")
    candidate = root.joinpath(*path.parts)
    try:
        candidate.relative_to(root)
    except ValueError as error:
        raise RegressionSelectionError(f"changed path escapes repository: {raw!r}") from error
    return path.as_posix()


def _validate_string_list(value: Any, field: str, maximum: int = 256) -> list[str]:
    if (
        not isinstance(value, list)
        or len(value) > maximum
        or any(not isinstance(item, str) or not item or len(item) > 1024 for item in value)
        or value != list(dict.fromkeys(value))
    ):
        raise RegressionSelectionError(f"invalid mapping field: {field}")
    return value


def load_mapping(path: Path = MAPPING_PATH) -> dict[str, Any]:
    mapping = _read_json(path, MAX_MAPPING_BYTES, "regression selection mapping")
    if set(mapping) != {
        "formatVersion",
        "changeRules",
        "components",
        "integrationAreas",
        "fullGate",
    } or mapping["formatVersion"] != 1:
        raise RegressionSelectionError("regression selection mapping schema is invalid")
    rules = mapping["changeRules"]
    components = mapping["components"]
    areas = mapping["integrationAreas"]
    full = mapping["fullGate"]
    if not isinstance(rules, list) or not rules or len(rules) > 128:
        raise RegressionSelectionError("changeRules must be a bounded non-empty array")
    if not isinstance(components, dict) or not components or not isinstance(areas, dict):
        raise RegressionSelectionError("component and integration mappings are required")
    for name, component in components.items():
        if (
            not isinstance(name, str)
            or not name
            or not isinstance(component, dict)
            or set(component) != {"directTestFiles", "integrationArea"}
            or component["integrationArea"] not in {*areas, "repository"}
        ):
            raise RegressionSelectionError(f"component mapping is invalid: {name!r}")
        test_files = _validate_string_list(
            component["directTestFiles"], f"components.{name}.directTestFiles"
        )
        for test_file in test_files:
            test_path = PurePosixPath(test_file)
            if (
                test_path.is_absolute()
                or ".." in test_path.parts
                or test_path.suffix not in {".py", ".cs"}
            ):
                raise RegressionSelectionError(
                    f"component test path is invalid: {test_file!r}"
                )
    for index, rule in enumerate(rules):
        if (
            not isinstance(rule, dict)
            or set(rule) != {"pattern", "component", "integrationArea", "forceLevel"}
            or rule["component"] not in components
            or rule["integrationArea"] != components[rule["component"]]["integrationArea"]
            or type(rule["forceLevel"]) is not int
            or rule["forceLevel"] not in {3, 4, 5}
        ):
            raise RegressionSelectionError(f"change rule {index} is invalid")
        pattern = rule["pattern"]
        if (
            not isinstance(pattern, str)
            or not pattern
            or pattern.startswith("/")
            or ".." in PurePosixPath(pattern).parts
            or ("*" in pattern[:-1])
            or pattern.count("*") > 1
        ):
            raise RegressionSelectionError(f"change rule {index} pattern is invalid")
    for name, area in areas.items():
        if (
            not isinstance(name, str)
            or not name
            or not isinstance(area, dict)
            or set(area) != {"command", "scopeId"}
            or not isinstance(area["scopeId"], str)
        ):
            raise RegressionSelectionError(f"integration area is invalid: {name!r}")
        _validate_string_list(area["command"], f"integrationAreas.{name}.command", 16)
        expected = INTEGRATION_COMMANDS.get(name)
        if expected is None or (area["command"], area["scopeId"]) != expected:
            raise RegressionSelectionError(
                f"integration area '{name}' does not use its canonical command"
            )
    if not isinstance(full, dict) or set(full) != {"command", "scopeId"}:
        raise RegressionSelectionError("fullGate mapping is invalid")
    _validate_string_list(full["command"], "fullGate.command", 16)
    if full["command"] != ["./tools/hatifect-check"] or full["scopeId"] != "full-host-free":
        raise RegressionSelectionError("Level 5 must remain the canonical host-free full gate")
    return mapping


def _rule_score(pattern: str, path: str) -> int | None:
    if pattern.endswith("*"):
        prefix = pattern[:-1]
        return len(prefix) if prefix and path.startswith(prefix) else None
    return len(pattern) + 10_000 if path == pattern else None


def _match_change(mapping: dict[str, Any], path: str) -> dict[str, Any] | None:
    matches = [
        (score, rule)
        for rule in mapping["changeRules"]
        if (score := _rule_score(rule["pattern"], path)) is not None
    ]
    if not matches:
        return None
    matches.sort(key=lambda item: (item[0], item[1]["pattern"]), reverse=True)
    top = [rule for score, rule in matches if score == matches[0][0]]
    identities = {
        (rule["component"], rule["integrationArea"], rule["forceLevel"])
        for rule in top
    }
    if len(identities) != 1:
        return {"ambiguous": True, "patterns": sorted(rule["pattern"] for rule in top)}
    return top[0]


def _python_module(path: str) -> str:
    if not path.startswith("tools/tests/test_") or not path.endswith(".py"):
        raise RegressionSelectionError(f"Python test file is outside tooling tests: {path}")
    return path[:-3].replace("/", ".")


def _project_owner(inventory: Inventory, path: str) -> Project | None:
    owners: list[Project] = []
    candidate = PurePosixPath(path)
    for project in inventory.projects.values():
        parent = PurePosixPath(project.path).parent
        if candidate == PurePosixPath(project.path) or candidate.is_relative_to(parent):
            owners.append(project)
    if not owners:
        return None
    owners.sort(key=lambda project: len(PurePosixPath(project.path).parts), reverse=True)
    if len(owners) > 1 and len(PurePosixPath(owners[0].path).parts) == len(PurePosixPath(owners[1].path).parts):
        raise RegressionSelectionError(f"changed path has ambiguous project ownership: {path}")
    return owners[0]


def _depends_on(inventory: Inventory, project: str, dependency: str) -> bool:
    pending = list(inventory.dependencies[project])
    seen: set[str] = set()
    while pending:
        current = pending.pop()
        if current == dependency:
            return True
        if current not in seen:
            seen.add(current)
            pending.extend(inventory.dependencies[current])
    return False


def _affected_test_projects(
    inventory: Inventory, changed_path: str
) -> tuple[list[Project], list[Project]]:
    owner = _project_owner(inventory, changed_path)
    if owner is None:
        return [], []
    if owner.is_test:
        return [owner], []
    affected = [
        project
        for project in inventory.projects.values()
        if project.is_test and _depends_on(inventory, project.path, owner.path)
    ]
    direct = [project for project in affected if project.module == owner.module]
    downstream = [project for project in affected if project.module != owner.module]
    return direct, downstream


def _project_stage(
    project: Project,
    level: int,
    reason: str,
    *,
    platform_required: bool | None = None,
) -> dict[str, Any]:
    platform_required = project.platform_direct if platform_required is None else platform_required
    command = ["./tools/hatifect-test", "--project", project.path]
    if platform_required:
        command.append("--platform")
    return {
        "level": level,
        "scopeId": f"dotnet-project:{project.path}",
        "reasonCode": reason,
        "command": command,
        "platformRequired": platform_required,
    }


def _test_file_stages(
    files: Iterable[str], inventory: Inventory, *, level: int, reason: str
) -> list[dict[str, Any]]:
    stages: list[dict[str, Any]] = []
    for path in files:
        source = inventory.root / path
        if not source.is_file() or source.is_symlink():
            raise RegressionSelectionError(f"mapped test file is missing or unsafe: {path}")
        if path.endswith(".py"):
            module = _python_module(path)
            stages.append(
                {
                    "level": level,
                    "scopeId": f"python-module:{module}",
                    "reasonCode": reason,
                    "command": ["python3", "-m", "unittest", module, "-q"],
                    "platformRequired": False,
                }
            )
            continue
        owner = _project_owner(inventory, path)
        if owner is None or not owner.is_test:
            raise RegressionSelectionError(f"mapped test file has no registered test assembly: {path}")
        stages.append(
            _project_stage(
                owner,
                level,
                reason,
                platform_required=inventory.is_platform(owner),
            )
        )
    return stages


def _validate_all_test_file_mappings(
    mapping: dict[str, Any], inventory: Inventory
) -> None:
    for component in mapping["components"].values():
        _test_file_stages(
            component["directTestFiles"],
            inventory,
            level=3,
            reason="MAPPING_VALIDATION",
        )


def _parse_exact_test(value: str, inventory: Inventory) -> dict[str, Any]:
    if PYTHON_TEST_RE.fullmatch(value):
        name = value.removeprefix("python:")
        return {
            "level": 1,
            "scopeId": value,
            "reasonCode": "EXACT_FAILING_TEST",
            "command": ["python3", "-m", "unittest", name, "-q"],
            "platformRequired": False,
        }
    match = DOTNET_TEST_RE.fullmatch(value)
    if match is None:
        raise RegressionSelectionError(
            "test must be python:<unittest-id> or dotnet:<registered.csproj>::<FullyQualifiedName>"
        )
    project_path = _normalize_path(match.group("project"))
    project = inventory.projects.get(project_path)
    if project is None or not project.is_test:
        raise RegressionSelectionError("exact .NET test project is not registered")
    command = [
        "./tools/hatifect-test",
        "--project",
        project.path,
        "--test-filter",
        match.group("name"),
    ]
    if inventory.is_platform(project):
        command.append("--platform")
    return {
        "level": 1,
        "scopeId": value,
        "reasonCode": "EXACT_FAILING_TEST",
        "command": command,
        "platformRequired": inventory.is_platform(project),
    }


def _scenario_context(scenario: str, kind: str | None) -> tuple[dict[str, Any], str, list[str]]:
    validator = _load_module("hatifect_regression_validator", VALIDATOR_PATH)
    try:
        manifest = validator.load_manifest()
        if kind is None:
            registered = manifest.get(scenario)
            if not isinstance(registered, dict) or registered.get("kind") not in {"smoke", "ui"}:
                raise RegressionSelectionError("scenario kind cannot be inferred")
            kind = registered["kind"]
        resolved = validator.resolve_scenario(manifest, scenario, kind)
    except Exception as error:
        raise RegressionSelectionError(f"scenario is not uniquely registered: {error}") from error
    actual_kind = resolved["kind"]
    if scenario == "all":
        return resolved, "aggregate", []
    context = _read_json(CONTEXT_PATH, MAX_MAPPING_BYTES, "diagnostic context mapping")
    rules = context.get("scenarioRules")
    if not isinstance(rules, list):
        raise RegressionSelectionError("diagnostic scenario ownership is invalid")
    matches: list[tuple[int, dict[str, Any]]] = []
    for rule in rules:
        if not isinstance(rule, dict):
            raise RegressionSelectionError("diagnostic scenario rule is invalid")
        pattern = rule.get("pattern")
        kinds = rule.get("kinds")
        if not isinstance(pattern, str) or not isinstance(kinds, list):
            raise RegressionSelectionError("diagnostic scenario rule identity is invalid")
        score = _rule_score(pattern, scenario)
        if score is not None and actual_kind in kinds:
            matches.append((score, rule))
    if not matches:
        raise RegressionSelectionError("scenario has no deterministic component owner")
    matches.sort(key=lambda item: item[0], reverse=True)
    if len(matches) > 1 and matches[0][0] == matches[1][0]:
        raise RegressionSelectionError("scenario has ambiguous component ownership")
    rule = matches[0][1]
    component = rule.get("component")
    test_files = rule.get("context", {}).get("scenarioTestFiles")
    if not isinstance(component, str) or not isinstance(test_files, list):
        raise RegressionSelectionError("scenario component/test ownership is invalid")
    return resolved, component, _validate_string_list(test_files, "scenarioTestFiles")


def _deduplicate_stages(stages: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    selected: list[dict[str, Any]] = []
    seen: set[tuple[str, ...]] = set()
    for stage in stages:
        identity = tuple(stage["command"])
        if identity in seen:
            continue
        seen.add(identity)
        selected.append(stage)
    return sorted(selected, key=lambda stage: (stage["level"], stage["scopeId"]))


def _test_inventory_scopes(inventory: Inventory, root: Path) -> list[dict[str, Any]]:
    scopes = [
        {
            "scopeId": f"python-module:{_python_module(path.relative_to(root).as_posix())}",
            "kind": "python-module",
            "platformRequired": False,
        }
        for path in sorted((root / "tools/tests").glob("test_*.py"))
        if path.is_file() and not path.is_symlink()
    ]
    scopes.extend(
        {
            "scopeId": f"dotnet-project:{project.path}",
            "kind": "dotnet-assembly",
            "platformRequired": inventory.is_platform(project),
        }
        for project in sorted(inventory.projects.values(), key=lambda item: item.path)
        if project.is_test
    )
    return scopes


def _omitted_scopes(
    stages: list[dict[str, Any]], inventory: Inventory, root: Path, areas: set[str]
) -> list[dict[str, Any]]:
    inventory_scopes = _test_inventory_scopes(inventory, root)
    selected = {stage["scopeId"] for stage in stages}
    full_host_free = "full-host-free" in selected
    python_all = full_host_free or "python-tooling" in selected
    covered_projects: set[str] = set()
    for name, (command, scope_id) in INTEGRATION_COMMANDS.items():
        if scope_id not in selected:
            continue
        includes_platform = "--platform" in command
        covered_projects.update(
            f"dotnet-project:{project.path}"
            for project in inventory.projects.values()
            if project.is_test
            and project.module == name
            and (includes_platform or not inventory.is_platform(project))
        )
    if full_host_free:
        covered_projects.update(
            f"dotnet-project:{project.path}"
            for project in inventory.projects.values()
            if project.is_test and not inventory.is_platform(project)
        )
    exact_python_modules = {
        "python-module:" + ".".join(stage["scopeId"].removeprefix("python:").split(".")[:3])
        for stage in stages
        if stage["scopeId"].startswith("python:")
    }
    exact_dotnet_projects = {
        f"dotnet-project:{match.group('project')}"
        for stage in stages
        if (match := DOTNET_TEST_RE.fullmatch(stage["scopeId"])) is not None
    }
    omitted: list[dict[str, Any]] = []
    for scope in inventory_scopes:
        scope_id = scope["scopeId"]
        covered = (
            scope_id in selected
            or scope_id in covered_projects
            or (scope["kind"] == "python-module" and python_all)
        )
        if covered:
            continue
        if scope_id in exact_python_modules or scope_id in exact_dotnet_projects:
            reason = "PARTIALLY_COVERED_BY_EXACT_TEST"
        elif full_host_free and scope["platformRequired"]:
            reason = "PLATFORM_TEST_NOT_IN_HOST_FREE_FULL_GATE"
        elif (
            (scope["kind"] == "python-module" and "tooling" in areas)
            or (
                scope["kind"] == "dotnet-assembly"
                and scope_id.removeprefix("dotnet-project:") in inventory.projects
                and inventory.projects[
                    scope_id.removeprefix("dotnet-project:")
                ].module
                in areas
            )
        ):
            reason = "OWNING_AREA_NOT_REACHED"
        else:
            reason = "OUTSIDE_SELECTED_SCOPE"
        omitted.append({**scope, "reason": reason})
    if not full_host_free:
        omitted.append(
            {
                "scopeId": "full-host-free",
                "kind": "authoritative-gate",
                "platformRequired": False,
                "reason": "LOCAL_ITERATION_NOT_FINAL",
            }
        )
    return omitted


def build_plan(
    *,
    changed_paths: Iterable[str] = (),
    scenario: str | None = None,
    scenario_kind: str | None = None,
    exact_tests: Iterable[str] = (),
    final: bool = False,
    mapping_path: Path = MAPPING_PATH,
    root: Path = ROOT,
    head: str | None = None,
    discover_worktree: bool = False,
) -> dict[str, Any]:
    mapping = load_mapping(mapping_path)
    requested_paths = sorted(
        dict.fromkeys(_normalize_path(path, root=root) for path in changed_paths)
    )
    discovered_paths = _worktree_changes(root) if discover_worktree else []
    paths = sorted(dict.fromkeys([*requested_paths, *discovered_paths]))
    tests = sorted(dict.fromkeys(exact_tests))
    if len(paths) > MAX_CHANGED_PATHS or len(tests) > MAX_TESTS:
        raise RegressionSelectionError("regression selection input exceeds its count bound")
    if scenario_kind is not None and scenario is None:
        raise RegressionSelectionError("--scenario-kind requires --scenario")
    if not paths and not scenario and not tests and not final:
        raise RegressionSelectionError("provide a changed path, exact test, scenario, or --final")
    try:
        inventory = Inventory(root / "Hatifect.slnx")
    except InventoryError as error:
        raise RegressionSelectionError(f"cannot load test inventory: {error}") from error
    _validate_all_test_file_mappings(mapping, inventory)

    stages: list[dict[str, Any]] = [_parse_exact_test(value, inventory) for value in tests]
    reasons: list[dict[str, str]] = []
    components: set[str] = set()
    areas: set[str] = set()
    downstream_areas: set[str] = set()
    direct_files: set[str] = set()
    target = 1 if tests else 0
    force_full = final
    resolved_scenario_kind = scenario_kind

    if tests:
        reasons.append({"code": "EXACT_FAILING_TEST", "detail": "Explicit exact test context selects Level 1."})

    if scenario is not None:
        resolved, component, scenario_test_files = _scenario_context(scenario, scenario_kind)
        resolved_scenario_kind = resolved["kind"]
        command = [
            "./tools/hatifect-smoke" if resolved["kind"] == "smoke" else "./tools/hatifect-ui-test",
            scenario,
        ]
        stages.append(
            {
                "level": 2,
                "scopeId": f"scenario:{resolved['kind']}:{scenario}",
                "reasonCode": "FAILING_SCENARIO",
                "command": command,
                "platformRequired": True,
            }
        )
        target = max(target, 2)
        reasons.append({"code": "FAILING_SCENARIO", "detail": "Registered scenario context selects Level 2."})
        if component == "aggregate":
            force_full = True
            reasons.append({"code": "AGGREGATE_SCENARIO", "detail": "Aggregate scenario ownership requires Level 5."})
        else:
            components.add(component)
            if component in mapping["components"]:
                areas.add(mapping["components"][component]["integrationArea"])
                direct_files.update(scenario_test_files)

    unknown: list[str] = []
    ambiguous: list[str] = []
    force_level = 0
    for path in paths:
        rule = _match_change(mapping, path)
        if rule is None:
            unknown.append(path)
            continue
        if rule.get("ambiguous"):
            ambiguous.append(path)
            continue
        components.add(rule["component"])
        areas.add(rule["integrationArea"])
        force_level = max(force_level, rule["forceLevel"])
        direct_files.update(mapping["components"][rule["component"]]["directTestFiles"])
        direct_projects, downstream_projects = _affected_test_projects(inventory, path)
        for project in direct_projects:
            stages.append(
                _project_stage(
                    project,
                    3,
                    "DIRECTLY_AFFECTED_TEST_ASSEMBLY",
                    platform_required=inventory.is_platform(project),
                )
            )
        for project in downstream_projects:
            if project.module in mapping["integrationAreas"]:
                downstream_areas.add(project.module)
            else:
                force_full = True
                reasons.append(
                    {
                        "code": "UNKNOWN_DOWNSTREAM_INTEGRATION",
                        "detail": f"Downstream test assembly has no integration area: {project.path}",
                    }
                )

    if paths:
        target = max(target, 3, force_level)
        reasons.append({"code": "DIRECTLY_AFFECTED_COMPONENT", "detail": "Known changed paths select direct Level 3 tests."})
    additionally_discovered = sorted(set(discovered_paths) - set(requested_paths))
    if additionally_discovered:
        reasons.append(
            {
                "code": "WORKTREE_CHANGE_DISCOVERED",
                "detail": "Unlisted candidate changes were included: "
                + ", ".join(additionally_discovered),
            }
        )
    if unknown:
        force_full = True
        reasons.append({"code": "UNKNOWN_CHANGE", "detail": f"Unmapped paths require Level 5: {', '.join(unknown)}"})
    if ambiguous:
        force_full = True
        reasons.append({"code": "AMBIGUOUS_CHANGE", "detail": f"Ambiguous paths require Level 5: {', '.join(ambiguous)}"})

    stages.extend(_test_file_stages(sorted(direct_files), inventory, level=3, reason="COMPONENT_TEST_MAPPING"))
    explicitly_changed_areas = set(areas)
    if downstream_areas:
        areas.update(downstream_areas)
        target = max(target, 4)
        reasons.append(
            {
                "code": "DOWNSTREAM_INTEGRATION_CONSUMER",
                "detail": "Dependent integration areas require Level 4: "
                + ", ".join(sorted(downstream_areas)),
            }
        )
    if len(components) > 1:
        target = max(target, 4)
        reasons.append({"code": "CROSS_COMPONENT_CHANGE", "detail": "Multiple component owners require Level 4 integration."})
    if len(explicitly_changed_areas) > 1:
        force_full = True
        reasons.append({"code": "MULTI_AREA_CHANGE", "detail": "Multiple integration areas require Level 5."})
    if "repository" in areas:
        force_full = True
        reasons.append({"code": "CI_RELEASE_AUTHORITY", "detail": "Repository validation authority requires Level 5."})
    if final:
        reasons.append({"code": "AUTHORITATIVE_FINAL_GATE", "detail": "Final validation explicitly requires Level 5."})
    if force_full:
        target = 5

    level_4_areas = set(downstream_areas)
    if force_level >= 4 or len(components) > 1 or len(explicitly_changed_areas) > 1:
        level_4_areas.update(explicitly_changed_areas)
    if target >= 4:
        for area in sorted(level_4_areas - {"repository"}):
            entry = mapping["integrationAreas"].get(area)
            if entry is None:
                target = 5
                reasons.append({"code": "UNKNOWN_INTEGRATION_AREA", "detail": f"Area '{area}' has no Level 4 gate."})
                continue
            stages.append(
                {
                    "level": 4,
                    "scopeId": entry["scopeId"],
                    "reasonCode": "RELEVANT_INTEGRATION_AREA",
                    "command": entry["command"],
                    "platformRequired": "--platform" in entry["command"],
                }
            )
    if target >= 5:
        full = mapping["fullGate"]
        stages.append(
            {
                "level": 5,
                "scopeId": full["scopeId"],
                "reasonCode": "AUTHORITATIVE_FULL_GATE",
                "command": full["command"],
                "platformRequired": False,
            }
        )

    stages = _deduplicate_stages(stage for stage in stages if stage["level"] <= target)
    if not stages:
        raise RegressionSelectionError("selection produced no executable regression scope")
    if target >= 3 and not any(stage["level"] >= 3 for stage in stages):
        target = 5
        full = mapping["fullGate"]
        stages.append(
            {
                "level": 5,
                "scopeId": full["scopeId"],
                "reasonCode": "MISSING_DIRECT_MAPPING_FALLBACK",
                "command": full["command"],
                "platformRequired": False,
            }
        )
        reasons.append({"code": "MISSING_DIRECT_MAPPING", "detail": "No direct tests were resolved; Level 5 is required."})
        stages = _deduplicate_stages(stages)

    planned_escalation = [
        {
            "fromLevel": 0,
            "toLevel": stages[0]["level"],
            "code": stages[0]["reasonCode"],
        },
        *[
            {
                "fromLevel": previous["level"],
                "toLevel": current["level"],
                "code": current["reasonCode"],
            }
            for previous, current in zip(stages, stages[1:])
            if current["level"] > previous["level"]
        ],
    ]
    omitted = _omitted_scopes(stages, inventory, root, areas)
    plan = {
        "formatVersion": 1,
        "repositoryHead": head or _repository_head(root),
        "worktreePaths": discovered_paths if discover_worktree else None,
        "changedPaths": paths,
        "exactTests": tests,
        "finalRequested": final,
        "scenario": scenario,
        "scenarioKind": resolved_scenario_kind,
        "components": sorted(components),
        "integrationAreas": sorted(areas),
        "selectionReason": sorted(reasons, key=lambda item: (item["code"], item["detail"])),
        "selectedScope": stages,
        "testsExecuted": [],
        "testsOmittedByScope": omitted,
        "plannedScopesNotExecuted": [],
        "plannedEscalation": planned_escalation,
        "escalationTrigger": [],
        "executionStop": None,
        "testInventory": {
            "scopeCount": len(_test_inventory_scopes(inventory, root)),
            "omittedScopeCount": sum(
                item["kind"] in {"python-module", "dotnet-assembly"}
                for item in omitted
            ),
            "authoritativeGateOmitted": any(
                item["kind"] == "authoritative-gate" for item in omitted
            ),
        },
        "plannedRegressionLevel": target,
        "finalRegressionLevel": 0,
        "authoritativeFullGateSelected": any(stage["level"] == 5 for stage in stages),
    }
    plan["selectionFingerprint"] = _selection_fingerprint(plan)
    if len(_canonical_bytes(plan)) > MAX_REPORT_BYTES:
        raise RegressionSelectionError("regression plan exceeds its serialized size bound")
    return plan


Executor = Callable[[list[str], Path, dict[str, str]], tuple[int, str]]


def _selection_fingerprint(plan: dict[str, Any]) -> str:
    source = {key: value for key, value in plan.items() if key != "selectionFingerprint"}
    return hashlib.sha256(_canonical_bytes(source)).hexdigest()


def _validate_executable_command(
    command: Any, *, root: Path, inventory: Inventory
) -> list[str]:
    if (
        not isinstance(command, list)
        or not command
        or any(not isinstance(argument, str) or not argument for argument in command)
    ):
        raise RegressionSelectionError("regression stage command is invalid")
    if command == ["./tools/hatifect-check"]:
        return command
    if command[:3] == ["python3", "-m", "unittest"]:
        if (
            len(command) != 5
            or command[-1] != "-q"
            or re.fullmatch(
                r"tools\.tests\.test_[A-Za-z0-9_]+"
                r"(?:\.[A-Za-z_][A-Za-z0-9_]*){0,2}",
                command[3],
            )
            is None
        ):
            raise RegressionSelectionError("Python regression command is not an exact tooling test")
        module = ".".join(command[3].split(".")[:3])
        module_path = root / (module.replace(".", "/") + ".py")
        if not module_path.is_file() or module_path.is_symlink():
            raise RegressionSelectionError("Python regression test module is missing or unsafe")
        return command
    if command[0] in {"./tools/hatifect-smoke", "./tools/hatifect-ui-test"}:
        if len(command) != 2 or not command[1]:
            raise RegressionSelectionError("scenario regression command is invalid")
        expected_kind = "smoke" if command[0].endswith("smoke") else "ui"
        _scenario_context(command[1], expected_kind)
        return command
    if command[0] == "./tools/hatifect-test":
        if command in (entry[0] for entry in INTEGRATION_COMMANDS.values()):
            return command
        if len(command) not in {3, 4, 5, 6} or command[1] != "--project":
            raise RegressionSelectionError("project regression command is invalid")
        project_path = _normalize_path(command[2], root=root)
        project = inventory.projects.get(project_path)
        if project is None or not project.is_test:
            raise RegressionSelectionError("project regression command is invalid")
        remainder = command[3:]
        if remainder and remainder[-1] == "--platform":
            if not inventory.is_platform(project):
                raise RegressionSelectionError("host-free test project cannot request platform mode")
            remainder = remainder[:-1]
        elif inventory.is_platform(project):
            raise RegressionSelectionError("platform test project requires platform mode")
        if remainder:
            if (
                len(remainder) != 2
                or remainder[0] != "--test-filter"
                or re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.+`]*", remainder[1]) is None
            ):
                raise RegressionSelectionError(
                    "project regression command contains unsupported arguments"
                )
        return command
    raise RegressionSelectionError("regression plan contains a non-canonical command")


def _terminate_process_group(process: subprocess.Popen[bytes], *, force: bool) -> None:
    try:
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGKILL if force else signal.SIGTERM)
        elif force:
            process.kill()
        else:
            process.terminate()
    except ProcessLookupError:
        pass


def _process_group_exists(process_group_id: int) -> bool:
    try:
        os.killpg(process_group_id, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def _execute(
    command: list[str],
    root: Path,
    environment: dict[str, str],
    *,
    timeout_seconds: float = MAX_STAGE_SECONDS,
) -> tuple[int, str]:
    if os.name != "posix":
        return 2, "HATIFECT_REGRESSION_PROCESS_GROUP_UNSUPPORTED\n"
    process = subprocess.Popen(
        command,
        cwd=root,
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )
    process_group_id = process.pid
    assert process.stdout is not None
    tail = bytearray()

    def drain_output() -> None:
        try:
            while chunk := process.stdout.read(8192):
                tail.extend(chunk)
                if len(tail) > MAX_CAPTURE_BYTES:
                    del tail[: len(tail) - MAX_CAPTURE_BYTES]
        except (OSError, ValueError):
            pass

    reader = threading.Thread(target=drain_output, daemon=True)
    reader.start()
    timed_out = False
    try:
        exit_code = process.wait(timeout=timeout_seconds)
    except subprocess.TimeoutExpired:
        timed_out = True
        _terminate_process_group(process, force=False)
        deadline = time.monotonic() + TERMINATION_GRACE_SECONDS
        while time.monotonic() < deadline:
            leader_exited = process.poll() is not None
            if not _process_group_exists(process_group_id):
                break
            if leader_exited:
                _terminate_process_group(process, force=True)
                break
            time.sleep(0.01)
        if _process_group_exists(process_group_id):
            _terminate_process_group(process, force=True)
        try:
            exit_code = process.wait(timeout=TERMINATION_GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            _terminate_process_group(process, force=True)
            exit_code = process.wait()

    reader.join(timeout=TERMINATION_GRACE_SECONDS)
    if reader.is_alive():
        _terminate_process_group(process, force=True)
        process.stdout.close()
        reader.join(timeout=TERMINATION_GRACE_SECONDS)
    else:
        process.stdout.close()

    if timed_out:
        marker = b"\nHATIFECT_REGRESSION_TIMEOUT\n"
        tail.extend(marker)
        if len(tail) > MAX_CAPTURE_BYTES:
            del tail[: len(tail) - MAX_CAPTURE_BYTES]
        exit_code = 2
    return exit_code, tail.decode("utf-8", errors="replace")


def _parsed_test_count(output: str) -> int | None:
    structured = [
        *(int(match.group("count")) for match in PYTHON_SUITE_COUNT_RE.finditer(output)),
        *(int(match.group("count")) for match in DOTNET_COUNT_RE.finditer(output)),
    ]
    if structured:
        return sum(structured)
    match = UNITTEST_COUNT_RE.search(output)
    return int(match.group("count")) if match is not None else None


def _validate_plan(
    plan: dict[str, Any], root: Path, *, verify_candidate: bool
) -> Inventory:
    required = {
        "formatVersion",
        "repositoryHead",
        "worktreePaths",
        "changedPaths",
        "exactTests",
        "finalRequested",
        "scenario",
        "scenarioKind",
        "components",
        "integrationAreas",
        "selectionReason",
        "selectedScope",
        "testsExecuted",
        "testsOmittedByScope",
        "plannedScopesNotExecuted",
        "plannedEscalation",
        "escalationTrigger",
        "executionStop",
        "testInventory",
        "plannedRegressionLevel",
        "finalRegressionLevel",
        "authoritativeFullGateSelected",
        "selectionFingerprint",
    }
    if set(plan) != required or plan.get("formatVersion") != 1:
        raise RegressionSelectionError("regression plan schema is invalid")
    if (
        plan["repositoryHead"] != _repository_head(root)
        or plan["selectionFingerprint"] != _selection_fingerprint(plan)
    ):
        raise RegressionSelectionError("regression plan is stale or was modified")
    if (
        plan["testsExecuted"] != []
        or plan["plannedScopesNotExecuted"] != []
        or plan["escalationTrigger"] != []
        or plan["executionStop"] is not None
        or not isinstance(plan["selectedScope"], list)
        or not plan["selectedScope"]
        or len(plan["selectedScope"]) > 256
    ):
        raise RegressionSelectionError("regression plan runtime fields are invalid")
    try:
        inventory = Inventory(root / "Hatifect.slnx")
    except InventoryError as error:
        raise RegressionSelectionError(f"cannot load test inventory: {error}") from error
    if verify_candidate:
        if not isinstance(plan["worktreePaths"], list):
            raise RegressionSelectionError("regression plan has no validated worktree candidate")
        if _worktree_changes(root) != plan["worktreePaths"]:
            raise RegressionSelectionError("repository worktree changed after regression selection")
    expected = build_plan(
        changed_paths=plan["changedPaths"],
        scenario=plan["scenario"],
        scenario_kind=plan["scenarioKind"],
        exact_tests=plan["exactTests"],
        final=plan["finalRequested"],
        root=root,
        head=plan["repositoryHead"],
    )
    semantic_fields = (
        "changedPaths",
        "exactTests",
        "finalRequested",
        "scenario",
        "scenarioKind",
        "components",
        "integrationAreas",
        "selectedScope",
        "testsOmittedByScope",
        "plannedEscalation",
        "testInventory",
        "plannedRegressionLevel",
        "finalRegressionLevel",
        "authoritativeFullGateSelected",
    )
    if any(plan[field] != expected[field] for field in semantic_fields):
        raise RegressionSelectionError("regression plan selection does not match canonical ownership")
    actual_reasons = [
        item for item in plan["selectionReason"] if item.get("code") != "WORKTREE_CHANGE_DISCOVERED"
    ]
    if actual_reasons != expected["selectionReason"]:
        raise RegressionSelectionError("regression plan reasons do not match canonical ownership")
    return inventory


def execute_plan(
    plan: dict[str, Any],
    *,
    root: Path = ROOT,
    executor: Executor = _execute,
    verify_candidate: bool = True,
) -> dict[str, Any]:
    inventory = _validate_plan(plan, root, verify_candidate=verify_candidate)
    validated_commands = [
        _validate_executable_command(stage.get("command"), root=root, inventory=inventory)
        for stage in plan["selectedScope"]
    ]
    report = json.loads(json.dumps(plan))
    report["testsExecuted"] = []
    report["plannedScopesNotExecuted"] = []
    report["escalationTrigger"] = []
    report["executionStop"] = None
    report["result"] = "PASS"
    report["finalRegressionLevel"] = 0
    report["executedTestCount"] = 0
    report["testCountComplete"] = True
    environment = dict(os.environ)
    environment.update({"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"})
    previous_level = 0
    for index, (stage, command) in enumerate(
        zip(plan["selectedScope"], validated_commands, strict=True)
    ):
        if stage["level"] > previous_level:
            report["escalationTrigger"].append(
                {
                    "fromLevel": previous_level,
                    "toLevel": stage["level"],
                    "code": stage["reasonCode"],
                }
            )
        started = time.monotonic()
        try:
            exit_code, output = executor(list(command), root, environment)
        except OSError as error:
            exit_code, output = 2, str(error)
        duration_ms = max(0, round((time.monotonic() - started) * 1000))
        status = "PASS" if exit_code == 0 else "BLOCKED" if exit_code == 2 else "FAIL"
        is_scenario = stage["scopeId"].startswith("scenario:")
        test_count = None if is_scenario else _parsed_test_count(output)
        if status == "PASS" and not is_scenario and (test_count is None or test_count <= 0):
            status = "FAIL"
        count_complete = is_scenario or (
            test_count is not None
            and test_count > 0
            and (status == "PASS" or command[:3] == ["python3", "-m", "unittest"])
        )
        executed = {
            "level": stage["level"],
            "scopeId": stage["scopeId"],
            "command": stage["command"],
            "status": status,
            "exitCode": exit_code,
            "durationMs": duration_ms,
            "testCount": test_count,
            "scenarioCount": 1 if is_scenario else 0,
            "evidenceStatus": (
                "NOT_APPLICABLE"
                if is_scenario
                else "VALID"
                if count_complete
                else "PARTIAL"
                if test_count is not None and test_count > 0
                else "MISSING_OR_ZERO_TEST_COUNT"
            ),
            "outputTail": [_bounded_text(line) for line in output.splitlines()[-12:]],
        }
        report["testsExecuted"].append(executed)
        report["finalRegressionLevel"] = stage["level"]
        previous_level = stage["level"]
        if not is_scenario:
            if not count_complete:
                report["testCountComplete"] = False
            if test_count is not None and test_count > 0:
                report["executedTestCount"] += test_count
        if status != "PASS":
            report["result"] = status
            report["executionStop"] = {
                "level": stage["level"],
                "scopeId": stage["scopeId"],
                "code": (
                    "EXECUTION_BLOCKED"
                    if status == "BLOCKED"
                    else "TEST_EVIDENCE_MISSING"
                    if exit_code == 0
                    else "TEST_FAILURE_STOP"
                ),
            }
            report["plannedScopesNotExecuted"] = [
                {
                    "level": remaining["level"],
                    "scopeId": remaining["scopeId"],
                    "reason": "EARLIER_STAGE_" + status,
                }
                for remaining in plan["selectedScope"][index + 1 :]
            ]
            break
    report["authoritativeFullGatePassed"] = any(
        item["level"] == 5 and item["status"] == "PASS" for item in report["testsExecuted"]
    )
    if len(_canonical_bytes(report)) > MAX_REPORT_BYTES:
        for item in report["testsExecuted"]:
            item["outputTail"] = item["outputTail"][-2:]
    if len(_canonical_bytes(report)) > MAX_REPORT_BYTES:
        raise RegressionSelectionError("regression report exceeds its serialized size bound")
    return report


def publish_report(report: dict[str, Any], parent: Path) -> Path:
    parent = parent.resolve()
    parent.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="regression-", dir=parent))
    target = directory / "regression-report.json"
    payload = _canonical_bytes(report)
    temporary = directory / ".regression-report.tmp"
    with temporary.open("xb") as stream:
        os.chmod(temporary, 0o600)
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, target)
    return target


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="mode", required=True)
    for mode in ("plan", "run"):
        command = subparsers.add_parser(mode)
        command.add_argument("--changed", action="append", default=[], metavar="PATH")
        command.add_argument("--scenario")
        command.add_argument("--scenario-kind", choices=("smoke", "ui"))
        command.add_argument("--test", action="append", default=[], metavar="TEST_ID")
        command.add_argument("--final", action="store_true")
        if mode == "run":
            command.add_argument(
                "--results-directory",
                type=Path,
                default=ROOT / "artifacts/validation",
            )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        plan = build_plan(
            changed_paths=args.changed,
            scenario=args.scenario,
            scenario_kind=args.scenario_kind,
            exact_tests=args.test,
            final=args.final,
            discover_worktree=True,
        )
        if args.mode == "plan":
            sys.stdout.buffer.write(_canonical_bytes(plan))
            return 0
        report = execute_plan(plan)
        path = publish_report(report, args.results_directory)
        print(f"Evidence: {path}")
        print(
            f"RESULT: {report['result']} — final regression level "
            f"{report['finalRegressionLevel']}"
        )
        return 0 if report["result"] == "PASS" else 2 if report["result"] == "BLOCKED" else 1
    except RegressionSelectionError as error:
        print(f"RESULT: FAIL — {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
