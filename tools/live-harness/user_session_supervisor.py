#!/usr/bin/env python3
"""Stable process-lifecycle supervisor for the replaceable user-session runtime worker."""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import fcntl
import hashlib
import json
import os
import re
import signal
import stat
import subprocess
import sys
import time
import traceback
import uuid
from collections import defaultdict, deque
from pathlib import Path
from typing import Any, Callable


PROTOCOL_VERSION = 1
STATE_TYPE = "userSessionExecutorState"
WORKER_RESTART_EXIT = 75
WORKER_PROTOCOL_MISMATCH_EXIT = 76
MAX_DOCUMENT_BYTES = 256 * 1024
HEARTBEAT_STALE_SECONDS = 5.0
POLL_SECONDS = 0.05
PRIVATE_DIRECTORY_MODE = 0o700
PRIVATE_FILE_MODE = 0o600
IDENTICAL_CRASH_LIMIT = 3
CRASH_WINDOW_SECONDS = 60.0
RESTART_BACKOFF_SECONDS = (0.25, 0.5)
WORKER_STARTUP_SECONDS = 10.0
WORKER_SHUTDOWN_SECONDS = 10.0
CHILD_SHUTDOWN_SECONDS = 5.0
WORKER_SOURCE_NAMES = (
    "user_session_runtime.py",
    "direct_runtime.py",
    "save_provisioning.py",
    "validate.py",
    "run_process.py",
    "scenarios.json",
)
PUBLIC_LIFECYCLE_STATES = {
    "Starting",
    "Ready",
    "Running",
    "RestartPending",
    "Restarting",
    "Faulted",
    "Stopping",
    "Stopped",
    "SupervisorRestartRequired",
}
PUBLIC_STATE_FIELDS = {
    "protocolVersion",
    "stateType",
    "executorId",
    "repositoryRoot",
    "checkoutSha",
    "pid",
    "lifecycleState",
    "requestId",
    "scenarioId",
    "startedAtUtc",
    "heartbeatAtUtc",
    "message",
    "supervisorId",
    "supervisorPid",
    "workerId",
    "workerPid",
    "workerGeneration",
    "restartReason",
    "restartCount",
    "workerSourceDigest",
    "acceptedCheckoutSha",
    "heartbeat",
    "activeRequestId",
}
WORKER_STATE_FIELDS = {
    "protocolVersion",
    "stateType",
    "executorId",
    "repositoryRoot",
    "checkoutSha",
    "pid",
    "lifecycleState",
    "requestId",
    "scenarioId",
    "startedAtUtc",
    "heartbeatAtUtc",
    "message",
}


class SupervisorError(ValueError):
    pass


def _utc_now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def _timestamp(value: dt.datetime | None = None) -> str:
    return (value or _utc_now()).isoformat().replace("+00:00", "Z")


def _parse_timestamp(value: Any) -> dt.datetime:
    if not isinstance(value, str) or not value:
        raise SupervisorError("Runtime timestamp must be a non-empty string.")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise SupervisorError(f"Invalid runtime timestamp: {value!r}") from error
    if parsed.tzinfo is None:
        raise SupervisorError("Runtime timestamp must include a UTC offset.")
    return parsed.astimezone(dt.timezone.utc)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _source_set_digest(paths: tuple[Path, ...]) -> str:
    digest = hashlib.sha256()
    for path in paths:
        resolved = path.resolve(strict=True)
        digest.update(resolved.name.encode("utf-8"))
        digest.update(b"\0")
        digest.update(_sha256(resolved).encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def _read_json(path: Path) -> Any:
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode):
            raise SupervisorError(f"Runtime document is not a regular file: {path}")
        if info.st_size > MAX_DOCUMENT_BYTES:
            raise SupervisorError(f"Runtime document exceeds {MAX_DOCUMENT_BYTES} bytes: {path}")
        with os.fdopen(descriptor, "r", encoding="utf-8") as stream:
            descriptor = -1
            return json.load(stream)
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def _atomic_write_json(path: Path, payload: dict[str, Any], *, replace: bool = False) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
    temporary = path.parent / f".{path.name}.{uuid.uuid4()}.tmp"
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(temporary, flags, PRIVATE_FILE_MODE)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            descriptor = -1
            json.dump(payload, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        if not replace and path.exists():
            raise FileExistsError(path)
        os.replace(temporary, path)
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()


def _ensure_private_directory(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode):
        raise SupervisorError(f"Unsafe runtime state directory: {path}")
    os.chmod(path, PRIVATE_DIRECTORY_MODE)
    return path.resolve(strict=True)


def _state_root(repository: Path) -> Path:
    repository = repository.resolve(strict=True)
    smapi_root = repository / ".smapi-test"
    if smapi_root.exists() and smapi_root.is_symlink():
        raise SupervisorError(f"Unsafe runtime state directory: {smapi_root}")
    return _ensure_private_directory(_ensure_private_directory(smapi_root) / "user-session-runtime")


@contextlib.contextmanager
def _exclusive_lock(path: Path, message: str):
    path.parent.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
    with path.open("a+b") as lock:
        os.chmod(path, PRIVATE_FILE_MODE)
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise SupervisorError(message) from error
        try:
            yield
        finally:
            fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def _contained(root: Path, candidate: Path) -> Path:
    resolved_root = root.resolve(strict=True)
    resolved = candidate.resolve(strict=False)
    try:
        resolved.relative_to(resolved_root)
    except ValueError as error:
        raise SupervisorError(f"Path escapes its owned root: {candidate}") from error
    return resolved


def _validate_worker_state(
    document: Any,
    repository: Path,
    worker_id: str,
    worker_pid: int,
    *,
    require_fresh: bool,
) -> dict[str, Any]:
    if not isinstance(document, dict) or set(document) != WORKER_STATE_FIELDS:
        raise SupervisorError("Worker state has an invalid field set.")
    if (
        document["protocolVersion"] != PROTOCOL_VERSION
        or document["stateType"] != STATE_TYPE
        or Path(document["repositoryRoot"]).resolve(strict=True) != repository.resolve(strict=True)
        or document["executorId"] != worker_id
        or document["pid"] != worker_pid
        or document["lifecycleState"] not in PUBLIC_LIFECYCLE_STATES
    ):
        raise SupervisorError("Worker state identity or protocol is invalid.")
    if not re.fullmatch(r"[0-9a-f]{40}", document["checkoutSha"] or ""):
        raise SupervisorError("Worker checkout identity is invalid.")
    if require_fresh:
        age = (_utc_now() - _parse_timestamp(document["heartbeatAtUtc"])).total_seconds()
        if age < -5 or age > HEARTBEAT_STALE_SECONDS:
            raise SupervisorError("Worker heartbeat is stale.")
    return document


class CrashBudget:
    def __init__(self) -> None:
        self._events: dict[str, deque[float]] = defaultdict(deque)

    def record(self, signature: str, now: float) -> tuple[bool, int]:
        events = self._events[signature]
        while events and now - events[0] > CRASH_WINDOW_SECONDS:
            events.popleft()
        events.append(now)
        count = len(events)
        return count < IDENTICAL_CRASH_LIMIT, count


class RuntimeSupervisor:
    def __init__(
        self,
        repository: Path,
        smapi_path: Path,
        *,
        process_factory: Callable[..., subprocess.Popen[Any]] = subprocess.Popen,
        monotonic: Callable[[], float] = time.monotonic,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        self.repository = repository.resolve(strict=True)
        self.smapi_path = smapi_path.resolve(strict=True)
        self.root = _state_root(self.repository)
        self.state_path = self.root / "state.json"
        self.result_path = self.root / "result.json"
        self.request_path = self.root / "request.json"
        self.worker_root = _ensure_private_directory(self.root / "workers")
        self.supervisor_id = str(uuid.uuid4())
        self.supervisor_pid = os.getpid()
        self.started_at = _timestamp()
        self.self_path = Path(__file__).resolve(strict=True)
        self.self_digest = _sha256(self.self_path)
        source_root = self.repository / "tools" / "live-harness"
        self.worker_sources = tuple(source_root / name for name in WORKER_SOURCE_NAMES)
        self.process_factory = process_factory
        self.monotonic = monotonic
        self.sleep = sleep
        self.process: subprocess.Popen[Any] | None = None
        self.worker_id: str | None = None
        self.worker_state_path: Path | None = None
        self.worker_log = None
        self.worker_generation = 0
        self.worker_source_digest: str | None = None
        self.restart_reason: str | None = None
        self.restart_count = 0
        self.accepted_checkout_sha: str | None = None
        self.checkout_sha: str | None = None
        self.scenario_id: str | None = None
        self.active_request_id: str | None = None
        self.last_worker_state: dict[str, Any] | None = None
        self.crashes = CrashBudget()
        self.stop_requested = False
        self.ready_announced = False

    def _public_state(self, lifecycle: str, message: str) -> dict[str, Any]:
        if lifecycle not in PUBLIC_LIFECYCLE_STATES:
            raise SupervisorError(f"Unknown supervisor lifecycle state: {lifecycle}")
        checkout = self.checkout_sha or "0" * 40
        document = {
            "protocolVersion": PROTOCOL_VERSION,
            "stateType": STATE_TYPE,
            "executorId": self.supervisor_id,
            "repositoryRoot": str(self.repository),
            "checkoutSha": checkout,
            "pid": self.supervisor_pid,
            "lifecycleState": lifecycle,
            "requestId": self.active_request_id,
            "scenarioId": self.scenario_id,
            "startedAtUtc": self.started_at,
            "heartbeatAtUtc": _timestamp(),
            "message": message[:2048],
            "supervisorId": self.supervisor_id,
            "supervisorPid": self.supervisor_pid,
            "workerId": self.worker_id,
            "workerPid": self.process.pid if self.process is not None and self.process.poll() is None else None,
            "workerGeneration": self.worker_generation,
            "restartReason": self.restart_reason,
            "restartCount": self.restart_count,
            "workerSourceDigest": self.worker_source_digest,
            "acceptedCheckoutSha": self.accepted_checkout_sha,
            "heartbeat": _timestamp(),
            "activeRequestId": self.active_request_id,
        }
        document["heartbeat"] = document["heartbeatAtUtc"]
        if set(document) != PUBLIC_STATE_FIELDS:
            raise SupervisorError("Internal supervisor status field mismatch.")
        return document

    def publish(self, lifecycle: str, message: str) -> None:
        _atomic_write_json(self.state_path, self._public_state(lifecycle, message), replace=True)

    def start_worker(self, reason: str) -> None:
        if self.process is not None and self.process.poll() is None:
            raise SupervisorError("Refusing to start a second runtime worker.")
        self.worker_generation += 1
        self.worker_id = str(uuid.uuid4())
        self.worker_source_digest = _source_set_digest(self.worker_sources)
        self.worker_state_path = self.worker_root / f"generation-{self.worker_generation}.json"
        worker_lock = self.worker_root / "active.lock"
        log_path = self.worker_root / f"generation-{self.worker_generation}.log"
        self.worker_log = log_path.open("ab", buffering=0)
        command = [
            sys.executable,
            str(self.worker_sources[0]),
            "worker",
            "--repository-root",
            str(self.repository),
            "--smapi-path",
            str(self.smapi_path),
            "--worker-state",
            str(self.worker_state_path),
            "--worker-lock",
            str(worker_lock),
            "--worker-id",
            self.worker_id,
            "--worker-protocol",
            str(PROTOCOL_VERSION),
            "--expected-source-digest",
            self.worker_source_digest,
        ]
        self.restart_reason = reason
        self.last_worker_state = None
        self.process = self.process_factory(
            command,
            cwd=self.repository,
            stdin=subprocess.DEVNULL,
            stdout=self.worker_log,
            stderr=subprocess.STDOUT,
            close_fds=True,
        )

    def observe_worker(self) -> dict[str, Any] | None:
        if self.process is None or self.worker_id is None or self.worker_state_path is None:
            return None
        if not self.worker_state_path.is_file():
            return None
        state = _validate_worker_state(
            _read_json(self.worker_state_path),
            self.repository,
            self.worker_id,
            self.process.pid,
            require_fresh=self.process.poll() is None,
        )
        self.last_worker_state = state
        self.checkout_sha = state["checkoutSha"]
        self.active_request_id = state["requestId"]
        self.scenario_id = state["scenarioId"]
        if state["lifecycleState"] == "Running":
            self.accepted_checkout_sha = state["checkoutSha"]
        return state

    def _close_worker_log(self) -> None:
        if self.worker_log is not None:
            self.worker_log.close()
            self.worker_log = None

    def _read_active_request(self) -> dict[str, Any] | None:
        if self.active_request_id is None or not self.request_path.is_file():
            return None
        try:
            request = _read_json(self.request_path)
        except (OSError, ValueError, json.JSONDecodeError):
            return None
        if not isinstance(request, dict) or request.get("requestId") != self.active_request_id:
            return None
        return request

    def _result_matches(self, request: dict[str, Any]) -> bool:
        if not self.result_path.is_file():
            return False
        try:
            result = _read_json(self.result_path)
        except (OSError, ValueError, json.JSONDecodeError):
            return False
        return isinstance(result, dict) and result.get("requestId") == request.get("requestId")

    def _worker_log_evidence(self) -> tuple[Path, str]:
        path = self.worker_root / f"generation-{self.worker_generation}.log"
        if not path.is_file():
            return path, ""
        with path.open("rb") as stream:
            stream.seek(0, os.SEEK_END)
            length = stream.tell()
            stream.seek(max(0, length - 65536), os.SEEK_SET)
            return path, stream.read().decode("utf-8", errors="replace")

    def _record_worker_failure(self, request: dict[str, Any], exit_code: int, message: str) -> None:
        artifact = _contained(
            self.repository / "artifacts" / "runtime",
            Path(request["artifactDirectory"]),
        )
        diagnostics = _ensure_private_directory(artifact / "diagnostics")
        evidence_path = diagnostics / "worker-failure.json"
        worker_log_path, worker_log_tail = self._worker_log_evidence()
        _atomic_write_json(
            evidence_path,
            {
                "protocolVersion": PROTOCOL_VERSION,
                "requestId": request["requestId"],
                "scenarioId": request["scenarioId"],
                "checkoutSha": request["checkoutSha"],
                "supervisorId": self.supervisor_id,
                "workerId": self.worker_id,
                "workerGeneration": self.worker_generation,
                "workerExitCode": exit_code,
                "workerLogPath": str(worker_log_path),
                "workerLogTail": worker_log_tail,
                "message": message,
                "capturedAtUtc": _timestamp(),
            },
            replace=True,
        )
        result = {
            "protocolVersion": PROTOCOL_VERSION,
            "resultType": "directRuntimeExecutionResult",
            "requestId": request["requestId"],
            "scenarioId": request["scenarioId"],
            "checkoutSha": request["checkoutSha"],
            "state": "Failed",
            "status": "BLOCKED",
            "exitCode": 2,
            "message": f"Runtime worker exited unexpectedly. Raw failure evidence: {evidence_path}",
            "directTransportResultPath": None,
            "completedAtUtc": _timestamp(),
        }
        _atomic_write_json(self.result_path, result, replace=True)

    def _cleanup_owned_child(self, request: dict[str, Any]) -> list[str]:
        errors: list[str] = []
        try:
            artifact = _contained(
                self.repository / "artifacts" / "runtime",
                Path(request["artifactDirectory"]),
            )
            process_path = artifact / "process.json"
            if not process_path.is_file():
                return errors
            process_document = _read_json(process_path)
            if (
                not isinstance(process_document, dict)
                or process_document.get("requestId") != request["requestId"]
                or process_document.get("completedAtUtc") is not None
            ):
                return errors
            pid = process_document.get("pid")
            process_group = process_document.get("processGroup")
            if (
                not isinstance(pid, int)
                or isinstance(pid, bool)
                or not isinstance(process_group, int)
                or isinstance(process_group, bool)
                or pid <= 1
                or process_group != pid
            ):
                raise SupervisorError("Owned child process evidence is incomplete or unsafe.")
            with contextlib.suppress(ProcessLookupError):
                os.killpg(process_group, signal.SIGTERM)
            deadline = self.monotonic() + CHILD_SHUTDOWN_SECONDS
            while self.monotonic() < deadline:
                try:
                    os.kill(pid, 0)
                except ProcessLookupError:
                    break
                except PermissionError:
                    pass
                self.sleep(POLL_SECONDS)
            else:
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(process_group, signal.SIGKILL)
            process_document["supervisorCleanupAtUtc"] = _timestamp()
            _atomic_write_json(process_path, process_document, replace=True)
        except Exception as error:
            errors.append(f"{type(error).__name__}: {error}")
        return errors

    def contain_worker_exit(self, exit_code: int) -> tuple[str, int]:
        request = self._read_active_request()
        cleanup_errors: list[str] = []
        if request is not None:
            cancellation = Path(request["artifactDirectory"]) / ".cancel-requested"
            with contextlib.suppress(FileExistsError, OSError, ValueError):
                _atomic_write_json(
                    cancellation,
                    {
                        "protocolVersion": PROTOCOL_VERSION,
                        "requestId": request["requestId"],
                        "requestedAtUtc": _timestamp(),
                        "reason": "Runtime worker exited unexpectedly",
                    },
                )
            cleanup_errors = self._cleanup_owned_child(request)
            if not self._result_matches(request):
                self._record_worker_failure(
                    request,
                    exit_code,
                    "Unexpected worker exit"
                    + (f"; cleanup errors: {'; '.join(cleanup_errors)}" if cleanup_errors else ""),
                )
        _, worker_log_tail = self._worker_log_evidence()
        log_fingerprint = hashlib.sha256(worker_log_tail.encode("utf-8")).hexdigest()
        signature_material = (
            f"{exit_code}|{self.worker_source_digest}|{log_fingerprint}|{'|'.join(cleanup_errors)}"
        )
        signature = hashlib.sha256(signature_material.encode("utf-8")).hexdigest()
        return signature, len(cleanup_errors)

    def stop_worker(self) -> None:
        if self.process is None or self.process.poll() is not None:
            self._close_worker_log()
            return
        request = self._read_active_request()
        if request is not None:
            cancellation = Path(request["artifactDirectory"]) / ".cancel-requested"
            with contextlib.suppress(FileExistsError, OSError, ValueError):
                _atomic_write_json(
                    cancellation,
                    {
                        "protocolVersion": PROTOCOL_VERSION,
                        "requestId": request["requestId"],
                        "requestedAtUtc": _timestamp(),
                        "reason": "Runtime supervisor shutdown requested",
                    },
                )
        self.process.terminate()
        try:
            self.process.wait(timeout=WORKER_SHUTDOWN_SECONDS)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=CHILD_SHUTDOWN_SECONDS)
        if request is not None:
            self._cleanup_owned_child(request)
        self._close_worker_log()

    def self_update_waits_for_active_worker(self) -> bool:
        return (
            self.process is not None
            and self.process.poll() is None
            and self.last_worker_state is not None
            and self.last_worker_state["lifecycleState"] == "Running"
        )

    def _fault_loop(self, message: str) -> int:
        self.publish("Faulted", message)
        while not self.stop_requested:
            self.publish("Faulted", message)
            self.sleep(1.0)
        self.publish("Stopping", "Stopping a fault-contained runtime supervisor.")
        self.stop_worker()
        self.publish("Stopped", "Fault-contained runtime supervisor stopped cleanly.")
        return 2

    def _supervisor_restart_required(self) -> int:
        self.stop_worker()
        message = (
            "Stable runtime supervisor source changed. Restart the owned executor in its terminal "
            "with `rtk proxy ./tools/hatifect-runtime-executor serve` from this worktree root; "
            "no new requests will be accepted."
        )
        while not self.stop_requested:
            self.publish("SupervisorRestartRequired", message)
            self.sleep(1.0)
        self.publish("Stopping", "Stopping the supervisor awaiting an owner-controlled self-update restart.")
        self.publish("Stopped", "Supervisor stopped; run `rtk proxy ./tools/hatifect-runtime-executor serve` in this worktree to load its update.")
        return 2

    def run(self) -> int:
        if not os.access(self.smapi_path, os.X_OK):
            raise SupervisorError(f"SMAPI executable is unavailable: {self.smapi_path}")
        print("Hatifect user-session runtime executor starting", flush=True)
        self.publish("Starting", "Validating supervisor ownership before worker startup.")
        self.start_worker("Initial worker generation")
        startup_deadline = self.monotonic() + WORKER_STARTUP_SECONDS
        while not self.stop_requested:
            if _sha256(self.self_path) != self.self_digest:
                with contextlib.suppress(OSError, ValueError, json.JSONDecodeError):
                    self.observe_worker()
                worker_live = self.process is not None and self.process.poll() is None
                if self.self_update_waits_for_active_worker():
                    self.publish(
                        "Running",
                        "Stable supervisor update detected; completing the accepted request before manual restart.",
                    )
                    self.sleep(POLL_SECONDS)
                    continue
                if not worker_live and self.process is not None and self.active_request_id is not None:
                    self._close_worker_log()
                    self.contain_worker_exit(self.process.poll() or 0)
                return self._supervisor_restart_required()
            try:
                state = self.observe_worker()
            except (OSError, ValueError, json.JSONDecodeError) as error:
                if self.process is not None and self.process.poll() is None:
                    if "heartbeat is stale" in str(error):
                        self.process.terminate()
                    else:
                        self.publish("Restarting", f"Waiting for valid worker state: {error}")
                        self.sleep(POLL_SECONDS)
                        continue
                state = self.last_worker_state
            exit_code = self.process.poll() if self.process is not None else 2
            if exit_code is None:
                if state is None:
                    if self.monotonic() > startup_deadline:
                        self.process.terminate()
                    else:
                        lifecycle = "Starting" if self.worker_generation == 1 else "Restarting"
                        self.publish(lifecycle, "Waiting for worker startup validation.")
                        self.sleep(POLL_SECONDS)
                        continue
                else:
                    lifecycle = state["lifecycleState"]
                    if lifecycle == "Ready":
                        self.publish("Ready", "Waiting for one atomic workspace request.")
                        if not self.ready_announced:
                            print("Hatifect user-session runtime executor ready", flush=True)
                            self.ready_announced = True
                    elif lifecycle == "Running":
                        self.publish("Running", state["message"])
                    elif lifecycle == "RestartPending":
                        self.publish("RestartPending", state["message"])
                    elif lifecycle == "Faulted":
                        self.publish("Restarting", state["message"])
                    else:
                        mapped = "Starting" if self.worker_generation == 1 else "Restarting"
                        self.publish(mapped, state["message"])
                    self.sleep(POLL_SECONDS)
                    continue
            self._close_worker_log()
            if self.stop_requested:
                break
            if exit_code == WORKER_RESTART_EXIT:
                self.restart_count += 1
                self.publish("RestartPending", "Worker source changed; preparing a replacement generation.")
                self.start_worker("Worker source digest changed")
                startup_deadline = self.monotonic() + WORKER_STARTUP_SECONDS
                continue
            if exit_code == WORKER_PROTOCOL_MISMATCH_EXIT:
                return self._fault_loop("Worker/supervisor protocol mismatch; automatic restart is unsafe.")
            signature, cleanup_error_count = self.contain_worker_exit(exit_code or 0)
            may_restart, identical_count = self.crashes.record(signature, self.monotonic())
            if not may_restart:
                return self._fault_loop(
                    f"Worker crash loop contained after {identical_count} identical exits; "
                    f"last exit={exit_code}, cleanupErrors={cleanup_error_count}."
                )
            self.restart_count += 1
            backoff = RESTART_BACKOFF_SECONDS[min(identical_count - 1, len(RESTART_BACKOFF_SECONDS) - 1)]
            self.publish(
                "Restarting",
                f"Unexpected worker exit {exit_code}; bounded recovery {identical_count}/{IDENTICAL_CRASH_LIMIT}.",
            )
            self.sleep(backoff)
            self.start_worker(f"Unexpected worker exit {exit_code}")
            startup_deadline = self.monotonic() + WORKER_STARTUP_SECONDS
        self.publish("Stopping", "Stopping worker and owned child processes.")
        self.stop_worker()
        self.active_request_id = None
        self.scenario_id = None
        self.publish("Stopped", "Supervisor stopped after bounded owned-process teardown.")
        print("Hatifect user-session runtime executor stopped", flush=True)
        return 0


def serve(args: argparse.Namespace) -> int:
    repository = Path(args.repository_root).resolve(strict=True)
    supervisor = RuntimeSupervisor(repository, Path(args.smapi_path))

    def stop(_signal_number, _frame) -> None:
        supervisor.stop_requested = True

    previous = {name: signal.signal(name, stop) for name in (signal.SIGINT, signal.SIGTERM)}
    try:
        with _exclusive_lock(
            supervisor.root / "serve.lock",
            "Another Hatifect runtime supervisor already owns this workspace.",
        ):
            return supervisor.run()
    except (OSError, ValueError, json.JSONDecodeError) as error:
        with contextlib.suppress(Exception):
            supervisor.publish("Faulted", f"Supervisor startup failed: {error}")
        print(f"Hatifect runtime supervisor: BLOCKED: {error}", file=sys.stderr, flush=True)
        return 2
    except Exception as error:
        with contextlib.suppress(Exception):
            supervisor.publish(
                "Faulted",
                f"Unhandled supervisor failure: {type(error).__name__}: {error}",
            )
            _atomic_write_json(
                supervisor.root / "supervisor-failure.json",
                {
                    "protocolVersion": PROTOCOL_VERSION,
                    "supervisorId": supervisor.supervisor_id,
                    "exceptionType": type(error).__name__,
                    "message": str(error),
                    "traceback": traceback.format_exc()[-131072:],
                    "capturedAtUtc": _timestamp(),
                },
                replace=True,
            )
        print(f"Hatifect runtime supervisor: BLOCKED: {error}", file=sys.stderr, flush=True)
        return 2
    finally:
        with contextlib.suppress(Exception):
            supervisor.stop_worker()
        for name, handler in previous.items():
            signal.signal(name, handler)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", required=True)
    parser.add_argument("--smapi-path", required=True)
    return parser


def main() -> int:
    return serve(build_parser().parse_args())


if __name__ == "__main__":
    raise SystemExit(main())
