import datetime as dt
import contextlib
import fcntl
import importlib.util
import io
import json
import os
import tempfile
import threading
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "live-harness" / "user_session_runtime.py"
SPEC = importlib.util.spec_from_file_location("hatifect_user_session_runtime", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
RUNTIME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNTIME)


class _FakeDirect:
    MAX_TIMEOUT_SECONDS = 7200

    def __init__(self, repository: Path, checkout_sha: str):
        self.repository = repository.resolve()
        self.checkout_sha = checkout_sha

    def _repository_identity(self, repository: Path):
        resolved = repository.resolve(strict=True)
        info = resolved.stat()
        return resolved, info.st_dev, info.st_ino

    def _repository_head(self, _repository: Path):
        return self.checkout_sha

    def _contained(self, root: Path, candidate: Path):
        resolved_root = root.resolve(strict=True)
        resolved = candidate.resolve(strict=False)
        resolved.relative_to(resolved_root)
        return resolved

    def _validate_isolated_root(self, _repository: Path, isolated: Path):
        return isolated.resolve(strict=True)


class UserSessionRuntimeTests(unittest.TestCase):
    def test_roundtrip_request_rejects_legacy_and_other_run_save_names(self) -> None:
        save_root = self.isolated / 'config/StardewValley/Saves'
        save_root.mkdir(parents=True)
        self.request['scenarioId'] = 'flow.chest.roundtrip'
        self.validator.resolve_scenario.return_value['requiresSave'] = True
        spec = importlib.util.spec_from_file_location('test_real_save_naming', ROOT / 'tools/live-harness/save_provisioning.py')
        provisioner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(provisioner)
        self.direct._load_module = lambda *_: provisioner
        for name in (f'HatifectHarness_{uuid.UUID(self.request_id).hex}', 'HatifectHarness' + uuid.uuid4().hex + '_4242424242'):
            self.request['savePath'] = str(save_root / name)
            with self.assertRaisesRegex(RUNTIME.UserSessionRuntimeError, 'planned isolated working copy'):
                RUNTIME._validate_request(self.request, self.repository)
        self.request['savePath'] = str(save_root / provisioner._working_name(self.request_id, 'flow.chest.roundtrip'))
        self.assertEqual(self.request, RUNTIME._validate_request(self.request, self.repository))

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="hatifect-user-session-test.")
        self.repository = Path(self.temporary.name) / "repository"
        self.repository.mkdir()
        (self.repository / "tools" / "live-harness").mkdir(parents=True)
        self.request_id = str(uuid.uuid4())
        self.artifact = self.repository / "artifacts" / "runtime" / self.request_id
        self.artifact.mkdir(parents=True)
        self.isolated = self.repository / ".smapi-test" / "isolated"
        self.isolated.mkdir(parents=True)
        self.checkout_sha = "a" * 40
        self.direct = _FakeDirect(self.repository, self.checkout_sha)
        now = dt.datetime.now(dt.timezone.utc)
        info = self.repository.stat()
        self.request = {
            "protocolVersion": RUNTIME.PROTOCOL_VERSION,
            "requestType": RUNTIME.REQUEST_TYPE,
            "requestId": self.request_id,
            "repositoryRoot": str(self.repository),
            "repositoryDevice": info.st_dev,
            "repositoryInode": info.st_ino,
            "checkoutSha": self.checkout_sha,
            "kind": "ui",
            "scenarioId": "semantic.lifecycle",
            "isolatedRoot": str(self.isolated),
            "artifactDirectory": str(self.artifact),
            "resultPath": str(self.artifact / "result.json"),
            "savePath": None,
            "timeoutSeconds": 1800,
            "seed": 0,
            "createdAtUtc": RUNTIME._timestamp(now),
            "expiresAtUtc": RUNTIME._timestamp(
                now + dt.timedelta(seconds=RUNTIME.REQUEST_TTL_SECONDS)
            ),
        }
        self.validator = mock.Mock()
        self.validator.load_manifest.return_value = {}
        self.validator.resolve_scenario.return_value = {
            "id": "semantic.lifecycle",
            "kind": "ui",
            "requiresSave": False,
            "timeoutSeconds": 1800,
        }
        self.direct_patch = mock.patch.object(
            RUNTIME, "_direct_runtime", return_value=self.direct
        )
        self.load_patch = mock.patch.object(
            RUNTIME, "_load_module", return_value=self.validator
        )
        self.direct_patch.start()
        self.load_patch.start()

    def tearDown(self) -> None:
        self.load_patch.stop()
        self.direct_patch.stop()
        self.temporary.cleanup()

    def test_typed_request_accepts_exact_workspace_identity(self) -> None:
        validated = RUNTIME._validate_request(
            self.request, self.repository, now=RUNTIME._parse_timestamp(self.request["createdAtUtc"])
        )

        self.assertEqual(validated["requestId"], self.request_id)
        self.assertEqual(validated["scenarioId"], "semantic.lifecycle")
        self.assertEqual(validated["checkoutSha"], self.checkout_sha)

    def test_request_identity_mismatches_fail_closed(self) -> None:
        mutations = {
            "protocolVersion": RUNTIME.PROTOCOL_VERSION + 1,
            "requestId": str(uuid.uuid4()),
            "scenarioId": "unknown.scenario",
            "checkoutSha": "b" * 40,
        }
        for field, value in mutations.items():
            with self.subTest(field=field):
                changed = dict(self.request)
                changed[field] = value
                if field == "requestId":
                    changed["artifactDirectory"] = self.request["artifactDirectory"]
                if field == "scenarioId":
                    self.validator.resolve_scenario.side_effect = ValueError("unknown scenario")
                else:
                    self.validator.resolve_scenario.side_effect = None
                with self.assertRaises((RUNTIME.UserSessionRuntimeError, ValueError)):
                    RUNTIME._validate_request(
                        changed,
                        self.repository,
                        now=RUNTIME._parse_timestamp(self.request["createdAtUtc"]),
                    )

    def test_typed_rejection_result_checks_protocol_id_scenario_and_sha(self) -> None:
        result = RUNTIME._executor_result(
            self.request,
            state="Rejected",
            status="BLOCKED",
            exit_code=2,
            message="rejected",
            direct_path=None,
        )
        RUNTIME._validate_result(result, self.request)

        for field, value in (
            ("protocolVersion", 99),
            ("requestId", str(uuid.uuid4())),
            ("scenarioId", "runtime.boot"),
            ("checkoutSha", "b" * 40),
        ):
            with self.subTest(field=field):
                changed = dict(result)
                changed[field] = value
                with self.assertRaisesRegex(
                    RUNTIME.UserSessionRuntimeError,
                    "protocolVersion, requestId, scenario, or checkout SHA",
                ):
                    RUNTIME._validate_result(changed, self.request)

    def test_atomic_mailbox_publication_has_no_partial_leftovers(self) -> None:
        mailbox = self.repository / ".smapi-test" / "user-session-runtime" / "request.json"

        RUNTIME._atomic_write_json(mailbox, self.request, replace=True)
        replacement = dict(self.request)
        replacement["seed"] = 1
        RUNTIME._atomic_write_json(mailbox, replacement, replace=True)

        self.assertEqual(json.loads(mailbox.read_text(encoding="utf-8"))["seed"], 1)
        self.assertEqual(list(mailbox.parent.glob("*.tmp")), [])
        self.assertEqual(list(mailbox.parent.glob(".*.tmp")), [])

    def test_mailbox_reader_rejects_symlinked_document(self) -> None:
        target = self.repository / "target.json"
        target.write_text("{}\n", encoding="utf-8")
        link = self.repository / "request-link.json"
        link.symlink_to(target)

        with self.assertRaises(OSError):
            RUNTIME._read_json(link)

    def test_workspace_mailbox_rejects_symlinked_smapi_test_root(self) -> None:
        repository = Path(self.temporary.name) / "symlinked-repository"
        repository.mkdir()
        external = Path(self.temporary.name) / "external-state"
        external.mkdir()
        (repository / ".smapi-test").symlink_to(external, target_is_directory=True)

        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "Unsafe user-session state directory",
        ):
            RUNTIME._state_root(repository)

    def test_server_source_digest_set_fails_closed_after_change(self) -> None:
        source = Path(self.temporary.name) / "transport.py"
        source.write_text("original\n", encoding="utf-8")
        digests = RUNTIME._capture_digests((source,))

        RUNTIME._verify_digests(digests)
        source.write_text("changed\n", encoding="utf-8")

        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "source changed",
        ):
            RUNTIME._verify_digests(digests)

    def test_running_source_drift_defers_restart_without_request_cancellation(self) -> None:
        restart_pending = threading.Event()
        with mock.patch.object(
            RUNTIME,
            "_verify_digests",
            side_effect=RUNTIME.WorkerRestartRequested("changed"),
        ):
            RUNTIME._observe_running_source_drift({}, restart_pending)

        self.assertTrue(restart_pending.is_set())

    def test_request_rejects_symlinked_artifact_directory(self) -> None:
        target = self.repository / "artifacts" / "runtime" / f"target-{self.request_id}"
        target.mkdir()
        self.artifact.rmdir()
        self.artifact.symlink_to(target, target_is_directory=True)

        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "must not be a symlink",
        ):
            RUNTIME._validate_request(
                self.request,
                self.repository,
                now=RUNTIME._parse_timestamp(self.request["createdAtUtc"]),
            )

    def test_accepted_executor_failure_without_direct_evidence_stays_blocked(self) -> None:
        result = RUNTIME._executor_result(
            self.request,
            state="Failed",
            status="BLOCKED",
            exit_code=2,
            message="executor failure",
            direct_path=None,
        )

        self.assertEqual(RUNTIME._validate_result(result, self.request)["status"], "BLOCKED")

    def test_unexpected_accepted_request_failure_is_typed_and_next_request_can_run(self) -> None:
        completed = RUNTIME._executor_result(
            self.request,
            state="Completed",
            status="PASS",
            exit_code=0,
            message="complete",
            direct_path=self.artifact / "diagnostics" / "transport-result.json",
        )

        with mock.patch.object(
            RUNTIME,
            "_process_request",
            side_effect=[RuntimeError("unexpected request failure"), completed],
        ), mock.patch.object(RUNTIME, "_validate_result", side_effect=lambda result, _request: result):
            failed = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )
            recovered = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )

        self.assertEqual(failed["state"], "Failed")
        self.assertEqual(failed["status"], "BLOCKED")
        self.assertEqual(failed["exitCode"], 2)
        self.assertIn("RuntimeError: unexpected request failure", failed["message"])
        evidence = RUNTIME._read_json(
            self.artifact / "diagnostics" / "executor-failure.json"
        )
        self.assertEqual(evidence["exceptionType"], "RuntimeError")
        self.assertIn("unexpected request failure", evidence["traceback"])
        self.assertEqual(recovered["status"], "PASS")

    def test_timed_out_child_request_does_not_poison_the_next_request(self) -> None:
        timed_out = RUNTIME._executor_result(
            self.request,
            state="TimedOut",
            status="BLOCKED",
            exit_code=2,
            message="child timeout",
            direct_path=self.artifact / "diagnostics" / "transport-result.json",
        )
        completed = RUNTIME._executor_result(
            self.request,
            state="Completed",
            status="PASS",
            exit_code=0,
            message="complete",
            direct_path=self.artifact / "diagnostics" / "transport-result.json",
        )
        with mock.patch.object(
            RUNTIME,
            "_process_request",
            side_effect=[timed_out, completed],
        ), mock.patch.object(RUNTIME, "_validate_result", side_effect=lambda result, _request: result):
            first = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )
            second = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )

        self.assertEqual((first["state"], first["status"]), ("TimedOut", "BLOCKED"))
        self.assertEqual((second["state"], second["status"]), ("Completed", "PASS"))

    def test_single_run_lock_rejects_concurrent_owner(self) -> None:
        lock_path = self.repository / ".smapi-test" / "user-session-runtime" / "serve.lock"
        lock_path.parent.mkdir(parents=True)
        with lock_path.open("a+b") as first:
            fcntl.flock(first.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaisesRegex(
                RUNTIME.UserSessionRuntimeError, "already owns"
            ):
                with RUNTIME._exclusive_lock(lock_path, "already owns"):
                    self.fail("concurrent executor lock must not be acquired")

    def test_executor_delegates_to_canonical_direct_runtime_and_binds_result(self) -> None:
        direct_request = {
            "repositoryHead": self.checkout_sha,
            "requestId": self.request_id,
            "scenarioId": "semantic.lifecycle",
        }
        direct_result = {
            "requestId": self.request_id,
            "scenarioId": "semantic.lifecycle",
            "state": "Completed",
            "status": "PASS",
            "exitCode": 0,
            "message": "complete",
        }
        transport_path = self.artifact / "diagnostics" / "transport-result.json"

        def execute(_args):
            transport_path.parent.mkdir(parents=True)
            (self.artifact / "request.json").write_text(
                json.dumps(direct_request), encoding="utf-8"
            )
            transport_path.write_text(json.dumps(direct_result), encoding="utf-8")
            return 0

        delegated = mock.Mock()
        delegated._direct_metadata.return_value = {"repositoryHead": self.checkout_sha}
        delegated.direct.side_effect = execute
        delegated._read_json.side_effect = lambda path: json.loads(
            Path(path).read_text(encoding="utf-8")
        )
        delegated._validate_response.side_effect = lambda document, _request: document

        with mock.patch.object(
            RUNTIME, "_validate_request", return_value=self.request
        ), mock.patch.object(RUNTIME, "_direct_runtime", return_value=delegated):
            result = RUNTIME._process_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )

        delegated.direct.assert_called_once()
        self.assertEqual(result["status"], "PASS")
        self.assertEqual(result["requestId"], self.request_id)
        self.assertEqual(result["scenarioId"], "semantic.lifecycle")
        self.assertEqual(result["checkoutSha"], self.checkout_sha)
        self.assertEqual(result["directTransportResultPath"], str(transport_path))

    def test_status_accepts_stale_ready_checkout_but_rejects_stale_running_checkout(self) -> None:
        root = self.repository / ".smapi-test" / "user-session-runtime"
        root.mkdir(parents=True, exist_ok=True)
        state = RUNTIME._state_document(
            self.repository,
            str(uuid.uuid4()),
            RUNTIME._timestamp(),
            "Ready",
            "ready",
        )
        RUNTIME._atomic_write_json(root / "state.json", state, replace=True)

        output = io.StringIO()
        with mock.patch.object(RUNTIME, "_state_root", return_value=root), \
             contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            self.assertEqual(
                RUNTIME.status(SimpleNamespace(repository_root=str(self.repository))),
                0,
            )
            state["checkoutSha"] = "b" * 40
            RUNTIME._atomic_write_json(root / "state.json", state, replace=True)
            self.assertEqual(
                RUNTIME.status(SimpleNamespace(repository_root=str(self.repository))),
                0,
            )
            state["lifecycleState"] = "Running"
            state["requestId"] = self.request_id
            state["scenarioId"] = "semantic.lifecycle"
            RUNTIME._atomic_write_json(root / "state.json", state, replace=True)
            self.assertEqual(
                RUNTIME.status(SimpleNamespace(repository_root=str(self.repository))),
                2,
            )

    def test_status_validates_expanded_supervisor_worker_identity(self) -> None:
        supervisor_id = str(uuid.uuid4())
        worker_id = str(uuid.uuid4())
        state = RUNTIME._state_document(
            self.repository,
            supervisor_id,
            RUNTIME._timestamp(),
            "Ready",
            "ready",
        )
        state.update(
            {
                "supervisorId": supervisor_id,
                "supervisorPid": state["pid"],
                "workerId": worker_id,
                "workerPid": state["pid"] + 1,
                "workerGeneration": 2,
                "restartReason": "Worker source digest changed",
                "restartCount": 1,
                "workerSourceDigest": "c" * 64,
                "acceptedCheckoutSha": self.checkout_sha,
                "heartbeat": state["heartbeatAtUtc"],
                "activeRequestId": None,
            }
        )

        validated = RUNTIME._validate_state(state, self.repository, require_live=False)
        self.assertEqual(validated["supervisorId"], supervisor_id)
        self.assertEqual(validated["workerId"], worker_id)
        changed = dict(state)
        changed["activeRequestId"] = self.request_id
        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "aliases or counters",
        ):
            RUNTIME._validate_state(changed, self.repository, require_live=False)

    def test_vscode_task_is_explicit_process_singleton(self) -> None:
        task_document = json.loads(
            (ROOT / ".vscode" / "tasks.json").read_text(encoding="utf-8")
        )
        task = task_document["tasks"][0]

        self.assertEqual(task["type"], "process")
        self.assertEqual(
            task["command"],
            "${workspaceFolder}/tools/hatifect-runtime-executor",
        )
        self.assertEqual(task["args"], ["serve"])
        self.assertEqual(task["options"], {"cwd": "${workspaceFolder}"})
        self.assertTrue(task["isBackground"])
        self.assertEqual(task["runOptions"], {"instanceLimit": 1})
        self.assertNotIn("runOn", task["runOptions"])
        wrapper = (ROOT / "tools" / "hatifect-runtime-executor").read_text(encoding="utf-8")
        self.assertIn('supervisor="$TOOLS_DIR/live-harness/user_session_supervisor.py"', wrapper)
        self.assertIn('exec python3 "$supervisor"', wrapper)
        self.assertNotIn('"$executor" serve', wrapper)

    def test_new_executor_start_atomically_replaces_stale_heartbeat_state(self) -> None:
        state_path = self.repository / ".smapi-test" / "user-session-runtime" / "state.json"
        stale = RUNTIME._state_document(
            self.repository,
            str(uuid.uuid4()),
            RUNTIME._timestamp(dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=1)),
            "Stopped",
            "stale",
        )
        stale["heartbeatAtUtc"] = RUNTIME._timestamp(
            dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=1)
        )
        RUNTIME._atomic_write_json(state_path, stale, replace=True)
        replacement = RUNTIME._state_document(
            self.repository,
            str(uuid.uuid4()),
            RUNTIME._timestamp(),
            "Ready",
            "fresh",
        )

        RUNTIME._atomic_write_json(state_path, replacement, replace=True)

        observed = RUNTIME._read_json(state_path)
        self.assertEqual(observed["executorId"], replacement["executorId"])
        self.assertEqual(observed["lifecycleState"], "Ready")
        self.assertNotEqual(observed["heartbeatAtUtc"], stale["heartbeatAtUtc"])

    def test_task_wiring_does_not_launch_an_editor_or_gui_bootstrap(self) -> None:
        task = json.loads(
            (ROOT / ".vscode" / "tasks.json").read_text(encoding="utf-8")
        )["tasks"][0]

        self.assertEqual(
            {task["command"], *task["args"]},
            {"${workspaceFolder}/tools/hatifect-runtime-executor", "serve"},
        )

    def test_executor_publishes_idle_and_running_heartbeats_every_second(self) -> None:
        implementation = MODULE_PATH.read_text(encoding="utf-8")

        self.assertIn("heartbeat_due = now + 1.0", implementation)
        self.assertIn("heartbeat_stop.wait(1.0)", implementation)
        self.assertIn('executor_state("Ready"', implementation)
        self.assertIn('executor_state(\n                                        "Running"', implementation)
        self.assertIn('checkout_sha = trusted["checkoutSha"]', implementation)

    def test_executor_shutdown_requests_direct_runtime_cancellation(self) -> None:
        implementation = MODULE_PATH.read_text(encoding="utf-8")

        self.assertIn('if stop_requested:', implementation)
        self.assertIn(
            '"User-session executor shutdown requested"',
            implementation,
        )

    def test_executor_implementation_has_no_forbidden_bootstrap_transport(self) -> None:
        implementation = MODULE_PATH.read_text(encoding="utf-8")
        supervisor = (
            ROOT / "tools" / "live-harness" / "user_session_supervisor.py"
        ).read_text(encoding="utf-8")
        task = (ROOT / ".vscode" / "tasks.json").read_text(encoding="utf-8")
        wrapper = (ROOT / "tools" / "hatifect-runtime-executor").read_text(encoding="utf-8")
        combined = implementation + supervisor + task + wrapper

        for forbidden in (
            "Terminal.app",
            "LaunchAgent",
            "launchctl",
            "Apple Events",
            "osascript",
            "sudo",
        ):
            self.assertNotIn(forbidden, combined)


if __name__ == "__main__":
    unittest.main()
