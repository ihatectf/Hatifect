from __future__ import annotations

import copy
import io
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import time
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
TOOLS = ROOT / "tools"
sys.path.insert(0, str(TOOLS))
import progressive_regression as regression


HEAD = "1" * 40


class ProgressiveRegressionTests(unittest.TestCase):
    def plan(self, **kwargs):
        return regression.build_plan(root=ROOT, head=HEAD, **kwargs)

    def executable_plan(self, **kwargs):
        return regression.build_plan(root=ROOT, **kwargs)

    @staticmethod
    def scopes(plan):
        return [stage["scopeId"] for stage in plan["selectedScope"]]

    def test_level_1_selects_exact_python_test_only(self) -> None:
        plan = self.executable_plan(
            exact_tests=[
                "python:tools.tests.test_progressive_regression."
                "ProgressiveRegressionTests.test_level_1_selects_exact_python_test_only"
            ]
        )

        self.assertEqual(1, plan["plannedRegressionLevel"])
        self.assertEqual(1, len(plan["selectedScope"]))
        self.assertEqual("EXACT_FAILING_TEST", plan["selectedScope"][0]["reasonCode"])
        self.assertIn("unittest", plan["selectedScope"][0]["command"])
        self.assertFalse(plan["authoritativeFullGateSelected"])
        for inexact in (
            "python:tools.tests.test_progressive_regression",
            "python:tools.tests.test_progressive_regression.ProgressiveRegressionTests",
        ):
            with self.subTest(inexact=inexact), self.assertRaises(
                regression.RegressionSelectionError
            ):
                self.plan(exact_tests=[inexact])

    def test_level_1_selects_exact_registered_dotnet_test(self) -> None:
        project = (
            "Hatifect UI/tests/Hatifect.UI.Planning.Tests/"
            "Hatifect.UI.Planning.Tests.csproj"
        )
        plan = self.plan(
            exact_tests=[f"dotnet:{project}::Example.Namespace.PlannerTests.OneCase"]
        )

        stage = plan["selectedScope"][0]
        self.assertEqual(1, plan["plannedRegressionLevel"])
        self.assertEqual(project, stage["command"][stage["command"].index("--project") + 1])
        self.assertEqual(
            "Example.Namespace.PlannerTests.OneCase",
            stage["command"][stage["command"].index("--test-filter") + 1],
        )

        platform_project = (
            "Hatifect UI/tests/Hatifect.UI.Stardew.Tests/"
            "Hatifect.UI.Stardew.Tests.csproj"
        )
        platform = self.plan(
            exact_tests=[f"dotnet:{platform_project}::Example.Tests.OneCase"]
        )
        self.assertEqual("--platform", platform["selectedScope"][0]["command"][-1])
        self.assertTrue(platform["selectedScope"][0]["platformRequired"])

    def test_level_2_selects_registered_scenario_and_canonical_runner(self) -> None:
        smoke = self.plan(scenario="runtime.boot")
        ui = self.plan(scenario="semantic.lifecycle")

        self.assertEqual(2, smoke["plannedRegressionLevel"])
        self.assertEqual(
            ["./tools/hatifect-smoke", "runtime.boot"],
            smoke["selectedScope"][0]["command"],
        )
        self.assertEqual(
            ["./tools/hatifect-ui-test", "semantic.lifecycle"],
            ui["selectedScope"][0]["command"],
        )
        self.assertTrue(smoke["selectedScope"][0]["platformRequired"])

    def test_level_3_component_mapping_selects_direct_python_test(self) -> None:
        plan = self.plan(changed_paths=["tools/live-harness/diagnostic_packet.py"])

        self.assertEqual(3, plan["plannedRegressionLevel"])
        self.assertEqual(["diagnostic-packet"], plan["components"])
        self.assertIn(
            "python-module:tools.tests.test_diagnostic_packet", self.scopes(plan)
        )
        self.assertNotIn("python-tooling", self.scopes(plan))

    def test_level_3_test_assembly_mapping_uses_solution_dependencies(self) -> None:
        plan = self.plan(
            changed_paths=["Hatifect UI/Hatifect.UI.Language/Syntax/UiLexer.cs"]
        )

        self.assertEqual(4, plan["plannedRegressionLevel"])
        direct = [stage for stage in plan["selectedScope"] if stage["level"] == 3]
        self.assertEqual(
            [
                "Hatifect.UI.DevTools.Tests",
                "Hatifect.UI.Planning.Tests",
                "Hatifect.UI.Runtime.Tests",
                "Hatifect.UI.Semantics.Tests",
                "Hatifect.UI.Stardew.Tests",
                "Hatifect.UI.Tooling.Server.Tests",
                "Hatifect.UI.Tooling.Tests",
            ],
            [Path(stage["scopeId"].removeprefix("dotnet-project:")).parent.name for stage in direct],
        )
        self.assertTrue(
            next(stage for stage in direct if "Stardew.Tests" in stage["scopeId"])[
                "platformRequired"
            ]
        )
        self.assertEqual(
            ["ca-platform", "flow-host-free"],
            [stage["scopeId"] for stage in plan["selectedScope"] if stage["level"] == 4],
        )

    def test_level_4_cross_component_change_selects_relevant_integration(self) -> None:
        plan = self.plan(
            changed_paths=[
                "tools/live-harness/diagnostic_packet.py",
                "tools/live-harness/reproduction.py",
            ]
        )

        self.assertEqual(4, plan["plannedRegressionLevel"])
        self.assertIn("python-tooling", self.scopes(plan))
        self.assertEqual(
            ["diagnostic-packet", "reproduction-planner"], plan["components"]
        )
        self.assertIn(
            "python-module:tools.tests.test_diagnostic_packet", self.scopes(plan)
        )
        self.assertIn("python-module:tools.tests.test_reproduction", self.scopes(plan))
        self.assertNotIn("full-host-free", self.scopes(plan))
        self.assertIn("CROSS_COMPONENT_CHANGE", {item["code"] for item in plan["selectionReason"]})

    def test_level_5_multi_area_change_preserves_full_gate(self) -> None:
        plan = self.plan(
            changed_paths=[
                "tools/live-harness/diagnostic_packet.py",
                "Hatifect UI/Hatifect.UI.Language/Syntax/UiLexer.cs",
            ]
        )

        self.assertEqual(5, plan["plannedRegressionLevel"])
        self.assertEqual(["./tools/hatifect-check"], plan["selectedScope"][-1]["command"])
        self.assertTrue(plan["authoritativeFullGateSelected"])
        self.assertIn("MULTI_AREA_CHANGE", {item["code"] for item in plan["selectionReason"]})

    def test_unknown_change_falls_back_to_full_regression(self) -> None:
        plan = self.plan(changed_paths=["unmapped/new-owner.contract"])

        self.assertEqual(5, plan["plannedRegressionLevel"])
        self.assertEqual(["full-host-free"], self.scopes(plan))
        self.assertIn("UNKNOWN_CHANGE", {item["code"] for item in plan["selectionReason"]})

    def test_equal_specificity_ambiguity_falls_back_to_full_regression(self) -> None:
        mapping = json.loads(regression.MAPPING_PATH.read_text(encoding="utf-8"))
        mapping["changeRules"].append(
            {
                "pattern": "tools/progressive_regression.py",
                "component": "tooling",
                "integrationArea": "tooling",
                "forceLevel": 4,
            }
        )
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "mapping.json"
            path.write_text(json.dumps(mapping), encoding="utf-8")
            plan = self.plan(
                changed_paths=["tools/progressive_regression.py"], mapping_path=path
            )

        self.assertEqual(5, plan["plannedRegressionLevel"])
        self.assertIn("AMBIGUOUS_CHANGE", {item["code"] for item in plan["selectionReason"]})

    def test_ci_release_authority_and_explicit_final_both_select_level_5(self) -> None:
        ci = self.plan(changed_paths=[".github/workflows/ci-static.yml"])
        final = self.plan(
            changed_paths=["tools/progressive_regression.py"], final=True
        )

        self.assertEqual(5, ci["plannedRegressionLevel"])
        self.assertIn("CI_RELEASE_AUTHORITY", {item["code"] for item in ci["selectionReason"]})
        self.assertEqual(5, final["plannedRegressionLevel"])
        self.assertIn("AUTHORITATIVE_FINAL_GATE", {item["code"] for item in final["selectionReason"]})

    def test_component_and_scenario_context_progress_through_levels_1_to_3(self) -> None:
        exact = (
            "python:tools.tests.test_live_harness."
            "HarnessContractTests.test_manifest_and_runner_contract"
        )
        plan = self.plan(
            exact_tests=[exact],
            scenario="runtime.boot",
            changed_paths=[
                "Hatifect UI/tests/Hatifect.UI.Semantics.Tests/LanguageTests.cs"
            ],
        )

        self.assertEqual(3, plan["plannedRegressionLevel"])
        self.assertEqual([1, 2, 3], sorted({stage["level"] for stage in plan["selectedScope"]}))
        self.assertEqual(["semantic-ui"], plan["components"])
        self.assertIn("python-module:tools.tests.test_live_harness", self.scopes(plan))
        self.assertEqual(
            [(0, 1), (1, 2), (2, 3)],
            [
                (item["fromLevel"], item["toLevel"])
                for item in plan["plannedEscalation"]
            ],
        )
        self.assertEqual([], plan["escalationTrigger"])

    def test_selection_reason_and_omitted_scopes_are_byte_stable(self) -> None:
        first = self.plan(changed_paths=["tools/progressive_regression.py"])
        second = self.plan(changed_paths=["tools/progressive_regression.py"])

        self.assertEqual(regression._canonical_bytes(first), regression._canonical_bytes(second))
        self.assertIn(
            {
                "scopeId": "full-host-free",
                "kind": "authoritative-gate",
                "platformRequired": False,
                "reason": "LOCAL_ITERATION_NOT_FINAL",
            },
            first["testsOmittedByScope"],
        )
        validation_scope = next(
            item
            for item in first["testsOmittedByScope"]
            if item["scopeId"] == "python-module:tools.tests.test_validation"
        )
        self.assertEqual("OWNING_AREA_NOT_REACHED", validation_scope["reason"])
        self.assertEqual(
            first["testInventory"]["scopeCount"] - 1,
            first["testInventory"]["omittedScopeCount"],
        )
        self.assertTrue(first["testInventory"]["authoritativeGateOmitted"])
        changed = self.plan(changed_paths=["tools/live-harness/reproduction.py"])
        self.assertNotEqual(first["selectionFingerprint"], changed["selectionFingerprint"])

    def test_aggregate_scenario_falls_back_to_full_regression(self) -> None:
        plan = self.plan(scenario="all")

        self.assertEqual(5, plan["plannedRegressionLevel"])
        self.assertIn("AGGREGATE_SCENARIO", {item["code"] for item in plan["selectionReason"]})
        self.assertEqual("full-host-free", plan["selectedScope"][-1]["scopeId"])

    def test_execution_records_passed_scopes_and_reaches_planned_level(self) -> None:
        plan = self.executable_plan(
            changed_paths=[
                "tools/live-harness/diagnostic_packet.py",
                "tools/live-harness/reproduction.py",
            ]
        )
        calls = []

        def execute(command, _root, _environment):
            calls.append(command)
            return 0, "Ran 3 tests\nOK"

        report = regression.execute_plan(
            plan, root=ROOT, executor=execute, verify_candidate=False
        )

        self.assertEqual("PASS", report["result"])
        self.assertEqual(4, report["finalRegressionLevel"])
        self.assertEqual(len(plan["selectedScope"]), len(calls))
        self.assertTrue(all(item["status"] == "PASS" for item in report["testsExecuted"]))
        self.assertEqual(3 * len(calls), report["executedTestCount"])
        self.assertTrue(report["testCountComplete"])
        self.assertFalse(report["authoritativeFullGatePassed"])

    def test_execution_stops_at_first_failure_without_broad_rerun(self) -> None:
        plan = self.executable_plan(
            changed_paths=["tools/progressive_regression.py"], final=True
        )
        calls = []

        def execute(command, _root, _environment):
            calls.append(command)
            return 1, "Ran 1 test in 0.1s\nFAILED (failures=1)"

        report = regression.execute_plan(
            plan, root=ROOT, executor=execute, verify_candidate=False
        )

        self.assertEqual("FAIL", report["result"])
        self.assertEqual(1, len(calls))
        self.assertEqual(3, report["finalRegressionLevel"])
        self.assertEqual("TEST_FAILURE_STOP", report["executionStop"]["code"])
        self.assertIn(
            {"level": 5, "scopeId": "full-host-free", "reason": "EARLIER_STAGE_FAIL"},
            report["plannedScopesNotExecuted"],
        )
        self.assertEqual(1, report["executedTestCount"])
        self.assertTrue(report["testCountComplete"])
        self.assertEqual("VALID", report["testsExecuted"][0]["evidenceStatus"])
        self.assertFalse(report["authoritativeFullGatePassed"])

    def test_execution_classifies_blocked_prerequisite(self) -> None:
        plan = self.executable_plan(scenario="runtime.boot")
        report = regression.execute_plan(
            plan,
            root=ROOT,
            executor=lambda *_: (2, "SMAPI prerequisite unavailable"),
            verify_candidate=False,
        )

        self.assertEqual("BLOCKED", report["result"])
        self.assertEqual("EXECUTION_BLOCKED", report["executionStop"]["code"])
        self.assertIn("SMAPI prerequisite unavailable", report["testsExecuted"][0]["outputTail"])

        unavailable = regression.execute_plan(
            plan,
            root=ROOT,
            executor=mock.Mock(side_effect=OSError("executor unavailable")),
            verify_candidate=False,
        )
        self.assertEqual("BLOCKED", unavailable["result"])
        self.assertEqual("EXECUTION_BLOCKED", unavailable["executionStop"]["code"])

    def test_successful_scenario_records_scenario_without_test_count(self) -> None:
        plan = self.executable_plan(scenario="runtime.boot")
        report = regression.execute_plan(
            plan,
            root=ROOT,
            executor=lambda *_: (0, "canonical scenario completed"),
            verify_candidate=False,
        )

        executed = report["testsExecuted"][0]
        self.assertEqual("PASS", report["result"])
        self.assertEqual(1, executed["scenarioCount"])
        self.assertIsNone(executed["testCount"])
        self.assertEqual("NOT_APPLICABLE", executed["evidenceStatus"])
        self.assertTrue(report["testCountComplete"])

    def test_parsed_test_count_accepts_all_canonical_summary_formats(self) -> None:
        self.assertEqual(1, regression._parsed_test_count("Ran 1 test in 0.1s\nOK"))
        self.assertEqual(
            5,
            regression._parsed_test_count(
                "Python tests: 5; failures: 0; skipped: 0\n"
            ),
        )
        self.assertEqual(
            5,
            regression._parsed_test_count(
                "Flow.Tests: Passed: 2, Failed: 0, Skipped: 0\n"
                "Runtime.Tests: Passed: 3, Failed: 0, Skipped: 0\n"
            ),
        )
        self.assertEqual(
            7,
            regression._parsed_test_count(
                "Python tests: 2; failures: 0; skipped: 0\n"
                "Runtime.Tests: Passed: 5, Failed: 0, Skipped: 0\n"
            ),
        )

    def test_successful_exit_rejects_omitted_test_outcomes(self) -> None:
        plan = self.executable_plan(changed_paths=["tools/progressive_regression.py"])
        outputs = (
            "Ran 1 test in 0.1s\n\nOK (skipped=1)",
            "Ran 1 test in 0.1s\n\nOK (expected failures=1)",
            "Ran 1 test in 0.1s\n\nOK (unexpected successes=1)",
            "Ran 1 test in 0.1s\n\nFAILED (failures=1)",
            "OK\nRan 1 test in 0.1s\n\nFAILED (failures=1)",
            (
                "Ran 1 test in 0.1s\nFAILED (failures=1)\n"
                "Python tests: 1; failures: 0; skipped: 0\n"
            ),
            (
                "Ran 2 tests in 0.1s\nOK\n"
                "Python tests: 1; failures: 0; skipped: 0\n"
            ),
            "Python tests: 1; failures: 0; skipped: 1\n",
            "Python tests: 1; failures: 1; skipped: 0\n",
            "Runtime.Tests: Passed: 1, Failed: 0, Skipped: 1\n",
            "Runtime.Tests: Passed: 1, Failed: 1, Skipped: 0\n",
        )
        for output in outputs:
            with self.subTest(output=output):
                report = regression.execute_plan(
                    plan,
                    root=ROOT,
                    executor=lambda *_: (0, output),
                    verify_candidate=False,
                )
                executed = report["testsExecuted"][0]
                self.assertEqual("FAIL", report["result"])
                self.assertEqual("TEST_EVIDENCE_MISSING", report["executionStop"]["code"])
                self.assertEqual(1, executed["testCount"])
                self.assertEqual("PARTIAL", executed["evidenceStatus"])
                self.assertFalse(report["testCountComplete"])

    def test_report_publication_is_private_atomic_and_bounded(self) -> None:
        plan = self.executable_plan(changed_paths=["tools/progressive_regression.py"])
        report = regression.execute_plan(
            plan,
            root=ROOT,
            executor=lambda *_: (0, "Ran 1 test\nOK"),
            verify_candidate=False,
        )
        with tempfile.TemporaryDirectory() as directory:
            path = regression.publish_report(report, Path(directory))
            payload = path.read_bytes()

            self.assertEqual(report, json.loads(payload))
            self.assertLessEqual(len(payload), regression.MAX_REPORT_BYTES)
            self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            self.assertFalse((path.parent / ".regression-report.tmp").exists())

    def test_execution_rejects_stale_tampered_or_zero_test_plan(self) -> None:
        plan = self.executable_plan(changed_paths=["tools/progressive_regression.py"])
        executor = mock.Mock(return_value=(0, "Ran 1 test\nOK"))
        for mutation in ("fingerprint", "command", "valid-command", "metadata", "head"):
            candidate = copy.deepcopy(plan)
            if mutation == "fingerprint":
                candidate["selectionFingerprint"] = "0" * 64
            elif mutation == "command":
                candidate["selectedScope"][0]["command"] = ["python3", "arbitrary.py"]
                candidate["selectionFingerprint"] = regression._selection_fingerprint(candidate)
            elif mutation == "valid-command":
                candidate["selectedScope"][0]["command"] = [
                    "python3", "-m", "unittest", "tools.tests.test_validation", "-q"
                ]
                candidate["selectionFingerprint"] = regression._selection_fingerprint(candidate)
            elif mutation == "metadata":
                candidate["selectedScope"][0]["scopeId"] = "python-tooling"
                candidate["selectionFingerprint"] = regression._selection_fingerprint(candidate)
            else:
                candidate["repositoryHead"] = "0" * 40
                candidate["selectionFingerprint"] = regression._selection_fingerprint(candidate)
            with self.subTest(mutation=mutation), self.assertRaises(
                regression.RegressionSelectionError
            ):
                regression.execute_plan(
                    candidate,
                    root=ROOT,
                    executor=executor,
                    verify_candidate=False,
                )
        executor.assert_not_called()

        zero = regression.execute_plan(
            plan,
            root=ROOT,
            executor=lambda *_: (0, "Ran 0 tests\nOK"),
            verify_candidate=False,
        )
        self.assertEqual("FAIL", zero["result"])
        self.assertEqual("TEST_EVIDENCE_MISSING", zero["executionStop"]["code"])
        self.assertFalse(zero["testCountComplete"])

        multi = self.executable_plan(
            changed_paths=["tools/progressive_regression.py"], final=True
        )
        multi["selectedScope"][1]["command"] = ["./tools/hatifect-test", "tools"]
        multi["selectionFingerprint"] = regression._selection_fingerprint(multi)
        late_executor = mock.Mock(return_value=(0, "Ran 1 test\nOK"))
        with self.assertRaises(regression.RegressionSelectionError):
            regression.execute_plan(
                multi,
                root=ROOT,
                executor=late_executor,
                verify_candidate=False,
            )
        late_executor.assert_not_called()

    def test_execution_bounds_output_tail_and_records_report_contract(self) -> None:
        plan = self.executable_plan(changed_paths=["tools/progressive_regression.py"])
        output = "Ran 2 tests\n" + "\n".join(
            [f"line-{index}" for index in range(13)] + ["x" * 900]
        )
        report = regression.execute_plan(
            plan,
            root=ROOT,
            executor=lambda *_: (0, output),
            verify_candidate=False,
        )

        expected_plan_keys = {
            "formatVersion", "repositoryHead", "worktreePaths", "changedPaths",
            "exactTests", "finalRequested", "scenario", "scenarioKind",
            "components", "integrationAreas", "selectionReason",
            "selectedScope", "testsExecuted", "testsOmittedByScope",
            "plannedScopesNotExecuted", "plannedEscalation", "escalationTrigger",
            "executionStop", "testInventory", "plannedRegressionLevel",
            "finalRegressionLevel", "authoritativeFullGateSelected",
            "selectionFingerprint",
        }
        self.assertEqual(expected_plan_keys, set(plan))
        tail = report["testsExecuted"][0]["outputTail"]
        self.assertEqual(12, len(tail))
        self.assertEqual("line-2", tail[0])
        self.assertEqual(regression.MAX_TEXT, len(tail[-1]))
        self.assertTrue(tail[-1].endswith("..."))
        self.assertLessEqual(len(regression._canonical_bytes(report)), regression.MAX_REPORT_BYTES)

    def test_real_executor_retains_only_bounded_stdout_tail(self) -> None:
        exit_code, captured = regression._execute(
            [
                sys.executable,
                "-c",
                "print('BEGIN-' + 'x' * 70000); print('THE-END')",
            ],
            ROOT,
            dict(os.environ),
        )

        self.assertEqual(0, exit_code)
        self.assertLessEqual(len(captured.encode("utf-8")), regression.MAX_CAPTURE_BYTES)
        self.assertNotIn("BEGIN-", captured)
        self.assertTrue(captured.rstrip().endswith("THE-END"))

    def test_execution_rechecks_worktree_candidate_before_first_command(self) -> None:
        with mock.patch.object(
            regression,
            "_worktree_changes",
            return_value=["tools/progressive_regression.py"],
        ):
            plan = regression.build_plan(
                root=ROOT,
                changed_paths=["tools/progressive_regression.py"],
                discover_worktree=True,
            )
        executor = mock.Mock(return_value=(0, "Ran 1 test\nOK"))
        with mock.patch.object(
            regression,
            "_worktree_changes",
            return_value=["tools/progressive_regression.py", "tools/validation.py"],
        ), self.assertRaisesRegex(
            regression.RegressionSelectionError, "worktree changed"
        ):
            regression.execute_plan(plan, root=ROOT, executor=executor)
        executor.assert_not_called()

    def test_invalid_paths_tests_and_mapping_fail_closed(self) -> None:
        for kwargs in (
            {"changed_paths": ["../escape.py"]},
            {"exact_tests": ["python:arbitrary.module.test"]},
            {"scenario": "unknown.scenario"},
        ):
            with self.subTest(kwargs=kwargs), self.assertRaises(regression.RegressionSelectionError):
                self.plan(**kwargs)

        mapping = json.loads(regression.MAPPING_PATH.read_text(encoding="utf-8"))
        mapping["fullGate"]["command"] = ["./tools/hatifect-test", "tools"]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "mapping.json"
            path.write_text(json.dumps(mapping), encoding="utf-8")
            with self.assertRaisesRegex(regression.RegressionSelectionError, "Level 5"):
                self.plan(changed_paths=["tools/progressive_regression.py"], mapping_path=path)

        mapping = json.loads(regression.MAPPING_PATH.read_text(encoding="utf-8"))
        mapping["components"]["diagnostic-packet"]["directTestFiles"] = [
            "tools/tests/test_missing.py"
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "mapping.json"
            path.write_text(json.dumps(mapping), encoding="utf-8")
            with self.assertRaisesRegex(regression.RegressionSelectionError, "missing or unsafe"):
                self.plan(changed_paths=["tools/progressive_regression.py"], mapping_path=path)

    def test_worktree_discovery_prevents_incomplete_changed_manifest(self) -> None:
        with mock.patch.object(
            regression,
            "_worktree_changes",
            return_value=["tools/progressive_regression.py", "tools/validation.py"],
        ):
            plan = regression.build_plan(
                root=ROOT,
                head=HEAD,
                changed_paths=["tools/progressive_regression.py"],
                discover_worktree=True,
            )

        self.assertEqual(5, plan["plannedRegressionLevel"])
        self.assertEqual(
            ["tools/progressive_regression.py", "tools/validation.py"],
            plan["changedPaths"],
        )
        self.assertIn(
            "WORKTREE_CHANGE_DISCOVERED", {item["code"] for item in plan["selectionReason"]}
        )

    def test_cli_plan_is_read_only_and_emits_the_same_contract(self) -> None:
        payload = io.BytesIO()
        output = io.TextIOWrapper(payload, encoding="utf-8")
        with mock.patch.object(sys, "stdout", output), mock.patch.object(
            regression,
            "_worktree_changes",
            return_value=["tools/progressive_regression.py"],
        ):
            exit_code = regression.main(
                ["plan", "--changed", "tools/progressive_regression.py"]
            )
        output.flush()
        document = json.loads(payload.getvalue())
        output.detach()

        self.assertEqual(0, exit_code)
        self.assertEqual(3, document["plannedRegressionLevel"])
        self.assertEqual(0, document["finalRegressionLevel"])
        self.assertEqual([], document["testsExecuted"])
        self.assertFalse(document["authoritativeFullGateSelected"])

    def test_full_if_clean_selects_full_gate_without_weakening_empty_context_guard(self) -> None:
        with mock.patch.object(regression, "_worktree_changes", return_value=[]):
            plan = self.plan(discover_worktree=True, full_if_clean=True)
            with self.assertRaisesRegex(regression.RegressionSelectionError, "provide a changed path"):
                self.plan(discover_worktree=True)
        self.assertEqual(["full-host-free"], self.scopes(plan))
        self.assertTrue(plan["authoritativeFullGateSelected"])
        self.assertEqual([], plan["worktreePaths"])

    def test_full_if_clean_preserves_changed_and_exact_context(self) -> None:
        for changes, context, level in (
            (["tools/progressive_regression.py"], {}, 3),
            ([], {"changed_paths": ["tools/progressive_regression.py"]}, 3),
            ([], {"scenario": "runtime.boot"}, 2),
            ([], {"exact_tests": ["python:tools.tests.test_progressive_regression."
                                  "ProgressiveRegressionTests.test_level_1_selects_exact_python_test_only"]}, 1),
        ):
            with self.subTest(context=context, changes=changes), mock.patch.object(
                regression, "_worktree_changes", return_value=changes
            ):
                plan = self.plan(discover_worktree=True, full_if_clean=True, **context)
            self.assertEqual(level, plan["plannedRegressionLevel"])
            self.assertFalse(plan["authoritativeFullGateSelected"])

    def test_cli_full_if_clean_selects_without_reading_agent_instructions(self) -> None:
        original_open = Path.open

        def open_without_agent_files(path, *args, **kwargs):
            if {"AGENTS.md", ".agents", ".codex"}.intersection(path.parts):
                self.fail(f"selection tried to read agent configuration: {path}")
            return original_open(path, *args, **kwargs)

        payload = io.BytesIO()
        output = io.TextIOWrapper(payload, encoding="utf-8")
        with mock.patch.object(Path, "open", open_without_agent_files), \
                mock.patch.object(sys, "stdout", output), \
                mock.patch.object(regression, "_worktree_changes", return_value=[]):
            self.assertEqual(0, regression.main(["plan", "--full-if-clean"]))
        output.flush()
        document = json.loads(payload.getvalue())
        output.detach()
        self.assertEqual(["full-host-free"], self.scopes(document))
        self.assertTrue(document["authoritativeFullGateSelected"])

    def test_cli_run_publishes_report_and_preserves_result_exit_codes(self) -> None:
        for status, expected in (("PASS", 0), ("FAIL", 1), ("BLOCKED", 2)):
            plan = {"plan": status}
            report = {"result": status, "finalRegressionLevel": 3}
            results = Path("artifacts/custom-regression")
            with self.subTest(status=status), mock.patch.object(
                regression, "build_plan", return_value=plan
            ) as build, mock.patch.object(
                regression, "execute_plan", return_value=report
            ) as execute, mock.patch.object(
                regression,
                "publish_report",
                return_value=Path("artifacts/validation/run/regression-report.json"),
            ) as publish, mock.patch.object(sys, "stdout", io.StringIO()):
                actual = regression.main(
                    [
                        "run", "--changed", "tools/progressive_regression.py",
                        "--results-directory", str(results),
                    ]
                )

            self.assertEqual(expected, actual)
            self.assertTrue(build.call_args.kwargs["discover_worktree"])
            execute.assert_called_once_with(plan)
            publish.assert_called_once_with(report, results)

    def test_real_executor_times_out_and_terminates_child_process_group(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "leaked-child.txt"
            ready = Path(directory) / "child-ready.txt"
            child = (
                "import os,pathlib,signal,sys,time; "
                "signal.signal(signal.SIGTERM, signal.SIG_IGN); "
                "os.close(1); os.close(2); "
                "pathlib.Path(sys.argv[2]).write_text('ready', encoding='utf-8'); "
                "time.sleep(0.5); "
                "pathlib.Path(sys.argv[1]).write_text('leaked', encoding='utf-8')"
            )
            parent = (
                "import subprocess,sys,time; "
                "subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2], sys.argv[3]]); "
                "deadline=time.monotonic()+2; "
                "exec(\"while not __import__('pathlib').Path(sys.argv[3]).exists():\\n"
                " if time.monotonic() >= deadline: raise RuntimeError('child not ready')\\n"
                " time.sleep(0.005)\"); "
                "print('started', flush=True); time.sleep(10)"
            )
            started = time.monotonic()

            exit_code, output = regression._execute(
                [sys.executable, "-c", parent, child, str(marker), str(ready)],
                ROOT,
                os.environ.copy(),
                timeout_seconds=0.2,
            )
            elapsed = time.monotonic() - started
            time.sleep(0.7)

            self.assertEqual(2, exit_code)
            self.assertLess(elapsed, 2)
            self.assertIn("started", output)
            self.assertIn("HATIFECT_REGRESSION_TIMEOUT", output)
            self.assertTrue(ready.exists())
            self.assertFalse(marker.exists())

    def test_real_executor_preserves_child_signal_mask_and_sigint_delivery(self) -> None:
        original_mask = regression.signal.pthread_sigmask(regression.signal.SIG_BLOCK, set())
        child = (
            "import json,os,signal; "
            "print(json.dumps(sorted(int(s) for s in signal.pthread_sigmask(signal.SIG_BLOCK, set()))), flush=True); "
            "signal.signal(signal.SIGINT, lambda *_: print('interrupt delivered', flush=True)); "
            "os.kill(os.getpid(), signal.SIGINT)"
        )
        try:
            for mask in (set(), {regression.signal.SIGUSR1}):
                with self.subTest(mask=mask):
                    regression.signal.pthread_sigmask(regression.signal.SIG_SETMASK, mask)
                    exit_code, output = regression._execute(
                        [sys.executable, "-c", child], ROOT, os.environ.copy(), timeout_seconds=5
                    )
                    self.assertEqual(0, exit_code, output)
                    self.assertEqual(
                        [json.dumps(sorted(int(s) for s in mask)), "interrupt delivered"],
                        output.splitlines(),
                    )
                    self.assertEqual(mask, regression.signal.pthread_sigmask(regression.signal.SIG_BLOCK, set()))
        finally:
            regression.signal.pthread_sigmask(regression.signal.SIG_SETMASK, original_mask)

    def test_real_executor_missing_command_is_blocked(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            exit_code, output = regression._execute(
                [str(Path(directory) / "missing-runner")], ROOT, os.environ.copy(), timeout_seconds=5
            )
        self.assertEqual(2, exit_code)
        self.assertIn("HATIFECT_REGRESSION_EXEC_BLOCKED", output)

    def test_real_executor_interrupt_terminates_and_reaps_child_process_group(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "leaked-child.txt"
            ready = Path(directory) / "child-ready.txt"
            child = (
                "import pathlib,signal,sys,time; "
                "signal.signal(signal.SIGTERM, signal.SIG_IGN); "
                "pathlib.Path(sys.argv[2]).write_text('ready', encoding='utf-8'); "
                "time.sleep(0.5); "
                "pathlib.Path(sys.argv[1]).write_text('leaked', encoding='utf-8')"
            )
            parent = (
                "import subprocess,sys,time; "
                "subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2], sys.argv[3]]); "
                "time.sleep(10)"
            )
            real_popen = regression.subprocess.Popen
            spawned = []

            def interrupted_popen(*args, **kwargs):
                process = real_popen(*args, **kwargs)
                spawned.append(process)
                original_wait = process.wait
                interrupted = False

                def wait(*, timeout=None):
                    nonlocal interrupted
                    if not interrupted:
                        deadline = time.monotonic() + 2
                        while not ready.exists() and time.monotonic() < deadline:
                            time.sleep(0.005)
                        interrupted = True
                        raise KeyboardInterrupt
                    return original_wait(timeout=timeout)

                process.wait = wait
                return process

            with mock.patch.object(
                regression.subprocess, "Popen", side_effect=interrupted_popen
            ), self.assertRaises(KeyboardInterrupt):
                regression._execute(
                    [sys.executable, "-c", parent, child, str(marker), str(ready)],
                    ROOT,
                    os.environ.copy(),
                    timeout_seconds=10,
                )

            time.sleep(0.7)
            self.assertTrue(ready.exists())
            self.assertFalse(marker.exists())
            self.assertIsNotNone(spawned[0].poll())
            self.assertFalse(regression._process_group_exists(spawned[0].pid))

    def test_real_executor_interrupt_before_reader_start_reaps_spawned_process(self) -> None:
        spawned = []
        real_popen = regression.subprocess.Popen

        def capturing_popen(*args, **kwargs):
            process = real_popen(*args, **kwargs)
            spawned.append(process)
            return process

        with mock.patch.object(
            regression.subprocess, "Popen", side_effect=capturing_popen
        ), mock.patch.object(
            regression.threading.Thread, "start", side_effect=KeyboardInterrupt
        ), self.assertRaises(KeyboardInterrupt):
            regression._execute(
                [sys.executable, "-c", "import time; time.sleep(10)"],
                ROOT,
                os.environ.copy(),
                timeout_seconds=10,
            )

        self.assertEqual(1, len(spawned))
        self.assertIsNotNone(spawned[0].poll())
        self.assertFalse(regression._process_group_exists(spawned[0].pid))

    def test_real_executor_memory_error_immediately_after_spawn_reaps_process_group(self) -> None:
        spawned = []
        real_popen = regression.subprocess.Popen

        def capturing_popen(*args, **kwargs):
            process = real_popen(*args, **kwargs)
            spawned.append(process)
            return process

        with mock.patch.object(
            regression.subprocess, "Popen", side_effect=capturing_popen
        ), mock.patch.object(
            regression, "bytearray", side_effect=MemoryError, create=True
        ), self.assertRaises(MemoryError):
            regression._execute(
                [sys.executable, "-c", "import time; time.sleep(10)"],
                ROOT,
                os.environ.copy(),
                timeout_seconds=10,
            )

        self.assertEqual(1, len(spawned))
        self.assertIsNotNone(spawned[0].poll())
        self.assertFalse(regression._process_group_exists(spawned[0].pid))

    def test_real_executor_memory_error_on_first_post_spawn_access_cleans_lifetime(self) -> None:
        spawned = []
        wrapped = []
        readers = []
        real_popen = regression.subprocess.Popen
        real_thread = regression.threading.Thread
        original_signal_mask = regression.signal.pthread_sigmask(
            regression.signal.SIG_BLOCK, set()
        )

        class FailFirstPidAccess:
            def __init__(self, process):
                self.process = process
                self.failure = MemoryError("first post-Popen pid access")
                self.pid_accesses = 0

            @property
            def pid(self):
                self.pid_accesses += 1
                if self.pid_accesses == 1:
                    raise self.failure
                return self.process.pid

            def __getattr__(self, name):
                return getattr(self.process, name)

        def capturing_popen(*args, **kwargs):
            process = real_popen(*args, **kwargs)
            proxy = FailFirstPidAccess(process)
            spawned.append(process)
            wrapped.append(proxy)
            return proxy

        def capturing_thread(*args, **kwargs):
            reader = real_thread(*args, **kwargs)
            readers.append(reader)
            return reader

        try:
            with mock.patch.object(
                regression.subprocess, "Popen", side_effect=capturing_popen
            ), mock.patch.object(
                regression.threading, "Thread", side_effect=capturing_thread
            ), self.assertRaises(MemoryError) as raised:
                regression._execute(
                    [sys.executable, "-c", "import time; time.sleep(10)"],
                    ROOT,
                    os.environ.copy(),
                    timeout_seconds=10,
                )

            self.assertEqual(1, len(spawned))
            self.assertIs(wrapped[0].failure, raised.exception)
            self.assertIsNotNone(spawned[0].poll())
            self.assertFalse(regression._process_group_exists(spawned[0].pid))
            self.assertTrue(spawned[0].stdout.closed)
            self.assertFalse(any(reader.is_alive() for reader in readers))
            self.assertEqual(
                original_signal_mask,
                regression.signal.pthread_sigmask(regression.signal.SIG_BLOCK, set()),
            )
        finally:
            for process in spawned:
                if process.poll() is None:
                    try:
                        os.killpg(process.pid, regression.signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    process.wait()
                if process.stdout is not None and not process.stdout.closed:
                    process.stdout.close()
            regression.signal.pthread_sigmask(
                regression.signal.SIG_SETMASK, original_signal_mask
            )

    def test_real_executor_memory_error_during_reader_join_reaps_process_group(self) -> None:
        spawned = []
        real_popen = regression.subprocess.Popen
        real_join = regression.threading.Thread.join
        raised = False
        joined_threads = []

        def capturing_popen(*args, **kwargs):
            process = real_popen(*args, **kwargs)
            spawned.append(process)
            return process

        def interrupted_join(thread, timeout=None):
            nonlocal raised
            joined_threads.append(thread)
            if not raised:
                raised = True
                raise MemoryError
            return real_join(thread, timeout=timeout)

        with mock.patch.object(
            regression.subprocess, "Popen", side_effect=capturing_popen
        ), mock.patch.object(
            regression.threading.Thread, "join", new=interrupted_join
        ), self.assertRaises(MemoryError):
            regression._execute(
                [sys.executable, "-c", "import time; time.sleep(0.05)"],
                ROOT,
                os.environ.copy(),
                timeout_seconds=10,
            )

        self.assertTrue(raised)
        self.assertEqual(1, len(spawned))
        self.assertIsNotNone(spawned[0].poll())
        self.assertFalse(regression._process_group_exists(spawned[0].pid))
        self.assertTrue(joined_threads)
        self.assertFalse(joined_threads[0].is_alive())

    def test_real_executor_fails_closed_without_process_group_support(self) -> None:
        with mock.patch.object(regression.os, "name", "nt"), mock.patch.object(
            regression.subprocess, "Popen"
        ) as spawn:
            exit_code, output = regression._execute(
                [sys.executable, "-c", "raise SystemExit(0)"],
                ROOT,
                os.environ.copy(),
            )

        self.assertEqual(2, exit_code)
        self.assertEqual("HATIFECT_REGRESSION_PROCESS_GROUP_UNSUPPORTED\n", output)
        spawn.assert_not_called()


if __name__ == "__main__":
    unittest.main()
