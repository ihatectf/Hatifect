"""Behavioral regression tests for the current repository validation entrypoints."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
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

    def project(self, name: str, *, references=(), packages=(), test=False, import_path=None) -> str:
        path = self.root / f"{name}.csproj"
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        group = ET.SubElement(project, "ItemGroup")
        for reference in references:
            ET.SubElement(group, "ProjectReference", Include=reference)
        for package in (*packages, *(("Microsoft.NET.Test.Sdk",) if test else ())):
            ET.SubElement(group, "PackageReference", Include=package, Version="1.0.0")
        if import_path:
            ET.SubElement(project, "Import", Project=import_path)
        ET.ElementTree(project).write(path)
        return path.name

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
            execution_ids=("one", "two"), extra=None, summary="Completed") -> Path:
        path = self.root / "tests.trx"
        root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        results = ET.SubElement(root, "Results")
        for outcome, identity in zip(outcomes, execution_ids):
            ET.SubElement(results, "UnitTestResult", outcome=outcome, executionId=identity)
        result_summary = ET.SubElement(root, "ResultSummary", outcome=summary)
        counts = {"total": total, "executed": executed, "passed": passed, "failed": failed}
        counts.update(extra or {})
        ET.SubElement(result_summary, "Counters", **{key: str(value) for key, value in counts.items()})
        ET.ElementTree(root).write(path)
        return path

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
                self.assertIn("Hatifect.Flow.Tests", solution.read_text())
            if name == selected[0].identifier:
                output = Path(arguments[arguments.index("--results-directory") + 1]) / "tests.trx"
                output.write_bytes(self.trx().read_bytes())
                run.stages.append({"name": name, "status": "PASS"})
            return ""

        with patch.object(run, "command", side_effect=command):
            run.build(inventory, selected, "/sdk/dotnet")
            run.tests(selected, "/sdk/dotnet")
        self.assertEqual(["restore", "build", selected[0].identifier], [name for name, _ in calls])
        for _, arguments in calls:
            for flag in ("-p:EnableModDeploy=false", "-p:HatifectBuildSuite=false", "-p:HatifectDeploySuite=false"):
                self.assertIn(flag, arguments)
            self.assertNotIn("--filter", arguments)
        self.assertIn("--no-build", calls[-1][1])
        self.assertIn("--no-restore", calls[-1][1])
        self.assertEqual(2, run.stages[-1]["passed"])
        build_arguments = next(arguments for name, arguments in calls if name == "build")
        self.assertIn("-warnaserror", build_arguments)
        self.assertEqual(["-warnNotAsError:NETSDK1138"],
                         [value for value in build_arguments if value.startswith("-warnNotAsError:")])

    def platform_graph(self) -> test_inventory.Inventory:
        production = self.project("Hatifect.Flow", packages=("Pathoschild.Stardew.ModBuildConfig",))
        tests = self.project("Hatifect.Flow.Tests", references=(production,), test=True)
        return test_inventory.Inventory(self.solution(production, tests))

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
                self.assertEqual(["restore", "game-path"], calls)
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
        self.assertEqual(["restore", "game-path", "build"], [stage for stage, _ in calls])
        query = calls[1][1]
        self.assertIn("-getItem:Reference", query)
        self.assertFalse(any(argument.startswith("-p:GamePath=") for argument in query))
        self.assertEqual("PASS", run.stages[-1]["status"])

    def test_failed_or_timed_out_command_retains_distinct_fail_and_blocked_status(self) -> None:
        for error, expected in ((None, "FAIL"), (subprocess.TimeoutExpired(["test"], 1), "BLOCKED")):
            with self.subTest(status=expected):
                run = validation.Run(self.root)
                with patch("validation.subprocess.run", side_effect=error,
                           return_value=subprocess.CompletedProcess(["test"], 7)):
                    with self.assertRaises(validation.ValidationError) as caught:
                        run.command("test", ["test"])
                self.assertEqual(expected, caught.exception.status)
                self.assertEqual(expected, run.stages[-1]["status"])
                self.assertTrue(Path(run.stages[-1]["log"]).is_file())

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
        self.assertEqual(["agent-setup", "architecture", "semantic-public-api", "python-tests"], steps)


if __name__ == "__main__":
    unittest.main()
