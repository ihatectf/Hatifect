import datetime as dt
import fcntl
import importlib.util
import json
import os
import shutil
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "live-harness" / "user_session_supervisor.py"
SPEC = importlib.util.spec_from_file_location("hatifect_user_session_supervisor", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
SUPERVISOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SUPERVISOR)
WORKER_MODULE_PATH = ROOT / "tools" / "live-harness" / "user_session_runtime.py"
WORKER_SPEC = importlib.util.spec_from_file_location(
    "hatifect_user_session_worker_for_supervisor_tests",
    WORKER_MODULE_PATH,
)
assert WORKER_SPEC is not None and WORKER_SPEC.loader is not None
WORKER = importlib.util.module_from_spec(WORKER_SPEC)
WORKER_SPEC.loader.exec_module(WORKER)


class _FakeProcess:
    def __init__(self, pid=41001, exit_code=None):
        self.pid = pid
        self.exit_code = exit_code
        self.terminate_calls = 0
        self.kill_calls = 0
        self.wait_calls = []

    def poll(self):
        return self.exit_code

    def terminate(self):
        self.terminate_calls += 1
        self.exit_code = -15

    def kill(self):
        self.kill_calls += 1
        self.exit_code = -9

    def wait(self, timeout=None):
        self.wait_calls.append(timeout)
        return self.exit_code


class RuntimeSupervisorTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="hatifect-supervisor-test.")
        self.repository = Path(self.temporary.name) / "repository"
        source_root = self.repository / "tools" / "live-harness"
        source_root.mkdir(parents=True)
        for name in SUPERVISOR.WORKER_SOURCE_NAMES:
            source = ROOT / "tools" / "live-harness" / name
            if source.is_file():
                shutil.copy2(source, source_root / name)
            else:
                (source_root / name).write_text(name + "\n", encoding="utf-8")
        self.smapi = Path(self.temporary.name) / "StardewModdingAPI"
        self.smapi.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        self.smapi.chmod(0o700)
        self.spawned = []

        def spawn(*args, **kwargs):
            process = _FakeProcess(pid=41000 + len(self.spawned) + 1)
            self.spawned.append((process, args, kwargs))
            return process

        self.supervisor = SUPERVISOR.RuntimeSupervisor(
            self.repository,
            self.smapi,
            process_factory=spawn,
            sleep=lambda _seconds: None,
        )

    def tearDown(self):
        self.supervisor._close_worker_log()
        self.temporary.cleanup()

    def test_public_status_exposes_supervisor_worker_and_request_identity(self):
        self.supervisor.start_worker("startup")
        self.supervisor.checkout_sha = "a" * 40
        self.supervisor.accepted_checkout_sha = "a" * 40
        self.supervisor.active_request_id = str(uuid.uuid4())
        state = self.supervisor._public_state("Running", "active")

        self.assertEqual(set(state), SUPERVISOR.PUBLIC_STATE_FIELDS)
        self.assertEqual(state["supervisorId"], state["executorId"])
        self.assertEqual(state["supervisorPid"], state["pid"])
        self.assertEqual(state["workerGeneration"], 1)
        self.assertEqual(state["workerPid"], 41001)
        self.assertEqual(state["activeRequestId"], state["requestId"])
        self.assertEqual(state["heartbeat"], state["heartbeatAtUtc"])

    def test_supervisor_never_starts_parallel_workers(self):
        self.supervisor.start_worker("startup")

        with self.assertRaisesRegex(SUPERVISOR.SupervisorError, "second runtime worker"):
            self.supervisor.start_worker("duplicate")

        self.assertEqual(len(self.spawned), 1)

    def test_worker_generation_pins_a_new_digest_after_source_change(self):
        self.supervisor.start_worker("startup")
        first = self.supervisor.worker_source_digest
        self.spawned[0][0].exit_code = SUPERVISOR.WORKER_RESTART_EXIT
        self.supervisor._close_worker_log()
        source = self.repository / "tools" / "live-harness" / "direct_runtime.py"
        source.write_text(source.read_text(encoding="utf-8") + "# generation two\n", encoding="utf-8")

        self.supervisor.start_worker("source drift")

        self.assertEqual(self.supervisor.worker_generation, 2)
        self.assertNotEqual(first, self.supervisor.worker_source_digest)
        self.assertEqual(len(self.spawned), 2)

    def test_supervisor_and_worker_compute_the_same_source_set_digest(self):
        sources = tuple(
            self.repository / "tools" / "live-harness" / name
            for name in SUPERVISOR.WORKER_SOURCE_NAMES
        )
        supervisor_digest = SUPERVISOR._source_set_digest(sources)
        worker_digest = WORKER._source_set_digest(WORKER._capture_digests(sources))

        self.assertEqual(supervisor_digest, worker_digest)

    def test_checkout_identity_can_change_between_ready_requests(self):
        self.supervisor.start_worker("startup")
        self._write_worker_state("Ready", "a" * 40)
        self.supervisor.observe_worker()
        self.assertEqual(self.supervisor.checkout_sha, "a" * 40)
        request_id = str(uuid.uuid4())
        self._write_worker_state("Running", "b" * 40, request_id=request_id)

        self.supervisor.observe_worker()

        self.assertEqual(self.supervisor.checkout_sha, "b" * 40)
        self.assertEqual(self.supervisor.accepted_checkout_sha, "b" * 40)
        self.assertEqual(self.supervisor.active_request_id, request_id)

    def test_supervisor_self_update_waits_only_while_the_running_worker_is_live(self):
        self.supervisor.start_worker("startup")
        self.supervisor.last_worker_state = self._worker_state("Running", "a" * 40)

        self.assertTrue(self.supervisor.self_update_waits_for_active_worker())
        self.spawned[0][0].exit_code = 9
        self.assertFalse(self.supervisor.self_update_waits_for_active_worker())

    def test_source_restart_and_protocol_mismatch_have_distinct_exit_codes(self):
        self.assertEqual(SUPERVISOR.WORKER_RESTART_EXIT, 75)
        self.assertEqual(SUPERVISOR.WORKER_PROTOCOL_MISMATCH_EXIT, 76)
        self.assertNotEqual(SUPERVISOR.WORKER_RESTART_EXIT, SUPERVISOR.WORKER_PROTOCOL_MISMATCH_EXIT)

    def test_three_identical_crashes_exhaust_the_documented_budget(self):
        budget = SUPERVISOR.CrashBudget()

        self.assertEqual(budget.record("same", 1.0), (True, 1))
        self.assertEqual(budget.record("same", 2.0), (True, 2))
        self.assertEqual(budget.record("same", 3.0), (False, 3))

    def test_crash_budget_discards_events_outside_the_sixty_second_window(self):
        budget = SUPERVISOR.CrashBudget()
        budget.record("same", 1.0)
        budget.record("same", 2.0)

        self.assertEqual(budget.record("same", 62.0), (True, 2))

    def test_stale_worker_heartbeat_is_rejected(self):
        self.supervisor.start_worker("startup")
        stale = dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=1)
        self._write_worker_state("Ready", "a" * 40, heartbeat=stale)

        with self.assertRaisesRegex(SUPERVISOR.SupervisorError, "heartbeat is stale"):
            self.supervisor.observe_worker()

    def test_live_singleton_lock_rejects_a_second_supervisor_owner(self):
        lock_path = self.supervisor.root / "serve.lock"
        with lock_path.open("a+b") as owner:
            fcntl.flock(owner.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaisesRegex(SUPERVISOR.SupervisorError, "already owns"):
                with SUPERVISOR._exclusive_lock(lock_path, "already owns"):
                    self.fail("second supervisor must not acquire the ownership lock")

    def test_idle_worker_crash_preserves_the_last_typed_result(self):
        retained = {"requestId": str(uuid.uuid4()), "status": "PASS"}
        SUPERVISOR._atomic_write_json(self.supervisor.result_path, retained)
        self.supervisor.worker_source_digest = "c" * 64

        self.supervisor.contain_worker_exit(9)

        self.assertEqual(SUPERVISOR._read_json(self.supervisor.result_path), retained)

    def test_active_worker_crash_writes_raw_evidence_and_typed_blocked_result(self):
        request = self._active_request()
        self.supervisor.worker_id = str(uuid.uuid4())
        self.supervisor.worker_generation = 2

        self.supervisor.contain_worker_exit(9)

        result = SUPERVISOR._read_json(self.supervisor.result_path)
        evidence = SUPERVISOR._read_json(
            Path(request["artifactDirectory"]) / "diagnostics" / "worker-failure.json"
        )
        self.assertEqual(result["requestId"], request["requestId"])
        self.assertEqual(result["status"], "BLOCKED")
        self.assertEqual(evidence["workerExitCode"], 9)
        self.assertEqual(evidence["workerGeneration"], 2)
        self.assertTrue(evidence["workerLogPath"].endswith("generation-2.log"))
        self.assertIn("workerLogTail", evidence)

    def test_owned_child_cleanup_targets_only_the_recorded_process_group(self):
        request = self._active_request()
        process_path = Path(request["artifactDirectory"]) / "process.json"
        SUPERVISOR._atomic_write_json(
            process_path,
            {"requestId": request["requestId"], "pid": 42001, "processGroup": 42001},
        )

        with mock.patch.object(SUPERVISOR.os, "killpg") as kill_group, mock.patch.object(
            SUPERVISOR.os, "kill", side_effect=ProcessLookupError
        ):
            errors = self.supervisor._cleanup_owned_child(request)

        self.assertEqual(errors, [])
        kill_group.assert_called_once_with(42001, SUPERVISOR.signal.SIGTERM)
        self.assertIn("supervisorCleanupAtUtc", SUPERVISOR._read_json(process_path))

    def test_child_cleanup_refuses_an_unowned_process_group(self):
        request = self._active_request()
        process_path = Path(request["artifactDirectory"]) / "process.json"
        SUPERVISOR._atomic_write_json(
            process_path,
            {"requestId": request["requestId"], "pid": 42001, "processGroup": 42002},
        )

        with mock.patch.object(SUPERVISOR.os, "killpg") as kill_group:
            errors = self.supervisor._cleanup_owned_child(request)

        self.assertEqual(len(errors), 1)
        self.assertIn("unsafe", errors[0])
        kill_group.assert_not_called()

    def test_shutdown_requests_cancellation_and_waits_for_the_worker(self):
        request = self._active_request()
        self.supervisor.start_worker("startup")
        process = self.spawned[0][0]

        self.supervisor.stop_worker()

        cancellation = SUPERVISOR._read_json(
            Path(request["artifactDirectory"]) / ".cancel-requested"
        )
        self.assertEqual(cancellation["requestId"], request["requestId"])
        self.assertEqual(process.terminate_calls, 1)
        self.assertEqual(process.wait_calls, [SUPERVISOR.WORKER_SHUTDOWN_SECONDS])

    def test_worker_protocol_mismatch_is_rejected_before_status_projection(self):
        self.supervisor.start_worker("startup")
        state = self._worker_state("Ready", "a" * 40)
        state["protocolVersion"] += 1
        SUPERVISOR._atomic_write_json(self.supervisor.worker_state_path, state)

        with self.assertRaisesRegex(SUPERVISOR.SupervisorError, "identity or protocol"):
            self.supervisor.observe_worker()

    def test_worker_restart_does_not_delete_the_previous_result(self):
        retained = {"requestId": str(uuid.uuid4()), "status": "FAIL"}
        SUPERVISOR._atomic_write_json(self.supervisor.result_path, retained)
        self.supervisor.start_worker("startup")
        self.spawned[0][0].exit_code = SUPERVISOR.WORKER_RESTART_EXIT
        self.supervisor._close_worker_log()

        self.supervisor.start_worker("source drift")

        self.assertEqual(SUPERVISOR._read_json(self.supervisor.result_path), retained)

    def test_worker_launch_uses_only_the_typed_worker_command(self):
        self.supervisor.start_worker("startup")
        command = self.spawned[0][1][0]

        self.assertEqual(
            Path(command[1]),
            (self.repository / "tools" / "live-harness" / "user_session_runtime.py").resolve(),
        )
        self.assertEqual(command[2], "worker")
        lock_index = command.index("--worker-lock") + 1
        self.assertEqual(Path(command[lock_index]).name, "active.lock")
        for forbidden in ("code", "open", "osascript", "launchctl", "sudo"):
            self.assertNotIn(forbidden, command)

    def _worker_state(self, lifecycle, checkout, *, request_id=None, heartbeat=None):
        return {
            "protocolVersion": SUPERVISOR.PROTOCOL_VERSION,
            "stateType": SUPERVISOR.STATE_TYPE,
            "executorId": self.supervisor.worker_id,
            "repositoryRoot": str(self.repository),
            "checkoutSha": checkout,
            "pid": self.supervisor.process.pid,
            "lifecycleState": lifecycle,
            "requestId": request_id,
            "scenarioId": "semantic.lifecycle" if request_id else None,
            "startedAtUtc": SUPERVISOR._timestamp(),
            "heartbeatAtUtc": SUPERVISOR._timestamp(heartbeat),
            "message": lifecycle,
        }

    def _write_worker_state(self, lifecycle, checkout, *, request_id=None, heartbeat=None):
        SUPERVISOR._atomic_write_json(
            self.supervisor.worker_state_path,
            self._worker_state(lifecycle, checkout, request_id=request_id, heartbeat=heartbeat),
            replace=self.supervisor.worker_state_path.exists(),
        )

    def _active_request(self):
        request_id = str(uuid.uuid4())
        artifact = self.repository / "artifacts" / "runtime" / request_id
        artifact.mkdir(parents=True)
        request = {
            "protocolVersion": SUPERVISOR.PROTOCOL_VERSION,
            "requestId": request_id,
            "scenarioId": "semantic.lifecycle",
            "checkoutSha": "a" * 40,
            "artifactDirectory": str(artifact),
        }
        SUPERVISOR._atomic_write_json(self.supervisor.request_path, request)
        self.supervisor.active_request_id = request_id
        self.supervisor.scenario_id = request["scenarioId"]
        self.supervisor.accepted_checkout_sha = request["checkoutSha"]
        return request


if __name__ == "__main__":
    unittest.main()
