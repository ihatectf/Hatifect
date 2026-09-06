import datetime as dt
import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
DIRECT_RUNTIME_PATH = ROOT / "tools" / "live-harness" / "direct_runtime.py"
SPEC = importlib.util.spec_from_file_location("hatifect_direct_runtime", DIRECT_RUNTIME_PATH)
assert SPEC is not None and SPEC.loader is not None
DIRECT_RUNTIME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DIRECT_RUNTIME)


class DirectRuntimeTests(unittest.TestCase):
    def test_canonical_flow_save_is_cleaned_on_launch_failure_with_same_scenario_authority(self) -> None:
        for scenario in ('flow.chest.roundtrip', 'flow.chest.performance'):
            with self.subTest(scenario=scenario):
                with _RequestFixture() as fixture:
                    fixture.request['scenarioId'] = scenario
                    isolated = Path(fixture.request['isolatedRoot'])
                    path = isolated / ('HatifectHarness' + fixture.request['requestId'].replace('-', '') + '_4242424242')
                    path.mkdir()
                    fixture.request['savePath'] = str(path)
                    fixture.metadata['saveProvisionerExecutable'] = 'save.py'
                    provisioner = mock.Mock()
                    provisioner.validate_fixture.return_value = ({'runtimeId': 'runtime'}, None)
                    provisioner.prepare_working_copy.return_value = path
                    provisioner.cleanup_working_copy.side_effect = lambda *args, **kwargs: args[1].rmdir()
                    with mock.patch.object(DIRECT_RUNTIME, '_load_module', return_value=provisioner):
                        with self.assertRaisesRegex(OSError, 'launch failed'):
                            with DIRECT_RUNTIME._prepared_request_saves(fixture.request, fixture.metadata):
                                raise OSError('launch failed')
                    self.assertFalse(path.exists())
                    provisioner.prepare_flow_secondary.assert_not_called()
                    provisioner.cleanup_working_copy.assert_called_once_with(isolated, path, 'runtime', fixture.request['requestId'], scenario, role='primary')

    def test_flow_lifecycle_report_has_fixed_module_ownership(self) -> None:
        isolated = Path("/isolated")
        self.assertEqual(
            DIRECT_RUNTIME._acceptance_report_source(isolated, "flow.route.basic"),
            isolated / "Mods/Hatifect/Hatifect Flow/.acceptance/host-acceptance-report.json",
        )
        self.assertEqual(
            DIRECT_RUNTIME._acceptance_report_source(isolated, "flow.save.isolation"),
            isolated / "Mods/Hatifect/Hatifect Flow/.acceptance/host-acceptance-report.json",
        )
        self.assertEqual(DIRECT_RUNTIME._acceptance_report_source(isolated, 'flow.chest.roundtrip'),
                         isolated / 'Mods/Hatifect/Hatifect Flow/.acceptance/host-acceptance-report.json')
        for scenario in ("runtime.boot", "semantic.terminal", "flow.route.unknown", "../escape"):
            with self.subTest(scenario=scenario):
                self.assertEqual(
                    DIRECT_RUNTIME._acceptance_report_source(isolated, scenario),
                    isolated / "Mods/Hatifect/Hatifect UI/.acceptance/host-acceptance-report.json",
                )

    def test_secondary_preparation_failure_cleans_primary_without_claiming_collision(self) -> None:
        with _RequestFixture() as fixture:
            fixture.request["scenarioId"] = "flow.save.isolation"
            primary = Path(fixture.request["isolatedRoot"]) / "primary"
            primary.mkdir()
            fixture.request["savePath"] = str(primary)
            fixture.metadata["saveProvisionerExecutable"] = "save.py"
            provisioner = mock.Mock()
            provisioner.validate_fixture.return_value = ({"runtimeId": "runtime"}, None)
            provisioner.prepare_working_copy.return_value = primary
            provisioner.prepare_flow_secondary.side_effect = ValueError("foreign secondary collision")
            provisioner._working_name.return_value = "secondary"
            provisioner.cleanup_working_copy.side_effect = lambda *args, **kwargs: args[1].rmdir()
            with mock.patch.object(DIRECT_RUNTIME, "_load_module", return_value=provisioner):
                with self.assertRaisesRegex(ValueError, "foreign secondary collision"):
                    DIRECT_RUNTIME._prepare_request_saves(fixture.request, fixture.metadata)
            self.assertFalse(primary.exists())
            provisioner.cleanup_working_copy.assert_called_once_with(Path(fixture.request["isolatedRoot"]), primary, "runtime", fixture.request["requestId"], 'flow.save.isolation', role='primary')

    def test_prepared_flow_copies_are_both_cleaned_on_launch_exception(self) -> None:
        with _RequestFixture() as fixture:
            fixture.request["scenarioId"] = "flow.save.isolation"
            isolated = Path(fixture.request["isolatedRoot"])
            primary, secondary = isolated / "primary", isolated / "secondary"
            primary.mkdir()
            secondary.mkdir()
            fixture.request["savePath"] = str(primary)
            fixture.metadata["saveProvisionerExecutable"] = "save.py"
            provisioner = mock.Mock()
            provisioner.validate_fixture.return_value = ({"runtimeId": "runtime"}, None)
            provisioner.prepare_working_copy.return_value = primary
            provisioner.prepare_flow_secondary.return_value = secondary
            provisioner._working_name.return_value = "secondary"
            provisioner.flow_secondary_run_id.return_value = "secondary-id"
            provisioner.cleanup_working_copy.side_effect = lambda *args, **kwargs: args[1].rmdir()
            with mock.patch.object(DIRECT_RUNTIME, "_load_module", return_value=provisioner):
                with self.assertRaisesRegex(OSError, "launch failed"):
                    with DIRECT_RUNTIME._prepared_request_saves(fixture.request, fixture.metadata):
                        raise OSError("launch failed")
            self.assertFalse(primary.exists())
            self.assertFalse(secondary.exists())
            self.assertEqual(provisioner.cleanup_working_copy.call_args_list, [mock.call(isolated, primary, "runtime", fixture.request["requestId"], 'flow.save.isolation', role='primary'), mock.call(isolated, secondary, "runtime", "secondary-id", 'flow.save.isolation', role='primary')])

    def test_production_isolation_cleanup_retains_distinct_derived_copy_roles(self) -> None:
        with _RequestFixture() as fixture:
            fixture.request["scenarioId"] = "flow.chest.isolation"
            isolated = Path(fixture.request["isolatedRoot"])
            primary, secondary = isolated / "primary", isolated / "secondary"
            primary.mkdir()
            secondary.mkdir()
            fixture.request["savePath"] = str(primary)
            fixture.metadata["saveProvisionerExecutable"] = "save.py"
            provisioner = mock.Mock()
            provisioner.validate_fixture.return_value = ({"runtimeId": "runtime"}, None)
            provisioner.prepare_working_copy.return_value = primary
            provisioner.prepare_flow_secondary.return_value = secondary
            provisioner.flow_secondary_run_id.return_value = "secondary-id"
            provisioner._working_name.return_value = "secondary"
            provisioner.cleanup_working_copy.side_effect = lambda *args, **kwargs: args[1].rmdir()
            expected = [(primary, fixture.request["requestId"], "primary"), (secondary, "secondary-id", "secondary")]
            self.assertEqual(DIRECT_RUNTIME._request_save_copies(fixture.request, provisioner), expected)
            with mock.patch.object(DIRECT_RUNTIME, "_load_module", return_value=provisioner):
                with self.assertRaisesRegex(OSError, "launch failed"):
                    with DIRECT_RUNTIME._prepared_request_saves(fixture.request, fixture.metadata):
                        raise OSError("launch failed")
            provisioner.prepare_flow_secondary.assert_called_once_with(isolated, Path(fixture.metadata["smapiPath"]),
                                                                       fixture.request["requestId"], "flow.chest.isolation")
            self.assertEqual(provisioner.cleanup_working_copy.call_args_list, [
                mock.call(isolated, primary, "runtime", fixture.request["requestId"], "flow.chest.isolation", role="primary"),
                mock.call(isolated, secondary, "runtime", "secondary-id", "flow.chest.isolation", role="secondary")])
            self.assertFalse(primary.exists())
            self.assertFalse(secondary.exists())

    def test_cleanup_attempts_both_owned_copies_after_first_failure(self) -> None:
        provisioner = mock.Mock()
        provisioner.SaveProvisioningError = lambda code, message: ValueError(message)
        provisioner.cleanup_working_copy.side_effect = [OSError("first locked"), None]
        copies = [(Path("/isolated/A"), "A", "primary"), (Path("/isolated/B"), "B", "primary")]
        with self.assertRaisesRegex(ValueError, "A: first locked"):
            DIRECT_RUNTIME._cleanup_owned_copies(provisioner, Path("/isolated"), "runtime", copies)
        self.assertEqual(provisioner.cleanup_working_copy.call_args_list, [mock.call(Path("/isolated"), copies[0][0], "runtime", "A", '', role='primary'), mock.call(Path("/isolated"), copies[1][0], "runtime", "B", '', role='primary')])

    def test_other_scenarios_never_prepare_secondary_copy(self) -> None:
        with _RequestFixture() as fixture:
            fixture.request["savePath"] = "/isolated/primary"
            fixture.metadata["saveProvisionerExecutable"] = "save.py"
            provisioner = mock.Mock()
            provisioner.validate_fixture.return_value = ({"runtimeId": "runtime"}, None)
            provisioner.prepare_working_copy.return_value = Path(fixture.request["savePath"])
            with mock.patch.object(DIRECT_RUNTIME, "_load_module", return_value=provisioner):
                prepared = DIRECT_RUNTIME._prepare_request_saves(fixture.request, fixture.metadata)
            provisioner.prepare_flow_secondary.assert_not_called()
            self.assertEqual(prepared[2], [(Path(fixture.request["savePath"]), fixture.request["requestId"], "primary")])

    def test_checked_in_protocol_schemas_match_runtime_field_sets(self) -> None:
        schema_root = ROOT / "dev" / "Hatifect.TestHarness" / "schemas"
        request = json.loads(
            (schema_root / "runtime-request.schema.json").read_text(encoding="utf-8")
        )
        response = json.loads(
            (schema_root / "runtime-response.schema.json").read_text(encoding="utf-8")
        )

        self.assertEqual(set(request["required"]), DIRECT_RUNTIME.REQUEST_FIELDS)
        self.assertEqual(set(request["properties"]), DIRECT_RUNTIME.REQUEST_FIELDS)
        self.assertEqual(set(response["required"]), DIRECT_RUNTIME.RESPONSE_FIELDS)
        self.assertEqual(set(response["properties"]), DIRECT_RUNTIME.RESPONSE_FIELDS)

    def test_request_field_set_rejects_generic_command_authority(self) -> None:
        with self._request_fixture() as fixture:
            request = dict(fixture.request)
            request["command"] = "/bin/sh -c anything"

            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "invalid field set"):
                DIRECT_RUNTIME._validate_request(request, fixture.metadata, now=fixture.now)

    def test_valid_request_is_allowlisted_and_fully_contained(self) -> None:
        with self._request_fixture() as fixture:
            validated = DIRECT_RUNTIME._validate_request(
                fixture.request, fixture.metadata, now=fixture.now
            )

        self.assertEqual(validated["scenarioId"], "semantic.lifecycle")
        self.assertNotIn("command", validated)
        self.assertNotIn("environment", validated)

    def test_request_timeout_cannot_exceed_scenario_allowlist(self) -> None:
        with self._request_fixture() as fixture:
            request = dict(fixture.request)
            request["timeoutSeconds"] = 1801

            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "timeout exceeds"):
                DIRECT_RUNTIME._validate_request(request, fixture.metadata, now=fixture.now)

    def test_protocol_version_mismatch_is_rejected(self) -> None:
        with self._request_fixture() as fixture:
            request = dict(fixture.request)
            request["protocolVersion"] = DIRECT_RUNTIME.PROTOCOL_VERSION + 1
            DIRECT_RUNTIME._atomic_write_json(
                Path(request["artifactDirectory"]) / "request.json", request, replace=True
            )
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "protocol"):
                DIRECT_RUNTIME._validate_request(request, fixture.metadata, now=fixture.now)

    def test_unknown_scenario_is_rejected_fail_closed(self) -> None:
        with self._request_fixture() as fixture, mock.patch.object(
            DIRECT_RUNTIME, "_scenario", side_effect=DIRECT_RUNTIME.DirectRuntimeError("unknown scenario")
        ):
            request = dict(fixture.request)
            request["scenarioId"] = "semantic.unknown"
            DIRECT_RUNTIME._atomic_write_json(
                Path(request["artifactDirectory"]) / "request.json", request, replace=True
            )
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "unknown scenario"):
                DIRECT_RUNTIME._validate_request(request, fixture.metadata, now=fixture.now)

    def test_stale_request_is_rejected(self) -> None:
        with self._request_fixture() as fixture:
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "stale"):
                DIRECT_RUNTIME._validate_request(
                    fixture.request,
                    fixture.metadata,
                    now=fixture.now + dt.timedelta(seconds=DIRECT_RUNTIME.REQUEST_TTL_SECONDS + 1),
                )

    def test_oversized_json_document_is_rejected_before_parse(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "request.json"
            path.write_bytes(b"{" + b" " * DIRECT_RUNTIME.MAX_DOCUMENT_BYTES + b"}")
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "size"):
                DIRECT_RUNTIME._read_json(path)

    def test_path_traversal_and_symlink_escape_are_rejected(self) -> None:
        with self._request_fixture() as fixture:
            traversal = dict(fixture.request)
            traversal["artifactDirectory"] = str(
                fixture.repository / "artifacts" / "runtime" / ".." / "outside"
            )
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "escapes|required identity"):
                DIRECT_RUNTIME._validate_request(traversal, fixture.metadata, now=fixture.now)

            escape_id = "22222222-2222-4222-8222-222222222222"
            outside = Path(fixture.temporary.name) / "outside"
            outside.mkdir()
            link = fixture.repository / "artifacts" / "runtime" / escape_id
            link.symlink_to(outside, target_is_directory=True)
            escaped = dict(fixture.request)
            escaped["requestId"] = escape_id
            escaped["artifactDirectory"] = str(link)
            escaped["resultPath"] = str(link / "result.json")
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "escapes"):
                DIRECT_RUNTIME._validate_request(escaped, fixture.metadata, now=fixture.now)

    def test_state_machine_accepts_only_declared_transitions(self) -> None:
        with self._request_fixture() as fixture:
            active = Path(fixture.temporary.name) / "active.json"
            DIRECT_RUNTIME._atomic_write_json(active, fixture.request)
            DIRECT_RUNTIME._transition(active, fixture.request, "Accepted", "accepted")
            DIRECT_RUNTIME._transition(active, fixture.request, "Launching", "launching")
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "transition"):
                DIRECT_RUNTIME._transition(active, fixture.request, "Completed", "invalid")

    def test_started_process_preserves_launching_state_before_running_transition(self) -> None:
        with self._request_fixture() as fixture:
            active = Path(fixture.temporary.name) / "active.json"
            smapi = Path(fixture.temporary.name) / "StardewModdingAPI"
            DIRECT_RUNTIME._atomic_write_json(active, fixture.request)
            DIRECT_RUNTIME._transition(active, fixture.request, "Accepted", "accepted")
            DIRECT_RUNTIME._transition(active, fixture.request, "Launching", "launching")

            DIRECT_RUNTIME._record_started_process(active, fixture.request, smapi, 4242, 4242)

            document = DIRECT_RUNTIME._read_json(active)
            self.assertEqual(document["lifecycleState"], "Running")
            self.assertEqual(document["smapiPid"], 4242)
            self.assertEqual(document["smapiProcessGroup"], 4242)
            self.assertEqual(document["smapiExecutable"], str(smapi))

    def test_concurrent_atomic_submission_has_one_winner(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "same.json"

            def write(value: int) -> str:
                try:
                    DIRECT_RUNTIME._atomic_write_json(path, {"value": value})
                    return "written"
                except DIRECT_RUNTIME.DirectRuntimeError:
                    return "rejected"

            with ThreadPoolExecutor(max_workers=2) as executor:
                outcomes = list(executor.map(write, (1, 2)))
            self.assertEqual(sorted(outcomes), ["rejected", "written"])

    def test_parallel_requests_are_serialized_by_single_run_lock(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            lock_path = Path(directory) / "run.lock"
            with lock_path.open("a+b") as first, lock_path.open("a+b") as second:
                DIRECT_RUNTIME.fcntl.flock(first.fileno(), DIRECT_RUNTIME.fcntl.LOCK_EX | DIRECT_RUNTIME.fcntl.LOCK_NB)
                with self.assertRaises(BlockingIOError):
                    DIRECT_RUNTIME.fcntl.flock(
                        second.fileno(), DIRECT_RUNTIME.fcntl.LOCK_EX | DIRECT_RUNTIME.fcntl.LOCK_NB
                    )
                DIRECT_RUNTIME.fcntl.flock(first.fileno(), DIRECT_RUNTIME.fcntl.LOCK_UN)
                DIRECT_RUNTIME.fcntl.flock(second.fileno(), DIRECT_RUNTIME.fcntl.LOCK_EX | DIRECT_RUNTIME.fcntl.LOCK_NB)
                DIRECT_RUNTIME.fcntl.flock(second.fileno(), DIRECT_RUNTIME.fcntl.LOCK_UN)

    def test_malformed_or_version_mismatched_response_is_rejected(self) -> None:
        with self._request_fixture() as fixture:
            response = DIRECT_RUNTIME._response(
                fixture.request,
                "Completed",
                "PASS",
                0,
                None,
                "ok",
                fixture.now,
            )
            DIRECT_RUNTIME._validate_response(response, fixture.request)
            malformed = dict(response)
            malformed.pop("artifacts")
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "field set"):
                DIRECT_RUNTIME._validate_response(malformed, fixture.request)
            mismatched = dict(response)
            mismatched["protocolVersion"] += 1
            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "identity"):
                DIRECT_RUNTIME._validate_response(mismatched, fixture.request)

    def test_exit_code_mapping_is_authoritative(self) -> None:
        self.assertEqual(DIRECT_RUNTIME._exit_code("PASS"), 0)
        self.assertEqual(DIRECT_RUNTIME._exit_code("FAIL"), 1)
        self.assertEqual(DIRECT_RUNTIME._exit_code("BLOCKED"), 2)
        with self.assertRaises(DIRECT_RUNTIME.DirectRuntimeError):
            DIRECT_RUNTIME._exit_code("UNKNOWN")

    def test_runtime_environment_rehomes_all_mutable_user_state(self) -> None:
        with self._request_fixture() as fixture:
            environment = DIRECT_RUNTIME._minimal_environment(fixture.request, fixture.metadata)
            isolated = Path(fixture.request["isolatedRoot"])
            for name in ("HOME", "TMPDIR", "DOTNET_BUNDLE_EXTRACT_BASE_DIR"):
                path = Path(environment[name])
                self.assertTrue(path.is_dir())
                self.assertEqual(path.stat().st_mode & 0o777, 0o700)

        self.assertEqual(environment["HOME"], str(isolated / "home"))
        self.assertEqual(environment["XDG_CONFIG_HOME"], str(isolated / "config"))
        self.assertEqual(environment["SMAPI_MODS_PATH"], str(isolated / "Mods"))
        self.assertEqual(environment["HATIFECT_TEST_RUN_ID"], fixture.request["requestId"])
        self.assertEqual(environment["HATIFECT_TEST_ISOLATED_ROOT"], str(isolated))
        self.assertNotEqual(environment["HOME"], str(Path.home()))

    def test_minimal_environment_does_not_inherit_unallowlisted_ambient_values(self) -> None:
        with self._request_fixture() as fixture, mock.patch.dict(
            os.environ,
            {"HATIFECT_AMBIENT_SECRET_SENTINEL": "must-not-be-inherited"},
        ):
            environment = DIRECT_RUNTIME._minimal_environment(fixture.request, fixture.metadata)

        self.assertNotIn("HATIFECT_AMBIENT_SECRET_SENTINEL", environment)
        self.assertEqual(
            environment["HOME"],
            str(Path(fixture.request["isolatedRoot"]) / "home"),
        )

    def test_pre_discovery_abort_is_classified_as_local_infrastructure(self) -> None:
        with self._request_fixture() as fixture:
            outcome, result_writer = self._execute_exit_without_report(
                fixture,
                "[SMAPI] SMAPI test\n[SMAPI] Mods go here: /isolated/Mods\n",
            )

        self.assertEqual(outcome[0:4], ("Failed", "BLOCKED", 2, "LocalInfrastructure"))
        self.assertIn("BLOCKED_LOCAL_INFRA", outcome[4])
        self.assertEqual(result_writer.call_args.args[4], "HARNESS-LOCAL-INFRA-BOOT")

    def test_abort_after_hatifect_runtime_is_classified_as_product_runtime(self) -> None:
        with self._request_fixture() as fixture:
            outcome, result_writer = self._execute_exit_without_report(
                fixture,
                "[SMAPI] Mods loaded and ready!\n[Hatifect UI] runtime started\n",
            )

        self.assertEqual(outcome[0:4], ("Failed", "BLOCKED", 2, "ProductRuntimeFailure"))
        self.assertEqual(result_writer.call_args.args[4], "HARNESS-PROCESS-BOOT")

    def test_execute_request_uses_fixed_argument_list_and_game_directory(self) -> None:
        with self._request_fixture() as fixture:
            active = Path(fixture.temporary.name) / "direct-state.json"
            DIRECT_RUNTIME._atomic_write_json(active, fixture.request)
            DIRECT_RUNTIME._transition(active, fixture.request, "Accepted", "accepted")
            captured = {}

            def run(command, working_directory, _log, _timeout, _grace, **kwargs):
                captured["command"] = command
                captured["working_directory"] = working_directory
                captured["environment"] = kwargs["environment"]
                kwargs["on_started"](4242, 4242)
                report = (
                    Path(fixture.request["isolatedRoot"])
                    / "Mods"
                    / "Hatifect"
                    / "Hatifect UI"
                    / ".acceptance"
                    / "host-acceptance-report.json"
                )
                report.parent.mkdir()
                report.write_text("{}\n", encoding="utf-8")
                kwargs["on_completed"](0, [])
                return 0

            supervisor = SimpleNamespace(
                run=run,
                TEARDOWN_FAILURE_EXIT=125,
                TIMEOUT_EXIT=124,
                START_FAILURE_EXIT=126,
                INTERRUPTED_EXIT=130,
            )

            def finalize(args):
                Path(args.result).write_text('{"status":"PASS"}\n', encoding="utf-8")
                return 0

            validator = SimpleNamespace(
                command_finalize=finalize,
                validate_result=lambda _result, _scenario: "PASS",
            )

            def load_module(name, _path):
                return supervisor if name == "hatifect_direct_runtime_process_supervisor" else validator

            with mock.patch.object(DIRECT_RUNTIME, "_load_module", side_effect=load_module):
                outcome = DIRECT_RUNTIME._execute_request(
                    fixture.request,
                    fixture.metadata,
                    active,
                    Path(fixture.temporary.name) / "cancel",
                )

            self.assertEqual(outcome[:5], ("Completed", "PASS", 0, None, "Automated acceptance evidence finalized."))
            self.assertEqual(captured["command"], ["/game/StardewModdingAPI"])
            self.assertEqual(captured["working_directory"], Path("/game"))
            self.assertEqual(
                captured["environment"]["SMAPI_MODS_PATH"],
                str(Path(fixture.request["isolatedRoot"]) / "Mods"),
            )

    def test_direct_transport_has_no_launchagent_or_installed_state_surface(self) -> None:
        self.assertFalse(hasattr(DIRECT_RUNTIME, "_launchctl"))
        self.assertFalse(hasattr(DIRECT_RUNTIME, "_installed_metadata"))
        self.assertFalse(hasattr(DIRECT_RUNTIME, "install"))
        self.assertFalse(hasattr(DIRECT_RUNTIME, "serve"))
        self.assertFalse(hasattr(DIRECT_RUNTIME, "submit"))

        with self._request_fixture() as fixture:
            result_path = Path(fixture.request["resultPath"])
            args = SimpleNamespace(
                repository_root=str(fixture.repository),
                smapi_path=fixture.metadata["smapiPath"],
                kind=fixture.request["kind"],
                scenario=fixture.request["scenarioId"],
                isolated_root=fixture.request["isolatedRoot"],
                artifact_directory=fixture.request["artifactDirectory"],
                result=fixture.request["resultPath"],
                save_path=None,
                timeout_seconds=fixture.request["timeoutSeconds"],
                seed=fixture.request["seed"],
            )

            def execute(request, _metadata, active, _cancellation):
                DIRECT_RUNTIME._transition(active, request, "Launching", "launching")
                DIRECT_RUNTIME._transition(active, request, "Running", "running")
                result_path.write_text('{"status":"PASS"}\n', encoding="utf-8")
                return "Completed", "PASS", 0, None, "completed", False

            validator = SimpleNamespace(validate_result=lambda _result, _scenario: "PASS")
            with mock.patch.object(
                DIRECT_RUNTIME, "_direct_metadata", return_value=fixture.metadata
            ), mock.patch.object(
                DIRECT_RUNTIME,
                "_build_request",
                return_value=(fixture.request, fixture.metadata),
            ), mock.patch.object(
                DIRECT_RUNTIME, "_execute_request", side_effect=execute
            ) as execution, mock.patch.object(
                DIRECT_RUNTIME, "_load_module", return_value=validator
            ):
                exit_code = DIRECT_RUNTIME.direct(args)

            self.assertEqual(exit_code, 0)
            execution.assert_called_once()
            state = DIRECT_RUNTIME._read_json(
                Path(fixture.request["artifactDirectory"]) / "direct-process-state.json"
            )
            self.assertEqual(state["lifecycleState"], "Completed")
            response = DIRECT_RUNTIME._read_json(
                Path(fixture.request["artifactDirectory"])
                / "diagnostics"
                / "transport-result.json"
            )
            self.assertEqual(response["status"], "PASS")

    def test_atomic_state_refuses_implicit_overwrite(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "response.json"
            DIRECT_RUNTIME._atomic_write_json(path, {"value": 1})

            with self.assertRaisesRegex(DIRECT_RUNTIME.DirectRuntimeError, "overwrite"):
                DIRECT_RUNTIME._atomic_write_json(path, {"value": 2})

            self.assertEqual(json.loads(path.read_text(encoding="utf-8")), {"value": 1})

    def _request_fixture(self):
        return _RequestFixture()

    def _execute_exit_without_report(self, fixture, log_text):
        active = Path(fixture.temporary.name) / "exit-state.json"
        DIRECT_RUNTIME._atomic_write_json(active, fixture.request)
        DIRECT_RUNTIME._transition(active, fixture.request, "Accepted", "accepted")

        def run(_command, _working_directory, log_path, _timeout, _grace, **kwargs):
            kwargs["on_started"](4343, 4343)
            Path(log_path).write_text(log_text, encoding="utf-8")
            kwargs["on_completed"](134, [])
            return 134

        supervisor = SimpleNamespace(
            run=run,
            TEARDOWN_FAILURE_EXIT=125,
            TIMEOUT_EXIT=124,
            START_FAILURE_EXIT=126,
            INTERRUPTED_EXIT=130,
        )
        with mock.patch.object(
            DIRECT_RUNTIME, "_load_module", return_value=supervisor
        ), mock.patch.object(DIRECT_RUNTIME, "_write_harness_result") as result_writer:
            outcome = DIRECT_RUNTIME._execute_request(
                fixture.request,
                fixture.metadata,
                active,
                Path(fixture.temporary.name) / "cancel",
            )
        return outcome, result_writer


class _RequestFixture:
    def __enter__(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="hatifect-direct-runtime-test.")
        base = Path(self.temporary.name)
        self.repository = base / "repository"
        self.repository.mkdir()
        (self.repository / "AGENTS.md").write_text("test\n", encoding="utf-8")
        harness = self.repository / "tools" / "live-harness"
        harness.mkdir(parents=True)
        (harness / "scenarios.json").write_text("{}\n", encoding="utf-8")
        artifact = self.repository / "artifacts" / "runtime" / "11111111-1111-4111-8111-111111111111"
        artifact.mkdir(parents=True)
        isolated = base / "hatifect-smapi-test.unit"
        (isolated / "Mods" / "Hatifect" / "Hatifect UI").mkdir(parents=True)
        (isolated / "Mods" / "Hatifect" / "Hatifect UI" / "manifest.json").write_text(
            "{}\n", encoding="utf-8"
        )
        (isolated / "deployment.json").write_text("{}\n", encoding="utf-8")
        repository, device, inode = DIRECT_RUNTIME._repository_identity(self.repository)
        self.now = dt.datetime.now(dt.timezone.utc)
        self.request = {
            "protocolVersion": DIRECT_RUNTIME.PROTOCOL_VERSION,
            "requestType": "runScenario",
            "requestId": artifact.name,
            "repositoryRoot": str(repository),
            "repositoryDevice": device,
            "repositoryInode": inode,
            "repositoryHead": "a" * 40,
            "environmentId": DIRECT_RUNTIME.ENVIRONMENT_ID,
            "kind": "ui",
            "scenarioId": "semantic.lifecycle",
            "isolatedRoot": str(isolated),
            "artifactDirectory": str(artifact),
            "resultPath": str(artifact / "result.json"),
            "savePath": None,
            "timeoutSeconds": 1800,
            "seed": 0,
            "createdAtUtc": DIRECT_RUNTIME._timestamp(self.now),
            "expiresAtUtc": DIRECT_RUNTIME._timestamp(
                self.now + dt.timedelta(seconds=DIRECT_RUNTIME.REQUEST_TTL_SECONDS)
            ),
        }
        self.metadata = {
            "protocolVersion": DIRECT_RUNTIME.PROTOCOL_VERSION,
            "transportId": DIRECT_RUNTIME.TRANSPORT_ID,
            "repositoryRoot": str(repository),
            "repositoryDevice": device,
            "repositoryInode": inode,
            "repositoryHead": "a" * 40,
            "pythonExecutable": str(Path(os.__file__).resolve()),
            "transportExecutable": str(DIRECT_RUNTIME_PATH),
            "transportSha256": "test",
            "validatorExecutable": str(harness / "validate.py"),
            "validatorSha256": "test",
            "processSupervisorExecutable": str(harness / "run_process.py"),
            "processSupervisorSha256": "test",
            "scenarioManifest": str(harness / "scenarios.json"),
            "scenarioManifestSha256": "test",
            "smapiPath": "/game/StardewModdingAPI",
            "runtimeStateRoot": str(base / "state"),
            "constructedAtUtc": DIRECT_RUNTIME._timestamp(self.now),
        }
        scenario = {
            "id": "semantic.lifecycle",
            "kind": "ui",
            "requiresSave": False,
            "timeoutSeconds": 1800,
        }
        validator = SimpleNamespace(command_validate_deployment=lambda _args: 0)
        self.patches = (
            mock.patch.object(DIRECT_RUNTIME, "_scenario", return_value=scenario),
            mock.patch.object(DIRECT_RUNTIME, "_repository_head", return_value="a" * 40),
            mock.patch.object(DIRECT_RUNTIME, "_validate_transport_sources"),
            mock.patch.object(DIRECT_RUNTIME, "_validate_repository_transport_sources"),
            mock.patch.object(DIRECT_RUNTIME, "_load_module", return_value=validator),
            mock.patch.object(DIRECT_RUNTIME, "_validate_isolated_root", return_value=isolated),
        )
        for patch in self.patches:
            patch.start()
        DIRECT_RUNTIME._atomic_write_json(artifact / "request.json", self.request)
        return self

    def __exit__(self, exception_type, exception, traceback):
        for patch in reversed(self.patches):
            patch.stop()
        self.temporary.cleanup()


if __name__ == "__main__":
    unittest.main()
