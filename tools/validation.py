#!/usr/bin/env python3
"""Canonical local/CI checks. Ordinary validation never deploys or starts the game."""
from __future__ import annotations

import argparse
from concurrent.futures import Future, ThreadPoolExecutor
from dataclasses import dataclass, replace
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
import xml.etree.ElementTree as ET

import architecture_check
import progressive_regression
from test_inventory import Inventory, InventoryError, Project

ROOT = Path(__file__).resolve().parents[1]
BASE_BUILD_PROPERTIES = (
    "-p:HatifectBuildSuite=false", "-p:HatifectDeploySuite=false",
    "-p:EnableModDeploy=false", "-p:EnableModZip=false",
)
# Stable compatibility export used by tooling that intentionally keeps the
# conservative one-node profile outside this validator.
BUILD_PROPERTIES = (*BASE_BUILD_PROPERTIES, "-p:UseSharedCompilation=false", "-nodeReuse:false", "-m:1")
EXPECTED_TOOL_TEST_COUNT = 683
EXPECTED_HOST_FREE_TEST_COUNTS = {
    "hatifect-flow-tests": 803,
    "hatifect-ui-devtools-tests": 10,
    "hatifect-ui-planning-tests": 147,
    "hatifect-ui-runtime-tests": 703,
    "hatifect-ui-semantics-tests": 67,
    "hatifect-ui-tooling-server-tests": 16,
    "hatifect-ui-tooling-tests": 192,
}
RUNTIME_TEST_PROJECT = "hatifect-ui-runtime-tests"
RUNTIME_TEST_SHARDS = (
    (
        "gates",
        "FullyQualifiedName~Hatifect.UI.Runtime.Tests.SemanticRuntimePerformanceGateTests",
        10,
    ),
    (
        "adaptive",
        "(FullyQualifiedName=Hatifect.UI.Runtime.Tests.CollectionPublicationInteractionTests."
        "ColdPredecessorSearchReportsTheWorstRemovalTransitionAndLeavesSteadyRenderingBounded"
        "&DisplayName~Adaptive)|FullyQualifiedName~Hatifect.UI.Runtime.Tests."
        "CollectionPublicationPerformanceTests",
        9,
    ),
    (
        "remainder",
        "FullyQualifiedName!~Hatifect.UI.Runtime.Tests.SemanticRuntimePerformanceGateTests"
        "&FullyQualifiedName!~Hatifect.UI.Runtime.Tests.CollectionPublicationPerformanceTests"
        "&(FullyQualifiedName!=Hatifect.UI.Runtime.Tests.CollectionPublicationInteractionTests."
        "ColdPredecessorSearchReportsTheWorstRemovalTransitionAndLeavesSteadyRenderingBounded"
        "|DisplayName!~Adaptive)",
        684,
    ),
)


@dataclass(frozen=True)
class PerformanceProfile:
    name: str
    msbuild_nodes: int
    test_workers: int
    test_process_cpus: int
    diagnostic: bool = False
    python_build_overlap: bool = False
    runtime_test_shards: int = 1

    @property
    def build_properties(self) -> tuple[str, ...]:
        return (
            *BASE_BUILD_PROPERTIES,
            "-p:UseSharedCompilation=false",
            "-nodeReuse:false",
            f"-m:{self.msbuild_nodes}",
        )


@dataclass(frozen=True)
class TrxTestIdentities:
    test_ids: frozenset[str]
    test_names: frozenset[str]


def resolve_performance_profile(name: str) -> PerformanceProfile:
    cpus = max(1, os.cpu_count() or 1)
    if name == "safe":
        return PerformanceProfile(name, 1, 1, cpus)
    workers = min(3, max(1, cpus // 3))
    overlap = name == "fast" and cpus > 1
    return PerformanceProfile(
        name,
        min(4, max(1, cpus - int(overlap))),
        workers,
        max(1, cpus // workers),
        diagnostic=name == "diagnostic",
        python_build_overlap=overlap,
        runtime_test_shards=3 if name == "fast" and workers > 1 else 1,
    )


class ValidationError(RuntimeError):
    def __init__(self, message: str, status: str = "FAIL"):
        super().__init__(message)
        self.status = status


def read_trx(path: Path) -> dict[str, int]:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise ValidationError(f"required TRX is missing or malformed: {path}: {error}") from error
    summaries = root.findall("{*}ResultSummary")
    if len(summaries) != 1 or summaries[0].get("outcome") not in {"Completed", "Passed"}:
        raise ValidationError(f"TRX has no successful result summary: {path}")
    element = summaries[0].find("{*}Counters")
    if element is None:
        raise ValidationError(f"TRX counters are missing: {path}")
    try:
        counts = {key: int(element.attrib[key]) for key in ("total", "executed", "passed", "failed")}
        extras = {key: int(value) for key, value in element.attrib.items() if key not in counts}
    except (KeyError, ValueError) as error:
        raise ValidationError(f"TRX counters are invalid: {path}") from error
    if (counts["total"] <= 0 or counts["executed"] != counts["total"]
            or counts["passed"] != counts["total"] or counts["failed"] != 0
            or any(value < 0 or value != 0 and key != "completed" for key, value in extras.items())):
        raise ValidationError(f"tests failed, were skipped, or no tests executed: {path}: {counts | extras}")
    results = root.findall("{*}Results/{*}UnitTestResult")
    if len(results) != counts["total"] or any(result.get("outcome") != "Passed" for result in results):
        raise ValidationError(f"TRX results disagree with successful counters: {path}")
    execution_ids = [(result.get("executionId") or "").strip() for result in results]
    if not all(execution_ids) or len(set(execution_ids)) != len(execution_ids):
        raise ValidationError(f"TRX execution identities are missing or duplicated: {path}")
    return counts


def read_trx_test_identities(path: Path) -> TrxTestIdentities:
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise ValidationError(f"required TRX is missing or malformed: {path}: {error}") from error
    results = root.findall("{*}Results/{*}UnitTestResult")
    names = [(result.get("testName") or "").strip() for result in results]
    test_ids = [(result.get("testId") or "").strip() for result in results]
    if (not results or not all(names) or not all(test_ids)
            or len(set(names)) != len(names) or len(set(test_ids)) != len(test_ids)):
        raise ValidationError(f"TRX test identities are missing or duplicated: {path}")
    return TrxTestIdentities(frozenset(test_ids), frozenset(names))


def materialize_solution(root: Path, projects: tuple[Project, ...], output: Path) -> None:
    if not projects:
        raise ValidationError("cannot build an empty project selection")
    entries: list[str] = []
    configurations: list[str] = []
    for project in projects:
        identity = "{" + str(uuid.uuid5(uuid.NAMESPACE_URL, project.path)).upper() + "}"
        relative = os.path.relpath(root / project.path, output.parent).replace("/", "\\")
        entries.extend([
            f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "{project.name}", "{relative}", "{identity}"',
            "EndProject",
        ])
        for configuration in ("Debug", "Release"):
            configurations.extend([
                f"\t\t{identity}.{configuration}|Any CPU.ActiveCfg = {configuration}|Any CPU",
                f"\t\t{identity}.{configuration}|Any CPU.Build.0 = {configuration}|Any CPU",
            ])
    lines = [
        "Microsoft Visual Studio Solution File, Format Version 12.00", "# Visual Studio Version 17",
        *entries, "Global", "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
        "\t\tDebug|Any CPU = Debug|Any CPU", "\t\tRelease|Any CPU = Release|Any CPU", "\tEndGlobalSection",
        "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution", *configurations,
        "\tEndGlobalSection", "EndGlobal",
    ]
    output.write_text("\n".join(lines) + "\n", encoding="utf-8")


def resolve_dotnet(*, tests: bool = False) -> str:
    variable = "HATIFECT_TEST_DOTNET" if tests else "HATIFECT_DOTNET"
    explicit = os.environ.get(variable)
    candidates = [explicit] if explicit else [
        os.environ.get("HATIFECT_DOTNET"),
        str(Path.home() / ".dotnet/hatifect-x64-8/dotnet") if platform.system() == "Darwin" else None,
        shutil.which("dotnet"),
        "/usr/local/share/dotnet/x64/dotnet" if platform.system() == "Darwin" else None,
    ]
    for candidate in dict.fromkeys(candidates):
        if not candidate or not os.access(candidate, os.X_OK):
            continue
        try:
            sdks = subprocess.run([candidate, "--list-sdks"], check=True, capture_output=True, text=True, timeout=20)
            runtimes = subprocess.run([candidate, "--list-runtimes"], check=True, capture_output=True, text=True, timeout=20)
        except (OSError, subprocess.SubprocessError):
            continue
        if re.search(r"^8\.0\.", sdks.stdout, re.MULTILINE) and (
            not tests or re.search(r"^Microsoft\.NETCore\.App 6\.0\.", runtimes.stdout, re.MULTILINE)
        ):
            return candidate
    detail = " plus .NET 6 runtime" if tests else ""
    raise ValidationError(f".NET 8 SDK{detail} is required; configure {variable}", "BLOCKED")


def changed_paths_from_git(root: Path, base: str) -> tuple[str, ...]:
    try:
        resolved = subprocess.run(
            ["git", "-C", str(root), "rev-parse", "--verify", f"{base}^{{commit}}"],
            check=True,
            capture_output=True,
            text=True,
            timeout=30,
        ).stdout.strip()
        tracked = subprocess.run(
            ["git", "-C", str(root), "diff", "--name-only", "-z", resolved, "--"],
            check=True,
            capture_output=True,
            timeout=60,
        ).stdout
        untracked = subprocess.run(
            ["git", "-C", str(root), "ls-files", "--others", "--exclude-standard", "-z"],
            check=True,
            capture_output=True,
            timeout=60,
        ).stdout
    except (OSError, subprocess.SubprocessError) as error:
        raise ValidationError(f"cannot resolve affected-test base {base!r}: {error}", "BLOCKED") from error
    try:
        return tuple(sorted({
            item.decode("utf-8")
            for item in (*tracked.split(b"\0"), *untracked.split(b"\0"))
            if item
        }))
    except UnicodeDecodeError as error:
        raise ValidationError("affected-test paths must be UTF-8", "BLOCKED") from error


class Run:
    def __init__(self, root: Path, results_directory: Path | None = None,
                 profile: PerformanceProfile | None = None):
        self.root = root
        self.profile = profile or resolve_performance_profile("safe")
        base = results_directory or root / "artifacts/validation"
        base.mkdir(parents=True, exist_ok=True)
        self.directory = Path(tempfile.mkdtemp(prefix="run-", dir=base))
        self.stages: list[dict] = []
        self._stages_lock = threading.Lock()
        self._command_cancellation: threading.Event | None = None
        self.environment = dict(os.environ)
        self.environment.update({
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
            "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
        })
        print(f"Evidence: {self.directory}", flush=True)

    def reserve_stage(self, name: str) -> dict:
        with self._stages_lock:
            log = self.directory / f"{len(self.stages) + 1:03d}-{name}.log"
            stage = {"name": name, "log": str(log), "status": "BLOCKED"}
            self.stages.append(stage)
        return stage

    @staticmethod
    def stop_process(process: subprocess.Popen) -> None:
        if os.name == "nt":
            try:
                result = subprocess.run(
                    ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=10,
                    check=False,
                )
            except (OSError, subprocess.SubprocessError) as error:
                try:
                    process.kill()
                except OSError:
                    pass
                raise ValidationError(
                    f"could not stop owned Windows process tree {process.pid}: {error}",
                    "BLOCKED",
                ) from error
            if result.returncode != 0:
                try:
                    process.kill()
                    process.wait(timeout=5)
                except (OSError, subprocess.TimeoutExpired):
                    pass
                raise ValidationError(
                    f"taskkill could not stop owned Windows process tree {process.pid} "
                    f"(exit {result.returncode})",
                    "BLOCKED",
                )
            try:
                process.wait(timeout=5)
                return
            except subprocess.TimeoutExpired as error:
                try:
                    process.kill()
                    process.wait(timeout=5)
                except (OSError, subprocess.TimeoutExpired):
                    pass
                raise ValidationError(
                    f"owned Windows process tree {process.pid} did not stop after taskkill",
                    "BLOCKED",
                ) from error

        process_group = process.pid
        try:
            os.killpg(process_group, signal.SIGTERM)
        except ProcessLookupError:
            return
        deadline = time.monotonic() + 1
        while time.monotonic() < deadline:
            try:
                os.killpg(process_group, 0)
            except ProcessLookupError:
                pass
                break
            time.sleep(.05)
        else:
            try:
                os.killpg(process_group, signal.SIGKILL)
            except ProcessLookupError:
                pass
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(process_group, signal.SIGKILL)
            except ProcessLookupError:
                pass

    def command(self, name: str, arguments: list[str], *, timeout: int = 1200,
                environment: dict[str, str] | None = None,
                reserved_stage: dict | None = None) -> str:
        stage = reserved_stage or self.reserve_stage(name)
        if stage.get("name") != name or not any(item is stage for item in self.stages):
            raise ValidationError(f"invalid reserved validation stage: {name}")
        stage["command"] = arguments
        log = Path(stage["log"])
        print(f"{name}: running; log: {log}", flush=True)
        command_environment = self.environment | (environment or {})
        try:
            with log.open("w", encoding="utf-8") as stream:
                cancellation = self._command_cancellation
                if cancellation is not None and cancellation.is_set():
                    raise ValidationError(f"{name} cancelled before execution", "BLOCKED")
                process = subprocess.Popen(
                    arguments,
                    cwd=self.root,
                    env=command_environment,
                    stdout=stream,
                    stderr=subprocess.STDOUT,
                    start_new_session=os.name != "nt",
                    creationflags=(
                        subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
                    ),
                )
                deadline = time.monotonic() + timeout
                while True:
                    try:
                        return_code = process.wait(timeout=.2)
                        break
                    except subprocess.TimeoutExpired:
                        if cancellation is not None and cancellation.is_set():
                            self.stop_process(process)
                            stage["exitCode"] = process.returncode
                            raise ValidationError(f"{name} cancelled by sibling validation", "BLOCKED")
                        if time.monotonic() >= deadline:
                            if cancellation is not None:
                                cancellation.set()
                            self.stop_process(process)
                            stage["exitCode"] = process.returncode
                            raise ValidationError(f"{name} timed out after {timeout} seconds", "BLOCKED")
            stage["exitCode"] = return_code
        except ValidationError:
            raise
        except (OSError, subprocess.TimeoutExpired) as error:
            if self._command_cancellation is not None:
                self._command_cancellation.set()
            raise ValidationError(f"{name} could not execute: {error}; log: {log}", "BLOCKED") from error
        output = log.read_text(encoding="utf-8", errors="replace")
        if return_code:
            stage["status"] = "FAIL"
            if self._command_cancellation is not None:
                self._command_cancellation.set()
            print("\n".join(output.splitlines()[-40:]), file=sys.stderr)
            raise ValidationError(f"{name} exited {return_code}; log: {log}")
        stage["status"] = "PASS"
        return output

    def stage(self, name: str) -> dict:
        with self._stages_lock:
            matches = [stage for stage in self.stages if stage.get("name") == name]
        if len(matches) != 1:
            raise ValidationError(f"validation stage identity is ambiguous: {name}")
        return matches[0]

    def python_tests(self, expected_count: int | None = None) -> None:
        suite = unittest.defaultTestLoader.discover(str(self.root / "tools/tests"), pattern="test_*.py")
        if suite.countTestCases() == 0:
            raise ValidationError("no Python tooling tests discovered")
        log = self.directory / "python-tests.log"
        with log.open("w", encoding="utf-8") as stream:
            result = unittest.TextTestRunner(stream=stream, verbosity=2).run(suite)
        count_matches = expected_count is None or result.testsRun == expected_count
        passed = (result.testsRun > 0 and result.wasSuccessful() and not result.skipped
                  and not result.expectedFailures and count_matches)
        self.stages.append({
            "name": "python-tests", "status": "PASS" if passed else "FAIL", "log": str(log),
            "total": result.testsRun, "failed": len(result.failures) + len(result.errors),
            "skipped": len(result.skipped) + len(result.expectedFailures),
            "expectedTotal": expected_count,
        })
        print(f"Python tests: {result.testsRun}; failures: {len(result.failures) + len(result.errors)}; skipped: {len(result.skipped)}")
        if not passed:
            print(log.read_text(encoding="utf-8"), file=sys.stderr)
            raise ValidationError(
                f"Python tooling tests failed or skipped, or changed count "
                f"(expected {expected_count}, actual {result.testsRun}); log: {log}"
            )

    def python_tests_isolated(self, expected_count: int, reserved_stage: dict | None = None) -> None:
        try:
            worker_root = self.directory / "python-worker"
            worker_root.mkdir()
            started = time.time_ns()
            self.command("python-tests", [
                sys.executable,
                str(self.root / "tools/validation.py"),
                "test",
                "tools",
                "--performance-profile",
                "safe",
                "--results-directory",
                str(worker_root),
                "--expected-tool-count",
                str(expected_count),
            ], reserved_stage=reserved_stage)
            stage = reserved_stage or self.stage("python-tests")
            runs = sorted(
                path for path in worker_root.iterdir()
                if path.is_dir() and path.name.startswith("run-")
            )
            if len(runs) != 1:
                raise ValidationError(
                    f"isolated Python tests produced {len(runs)} evidence runs; expected exactly one"
                )
            summary_path = runs[0] / "summary.json"
            if not summary_path.is_file() or summary_path.stat().st_mtime_ns < started:
                raise ValidationError("isolated Python test evidence is missing or stale")
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
            if not isinstance(summary, dict):
                raise ValidationError("isolated Python test summary must be a JSON object")
            matches = [
                item for item in summary.get("stages", [])
                if isinstance(item, dict) and item.get("name") == "python-tests"
            ]
            if summary.get("status") != "PASS" or len(matches) != 1:
                raise ValidationError("isolated Python test evidence has no unique successful stage")
            evidence = matches[0]
            total = evidence.get("total")
            failed = evidence.get("failed")
            skipped = evidence.get("skipped")
            expected_total = evidence.get("expectedTotal")
            counters = (total, expected_total, failed, skipped)
            if (any(type(value) is not int for value in counters)
                    or evidence.get("status") != "PASS" or total != expected_count
                    or expected_total != expected_count or failed != 0 or skipped != 0):
                raise ValidationError(
                    "isolated Python tooling tests failed, skipped, or changed count "
                    f"(expected {expected_count}, declared {expected_total}, actual {total}, "
                    f"failed {failed}, skipped {skipped})"
                )
            worker_directory = runs[0].resolve()
            raw_log = evidence.get("log")
            if not isinstance(raw_log, str) or not raw_log:
                raise ValidationError("isolated Python test evidence log is missing")
            evidence_log = Path(raw_log).resolve()
            if (not evidence_log.is_relative_to(worker_directory)
                    or not evidence_log.is_file()
                    or evidence_log.stat().st_mtime_ns < started):
                raise ValidationError("isolated Python test evidence log is outside the worker run, missing, or stale")
            stage.update({
                "total": total,
                "failed": failed,
                "skipped": skipped,
                "expectedTotal": expected_count,
                "workerSummary": str(summary_path),
                "workerLog": str(evidence_log),
            })
            print(f"Python tests: {total}; failures: 0; skipped: 0", flush=True)
        except (OSError, ValueError, TypeError, json.JSONDecodeError, ValidationError) as error:
            if isinstance(error, ValidationError):
                if reserved_stage is not None:
                    stage = reserved_stage
                else:
                    matches = [item for item in self.stages if item.get("name") == "python-tests"]
                    stage = matches[0] if len(matches) == 1 else None
                if stage is not None and stage.get("status") not in {"FAIL", "BLOCKED"}:
                    stage["status"] = "FAIL"
                if self._command_cancellation is not None:
                    self._command_cancellation.set()
                raise
            stage = reserved_stage
            if stage is not None:
                stage["status"] = "FAIL"
            if self._command_cancellation is not None:
                self._command_cancellation.set()
            raise ValidationError(f"cannot validate isolated Python test evidence: {error}") from error

    def static_contracts(self) -> None:
        self.command("architecture", [sys.executable, str(self.root / "tools/architecture_check.py")])
        self.command("semantic-public-api", [sys.executable, str(self.root / "Hatifect UI/tools/verify_public_api.py")])

    def static(self, expected_tool_count: int | None = None) -> None:
        self.static_contracts()
        if expected_tool_count is None:
            self.python_tests()
        else:
            self.python_tests(expected_tool_count)

    def game_references(self, relative: str, output: str) -> None:
        stage = {"name": "game-references", "project": relative, "status": "BLOCKED"}
        self.stages.append(stage)
        try:
            metadata = json.loads(output)
            game_path = metadata["Properties"]["GamePath"]
            references = metadata["Items"]["Reference"]
            if not isinstance(game_path, str) or not game_path.strip() or not isinstance(references, list):
                raise ValueError("GamePath and evaluated Reference items are required")
            project_directory = (self.root / relative).parent
            game = Path(game_path.replace("\\", "/"))
            game = (project_directory / game).resolve()
            # ModBuildConfig 4.3.2 requires both files in BeforeBuild. Also check
            # the evaluated game-reference closure, including SMAPI internals.
            required = {game / "Stardew Valley.dll", game / "StardewModdingAPI.dll"}
            resolved = set()
            for item in references:
                if not isinstance(item, dict):
                    raise ValueError("each evaluated Reference must be an object")
                hint = item.get("HintPath", "")
                if not isinstance(hint, str):
                    raise ValueError("Reference HintPath must be a string")
                if not hint:
                    continue
                path = (project_directory / hint.replace("\\", "/")).resolve()
                if path.is_relative_to(game):
                    resolved.add(path)
            if not resolved:
                raise ValueError("no evaluated game Reference HintPaths were found")
            required.update(resolved)
        except (ValueError, KeyError, TypeError, OSError) as error:
            raise ValidationError(f"cannot resolve game references for {relative}: {error}; see game-path log", "BLOCKED") from error
        stage["gamePath"] = str(game)
        stage["requiredReferences"] = sorted(str(path) for path in required)
        missing = sorted(str(path) for path in required if not path.is_file())
        stage["missingReferences"] = missing
        if not game.is_dir() or missing:
            raise ValidationError(
                f"game references unavailable for {relative}; GamePath={game}; missing: {', '.join(missing)}; "
                "install the required game/SMAPI files or set HATIFECT_GAME_PATH", "BLOCKED")
        stage["status"] = "PASS"

    def build(self, inventory: Inventory, projects: tuple[Project, ...], dotnet: str) -> None:
        self.environment["HATIFECT_DOTNET"] = dotnet
        # These tests execute MSBuild, so keep them in the SDK-equipped build
        # stage rather than adding an SDK dependency to Python-only static CI.
        self.command("build-metadata-tests", [sys.executable, str(self.root / "tools/build_metadata_tests.py")])
        closure = set()

        def visit(relative: str) -> None:
            if relative not in closure:
                closure.add(relative)
                for dependency in inventory.dependencies[relative]:
                    visit(dependency)

        for project in projects:
            visit(project.path)
        properties = list(self.profile.build_properties)
        consumes_ui_packages = any(
            package.startswith("Hatifect.UI.")
            for relative in closure
            for package in inventory.projects[relative].packages
        )
        if consumes_ui_packages and self.profile.name == "safe":
            pack_command = [str(self.root / "tools/hatifect-pack-ui"), "--host-free"]
            if os.name == "nt":
                bash = shutil.which("bash")
                if not bash:
                    raise ValidationError("Git Bash is required to package UI dependencies on Windows")
                pack_command.insert(0, bash)
            self.command("ui-packages", pack_command)
        if os.environ.get("HATIFECT_GAME_PATH"):
            properties.append("-p:GamePath=" + os.environ["HATIFECT_GAME_PATH"])
        # Keep the generated solution on the repository's volume. Windows
        # cannot calculate relative paths when TEMP and the checkout use
        # different drive letters (for example C: and D: on GitHub runners).
        with tempfile.TemporaryDirectory(prefix="hatifect-build-", dir=self.directory) as temporary:
            solution = Path(temporary) / "Hatifect.sln"
            materialize_solution(self.root, projects, solution)
            if consumes_ui_packages and self.profile.name != "safe":
                feed = self.directory / "ui-packages"
                package_projects = tuple(
                    inventory.projects[path]
                    for path in dict.fromkeys(inventory.package_projects.values())
                    if path in inventory.projects and not inventory.is_platform(inventory.projects[path])
                )
                package_solution = Path(temporary) / "Hatifect.UI.Packages.sln"
                materialize_solution(self.root, package_projects, package_solution)
                self.command("ui-package-contract", [
                    sys.executable, str(self.root / "tools/ui_packages.py"), "verify-contract",
                ])
                pack_properties = [
                    *properties, f"-p:PackageOutputPath={feed}",
                ]
                if self.profile.diagnostic:
                    pack_properties.append("-p:ReportAnalyzer=true")
                self.command("ui-packages", [
                    dotnet, "pack", str(package_solution), "-c", "Release",
                    "--disable-build-servers", "--verbosity", "minimal", "-warnaserror",
                    f"-bl:{self.directory / 'ui-packages.binlog'}", *pack_properties,
                ])
                self.command("ui-package-feed", [
                    sys.executable, str(self.root / "tools/ui_packages.py"),
                    "verify-feed", str(feed), "--host-free",
                ])
                properties.append(f"-p:HatifectUiLocalFeed={feed}")
            restore_arguments = [dotnet, "restore", str(solution)]
            restore_arguments.append("--disable-parallel")
            restore_arguments.extend(("--verbosity", "minimal", f"-bl:{self.directory / 'restore.binlog'}", *properties))
            self.command("restore", restore_arguments)
            for relative in sorted(closure):
                item = inventory.projects[relative]
                if not item.platform_direct:
                    continue
                output = self.command("game-path", [
                    dotnet, "msbuild", str(self.root / relative), "-nologo",
                    "-getProperty:GamePath,TargetFramework", "-getItem:Reference", *properties,
                ])
                self.game_references(relative, output)
            # net6.0 remains required for Stardew/SMAPI compatibility. Keep its
            # SDK lifecycle warning visible; every other warning stays an error.
            build_arguments = [
                dotnet, "build", str(solution), "-c", "Release", "--no-restore",
                "--disable-build-servers", "--verbosity", "minimal", "-warnaserror",
                "-warnNotAsError:NETSDK1138",
                f"-bl:{self.directory / 'build.binlog'}", *properties,
            ]
            if self.profile.diagnostic:
                build_arguments.append("-p:ReportAnalyzer=true")
            self.command("build", build_arguments)

    def build_with_python_tests(
        self,
        inventory: Inventory,
        projects: tuple[Project, ...],
        dotnet: str,
        expected_tool_count: int,
    ) -> None:
        self.environment["HATIFECT_DOTNET"] = dotnet
        python_stage = self.reserve_stage("python-tests")
        cancellation = threading.Event()
        self._command_cancellation = cancellation
        failures: list[tuple[str, ValidationError]] = []
        unexpected: BaseException | None = None
        executor = ThreadPoolExecutor(max_workers=2, thread_name_prefix="hatifect-check")

        def guarded(action, *arguments):
            try:
                return action(*arguments)
            except BaseException:
                cancellation.set()
                raise

        try:
            branches = (
                ("python-tests", executor.submit(
                    guarded,
                    self.python_tests_isolated,
                    expected_tool_count,
                    python_stage,
                )),
                ("build", executor.submit(guarded, self.build, inventory, projects, dotnet)),
            )
            for name, future in branches:
                try:
                    future.result()
                except ValidationError as error:
                    failures.append((name, error))
                    cancellation.set()
                except BaseException as error:
                    cancellation.set()
                    if name == "python-tests":
                        python_stage["status"] = "FAIL"
                    else:
                        with self._stages_lock:
                            self.stages.append({
                                "name": "build-orchestration",
                                "status": "FAIL",
                                "error": str(error),
                            })
                    unexpected = unexpected or error
        finally:
            cancellation.set()
            executor.shutdown(wait=True, cancel_futures=True)
            self._command_cancellation = None
        if unexpected is not None:
            if isinstance(unexpected, (KeyboardInterrupt, SystemExit)):
                raise unexpected
            raise ValidationError(f"overlapped Python/build validation crashed: {unexpected}") from unexpected
        if failures:
            status = "FAIL" if any(error.status == "FAIL" for _, error in failures) else "BLOCKED"
            detail = "; ".join(f"{name}: {error}" for name, error in failures)
            raise ValidationError(f"overlapped Python/build validation failed: {detail}", status)

    def tests(self, projects: tuple[Project, ...], dotnet: str,
              test_filter: str | None = None,
              expected_counts: dict[str, int] | None = None) -> None:
        properties = list(self.profile.build_properties)
        if os.environ.get("HATIFECT_GAME_PATH"):
            properties.append("-p:GamePath=" + os.environ["HATIFECT_GAME_PATH"])

        sharded_runtime = next(
            (
                project for project in projects
                if project.identifier == RUNTIME_TEST_PROJECT
                and self.profile.runtime_test_shards == 3
                and test_filter is None
            ),
            None,
        )
        shard_invocations = []
        if sharded_runtime is not None:
            shard_invocations = [
                (
                    sharded_runtime,
                    f"{RUNTIME_TEST_PROJECT}-shard-{index:02d}-{name}",
                    expression,
                    expected,
                    name,
                )
                for index, (name, expression, expected) in enumerate(RUNTIME_TEST_SHARDS, 1)
            ]
        ordinary_invocations = [
            (project, project.identifier, None, None, None)
            for project in projects
            if project is not sharded_runtime
        ]
        gate_invocations = shard_invocations[:1]
        parallel_invocations = [*shard_invocations[1:], *ordinary_invocations]
        results: dict[str, tuple[dict[str, int], TrxTestIdentities | None]] = {}

        def execute(invocation) -> tuple[dict[str, int], TrxTestIdentities | None]:
            project, stage_name, shard_filter, shard_expected, shard_name = invocation
            directory = self.directory / "tests" / project.identifier
            if shard_name is not None:
                directory /= f"shard-{stage_name.split('-shard-', 1)[1]}"
            directory.mkdir(parents=True)
            arguments = [
                dotnet, "test", str(self.root / project.path), "-c", "Release",
                "--no-build", "--no-restore", "--verbosity", "minimal",
                "--logger", "trx;LogFileName=tests.trx", "--results-directory", str(directory),
                *properties,
            ]
            filter_expression = shard_filter
            if filter_expression is None and test_filter is not None:
                filter_expression = f"FullyQualifiedName={test_filter}"
            if filter_expression is not None:
                arguments.extend(("--filter", filter_expression))
            process_environment: dict[str, str] = {}
            if self.profile.test_workers > 1 and len(parallel_invocations) > 1:
                process_environment["DOTNET_PROCESSOR_COUNT"] = str(self.profile.test_process_cpus)
            if shard_name is not None:
                temporary = directory / "tmp"
                temporary.mkdir()
                process_environment.update({
                    "TMPDIR": str(temporary),
                    "TMP": str(temporary),
                    "TEMP": str(temporary),
                })
            self.command(stage_name, arguments, environment=process_environment or None)
            stage = self.stage(stage_name)
            try:
                trx_path = directory / "tests.trx"
                counts = read_trx(trx_path)
                identities = read_trx_test_identities(trx_path) if shard_name is not None else None
            except ValidationError:
                stage["status"] = "FAIL"
                raise
            expected = shard_expected
            if expected is None and expected_counts is not None:
                expected = expected_counts.get(project.identifier)
            if expected is not None and counts["total"] != expected:
                stage.update(counts | {"status": "FAIL", "expectedTotal": expected})
                raise ValidationError(
                    f"test count changed for {stage_name}: expected {expected}, actual {counts['total']}"
                )
            stage.update(counts | {"expectedTotal": expected})
            if shard_name is not None:
                print(
                    f"{project.name} [{shard_name}]: Passed: {counts['passed']}, Failed: 0, Skipped: 0",
                    flush=True,
                )
            else:
                print(f"{project.name}: Passed: {counts['passed']}, Failed: 0, Skipped: 0", flush=True)
            return counts, identities

        failure: Exception | None = None

        def execute_all(invocations) -> None:
            nonlocal failure
            if self.profile.test_workers == 1 or len(invocations) == 1:
                for invocation in invocations:
                    try:
                        results[invocation[1]] = execute(invocation)
                    except Exception as error:
                        failure = failure or error
                return
            with ThreadPoolExecutor(
                max_workers=self.profile.test_workers,
                thread_name_prefix="hatifect-test",
            ) as executor:
                futures: list[tuple[tuple, Future]] = [
                    (invocation, executor.submit(execute, invocation))
                    for invocation in invocations
                ]
                for invocation, future in futures:
                    try:
                        results[invocation[1]] = future.result()
                    except Exception as error:
                        failure = failure or error

        execute_all(parallel_invocations)
        if failure is None:
            for invocation in gate_invocations:
                try:
                    results[invocation[1]] = execute(invocation)
                except Exception as error:
                    failure = failure or error

        identifiers = [project.identifier for project in projects]
        runtime_stage = None
        if sharded_runtime is not None:
            shard_stages = []
            shard_identities: list[TrxTestIdentities] = []
            for invocation in shard_invocations:
                stage_name = invocation[1]
                matches = [stage for stage in self.stages if stage.get("name") == stage_name]
                if len(matches) == 1:
                    shard_stages.append(matches[0])
                result = results.get(stage_name)
                if result is not None and result[1] is not None:
                    shard_identities.append(result[1])
            runtime_stage = {
                "name": RUNTIME_TEST_PROJECT,
                "status": "BLOCKED",
                "expectedTotal": (
                    expected_counts.get(RUNTIME_TEST_PROJECT)
                    if expected_counts is not None else sum(item[2] for item in RUNTIME_TEST_SHARDS)
                ),
                "shards": shard_stages,
            }
            if (len(shard_stages) == len(RUNTIME_TEST_SHARDS)
                    and len(shard_identities) == len(RUNTIME_TEST_SHARDS)):
                seen_ids: set[str] = set()
                seen_names: set[str] = set()
                duplicate = False
                for identities in shard_identities:
                    if (seen_ids.intersection(identities.test_ids)
                            or seen_names.intersection(identities.test_names)):
                        duplicate = True
                        break
                    seen_ids.update(identities.test_ids)
                    seen_names.update(identities.test_names)
                counts = {
                    key: sum(results[invocation[1]][0][key] for invocation in shard_invocations)
                    for key in ("total", "executed", "passed", "failed")
                }
                runtime_stage.update(counts)
                if duplicate:
                    runtime_stage["status"] = "FAIL"
                    failure = failure or ValidationError("runtime test shards contain duplicate test identities")
                elif counts["total"] != runtime_stage["expectedTotal"]:
                    runtime_stage["status"] = "FAIL"
                    failure = failure or ValidationError(
                        "runtime test shard total changed: "
                        f"expected {runtime_stage['expectedTotal']}, actual {counts['total']}"
                    )
                elif failure is None:
                    runtime_stage["status"] = "PASS"
                    print(
                        f"{sharded_runtime.name}: Passed: {counts['passed']}, Failed: 0, Skipped: 0",
                        flush=True,
                    )
            if any(stage.get("status") == "FAIL" for stage in shard_stages):
                runtime_stage["status"] = "FAIL"

        with self._stages_lock:
            test_names = set(identifiers) | {invocation[1] for invocation in shard_invocations}
            test_stages = {
                stage["name"]: stage for stage in self.stages
                if stage.get("name") in identifiers
            }
            if runtime_stage is not None:
                test_stages[RUNTIME_TEST_PROJECT] = runtime_stage
            non_test_stages = [stage for stage in self.stages if stage.get("name") not in test_names]
            ordered = [test_stages[identifier] for identifier in identifiers if identifier in test_stages]
            self.stages = [*non_test_stages, *ordered]
        if failure is not None:
            raise failure

    def finish(self, status: str, message: str, *, platform_requested: bool) -> None:
        summary = {
            "status": status, "message": message, "stages": self.stages,
            "gameLinkedValidation": status if platform_requested else "NOT_APPLICABLE",
            "gameRuntimeValidation": "NOT_APPLICABLE",
        }
        (self.directory / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        print(f"RESULT: {status} — {message}", flush=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "static", "build", "test"))
    parser.add_argument("scope", nargs="?", choices=("all", "ui", "flow", "ca", "tools"),
                        help="explicit test scope; omitted scope uses progressive regression")
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--platform", action="store_true", help="also build/test game-linked projects; never deploy")
    modes.add_argument("--host-free", action="store_true", help="explicit default: no game-linked projects")
    parser.add_argument("--project", help="one solution test project, used by the CI matrix")
    parser.add_argument(
        "--test-filter",
        help="one exact FullyQualifiedName within --project, for local Level 1 regression",
    )
    parser.add_argument("--no-build", action="store_true", help="test an already built Release candidate without restoring or rebuilding")
    parser.add_argument(
        "--affected-from",
        help="internal iteration only: run tests affected by tracked/untracked changes since this Git commit",
    )
    # Full check/build profiles are intentional local canary surfaces. Public CI
    # and ordinary invocations omit this flag and therefore remain on safe.
    parser.add_argument(
        "--performance-profile",
        choices=("safe", "fast", "diagnostic"),
        default="safe",
        help="safe is the default; fast/diagnostic are explicit full local canary profiles",
    )
    parser.add_argument(
        "--python-build-overlap",
        choices=("profile", "on", "off"),
        default="profile",
        help="full host-free check overlap; profile follows the canary-approved fast default",
    )
    parser.add_argument(
        "--runtime-test-shards",
        choices=("profile", "1", "3"),
        default="profile",
        help="Runtime.Tests sharding; profile follows the canary-approved fast default",
    )
    parser.add_argument("--expected-tool-count", type=int, help=argparse.SUPPRESS)
    parser.add_argument("--results-directory", type=Path, help="parent directory for a fresh evidence run")
    args = parser.parse_args(argv)
    # Progressive stages call back with an explicit scope or project. Keep those
    # calls, and the full check gate, on the direct runner to avoid recursion.
    if (args.command == "test" and args.scope is None and args.project is None
            and args.test_filter is None and not args.platform
            and not args.host_free and not args.no_build and args.affected_from is None
            and args.performance_profile == "safe"
            and args.python_build_overlap == "profile"
            and args.runtime_test_shards == "profile"
            and args.expected_tool_count is None):
        arguments = ["run", "--full-if-clean"]
        if args.results_directory is not None:
            arguments.extend(("--results-directory", str(args.results_directory)))
        return progressive_regression.main(arguments)
    args.scope = args.scope or "all"
    profile = resolve_performance_profile(args.performance_profile)
    overlap = (
        profile.python_build_overlap
        if args.python_build_overlap == "profile"
        else args.python_build_overlap == "on"
    )
    shards = (
        profile.runtime_test_shards
        if args.runtime_test_shards == "profile"
        else int(args.runtime_test_shards)
    )
    available_cpus = max(1, os.cpu_count() or 1)
    msbuild_nodes = (
        1 if profile.name == "safe"
        else min(4, max(1, available_cpus - int(overlap)))
    )
    profile = replace(
        profile,
        msbuild_nodes=msbuild_nodes,
        python_build_overlap=overlap,
        runtime_test_shards=shards,
    )
    run = Run(ROOT, args.results_directory, profile)
    try:
        if args.no_build and args.command != "test":
            raise ValidationError("--no-build is valid only for hatifect-test")
        if args.project and args.command != "test":
            raise ValidationError("--project is valid only for hatifect-test")
        if args.python_build_overlap == "on" and available_cpus < 2:
            raise ValidationError("--python-build-overlap on requires at least two logical CPUs")
        if args.runtime_test_shards == "3" and (
            args.command not in {"check", "test"}
            or args.scope == "tools"
            or args.test_filter is not None
        ):
            raise ValidationError(
                "--runtime-test-shards 3 requires an unfiltered test selection containing Hatifect.UI.Runtime.Tests"
            )
        if args.expected_tool_count is not None and (
            args.command != "test" or args.scope != "tools" or args.expected_tool_count <= 0
        ):
            raise ValidationError("--expected-tool-count is internal to hatifect-test tools and must be positive")
        if args.affected_from is not None and (
            args.command != "test" or args.project or args.test_filter is not None
            or args.platform or args.scope != "all"
        ):
            raise ValidationError(
                "--affected-from is an internal host-free all-scope test selector and cannot be combined with project, filter, or platform selectors"
            )
        if args.test_filter is not None and (
            args.command != "test"
            or not args.project
            or re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.+`]*", args.test_filter) is None
        ):
            raise ValidationError(
                "--test-filter requires hatifect-test --project and one exact FullyQualifiedName"
            )
        if (args.command == "static" or args.scope == "tools") and (args.platform or args.project):
            raise ValidationError("static/tooling validation does not accept platform or project selectors")
        overlap_supported = (
            args.command == "check" and args.scope == "all" and not args.platform
            and args.project is None and args.test_filter is None and not args.no_build
            and args.affected_from is None
        )
        if args.python_build_overlap == "on" and not overlap_supported:
            raise ValidationError(
                "--python-build-overlap on requires a full host-free hatifect-check"
            )
        if profile.python_build_overlap and not overlap_supported:
            profile = replace(
                profile,
                msbuild_nodes=(
                    1 if profile.name == "safe" else min(4, available_cpus)
                ),
                python_build_overlap=False,
            )
            run.profile = profile
        if args.command == "static":
            run.static()
        elif args.scope == "tools":
            if args.command != "test" or args.project:
                raise ValidationError("tools scope is supported only by hatifect-test tools")
            if args.expected_tool_count is None:
                run.python_tests()
            else:
                run.python_tests(args.expected_tool_count)
        else:
            inventory = Inventory(ROOT / "Hatifect.slnx")
            if args.affected_from is not None:
                selected = inventory.affected_tests(changed_paths_from_git(ROOT, args.affected_from))
            else:
                selected = inventory.select(tests=args.command == "test", scope=args.scope,
                                            platform=args.platform, project=args.project)
            if args.command == "build":
                tests = ()
            elif args.affected_from is not None:
                tests = selected
            else:
                tests = inventory.select(
                    tests=True,
                    scope=args.scope,
                    platform=args.platform,
                    project=args.project,
                )
            runtime_selected = any(
                project.identifier == RUNTIME_TEST_PROJECT for project in tests
            )
            if args.runtime_test_shards == "3" and (
                args.command not in {"check", "test"}
                or args.test_filter is not None
                or not runtime_selected
            ):
                raise ValidationError(
                    "--runtime-test-shards 3 requires an unfiltered selection containing Hatifect.UI.Runtime.Tests"
                )
            if profile.runtime_test_shards == 3 and not runtime_selected:
                profile = replace(profile, runtime_test_shards=1)
                run.profile = profile
            if args.command == "check":
                expected_tools = EXPECTED_TOOL_TEST_COUNT if args.scope == "all" and not args.platform else None
                if profile.python_build_overlap:
                    run.static_contracts()
                else:
                    run.static(expected_tools)
            test_dotnet = resolve_dotnet(tests=True) if args.command != "build" else None
            if not args.no_build:
                build_dotnet = resolve_dotnet()
                if profile.python_build_overlap:
                    run.build_with_python_tests(
                        inventory,
                        selected,
                        build_dotnet,
                        EXPECTED_TOOL_TEST_COUNT,
                    )
                else:
                    run.build(inventory, selected, build_dotnet)
            if args.command != "build":
                expected_counts = None
                if (args.command == "check" and args.scope == "all" and not args.platform
                        and args.project is None and args.test_filter is None):
                    identifiers = {project.identifier for project in tests}
                    if identifiers != set(EXPECTED_HOST_FREE_TEST_COUNTS):
                        raise ValidationError(
                            "host-free test project inventory changed; update the reviewed expected-count contract"
                        )
                    expected_counts = EXPECTED_HOST_FREE_TEST_COUNTS
                if expected_counts is None:
                    run.tests(tests, test_dotnet, args.test_filter)
                else:
                    run.tests(tests, test_dotnet, args.test_filter, expected_counts)
        run.finish("PASS", "all selected validation stages satisfied", platform_requested=args.platform)
        return 0
    except (InventoryError, ValidationError) as error:
        status = error.status if isinstance(error, ValidationError) else "FAIL"
        run.finish(status, str(error), platform_requested=args.platform)
        return 2 if status == "BLOCKED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
