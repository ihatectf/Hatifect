import datetime as dt
import base64
import copy
import importlib.util
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR_PATH = ROOT / "tools" / "live-harness" / "validate.py"
SPEC = importlib.util.spec_from_file_location("hatifect_live_harness", VALIDATOR_PATH)
assert SPEC is not None and SPEC.loader is not None
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)


class LiveHarnessTests(unittest.TestCase):
    def setUp(self) -> None:
        self.scenarios = HARNESS.load_manifest()

    def test_manifest_resolves_all_ui_requirements_deterministically(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "all", "ui")
        expected_all_includes = [
            scenario_id
            for scenario_id, scenario in self.scenarios.items()
            if scenario_id != "all"
            and scenario["kind"] == "ui"
            and scenario.get("includeInAll", True)
        ]
        expected_checks = [
            check_id
            for scenario_id in expected_all_includes
            for check_id in self.scenarios[scenario_id].get("checks", [])
        ]

        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(self.scenarios["all"]["includes"], expected_all_includes)
        self.assertEqual(resolved["checks"], expected_checks)
        self.assertEqual(resolved["performance"]["scenarioId"], "semantic.performance")

    def test_all_checks_have_exactly_one_named_owner(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "all", "ui")
        all_scenario = self.scenarios["all"]
        driver = (
            ROOT
            / "Hatifect UI"
            / "Hatifect.UI.Stardew"
            / "Diagnostics"
            / "UiAutomatedAcceptanceController.cs"
        ).read_text(encoding="utf-8")

        self.assertEqual(all_scenario.get("checks", []), [])
        self.assertIn("BuiltInScenarioRegistry", driver)
        self.assertIn("private static AcceptanceScenarioCatalog BuildScenarioCatalog", driver)
        self.assertIn("if (!checkOwners.TryAdd(check, scenario.Id))", driver)
        self.assertIn("allChecks.AddRange(scenario.Checks);", driver)
        self.assertIn("scenario.Kind == AcceptanceScenarioKind.Ui", driver)
        self.assertIn("contributions.Descriptors", driver)
        self.assertIn("scenario.Contribution.Execute(context);", driver)
        self.assertIn("ExecuteAllNamedUiScenarios()", driver)
        self.assertIn("ValidateAllExecution(allScenarios);", driver)
        self.assertIn("ScenarioCatalog.IsDeclaredCheck(_scenario, checkId)", driver)
        self.assertIn("produced undeclared check", driver)
        self.assertIn("contribution.IncludeInAggregate", driver)
        self.assertIn("if (!contribution.IncludeInAggregate) continue;", driver)
        self.assertNotIn('case "all":', driver)
        self.assertNotIn('["all"] = new[]', driver)
        owners: dict[str, str] = {}
        for scenario_id in all_scenario["includes"]:
            for check_id in self.scenarios[scenario_id].get("checks", []):
                self.assertNotIn(check_id, owners)
                owners[check_id] = scenario_id
        self.assertEqual(list(owners), resolved["checks"])
        self.assertEqual(len(owners), len(resolved["checks"]))
        declared_checks = [
            check_id
            for scenario_id, scenario in self.scenarios.items()
            if scenario_id != "all"
            for check_id in scenario.get("checks", [])
        ]
        built_in_checks = re.findall(r'Record\(\s*"([^"]+)"', driver)
        self.assertTrue(set(built_in_checks).issubset(set(declared_checks)))
        self.assertEqual(len(built_in_checks), len(set(built_in_checks)))

    def test_resolving_conflicting_named_check_ids_fails_closed(self) -> None:
        scenarios = {
            "all": self._scenario("all", includes=["semantic.one", "semantic.two"]),
            "semantic.one": self._scenario("semantic.one", checks=["semantic.shared"]),
            "semantic.two": self._scenario("semantic.two", checks=["semantic.shared"]),
        }

        with self.assertRaisesRegex(
            HARNESS.HarnessError,
            "resolves duplicate check 'semantic.shared' from 'semantic.one' and 'semantic.two'",
        ):
            HARNESS.resolve_scenario(scenarios, "all", "ui")

    def test_manifest_rejects_all_with_direct_orphan_check(self) -> None:
        document = {
            "formatVersion": 1,
            "scenarios": [self._scenario("all", checks=["semantic.orphan"])],
        }
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "scenarios.json"
            manifest.write_text(json.dumps(document), encoding="utf-8")

            with self.assertRaisesRegex(
                HARNESS.HarnessError,
                "Scenario 'all' must resolve checks from named scenarios",
            ):
                HARNESS.load_manifest(manifest)

    def test_manifest_rejects_incomplete_all_includes(self) -> None:
        document = {
            "formatVersion": 1,
            "scenarios": [
                self._scenario("semantic.one", checks=["semantic.one.check"]),
                self._scenario("semantic.two", checks=["semantic.two.check"]),
                self._scenario("all", includes=["semantic.one"]),
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "scenarios.json"
            manifest.write_text(json.dumps(document), encoding="utf-8")

            with self.assertRaisesRegex(
                HARNESS.HarnessError,
                "Scenario 'all' must include every aggregate-enabled UI scenario in declared manifest order",
            ):
                HARNESS.load_manifest(manifest)

    def test_locale_scale_theme_requires_gameplay_save_and_driver_loads_it(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "semantic.locale-scale-theme", "ui"
        )
        driver = (
            ROOT
            / "Hatifect UI"
            / "Hatifect.UI.Stardew"
            / "Diagnostics"
            / "UiAutomatedAcceptanceController.cs"
        ).read_text(encoding="utf-8")

        self.assertTrue(resolved["requiresSave"])
        self.assertIn(
            'new("semantic.locale-scale-theme", AcceptanceScenarioKind.Ui, true',
            driver,
        )
        self.assertIn("if (ScenarioCatalog.RequiresWorld(_scenario))", driver)
        self.assertIn("BeginLoadIsolatedSave();", driver)
        self.assertIn("if (!Context.IsWorldReady)", driver)

    def test_required_mods_resolve_from_includes_in_declared_order(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "all", "ui")

        self.assertEqual(
            resolved["requiredMods"],
            ["Pathoschild.ChestsAnywhere"],
        )

    def test_product_scenario_manifest_matches_runtime_contributions(self) -> None:
        contributions = (
            (
                "semantic.chests-anywhere-overlay",
                "CompatibleScenario",
                700,
                ROOT
                / "Integrations"
                / "Chests Anywhere"
                / "Hatifect Chests Anywhere Overlay"
                / "UI"
                / "Semantic"
                / "SemanticChestsAnywhereOverlayAcceptanceScenarios.cs",
            ),
            (
                "semantic.chests-anywhere-overlay.capture-exception",
                "CaptureExceptionScenario",
                730,
                ROOT
                / "Integrations"
                / "Chests Anywhere"
                / "Hatifect Chests Anywhere Overlay"
                / "UI"
                / "Semantic"
                / "SemanticChestsAnywhereOverlayAcceptanceScenarios.cs",
            ),
        )

        for scenario_id, constant_name, order, source_path in contributions:
            with self.subTest(scenario=scenario_id):
                scenario = self.scenarios[scenario_id]
                source = source_path.read_text(encoding="utf-8")
                self.assertIn(
                    f'private const string {constant_name} = "{scenario_id}";', source
                )
                self.assertIn(f"order: {order}", source)
                self.assertIn("requiresWorld: true", source)
                for check_id in scenario["checks"]:
                    suffix = check_id.removeprefix(scenario_id)
                    self.assertIn(f'{constant_name} + "{suffix}"', source)

    def test_retired_flow_scenarios_are_not_allowlisted(self) -> None:
        all_includes = self.scenarios["all"]["includes"]
        for scenario_id in (
            "flow.route.invalidated",
            "semantic.flow-filter",
            "semantic.flow-network-overview",
        ):
            with self.subTest(scenario=scenario_id):
                self.assertNotIn(scenario_id, self.scenarios)
                self.assertNotIn(scenario_id, all_includes)
                with self.assertRaisesRegex(
                    HARNESS.HarnessError,
                    rf"Unknown ui scenario '{re.escape(scenario_id)}'",
                ):
                    HARNESS.resolve_scenario(self.scenarios, scenario_id, "ui")

    def test_negative_dependency_scenarios_are_independent_of_all(self) -> None:
        all_scenario = self.scenarios["all"]
        for scenario_id in (
            "semantic.chests-anywhere-overlay.absent",
            "semantic.chests-anywhere-overlay.incompatible",
            "semantic.chests-anywhere-overlay.capture-exception",
            "semantic.chests-anywhere-overlay.return-to-title",
        ):
            with self.subTest(scenario=scenario_id):
                self.assertFalse(self.scenarios[scenario_id]["includeInAll"])
                self.assertNotIn(scenario_id, all_scenario["includes"])

        self.assertEqual(
            self.scenarios["semantic.chests-anywhere-overlay.absent"].get(
                "requiredMods", []
            ),
            [],
        )
        for scenario_id in (
            "semantic.chests-anywhere-overlay.incompatible",
            "semantic.chests-anywhere-overlay.capture-exception",
            "semantic.chests-anywhere-overlay.return-to-title",
        ):
            self.assertEqual(
                self.scenarios[scenario_id]["requiredMods"],
                ["Pathoschild.ChestsAnywhere"],
            )

    def test_manifest_rejects_non_boolean_include_in_all(self) -> None:
        scenario = self._scenario("semantic.one", checks=["semantic.one.check"])
        scenario["includeInAll"] = "false"
        document = {
            "formatVersion": 1,
            "scenarios": [
                scenario,
                self._scenario("all", includes=["semantic.one"]),
            ],
        }
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory) / "scenarios.json"
            manifest.write_text(json.dumps(document), encoding="utf-8")

            with self.assertRaisesRegex(HARNESS.HarnessError, "invalid includeInAll"):
                HARNESS.load_manifest(manifest)

    def test_required_mods_are_deduplicated_across_includes_in_first_seen_order(self) -> None:
        scenarios = {
            "semantic.one": self._scenario(
                "semantic.one",
                checks=["semantic.one.check"],
                required_mods=["Author.Core", "Author.Shared"],
            ),
            "semantic.two": self._scenario(
                "semantic.two",
                checks=["semantic.two.check"],
                required_mods=["Author.Shared", "Author.Adapter"],
            ),
            "all": self._scenario(
                "all", includes=["semantic.one", "semantic.two"]
            ),
        }

        resolved = HARNESS.resolve_scenario(scenarios, "all", "ui")

        self.assertEqual(
            resolved["requiredMods"],
            ["Author.Core", "Author.Shared", "Author.Adapter"],
        )

    def test_manifest_rejects_invalid_or_duplicate_required_mods(self) -> None:
        for required_mods in (
            ["Pathoschild/ChestsAnywhere"],
            ["Pathoschild.ChestsAnywhere", "Pathoschild.ChestsAnywhere"],
        ):
            document = {
                "formatVersion": 1,
                "scenarios": [
                    self._scenario(
                        "semantic.one",
                        checks=["semantic.one.check"],
                        required_mods=required_mods,
                    ),
                    self._scenario("all", includes=["semantic.one"]),
                ],
            }
            with tempfile.TemporaryDirectory() as directory:
                manifest = Path(directory) / "scenarios.json"
                manifest.write_text(json.dumps(document), encoding="utf-8")

                with self.assertRaisesRegex(HARNESS.HarnessError, "invalid requiredMods"):
                    HARNESS.load_manifest(manifest)

    def test_required_mod_preflight_uses_only_isolated_manifests(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "semantic.chests-anywhere-overlay", "ui"
        )
        with tempfile.TemporaryDirectory() as directory:
            mods_root = Path(directory) / "Mods"
            mods_root.mkdir()
            self.assertEqual(
                HARNESS.validate_required_mods(resolved, mods_root),
                ["Pathoschild.ChestsAnywhere"],
            )
            mod_root = mods_root / "ChestsAnywhere"
            mod_root.mkdir()
            (mod_root / "manifest.json").write_text(
                json.dumps({"UniqueID": "Pathoschild.ChestsAnywhere"}),
                encoding="utf-8",
            )

            self.assertEqual(HARNESS.validate_required_mods(resolved, mods_root), [])

    def test_required_mod_preflight_rejects_manifest_escape(self) -> None:
        if not hasattr(os, "symlink"):
            self.skipTest("symlinks are unavailable")
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "semantic.chests-anywhere-overlay", "ui"
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            mods_root = root / "Mods"
            mods_root.mkdir()
            outside = root / "outside-manifest.json"
            outside.write_text(
                json.dumps({"UniqueID": "Pathoschild.ChestsAnywhere"}),
                encoding="utf-8",
            )
            (mods_root / "manifest.json").symlink_to(outside)

            with self.assertRaisesRegex(HARNESS.HarnessError, "escapes"):
                HARNESS.validate_required_mods(resolved, mods_root)

    def test_ui_scale_failures_remain_structured_runtime_diagnostics(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "semantic.locale-scale-theme", "ui")
        for scale in (75, 100, 125, 150):
            with self.subTest(scale=scale):
                report = self._report(resolved)
                check_id = f"semantic.scale.{scale}"
                note = f"HARNESS-VISUAL-STATE-NOT-RENDERED: scale={scale / 100}; menu origin is invalid"
                check = next(item for item in report["HostChecks"] if item["Id"] == check_id)
                check.update(Passed=False, Note=note)

                with self.assertRaises(HARNESS.HarnessError) as caught:
                    HARNESS.validate_report(resolved, report, started_at=0)

                self.assertEqual(str(caught.exception), f"Failed required host checks: {check_id}")
                self.assertEqual(caught.exception.assertions, [{
                    "id": check_id,
                    "status": "FAIL",
                    "subject": "semantic.locale-scale-theme",
                    "expected": "The required host check passes.",
                    "actual": note,
                }])

    def test_terminal_failure_is_retained_as_one_typed_diagnostic(self) -> None:
        driver = (
            ROOT
            / "Hatifect UI"
            / "Hatifect.UI.Stardew"
            / "Diagnostics"
            / "UiAutomatedAcceptanceController.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("private HarnessTerminalFailure? _terminalFailure;", driver)
        self.assertIn("private bool _diagnosticsWritten;", driver)
        self.assertIn("private void RetainTerminalFailure(string reason, Exception error)", driver)
        self.assertIn("if (_terminalFailure != null) return;", driver)
        self.assertIn("terminalError = _terminalFailure == null ? null : new", driver)
        self.assertIn("reason = _terminalFailure.Reason", driver)
        self.assertIn("type = _terminalFailure.ExceptionType", driver)
        self.assertIn("message = _terminalFailure.ExceptionMessage", driver)
        self.assertIn("stack = _terminalFailure.ExceptionStack", driver)
        self.assertNotIn("WriteDiagnostics(null)", driver)
        self.assertNotIn("error = error?.ToString()", driver)

        write_diagnostics = driver[
            driver.index("private void WriteDiagnostics()"):driver.index(
                "private enum AcceptanceScenarioKind"
            )
        ]
        guard = write_diagnostics.index("if (_diagnosticsWritten) return;")
        move = write_diagnostics.index("File.Move(temporary, path, overwrite: true);")
        marked = write_diagnostics.index("_diagnosticsWritten = true;")
        self.assertLess(guard, move)
        self.assertLess(move, marked)

        execution_failure = driver[
            driver.index('RetainTerminalFailure("HARNESS-AUTOMATION-EXECUTION-EXCEPTION", error);'):
            driver.index("private bool ExecuteAllNamedUiScenarios()")
        ]
        self.assertLess(
            execution_failure.index('RetainTerminalFailure("HARNESS-AUTOMATION-EXECUTION-EXCEPTION", error);'),
            execution_failure.index("WriteDiagnostics();"),
        )
        self.assertLess(
            execution_failure.index("WriteDiagnostics();"),
            execution_failure.index("FailUnrecorded(_terminalFailure!.Reason);"),
        )
        capture_path = driver[
            driver.index("private void OnRendered("):driver.index("private void Execute()")
        ]
        self.assertLess(
            capture_path.index("_recorder.SaveAutomatedEvidence();"),
            capture_path.index("WriteDiagnostics();"),
        )
        capture_failure = capture_path[capture_path.index("catch (Exception error)"):]
        self.assertLess(
            capture_failure.index("WriteDiagnostics();"),
            capture_failure.index("FailUnrecorded(_terminalFailure!.Reason);"),
        )
        lifecycle_failure = driver[
            driver.index("private void FailAutomationLifecycle(Exception error)"):
            driver.index("private void BeginLoadIsolatedSave()")
        ]
        self.assertLess(
            lifecycle_failure.index("WriteDiagnostics();"),
            lifecycle_failure.index("FailUnrecorded(_terminalFailure!.Reason);"),
        )
        failed_evidence = driver[
            driver.index("private void FailUnrecorded(string message)"):
            driver.index("private void RetainTerminalFailure")
        ]
        self.assertIn("try\n        {\n            _recorder.SaveAutomatedEvidence();", failed_evidence)
        self.assertIn(
            'RetainTerminalFailure("HARNESS-EVIDENCE-PERSISTENCE-EXCEPTION", error);',
            failed_evidence,
        )

    def test_save_bootstrap_is_no_save_smoke_and_uses_stardew_save_reload(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "save.bootstrap", "smoke")
        driver = (
            ROOT
            / "Hatifect UI"
            / "Hatifect.UI.Stardew"
            / "Diagnostics"
            / "UiAutomatedAcceptanceController.cs"
        ).read_text(encoding="utf-8")
        live_runner = (ROOT / "tools" / "hatifect-live-runner").read_text(encoding="utf-8")

        self.assertFalse(resolved["requiresSave"])
        self.assertEqual(resolved["checks"], ["save.bootstrap.reload"])
        self.assertIn("private const int SaveFixtureSchemaVersion = 2;", driver)
        self.assertIn("SaveGame.Save()", driver)
        self.assertIn("SaveGame.Load(saveName);", driver)
        self.assertLess(
            driver.index("SaveGame.Load(saveName);"),
            driver.index("Game1.exitActiveMenu();"),
        )
        self.assertNotIn('"LoadGame"', driver)
        self.assertIn("Awaiting world-ready: bootstrap=", driver)
        self.assertIn("worldReady={Context.IsWorldReady}", driver)
        self.assertIn("hasLoadedGame={Game1.hasLoadedGame}", driver)
        self.assertIn("(_worldWaitTicks % 300) == 0", driver)
        self.assertIn("Game1.SetSaveName(BootstrapPlayerName);", driver)
        self.assertNotIn("Game1.SetSaveName(BootstrapSaveName);", driver)
        bootstrap_save = driver[
            driver.index("private void BeginBootstrapSave()"):
            driver.index("private void CompleteBootstrapSaveAndScheduleInitialLoad()")
        ]
        self.assertLess(
            bootstrap_save.index("Game1.game1.loadForNewGame(false);"),
            bootstrap_save.index("Game1.dayOfMonth = 1;"),
        )
        self.assertLess(
            bootstrap_save.index("Game1.dayOfMonth = 1;"),
            bootstrap_save.index("SaveGame.Save();"),
        )
        self.assertIn("SeedBootstrapAcceptanceStorage();", bootstrap_save)
        self.assertLess(
            bootstrap_save.index("SeedBootstrapAcceptanceStorage();"),
            bootstrap_save.index("SaveGame.Save();"),
        )
        self.assertIn('BootstrapAcceptanceStorageLocation = "Farm"', driver)
        self.assertIn("new Chest(playerChest: true", driver)
        self.assertIn("chest.playerChest.Value = true;", driver)
        self.assertIn("chest.fridge.Value = false;", driver)
        self.assertIn("chest.giftbox.Value = false;", driver)
        self.assertIn("chest.GlobalInventoryId = null;", driver)
        self.assertIn("chest.SpecialChestType = Chest.SpecialChestTypes.None;", driver)
        self.assertIn("ItemRegistry.Create(BootstrapAcceptanceStorageItemId", driver)
        # Bootstrap reload, generic UI lifecycle, and the independent contributed CA lifecycle.
        self.assertEqual(driver.count("RequestReturnToTitle();"), 3)
        return_to_title = driver[
            driver.index("private static void RequestReturnToTitle()"):
            driver.index("private static void ResetForBootstrapInitialLoad()")
        ]
        self.assertIn('"ExitToTitle"', return_to_title)
        self.assertIn("types: new[] { typeof(Action) }", return_to_title)
        self.assertIn("exitToTitle.Invoke(", return_to_title)
        self.assertNotIn("CleanupReturningToTitle", return_to_title)
        bootstrap_schedule = driver[
            driver.index("private void CompleteBootstrapSaveAndScheduleInitialLoad()"):
            driver.index("private void BeginBootstrapInitialLoad()")
        ]
        schedule_state = bootstrap_schedule.index(
            "_bootstrapLifecycle = BootstrapLifecycleState.AwaitingInitialLoadStart;"
        )
        schedule_reset = bootstrap_schedule.index("ResetForBootstrapInitialLoad();")
        self.assertLess(schedule_state, schedule_reset)
        self.assertNotIn("BeginLoadSave(savePath);", bootstrap_schedule)
        bootstrap_initial_load = driver[
            driver.index("private void BeginBootstrapInitialLoad()"):
            driver.index("private void CompleteBootstrapInitialLoad()")
        ]
        initial_state = bootstrap_initial_load.index(
            "_bootstrapLifecycle = BootstrapLifecycleState.AwaitingInitialWorld;"
        )
        initial_load = bootstrap_initial_load.index("BeginLoadSave(savePath);")
        self.assertLess(initial_state, initial_load)
        update_loop = driver[
            driver.index("private void OnUpdateTickedCore()"):
            driver.index("private void OnRendered(")
        ]
        self.assertLess(
            update_loop.index("BootstrapLifecycleState.AwaitingInitialLoadStart"),
            update_loop.index("if (_saveEnumerator != null)"),
        )
        bootstrap_world_ready = driver[
            driver.index("private void CompleteBootstrapInitialLoad()"):
            driver.index("private void CompleteBootstrapReloadVerification()")
        ]
        self.assertIn("EnsureBootstrapIdentity(\"Initially loaded\");", bootstrap_world_ready)
        self.assertIn("EnsureBootstrapAcceptanceStorage(\"Initially loaded\");", bootstrap_world_ready)
        self.assertIn(
            "_bootstrapLifecycle = BootstrapLifecycleState.AwaitingReturnToTitle;",
            bootstrap_world_ready,
        )
        self.assertIn("RequestReturnToTitle();", bootstrap_world_ready)
        return_state = bootstrap_world_ready.index(
            "_bootstrapLifecycle = BootstrapLifecycleState.AwaitingReturnToTitle;"
        )
        return_guard = bootstrap_world_ready.index("_awaitingReturnedToTitle = true;")
        return_request = bootstrap_world_ready.index("RequestReturnToTitle();")
        self.assertLess(return_state, return_guard)
        self.assertLess(return_guard, return_request)
        returned_to_title = driver[
            driver.index("internal void OnReturnedToTitle()"):
            driver.index("private void FailAutomationLifecycle(Exception error)")
        ]
        self.assertIn(
            "_bootstrapLifecycle = BootstrapLifecycleState.AwaitingVerificationLoadStart;",
            returned_to_title,
        )
        self.assertNotIn("BeginLoadSave(", returned_to_title)
        self.assertLess(
            update_loop.index("BootstrapLifecycleState.AwaitingVerificationLoadStart"),
            update_loop.index("if (_saveEnumerator != null)"),
        )
        verification_schedule = update_loop[
            update_loop.index(
                "if (_bootstrapLifecycle == BootstrapLifecycleState.AwaitingVerificationLoadStart)"
            ):
            update_loop.index("if (_saveEnumerator != null)")
        ]
        verification_schedule_call = verification_schedule.index(
            "BeginBootstrapVerificationLoad();"
        )
        verification_schedule_return = verification_schedule.index("return;")
        self.assertLess(verification_schedule_call, verification_schedule_return)
        verification_load = driver[
            driver.index("private void BeginBootstrapVerificationLoad()"):
            driver.index("private void CompleteBootstrapReloadVerification()")
        ]
        verification_state = verification_load.index(
            "_bootstrapLifecycle = BootstrapLifecycleState.AwaitingVerificationWorld;"
        )
        verification_begin = verification_load.index("BeginLoadSave(savePath);")
        self.assertLess(verification_state, verification_begin)
        bootstrap_verification = driver[
            driver.index("private void CompleteBootstrapReloadVerification()"):
            driver.index("private static void EnsureBootstrapIdentity(string phase)")
        ]
        self.assertIn("EnsureBootstrapIdentity(\"Verification-reloaded\");", bootstrap_verification)
        self.assertIn("EnsureBootstrapAcceptanceStorage(\"Verification-reloaded\");", bootstrap_verification)
        self.assertIn("acceptanceStorage = BootstrapAcceptanceStorageReceipt()", bootstrap_verification)
        self.assertIn("private static void EnsureBootstrapAcceptanceStorage(string phase)", driver)
        self.assertIn("private static object BootstrapAcceptanceStorageReceipt()", driver)
        self.assertIn('bootstrap_preflight_result="$(python3 "$save_provisioner" bootstrap-preflight', live_runner)
        self.assertIn('[[ "$bootstrap_preflight_result" == "HARNESS-SAVE-FIXTURE-EXISTS" ]]', live_runner)
        self.assertIn("compatible verified fixture reuse PASS", live_runner)
        self.assertIn(
            "_bootstrapLifecycle = BootstrapLifecycleState.None;",
            bootstrap_verification,
        )
        bootstrap_reset = driver[
            driver.index("private static void ResetForBootstrapInitialLoad()"):
            driver.index("private void ExecuteLifecycle()")
        ]
        self.assertIn('"CleanupReturningToTitle"', bootstrap_reset)
        self.assertIn("cleanup.Invoke(", bootstrap_reset)
        self.assertIn("FailAutomationLifecycle(error);", driver)

    def test_wrong_runner_kind_fails_closed(self) -> None:
        with self.assertRaises(HARNESS.HarnessError):
            HARNESS.resolve_scenario(self.scenarios, "runtime.boot", "ui")

    def test_flow_lifecycle_requires_save_and_all_six_host_checks(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "flow.route.basic", "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.Flow"])
        self.assertNotIn("flow.route.basic", self.scenarios["all"]["includes"])
        self.assertEqual(resolved["checks"], [
            "flow.route.basic." + suffix
            for suffix in ("loaded", "delivery", "pause", "idle", "lifecycle", "reload")
        ])
        report = self._report(resolved)
        self.assertEqual(report["Scenarios"], [])
        HARNESS.validate_report(resolved, report, started_at=0)
        for check in report["HostChecks"]:
            with self.subTest(check=check["Id"]):
                check["Passed"] = False
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.validate_report(resolved, report, started_at=0)
                check["Passed"] = True

    def test_flow_names_requires_every_native_capture_check_and_a_world(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "flow.ui.names", "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.Flow"])
        self.assertNotIn("flow.ui.names", self.scenarios["all"]["includes"])
        self.assertEqual(resolved["checks"], ["flow.ui.names." + suffix for suffix in
                         ("loaded", "english", "russian", "capture-state", "retained", "restored", "read-only")])
        report = self._report(resolved)
        HARNESS.validate_report(resolved, report, started_at=0)
        for check in report["HostChecks"]:
            with self.subTest(check=check["Id"]):
                check["Passed"] = False
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.validate_report(resolved, report, started_at=0)
                check["Passed"] = True

    def test_flow_ui_isolation_requires_exact_lifecycle_checks_and_both_mods(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "flow.ui.isolation", "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.UI", "Hatifect.Flow"])
        self.assertEqual(resolved["timeoutSeconds"], 420)
        self.assertFalse(self.scenarios["flow.ui.isolation"]["includeInAll"])
        self.assertNotIn("flow.ui.isolation", self.scenarios["all"]["includes"])
        self.assertEqual(resolved["checks"], ["flow.ui.isolation." + suffix for suffix in (
            "loaded", "empty", "missing", "publication", "locale", "scale", "controller-profile",
            "unavailable", "faulted", "close", "unsubscribe-retry", "save-isolation", "retired-handles",
            "restored", "read-only")])
        self._verify_each_flow_check_is_required(resolved)

    def test_save_isolation_preserves_lifecycle_checks_and_requires_separate_save_check(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "flow.save.isolation", "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.Flow"])
        self.assertNotIn("flow.save.isolation", self.scenarios["all"]["includes"])
        expected = [check.replace("flow.route.basic.", "flow.save.isolation.")
                    for check in self.scenarios["flow.route.basic"]["checks"]]
        self.assertEqual(resolved["checks"], expected + ["flow.save.isolation.save-isolation"])
        report = self._report(resolved)
        HARNESS.validate_report(resolved, report, started_at=0)
        for check in report["HostChecks"]:
            with self.subTest(check=check["Id"]):
                check["Passed"] = False
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.validate_report(resolved, report, started_at=0)
                check["Passed"] = True

    def test_cancellation_requires_each_fresh_flow_lifecycle_and_source_check(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "flow.chest.cancellation", "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.Flow"])
        self.assertNotIn("flow.chest.cancellation", self.scenarios["all"]["includes"])
        self.assertTrue({"flow.chest.cancellation.cancellation", "flow.chest.cancellation.source-conflict",
                         "flow.chest.cancellation.re-admission", "flow.chest.cancellation.no-late-effect"}
                        .issubset(resolved["checks"]))
        self._verify_each_flow_check_is_required(resolved)

    def test_return_requires_each_capacity_retry_custody_and_taken_item_check(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "flow.chest.return", "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.Flow"])
        self.assertNotIn("flow.chest.return", self.scenarios["all"]["includes"])
        self.assertTrue({"flow.chest.return.capacity", "flow.chest.return.delivery-retry", "flow.chest.return.return-requested",
                         "flow.chest.return.return-rejected", "flow.chest.return.returned", "flow.chest.return.no-auto-retry",
                         "flow.chest.return.taken-items"}.issubset(resolved["checks"]))
        self._verify_each_flow_check_is_required(resolved)

    def test_returned_crash_requires_restart_and_all_return_family_checks(self) -> None:
        scenario = "flow.chest.crash-after-return"
        resolved = HARNESS.resolve_scenario(self.scenarios, scenario, "smoke")
        ordinary = HARNESS.resolve_scenario(self.scenarios, "flow.chest.return", "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.Flow"])
        self.assertNotIn(scenario, self.scenarios["all"]["includes"])
        self.assertEqual(set(resolved["checks"]),
                         {check.replace("flow.chest.return.", scenario + ".") for check in ordinary["checks"]}
                         | {scenario + ".process-restart"})
        self._verify_each_flow_check_is_required(resolved)

    def test_production_isolation_requires_every_world_session_and_file_check(self) -> None:
        scenario = "flow.chest.isolation"
        resolved = HARNESS.resolve_scenario(self.scenarios, scenario, "smoke")
        self.assertTrue(resolved["requiresSave"])
        self.assertEqual(resolved["requiredMods"], ["Hatifect.Flow"])
        self.assertNotIn(scenario, self.scenarios["all"]["includes"])
        self.assertTrue({scenario + ".save-isolation", scenario + ".session-fencing", scenario + ".inactive-files",
                         scenario + ".item-fidelity", scenario + ".no-duplication"}.issubset(resolved["checks"]))
        self._verify_each_flow_check_is_required(resolved)

    def _verify_each_flow_check_is_required(self, resolved) -> None:
        report = self._report(resolved)
        self.assertEqual(report["Scenarios"], [])
        HARNESS.validate_report(resolved, report, started_at=0)
        for index, check in enumerate(report["HostChecks"]):
            with self.subTest(check=check["Id"]):
                check["Passed"] = False
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.validate_report(resolved, report, started_at=0)
                check["Passed"] = True
                missing = dict(report, HostChecks=report["HostChecks"][:index] + report["HostChecks"][index + 1:])
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.validate_report(resolved, missing, started_at=0)

    def test_fresh_complete_semantic_report_passes(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "semantic.performance", "ui")
        report = self._report(resolved)

        evidence = HARNESS.validate_report(resolved, report, started_at=0)

        self.assertEqual(evidence["performance"]["frames"], 600)
        self.assertEqual(evidence["checks"], ["semantic.performance.steady"])

    def test_late_diagnostic_write_failure_cannot_finalize_passed_matrix(self) -> None:
        scenario = "semantic.locale-scale-theme"
        resolved = HARNESS.resolve_scenario(self.scenarios, scenario, "ui")
        report = self._report(resolved)
        self.assertTrue(all(check["Passed"] for check in report["HostChecks"]))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report_path = root / "host-acceptance-report.json"
            report_path.write_text(json.dumps(report), encoding="utf-8")
            (root / "diagnostics" / "runtime.json").mkdir(parents=True)
            args = HARNESS.argparse.Namespace(
                kind="ui", scenario=scenario, manifest=str(HARNESS.DEFAULT_MANIFEST),
                report=str(report_path), result=str(root / "result.json"),
                artifact_root=str(root), run_id="late-matrix-write", started_at=0,
            )

            exit_code = HARNESS.command_finalize(args)

            self.assertEqual(1, exit_code)
            result = json.loads((root / "result.json").read_text(encoding="utf-8"))
            self.assertEqual("FAIL", result["status"])
            self.assertEqual("HARNESS-RUNTIME-DIAGNOSTICS", result["assertions"][0]["id"])
            self.assertEqual(report, json.loads(report_path.read_text(encoding="utf-8")))

    def test_ui_finalization_rejects_invalid_or_foreign_diagnostics(self) -> None:
        for defect in ("missing", "temporary-only", "invalid-json", "invalid-utf8", "deep-json", "array",
                       "boolean-protocol", "foreign-run", "foreign-scenario", "missing-terminal-error",
                       "terminal-failure", "stale", "no-timezone", "boolean-process", "zero-process"):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                args, payload = self._finalization_fixture(root, "semantic.locale-scale-theme")
                path = root / "diagnostics/runtime.json"
                if defect == "missing": path.unlink()
                elif defect == "temporary-only": path.rename(path.with_suffix(".json.tmp"))
                elif defect == "invalid-json": path.write_text("{", encoding="utf-8")
                elif defect == "invalid-utf8": path.write_bytes(b"\xff")
                elif defect == "deep-json": path.write_text("[" * 100000 + "]" * 100000, encoding="utf-8")
                else:
                    if defect == "array": payload = []
                    elif defect == "boolean-protocol": payload["protocolVersion"] = True
                    elif defect == "foreign-run": payload["runId"] = "other-request"
                    elif defect == "foreign-scenario": payload["scenario"] = "semantic.lifecycle"
                    elif defect == "missing-terminal-error": del payload["terminalError"]
                    elif defect == "terminal-failure": payload["terminalError"] = {"reason": "LATE-WRITE", "message": "failed"}
                    elif defect == "stale": payload["capturedAtUtc"] = "2000-01-01T00:00:00Z"
                    elif defect == "no-timezone": payload["capturedAtUtc"] = "2026-01-01T00:00:00"
                    elif defect == "boolean-process": payload["processId"] = True
                    elif defect == "zero-process": payload["processId"] = 0
                    path.write_text(json.dumps(payload), encoding="utf-8")

                self.assertEqual(1, HARNESS.command_finalize(args))
                result = json.loads((root / "result.json").read_text(encoding="utf-8"))
                self.assertEqual("FAIL", result["status"])
                self.assertEqual("HARNESS-RUNTIME-DIAGNOSTICS", result["assertions"][0]["id"])

    def test_ui_finalization_requires_complete_rendered_matrix_including_aggregate(self) -> None:
        defects = ("missing-matrix", "not-restored", "duplicate-state", "old-frame", "boolean-frame", "no-probe",
                   "no-observation", "locale", "theme", "unapplied-scale", "nan-scale", "zero-pixel-scale",
                   "infinite-pixel-scale", "boolean-geometry", "menu-origin", "viewport", "menu-size",
                   "invalid-tree", "missing-probe", "fade", "wrong-path", "missing-png", "not-png")
        for scenario in ("semantic.locale-scale-theme", "all"):
            for defect in defects:
                with self.subTest(scenario=scenario, defect=defect), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    args, payload = self._finalization_fixture(root, scenario)
                    capture = payload["visualMatrix"][1]
                    observation = capture["Observation"]
                    if defect == "missing-matrix": del payload["visualMatrix"]
                    elif defect == "not-restored": payload["visualMatrixRestored"] = False
                    elif defect == "duplicate-state": payload["visualMatrix"] = [copy.deepcopy(capture)] * 8
                    elif defect == "old-frame": capture["CompletedFrame"] = 2
                    elif defect == "boolean-frame": capture["CompletedFrame"] = True
                    elif defect == "no-probe": capture["ProbeText"] = ""
                    elif defect == "no-observation": capture["Observation"] = None
                    elif defect == "locale": observation["SceneLocale"] = "ru"
                    elif defect == "theme": observation["Theme"] = "unknown"
                    elif defect == "unapplied-scale": observation["BaseScale"] = .75
                    elif defect == "nan-scale": observation["DesiredScale"] = float("nan")
                    elif defect == "zero-pixel-scale": observation["PixelScale"] = 0
                    elif defect == "infinite-pixel-scale": observation["PixelScale"] = float("inf")
                    elif defect == "boolean-geometry": observation["BackBufferWidth"] = True
                    elif defect == "menu-origin": observation["MenuX"] = -2147483648
                    elif defect == "viewport": observation["ViewportWidth"] += 1
                    elif defect == "menu-size": observation["MenuHeight"] += 1
                    elif defect == "invalid-tree": observation["HasValidTree"] = False
                    elif defect == "missing-probe": observation["HasProbeText"] = 1
                    elif defect == "fade": observation["LoadFadeFinished"] = False
                    elif defect == "wrong-path": capture["Screenshot"] = "../foreign.png"
                    elif defect == "missing-png": (root / capture["UiLayerScreenshot"]).unlink()
                    elif defect == "not-png": (root / capture["Screenshot"]).write_bytes(b"not a PNG")
                    (root / "diagnostics/runtime.json").write_text(json.dumps(payload), encoding="utf-8")

                    self.assertEqual(1, HARNESS.command_finalize(args))
                    result = json.loads((root / "result.json").read_text(encoding="utf-8"))
                    self.assertEqual("FAIL", result["status"])
                    self.assertEqual("HARNESS-RUNTIME-DIAGNOSTICS", result["assertions"][0]["id"])

    def test_ui_finalization_accepts_current_diagnostics_and_restored_matrix(self) -> None:
        for scenario in ("semantic.locale-scale-theme", "all", "semantic.lifecycle"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                args, payload = self._finalization_fixture(root, scenario)
                # Closed menus and non-matrix scenarios remain valid final states.
                payload["terminalOpen"] = False
                payload["menuBounds"] = None
                if scenario == "semantic.lifecycle":
                    payload["visualMatrix"] = []
                    payload["visualMatrixRestored"] = False
                    args.run_id = None
                    payload["runId"] = root.name
                (root / "diagnostics/runtime.json").write_text(json.dumps(payload), encoding="utf-8")

                self.assertEqual(0, HARNESS.command_finalize(args))
                result = json.loads((root / "result.json").read_text(encoding="utf-8"))
                self.assertEqual("PASS", result["status"])
                self.assertTrue(all(item["status"] == "PASS" for item in result["assertions"]))
                self.assertEqual(payload["runId"], result["runId"])

    def test_evidence_v3_preserves_checked_in_acceptance_criteria(self) -> None:
        ui_root = ROOT / "Hatifect UI"
        budgets = json.loads(
            (ui_root / "PERFORMANCE_BUDGETS.json").read_text(encoding="utf-8")
        )
        requirements = json.loads(
            (ui_root / "HOST_ACCEPTANCE_REQUIREMENTS.json").read_text(encoding="utf-8")
        )

        self.assertEqual(budgets["FormatVersion"], 3)
        self.assertEqual(requirements["PerformanceFormatVersion"], 3)
        self.assertEqual(budgets["MeasurementFrames"], 600)
        self.assertEqual(
            budgets["Budgets"],
            {
                "MaxP95UiThreadMs": 2.0,
                "MaxP99UiThreadMs": 4.0,
                "MaxSteadyStateAllocatedBytesPerFrame": 16384,
                "MaxMeasureCacheMissRatio": 0.2,
                "MaxArrangeCacheMissRatio": 0.2,
            },
        )
        self.assertEqual(budgets["RequiredScenarios"], ["semantic.performance"])
        resolved = HARNESS.resolve_scenario(self.scenarios, "all", "ui")
        self.assertEqual(
            [item["Id"] for item in requirements["RequiredChecks"]],
            resolved["checks"],
        )

    def test_missing_operator_check_fails_closed(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "semantic.lifecycle", "ui")
        report = self._report(resolved)
        report["HostChecks"] = report["HostChecks"][:-1]

        with self.assertRaisesRegex(HARNESS.HarnessError, "Missing required host checks"):
            HARNESS.validate_report(resolved, report, started_at=0)

    def test_duplicate_report_host_check_ids_fail_closed(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "semantic.lifecycle", "ui")
        report = self._report(resolved)
        report["HostChecks"].append({"Id": "semantic.lifecycle.visual", "Passed": True})

        with self.assertRaisesRegex(
            HARNESS.HarnessError, "Duplicate host check ids: semantic.lifecycle.visual"
        ):
            HARNESS.validate_report(resolved, report, started_at=0)

    def test_unexpected_report_host_check_id_fails_closed(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "semantic.lifecycle", "ui")
        report = self._report(resolved)
        report["HostChecks"].append({"Id": "semantic.unexpected", "Passed": True})

        with self.assertRaisesRegex(
            HARNESS.HarnessError, "Unexpected host checks: semantic.unexpected"
        ):
            HARNESS.validate_report(resolved, report, started_at=0)

    def test_legacy_surface_cannot_satisfy_semantic_performance(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "semantic.performance", "ui")
        report = self._report(resolved)
        report["Scenarios"][0]["SurfaceKinds"] = ["menu"]

        with self.assertRaisesRegex(HARNESS.HarnessError, "semantic surfaces"):
            HARNESS.validate_report(resolved, report, started_at=0)

    def test_stale_report_fails_closed(self) -> None:
        resolved = HARNESS.resolve_scenario(self.scenarios, "runtime.boot", "smoke")
        report = self._report(resolved)

        with self.assertRaisesRegex(HARNESS.HarnessError, "predates"):
            HARNESS.validate_report(resolved, report, started_at=4_000_000_000)

    def test_result_writer_uses_canonical_status(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.json"
            HARNESS.write_result(path, "BLOCKED", "runtime.boot", "not configured")
            result = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(result["status"], "BLOCKED")
        self.assertEqual(result["protocolVersion"], 1)
        self.assertEqual(
            set(result),
            {
                "protocolVersion",
                "runId",
                "scenario",
                "status",
                "durationMs",
                "assertions",
                "exceptions",
                "artifacts",
            },
        )
        self.assertEqual(result["assertions"][0]["id"], "HARNESS-EXECUTION")

    def test_result_writer_accepts_aggregate_all_identifier(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.json"
            HARNESS.write_result(path, "BLOCKED", "all", "save fixture unavailable")
            result = json.loads(path.read_text(encoding="utf-8"))

        self.assertEqual(HARNESS.validate_result(result, "all"), "BLOCKED")
        self.assertEqual(result["scenario"], "all")
        self.assertEqual(result["assertions"][0]["subject"], "all")

    def test_deployment_marker_validation_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "deployment.json"
            marker.write_text("{}\n", encoding="utf-8")
            completed = subprocess.run(
                [sys.executable, str(VALIDATOR_PATH), "validate-deployment", str(marker)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(completed.returncode, 2)
        self.assertIn("Invalid isolated deployment marker", completed.stderr)

    def test_deployment_marker_writer_is_atomic_and_valid(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "deployment.json"
            written = subprocess.run(
                [sys.executable, str(VALIDATOR_PATH), "write-deployment", str(marker)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )
            validated = subprocess.run(
                [sys.executable, str(VALIDATOR_PATH), "validate-deployment", str(marker)],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )
            leftovers = list(marker.parent.glob(".deployment.json.*.tmp"))

        self.assertEqual(written.returncode, 0, written.stderr)
        self.assertEqual(validated.returncode, 0, validated.stderr)
        self.assertEqual(leftovers, [])

    def test_shell_result_validator_accepts_matching_pass(self) -> None:
        completed = self._validate_shell_result(
            self._result("PASS", "runtime.boot"),
            "runtime.boot",
        )

        self.assertEqual(completed.returncode, 0, completed.stderr)

    def test_shell_result_validator_rejects_scenario_mismatch(self) -> None:
        completed = self._validate_shell_result(
            self._result("PASS", "other.scenario"),
            "runtime.boot",
        )

        self.assertEqual(completed.returncode, 2)
        self.assertIn("identity mismatch", completed.stderr)

    def test_macos_runner_uses_typed_user_session_request_and_canonical_direct_transport(self) -> None:
        runner = (ROOT / "tools" / "hatifect-live-runner").read_text(
            encoding="utf-8"
        )

        self.assertIn('user_session_runtime="$TOOLS_DIR/live-harness/user_session_runtime.py"', runner)
        self.assertIn('--repository-root "$REPO_ROOT"', runner)
        self.assertIn('--scenario "$scenario"', runner)
        self.assertIn('--isolated-root "$isolated_root"', runner)
        self.assertIn('--artifact-directory "$artifact_dir"', runner)
        self.assertIn('--result "$result"', runner)
        self.assertIn('deployment_marker="$isolated_root/deployment.json"', runner)
        self.assertIn('save_provisioner="$TOOLS_DIR/live-harness/save_provisioning.py"', runner)
        self.assertIn('validate-required-mods "$kind" "$scenario" "$isolated_root/Mods"', runner)
        self.assertIn("HARNESS-ENV-MODS", runner)
        self.assertIn('"$save_provisioner" plan', runner)
        self.assertNotIn('HATIFECT_SMAPI_TEST_SAVE', runner)
        self.assertIn('submit', runner)
        self.assertIn('exec python3 "$user_session_runtime"', runner)
        self.assertNotIn('hatifect-runtime-broker" doctor', runner)
        self.assertNotIn('hatifect-runtime-broker" status', runner)
        self.assertNotIn('LaunchAgent', runner)
        self.assertNotIn('runtime_broker.py', runner)
        self.assertNotIn('StardewModdingAPI', runner)
        self.assertNotIn('run_process.py', runner)

        for scenario in self.scenarios.values():
            self.assertEqual(
                scenario["automation"],
                {"protocolVersion": 1, "driver": "Hatifect.TestHarness"},
            )
            self.assertNotIn("commands", scenario)

        for wrapper_name in ("hatifect-smoke", "hatifect-ui-test"):
            wrapper = (ROOT / "tools" / wrapper_name).read_text(encoding="utf-8")
            self.assertIn("runner-result.invalid.json", wrapper)
            self.assertIn("runner-result.conflicting.json", wrapper)
            self.assertIn("HARNESS-RESULT-CONFLICT", wrapper)

        self.assertFalse((ROOT / "tools" / "hatifect-runtime-broker").exists())
        self.assertFalse((ROOT / "tools" / "live-harness" / "runtime_broker.py").exists())

    def test_ui_test_prepares_before_starting_direct_runner(self) -> None:
        wrapper = (ROOT / "tools" / "hatifect-ui-test").read_text(
            encoding="utf-8"
        )

        preparation = wrapper.index('"$TOOLS_DIR/hatifect-live-prepare"')
        execution = wrapper.index('"$TOOLS_DIR/hatifect-live-runner"')
        self.assertLess(preparation, execution)
        self.assertIn('"$run_dir/prepare.log"', wrapper)
        self.assertIn("HARNESS-PREPARE", wrapper)

    def test_prepare_invalidates_stale_deployment_before_release(self) -> None:
        prepare = (ROOT / "tools" / "hatifect-live-prepare").read_text(
            encoding="utf-8"
        )

        invalidation = prepare.index('rm -f -- "$deployment_marker"')
        release = prepare.index('"$REPO_ROOT/release.sh"')
        marker_write = prepare.index('write-deployment "$deployment_marker"')
        self.assertLess(invalidation, release)
        self.assertLess(release, marker_write)

    def test_live_harness_uses_one_validated_isolated_root(self) -> None:
        common = (ROOT / "tools" / "_common.sh").read_text(encoding="utf-8")
        prepare = (ROOT / "tools" / "hatifect-live-prepare").read_text(
            encoding="utf-8"
        )
        runner = (ROOT / "tools" / "hatifect-live-runner").read_text(
            encoding="utf-8"
        )

        self.assertIn("resolve_smapi_test_root()", common)
        self.assertIn('test_root="$(resolve_smapi_test_root)"', prepare)
        self.assertIn('isolated_root="$(resolve_smapi_test_root)"', runner)

        environment = os.environ.copy()
        environment["HATIFECT_SMAPI_TEST_ROOT"] = str(ROOT)
        completed = subprocess.run(
            [
                "bash",
                "-c",
                'source tools/_common.sh; resolve_smapi_test_root',
            ],
            cwd=ROOT,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertEqual(completed.returncode, 2)
        self.assertIn("HATIFECT_SMAPI_TEST_ROOT must stay below", completed.stderr)

    def test_explicit_process_environment_overrides_local_defaults(self) -> None:
        variable_names = (
            "HATIFECT_SOLUTION",
            "HATIFECT_CONFIGURATION",
            "HATIFECT_SMAPI_PATH",
            "HATIFECT_SMAPI_TEST_ROOT",
            "HATIFECT_TEST_TIMEOUT_SECONDS",
            "HATIFECT_TEST_SEED",
            "HATIFECT_LIVE_DOTNET",
            "HATIFECT_LIVE_TEST_DOTNET",
            "HATIFECT_LIVE_RUN_HOST_FREE_TESTS",
            "HATIFECT_VALIDATOR",
            "HATIFECT_WARNINGS_AS_ERRORS",
        )
        with tempfile.TemporaryDirectory() as directory:
            tools = Path(directory) / "tools"
            tools.mkdir()
            shutil.copy2(ROOT / "tools" / "_common.sh", tools / "_common.sh")
            (tools / "hatifect.env").write_text(
                "\n".join(
                    f'{name}="configured-{index}"'
                    for index, name in enumerate(variable_names)
                ),
                encoding="utf-8",
            )
            environment = os.environ.copy()
            expected = [
                f"explicit-{index}" for index, _ in enumerate(variable_names)
            ]
            environment.update(zip(variable_names, expected, strict=True))
            print_arguments = " ".join(
                f'"${name}"' for name in variable_names
            )

            completed = subprocess.run(
                [
                    "/bin/bash",
                    "-c",
                    (
                        'source "$1"; '
                        f"printf '%s\\n' {print_arguments}"
                    ),
                    "live-harness-env-test",
                    str(tools / "_common.sh"),
                ],
                env=environment,
                capture_output=True,
                text=True,
                check=True,
            )

        self.assertEqual(
            completed.stdout.splitlines(),
            expected,
        )

    def test_process_supervisor_times_out_and_retires_descendants(self) -> None:
        supervisor = ROOT / "tools" / "live-harness" / "run_process.py"
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "process.log"
            child_source = (
                "import subprocess,sys,time;"
                "child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)']);"
                "print(child.pid,flush=True);time.sleep(30)"
            )
            completed = subprocess.run(
                [
                    sys.executable,
                    str(supervisor),
                    "--timeout-seconds",
                    "0.3",
                    "--grace-seconds",
                    "0.1",
                    "--log",
                    str(log),
                    "--cwd",
                    directory,
                    "--",
                    sys.executable,
                    "-c",
                    child_source,
                ],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=5,
                check=False,
            )
            child_pid = int(log.read_text(encoding="utf-8").strip().splitlines()[0])

            self.assertEqual(completed.returncode, 124, completed.stderr)
            self.assertIn(str(child_pid), completed.stdout)
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline and self._process_exists(child_pid):
                time.sleep(0.05)
            self.assertFalse(self._process_exists(child_pid))

    def test_fake_executable_captures_stdout_and_stderr(self) -> None:
        supervisor = ROOT / "tools" / "live-harness" / "run_process.py"
        with tempfile.TemporaryDirectory() as directory:
            fake = Path(directory) / "fake-smapi"
            fake.write_text(
                f"#!{sys.executable}\n"
                "import sys\n"
                "print('fake stdout', flush=True)\n"
                "print('fake stderr', file=sys.stderr, flush=True)\n",
                encoding="utf-8",
            )
            fake.chmod(0o700)
            log = Path(directory) / "smapi.log"
            completed = subprocess.run(
                [
                    sys.executable,
                    str(supervisor),
                    "--timeout-seconds",
                    "2",
                    "--grace-seconds",
                    "0.1",
                    "--log",
                    str(log),
                    "--cwd",
                    directory,
                    "--",
                    str(fake),
                ],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=5,
                check=False,
            )
            evidence = log.read_text(encoding="utf-8")
            self.assertEqual(completed.returncode, 0, completed.stderr)
            self.assertIn("fake stdout", evidence)
            self.assertIn("fake stderr", evidence)

    def test_cancellation_retires_owned_tree_but_not_unrelated_process(self) -> None:
        supervisor = ROOT / "tools" / "live-harness" / "run_process.py"
        unrelated = None
        try:
            with tempfile.TemporaryDirectory() as directory:
                log = Path(directory) / "smapi.log"
                child_source = (
                    "import os,time;print(os.getpid(),flush=True);time.sleep(30)"
                )
                supervisor_process = subprocess.Popen(
                    [
                        sys.executable,
                        str(supervisor),
                        "--timeout-seconds",
                        "10",
                        "--grace-seconds",
                        "0.1",
                        "--log",
                        str(log),
                        "--cwd",
                        directory,
                        "--",
                        sys.executable,
                        "-c",
                        child_source,
                    ],
                    cwd=ROOT,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                )
                deadline = time.monotonic() + 3
                while time.monotonic() < deadline:
                    if log.is_file() and log.read_text(encoding="utf-8").strip():
                        break
                    time.sleep(0.05)
                child_pid = int(log.read_text(encoding="utf-8").strip().splitlines()[0])
                unrelated = subprocess.Popen(
                    [sys.executable, "-c", "import time; time.sleep(30)"],
                    start_new_session=True,
                )
                supervisor_process.send_signal(signal.SIGINT)
                _, stderr = supervisor_process.communicate(timeout=5)
                self.assertEqual(supervisor_process.returncode, 130, stderr)
                deadline = time.monotonic() + 2
                while time.monotonic() < deadline and self._process_exists(child_pid):
                    time.sleep(0.05)
                self.assertFalse(self._process_exists(child_pid))
                self.assertIsNone(unrelated.poll())
        finally:
            if unrelated is not None and unrelated.poll() is None:
                os.killpg(unrelated.pid, signal.SIGTERM)
                unrelated.wait(timeout=3)

    @staticmethod
    def _scenario(scenario_id, *, checks=None, includes=None, required_mods=None):
        scenario = {
            "id": scenario_id,
            "kind": "ui",
            "requiresSave": False,
            "timeoutSeconds": 60,
            "checks": checks or [],
            "includes": includes or [],
            "automation": {
                "protocolVersion": 1,
                "driver": "Hatifect.TestHarness",
            },
        }
        if required_mods is not None:
            scenario["requiredMods"] = required_mods
        return scenario

    @staticmethod
    def _report(resolved):
        checks = [{"Id": check, "Passed": True} for check in resolved["checks"]]
        performance = resolved["performance"]
        scenarios = []
        if performance is not None:
            scenarios.append(
                {
                    "Id": performance["scenarioId"],
                    "Frames": performance["minimumFrames"],
                    "P95UiThreadMs": 1.0,
                    "P99UiThreadMs": 2.0,
                    "SteadyStateAllocatedBytesPerFrame": 1024.0,
                    "MeasureCacheMissRatio": 0.1,
                    "ArrangeCacheMissRatio": 0.1,
                    "SurfaceKinds": list(performance["surfaceKinds"]),
                }
            )
        return {
            "FormatVersion": 3,
            "PerformanceFormatVersion": 3,
            "CapturedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "RuntimeFingerprint": "sha256:test",
            "GameVersion": "test-game",
            "SmapiVersion": "test-smapi",
            "HostChecks": checks,
            "Scenarios": scenarios,
        }

    def _finalization_fixture(self, root, scenario):
        resolved = HARNESS.resolve_scenario(self.scenarios, scenario, "ui")
        (root / "host-acceptance-report.json").write_text(json.dumps(self._report(resolved)), encoding="utf-8")
        args = HARNESS.argparse.Namespace(
            kind="ui", scenario=scenario, manifest=str(HARNESS.DEFAULT_MANIFEST),
            report=str(root / "host-acceptance-report.json"), result=str(root / "result.json"),
            artifact_root=str(root), run_id="matrix-finalization", started_at=time.time() - 1,
        )
        captures = []
        png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jhE0AAAAASUVORK5CYII=")
        (root / "screenshots").mkdir()
        for locale in ("en", "ru"):
            for scale, width, height in ((.75, 1707, 960), (1., 1280, 720), (1.25, 1024, 576), (1.5, 854, 480)):
                stem = f"screenshots/matrix-{locale}-{int(scale * 100)}-dark"
                capture = {
                    "Locale": locale, "Scale": scale, "CompletedFrame": 2 * (len(captures) + 1),
                    "ProbeText": "English probe" if locale == "en" else "Русский текст: ё, щ, №42",
                    "Screenshot": stem + ".png", "UiLayerScreenshot": stem + "-ui-layer.png",
                    "Observation": {
                        "GameLocale": locale, "SceneLocale": locale, "Theme": "Hatifect.UI/theme/Dark",
                        "DesiredScale": scale, "BaseScale": scale, "PixelScale": scale,
                        "BackBufferWidth": 1280, "BackBufferHeight": 720,
                        "ViewportWidth": width, "ViewportHeight": height,
                        "MenuX": 0, "MenuY": 0, "MenuWidth": width, "MenuHeight": height,
                        "HasValidTree": True, "HasProbeText": True, "LoadFadeFinished": True,
                    },
                }
                for key in ("Screenshot", "UiLayerScreenshot"):
                    (root / capture[key]).write_bytes(png)
                captures.append(capture)
        payload = {
            "protocolVersion": 1, "runId": args.run_id, "scenario": scenario,
            "capturedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(), "processId": 42,
            "terminalError": None, "visualMatrix": captures, "visualMatrixRestored": True,
        }
        (root / "diagnostics").mkdir()
        (root / "diagnostics/runtime.json").write_text(json.dumps(payload), encoding="utf-8")
        return args, payload

    @staticmethod
    def _validate_shell_result(payload, expected):
        with tempfile.TemporaryDirectory() as directory:
            result = Path(directory) / "result.json"
            result.write_text(json.dumps(payload), encoding="utf-8")
            return subprocess.run(
                [
                    "bash",
                    "-c",
                    'source "$1"; validate_result_json "$2" "$3"',
                    "live-harness-test",
                    str(ROOT / "tools" / "_common.sh"),
                    str(result),
                    expected,
                ],
                cwd=ROOT,
                capture_output=True,
                text=True,
                check=False,
            )

    @staticmethod
    def _result(status, scenario):
        return {
            "protocolVersion": 1,
            "runId": "test-run",
            "scenario": scenario,
            "status": status,
            "durationMs": 1,
            "assertions": [],
            "exceptions": [],
            "artifacts": [],
        }

    @staticmethod
    def _process_exists(process_id):
        try:
            os.kill(process_id, 0)
            return True
        except ProcessLookupError:
            return False


if __name__ == "__main__":
    unittest.main()
