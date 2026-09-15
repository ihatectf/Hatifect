"""Behavioral regression tests for the current repository validation entrypoints."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
import architecture_check
import ci_contract
import test_inventory
import validation


class ValidationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        quiet_print = patch("builtins.print")
        quiet_print.start()
        self.addCleanup(quiet_print.stop)

    def project(self, name: str, *, references=(), packages=(), test=False, import_path=None,
                directory: str | None = None) -> str:
        parent = self.root / directory if directory is not None else self.root
        parent.mkdir(parents=True, exist_ok=True)
        path = parent / f"{name}.csproj"
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        group = ET.SubElement(project, "ItemGroup")
        for reference in references:
            ET.SubElement(group, "ProjectReference", Include=reference)
        for package in (*packages, *(("Microsoft.NET.Test.Sdk",) if test else ())):
            ET.SubElement(group, "PackageReference", Include=package, Version="1.0.0")
        if import_path:
            ET.SubElement(project, "Import", Project=import_path)
        ET.ElementTree(project).write(path)
        return path.relative_to(self.root).as_posix()

    def solution(self, *projects: str) -> Path:
        path = self.root / "Hatifect.slnx"
        root = ET.Element("Solution")
        for project in projects:
            ET.SubElement(root, "Project", Path=project)
        ET.ElementTree(root).write(path)
        return path

    def basic_graph(self) -> test_inventory.Inventory:
        ui = self.project("Hatifect.UI.Tests", test=True)
        flow = self.project("Hatifect.Flow.Tests", test=True)
        ca = self.project("Hatifect.ChestsAnywhereOverlay.Tests", test=True,
                          packages=("Pathoschild.Stardew.ModBuildConfig",))
        return test_inventory.Inventory(self.solution(ui, flow, ca))

    def trx(self, *, total=2, executed=2, passed=2, failed=0, outcomes=("Passed", "Passed"),
            execution_ids=("one", "two"), test_names=None, test_ids=None,
            extra=None, summary="Completed") -> Path:
        path = self.root / "tests.trx"
        root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        results = ET.SubElement(root, "Results")
        names = test_names or tuple(f"Example.Tests.Case{index}" for index in range(len(outcomes)))
        identities = test_ids or tuple(f"test-{index}" for index in range(len(outcomes)))
        for outcome, identity, name, test_id in zip(outcomes, execution_ids, names, identities):
            ET.SubElement(
                results,
                "UnitTestResult",
                outcome=outcome,
                executionId=identity,
                testName=name,
                testId=test_id,
            )
        result_summary = ET.SubElement(root, "ResultSummary", outcome=summary)
        counts = {"total": total, "executed": executed, "passed": passed, "failed": failed}
        counts.update(extra or {})
        ET.SubElement(result_summary, "Counters", **{key: str(value) for key, value in counts.items()})
        ET.ElementTree(root).write(path)
        return path

    def write_trx(
        self,
        path: Path,
        names: tuple[str, ...],
        test_ids: tuple[str, ...] | None = None,
    ) -> None:
        root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        results = ET.SubElement(root, "Results")
        identities = test_ids or tuple(f"test-{name}" for name in names)
        for index, (name, test_id) in enumerate(zip(names, identities)):
            ET.SubElement(
                results,
                "UnitTestResult",
                outcome="Passed",
                executionId=f"execution-{index}",
                testName=name,
                testId=test_id,
            )
        summary = ET.SubElement(root, "ResultSummary", outcome="Completed")
        total = str(len(names))
        ET.SubElement(
            summary,
            "Counters",
            total=total,
            executed=total,
            passed=total,
            failed="0",
        )
        ET.ElementTree(root).write(path)

    def test_module_scopes_select_registered_projects_without_category_filters(self) -> None:
        inventory = self.basic_graph()
        self.assertEqual(["Hatifect.UI.Tests", "Hatifect.Flow.Tests"],
                         [item.name for item in inventory.select(tests=True)])
        self.assertEqual(["Hatifect.Flow.Tests"],
                         [item.name for item in inventory.select(tests=True, scope="flow")])
        self.assertEqual(["Hatifect.ChestsAnywhereOverlay.Tests"],
                         [item.name for item in inventory.select(tests=True, scope="ca", platform=True)])
        with self.assertRaisesRegex(test_inventory.InventoryError, "no runnable projects"):
            inventory.select(tests=True, scope="ca")
        with self.assertRaisesRegex(test_inventory.InventoryError, "not in the solution"):
            inventory.select(tests=True, project="Hatifect.Unknown.Tests.csproj")
        self.assertEqual(
            ["Hatifect.UI.Tests"],
            [item.name for item in inventory.affected_tests(("Hatifect.UI.Tests.csproj",))],
        )
        self.assertEqual(
            ["Hatifect.UI.Tests", "Hatifect.Flow.Tests"],
            [item.name for item in inventory.affected_tests(("Directory.Build.props",))],
        )
        runtime = self.project(
            "Hatifect.UI.Runtime",
            directory="Hatifect UI/Hatifect.UI.Runtime",
        )
        runtime_tests = self.project("Hatifect.UI.Runtime.Tests", references=(runtime,), test=True)
        nested_inventory = test_inventory.Inventory(
            self.solution(*inventory.projects, runtime, runtime_tests)
        )
        affected = nested_inventory.affected_tests((
            "Hatifect UI/Hatifect.UI.Runtime/HotReload/UiSemanticLiveAssets.cs",
        ))
        self.assertEqual(
            ["Hatifect.UI.Runtime.Tests"],
            [item.name for item in affected],
        )
        git_results = (
            Mock(stdout="resolved\n"),
            Mock(stdout=b"src/B.cs\0src/A.cs\0"),
            Mock(stdout=b"src/A.cs\0src/C.cs\0"),
        )
        with patch.object(validation.subprocess, "run", side_effect=git_results):
            self.assertEqual(
                ("src/A.cs", "src/B.cs", "src/C.cs"),
                validation.changed_paths_from_git(self.root, "HEAD"),
            )
        with patch.object(
            validation.subprocess,
            "run",
            side_effect=subprocess.CalledProcessError(128, ["git", "rev-parse"]),
        ):
            with self.assertRaisesRegex(validation.ValidationError, "cannot resolve affected-test base") as caught:
                validation.changed_paths_from_git(self.root, "missing")
        self.assertEqual("BLOCKED", caught.exception.status)
        invalid_utf8 = (
            Mock(stdout="resolved\n"),
            Mock(stdout=b"src/valid.cs\0"),
            Mock(stdout=b"src/invalid-\xff.cs\0"),
        )
        with patch.object(validation.subprocess, "run", side_effect=invalid_utf8):
            with self.assertRaisesRegex(validation.ValidationError, "must be UTF-8") as caught:
                validation.changed_paths_from_git(self.root, "HEAD")
        self.assertEqual("BLOCKED", caught.exception.status)
        with self.assertRaisesRegex(test_inventory.InventoryError, "no changed files"):
            inventory.affected_tests(())

    def test_ci_matrix_contains_every_host_free_project_in_solution_order(self) -> None:
        inventory = self.basic_graph()
        matrix = ci_contract.test_matrix(inventory.solution)
        self.assertEqual([
            {"id": "hatifect-ui-tests", "project": "Hatifect.UI.Tests.csproj"},
            {"id": "hatifect-flow-tests", "project": "Hatifect.Flow.Tests.csproj"},
        ], matrix["include"])
        other = self.project("Hatifect.UI.Tooling.Server.Tests", test=True)
        self.solution(*inventory.projects, other)
        updated = ci_contract.test_matrix(inventory.solution)
        self.assertEqual("Hatifect.UI.Tooling.Server.Tests.csproj", updated["include"][-1]["project"])
        self.assertEqual(3, len(updated["include"]))

    def test_imported_platform_reference_propagates_through_versioned_package(self) -> None:
        imported = self.root / "platform.props"
        imported.write_text('<Project><ItemGroup><Reference Include="MonoGame.Framework" /></ItemGroup></Project>')
        producer = self.project("Hatifect.UI.Experience", import_path="platform.props")
        consumer = self.project("Hatifect.ChestsAnywhereOverlay.UI.Semantic", packages=("Hatifect.UI.Experience",))
        tests = self.project("Hatifect.ChestsAnywhereOverlay.Tests", references=(consumer,), test=True)
        neutral = self.project("Hatifect.Flow.Tests", test=True)
        (self.root / "Hatifect.UI.Packages.json").write_text(json.dumps({
            "Packages": [{"Id": "Hatifect.UI.Experience", "Project": producer}],
        }))
        inventory = test_inventory.Inventory(self.solution(producer, consumer, tests, neutral))
        self.assertTrue(inventory.is_platform(inventory.projects[tests]))
        self.assertFalse(inventory.is_platform(inventory.projects[neutral]))
        self.assertEqual([neutral], [item.path for item in inventory.select(tests=True)])

    def test_graph_rejects_missing_unlisted_and_cyclic_project_references(self) -> None:
        a = self.project("Hatifect.UI.Tests", references=("Hatifect.UI.Language.csproj",), test=True)
        solution = self.solution(a)
        with self.assertRaisesRegex(test_inventory.InventoryError, "missing or escapes"):
            test_inventory.Inventory(solution)
        b = self.project("Hatifect.UI.Language")
        with self.assertRaisesRegex(test_inventory.InventoryError, "unlisted project reference"):
            test_inventory.Inventory(solution)
        self.project("Hatifect.UI.Language", references=(a,))
        self.solution(a, b)
        with self.assertRaisesRegex(test_inventory.InventoryError, "cycle"):
            test_inventory.Inventory(solution)

    def test_solution_rejects_escaping_duplicate_and_empty_project_entries(self) -> None:
        tests = self.project("Hatifect.Flow.Tests", test=True)
        for entries, expected in (
            (("../outside.csproj",), "repository-relative"),
            ((tests, tests), "duplicate"),
            ((), "no projects"),
            (("",), "repository-relative"),
        ):
            with self.subTest(entries=entries):
                with self.assertRaisesRegex(test_inventory.InventoryError, expected):
                    test_inventory.Inventory(self.solution(*entries))

    def test_unregistered_and_absent_test_projects_fail_closed(self) -> None:
        production = self.project("Hatifect.UI.Language")
        solution = self.solution(production)
        with self.assertRaisesRegex(test_inventory.InventoryError, "no test projects"):
            test_inventory.Inventory(solution)
        tests = self.project("Hatifect.UI.Tests", test=True)
        self.solution(production, tests)
        self.project("Hatifect.UI.Tooling.Server.Tests", test=True)
        with self.assertRaisesRegex(test_inventory.InventoryError, "outside the solution"):
            test_inventory.Inventory(solution)

    def test_unregistered_production_project_must_join_the_build_inventory(self) -> None:
        tests = self.project("Hatifect.Flow.Tests", test=True)
        solution = self.solution(tests)
        production = self.project("Hatifect.Flow.UI.Semantic")
        with self.assertRaisesRegex(test_inventory.InventoryError, "outside the solution"):
            test_inventory.Inventory(solution)
        self.solution(tests, production)
        inventory = test_inventory.Inventory(solution)
        self.assertIn(production, [item.path for item in inventory.select()])
        self.assertEqual([tests], [item.path for item in inventory.select(tests=True)])

    def test_unknown_first_party_package_or_dynamic_package_identity_is_rejected(self) -> None:
        for package, expected in (("Hatifect.UI.Experience", "no listed producer"), ("$(HiddenPackage)", "statically")):
            with self.subTest(package=package):
                tests = self.project("Hatifect.UI.Tests", packages=(package,), test=True)
                with self.assertRaisesRegex(test_inventory.InventoryError, expected):
                    test_inventory.Inventory(self.solution(tests))

    def test_framework_and_flow_core_cannot_depend_on_concrete_consumers(self) -> None:
        ca = self.project("Hatifect.ChestsAnywhereOverlay")
        core = self.project("Hatifect.Flow.Core", references=(ca,))
        ui = self.project("Hatifect.UI.Runtime", references=(core,))
        tests = self.project("Hatifect.Flow.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(ca, core, ui, tests))
        issues = architecture_check.dependency_issues(inventory)
        self.assertTrue(any("Hatifect.Flow.Core" in issue and "forbidden dependency" in issue for issue in issues))
        self.assertTrue(any("UI framework depends on a consumer" in issue for issue in issues))

    def test_ci_gate_rejects_every_non_success_state_and_missing_job(self) -> None:
        success = {job: "success" for job in ci_contract.REQUIRED_JOBS}
        ci_contract.verify_gate(success)
        for job in ci_contract.REQUIRED_JOBS:
            for state in ("failure", "cancelled", "skipped", "", "unknown"):
                with self.subTest(job=job, state=state):
                    with self.assertRaises(ci_contract.CiContractError):
                        ci_contract.verify_gate(success | {job: state})
        with self.assertRaisesRegex(ci_contract.CiContractError, "missing or unexpected"):
            ci_contract.verify_gate({"architecture": "success", "build": "success"})
        with self.assertRaises(ci_contract.CiContractError):
            ci_contract.verify_gate(success | {"extra": "success"})

    def test_trx_accepts_positive_execution_with_matching_successful_results(self) -> None:
        self.assertEqual({"total": 2, "executed": 2, "passed": 2, "failed": 0},
                         validation.read_trx(self.trx()))

    def test_trx_rejects_zero_execution_failed_skipped_or_contradictory_evidence(self) -> None:
        cases = (
            {"total": 0, "executed": 0, "passed": 0, "outcomes": (), "execution_ids": ()},
            {"passed": 1, "failed": 1, "outcomes": ("Passed", "Failed")},
            {"executed": 1, "passed": 1, "extra": {"notExecuted": 1}},
            {"outcomes": ("Passed", "NotExecuted")},
            {"outcomes": ("Passed",), "execution_ids": ("one",)},
            {"execution_ids": ("one", "one")},
            {"execution_ids": ("", "two")},
            {"summary": "Failed"},
            {"extra": {"warning": 1}},
        )
        for case in cases:
            with self.subTest(case=case):
                with self.assertRaises(validation.ValidationError):
                    validation.read_trx(self.trx(**case))

    def test_trx_rejects_missing_malformed_or_incomplete_counters(self) -> None:
        path = self.root / "tests.trx"
        for text in (None, "<", "<TestRun><ResultSummary outcome='Completed'/></TestRun>",
                     "<TestRun><ResultSummary outcome='Completed'><Counters total='2'/></ResultSummary></TestRun>"):
            with self.subTest(text=text):
                if text is not None:
                    path.write_text(text)
                with self.assertRaises(validation.ValidationError):
                    validation.read_trx(path)

    def test_performance_profiles_preserve_nuget_audit(self) -> None:
        for name in ("safe", "fast", "diagnostic"):
            with self.subTest(name=name):
                profile = validation.resolve_performance_profile(name)
                self.assertNotIn("-p:NuGetAudit=false", profile.build_properties)
        with patch.object(validation.os, "cpu_count", return_value=10):
            safe = validation.resolve_performance_profile("safe")
            fast = validation.resolve_performance_profile("fast")
            diagnostic = validation.resolve_performance_profile("diagnostic")
        self.assertFalse(safe.python_build_overlap)
        self.assertEqual(1, safe.runtime_test_shards)
        self.assertTrue(fast.python_build_overlap)
        self.assertEqual(3, fast.runtime_test_shards)
        self.assertFalse(diagnostic.python_build_overlap)
        self.assertEqual(1, diagnostic.runtime_test_shards)
        with patch.object(validation.os, "cpu_count", return_value=2):
            constrained = validation.resolve_performance_profile("fast")
        self.assertEqual(1, constrained.msbuild_nodes)
        self.assertEqual(1, constrained.runtime_test_shards)

    def test_isolated_python_worker_requires_one_fresh_exact_summary(self) -> None:
        run = validation.Run(self.root)

        def command(name, arguments, **_):
            self.assertEqual("python-tests", name)
            worker_root = Path(arguments[arguments.index("--results-directory") + 1])
            child = worker_root / "run-child"
            child.mkdir()
            (child / "summary.json").write_text(json.dumps({
                "status": "PASS",
                "stages": [{
                    "name": "python-tests",
                    "status": "PASS",
                    "total": 679,
                    "failed": 0,
                    "skipped": 0,
                    "expectedTotal": 679,
                    "log": str(child / "python-tests.log"),
                }],
            }))
            (child / "python-tests.log").write_text("ok")
            run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(run, "command", side_effect=command):
            run.python_tests_isolated(679)
        self.assertEqual(679, run.stages[-1]["total"])
        self.assertEqual(679, run.stages[-1]["expectedTotal"])
        self.assertTrue(Path(run.stages[-1]["workerSummary"]).is_file())

        invalid = validation.Run(self.root)

        def changed_count(name, arguments, **_):
            worker_root = Path(arguments[arguments.index("--results-directory") + 1])
            child = worker_root / "run-child"
            child.mkdir()
            (child / "summary.json").write_text(json.dumps({
                "status": "PASS",
                "stages": [{
                    "name": "python-tests", "status": "PASS",
                    "total": 667, "failed": 0, "skipped": 0,
                    "expectedTotal": 679,
                    "log": str(child / "python-tests.log"),
                }],
            }))
            (child / "python-tests.log").write_text("wrong count")
            invalid.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(invalid, "command", side_effect=changed_count):
            with self.assertRaisesRegex(validation.ValidationError, "changed count"):
                invalid.python_tests_isolated(679)
        self.assertEqual("FAIL", invalid.stages[-1]["status"])

    def test_isolated_python_worker_rejects_malformed_shape_and_preserves_blocked(self) -> None:
        malformed = validation.Run(self.root)

        def malformed_command(name, arguments, **_):
            worker_root = Path(arguments[arguments.index("--results-directory") + 1])
            child = worker_root / "run-child"
            child.mkdir()
            (child / "summary.json").write_text("[]")
            malformed.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(malformed, "command", side_effect=malformed_command):
            with self.assertRaisesRegex(validation.ValidationError, "JSON object"):
                malformed.python_tests_isolated(679)
        self.assertEqual("FAIL", malformed.stages[-1]["status"])

        blocked = validation.Run(self.root)
        reserved = blocked.reserve_stage("python-tests")
        with patch.object(
            blocked,
            "command",
            side_effect=validation.ValidationError("cancelled", "BLOCKED"),
        ):
            with self.assertRaises(validation.ValidationError) as caught:
                blocked.python_tests_isolated(679, reserved)
        self.assertEqual("BLOCKED", caught.exception.status)
        self.assertEqual("BLOCKED", reserved["status"])

    def test_python_and_build_overlap_in_isolated_branches_with_canonical_stage_order(self) -> None:
        tests = self.project("Hatifect.Flow.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(tests))
        run = validation.Run(self.root)
        python_started = threading.Event()
        build_started = threading.Event()

        def python_worker(expected, reserved):
            self.assertEqual(679, expected)
            self.assertEqual("python-tests", reserved["name"])
            python_started.set()
            self.assertTrue(build_started.wait(2))
            reserved["status"] = "PASS"

        def build_worker(*_):
            build_started.set()
            self.assertTrue(python_started.wait(2))
            with run._stages_lock:
                run.stages.append({"name": "build", "status": "PASS"})

        run.stages.append({"name": "architecture", "status": "PASS"})
        with patch.object(run, "python_tests_isolated", side_effect=python_worker), \
                patch.object(run, "build", side_effect=build_worker):
            run.build_with_python_tests(inventory, inventory.select(), "/sdk/dotnet", 679)

        self.assertEqual(["architecture", "python-tests", "build"], [
            stage["name"] for stage in run.stages
        ])
        self.assertEqual("/sdk/dotnet", run.environment["HATIFECT_DOTNET"])

    def test_overlap_records_unexpected_python_branch_failure(self) -> None:
        tests = self.project("Hatifect.Flow.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(tests))
        run = validation.Run(self.root)
        with patch.object(
            run,
            "python_tests_isolated",
            side_effect=RuntimeError("worker crashed"),
        ), patch.object(run, "build", return_value=None):
            with self.assertRaisesRegex(validation.ValidationError, "worker crashed"):
                run.build_with_python_tests(
                    inventory,
                    inventory.select(),
                    "/sdk/dotnet",
                    679,
                )
        self.assertEqual("FAIL", run.stages[0]["status"])

    def test_overlapped_command_cancels_its_owned_process_group(self) -> None:
        if validation.os.name == "nt":
            self.skipTest("POSIX process-group regression")
        run = validation.Run(self.root)
        cancellation = threading.Event()
        run._command_cancellation = cancellation
        caught: list[validation.ValidationError] = []
        heartbeat = self.root / "descendant.heartbeat"
        child = (
            "import pathlib,signal,time;"
            "signal.signal(signal.SIGTERM, signal.SIG_IGN);"
            f"heartbeat=pathlib.Path({str(heartbeat)!r});"
            "exec('while True:\\n heartbeat.write_text(str(time.monotonic_ns()))\\n time.sleep(.05)')"
        )
        parent = (
            "import subprocess,sys,time;"
            f"subprocess.Popen([sys.executable, '-c', {child!r}]);"
            "time.sleep(30)"
        )

        def invoke() -> None:
            try:
                run.command(
                    "sleeping-worker",
                    [sys.executable, "-c", parent],
                    timeout=30,
                )
            except validation.ValidationError as error:
                caught.append(error)

        worker = threading.Thread(target=invoke)
        started = time.monotonic()
        worker.start()
        deadline = time.monotonic() + 3
        while not heartbeat.is_file() and time.monotonic() < deadline:
            time.sleep(.05)
        self.assertTrue(heartbeat.is_file(), "descendant never started")
        cancellation.set()
        worker.join(3)
        self.assertFalse(worker.is_alive())
        self.assertLess(time.monotonic() - started, 3)
        self.assertEqual("BLOCKED", caught[0].status)
        self.assertEqual("BLOCKED", run.stages[-1]["status"])
        final_heartbeat = heartbeat.read_text()
        time.sleep(.2)
        self.assertEqual(final_heartbeat, heartbeat.read_text())

        windows_process = Mock(pid=42)
        with patch.object(validation.os, "name", "nt"), patch.object(
            validation.subprocess,
            "run",
            return_value=subprocess.CompletedProcess(["taskkill"], 1),
        ):
            with self.assertRaisesRegex(validation.ValidationError, "taskkill") as windows_error:
                validation.Run.stop_process(windows_process)
        self.assertEqual("BLOCKED", windows_error.exception.status)
        windows_process.kill.assert_called_once_with()

    def test_build_and_test_commands_disable_deployment_and_do_not_filter_away_tests(self) -> None:
        tests = self.project("Hatifect.Flow.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(tests))
        selected = inventory.select(tests=True)
        run = validation.Run(self.root)
        calls: list[tuple[str, list[str]]] = []

        def command(name, arguments, **_):
            calls.append((name, arguments))
            if name == "restore":
                solution = Path(arguments[2])
                self.assertTrue(solution.is_file())
                self.assertTrue(solution.is_relative_to(run.directory))
                self.assertIn("Hatifect.Flow.Tests", solution.read_text())
            if name == selected[0].identifier:
                output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
                output.write_bytes(self.trx().read_bytes())
                run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(run, "command", side_effect=command):
            run.build(inventory, selected, "/sdk/dotnet")
            run.tests(selected, "/sdk/dotnet")
        self.assertEqual(["build-metadata-tests", "restore", "build", selected[0].identifier],
                         [name for name, _ in calls])
        self.assertEqual([sys.executable, str(self.root / "tools/build_metadata_tests.py")], calls[0][1])
        self.assertEqual("/sdk/dotnet", run.environment["HATIFECT_DOTNET"])
        for _, arguments in calls[1:]:
            for flag in ("-p:EnableModDeploy=false", "-p:HatifectBuildSuite=false", "-p:HatifectDeploySuite=false"):
                self.assertIn(flag, arguments)
            self.assertNotIn("--filter", arguments)
        self.assertIn("--no-build", calls[-1][1])
        self.assertIn("--no-restore", calls[-1][1])
        self.assertEqual(2, run.stages[-1]["passed"])
        build_arguments = next(arguments for name, arguments in calls if name == "build")
        self.assertIn("-warnaserror", build_arguments)
        self.assertIn("-m:1", build_arguments)
        restore_arguments = next(arguments for name, arguments in calls if name == "restore")
        self.assertIn("--disable-parallel", restore_arguments)
        self.assertEqual(["-warnNotAsError:NETSDK1138"],
                         [value for value in build_arguments if value.startswith("-warnNotAsError:")])

        second = self.project("Hatifect.UI.Tests", test=True)
        with patch.object(validation.os, "cpu_count", return_value=9):
            fast_profile = validation.resolve_performance_profile("fast")
            diagnostic_profile = validation.resolve_performance_profile("diagnostic")
        self.assertFalse(fast_profile.diagnostic)
        self.assertTrue(diagnostic_profile.diagnostic)

        package = self.project("Hatifect.UI.Experience")
        packaged_tests = self.project(
            "Hatifect.UI.Packaged.Tests",
            packages=("Hatifect.UI.Experience",),
            test=True,
        )
        (self.root / "Hatifect.UI.Packages.json").write_text(json.dumps({
            "Packages": [{"Id": "Hatifect.UI.Experience", "Project": package}],
        }))
        fast_inventory = test_inventory.Inventory(
            self.solution(tests, second, package, packaged_tests)
        )
        fast_projects = fast_inventory.select(tests=True)
        fast_run = validation.Run(self.root, profile=fast_profile)
        fast_calls: list[tuple[str, list[str], dict[str, str] | None]] = []
        package_solutions: list[str] = []
        fixture = self.trx().read_bytes()

        def fast_command(name, arguments, *, environment=None, **_):
            fast_calls.append((name, arguments, environment))
            if name == "ui-packages":
                package_solutions.append(Path(arguments[2]).read_text())
            if name in {project.identifier for project in fast_projects}:
                output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
                output.write_bytes(fixture)
                with fast_run._stages_lock:
                    fast_run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(fast_run, "command", side_effect=fast_command):
            fast_run.build(fast_inventory, fast_projects, "/sdk/dotnet")
            fast_run.tests(
                fast_projects,
                "/sdk/dotnet",
                expected_counts={project.identifier: 2 for project in fast_projects},
            )
        fast_restore = next(arguments for name, arguments, _ in fast_calls if name == "restore")
        fast_build = next(arguments for name, arguments, _ in fast_calls if name == "build")
        self.assertIn("--disable-parallel", fast_restore)
        self.assertIn("-m:4", fast_build)
        stage_names = [name for name, _, _ in fast_calls]
        self.assertLess(stage_names.index("ui-package-contract"), stage_names.index("ui-packages"))
        self.assertLess(stage_names.index("ui-packages"), stage_names.index("ui-package-feed"))
        self.assertLess(stage_names.index("ui-package-feed"), stage_names.index("restore"))
        package_arguments = next(arguments for name, arguments, _ in fast_calls if name == "ui-packages")
        self.assertIn("Hatifect.UI.Experience.csproj", package_solutions[0])
        self.assertTrue(any(value.startswith("-p:PackageOutputPath=") for value in package_arguments))
        feed_arguments = next(arguments for name, arguments, _ in fast_calls if name == "ui-package-feed")
        self.assertEqual("verify-feed", feed_arguments[2])
        self.assertIn("--host-free", feed_arguments)
        self.assertTrue(any(value.startswith("-p:HatifectUiLocalFeed=") for value in fast_restore))
        self.assertTrue(any(value.startswith("-p:HatifectUiLocalFeed=") for value in fast_build))
        test_calls = [call for call in fast_calls if call[0] in {project.identifier for project in fast_projects}]
        self.assertEqual(len(fast_projects), len(test_calls))
        self.assertTrue(all(environment == {"DOTNET_PROCESSOR_COUNT": "3"} for _, _, environment in test_calls))
        self.assertEqual(
            [project.identifier for project in fast_projects],
            [stage["name"] for stage in fast_run.stages[-len(fast_projects):]],
        )

        single_run = validation.Run(self.root, profile=fast_profile)
        single_calls: list[tuple[str, list[str], dict[str, str] | None]] = []

        def single_command(name, arguments, *, environment=None, **_):
            single_calls.append((name, arguments, environment))
            output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
            output.write_bytes(fixture)
            with single_run._stages_lock:
                single_run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(single_run, "command", side_effect=single_command):
            single_run.tests((fast_projects[0],), "/sdk/dotnet")
        self.assertIsNone(single_calls[0][2])

    def test_failed_metadata_regression_stops_before_package_restore_and_build(self) -> None:
        project = self.project("Hatifect.Flow.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(project))
        run = validation.Run(self.root)
        with patch.object(run, "command", side_effect=validation.ValidationError("metadata regression")) as command:
            with self.assertRaisesRegex(validation.ValidationError, "metadata regression"):
                run.build(inventory, inventory.select(tests=True), "/sdk/dotnet")
        command.assert_called_once_with(
            "build-metadata-tests", [sys.executable, str(self.root / "tools/build_metadata_tests.py")])

    def test_runtime_shards_are_exhaustive_isolated_and_aggregated(self) -> None:
        runtime = self.project("Hatifect.UI.Runtime.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(runtime))
        project = inventory.select(tests=True)[0]
        with patch.object(validation.os, "cpu_count", return_value=10):
            profile = validation.replace(
                validation.resolve_performance_profile("fast"),
                runtime_test_shards=3,
            )
        run = validation.Run(self.root, profile=profile)
        calls: list[tuple[str, str, dict[str, str] | None]] = []
        expected_by_filter = {
            expression: count for _, expression, count in validation.RUNTIME_TEST_SHARDS
        }

        def command(name, arguments, *, environment=None, **_):
            expression = arguments[arguments.index("--filter") + 1]
            calls.append((name, expression, environment))
            count = expected_by_filter[expression]
            output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
            self.write_trx(output, tuple(f"{name}.Case{index}" for index in range(count)))
            with run._stages_lock:
                run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(run, "command", side_effect=command):
            run.tests(
                (project,),
                "/sdk/dotnet",
                expected_counts={validation.RUNTIME_TEST_PROJECT: 703},
            )

        self.assertEqual(3, len(calls))
        self.assertTrue(calls[-1][0].endswith("shard-01-gates"))
        self.assertEqual(set(expected_by_filter), {expression for _, expression, _ in calls})
        temp_roots = {
            environment["TMPDIR"]
            for _, _, environment in calls
            if environment is not None
        }
        self.assertEqual(3, len(temp_roots))
        self.assertTrue(all(environment["DOTNET_PROCESSOR_COUNT"] == "3"
                            for _, _, environment in calls if environment is not None))
        self.assertEqual([validation.RUNTIME_TEST_PROJECT], [stage["name"] for stage in run.stages])
        stage = run.stages[0]
        self.assertEqual("PASS", stage["status"])
        self.assertEqual(703, stage["total"])
        self.assertEqual([10, 9, 684], [shard["total"] for shard in stage["shards"]])

    def test_runtime_shards_reject_count_drift_and_cross_shard_duplicates(self) -> None:
        runtime = self.project("Hatifect.UI.Runtime.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(runtime))
        project = inventory.select(tests=True)[0]
        with patch.object(validation.os, "cpu_count", return_value=10):
            profile = validation.replace(
                validation.resolve_performance_profile("fast"),
                runtime_test_shards=3,
            )

        for mutation, expected_message in (
            ("count", "test count changed"),
            ("duplicate-name", "duplicate"),
            ("duplicate-id", "duplicate"),
        ):
            with self.subTest(mutation=mutation):
                run = validation.Run(self.root, profile=profile)
                expected_by_filter = {
                    expression: count for _, expression, count in validation.RUNTIME_TEST_SHARDS
                }

                def command(name, arguments, **_):
                    expression = arguments[arguments.index("--filter") + 1]
                    count = expected_by_filter[expression]
                    if mutation == "count" and name.endswith("shard-02-adaptive"):
                        count -= 1
                    names = [f"{name}.Case{index}" for index in range(count)]
                    if mutation == "duplicate-name" and not name.endswith("shard-01-gates"):
                        names[0] = "Shared.Runtime.Test"
                    output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
                    test_ids = None
                    if mutation == "duplicate-id":
                        test_ids = tuple(
                            "shared-test-id" if index == 0 else f"{name}-id"
                            for index, name in enumerate(names)
                        )
                    self.write_trx(output, tuple(names), test_ids)
                    with run._stages_lock:
                        run.stages.append({"name": name, "status": "PASS"})
                    return ""

                with patch.object(run, "command", side_effect=command):
                    with self.assertRaisesRegex(validation.ValidationError, expected_message):
                        run.tests((project,), "/sdk/dotnet")
                self.assertEqual("FAIL", run.stages[0]["status"])

    def test_runtime_shard_aggregate_preserves_blocked_child_status(self) -> None:
        runtime = self.project("Hatifect.UI.Runtime.Tests", test=True)
        project = test_inventory.Inventory(self.solution(runtime)).select(tests=True)[0]
        profile = validation.replace(
            validation.resolve_performance_profile("fast"),
            runtime_test_shards=3,
        )
        run = validation.Run(self.root, profile=profile)

        def command(name, _arguments, **_):
            with run._stages_lock:
                run.stages.append({"name": name, "status": "BLOCKED"})
            raise validation.ValidationError("worker unavailable", "BLOCKED")

        with patch.object(run, "command", side_effect=command):
            with self.assertRaises(validation.ValidationError) as caught:
                run.tests((project,), "/sdk/dotnet")
        self.assertEqual("BLOCKED", caught.exception.status)
        self.assertEqual("BLOCKED", run.stages[0]["status"])

    def test_exact_test_filter_is_additive_and_preserves_trx_validation(self) -> None:
        tests = self.project("Hatifect.Flow.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(tests))
        selected = inventory.select(tests=True)
        run = validation.Run(self.root)
        calls: list[list[str]] = []

        def command(name, arguments, **_):
            calls.append(arguments)
            output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
            output.write_bytes(self.trx(total=1, executed=1, passed=1,
                                        outcomes=("Passed",), execution_ids=("only",)).read_bytes())
            run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(run, "command", side_effect=command):
            run.tests(selected, "/sdk/dotnet", "Example.Namespace.Tests.OneCase")

        self.assertEqual(1, len(calls))
        self.assertEqual(
            "FullyQualifiedName=Example.Namespace.Tests.OneCase",
            calls[0][calls[0].index("--filter") + 1],
        )
        self.assertEqual(1, run.stages[-1]["passed"])

        with patch.object(validation.os, "cpu_count", return_value=10):
            fast = validation.replace(
                validation.resolve_performance_profile("fast"),
                runtime_test_shards=3,
            )
        runtime_path = self.project("Hatifect.UI.Runtime.Tests", test=True)
        runtime_inventory = test_inventory.Inventory(self.solution(tests, runtime_path))
        runtime_project = tuple(
            project for project in runtime_inventory.select(tests=True)
            if project.path == runtime_path
        )
        sharding_run = validation.Run(self.root, profile=fast)
        sharded_calls: list[list[str]] = []

        def exact_command(name, arguments, **_):
            sharded_calls.append(arguments)
            output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
            output.write_bytes(self.trx(total=1, executed=1, passed=1,
                                        outcomes=("Passed",), execution_ids=("only",)).read_bytes())
            sharding_run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(sharding_run, "command", side_effect=exact_command):
            sharding_run.tests(runtime_project, "/sdk/dotnet", "Example.Namespace.Tests.OneCase")
        self.assertEqual(1, len(sharded_calls))
        self.assertEqual(
            "FullyQualifiedName=Example.Namespace.Tests.OneCase",
            sharded_calls[0][sharded_calls[0].index("--filter") + 1],
        )

    def test_exact_test_filter_still_rejects_empty_or_malformed_trx(self) -> None:
        tests = self.project("Hatifect.Flow.Tests", test=True)
        inventory = test_inventory.Inventory(self.solution(tests))
        selected = inventory.select(tests=True)
        fixtures = (
            self.trx(total=0, executed=0, passed=0, outcomes=(), execution_ids=()).read_bytes(),
            b"not xml",
        )
        for fixture in fixtures:
            with self.subTest(fixture=fixture[:12]):
                run = validation.Run(self.root)

                def command(name, arguments, **_):
                    output = Path(
                        arguments[arguments.index("--results-directory") + 1]
                    ) / "tests.trx"
                    output.write_bytes(fixture)
                    run.stages.append({"name": name, "status": "PASS"})
                    return ""

                with patch.object(run, "command", side_effect=command):
                    with self.assertRaises(validation.ValidationError):
                        run.tests(
                            selected,
                            "/sdk/dotnet",
                            "Example.Namespace.Tests.OneCase",
                        )
                self.assertEqual("FAIL", run.stages[-1]["status"])
        run = validation.Run(self.root)

        def changed_count(name, arguments, **_):
            output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
            output.write_bytes(self.trx().read_bytes())
            run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(run, "command", side_effect=changed_count):
            with self.assertRaisesRegex(validation.ValidationError, "test count changed"):
                run.tests(selected, "/sdk/dotnet", expected_counts={selected[0].identifier: 3})
        self.assertEqual("FAIL", run.stages[-1]["status"])

    def test_cli_test_filter_requires_project_and_nonempty_exact_fqn(self) -> None:
        cases = (
            ["test", "all", "--project", "Example.Tests.csproj", "--test-filter", ""],
            ["test", "all", "--test-filter", "Example.Tests.One"],
            ["build", "all", "--project", "Example.Tests.csproj", "--test-filter", "Example.Tests.One"],
            ["test", "all", "--project", "Example.Tests.csproj", "--test-filter", "Example;rm"],
            ["test", "flow", "--affected-from", "HEAD"],
            ["test", "all", "--affected-from", "HEAD", "--platform"],
        )
        for arguments in cases:
            run = Mock()
            with self.subTest(arguments=arguments), patch.object(
                sys, "argv", ["validation.py", *arguments]
            ), patch.object(validation, "Run", return_value=run):
                exit_code = validation.main()

            self.assertEqual(1, exit_code)
            run.static.assert_not_called()
            run.build.assert_not_called()
            run.tests.assert_not_called()
            self.assertEqual("FAIL", run.finish.call_args.args[0])

    def platform_graph(self) -> test_inventory.Inventory:
        production = self.project("Hatifect.Flow", packages=("Pathoschild.Stardew.ModBuildConfig",))
        tests = self.project("Hatifect.Flow.Tests", references=(production,), test=True)
        return test_inventory.Inventory(self.solution(production, tests))

    def test_default_test_routes_to_progressive(self) -> None:
        for status in (0, 1, 2):
            with self.subTest(status=status), patch.object(validation, "ROOT", self.root), \
                    patch.object(validation.progressive_regression, "main", return_value=status) as progressive, \
                    patch.object(validation, "Run") as direct:
                actual = validation.main(["test", "--results-directory", str(self.root / "evidence")])

            self.assertEqual(status, actual)
            progressive.assert_called_once_with([
                "run", "--full-if-clean", "--results-directory", str(self.root / "evidence"),
            ])
            direct.assert_not_called()
            self.assertEqual([], list(self.root.iterdir()))

    def test_bare_test_uses_progressive_default_evidence_directory(self) -> None:
        with patch.object(validation.progressive_regression, "main", return_value=0) as progressive, \
                patch.object(validation, "Run") as direct:
            self.assertEqual(0, validation.main(["test"]))
        progressive.assert_called_once_with(["run", "--full-if-clean"])
        direct.assert_not_called()

    def test_explicit_scopes_projects_and_build_flags_keep_direct_execution(self) -> None:
        inventory = self.basic_graph()
        cases = (
            (["all"], ["Hatifect.UI.Tests", "Hatifect.Flow.Tests"], True, None),
            (["ui"], ["Hatifect.UI.Tests"], True, None),
            (["flow"], ["Hatifect.Flow.Tests"], True, None),
            (["ca", "--platform"], ["Hatifect.ChestsAnywhereOverlay.Tests"], True, None),
            (["--host-free"], ["Hatifect.UI.Tests", "Hatifect.Flow.Tests"], True, None),
            (["--platform"], list(item.name for item in inventory.projects.values()), True, None),
            (["--no-build"], ["Hatifect.UI.Tests", "Hatifect.Flow.Tests"], False, None),
            (["--project", "Hatifect.Flow.Tests.csproj", "--test-filter", "Example.Tests.One"],
             ["Hatifect.Flow.Tests"], True, "Example.Tests.One"),
        )
        for arguments, names, build, test_filter in cases:
            with self.subTest(arguments=arguments), patch.object(validation, "ROOT", self.root), \
                    patch.object(validation, "Run") as run_type, \
                    patch.object(validation, "resolve_dotnet", return_value="/sdk/dotnet"), \
                    patch.object(validation.progressive_regression, "main") as progressive:
                self.assertEqual(0, validation.main(["test", *arguments]))

            run = run_type.return_value
            self.assertEqual(names, [item.name for item in run.tests.call_args.args[0]])
            self.assertEqual(("/sdk/dotnet", test_filter), run.tests.call_args.args[1:])
            self.assertEqual(int(build), run.build.call_count)
            run.static.assert_not_called()
            progressive.assert_not_called()

    def test_tooling_scope_and_full_gate_do_not_reenter_progressive(self) -> None:
        self.basic_graph()
        for arguments in (["test", "tools"], ["check"]):
            with self.subTest(arguments=arguments), patch.object(validation, "ROOT", self.root), \
                    patch.object(validation, "Run") as run_type, \
                    patch.object(validation, "resolve_dotnet", return_value="/sdk/dotnet"), \
                    patch.object(validation, "EXPECTED_HOST_FREE_TEST_COUNTS", {
                        "hatifect-ui-tests": 2,
                        "hatifect-flow-tests": 2,
                    }), \
                    patch.object(validation.progressive_regression, "main") as progressive:
                self.assertEqual(0, validation.main(arguments))
            run = run_type.return_value
            if arguments[0] == "check":
                run.static.assert_called_once_with(683)
                self.assertEqual(1, run.build.call_count)
                self.assertEqual(["Hatifect.UI.Tests", "Hatifect.Flow.Tests"],
                                 [item.name for item in run.tests.call_args.args[0]])
                run.python_tests.assert_not_called()
            else:
                run.python_tests.assert_called_once_with()
                run.build.assert_not_called()
                run.tests.assert_not_called()
            progressive.assert_not_called()

    def test_fast_full_check_uses_overlap_and_shards_while_safe_routes_stay_explicit(self) -> None:
        inventory = self.basic_graph()
        runtime = self.project("Hatifect.UI.Runtime.Tests", test=True)
        self.solution(*inventory.projects, runtime)
        counts = {
            "hatifect-ui-tests": 2,
            "hatifect-flow-tests": 2,
            validation.RUNTIME_TEST_PROJECT: 703,
        }
        with patch.object(validation, "ROOT", self.root), \
                patch.object(validation.os, "cpu_count", return_value=10), \
                patch.object(validation, "Run") as run_type, \
                patch.object(validation, "resolve_dotnet", return_value="/sdk/dotnet"), \
                patch.object(validation, "EXPECTED_HOST_FREE_TEST_COUNTS", counts):
            self.assertEqual(0, validation.main(["check", "--performance-profile", "fast"]))

        run = run_type.return_value
        run.static_contracts.assert_called_once_with()
        run.static.assert_not_called()
        run.build_with_python_tests.assert_called_once()
        self.assertEqual(683, run.build_with_python_tests.call_args.args[-1])
        self.assertEqual(counts, run.tests.call_args.args[-1])
        profile = run_type.call_args.args[2]
        self.assertTrue(profile.python_build_overlap)
        self.assertEqual(3, profile.runtime_test_shards)

        with patch.object(validation, "ROOT", self.root), patch.object(validation, "Run") as invalid:
            self.assertEqual(1, validation.main([
                "test", "ui", "--performance-profile", "fast",
                "--python-build-overlap", "on",
            ]))
        invalid.return_value.build.assert_not_called()
        invalid.return_value.tests.assert_not_called()
        self.assertEqual("FAIL", invalid.return_value.finish.call_args.args[0])

    def test_explicit_runtime_sharding_rejects_selections_without_unfiltered_runtime(self) -> None:
        self.basic_graph()
        cases = (
            ["static", "--runtime-test-shards", "3"],
            ["test", "tools", "--runtime-test-shards", "3"],
            ["build", "all", "--runtime-test-shards", "3"],
            ["test", "flow", "--runtime-test-shards", "3"],
            [
                "test", "all",
                "--project", "Hatifect.Flow.Tests.csproj",
                "--runtime-test-shards", "3",
            ],
            [
                "test", "all",
                "--project", "Hatifect.Flow.Tests.csproj",
                "--test-filter", "Example.Tests.Case",
                "--runtime-test-shards", "3",
            ],
        )
        for arguments in cases:
            with self.subTest(arguments=arguments), patch.object(validation, "ROOT", self.root), \
                    patch.object(validation, "Run") as run_type, \
                    patch.object(validation, "resolve_dotnet", return_value="/sdk/dotnet"):
                self.assertEqual(1, validation.main(arguments))
            run_type.return_value.static.assert_not_called()
            run_type.return_value.build.assert_not_called()
            run_type.return_value.tests.assert_not_called()

    def test_overlap_reserves_one_cpu_for_python_only_when_enabled(self) -> None:
        self.basic_graph()
        with patch.object(validation, "ROOT", self.root), \
                patch.object(validation.os, "cpu_count", return_value=4), \
                patch.object(validation, "Run") as run_type, \
                patch.object(validation, "resolve_dotnet", return_value="/sdk/dotnet"), \
                patch.object(validation, "EXPECTED_HOST_FREE_TEST_COUNTS", {
                    "hatifect-ui-tests": 2,
                    "hatifect-flow-tests": 2,
                }):
            self.assertEqual(0, validation.main([
                "check", "--performance-profile", "fast", "--python-build-overlap", "on",
            ]))
        self.assertEqual(3, run_type.call_args.args[2].msbuild_nodes)

        with patch.object(validation, "ROOT", self.root), \
                patch.object(validation.os, "cpu_count", return_value=1), \
                patch.object(validation, "Run") as constrained:
            self.assertEqual(1, validation.main([
                "check", "--performance-profile", "fast", "--python-build-overlap", "on",
            ]))
        constrained.return_value.static.assert_not_called()
        constrained.return_value.build.assert_not_called()

    def game_reference_metadata(self, game: Path) -> str:
        return json.dumps({
            "Properties": {"GamePath": str(game), "TargetFramework": "net6.0"},
            "Items": {"Reference": [
                {"Identity": Path(name).stem, "HintPath": str(game / name)}
                for name in ("Stardew Valley.dll", "StardewModdingAPI.dll", "MonoGame.Framework.dll",
                             "smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll")
            ]},
        })

    def test_platform_preflight_blocks_empty_and_incomplete_game_before_build(self) -> None:
        inventory = self.platform_graph()
        for name, available in (
            ("empty", ()),
            ("without-smapi", ("Stardew Valley.dll",)),
            ("missing-toolkit", ("Stardew Valley.dll", "StardewModdingAPI.dll", "MonoGame.Framework.dll")),
        ):
            with self.subTest(installation=name):
                game = self.root / name
                game.mkdir()
                for filename in available:
                    (game / filename).write_bytes(b"assembly fixture")
                run = validation.Run(self.root)
                calls: list[str] = []

                def command(stage, arguments, **_):
                    calls.append(stage)
                    return self.game_reference_metadata(game) if stage == "game-path" else ""

                with patch.object(run, "command", side_effect=command):
                    with self.assertRaises(validation.ValidationError) as caught:
                        run.build(inventory, inventory.select(platform=True), "/sdk/dotnet")
                self.assertEqual("BLOCKED", caught.exception.status)
                self.assertIn("game references", str(caught.exception))
                self.assertEqual(["build-metadata-tests", "restore", "game-path"], calls)
                self.assertEqual("BLOCKED", run.stages[-1]["status"])

    def test_platform_preflight_accepts_complete_automatically_resolved_game(self) -> None:
        inventory = self.platform_graph()
        game = self.root / "installed game"
        metadata = self.game_reference_metadata(game)
        for item in json.loads(metadata)["Items"]["Reference"]:
            path = Path(item["HintPath"])
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"assembly fixture")
        run = validation.Run(self.root)
        calls: list[tuple[str, list[str]]] = []

        def command(stage, arguments, **_):
            calls.append((stage, arguments))
            return metadata if stage == "game-path" else ""

        with patch.dict("os.environ", {}, clear=True), patch.object(run, "command", side_effect=command):
            run.build(inventory, inventory.select(platform=True), "/sdk/dotnet")
        self.assertEqual(["build-metadata-tests", "restore", "game-path", "build"], [stage for stage, _ in calls])
        query = next(arguments for stage, arguments in calls if stage == "game-path")
        self.assertIn("-getItem:Reference", query)
        self.assertFalse(any(argument.startswith("-p:GamePath=") for argument in query))
        self.assertEqual("PASS", run.stages[-1]["status"])

    def test_failed_or_timed_out_command_retains_distinct_fail_and_blocked_status(self) -> None:
        for expected in ("FAIL", "BLOCKED"):
            with self.subTest(status=expected):
                run = validation.Run(self.root)
                process = Mock(pid=123, returncode=7 if expected == "FAIL" else -15)
                process.wait.side_effect = (
                    None if expected == "FAIL"
                    else subprocess.TimeoutExpired(["test"], 1)
                )
                if expected == "FAIL":
                    process.wait.return_value = 7
                with patch("validation.subprocess.Popen", return_value=process), patch.object(
                    validation.time,
                    "monotonic",
                    side_effect=None if expected == "FAIL" else [0, 2],
                ), patch.object(run, "stop_process") as stop_process:
                    with self.assertRaises(validation.ValidationError) as caught:
                        run.command("test", ["test"], timeout=1)
                self.assertEqual(expected, caught.exception.status)
                self.assertEqual(expected, run.stages[-1]["status"])
                self.assertTrue(Path(run.stages[-1]["log"]).is_file())
                self.assertEqual(int(expected == "BLOCKED"), stop_process.call_count)

    def test_python_runner_rejects_empty_discovery_and_skipped_execution(self) -> None:
        run = validation.Run(self.root)
        with patch.object(unittest.defaultTestLoader, "discover", return_value=unittest.TestSuite()):
            with self.assertRaisesRegex(validation.ValidationError, "no Python"):
                run.python_tests()

        @unittest.skip("intentional negative fixture")
        def skipped():
            pass

        suite = unittest.TestSuite([unittest.FunctionTestCase(skipped)])
        with patch.object(unittest.defaultTestLoader, "discover", return_value=suite):
            with self.assertRaisesRegex(validation.ValidationError, "failed or skipped"):
                run.python_tests()
        self.assertEqual("FAIL", run.stages[-1]["status"])
        self.assertEqual(1, run.stages[-1]["skipped"])

    def test_each_run_uses_new_evidence_directory_so_old_trx_cannot_satisfy_it(self) -> None:
        first = validation.Run(self.root)
        (first.directory / "old.trx").write_bytes(self.trx().read_bytes())
        second = validation.Run(self.root)
        self.assertNotEqual(first.directory, second.directory)
        self.assertFalse(any(second.directory.iterdir()))

    def test_static_runs_current_public_contract_check_before_tooling_tests(self) -> None:
        run = validation.Run(self.root)
        steps: list[str] = []
        with patch.object(run, "command", side_effect=lambda name, _: steps.append(name)), \
                patch.object(run, "python_tests", side_effect=lambda: steps.append("python-tests")):
            run.static()
        self.assertEqual(["architecture", "semantic-public-api", "python-tests"], steps)


if __name__ == "__main__":
    unittest.main()
