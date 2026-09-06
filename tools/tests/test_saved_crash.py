"""Contract and real process tests for the fixed approved confirmed-save crash boundaries."""

import datetime as dt
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
HARNESS = ROOT / "tools" / "live-harness"


def _load(name):
    spec = importlib.util.spec_from_file_location("saved_crash_test_" + name, HARNESS / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


DIRECT = _load("direct_runtime")
RUNNER = _load("run_process")
SAVE = _load("save_provisioning")


class SavedCrashTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="hatifect-saved-crash-test.")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.isolated = self.root / "isolated"
        self.isolated.mkdir()
        self.smapi = self.root / "game" / "StardewModdingAPI"
        self.smapi.parent.mkdir()
        self.smapi.write_text("launcher")
        self.smapi.chmod(0o700)
        for name in ("Stardew Valley.dll", "StardewModdingAPI.dll"):
            (self.smapi.parent / name).write_bytes(name.encode())
        module = self.isolated / "Mods/Hatifect/Hatifect Flow"
        module.mkdir(parents=True)
        for name in ("Hatifect.Flow.Core.dll", "Hatifect.Flow.Persistence.dll", "Hatifect.Flow.dll"):
            (module / name).write_bytes(name.encode())

        bootstrap_id = str(uuid.uuid4())
        bootstrap = SAVE.bootstrap_preflight(self.isolated, self.smapi, bootstrap_id)
        bootstrap.mkdir()
        (bootstrap / bootstrap.name).write_bytes(b"confirmed-save")
        (bootstrap / "SaveGameInfo").write_bytes(b"save-info")
        runtime = SAVE._runtime_fingerprint(self.smapi)
        SAVE._atomic_write_json(bootstrap / ".hatifect-save-owner.json", SAVE._owner(runtime["runtimeId"], bootstrap_id))
        receipt = self.root / "bootstrap.json"
        SAVE._atomic_write_json(receipt, {
            "fixtureSchemaVersion": SAVE.FIXTURE_SCHEMA_VERSION, "runId": bootstrap_id, "savePath": str(bootstrap),
            "playerName": SAVE.SYNTHETIC_PLAYER_NAME, "farmName": SAVE.SYNTHETIC_FARM_NAME,
            "favoriteThing": SAVE.SYNTHETIC_FAVORITE_THING, "uniqueMultiplayerId": SAVE.SYNTHETIC_UNIQUE_ID,
            "stardewVersion": "test", "smapiVersion": "test", "reloadVerified": True,
            "acceptanceStorage": SAVE._acceptance_storage(),
        })
        SAVE.finalize_bootstrap(self.isolated, self.smapi, bootstrap_id, receipt)
        self.runtime_id = runtime["runtimeId"]
        self.run_id = str(uuid.uuid4())
        self.save = SAVE.prepare_working_copy(self.isolated, self.smapi, self.run_id, DIRECT.SAVED_CRASH_SCENARIO)
        self.artifact = self.root / self.run_id
        (self.artifact / "diagnostics").mkdir(parents=True)
        self.marker_path = self.artifact / "diagnostics/flow-crash-ready.json"
        self.process_path = self.artifact / "process.json"
        self.active = self.root / "active.json"
        self.cancel = self.root / "cancel"
        self.request = {
            "requestId": self.run_id, "scenarioId": DIRECT.SAVED_CRASH_SCENARIO, "isolatedRoot": str(self.isolated),
            "artifactDirectory": str(self.artifact), "savePath": str(self.save), "timeoutSeconds": 10,
            "resultPath": str(self.artifact / "result.json"), "seed": 0,
        }
        self.metadata = {"smapiPath": str(self.smapi), "saveProvisionerExecutable": str(HARNESS / "save_provisioning.py")}
        self.process = {"pid": 4242, "processGroup": 4242, "startedAtUtc": DIRECT._timestamp()}
        self.marker = {
            "formatVersion": 1, "runId": self.run_id, "scenarioId": DIRECT.SAVED_CRASH_SCENARIO,
            "phase": "saved-in-transit", "pid": 4242, "sessionId": str(uuid.uuid4()), "saveName": self.save.name,
            "saveId": 4242424242, "saveHash": DIRECT._sha256(self.save / self.save.name),
            "runtimeFingerprint": DIRECT._flow_runtime_fingerprint(self.isolated),
            "parcelId": str(uuid.uuid4()), "partialParcelId": str(uuid.uuid4()),
            "sourceX": 1, "sourceY": 2, "destinationX": 3, "destinationY": 4, "remainderXml": "<Item><Stack>8</Stack></Item>",
            "loads": 1, "savingEvents": 1, "savedEvents": 1, "capturedAtUtc": DIRECT._timestamp(),
        }
        DIRECT._atomic_write_json(self.active, {"lifecycleState": "Launching"})
        DIRECT._atomic_write_json(self.process_path, self.process)

    def write_marker(self, changes=None):
        value = dict(self.marker, **(changes or {}))
        self.marker_path.write_text(json.dumps(value))
        return value

    def started(self, pid, group, started, continuation=False):
        DIRECT._record_started_process(self.active, self.request, self.smapi, pid, group, continuation=continuation)
        DIRECT._atomic_write_json(self.process_path, {"pid": pid, "processGroup": group, "startedAtUtc": DIRECT._timestamp(started)}, replace=True)

    def completed(self, code, errors):
        process = DIRECT._read_json(self.process_path)
        process.update(observedExitCode=code, teardownErrors=errors)
        DIRECT._atomic_write_json(self.process_path, process, replace=True)

    def run_crash(self, runner):
        return DIRECT._run_saved_crash(self.request, self.metadata, runner, self.active, self.cancel, self.started, self.completed)

    def use_delivery_boundary(self):
        self.use_boundary(DIRECT.DELIVERED_CRASH_SCENARIO, "saved-delivered", 2)

    def use_boundary(self, scenario, phase, saves):
        SAVE.cleanup_working_copy(self.isolated, self.save, self.runtime_id, self.run_id, self.request["scenarioId"])
        self.request["scenarioId"] = scenario
        self.save = SAVE.prepare_working_copy(self.isolated, self.smapi, self.run_id, self.request["scenarioId"])
        self.marker.update(scenarioId=scenario, phase=phase, loads=saves, savingEvents=saves, savedEvents=saves)

    def test_unsaved_extraction_marker_requires_reserved_save_phase_and_first_save(self):
        self.use_boundary(DIRECT.UNSAVED_EXTRACTION_CRASH_SCENARIO, "saved-reserved-unsaved-extraction", 1)
        self.write_marker()
        self.assertEqual(self.marker, DIRECT._saved_crash_marker(self.request, self.metadata, self.process))
        self.assertEqual(self.save.name, f"HatifectHarness{uuid.UUID(self.run_id).hex}_4242424242")
        for changes in ({"phase": "saved-in-transit"}, {"phase": "saved-delivered"},
                        {"scenarioId": DIRECT.SAVED_CRASH_SCENARIO}, {"scenarioId": DIRECT.DELIVERED_CRASH_SCENARIO},
                        {"loads": 2}, {"savingEvents": 2}, {"savedEvents": 2}, {"savedEvents": True}, {"savedEvents": 0}):
            with self.subTest(changes=changes):
                self.write_marker(changes)
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    DIRECT._saved_crash_marker(self.request, self.metadata, self.process)

    def test_delivery_marker_requires_delivered_phase_and_exact_second_save(self):
        self.use_delivery_boundary()
        self.write_marker()
        self.assertEqual(self.marker, DIRECT._saved_crash_marker(self.request, self.metadata, self.process))
        for changes in ({"phase": "saved-in-transit"}, {"scenarioId": DIRECT.SAVED_CRASH_SCENARIO},
                        {"loads": 1}, {"savingEvents": 1}, {"savedEvents": 1}, {"savedEvents": True}, {"savedEvents": 3}):
            with self.subTest(changes=changes):
                self.write_marker(changes)
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    DIRECT._saved_crash_marker(self.request, self.metadata, self.process)
        self.request["scenarioId"] = DIRECT.SAVED_CRASH_SCENARIO
        self.write_marker()
        with self.assertRaises(DIRECT.DirectRuntimeError):
            DIRECT._saved_crash_marker(self.request, self.metadata, self.process)

    def test_unsaved_delivery_marker_rejects_saved_delivery_and_other_rollback_boundaries(self):
        self.use_boundary(DIRECT.UNSAVED_DELIVERY_CRASH_SCENARIO, "saved-in-transit-unsaved-delivery", 1)
        self.write_marker()
        self.assertEqual(self.marker, DIRECT._saved_crash_marker(self.request, self.metadata, self.process))
        self.assertEqual(self.save.name, f"HatifectHarness{uuid.UUID(self.run_id).hex}_4242424242")
        for changes in ({"phase": "saved-delivered"}, {"phase": "saved-in-transit"},
                        {"phase": "saved-reserved-unsaved-extraction"}, {"scenarioId": DIRECT.DELIVERED_CRASH_SCENARIO},
                        {"scenarioId": DIRECT.UNSAVED_EXTRACTION_CRASH_SCENARIO}, {"loads": 2},
                        {"savingEvents": 2}, {"savedEvents": 2}, {"savedEvents": True}, {"savedEvents": 0}):
            with self.subTest(changes=changes):
                self.write_marker(changes)
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    DIRECT._saved_crash_marker(self.request, self.metadata, self.process)

    def test_returned_marker_requires_returned_phase_and_exact_fourth_save(self):
        self.use_boundary(DIRECT.RETURNED_CRASH_SCENARIO, "saved-returned", 4)
        self.write_marker()
        self.assertEqual(self.marker, DIRECT._saved_crash_marker(self.request, self.metadata, self.process))
        self.assertEqual(self.save.name, f"HatifectHarness{uuid.UUID(self.run_id).hex}_4242424242")
        for changes in ({"phase": "saved-delivered"}, {"phase": "saved-in-transit"},
                        {"phase": "saved-in-transit-unsaved-delivery"}, {"scenarioId": DIRECT.DELIVERED_CRASH_SCENARIO},
                        {"scenarioId": "flow.chest.return"}, {"loads": 3}, {"loads": 5},
                        {"savingEvents": 3}, {"savedEvents": 3}, {"savedEvents": True}, {"savedEvents": 5}):
            with self.subTest(changes=changes):
                self.write_marker(changes)
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    DIRECT._saved_crash_marker(self.request, self.metadata, self.process)

    def test_marker_requires_current_process_save_bytes_and_actual_fixture_owner(self):
        self.assertIsNone(DIRECT._saved_crash_marker(self.request, self.metadata, self.process))
        self.write_marker()
        self.assertEqual(self.marker, DIRECT._saved_crash_marker(self.request, self.metadata, self.process))
        self.assertEqual(self.save.name, f"HatifectHarness{uuid.UUID(self.run_id).hex}_4242424242")
        cases = {
            "foreign process": {"pid": 4243}, "boolean PID": {"pid": True}, "foreign request": {"runId": str(uuid.uuid4())},
            "wrong save": {"saveName": "other"}, "wrong bytes": {"saveHash": "0" * 64},
            "wrong candidate": {"runtimeFingerprint": "0" * 64}, "no saved event": {"savedEvents": 0},
            "boolean counter": {"savingEvents": True}, "wrong phase": {"phase": "saving"},
            "same parcel": {"partialParcelId": self.marker["parcelId"]}, "empty session": {"sessionId": str(uuid.UUID(int=0))},
            "out of range": {"sourceX": -1}, "same station": {"sourceX": 3, "sourceY": 4},
            "future": {"capturedAtUtc": DIRECT._timestamp(DIRECT._utc_now() + dt.timedelta(minutes=1))},
            "stale": {"capturedAtUtc": DIRECT._timestamp(DIRECT._utc_now() - dt.timedelta(minutes=1))},
            "oversized remainder": {"remainderXml": "x" * 32769}, "extra authority": {"command": "anything"},
        }
        for name, changes in cases.items():
            with self.subTest(case=name):
                self.write_marker(changes)
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    DIRECT._saved_crash_marker(self.request, self.metadata, self.process)
        self.write_marker()
        owner = self.save / ".hatifect-save-owner.json"
        document = json.loads(owner.read_text())
        document["runId"] = str(uuid.uuid4())
        owner.write_text(json.dumps(document))
        with self.assertRaises(DIRECT.DirectRuntimeError):
            DIRECT._saved_crash_marker(self.request, self.metadata, self.process)

    def test_malformed_linked_and_unbounded_marker_never_authorize_crash(self):
        for payload in (b"{", b"\xff", b"[]", b" ", b"{" + b" " * 65536 + b"}", b"[" * 2000 + b"]" * 2000):
            with self.subTest(size=len(payload)):
                self.marker_path.write_bytes(payload)
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    DIRECT._saved_crash_marker(self.request, self.metadata, self.process)
        self.marker_path.unlink()
        outside = self.root / "foreign.json"
        outside.write_text(json.dumps(self.marker))
        self.marker_path.symlink_to(outside)
        with self.assertRaises(DIRECT.DirectRuntimeError):
            DIRECT._saved_crash_marker(self.request, self.metadata, self.process)
        self.assertEqual(json.loads(outside.read_text()), self.marker)

    def test_ordinary_scenario_cannot_use_marker_or_running_continuation(self):
        for scenario in ("flow.chest.roundtrip", "flow.chest.cancellation", "flow.chest.return", "flow.chest.isolation", "flow.chest.performance", "flow.chest.resources"):
            with self.subTest(scenario=scenario):
                self.request["scenarioId"] = scenario
                self.write_marker()
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    DIRECT._saved_crash_marker(self.request, self.metadata, self.process)
                DIRECT._atomic_write_json(self.active, {"lifecycleState": "Running", "smapiPid": 4242}, replace=True)
                with self.assertRaises(DIRECT.DirectRuntimeError):
                    self.started(4343, 4343, DIRECT._utc_now(), True)
                self.assertEqual(DIRECT._read_json(self.active)["smapiPid"], 4242)
                self.assertEqual(DIRECT._acceptance_report_source(self.isolated, scenario),
                                 self.isolated / "Mods/Hatifect/Hatifect Flow/.acceptance/host-acceptance-report.json")

    def test_real_sigkill_then_distinct_process_uses_same_owned_saved_bytes(self):
        self.verify_real_two_processes()

    def test_real_delivered_boundary_restarts_with_its_fixed_scenario_and_saved_bytes(self):
        self.use_delivery_boundary()
        self.verify_real_two_processes()

    def test_real_unsaved_extraction_boundary_restarts_with_the_same_confirmed_save(self):
        self.use_boundary(DIRECT.UNSAVED_EXTRACTION_CRASH_SCENARIO, "saved-reserved-unsaved-extraction", 1)
        self.verify_real_two_processes()

    def test_real_unsaved_delivery_boundary_restarts_from_its_confirmed_inflight_save(self):
        self.use_boundary(DIRECT.UNSAVED_DELIVERY_CRASH_SCENARIO, "saved-in-transit-unsaved-delivery", 1)
        self.verify_real_two_processes()

    def test_real_returned_boundary_restarts_from_the_fourth_confirmed_save(self):
        self.use_boundary(DIRECT.RETURNED_CRASH_SCENARIO, "saved-returned", 4)
        self.verify_real_two_processes()

    def test_child_save_before_launch_notification_still_authorizes_owned_restart(self):
        self.use_boundary(DIRECT.RETURNED_CRASH_SCENARIO, "saved-returned", 4)
        started = self.started
        notifications = []

        def after_child_save(pid, group, *args):
            if not notifications:
                deadline = time.monotonic() + 5
                while not self.marker_path.exists() and time.monotonic() < deadline:
                    time.sleep(0.01)
                self.assertTrue(self.marker_path.is_file(), "child did not reach its confirmed-save barrier")
            notifications.append(DIRECT._utc_now())
            started(pid, group, *args)

        with mock.patch.object(self, "started", side_effect=after_child_save):
            self.verify_real_two_processes()

        marker = DIRECT._read_json(self.marker_path)
        prepare = DIRECT._read_json(self.artifact / "diagnostics/process-prepare.json")
        resume = DIRECT._read_json(self.artifact / "diagnostics/process-resume.json")
        captured = DIRECT._parse_timestamp(marker["capturedAtUtc"])
        self.assertEqual(len(notifications), 2)
        self.assertLess(captured, notifications[0])
        self.assertLessEqual(DIRECT._parse_timestamp(prepare["startedAtUtc"]), captured)
        self.assertLess(captured, DIRECT._parse_timestamp(resume["startedAtUtc"]))
        self.assertLessEqual(DIRECT._parse_timestamp(resume["startedAtUtc"]), notifications[1])

    def verify_real_two_processes(self):
        template = self.artifact / "marker-template.json"
        template.write_text(json.dumps(self.marker))
        self.smapi.write_text(f"#!{sys.executable}\n" + '''import datetime,json,os,pathlib,time
artifact = pathlib.Path(os.environ["HATIFECT_TEST_ARTIFACTS"])
save = pathlib.Path(os.environ["HATIFECT_SMAPI_TEST_SAVE"])
assert (save / save.name).read_bytes() == b"confirmed-save"
if os.environ["HATIFECT_TEST_CRASH_PHASE"] == "prepare":
    marker = json.loads((artifact / "marker-template.json").read_text())
    marker.update(pid=os.getpid(), capturedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    temporary = artifact / "ready.tmp"
    temporary.write_text(json.dumps(marker))
    os.replace(temporary, artifact / "diagnostics/flow-crash-ready.json")
    print("prepared confirmed saved bytes", flush=True)
    time.sleep(30)
else:
    proof = json.loads((artifact / "diagnostics/flow-crash-termination.json").read_text())
    assert proof["rawExitCode"] == -9 and proof["pid"] != os.getpid()
    (artifact / "resume-observed.json").write_text(json.dumps({"pid": os.getpid(), "save": str(save)}))
    print("resumed same saved bytes", flush=True)
''')
        before = SAVE._inventory(self.save)
        self.assertEqual(self.run_crash(RUNNER), 0)
        prepare = DIRECT._read_json(self.artifact / "diagnostics/process-prepare.json")
        resume = DIRECT._read_json(self.artifact / "diagnostics/process-resume.json")
        proof = DIRECT._read_json(self.artifact / "diagnostics/flow-crash-termination.json")
        self.assertEqual((prepare["observedExitCode"], resume["observedExitCode"], proof["rawExitCode"]), (137, 0, -9))
        self.assertEqual(proof["scenarioId"], self.request["scenarioId"])
        self.assertNotEqual(prepare["pid"], resume["pid"])
        self.assertEqual(proof["pid"], prepare["pid"])
        self.assertEqual((proof["requestedSignal"], proof["processGroup"]), (9, prepare["pid"]))
        self.assertEqual(DIRECT._read_json(self.artifact / "resume-observed.json"), {"pid": resume["pid"], "save": str(self.save)})
        self.assertEqual(before, SAVE._inventory(self.save))
        self.assertEqual((prepare["teardownErrors"], resume["teardownErrors"]), ([], []))
        self.assertIn("prepared confirmed saved bytes", (self.artifact / "smapi-prepare.log").read_text())
        self.assertIn("resumed same saved bytes", (self.artifact / "smapi.log").read_text())
        SAVE.cleanup_working_copy(self.isolated, self.save, self.runtime_id, self.run_id, self.request["scenarioId"])
        self.assertFalse(self.save.exists())
        SAVE.validate_fixture(self.isolated, self.smapi)

    def fake_runner(self, first_exit=137, raw_exit=-9, second_exit=0, after_first=None, failed_launch=False):
        calls = []

        def run(command, cwd, log, timeout, grace, **kwargs):
            calls.append((command, cwd, dict(kwargs["environment"]), timeout))
            self.assertEqual(command, [str(self.smapi)])
            self.assertEqual(cwd, self.smapi.parent)
            self.assertTrue(self.save.exists())
            log.write_text("[Hatifect Flow] process log\n")
            if len(calls) == 1:
                kwargs["on_started"](4242, 4242, DIRECT._utc_now())
                self.write_marker({"capturedAtUtc": DIRECT._timestamp()})
                if raw_exit is not None:
                    self.assertTrue(kwargs["force_kill_requested"]())
                    kwargs["on_forced_exit"](4242, 4242, raw_exit)
                kwargs["on_completed"](first_exit, [])
                if after_first:
                    after_first()
                return first_exit
            self.assertIsNone(DIRECT._read_json(self.active)["smapiPid"])
            self.assertIsNone(DIRECT._read_json(self.process_path)["pid"])
            if not failed_launch:
                kwargs["on_started"](4343, 4343, DIRECT._utc_now())
            kwargs["on_completed"](second_exit, [])
            return second_exit

        return SimpleNamespace(run=run, TIMEOUT_EXIT=124, INTERRUPTED_EXIT=130, TEARDOWN_FAILURE_EXIT=125), calls

    def test_unproved_first_exits_never_restart_and_keep_original_failure_log(self):
        for code, raw in ((0, None), (134, None), (137, None), (137, -15), (0, -9), (124, -9), (130, -9), (125, -9)):
            with self.subTest(code=code, raw=raw):
                # Every attempt has fresh request-local output/state, just as the caller requires.
                for path in (self.marker_path, self.artifact / "diagnostics/flow-crash-termination.json", self.artifact / "diagnostics/process-prepare.json"):
                    path.unlink(missing_ok=True)
                DIRECT._atomic_write_json(self.active, {"lifecycleState": "Launching"}, replace=True)
                runner, calls = self.fake_runner(first_exit=code, raw_exit=raw)
                if (code, raw) in ((0, None), (137, -15), (0, -9)):
                    with self.assertRaises(DIRECT.DirectRuntimeError):
                        self.run_crash(runner)
                else:
                    self.assertEqual(self.run_crash(runner), code)
                self.assertEqual(len(calls), 1)
                self.assertIn("[Hatifect Flow]", (self.artifact / "smapi.log").read_text())
                self.assertTrue(self.save.exists())

    def test_changed_saved_bytes_after_death_prevent_resume(self):
        runner, calls = self.fake_runner(after_first=lambda: (self.save / self.save.name).write_bytes(b"changed"))
        with self.assertRaises(DIRECT.DirectRuntimeError):
            self.run_crash(runner)
        self.assertEqual(len(calls), 1)

    def test_prepare_failure_report_cannot_be_overwritten_by_a_successful_resume(self):
        report = DIRECT._acceptance_report_source(self.isolated, DIRECT.SAVED_CRASH_SCENARIO)
        report.parent.mkdir()
        payload = b'{"HostChecks":[{"Passed":false,"Message":"saved boundary advanced"}]}'
        runner, calls = self.fake_runner(after_first=lambda: report.write_bytes(payload))
        with self.assertRaisesRegex(DIRECT.DirectRuntimeError, "terminal acceptance report"):
            self.run_crash(runner)
        self.assertEqual(len(calls), 1)
        self.assertEqual(report.read_bytes(), payload)
        self.assertEqual((self.artifact / "diagnostics/host-acceptance-prepare.json").read_bytes(), payload)

    def test_cancel_after_observed_death_prevents_resume(self):
        runner, calls = self.fake_runner(after_first=lambda: self.cancel.touch())
        self.assertEqual(self.run_crash(runner), RUNNER.INTERRUPTED_EXIT)
        self.assertEqual(len(calls), 1)

    def test_second_launch_failure_has_no_stale_process_owner_and_uses_remaining_timeout(self):
        runner, calls = self.fake_runner(second_exit=126, failed_launch=True)
        self.assertEqual(self.run_crash(runner), 126)
        self.assertEqual([call[2]["HATIFECT_TEST_CRASH_PHASE"] for call in calls], ["prepare", "resume"])
        self.assertEqual([call[2]["HATIFECT_SMAPI_TEST_SAVE"] for call in calls], [str(self.save)] * 2)
        self.assertLess(calls[1][3], calls[0][3])
        self.assertGreater(calls[1][3], 0)
        self.assertIsNone(DIRECT._read_json(self.active)["smapiProcessGroup"])
        self.assertEqual(DIRECT._read_json(self.artifact / "diagnostics/process-resume.json")["observedExitCode"], 126)

    def test_invalid_marker_fails_public_execution_and_cleans_only_owned_copy(self):
        self.metadata.update(repositoryRoot=str(self.root), processSupervisorExecutable=str(HARNESS / "run_process.py"),
                             validatorExecutable=str(HARNESS / "validate.py"))
        self.process_path.unlink()  # The public executor creates its own initial ownership journal.
        DIRECT._atomic_write_json(self.active, {"lifecycleState": "Accepted"}, replace=True)
        self.smapi.write_text(f"#!{sys.executable}\n" + '''import os,pathlib,time
path = pathlib.Path(os.environ["HATIFECT_TEST_ARTIFACTS"]) / "diagnostics/flow-crash-ready.json"
path.write_text("{")
time.sleep(30)
''')
        foreign = self.save.parent / "foreign-save"
        foreign.mkdir()
        (foreign / "keep").write_text("preserve")
        outcome = DIRECT._execute_request(self.request, self.metadata, self.active, self.cancel)
        self.assertEqual(outcome[:4], ("Failed", "FAIL", 1, "ScenarioFailure"))
        result = DIRECT._read_json(Path(self.request["resultPath"]))
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(result["assertions"][0]["id"], "HARNESS-CRASH-EVIDENCE")
        self.assertFalse(self.save.exists())
        self.assertEqual((foreign / "keep").read_text(), "preserve")
        self.assertFalse((self.artifact / "diagnostics/process-resume.json").exists())
        self.assertFalse(ForcedProcessTests.process_exists(DIRECT._read_json(self.process_path)["pid"]))
        SAVE.validate_fixture(self.isolated, self.smapi)

    def test_exception_backstop_cleans_crash_copy_with_same_scenario_authority(self):
        SAVE.cleanup_working_copy(self.isolated, self.save, self.runtime_id, self.run_id, DIRECT.SAVED_CRASH_SCENARIO)
        with self.assertRaisesRegex(OSError, "interrupted execution"):
            with DIRECT._prepared_request_saves(self.request, self.metadata):
                self.assertTrue(self.save.exists())
                raise OSError("interrupted execution")
        self.assertFalse(self.save.exists())
        SAVE.validate_fixture(self.isolated, self.smapi)

    def test_manifest_keeps_crash_out_of_aggregate_and_uses_fixed_flow_report(self):
        validator = _load("validate")
        unsaved_scenarios = (DIRECT.UNSAVED_EXTRACTION_CRASH_SCENARIO, DIRECT.UNSAVED_DELIVERY_CRASH_SCENARIO)
        for scenario_id in (DIRECT.SAVED_CRASH_SCENARIO, DIRECT.DELIVERED_CRASH_SCENARIO, DIRECT.RETURNED_CRASH_SCENARIO, *unsaved_scenarios):
            with self.subTest(scenario=scenario_id):
                scenario = validator.load_manifest()[scenario_id]
                self.assertFalse(scenario["includeInAll"])
                self.assertTrue(scenario["requiresSave"])
                self.assertEqual(scenario["requiredMods"], ["Hatifect.Flow"])
                self.assertEqual(len(scenario["checks"]), 15 if scenario_id == DIRECT.RETURNED_CRASH_SCENARIO else 10 if scenario_id in unsaved_scenarios else 9)
                self.assertEqual(scenario_id + ".unsaved-rollback" in scenario["checks"], scenario_id in unsaved_scenarios)
                self.assertIn(scenario_id + ".process-restart", scenario["checks"])
                self.assertEqual(DIRECT._acceptance_report_source(self.isolated, scenario_id),
                                 self.isolated / "Mods/Hatifect/Hatifect Flow/.acceptance/host-acceptance-report.json")


class ForcedProcessTests(unittest.TestCase):
    def test_sigkill_retires_owned_group_and_preserves_unrelated_process(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ready = root / "ready.json"
            source = "import json,os,pathlib,subprocess,sys,time; child=subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)']);pathlib.Path(sys.argv[1]).write_text(json.dumps([os.getpid(),child.pid]));time.sleep(30)"
            unrelated = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(30)"], start_new_session=True)
            try:
                forced = []
                completed = []
                code = RUNNER.run([sys.executable, "-c", source, str(ready)], root, root / "process.log", 5, 0.1,
                                  force_kill_requested=ready.exists, on_forced_exit=lambda *args: forced.append(args),
                                  on_completed=lambda *args: completed.append(args))
                pid, child = json.loads(ready.read_text())
                self.assertEqual(code, 137)
                self.assertEqual(forced, [(pid, pid, -9)])
                self.assertEqual(completed, [(137, [])])
                for owned in (pid, child):
                    deadline = time.monotonic() + 2
                    while time.monotonic() < deadline and self.process_exists(owned):
                        time.sleep(0.05)
                    self.assertFalse(self.process_exists(owned), f"owned PID {owned} survived SIGKILL")
                self.assertIsNone(unrelated.poll())
            finally:
                unrelated.terminate()
                unrelated.wait(timeout=5)

    def test_control_error_cleans_process_and_records_failure_without_forced_proof(self):
        with tempfile.TemporaryDirectory() as directory:
            owned, completed, forced = [], [], []

            def broken_marker():
                raise ValueError("invalid marker")

            with self.assertRaisesRegex(ValueError, "invalid marker"):
                RUNNER.run([sys.executable, "-c", "import time;time.sleep(30)"], Path(directory), Path(directory) / "process.log", 5, 0.1,
                           on_started=lambda *args: owned.append(args), force_kill_requested=broken_marker,
                           on_forced_exit=lambda *args: forced.append(args), on_completed=lambda *args: completed.append(args))
            self.assertEqual(completed, [(126, [])])
            self.assertEqual(forced, [])
            self.assertFalse(self.process_exists(owned[0][0]))

    @staticmethod
    def process_exists(pid):
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        return True
