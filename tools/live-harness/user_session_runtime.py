#!/usr/bin/env python3
"""Project-local user-session executor for canonical direct SMAPI acceptance."""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import fcntl
import hashlib
import importlib.util
import json
import os
import re
import signal
import stat
import sys
import threading
import time
import traceback
import uuid
from pathlib import Path
from types import SimpleNamespace
from typing import Any


PROTOCOL_VERSION = 1
REQUEST_TYPE = "executeDirectRuntime"
RESULT_TYPE = "directRuntimeExecutionResult"
STATE_TYPE = "userSessionExecutorState"
MAX_DOCUMENT_BYTES = 256 * 1024
REQUEST_TTL_SECONDS = 30
READY_WAIT_SECONDS = 5.0
RESULT_GRACE_SECONDS = 20.0
HEARTBEAT_STALE_SECONDS = 5.0
POLL_SECONDS = 0.05
PRIVATE_DIRECTORY_MODE = 0o700
PRIVATE_FILE_MODE = 0o600
REQUEST_FIELDS = {
    "protocolVersion",
    "requestType",
    "requestId",
    "repositoryRoot",
    "repositoryDevice",
    "repositoryInode",
    "checkoutSha",
    "kind",
    "scenarioId",
    "isolatedRoot",
    "artifactDirectory",
    "resultPath",
    "savePath",
    "timeoutSeconds",
    "seed",
    "createdAtUtc",
    "expiresAtUtc",
}
RESULT_FIELDS = {
    "protocolVersion",
    "resultType",
    "requestId",
    "scenarioId",
    "checkoutSha",
    "state",
    "status",
    "exitCode",
    "message",
    "directTransportResultPath",
    "completedAtUtc",
}
STATE_FIELDS = {
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
SUPERVISOR_STATE_FIELDS = STATE_FIELDS | {
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
RESULT_STATES = {"Completed", "Failed", "TimedOut", "Cancelled", "Rejected"}
LIFECYCLE_STATES = {"Starting", "Ready", "Running", "Stopping", "Stopped", "Faulted"}
LIFECYCLE_STATES.update({"RestartPending", "Restarting", "SupervisorRestartRequired"})
WORKER_RESTART_EXIT = 75
WORKER_PROTOCOL_MISMATCH_EXIT = 76
PINNED_SOURCE_NAMES = (
    "user_session_runtime.py",
    "direct_runtime.py",
    "save_provisioning.py",
    "validate.py",
    "run_process.py",
    "scenarios.json",
)
_SERVER_SOURCE_DIGESTS: dict[Path, str] | None = None


class UserSessionRuntimeError(ValueError):
    pass


class WorkerRestartRequested(UserSessionRuntimeError):
    pass


def _load_module(name: str, path: Path):
    resolved = path.resolve(strict=True)
    if _SERVER_SOURCE_DIGESTS is not None:
        expected = _SERVER_SOURCE_DIGESTS.get(resolved)
        if expected is not None and _sha256(resolved) != expected:
            raise WorkerRestartRequested(
                f"Runtime worker source changed: {resolved.name}"
            )
    spec = importlib.util.spec_from_file_location(name, resolved)
    if spec is None or spec.loader is None:
        raise UserSessionRuntimeError(f"Unable to load user-session dependency: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _direct_runtime(repository: Path):
    return _load_module(
        "hatifect_user_session_direct_runtime",
        repository / "tools" / "live-harness" / "direct_runtime.py",
    )


def _utc_now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _capture_digests(paths: tuple[Path, ...]) -> dict[Path, str]:
    return {path.resolve(strict=True): _sha256(path.resolve(strict=True)) for path in paths}


def _verify_digests(digests: dict[Path, str]) -> None:
    for path, expected in digests.items():
        try:
            actual = _sha256(path.resolve(strict=True))
        except OSError as error:
            raise WorkerRestartRequested(
                f"Runtime worker source is unavailable: {path.name}"
            ) from error
        if actual != expected:
            raise WorkerRestartRequested(
                f"Runtime worker source changed: {path.name}"
            )


def _capture_server_source_digests(repository: Path) -> dict[Path, str]:
    source_root = repository.resolve(strict=True) / "tools" / "live-harness"
    expected_executor = source_root / "user_session_runtime.py"
    if Path(__file__).resolve(strict=True) != expected_executor.resolve(strict=True):
        raise UserSessionRuntimeError("Executor source is not the canonical repository file.")
    return _capture_digests(tuple(source_root / name for name in PINNED_SOURCE_NAMES))


def _source_set_digest(digests: dict[Path, str]) -> str:
    combined = hashlib.sha256()
    for path, digest in digests.items():
        combined.update(path.name.encode("utf-8"))
        combined.update(b"\0")
        combined.update(digest.encode("ascii"))
        combined.update(b"\n")
    return combined.hexdigest()


def _observe_running_source_drift(
    digests: dict[Path, str],
    restart_pending: threading.Event,
) -> None:
    try:
        _verify_digests(digests)
    except WorkerRestartRequested:
        restart_pending.set()


def _timestamp(value: dt.datetime | None = None) -> str:
    return (value or _utc_now()).isoformat().replace("+00:00", "Z")


def _parse_timestamp(value: Any) -> dt.datetime:
    if not isinstance(value, str) or not value:
        raise UserSessionRuntimeError("User-session timestamp must be a non-empty string.")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise UserSessionRuntimeError(f"Invalid user-session timestamp: {value!r}") from error
    if parsed.tzinfo is None:
        raise UserSessionRuntimeError("User-session timestamp must include a UTC offset.")
    return parsed.astimezone(dt.timezone.utc)


def _read_json(path: Path) -> Any:
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode):
            raise UserSessionRuntimeError(f"User-session document must be a regular file: {path}")
        if info.st_uid != os.getuid():
            raise UserSessionRuntimeError(f"User-session document is not owned by the current user: {path}")
        if info.st_size <= 0 or info.st_size > MAX_DOCUMENT_BYTES:
            raise UserSessionRuntimeError(
                f"User-session document size must be between 1 and {MAX_DOCUMENT_BYTES} bytes: {path}"
            )
        with os.fdopen(descriptor, "r", encoding="utf-8") as stream:
            descriptor = -1
            return json.load(stream)
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def _atomic_write_json(path: Path, payload: dict[str, Any], *, replace: bool = False) -> None:
    encoded = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")
    if len(encoded) > MAX_DOCUMENT_BYTES:
        raise UserSessionRuntimeError(f"User-session document exceeds {MAX_DOCUMENT_BYTES} bytes: {path}")
    path.parent.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
    temporary = path.parent / f".{path.name}.{uuid.uuid4()}.tmp"
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, PRIVATE_FILE_MODE)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        if replace:
            os.replace(temporary, path)
        else:
            try:
                os.link(temporary, path)
            except FileExistsError as error:
                raise UserSessionRuntimeError(
                    f"User-session runtime refuses to overwrite existing state: {path}"
                ) from error
            temporary.unlink()
        directory_descriptor = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)
    finally:
        with contextlib.suppress(FileNotFoundError):
            temporary.unlink()


def _validate_private_directory(path: Path, *, normalize_mode: bool) -> Path:
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
        raise UserSessionRuntimeError(f"Unsafe user-session state directory: {path}")
    if normalize_mode:
        os.chmod(path, PRIVATE_DIRECTORY_MODE)
    return path.resolve(strict=True)


def _ensure_private_directory(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
    return _validate_private_directory(path, normalize_mode=True)


def _state_root(repository: Path, *, create: bool = True) -> Path:
    resolved = repository.resolve(strict=True)
    harness_path = resolved / ".smapi-test"
    harness = (
        _ensure_private_directory(harness_path)
        if create
        else _validate_private_directory(harness_path, normalize_mode=False)
    )
    if harness.parent != resolved:
        raise UserSessionRuntimeError("User-session state root escapes the repository workspace.")
    root_path = harness / "user-session-runtime"
    root = (
        _ensure_private_directory(root_path)
        if create
        else _validate_private_directory(root_path, normalize_mode=False)
    )
    if root.parent != harness:
        raise UserSessionRuntimeError("User-session mailbox escapes the repository workspace.")
    return root


def _canonical_uuid(value: Any) -> str:
    try:
        parsed = uuid.UUID(value) if isinstance(value, str) else None
    except ValueError as error:
        raise UserSessionRuntimeError("User-session requestId must be a canonical UUID.") from error
    if parsed is None or str(parsed) != value:
        raise UserSessionRuntimeError("User-session requestId must be a canonical UUID.")
    return value


def _validate_request(
    document: Any,
    repository: Path,
    *,
    now: dt.datetime | None = None,
) -> dict[str, Any]:
    if not isinstance(document, dict) or set(document) != REQUEST_FIELDS:
        raise UserSessionRuntimeError("User-session request has an invalid field set.")
    if document["protocolVersion"] != PROTOCOL_VERSION or document["requestType"] != REQUEST_TYPE:
        raise UserSessionRuntimeError("User-session request protocolVersion is incompatible.")
    request_id = _canonical_uuid(document["requestId"])
    direct = _direct_runtime(repository)
    resolved_repository, device, inode = direct._repository_identity(repository)
    if Path(document["repositoryRoot"]).resolve(strict=True) != resolved_repository:
        raise UserSessionRuntimeError("User-session request targets a different repository.")
    if document["repositoryDevice"] != device or document["repositoryInode"] != inode:
        raise UserSessionRuntimeError("User-session request repository identity is stale.")
    checkout_sha = direct._repository_head(resolved_repository)
    if document["checkoutSha"] != checkout_sha:
        raise UserSessionRuntimeError("User-session request checkout SHA is stale.")
    if document["kind"] not in {"smoke", "ui"}:
        raise UserSessionRuntimeError("User-session request kind is invalid.")
    validator = _load_module(
        "hatifect_user_session_validator",
        resolved_repository / "tools" / "live-harness" / "validate.py",
    )
    scenario = validator.resolve_scenario(
        validator.load_manifest(resolved_repository / "tools" / "live-harness" / "scenarios.json"),
        document["scenarioId"],
        document["kind"],
    )
    timeout = document["timeoutSeconds"]
    if (
        not isinstance(timeout, int)
        or isinstance(timeout, bool)
        or timeout <= 0
        or timeout > direct.MAX_TIMEOUT_SECONDS
        or timeout > scenario["timeoutSeconds"]
    ):
        raise UserSessionRuntimeError("User-session request timeout exceeds the scenario bound.")
    seed = document["seed"]
    if not isinstance(seed, int) or isinstance(seed, bool) or abs(seed) > 2_147_483_647:
        raise UserSessionRuntimeError("User-session request seed is invalid.")
    created = _parse_timestamp(document["createdAtUtc"])
    expires = _parse_timestamp(document["expiresAtUtc"])
    current = now or _utc_now()
    if expires <= created or (expires - created).total_seconds() > REQUEST_TTL_SECONDS:
        raise UserSessionRuntimeError("User-session request validity interval is invalid.")
    if current < created - dt.timedelta(seconds=5) or current > expires:
        raise UserSessionRuntimeError("User-session request is stale or not yet valid.")
    artifact_root = resolved_repository / "artifacts" / "runtime"
    artifact_candidate = Path(document["artifactDirectory"])
    artifact_candidate_info = artifact_candidate.lstat()
    if stat.S_ISLNK(artifact_candidate_info.st_mode):
        raise UserSessionRuntimeError("User-session artifact directory must not be a symlink.")
    artifact = direct._contained(artifact_root, artifact_candidate)
    if artifact.name != request_id or not artifact.is_dir():
        raise UserSessionRuntimeError("User-session artifact directory does not match requestId.")
    artifact_info = artifact.lstat()
    if stat.S_ISLNK(artifact_info.st_mode) or artifact_info.st_uid != os.getuid():
        raise UserSessionRuntimeError("User-session artifact directory must be a current-user real directory.")
    result = direct._contained(artifact, Path(document["resultPath"]))
    if result != artifact / "result.json":
        raise UserSessionRuntimeError("User-session resultPath must be the canonical result.json.")
    isolated = direct._validate_isolated_root(resolved_repository, Path(document["isolatedRoot"]))
    save_value = document["savePath"]
    if scenario["requiresSave"]:
        if not isinstance(save_value, str) or not save_value:
            raise UserSessionRuntimeError("Allowlisted scenario requires an isolated save.")
        save_root = isolated / "config" / "StardewValley" / "Saves"
        save = direct._contained(save_root, Path(save_value))
        expected_name = f"HatifectHarness_{uuid.UUID(request_id).hex}"
        if save.parent != save_root.resolve(strict=True) or save.name != expected_name:
            raise UserSessionRuntimeError("User-session save path is not its planned isolated working copy.")
    elif save_value is not None:
        raise UserSessionRuntimeError("Scenario without save authority must use null savePath.")
    return document


def _build_request(args: argparse.Namespace) -> dict[str, Any]:
    repository = Path(args.repository_root).resolve(strict=True)
    direct = _direct_runtime(repository)
    resolved_repository, device, inode = direct._repository_identity(repository)
    created = _utc_now()
    request = {
        "protocolVersion": PROTOCOL_VERSION,
        "requestType": REQUEST_TYPE,
        "requestId": Path(args.artifact_directory).name,
        "repositoryRoot": str(resolved_repository),
        "repositoryDevice": device,
        "repositoryInode": inode,
        "checkoutSha": direct._repository_head(resolved_repository),
        "kind": args.kind,
        "scenarioId": args.scenario,
        "isolatedRoot": str(Path(args.isolated_root).resolve()),
        "artifactDirectory": str(Path(args.artifact_directory).resolve()),
        "resultPath": str(Path(args.result).resolve()),
        "savePath": str(Path(args.save_path).resolve()) if args.save_path else None,
        "timeoutSeconds": args.timeout_seconds,
        "seed": args.seed,
        "createdAtUtc": _timestamp(created),
        "expiresAtUtc": _timestamp(created + dt.timedelta(seconds=REQUEST_TTL_SECONDS)),
    }
    return _validate_request(request, resolved_repository, now=created)


def _validate_result(document: Any, request: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(document, dict) or set(document) != RESULT_FIELDS:
        raise UserSessionRuntimeError("User-session result has an invalid field set.")
    if (
        document["protocolVersion"] != PROTOCOL_VERSION
        or document["resultType"] != RESULT_TYPE
        or document["requestId"] != request["requestId"]
        or document["scenarioId"] != request["scenarioId"]
        or document["checkoutSha"] != request["checkoutSha"]
    ):
        raise UserSessionRuntimeError(
            "User-session result protocolVersion, requestId, scenario, or checkout SHA mismatches the request."
        )
    if document["state"] not in RESULT_STATES:
        raise UserSessionRuntimeError("User-session result state is invalid.")
    status = document["status"]
    exit_code = document["exitCode"]
    if status not in {"PASS", "FAIL", "BLOCKED"} or exit_code != {"PASS": 0, "FAIL": 1, "BLOCKED": 2}[status]:
        raise UserSessionRuntimeError("User-session result status and exitCode conflict.")
    direct_path = document["directTransportResultPath"]
    if direct_path is not None:
        artifact = Path(request["artifactDirectory"])
        expected = artifact / "diagnostics" / "transport-result.json"
        direct = _direct_runtime(Path(request["repositoryRoot"]))
        if direct._contained(artifact, Path(direct_path)) != expected or not expected.is_file():
            raise UserSessionRuntimeError("User-session result points outside canonical direct evidence.")
        direct_request = direct._read_json(artifact / "request.json")
        direct_result = direct._validate_response(direct._read_json(expected), direct_request)
        if (
            direct_request["repositoryHead"] != request["checkoutSha"]
            or direct_result["requestId"] != request["requestId"]
            or direct_result["scenarioId"] != request["scenarioId"]
            or direct_result["status"] != status
            or direct_result["exitCode"] != exit_code
        ):
            raise UserSessionRuntimeError("User-session result conflicts with canonical direct evidence.")
    elif status != "BLOCKED" or document["state"] not in {
        "Failed",
        "TimedOut",
        "Cancelled",
        "Rejected",
    }:
        raise UserSessionRuntimeError(
            "Missing canonical direct evidence must remain a non-PASS user-session result."
        )
    _parse_timestamp(document["completedAtUtc"])
    if not isinstance(document["message"], str) or len(document["message"]) > 2048:
        raise UserSessionRuntimeError("User-session result message is invalid.")
    return document


def _executor_result(
    request: dict[str, Any],
    *,
    state: str,
    status: str,
    exit_code: int,
    message: str,
    direct_path: Path | None,
) -> dict[str, Any]:
    result = {
        "protocolVersion": PROTOCOL_VERSION,
        "resultType": RESULT_TYPE,
        "requestId": request["requestId"],
        "scenarioId": request["scenarioId"],
        "checkoutSha": request["checkoutSha"],
        "state": state,
        "status": status,
        "exitCode": exit_code,
        "message": message[:2048],
        "directTransportResultPath": str(direct_path) if direct_path is not None else None,
        "completedAtUtc": _timestamp(),
    }
    if set(result) != RESULT_FIELDS:
        raise UserSessionRuntimeError("Internal user-session result field mismatch.")
    return result


def _request_cancellation(request: dict[str, Any], reason: str) -> None:
    cancellation = Path(request["artifactDirectory"]) / ".cancel-requested"
    with contextlib.suppress(FileExistsError, OSError, UserSessionRuntimeError):
        _atomic_write_json(
            cancellation,
            {
                "protocolVersion": PROTOCOL_VERSION,
                "requestId": request["requestId"],
                "requestedAtUtc": _timestamp(),
                "reason": reason[:512],
            },
        )


def _state_document(
    repository: Path,
    executor_id: str,
    started_at: str,
    lifecycle: str,
    message: str,
    *,
    request_id: str | None = None,
    scenario_id: str | None = None,
    checkout_sha: str | None = None,
) -> dict[str, Any]:
    resolved_checkout_sha = checkout_sha
    if resolved_checkout_sha is None:
        resolved_checkout_sha = _direct_runtime(repository)._repository_head(repository)
    document = {
        "protocolVersion": PROTOCOL_VERSION,
        "stateType": STATE_TYPE,
        "executorId": executor_id,
        "repositoryRoot": str(repository.resolve(strict=True)),
        "checkoutSha": resolved_checkout_sha,
        "pid": os.getpid(),
        "lifecycleState": lifecycle,
        "requestId": request_id,
        "scenarioId": scenario_id,
        "startedAtUtc": started_at,
        "heartbeatAtUtc": _timestamp(),
        "message": message[:2048],
    }
    if set(document) != STATE_FIELDS or lifecycle not in LIFECYCLE_STATES:
        raise UserSessionRuntimeError("Internal user-session executor state is invalid.")
    return document


def _validate_state(document: Any, repository: Path, *, require_live: bool) -> dict[str, Any]:
    if not isinstance(document, dict) or set(document) not in {frozenset(STATE_FIELDS), frozenset(SUPERVISOR_STATE_FIELDS)}:
        raise UserSessionRuntimeError("User-session executor state has an invalid field set.")
    direct = _direct_runtime(repository)
    if (
        document["protocolVersion"] != PROTOCOL_VERSION
        or document["stateType"] != STATE_TYPE
        or Path(document["repositoryRoot"]).resolve(strict=True) != repository.resolve(strict=True)
        or document["lifecycleState"] not in LIFECYCLE_STATES
    ):
        raise UserSessionRuntimeError("User-session executor protocol, repository, or checkout is stale.")
    checkout_sha = document["checkoutSha"]
    if not isinstance(checkout_sha, str) or not re.fullmatch(r"[0-9a-f]{40}", checkout_sha):
        raise UserSessionRuntimeError("User-session executor checkout identity is invalid.")
    if document["lifecycleState"] == "Running" and checkout_sha != direct._repository_head(repository):
        raise UserSessionRuntimeError("Running user-session request checkout is stale.")
    _canonical_uuid(document["executorId"])
    pid = document["pid"]
    if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 1:
        raise UserSessionRuntimeError("User-session executor PID is invalid.")
    if set(document) == SUPERVISOR_STATE_FIELDS:
        if (
            document["supervisorId"] != document["executorId"]
            or document["supervisorPid"] != document["pid"]
            or document["heartbeat"] != document["heartbeatAtUtc"]
            or document["activeRequestId"] != document["requestId"]
            or not isinstance(document["workerGeneration"], int)
            or isinstance(document["workerGeneration"], bool)
            or document["workerGeneration"] < 0
            or not isinstance(document["restartCount"], int)
            or isinstance(document["restartCount"], bool)
            or document["restartCount"] < 0
        ):
            raise UserSessionRuntimeError("Runtime supervisor state aliases or counters are invalid.")
        if document["workerId"] is not None:
            _canonical_uuid(document["workerId"])
        worker_pid = document["workerPid"]
        if worker_pid is not None and (
            not isinstance(worker_pid, int) or isinstance(worker_pid, bool) or worker_pid <= 1
        ):
            raise UserSessionRuntimeError("Runtime worker PID is invalid.")
        for field in ("workerSourceDigest", "acceptedCheckoutSha"):
            value = document[field]
            if value is not None and not re.fullmatch(
                r"[0-9a-f]{64}" if field == "workerSourceDigest" else r"[0-9a-f]{40}",
                value,
            ):
                raise UserSessionRuntimeError(f"Runtime supervisor {field} is invalid.")
    heartbeat = _parse_timestamp(document["heartbeatAtUtc"])
    if require_live:
        heartbeat_age = (_utc_now() - heartbeat).total_seconds()
        if heartbeat_age < -5 or heartbeat_age > HEARTBEAT_STALE_SECONDS:
            raise UserSessionRuntimeError("User-session executor heartbeat is stale.")
        try:
            os.kill(pid, 0)
        except PermissionError:
            # macOS sandbox/process-coalition policy may deny a signal probe for a live
            # same-user VS Code task. A fresh current-user-owned heartbeat remains authoritative.
            pass
        except ProcessLookupError as error:
            raise UserSessionRuntimeError("User-session executor process is unavailable.") from error
    return document


@contextlib.contextmanager
def _exclusive_lock(path: Path, message: str):
    path.parent.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
    with path.open("a+b") as lock:
        os.chmod(path, PRIVATE_FILE_MODE)
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise UserSessionRuntimeError(message) from error
        try:
            yield
        finally:
            fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def _process_request(request: dict[str, Any], repository: Path, smapi_path: Path) -> dict[str, Any]:
    request = _validate_request(request, repository)
    direct = _direct_runtime(repository)
    metadata = direct._direct_metadata(repository, Path(request["isolatedRoot"]), smapi_path)
    if metadata["repositoryHead"] != request["checkoutSha"]:
        raise UserSessionRuntimeError("Executor checkout changed after request validation.")
    args = SimpleNamespace(
        repository_root=str(repository),
        smapi_path=str(smapi_path),
        kind=request["kind"],
        scenario=request["scenarioId"],
        isolated_root=request["isolatedRoot"],
        artifact_directory=request["artifactDirectory"],
        result=request["resultPath"],
        save_path=request["savePath"],
        timeout_seconds=request["timeoutSeconds"],
        seed=request["seed"],
    )
    exit_code = direct.direct(args)
    direct_path = Path(request["artifactDirectory"]) / "diagnostics" / "transport-result.json"
    if not direct_path.is_file():
        raise UserSessionRuntimeError("Canonical direct transport produced no typed result.")
    direct_request = direct._read_json(Path(request["artifactDirectory"]) / "request.json")
    direct_result = direct._validate_response(direct._read_json(direct_path), direct_request)
    if (
        direct_request["repositoryHead"] != request["checkoutSha"]
        or direct_result["requestId"] != request["requestId"]
        or direct_result["scenarioId"] != request["scenarioId"]
        or direct_result["exitCode"] != exit_code
    ):
        raise UserSessionRuntimeError("Canonical direct transport result identity mismatch.")
    return _executor_result(
        request,
        state=direct_result["state"],
        status=direct_result["status"],
        exit_code=exit_code,
        message=direct_result["message"],
        direct_path=direct_path,
    )


def _process_accepted_request(
    request: dict[str, Any],
    repository: Path,
    smapi_path: Path,
) -> dict[str, Any]:
    try:
        result = _process_request(request, repository, smapi_path)
        _validate_result(result, request)
        return result
    except Exception as error:
        evidence_path = Path(request["artifactDirectory"]) / "diagnostics" / "executor-failure.json"
        evidence_error: Exception | None = None
        try:
            _atomic_write_json(
                evidence_path,
                {
                    "protocolVersion": PROTOCOL_VERSION,
                    "requestId": request["requestId"],
                    "scenarioId": request["scenarioId"],
                    "checkoutSha": request["checkoutSha"],
                    "exceptionType": type(error).__name__,
                    "message": str(error),
                    "traceback": traceback.format_exc()[-131072:],
                    "capturedAtUtc": _timestamp(),
                },
                replace=True,
            )
        except Exception as record_error:
            evidence_error = record_error
        message = (
            "User-session executor failed after accepting the request: "
            f"{type(error).__name__}: {error}."
        )
        if evidence_error is None:
            message += f" Raw failure evidence: {evidence_path}"
        else:
            message += (
                " Raw failure evidence could not be persisted: "
                f"{type(evidence_error).__name__}: {evidence_error}"
            )
        return _executor_result(
            request,
            state="Failed",
            status="BLOCKED",
            exit_code=2,
            message=message,
            direct_path=None,
        )


def worker(args: argparse.Namespace) -> int:
    global _SERVER_SOURCE_DIGESTS
    repository = Path(args.repository_root).resolve(strict=True)
    smapi_path = Path(args.smapi_path).resolve(strict=True)
    root = _state_root(repository)
    request_path = root / "request.json"
    result_path = root / "result.json"
    state_path = Path(args.worker_state).resolve(strict=False)
    worker_lock = Path(args.worker_lock).resolve(strict=False)
    worker_root = (root / "workers").resolve(strict=True)
    if state_path.parent != worker_root or worker_lock != worker_root / "active.lock":
        raise UserSessionRuntimeError("Worker state and ownership lock must use the supervisor-owned worker root.")
    executor_id = _canonical_uuid(args.worker_id)
    started_at = _timestamp()
    if _SERVER_SOURCE_DIGESTS is not None:
        raise UserSessionRuntimeError("User-session executor source pins are already active.")
    _SERVER_SOURCE_DIGESTS = _capture_server_source_digests(repository)
    if args.worker_protocol != PROTOCOL_VERSION:
        raise UserSessionRuntimeError(
            f"Worker protocol {args.worker_protocol} does not match runtime protocol {PROTOCOL_VERSION}."
        )
    if _source_set_digest(_SERVER_SOURCE_DIGESTS) != args.expected_source_digest:
        raise UserSessionRuntimeError("Worker source digest does not match the supervisor launch contract.")
    stop_requested = False
    checkout_sha: str | None = None

    def stop(_signal_number, _frame) -> None:
        nonlocal stop_requested
        stop_requested = True

    previous = {name: signal.signal(name, stop) for name in (signal.SIGINT, signal.SIGTERM)}
    print(f"Hatifect runtime worker {executor_id} starting", flush=True)
    try:
        with _exclusive_lock(
            worker_lock,
            "Another Hatifect runtime worker already owns this generation.",
        ):
            direct = _direct_runtime(repository)
            if not os.access(smapi_path, os.X_OK):
                raise UserSessionRuntimeError(f"SMAPI executable is unavailable: {smapi_path}")
            direct._repository_identity(repository)
            checkout_sha = direct._repository_head(repository)
            if not re.fullmatch(r"[0-9a-f]{40}", checkout_sha):
                raise UserSessionRuntimeError("Repository checkout SHA is unavailable during executor startup.")

            def executor_state(
                lifecycle: str,
                message: str,
                *,
                request_id: str | None = None,
                scenario_id: str | None = None,
            ) -> dict[str, Any]:
                return _state_document(
                    repository,
                    executor_id,
                    started_at,
                    lifecycle,
                    message,
                    request_id=request_id,
                    scenario_id=scenario_id,
                    checkout_sha=checkout_sha,
                )

            _atomic_write_json(
                state_path,
                executor_state("Ready", "Waiting for one atomic workspace request."),
                replace=True,
            )
            print(f"Hatifect runtime worker {executor_id} ready", flush=True)
            last_digest: str | None = None
            if request_path.is_file() and result_path.is_file():
                try:
                    existing_request = _read_json(request_path)
                    _validate_result(_read_json(result_path), existing_request)
                    last_digest = hashlib.sha256(request_path.read_bytes()).hexdigest()
                except (OSError, TypeError, ValueError, json.JSONDecodeError):
                    pass
            heartbeat_due = 0.0
            while not stop_requested:
                now = time.monotonic()
                if now >= heartbeat_due:
                    _verify_digests(_SERVER_SOURCE_DIGESTS)
                    _atomic_write_json(
                        state_path,
                        executor_state("Ready", "Waiting for one atomic workspace request."),
                        replace=True,
                    )
                    heartbeat_due = now + 1.0
                if not request_path.is_file():
                    time.sleep(POLL_SECONDS)
                    continue
                try:
                    content = request_path.read_bytes()
                    digest = hashlib.sha256(content).hexdigest()
                except OSError:
                    time.sleep(POLL_SECONDS)
                    continue
                if digest == last_digest:
                    time.sleep(POLL_SECONDS)
                    continue
                last_digest = digest
                request: Any = None
                restart_pending = threading.Event()
                try:
                    _verify_digests(_SERVER_SOURCE_DIGESTS)
                    request = _read_json(request_path)
                    trusted = _validate_request(request, repository)
                except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
                    if not isinstance(request, dict):
                        raise
                    _canonical_uuid(request.get("requestId"))
                    scenario_id = request.get("scenarioId")
                    request_checkout_sha = request.get("checkoutSha")
                    if not isinstance(scenario_id, str) or not scenario_id or not re.fullmatch(r"[0-9a-f]{40}", request_checkout_sha or ""):
                        raise
                    result = _executor_result(
                        {
                            "requestId": request["requestId"],
                            "scenarioId": scenario_id,
                            "checkoutSha": request_checkout_sha,
                        },
                        state="Rejected",
                        status="BLOCKED",
                        exit_code=2,
                        message=f"User-session executor rejected the request: {error}",
                        direct_path=None,
                    )
                else:
                    checkout_sha = trusted["checkoutSha"]
                    _atomic_write_json(
                        state_path,
                        executor_state(
                            "Running",
                            "Executing the canonical direct-process transport.",
                            request_id=trusted["requestId"],
                            scenario_id=trusted["scenarioId"],
                        ),
                        replace=True,
                    )
                    heartbeat_stop = threading.Event()
                    heartbeat_errors: list[Exception] = []

                    def publish_running_heartbeat() -> None:
                        try:
                            while not heartbeat_stop.wait(1.0):
                                _observe_running_source_drift(
                                    _SERVER_SOURCE_DIGESTS,
                                    restart_pending,
                                )
                                if stop_requested:
                                    _request_cancellation(
                                        trusted,
                                        "User-session executor shutdown requested",
                                    )
                                _atomic_write_json(
                                    state_path,
                                    executor_state(
                                        "Running",
                                        "Executing the canonical direct-process transport.",
                                        request_id=trusted["requestId"],
                                        scenario_id=trusted["scenarioId"],
                                    ),
                                    replace=True,
                                )
                        except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
                            heartbeat_errors.append(error)
                            _request_cancellation(
                                trusted,
                                "User-session heartbeat failure",
                            )
                            heartbeat_stop.set()

                    heartbeat = threading.Thread(
                        target=publish_running_heartbeat,
                        name="hatifect-runtime-heartbeat",
                        daemon=True,
                    )
                    heartbeat.start()
                    try:
                        result = _process_accepted_request(trusted, repository, smapi_path)
                    finally:
                        heartbeat_stop.set()
                        heartbeat.join(timeout=2.0)
                        if heartbeat.is_alive():
                            raise UserSessionRuntimeError(
                                "User-session executor heartbeat did not stop within its bounded deadline."
                            )
                        if heartbeat_errors:
                            raise UserSessionRuntimeError(
                                f"User-session executor heartbeat failed: {heartbeat_errors[0]}"
                            )
                _atomic_write_json(result_path, result, replace=True)
                cancelled = result["state"] == "Cancelled"
                if restart_pending.is_set():
                    _atomic_write_json(
                        state_path,
                        executor_state(
                            "RestartPending",
                            "Accepted request completed; worker source replacement is pending.",
                        ),
                        replace=True,
                    )
                    return WORKER_RESTART_EXIT
                _atomic_write_json(
                    state_path,
                    executor_state(
                        "Ready",
                        f"Published {result['status']} for {result['requestId']}.",
                    ),
                    replace=True,
                )
                if cancelled:
                    stop_requested = True
            _atomic_write_json(
                state_path,
                executor_state("Stopped", "Executor stopped after bounded owned-process teardown."),
                replace=True,
            )
            print(f"Hatifect runtime worker {executor_id} stopped", flush=True)
            return 0
    except WorkerRestartRequested as error:
        with contextlib.suppress(Exception):
            _atomic_write_json(
                state_path,
                _state_document(
                    repository,
                    executor_id,
                    started_at,
                    "RestartPending",
                    str(error),
                    checkout_sha=checkout_sha,
                ),
                replace=True,
            )
        print(f"Hatifect runtime worker restart pending: {error}", flush=True)
        return WORKER_RESTART_EXIT
    except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        with contextlib.suppress(Exception):
            _atomic_write_json(
                state_path,
                _state_document(
                    repository,
                    executor_id,
                    started_at,
                    "Faulted",
                    str(error),
                    checkout_sha=checkout_sha,
                ),
                replace=True,
            )
        print(f"Hatifect runtime worker: BLOCKED: {error}", file=sys.stderr, flush=True)
        if "protocol" in str(error).lower() or "source digest" in str(error).lower():
            return WORKER_PROTOCOL_MISMATCH_EXIT
        return 2
    finally:
        _SERVER_SOURCE_DIGESTS = None
        for name, handler in previous.items():
            signal.signal(name, handler)


def _ready_state(repository: Path, state_path: Path) -> dict[str, Any]:
    state = _validate_state(_read_json(state_path), repository, require_live=True)
    if state["lifecycleState"] != "Ready":
        raise UserSessionRuntimeError(
            f"User-session executor is not ready (state={state['lifecycleState']})."
        )
    return state


def submit(args: argparse.Namespace) -> int:
    repository = Path(args.repository_root).resolve(strict=True)
    root = _state_root(repository)
    state_path = root / "state.json"
    request_path = root / "request.json"
    result_path = root / "result.json"
    request: dict[str, Any] | None = None
    published = False
    try:
        request = _build_request(args)
        with _exclusive_lock(
            root / "submit.lock",
            "Another client already owns the user-session runtime request slot.",
        ):
            ready_deadline = time.monotonic() + READY_WAIT_SECONDS
            while True:
                try:
                    _ready_state(repository, state_path)
                    break
                except (FileNotFoundError, OSError, TypeError, ValueError, json.JSONDecodeError) as error:
                    if time.monotonic() >= ready_deadline:
                        raise UserSessionRuntimeError(
                            "User-session executor is unavailable; open this workspace in VS Code and allow the folderOpen task."
                        ) from error
                    time.sleep(POLL_SECONDS)
            _atomic_write_json(request_path, request, replace=True)
            published = True
            deadline = time.monotonic() + request["timeoutSeconds"] + RESULT_GRACE_SECONDS
            state_check_due = 0.0
            while time.monotonic() < deadline:
                if result_path.is_file():
                    try:
                        result = _read_json(result_path)
                    except (OSError, TypeError, ValueError, json.JSONDecodeError):
                        result = None
                    if isinstance(result, dict) and result.get("requestId") == request["requestId"]:
                        validated = _validate_result(result, request)
                        print(
                            f"User-session result: state={validated['state']} "
                            f"status={validated['status']} requestId={validated['requestId']}",
                            flush=True,
                        )
                        return validated["exitCode"]
                now = time.monotonic()
                if now >= state_check_due:
                    state = _validate_state(_read_json(state_path), repository, require_live=True)
                    if state["lifecycleState"] not in {"Ready", "Running"}:
                        raise UserSessionRuntimeError(
                            f"User-session executor stopped before publishing a result (state={state['lifecycleState']})."
                        )
                    if state["lifecycleState"] == "Running" and state["requestId"] != request["requestId"]:
                        raise UserSessionRuntimeError(
                            "User-session executor is running a different request identity."
                        )
                    state_check_due = now + 1.0
                time.sleep(POLL_SECONDS)
            _request_cancellation(request, "User-session client timeout")
            raise UserSessionRuntimeError(
                "User-session executor did not publish a matching typed result before the bounded client deadline."
            )
    except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        if published and request is not None:
            _request_cancellation(request, "User-session client stopped before a matching result")
        print(f"Hatifect user-session runtime client: BLOCKED: {error}", file=sys.stderr)
        return 2


def status(args: argparse.Namespace) -> int:
    repository = Path(args.repository_root).resolve(strict=True)
    root = _state_root(repository, create=False)
    try:
        document = _validate_state(_read_json(root / "state.json"), repository, require_live=True)
        print(json.dumps(document, indent=2, sort_keys=True))
        if document["lifecycleState"] in {"Ready", "Running"}:
            print("RESULT: PASS")
            return 0
        print("RESULT: BLOCKED")
        return 2
    except (FileNotFoundError, OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        print(f"BLOCKED: {error}", file=sys.stderr)
        print("RESULT: BLOCKED", file=sys.stderr)
        return 2


def doctor(args: argparse.Namespace) -> int:
    repository = Path(args.repository_root).resolve(strict=True)
    failures: list[str] = []
    task_path = repository / ".vscode" / "tasks.json"
    try:
        tasks = json.loads(task_path.read_text(encoding="utf-8"))
        matches = [
            task
            for task in tasks.get("tasks", [])
            if task.get("label") == "Hatifect: User-session runtime executor"
        ]
        if len(matches) != 1:
            raise UserSessionRuntimeError("Expected exactly one project-local executor task.")
        task = matches[0]
        if (
            task.get("type") != "process"
            or task.get("command") != "${workspaceFolder}/tools/hatifect-runtime-executor"
            or task.get("args") != ["serve"]
            or task.get("options") != {"cwd": "${workspaceFolder}"}
            or task.get("isBackground") is not True
            or task.get("runOptions", {}).get("runOn") != "folderOpen"
            or task.get("runOptions", {}).get("instanceLimit") != 1
        ):
            raise UserSessionRuntimeError("VS Code executor task is not the required folderOpen singleton process task.")
        print(f"PASS: VS Code folderOpen task: {task_path}")
    except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        failures.append(str(error))
    try:
        direct = _direct_runtime(repository)
        direct._direct_metadata(repository, Path(args.isolated_root), Path(args.smapi_path))
        print("PASS: canonical direct-process transport metadata and SMAPI executable")
    except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        failures.append(str(error))
    try:
        _ready_state(repository, _state_root(repository) / "state.json")
        print("PASS: user-session executor is live and ready to bind the current request checkout")
    except (FileNotFoundError, OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        failures.append(str(error))
    if failures:
        for failure in failures:
            print(f"BLOCKED: {failure}", file=sys.stderr)
        print("RESULT: BLOCKED", file=sys.stderr)
        return 2
    print("RESULT: PASS")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    worker_parser = subparsers.add_parser("worker")
    worker_parser.add_argument("--repository-root", required=True)
    worker_parser.add_argument("--smapi-path", required=True)
    worker_parser.add_argument("--worker-state", required=True)
    worker_parser.add_argument("--worker-lock", required=True)
    worker_parser.add_argument("--worker-id", required=True)
    worker_parser.add_argument("--worker-protocol", type=int, required=True)
    worker_parser.add_argument("--expected-source-digest", required=True)
    worker_parser.set_defaults(handler=worker)
    submit_parser = subparsers.add_parser("submit")
    submit_parser.add_argument("--repository-root", required=True)
    submit_parser.add_argument("--kind", choices=("smoke", "ui"), required=True)
    submit_parser.add_argument("--scenario", required=True)
    submit_parser.add_argument("--isolated-root", required=True)
    submit_parser.add_argument("--artifact-directory", required=True)
    submit_parser.add_argument("--result", required=True)
    submit_parser.add_argument("--save-path", default=None)
    submit_parser.add_argument("--timeout-seconds", type=int, required=True)
    submit_parser.add_argument("--seed", type=int, default=0)
    submit_parser.set_defaults(handler=submit)
    status_parser = subparsers.add_parser("status")
    status_parser.add_argument("--repository-root", required=True)
    status_parser.set_defaults(handler=status)
    doctor_parser = subparsers.add_parser("doctor")
    doctor_parser.add_argument("--repository-root", required=True)
    doctor_parser.add_argument("--smapi-path", required=True)
    doctor_parser.add_argument("--isolated-root", required=True)
    doctor_parser.set_defaults(handler=doctor)
    return parser


def main() -> int:
    try:
        args = build_parser().parse_args()
        return args.handler(args)
    except (OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        print(f"Hatifect user-session runtime: BLOCKED: {error}", file=sys.stderr)
        if "args" in locals() and args.command == "worker" and (
            "protocol" in str(error).lower() or "source digest" in str(error).lower()
        ):
            return WORKER_PROTOCOL_MISMATCH_EXIT
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
