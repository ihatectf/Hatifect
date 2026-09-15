#!/usr/bin/env python3
"""Canonical local/CI checks. Ordinary validation never deploys or starts the game."""
from __future__ import annotations

import argparse
from concurrent.futures import Future, ThreadPoolExecutor
from dataclasses import dataclass
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import threading
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
EXPECTED_TOOL_TEST_COUNT = 668
EXPECTED_HOST_FREE_TEST_COUNTS = {
    "hatifect-flow-tests": 803,
    "hatifect-ui-devtools-tests": 10,
    "hatifect-ui-planning-tests": 147,
    "hatifect-ui-runtime-tests": 703,
    "hatifect-ui-semantics-tests": 67,
    "hatifect-ui-tooling-server-tests": 16,
    "hatifect-ui-tooling-tests": 192,
}


@dataclass(frozen=True)
class PerformanceProfile:
    name: str
    msbuild_nodes: int
    test_workers: int
    test_process_cpus: int
    diagnostic: bool = False

    @property
    def build_properties(self) -> tuple[str, ...]:
        return (
            *BASE_BUILD_PROPERTIES,
            "-p:UseSharedCompilation=false",
            "-nodeReuse:false",
            f"-m:{self.msbuild_nodes}",
        )


def resolve_performance_profile(name: str) -> PerformanceProfile:
    cpus = max(1, os.cpu_count() or 1)
    if name == "safe":
        return PerformanceProfile(name, 1, 1, cpus)
    workers = min(3, max(1, cpus // 3))
    nodes = min(4, cpus)
    return PerformanceProfile(
        name,
        nodes,
        workers,
        max(1, cpus // workers),
        diagnostic=name == "diagnostic",
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
        self.environment = dict(os.environ)
        self.environment.update({
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
            "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
        })
        print(f"Evidence: {self.directory}", flush=True)

    def command(self, name: str, arguments: list[str], *, timeout: int = 1200,
                environment: dict[str, str] | None = None) -> str:
        with self._stages_lock:
            log = self.directory / f"{len(self.stages) + 1:03d}-{name}.log"
            stage = {"name": name, "command": arguments, "log": str(log), "status": "BLOCKED"}
            self.stages.append(stage)
        print(f"{name}: running; log: {log}", flush=True)
        command_environment = self.environment | (environment or {})
        try:
            with log.open("w", encoding="utf-8") as stream:
                process = subprocess.run(arguments, cwd=self.root, env=command_environment,
                                         stdout=stream, stderr=subprocess.STDOUT, timeout=timeout, check=False)
            stage["exitCode"] = process.returncode
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ValidationError(f"{name} could not execute: {error}; log: {log}", "BLOCKED") from error
        output = log.read_text(encoding="utf-8", errors="replace")
        if process.returncode:
            stage["status"] = "FAIL"
            print("\n".join(output.splitlines()[-40:]), file=sys.stderr)
            raise ValidationError(f"{name} exited {process.returncode}; log: {log}")
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

    def static(self, expected_tool_count: int | None = None) -> None:
        self.command("architecture", [sys.executable, str(self.root / "tools/architecture_check.py")])
        self.command("semantic-public-api", [sys.executable, str(self.root / "Hatifect UI/tools/verify_public_api.py")])
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

    def tests(self, projects: tuple[Project, ...], dotnet: str,
              test_filter: str | None = None,
              expected_counts: dict[str, int] | None = None) -> None:
        properties = list(self.profile.build_properties)
        if os.environ.get("HATIFECT_GAME_PATH"):
            properties.append("-p:GamePath=" + os.environ["HATIFECT_GAME_PATH"])

        def execute(project: Project) -> None:
            directory = self.directory / "tests" / project.identifier
            directory.mkdir(parents=True)
            arguments = [
                dotnet, "test", str(self.root / project.path), "-c", "Release",
                "--no-build", "--no-restore", "--verbosity", "minimal",
                "--logger", "trx;LogFileName=tests.trx", "--results-directory", str(directory),
                *properties,
            ]
            if test_filter is not None:
                arguments.extend(("--filter", f"FullyQualifiedName={test_filter}"))
            process_environment = None
            if self.profile.test_workers > 1 and len(projects) > 1:
                process_environment = {"DOTNET_PROCESSOR_COUNT": str(self.profile.test_process_cpus)}
            self.command(project.identifier, arguments, environment=process_environment)
            stage = self.stage(project.identifier)
            try:
                counts = read_trx(directory / "tests.trx")
            except ValidationError:
                stage["status"] = "FAIL"
                raise
            expected = expected_counts.get(project.identifier) if expected_counts is not None else None
            if expected is not None and counts["total"] != expected:
                stage.update(counts | {"status": "FAIL", "expectedTotal": expected})
                raise ValidationError(
                    f"test count changed for {project.name}: expected {expected}, actual {counts['total']}"
                )
            stage.update(counts | {"expectedTotal": expected})
            print(f"{project.name}: Passed: {counts['passed']}, Failed: 0, Skipped: 0", flush=True)

        if self.profile.test_workers == 1 or len(projects) == 1:
            for project in projects:
                execute(project)
            return

        identifiers = [project.identifier for project in projects]
        failure: Exception | None = None
        with ThreadPoolExecutor(
            max_workers=self.profile.test_workers,
            thread_name_prefix="hatifect-test",
        ) as executor:
            futures: list[Future[None]] = [executor.submit(execute, project) for project in projects]
            for future in futures:
                try:
                    future.result()
                except Exception as error:
                    failure = failure or error
        with self._stages_lock:
            test_stages = {stage["name"]: stage for stage in self.stages if stage.get("name") in identifiers}
            non_test_stages = [stage for stage in self.stages if stage.get("name") not in identifiers]
            self.stages = [*non_test_stages, *(test_stages[identifier] for identifier in identifiers)]
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
    parser.add_argument("--results-directory", type=Path, help="parent directory for a fresh evidence run")
    args = parser.parse_args(argv)
    # Progressive stages call back with an explicit scope or project. Keep those
    # calls, and the full check gate, on the direct runner to avoid recursion.
    if (args.command == "test" and args.scope is None and args.project is None
            and args.test_filter is None and not args.platform
            and not args.host_free and not args.no_build and args.affected_from is None
            and args.performance_profile == "safe"):
        arguments = ["run", "--full-if-clean"]
        if args.results_directory is not None:
            arguments.extend(("--results-directory", str(args.results_directory)))
        return progressive_regression.main(arguments)
    args.scope = args.scope or "all"
    profile = resolve_performance_profile(args.performance_profile)
    run = Run(ROOT, args.results_directory, profile)
    try:
        if args.no_build and args.command != "test":
            raise ValidationError("--no-build is valid only for hatifect-test")
        if args.project and args.command != "test":
            raise ValidationError("--project is valid only for hatifect-test")
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
        if args.command == "static":
            run.static()
        elif args.scope == "tools":
            if args.command != "test" or args.project:
                raise ValidationError("tools scope is supported only by hatifect-test tools")
            run.python_tests()
        else:
            inventory = Inventory(ROOT / "Hatifect.slnx")
            if args.command == "check":
                expected_tools = EXPECTED_TOOL_TEST_COUNT if args.scope == "all" and not args.platform else None
                run.static(expected_tools)
            if args.affected_from is not None:
                selected = inventory.affected_tests(changed_paths_from_git(ROOT, args.affected_from))
            else:
                selected = inventory.select(tests=args.command == "test", scope=args.scope,
                                            platform=args.platform, project=args.project)
            test_dotnet = resolve_dotnet(tests=True) if args.command != "build" else None
            if not args.no_build:
                run.build(inventory, selected, resolve_dotnet())
            if args.command != "build":
                tests = selected if args.affected_from is not None else inventory.select(
                    tests=True, scope=args.scope, platform=args.platform, project=args.project
                )
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
