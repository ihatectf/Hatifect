#!/usr/bin/env python3
"""Canonical owned direct-process transport for macOS acceptance runs."""

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
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from types import SimpleNamespace
from typing import Any


PROTOCOL_VERSION = 2
HARNESS_PROTOCOL_VERSION = 1
TRANSPORT_ID = "hatifect-direct-process-v1"
REQUEST_TYPE = "runScenario"
ENVIRONMENT_ID = "isolated-smapi-v1"
MAX_TIMEOUT_SECONDS = 7200
REQUEST_TTL_SECONDS = 30
MAX_DOCUMENT_BYTES = 256 * 1024
PRIVATE_DIRECTORY_MODE = 0o700
PRIVATE_FILE_MODE = 0o600
SAVED_CRASH_SCENARIO = "flow.chest.crash-after-save"
DELIVERED_CRASH_SCENARIO = "flow.chest.crash-after-delivery"
UNSAVED_EXTRACTION_CRASH_SCENARIO = "flow.chest.crash-after-unsaved-extraction"
UNSAVED_DELIVERY_CRASH_SCENARIO = "flow.chest.crash-after-unsaved-delivery"
RETURNED_CRASH_SCENARIO = "flow.chest.crash-after-return"
SAVED_CRASH_BOUNDARIES = {
    RETURNED_CRASH_SCENARIO: ("saved-returned", 4),
    SAVED_CRASH_SCENARIO: ("saved-in-transit", 1),
    DELIVERED_CRASH_SCENARIO: ("saved-delivered", 2),
    UNSAVED_EXTRACTION_CRASH_SCENARIO: ("saved-reserved-unsaved-extraction", 1),
    UNSAVED_DELIVERY_CRASH_SCENARIO: ("saved-in-transit-unsaved-delivery", 1),
}
LIFECYCLE_STATES = {
    "Accepted",
    "Launching",
    "Running",
    "Completed",
    "Failed",
    "TimedOut",
    "Cancelled",
    "Rejected",
}
FINAL_STATES = {"Completed", "Failed", "TimedOut", "Cancelled", "Rejected"}
STATE_TRANSITIONS = {
    None: {"Accepted", "Rejected"},
    "Accepted": {"Launching", "Cancelled", "Rejected"},
    "Launching": {"Running", "Failed", "TimedOut", "Cancelled"},
    "Running": {"Completed", "Failed", "TimedOut", "Cancelled"},
}
FAILURE_KINDS = {
    None,
    "ScenarioFailure",
    "ProductRuntimeFailure",
    "BrokerFailure",
    "LaunchFailure",
    "Timeout",
    "Cancelled",
    "LocalInfrastructure",
    "RejectedRequest",
}
REQUEST_FIELDS = {
    "protocolVersion",
    "requestType",
    "requestId",
    "repositoryRoot",
    "repositoryDevice",
    "repositoryInode",
    "repositoryHead",
    "environmentId",
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
RESPONSE_FIELDS = {
    "protocolVersion",
    "requestId",
    "scenarioId",
    "state",
    "status",
    "exitCode",
    "failureKind",
    "resultPath",
    "startedAtUtc",
    "completedAtUtc",
    "message",
    "artifacts",
}


class DirectRuntimeError(ValueError):
    pass


def _exit_code(status: str) -> int:
    try:
        return {"PASS": 0, "FAIL": 1, "BLOCKED": 2}[status]
    except KeyError as error:
        raise DirectRuntimeError(f"Unknown authoritative result status: {status!r}") from error


def _load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise DirectRuntimeError(f"Unable to load direct-runtime dependency: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _utc_now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def _timestamp(value: dt.datetime | None = None) -> str:
    return (value or _utc_now()).isoformat().replace("+00:00", "Z")


def _parse_timestamp(value: Any) -> dt.datetime:
    if not isinstance(value, str) or not value:
        raise DirectRuntimeError("Direct runtime timestamp must be a non-empty string.")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise DirectRuntimeError(f"Invalid direct runtime timestamp: {value!r}") from error
    if parsed.tzinfo is None:
        raise DirectRuntimeError("Direct runtime timestamp must include a UTC offset.")
    return parsed.astimezone(dt.timezone.utc)


def _read_json(path: Path) -> Any:
    size = path.stat().st_size
    if size <= 0 or size > MAX_DOCUMENT_BYTES:
        raise DirectRuntimeError(
            f"Direct runtime JSON document size must be between 1 and {MAX_DOCUMENT_BYTES} bytes: {path}"
        )
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def _atomic_write_json(path: Path, payload: dict[str, Any], *, replace: bool = False) -> None:
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.exists() and not replace:
        raise DirectRuntimeError(f"Direct runtime transport refuses to overwrite existing state: {path}")
    encoded = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")
    if len(encoded) > MAX_DOCUMENT_BYTES:
        raise DirectRuntimeError(f"Direct runtime JSON document exceeds {MAX_DOCUMENT_BYTES} bytes: {path}")
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        os.fchmod(descriptor, 0o600)
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
                raise DirectRuntimeError(f"Direct runtime state appeared concurrently: {path}") from error
            temporary.unlink()
        directory_descriptor = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _lstat(path: Path) -> os.stat_result | None:
    try:
        return os.lstat(path)
    except FileNotFoundError:
        return None



def _ensure_private_directory(path: Path) -> Path:
    info = _lstat(path)
    if info is None:
        path.mkdir(parents=True, exist_ok=False, mode=PRIVATE_DIRECTORY_MODE)
        info = _lstat(path)
    assert info is not None
    if stat.S_ISLNK(info.st_mode):
        raise DirectRuntimeError(f"Direct runtime directory must not be a symlink: {path}")
    if info.st_uid != os.getuid():
        raise DirectRuntimeError(f"Direct runtime directory is not owned by the current user: {path}")
    if not stat.S_ISDIR(info.st_mode):
        raise DirectRuntimeError(f"Direct runtime directory path is not a directory: {path}")
    flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    try:
        observed = os.fstat(descriptor)
        if (
            observed.st_dev != info.st_dev
            or observed.st_ino != info.st_ino
            or observed.st_uid != os.getuid()
            or not stat.S_ISDIR(observed.st_mode)
        ):
            raise DirectRuntimeError(f"Direct runtime directory changed during validation: {path}")
        if stat.S_IMODE(observed.st_mode) != PRIVATE_DIRECTORY_MODE:
            os.fchmod(descriptor, PRIVATE_DIRECTORY_MODE)
        verified = os.fstat(descriptor)
        if stat.S_IMODE(verified.st_mode) != PRIVATE_DIRECTORY_MODE:
            raise DirectRuntimeError(f"Direct runtime directory must have mode 0700: {path}")
    finally:
        os.close(descriptor)
    return path.resolve(strict=True)



def _repository_identity(root: Path) -> tuple[Path, int, int]:
    resolved = root.resolve(strict=True)
    info = resolved.stat()
    if not (resolved / "AGENTS.md").is_file() or not (
        resolved / "tools" / "live-harness" / "scenarios.json"
    ).is_file():
        raise DirectRuntimeError(f"Path is not a Hatifect repository root: {resolved}")
    return resolved, int(info.st_dev), int(info.st_ino)


def _repository_head(root: Path) -> str:
    completed = subprocess.run(
        ["/usr/bin/git", "-C", str(root), "rev-parse", "HEAD"],
        capture_output=True,
        text=True,
        check=False,
    )
    head = completed.stdout.strip()
    if completed.returncode != 0 or not re.fullmatch(r"[0-9a-f]{40}", head):
        detail = completed.stderr.strip() or "git rev-parse HEAD failed"
        raise DirectRuntimeError(f"Unable to resolve the repository HEAD: {detail}")
    return head


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _validate_transport_sources(metadata: dict[str, Any]) -> None:
    transport = Path(metadata["transportExecutable"]).resolve(strict=True)
    validator = Path(metadata["validatorExecutable"]).resolve(strict=True)
    supervisor = Path(metadata["processSupervisorExecutable"]).resolve(strict=True)
    manifest = Path(metadata["scenarioManifest"]).resolve(strict=True)
    save_provisioner = Path(metadata["saveProvisionerExecutable"]).resolve(strict=True)
    if _sha256(transport) != metadata["transportSha256"]:
        raise DirectRuntimeError("Direct transport source changed during request construction.")
    if _sha256(validator) != metadata["validatorSha256"]:
        raise DirectRuntimeError("Direct transport validator changed during request construction.")
    if _sha256(supervisor) != metadata["processSupervisorSha256"]:
        raise DirectRuntimeError("Process supervisor changed during request construction.")
    if _sha256(manifest) != metadata["scenarioManifestSha256"]:
        raise DirectRuntimeError("Scenario allowlist changed during request construction.")
    if _sha256(save_provisioner) != metadata["saveProvisionerSha256"]:
        raise DirectRuntimeError("Isolated-save provisioner changed during request construction.")


def _validate_repository_transport_sources(metadata: dict[str, Any]) -> None:
    repository = Path(metadata["repositoryRoot"]).resolve(strict=True)
    expected = {
        "direct_runtime.py": metadata["transportSha256"],
        "validate.py": metadata["validatorSha256"],
        "run_process.py": metadata["processSupervisorSha256"],
        "scenarios.json": metadata["scenarioManifestSha256"],
        "save_provisioning.py": metadata["saveProvisionerSha256"],
    }
    for name, digest in expected.items():
        source = repository / "tools" / "live-harness" / name
        if _sha256(source) != digest:
            raise DirectRuntimeError(f"Repository direct-runtime component changed: {name}.")


def _contained(root: Path, candidate: Path) -> Path:
    resolved_root = root.resolve(strict=True)
    resolved = candidate.resolve(strict=False)
    try:
        resolved.relative_to(resolved_root)
    except ValueError as error:
        raise DirectRuntimeError(f"Path escapes required root {resolved_root}: {resolved}") from error
    return resolved


def _validate_isolated_root(repository: Path, candidate: Path) -> Path:
    resolved = candidate.resolve(strict=True)
    repository_harness = (repository / ".smapi-test").resolve(strict=False)
    allowed = False
    try:
        resolved.relative_to(repository_harness)
        allowed = resolved != repository_harness
    except ValueError:
        pass
    temporary_roots = {Path(tempfile.gettempdir()).resolve(), Path("/private/tmp").resolve()}
    if not allowed:
        allowed = any(
            resolved.parent == root and resolved.name.startswith("hatifect-smapi-test.")
            for root in temporary_roots
        )
    if not allowed:
        raise DirectRuntimeError(f"Unsafe isolated test root: {resolved}")
    if "Stardew Valley" in resolved.parts or resolved.name.casefold() == "mods":
        raise DirectRuntimeError(f"Isolated root resembles a normal game path: {resolved}")
    return resolved



def _scenario(metadata: dict[str, Any], kind: str, scenario_id: str) -> dict[str, Any]:
    validator = _load_module(
        "hatifect_direct_runtime_validator",
        Path(metadata["validatorExecutable"]),
    )
    scenarios = validator.load_manifest(Path(metadata["scenarioManifest"]))
    return validator.resolve_scenario(scenarios, scenario_id, kind)


def _validate_request(
    document: Any,
    metadata: dict[str, Any],
    *,
    now: dt.datetime | None = None,
) -> dict[str, Any]:
    if not isinstance(document, dict) or set(document) != REQUEST_FIELDS:
        raise DirectRuntimeError("Direct runtime request has an invalid field set.")
    if document["protocolVersion"] != PROTOCOL_VERSION or document["requestType"] != REQUEST_TYPE:
        raise DirectRuntimeError("Direct runtime request protocol is incompatible.")
    request_id = document["requestId"]
    try:
        parsed_request_id = uuid.UUID(request_id) if isinstance(request_id, str) else None
    except ValueError as error:
        raise DirectRuntimeError("Direct runtime request ID must be a canonical UUID.") from error
    if parsed_request_id is None or str(parsed_request_id) != request_id:
        raise DirectRuntimeError("Direct runtime request ID is invalid.")
    if document["environmentId"] != ENVIRONMENT_ID:
        raise DirectRuntimeError("Direct runtime request environment is not allowlisted.")
    if document["kind"] not in {"smoke", "ui"}:
        raise DirectRuntimeError("Direct runtime request kind is invalid.")
    repository, device, inode = _repository_identity(Path(document["repositoryRoot"]))
    if repository != Path(metadata["repositoryRoot"]).resolve():
        raise DirectRuntimeError("Direct runtime request targets a different repository; run repair.")
    if document["repositoryDevice"] != device or document["repositoryInode"] != inode:
        raise DirectRuntimeError("Direct runtime request repository identity is stale.")
    if device != metadata["repositoryDevice"] or inode != metadata["repositoryInode"]:
        raise DirectRuntimeError("Direct runtime metadata repository identity is stale.")
    head = _repository_head(repository)
    if document["repositoryHead"] != head or metadata["repositoryHead"] != head:
        raise DirectRuntimeError("Direct runtime request repository HEAD is stale.")
    _validate_transport_sources(metadata)
    _validate_repository_transport_sources(metadata)
    resolved = _scenario(metadata, document["kind"], document["scenarioId"])
    timeout = document["timeoutSeconds"]
    if (
        not isinstance(timeout, int)
        or isinstance(timeout, bool)
        or timeout <= 0
        or timeout > MAX_TIMEOUT_SECONDS
        or timeout > resolved["timeoutSeconds"]
    ):
        raise DirectRuntimeError("Direct runtime request timeout exceeds the allowlisted scenario bound.")
    seed = document["seed"]
    if not isinstance(seed, int) or isinstance(seed, bool) or abs(seed) > 2_147_483_647:
        raise DirectRuntimeError("Direct runtime request seed is invalid.")
    created = _parse_timestamp(document["createdAtUtc"])
    expires = _parse_timestamp(document["expiresAtUtc"])
    current = now or _utc_now()
    if expires <= created or (expires - created).total_seconds() > REQUEST_TTL_SECONDS:
        raise DirectRuntimeError("Direct runtime request validity interval is invalid.")
    if current < created - dt.timedelta(seconds=5) or current > expires:
        raise DirectRuntimeError("Direct runtime request is stale or not yet valid.")
    artifact_root = repository / "artifacts" / "runtime"
    artifact = _contained(artifact_root, Path(document["artifactDirectory"]))
    if artifact.name != request_id or not artifact.is_dir():
        raise DirectRuntimeError("Direct runtime artifact directory does not match request identity.")
    artifact_info = artifact.lstat()
    if stat.S_ISLNK(artifact_info.st_mode) or artifact_info.st_uid != os.getuid():
        raise DirectRuntimeError("Direct runtime artifact directory must be a current-user real directory.")
    result_path = _contained(artifact, Path(document["resultPath"]))
    if result_path != artifact / "result.json":
        raise DirectRuntimeError("Direct runtime result path must be the run's canonical result.json.")
    isolated = _validate_isolated_root(repository, Path(document["isolatedRoot"]))
    marker = isolated / "deployment.json"
    manifest = isolated / "Mods" / "Hatifect" / "Hatifect UI" / "manifest.json"
    if not marker.is_file() or not manifest.is_file():
        raise DirectRuntimeError("Direct runtime request has no prepared isolated deployment.")
    validator = _load_module(
        "hatifect_direct_runtime_deployment_validator",
        Path(metadata["validatorExecutable"]),
    )
    if validator.command_validate_deployment(SimpleNamespace(marker=str(marker))) != 0:
        raise DirectRuntimeError("Direct runtime request deployment marker is invalid.")
    save_value = document["savePath"]
    if resolved["requiresSave"]:
        if not isinstance(save_value, str) or not save_value:
            raise DirectRuntimeError("Allowlisted scenario requires an isolated save.")
        save_root = isolated / "config" / "StardewValley" / "Saves"
        save = _contained(save_root, Path(save_value))
        provisioner = _load_module(
            "hatifect_direct_runtime_save_validator",
            Path(metadata["saveProvisionerExecutable"]),
        )
        fixture, _ = provisioner.validate_fixture(isolated, Path(metadata["smapiPath"]))
        expected = provisioner._save_root(isolated) / provisioner._working_name(request_id, document["scenarioId"])
        if save != expected:
            raise DirectRuntimeError("Direct runtime request save path is not its planned working copy.")
        if save.exists():
            provisioner.validate_working_copy(
                isolated,
                save,
                fixture["runtimeId"],
                request_id,
                document["scenarioId"],
            )
    elif save_value is not None:
        raise DirectRuntimeError("Scenario without save authority must use null savePath.")
    artifact_request = artifact / "request.json"
    if not artifact_request.is_file() or _read_json(artifact_request) != document:
        raise DirectRuntimeError("Direct runtime artifact request.json is missing or differs from the typed request.")
    return document


def _response(
    request: dict[str, Any],
    state: str,
    status: str,
    exit_code: int,
    failure_kind: str | None,
    message: str,
    started: dt.datetime,
) -> dict[str, Any]:
    artifact = Path(request["artifactDirectory"])
    artifacts: list[dict[str, str]] = []
    if artifact.is_dir():
        for path in sorted(item for item in artifact.rglob("*") if item.is_file()):
            if path.name.endswith(".tmp"):
                continue
            if path.is_symlink():
                raise DirectRuntimeError(f"Direct runtime transport refuses symlinked run evidence: {path}")
            _contained(artifact, path)
            artifacts.append(
                {"type": "artifact", "path": path.relative_to(artifact).as_posix()}
            )
    payload = {
        "protocolVersion": PROTOCOL_VERSION,
        "requestId": request["requestId"],
        "scenarioId": request["scenarioId"],
        "state": state,
        "status": status,
        "exitCode": exit_code,
        "failureKind": failure_kind,
        "resultPath": request["resultPath"],
        "startedAtUtc": _timestamp(started),
        "completedAtUtc": _timestamp(),
        "message": message[:2048],
        "artifacts": artifacts,
    }
    if (
        set(payload) != RESPONSE_FIELDS
        or state not in FINAL_STATES
        or status not in {"PASS", "FAIL", "BLOCKED"}
        or failure_kind not in FAILURE_KINDS
        or (state == "Completed" and (status != "PASS" or exit_code != 0 or failure_kind is not None))
        or (state != "Completed" and (status == "PASS" or failure_kind is None))
        or (state in {"TimedOut", "Cancelled", "Rejected"} and status != "BLOCKED")
    ):
        raise DirectRuntimeError("Internal direct runtime response is invalid.")
    return payload


def _validate_response(document: Any, request: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(document, dict) or set(document) != RESPONSE_FIELDS:
        raise DirectRuntimeError("Direct runtime response field set is invalid.")
    if (
        document["protocolVersion"] != PROTOCOL_VERSION
        or document["requestId"] != request["requestId"]
        or document["scenarioId"] != request["scenarioId"]
        or document["state"] not in FINAL_STATES
        or document["status"] not in {"PASS", "FAIL", "BLOCKED"}
        or document["exitCode"] != _exit_code(document["status"])
        or document["failureKind"] not in FAILURE_KINDS
        or not isinstance(document["message"], str)
        or len(document["message"]) > 2048
    ):
        raise DirectRuntimeError("Direct runtime response identity or enum value is invalid.")
    if (
        document["state"] == "Completed"
        and (document["status"] != "PASS" or document["failureKind"] is not None)
    ) or (
        document["state"] != "Completed"
        and (document["status"] == "PASS" or document["failureKind"] is None)
    ) or (
        document["state"] in {"TimedOut", "Cancelled", "Rejected"}
        and document["status"] != "BLOCKED"
    ):
        raise DirectRuntimeError("Direct runtime response lifecycle conflicts with status/failure classification.")
    _parse_timestamp(document["startedAtUtc"])
    _parse_timestamp(document["completedAtUtc"])
    if not isinstance(document["artifacts"], list):
        raise DirectRuntimeError("Direct runtime response artifacts must be an array.")
    for artifact in document["artifacts"]:
        if not isinstance(artifact, dict) or set(artifact) != {"type", "path"}:
            raise DirectRuntimeError("Direct runtime response contains a malformed artifact.")
        relative = artifact["path"]
        if (
            not isinstance(artifact["type"], str)
            or not artifact["type"]
            or not isinstance(relative, str)
            or not relative
            or Path(relative).is_absolute()
            or ".." in Path(relative).parts
        ):
            raise DirectRuntimeError("Direct runtime response contains an unsafe artifact.")
        candidate = _contained(Path(request["artifactDirectory"]), Path(request["artifactDirectory"]) / relative)
        if not candidate.is_file() or candidate.is_symlink():
            raise DirectRuntimeError("Direct runtime response names missing or symlinked evidence.")
    return document


def _append_transport_log(request: dict[str, Any], state: str, message: str) -> None:
    if state not in LIFECYCLE_STATES:
        raise DirectRuntimeError(f"Invalid direct runtime lifecycle state: {state}")
    path = Path(request["artifactDirectory"]) / "transport.log"
    path.parent.mkdir(parents=True, exist_ok=True)
    record = {
        "timestampUtc": _timestamp(),
        "requestId": request["requestId"],
        "scenarioId": request["scenarioId"],
        "state": state,
        "message": message[:2048],
    }
    with path.open("a", encoding="utf-8") as stream:
        stream.write(json.dumps(record, sort_keys=True) + "\n")
        stream.flush()
        os.fsync(stream.fileno())


def _transition(active_path: Path, request: dict[str, Any], state: str, message: str) -> None:
    if state not in LIFECYCLE_STATES:
        raise DirectRuntimeError(f"Invalid direct runtime lifecycle state: {state}")
    active = dict(_read_json(active_path)) if active_path.is_file() else dict(request)
    previous = active.get("lifecycleState")
    if state not in STATE_TRANSITIONS.get(previous, set()):
        raise DirectRuntimeError(f"Invalid direct runtime lifecycle transition: {previous!r} -> {state!r}")
    active["lifecycleState"] = state
    active["stateUpdatedAtUtc"] = _timestamp()
    _atomic_write_json(active_path, active, replace=True)
    _append_transport_log(request, state, message)


def _record_started_process(
    active_path: Path,
    request: dict[str, Any],
    smapi: Path,
    pid: int,
    process_group: int,
    *,
    continuation: bool = False,
) -> None:
    active = dict(_read_json(active_path))
    if continuation and (request["scenarioId"] not in SAVED_CRASH_BOUNDARIES or active.get("lifecycleState") != "Running"):
        raise DirectRuntimeError("Only the validated saved-crash continuation may replace a running child.")
    active["smapiPid"] = pid
    active["smapiProcessGroup"] = process_group
    active["smapiExecutable"] = str(smapi)
    _atomic_write_json(active_path, active, replace=True)
    if continuation:
        _append_transport_log(request, "Running", "Saved-crash continuation started a new owned SMAPI process group.")
    else:
        _transition(active_path, request, "Running", "SMAPI process group started.")


def _game_options_directory(isolated: Path) -> Path:
    return _ensure_private_directory(_ensure_private_directory(isolated / "config") / "StardewValley")


def _game_options_file_info(path: Path) -> os.stat_result | None:
    info = _lstat(path)
    if info is not None and (
        not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_uid != os.getuid()
        or info.st_size > MAX_DOCUMENT_BYTES
    ):
        raise DirectRuntimeError(f"Game options must be a bounded, owned regular file without links: {path}")
    return info


def _replace_game_options(path: Path, content: bytes, mode: int) -> None:
    _game_options_file_info(path)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        os.fchmod(descriptor, mode)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


@contextlib.contextmanager
def _background_game_options(request: dict[str, Any]):
    """Lease only isolated game preferences while the owned process group runs."""
    isolated = Path(request["isolatedRoot"]).resolve(strict=True)
    root = _game_options_directory(isolated)
    plans = []
    # Validate both documents before the first mutation. Missing documents use the
    # game's serializer roots; absent properties keep their game defaults.
    for name, root_name, parent_name in (
        ("startup_preferences", "StartupPreferences", "clientOptions"),
        ("default_options", "Options", None),
    ):
        path = root / name
        info = _game_options_file_info(path)
        original = path.read_bytes() if info is not None else None
        try:
            document = ET.fromstring(original, parser=ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))) if original is not None else ET.Element(root_name)
        except ET.ParseError as error:
            raise DirectRuntimeError(f"Invalid isolated game options XML: {path}: {error}") from error
        if document.tag != root_name:
            raise DirectRuntimeError(f"Unexpected isolated game options root: {path}")
        parents = document.findall(parent_name) if parent_name else [document]
        if len(parents) > 1:
            raise DirectRuntimeError(f"Ambiguous isolated game options parent: {path}")
        parent = parents[0] if parents else ET.SubElement(document, parent_name)
        fields = parent.findall("pauseWhenOutOfFocus")
        if len(fields) > 1 or (fields and (list(fields[0]) or fields[0].attrib)):
            raise DirectRuntimeError(f"Ambiguous isolated pauseWhenOutOfFocus option: {path}")
        field = fields[0] if fields else ET.SubElement(parent, "pauseWhenOutOfFocus")
        field.text = "false"
        effective = ET.tostring(document, encoding="utf-8", xml_declaration=True)
        plans.append((path, original, effective, stat.S_IMODE(info.st_mode) if info else PRIVATE_FILE_MODE))

    diagnostics = Path(request["artifactDirectory"]) / "diagnostics"
    evidence_path = diagnostics / "runtime-options.json"
    evidence = {
        "policy": "background-progress-v1", "requestId": request["requestId"],
        "pauseWhenOutOfFocus": False, "state": "Prepared", "files": [],
    }
    for path, original, effective, _mode in plans:
        if original is not None:
            backup = diagnostics / "runtime-options-originals" / path.name
            backup.parent.mkdir(parents=True, exist_ok=True, mode=PRIVATE_DIRECTORY_MODE)
            with backup.open("xb") as stream:
                os.fchmod(stream.fileno(), PRIVATE_FILE_MODE)
                stream.write(original)
                stream.flush()
                os.fsync(stream.fileno())
        evidence["files"].append({
            "path": path.relative_to(isolated).as_posix(),
            "originalSha256": hashlib.sha256(original).hexdigest() if original is not None else None,
            "effectiveSha256": hashlib.sha256(effective).hexdigest(), "restored": True,
        })
    _atomic_write_json(evidence_path, evidence)
    modified = []
    try:
        for index, (path, original, effective, mode) in enumerate(plans):
            _game_options_directory(isolated)
            modified.append(index)
            evidence["files"][index]["restored"] = False
            _replace_game_options(path, effective, mode)
        evidence["state"] = "Active"
        _atomic_write_json(evidence_path, evidence, replace=True)
        yield
    finally:
        errors = []
        for index in reversed(modified):
            path, original, _effective, mode = plans[index]
            try:
                _game_options_directory(isolated)
                _game_options_file_info(path)
                if original is None:
                    path.unlink(missing_ok=True)
                else:
                    _replace_game_options(path, original, mode)
                    if path.read_bytes() != original:
                        raise DirectRuntimeError(f"Isolated game options restoration mismatch: {path}")
                evidence["files"][index]["restored"] = True
            except (OSError, DirectRuntimeError) as error:
                errors.append(f"{path.name}: {error}")
        evidence["state"] = "RestoreFailed" if errors else "Restored"
        evidence["errors"] = errors
        _atomic_write_json(evidence_path, evidence, replace=True)
        if errors:
            raise DirectRuntimeError("Isolated game options restoration failed: " + "; ".join(errors))


def _minimal_environment(request: dict[str, Any], metadata: dict[str, Any]) -> dict[str, str]:
    isolated = Path(request["isolatedRoot"])
    artifact = Path(request["artifactDirectory"])
    isolated_home = isolated / "home"
    isolated_tmp = isolated / "tmp"
    dotnet_bundle = isolated / "dotnet-bundle"
    for directory in (isolated_home, isolated_tmp, dotnet_bundle):
        _ensure_private_directory(directory)
    environment: dict[str, str] = {
        "HOME": str(isolated_home),
        "PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin",
        "SHELL": "/bin/zsh",
    }
    for name in ("USER", "LOGNAME", "LANG", "LC_CTYPE"):
        value = os.environ.get(name)
        if value:
            environment[name] = value
    environment.update(
        {
            "SMAPI_MODS_PATH": str(isolated / "Mods"),
            "XDG_CONFIG_HOME": str(isolated / "config"),
            "TMPDIR": str(isolated_tmp),
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(dotnet_bundle),
            "SMAPI_USE_CURRENT_SHELL": "true",
            "HATIFECT_TEST_MODE": "1",
            "HATIFECT_TEST_AUTOMATED": "1",
            "HATIFECT_TEST_PROTOCOL_VERSION": str(HARNESS_PROTOCOL_VERSION),
            "HATIFECT_TEST_SCENARIO": request["scenarioId"],
            "HATIFECT_TEST_ISOLATED_ROOT": str(isolated),
            "HATIFECT_TEST_RESULT": request["resultPath"],
            "HATIFECT_TEST_ARTIFACTS": str(artifact),
            "HATIFECT_TEST_RUN_ID": request["requestId"],
            "HATIFECT_TEST_SEED": str(request["seed"]),
        }
    )
    if request["savePath"] is not None:
        environment["HATIFECT_SMAPI_TEST_SAVE"] = request["savePath"]
    if request["scenarioId"] == "save.bootstrap":
        provisioner = _load_module(
            "hatifect_direct_runtime_bootstrap_environment",
            Path(metadata["saveProvisionerExecutable"]),
        )
        environment.update(provisioner.bootstrap_environment(
            Path(request["isolatedRoot"]),
            request["requestId"],
            Path(request["artifactDirectory"]),
        ))
    return environment


def _write_transport_diagnostics(
    request: dict[str, Any],
    metadata: dict[str, Any],
    *,
    phase: str,
    status: str | None = None,
    process_id: int | None = None,
    process_group: int | None = None,
) -> None:
    artifact = Path(request["artifactDirectory"])
    _atomic_write_json(
        artifact / "diagnostics" / "transport.json",
        {
            "protocolVersion": PROTOCOL_VERSION,
            "requestId": request["requestId"],
            "scenario": request["scenarioId"],
            "phase": phase,
            "status": status,
            "transportPid": os.getpid(),
            "smapiPid": process_id,
            "smapiProcessGroup": process_group,
            "repositoryRoot": metadata["repositoryRoot"],
            "isolatedRoot": request["isolatedRoot"],
            "capturedAtUtc": _timestamp(),
        },
        replace=True,
    )


def _write_harness_result(
    metadata: dict[str, Any],
    request: dict[str, Any],
    status: str,
    message: str,
    assertion_id: str,
    started_at: float,
    *,
    exception_type: str | None = None,
) -> None:
    validator = _load_module(
        "hatifect_direct_runtime_result_writer",
        Path(metadata["validatorExecutable"]),
    )
    validator.write_result(
        Path(request["resultPath"]),
        status,
        request["scenarioId"],
        message,
        run_id=request["requestId"],
        duration_ms=max(0, int((time.time() - started_at) * 1000)),
        assertions=[validator._assertion(
            assertion_id,
            status,
            request["scenarioId"],
            "The automated isolated scenario completes with structured evidence.",
            message,
        )],
        exceptions=(
            [{"type": exception_type, "message": message}]
            if exception_type else []
        ),
        artifacts=validator.collect_artifacts(Path(request["artifactDirectory"])),
    )


def _copy_smapi_logs(isolated: Path, artifact: Path) -> None:
    source = isolated / "config" / "StardewValley" / "ErrorLogs"
    if not source.is_dir():
        return
    target = artifact / "smapi-logs"
    target.mkdir(parents=True, exist_ok=True)
    for log in sorted(source.glob("SMAPI-*.txt")):
        shutil.copy2(log, target / log.name)


def _complete_save_lifecycle(
    request: dict[str, Any],
    metadata: dict[str, Any],
    *,
    process_succeeded: bool,
    report_exists: bool,
) -> None:
    artifact = Path(request["artifactDirectory"])
    isolated = Path(request["isolatedRoot"])
    smapi = Path(metadata["smapiPath"])
    evidence: dict[str, Any] | None = None
    if request["savePath"] is not None:
        provisioner = _load_module(
            "hatifect_direct_runtime_save_lifecycle",
            Path(metadata["saveProvisionerExecutable"]),
        )
        fixture, _ = provisioner.validate_fixture(isolated, smapi)
        copies = _request_save_copies(request, provisioner)
        _cleanup_owned_copies(provisioner, isolated, fixture["runtimeId"], copies, scenario_id=request["scenarioId"])
        evidence = {
            "operation": "working-copy-cleanup",
            "status": "PASS",
            "runId": request["requestId"],
            "savePath": request["savePath"],
            "fixtureRuntimeId": fixture["runtimeId"],
            "completedAtUtc": _timestamp(),
        }
        if request["scenarioId"] in ("flow.save.isolation", "flow.chest.isolation"):
            evidence["workingCopies"] = [{"runId": run_id, "savePath": str(path), "role": role, "status": "PASS"} for path, run_id, role in copies]
    elif request["scenarioId"] == "save.bootstrap" and process_succeeded and report_exists:
        provisioner = _load_module(
            "hatifect_direct_runtime_save_lifecycle",
            Path(metadata["saveProvisionerExecutable"]),
        )
        fixture_path = provisioner.finalize_bootstrap(
            isolated,
            smapi,
            request["requestId"],
            artifact / "save-bootstrap-receipt.json",
        )
        fixture, _ = provisioner.validate_fixture(isolated, smapi)
        evidence = {
            "operation": "golden-save-bootstrap",
            "status": "PASS",
            "runId": request["requestId"],
            "fixturePath": str(fixture_path),
            "fixtureRuntimeId": fixture["runtimeId"],
            "fixtureChecksum": fixture["checksum"],
            "completedAtUtc": _timestamp(),
        }
    if evidence is not None:
        _atomic_write_json(artifact / "diagnostics" / "save-provisioning.json", evidence)


def _product_runtime_was_reached(artifact: Path) -> bool:
    log_path = artifact / "smapi.log"
    if not log_path.is_file():
        return False
    log = log_path.read_text(encoding="utf-8", errors="replace")
    return "[SMAPI] Mods loaded and ready!" in log or "\n[Hatifect" in log


def _request_save_copies(request: dict[str, Any], provisioner) -> list[tuple[Path, str, str]]:
    primary = Path(request["savePath"])
    copies = [(primary, request["requestId"], "primary")]
    scenario = request["scenarioId"]
    if scenario in ("flow.save.isolation", "flow.chest.isolation"):
        secondary_id = provisioner.flow_secondary_run_id(request["requestId"])
        role = "secondary" if scenario == "flow.chest.isolation" else "primary"
        secondary = primary.parent / provisioner._working_name(secondary_id, scenario, role=role)
        copies.append((secondary, secondary_id, role))
    return copies


def _cleanup_owned_copies(provisioner, isolated: Path, runtime_id: str, copies: list[tuple[Path, str, str]], *, remaining_only: bool = False, scenario_id: str = "") -> None:
    errors: list[str] = []
    for path, run_id, role in copies:
        if remaining_only and not path.exists() and not path.is_symlink():
            continue
        try:
            provisioner.cleanup_working_copy(isolated, path, runtime_id, run_id, scenario_id, role=role)
        except (OSError, ValueError) as error:
            errors.append(f"{run_id}: {error}")
    if errors:
        raise provisioner.SaveProvisioningError("HARNESS-SAVE-CLEANUP", "; ".join(errors))


def _prepare_request_saves(request: dict[str, Any], metadata: dict[str, Any]) -> tuple[Any, str, list[tuple[Path, str, str]]] | None:
    if request["savePath"] is None:
        return None
    provisioner = _load_module("hatifect_direct_runtime_save_preparer", Path(metadata["saveProvisionerExecutable"]))
    isolated = Path(request["isolatedRoot"])
    smapi = Path(metadata["smapiPath"])
    fixture, _ = provisioner.validate_fixture(isolated, smapi)
    expected = _request_save_copies(request, provisioner)
    owned: list[tuple[Path, str, str]] = []
    try:
        prepared = provisioner.prepare_working_copy(isolated, smapi, request["requestId"], request["scenarioId"])
        owned.append((prepared, request["requestId"], "primary"))
        if prepared != Path(request["savePath"]):
            raise DirectRuntimeError("Provisioned save path differs from the accepted request.")
        if len(expected) == 2:
            expected_path, secondary_id, role = expected[1]
            secondary = provisioner.prepare_flow_secondary(isolated, smapi, request["requestId"], request["scenarioId"])
            owned.append((secondary, secondary_id, role))
            if secondary != expected_path:
                raise DirectRuntimeError("Provisioned secondary differs from this scenario's derived save.")
    except BaseException as error:
        try:
            _cleanup_owned_copies(provisioner, isolated, fixture["runtimeId"], owned, scenario_id=request["scenarioId"])
        except (OSError, ValueError) as cleanup_error:
            raise provisioner.SaveProvisioningError("HARNESS-SAVE-CLEANUP", f"Preparation failed: {error}; cleanup failed: {cleanup_error}") from error
        raise
    return provisioner, fixture["runtimeId"], owned


@contextlib.contextmanager
def _prepared_request_saves(request: dict[str, Any], metadata: dict[str, Any]):
    prepared = _prepare_request_saves(request, metadata)
    try:
        yield
    finally:
        if prepared is not None:
            provisioner, runtime_id, owned = prepared
            _cleanup_owned_copies(provisioner, Path(request["isolatedRoot"]), runtime_id, owned, remaining_only=True, scenario_id=request["scenarioId"])


def _acceptance_report_source(isolated: Path, scenario_id: str) -> Path:
    # Fixed ownership for the allowlisted asynchronous Flow lifecycle scenario.
    # The request cannot supply an arbitrary report path or module name.
    module = "Hatifect Flow" if scenario_id in {"flow.route.basic", "flow.save.isolation", "flow.chest.roundtrip", "flow.chest.cancellation", "flow.chest.return", "flow.chest.isolation", "flow.chest.performance", "flow.chest.resources", *SAVED_CRASH_BOUNDARIES} else "Hatifect UI"
    return isolated / "Mods" / "Hatifect" / module / ".acceptance" / "host-acceptance-report.json"


def _flow_runtime_fingerprint(isolated: Path) -> str:
    module = isolated / "Mods" / "Hatifect" / "Hatifect Flow"
    names = ("Hatifect.Flow.Core.dll", "Hatifect.Flow.Persistence.dll", "Hatifect.Flow.dll")
    lines: list[str] = []
    for name in names:
        path = module / name
        if path != path.resolve(strict=True) or not path.is_file():
            raise DirectRuntimeError("Flow runtime fingerprint requires the exact deployed regular DLLs.")
        lines.append(f"{name}\t{_sha256(path)}\n")
    return hashlib.sha256("".join(lines).encode()).hexdigest()


def _saved_crash_marker(request: dict[str, Any], metadata: dict[str, Any], process: dict[str, Any]) -> dict[str, Any] | None:
    try:
        return _validate_saved_crash_marker(request, metadata, process)
    except (OSError, ValueError, RecursionError) as error:
        raise DirectRuntimeError(f"Unable to validate confirmed-save crash evidence: {error}") from error


def _validate_saved_crash_marker(request: dict[str, Any], metadata: dict[str, Any], process: dict[str, Any]) -> dict[str, Any] | None:
    if request["scenarioId"] not in SAVED_CRASH_BOUNDARIES:
        raise DirectRuntimeError("Saved-crash marker is not available to this scenario.")
    expected_phase, expected_saves = SAVED_CRASH_BOUNDARIES[request["scenarioId"]]
    artifact = Path(request["artifactDirectory"])
    path = artifact / "diagnostics" / "flow-crash-ready.json"
    info = _lstat(path)
    if info is None:
        return None
    if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= 65536 or path != _contained(artifact, path):
        raise DirectRuntimeError("Saved-crash marker must be a bounded regular request-owned file.")
    marker = _read_json(path)
    fields = {"formatVersion", "runId", "scenarioId", "phase", "pid", "sessionId", "saveName", "saveId", "saveHash",
              "runtimeFingerprint", "parcelId", "partialParcelId", "sourceX", "sourceY", "destinationX", "destinationY",
              "remainderXml", "loads", "savingEvents", "savedEvents", "capturedAtUtc"}
    if not isinstance(marker, dict) or set(marker) != fields:
        raise DirectRuntimeError("Saved-crash marker has an invalid field set.")
    if (type(marker["formatVersion"]) is not int or marker["formatVersion"] != 1
            or marker["runId"] != request["requestId"] or marker["scenarioId"] != request["scenarioId"]
            or marker["phase"] != expected_phase
            or type(marker["pid"]) is not int or marker["pid"] != process.get("pid")
            or type(marker["saveId"]) is not int or marker["saveId"] != 4242424242):
        raise DirectRuntimeError("Saved-crash marker has a foreign request, process or save identity.")
    for key in ("sessionId", "parcelId", "partialParcelId"):
        value = marker[key]
        try:
            valid = isinstance(value, str) and str(uuid.UUID(value)) == value and uuid.UUID(value).int != 0
        except ValueError:
            valid = False
        if not valid:
            raise DirectRuntimeError(f"Saved-crash marker has invalid {key}.")
    if marker["parcelId"] == marker["partialParcelId"]:
        raise DirectRuntimeError("Saved-crash marker aliases distinct cargo parcels.")
    for key in ("sourceX", "sourceY", "destinationX", "destinationY"):
        if type(marker[key]) is not int or not 0 <= marker[key] <= 10000:
            raise DirectRuntimeError("Saved-crash marker has invalid station coordinates.")
    if (marker["sourceX"], marker["sourceY"]) == (marker["destinationX"], marker["destinationY"]):
        raise DirectRuntimeError("Saved-crash marker aliases its source and destination.")
    if any(type(marker[key]) is not int or marker[key] != expected_saves for key in ("loads", "savingEvents", "savedEvents")):
        raise DirectRuntimeError("Saved-crash marker does not prove this scenario's confirmed save boundary.")
    if not isinstance(marker["remainderXml"], str) or not 0 < len(marker["remainderXml"]) <= 32768:
        raise DirectRuntimeError("Saved-crash remainder evidence exceeds its bound.")
    captured = _parse_timestamp(marker["capturedAtUtc"])
    if not _parse_timestamp(process.get("startedAtUtc")) <= captured <= _utc_now() + dt.timedelta(seconds=5):
        raise DirectRuntimeError("Saved-crash marker timestamp predates its process or is in the future.")
    save = Path(request["savePath"])
    if marker["saveName"] != save.name or marker["runtimeFingerprint"] != _flow_runtime_fingerprint(Path(request["isolatedRoot"])):
        raise DirectRuntimeError("Saved-crash marker belongs to another save or runtime candidate.")
    provisioner = _load_module("hatifect_crash_save_validator", Path(metadata["saveProvisionerExecutable"]))
    fixture, _ = provisioner.validate_fixture(Path(request["isolatedRoot"]), Path(metadata["smapiPath"]))
    provisioner.validate_working_copy(Path(request["isolatedRoot"]), save, fixture["runtimeId"], request["requestId"], request["scenarioId"])
    if marker["saveHash"] != _sha256(save / save.name):
        raise DirectRuntimeError("Confirmed saved bytes changed before the controlled process crash.")
    return marker


def _run_saved_crash(request, metadata, runner, active_path, cancellation_path, on_started, on_completed) -> int:
    """Exactly two fixed launches with one owned save; only a proved SIGKILL authorizes continuation."""
    artifact = Path(request["artifactDirectory"])
    smapi = Path(metadata["smapiPath"])
    deadline = time.monotonic() + request["timeoutSeconds"]
    marker: dict[str, Any] | None = None
    forced: dict[str, Any] | None = None

    def kill_requested() -> bool:
        nonlocal marker
        marker = _saved_crash_marker(request, metadata, _read_json(artifact / "process.json"))
        return marker is not None

    def forced_exit(pid: int, group: int, raw_exit: int) -> None:
        nonlocal forced
        forced = {"formatVersion": 1, "requestId": request["requestId"], "scenarioId": request["scenarioId"],
                  "pid": pid, "processGroup": group, "requestedSignal": int(signal.SIGKILL), "rawExitCode": raw_exit,
                  "observedAtUtc": _timestamp()}
        _atomic_write_json(artifact / "diagnostics" / "flow-crash-termination.json", forced)

    environment = _minimal_environment(request, metadata)
    environment["HATIFECT_TEST_CRASH_PHASE"] = "prepare"
    first_exit = runner.run([str(smapi)], smapi.parent, artifact / "smapi.log", request["timeoutSeconds"], 5.0,
                            environment=environment, on_started=on_started, cancel_requested=cancellation_path.exists,
                            on_completed=on_completed, force_kill_requested=kill_requested, on_forced_exit=forced_exit)
    process = _read_json(artifact / "process.json")
    _atomic_write_json(artifact / "diagnostics" / "process-prepare.json", process)
    _copy_smapi_logs(Path(request["isolatedRoot"]), artifact / "diagnostics" / "crash-prepare")
    if (artifact / "smapi.log").is_file():
        shutil.copy2(artifact / "smapi.log", artifact / "smapi-prepare.log")
    prepare_report = _acceptance_report_source(Path(request["isolatedRoot"]), request["scenarioId"])
    if _lstat(prepare_report) is not None:
        # Prepare must remain alive at its save barrier. Any terminal report is a failure,
        # even if SIGKILL wins the race with the driver's next Game1.Exit tick.
        if prepare_report.is_file():
            shutil.copy2(prepare_report, artifact / "diagnostics" / "host-acceptance-prepare.json")
        raise DirectRuntimeError("The prepare process published a terminal acceptance report before restart.")
    if forced is None:
        if first_exit == 0:
            raise DirectRuntimeError("The prepare process exited without the required controlled crash.")
        return first_exit
    if first_exit in {runner.TIMEOUT_EXIT, runner.INTERRUPTED_EXIT, runner.TEARDOWN_FAILURE_EXIT}:
        return first_exit
    if (first_exit != 128 + signal.SIGKILL or forced["rawExitCode"] != -signal.SIGKILL
            or forced["pid"] != process.get("pid") or forced["processGroup"] != process.get("processGroup")
            or process.get("teardownErrors") or marker is None):
        raise DirectRuntimeError("The prepare process did not prove the requested owned SIGKILL.")
    if _saved_crash_marker(request, metadata, process) != marker:
        raise DirectRuntimeError("Saved-crash evidence changed after process termination.")
    if cancellation_path.exists():
        return runner.INTERRUPTED_EXIT
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        return runner.TIMEOUT_EXIT
    # Retire the dead child's journal before a second launch can fail; supervisor cleanup must never use its old PID.
    active = dict(_read_json(active_path))
    active["smapiPid"] = active["smapiProcessGroup"] = None
    _atomic_write_json(active_path, active, replace=True)
    _atomic_write_json(artifact / "process.json", {"protocolVersion": PROTOCOL_VERSION, "requestId": request["requestId"],
                       "pid": None, "processGroup": None, "executable": str(smapi), "launchRequestedAtUtc": _timestamp()}, replace=True)
    environment["HATIFECT_TEST_CRASH_PHASE"] = "resume"
    result = runner.run([str(smapi)], smapi.parent, artifact / "smapi.log", remaining, 5.0,
                        environment=environment, on_started=lambda pid, group: on_started(pid, group, True),
                        cancel_requested=cancellation_path.exists, on_completed=on_completed)
    resumed = _read_json(artifact / "process.json")
    _atomic_write_json(artifact / "diagnostics" / "process-resume.json", resumed)
    if result == 0 and (resumed.get("pid") in {None, forced["pid"]} or resumed.get("processGroup") == forced["processGroup"]):
        raise DirectRuntimeError("Saved-crash continuation did not prove a distinct owned process.")
    return result


def _execute_request(
    request: dict[str, Any],
    metadata: dict[str, Any],
    active_path: Path,
    cancellation_path: Path,
) -> tuple[str, str, int, str | None, str, bool]:
    repository = Path(metadata["repositoryRoot"])
    artifact = Path(request["artifactDirectory"])
    isolated = Path(request["isolatedRoot"])
    smapi = Path(metadata["smapiPath"])
    smapi_directory = smapi.parent
    report_source = _acceptance_report_source(isolated, request["scenarioId"])
    report_artifact = artifact / "host-acceptance-report.json"
    started_at = time.time()
    teardown_errors: list[str] = []
    if report_source.exists():
        os.replace(report_source, artifact / "previous-acceptance-report.json")
    run_process = _load_module(
        "hatifect_direct_runtime_process_supervisor",
        Path(metadata["processSupervisorExecutable"]),
    )

    _transition(active_path, request, "Launching", "Starting the fixed configured SMAPI executable.")
    _atomic_write_json(
        artifact / "process.json",
        {
            "protocolVersion": PROTOCOL_VERSION,
            "requestId": request["requestId"],
            "pid": None,
            "processGroup": None,
            "executable": str(smapi),
            "launchRequestedAtUtc": _timestamp(),
        },
    )
    (artifact / "harness.log").write_text(
        f"{_timestamp()} scenario={request['scenarioId']} phase=awaiting TestHarness discovery\n",
        encoding="utf-8",
    )
    _write_transport_diagnostics(request, metadata, phase="starting")

    def on_started(pid: int, process_group: int, continuation: bool = False) -> None:
        _record_started_process(active_path, request, smapi, pid, process_group, continuation=continuation)
        _atomic_write_json(
            artifact / "process.json",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "requestId": request["requestId"],
                "pid": pid,
                "processGroup": process_group,
                "executable": str(smapi),
                "startedAtUtc": _timestamp(),
            },
            replace=True,
        )
        _write_transport_diagnostics(
            request,
            metadata,
            phase="running",
            process_id=pid,
            process_group=process_group,
        )

    def on_completed(observed_exit: int, errors: list[str]) -> None:
        teardown_errors.extend(errors)
        process_path = artifact / "process.json"
        if not process_path.is_file():
            return
        process_document = _read_json(process_path)
        process_document["completedAtUtc"] = _timestamp()
        process_document["observedExitCode"] = observed_exit
        process_document["teardownErrors"] = list(errors)
        _atomic_write_json(process_path, process_document, replace=True)

    with _background_game_options(request):
        try:
            if request["scenarioId"] in SAVED_CRASH_BOUNDARIES:
                exit_code = _run_saved_crash(request, metadata, run_process, active_path, cancellation_path, on_started, on_completed)
            else:
                exit_code = run_process.run(
                    [str(smapi)],
                    smapi_directory,
                    artifact / "smapi.log",
                    request["timeoutSeconds"],
                    5.0,
                    environment=_minimal_environment(request, metadata),
                    on_started=on_started,
                    cancel_requested=cancellation_path.exists,
                    on_completed=on_completed,
                )
        except DirectRuntimeError as error:
            if request["scenarioId"] not in SAVED_CRASH_BOUNDARIES:
                raise
            _copy_smapi_logs(isolated, artifact)
            if report_source.is_file():
                shutil.copy2(report_source, report_artifact)
            _complete_save_lifecycle(request, metadata, process_succeeded=False, report_exists=False)
            message = f"Controlled saved-crash evidence failed: {error}"
            _write_harness_result(metadata, request, "FAIL", message, "HARNESS-CRASH-EVIDENCE", started_at, exception_type="CrashEvidence")
            _write_transport_diagnostics(request, metadata, phase="completed", status="FAIL")
            return "Failed", "FAIL", 1, "ScenarioFailure", message, False
    _copy_smapi_logs(isolated, artifact)
    if report_source.is_file():
        shutil.copy2(report_source, report_artifact)
    _complete_save_lifecycle(
        request,
        metadata,
        process_succeeded=exit_code == 0,
        report_exists=report_artifact.is_file(),
    )

    if exit_code == run_process.TEARDOWN_FAILURE_EXIT:
        message = "Owned process teardown failed: " + "; ".join(teardown_errors)
        _write_harness_result(metadata, request, "BLOCKED", message, "HARNESS-PROCESS-TEARDOWN", started_at, exception_type="ProcessTeardown")
        _write_transport_diagnostics(request, metadata, phase="completed", status="BLOCKED")
        return "Failed", "BLOCKED", 2, "BrokerFailure", message, False
    if exit_code == run_process.TIMEOUT_EXIT:
        message = f"The automated SMAPI process exceeded {request['timeoutSeconds']} seconds."
        _write_harness_result(metadata, request, "BLOCKED", message, "HARNESS-PROCESS-TIMEOUT", started_at, exception_type="Timeout")
        _write_transport_diagnostics(request, metadata, phase="completed", status="BLOCKED")
        return "TimedOut", "BLOCKED", 2, "Timeout", message, False
    if exit_code in {run_process.START_FAILURE_EXIT, run_process.INTERRUPTED_EXIT}:
        message = f"The automated SMAPI process could not complete (exit {exit_code})."
        _write_harness_result(metadata, request, "BLOCKED", message, "HARNESS-PROCESS-START", started_at, exception_type="ProcessExit")
        _write_transport_diagnostics(request, metadata, phase="completed", status="BLOCKED")
        runtime_stopping = (
            exit_code == run_process.INTERRUPTED_EXIT
            and not cancellation_path.exists()
        )
        if exit_code == run_process.INTERRUPTED_EXIT:
            return "Cancelled", "BLOCKED", 2, "Cancelled", message, runtime_stopping
        return "Failed", "BLOCKED", 2, "LaunchFailure", message, runtime_stopping
    if exit_code != 0 and not report_artifact.is_file():
        if not _product_runtime_was_reached(artifact):
            message = (
                f"SMAPI exited with code {exit_code} before Hatifect mod discovery; "
                "classified BLOCKED_LOCAL_INFRA until a product runtime is reached."
            )
            assertion_id = "HARNESS-LOCAL-INFRA-BOOT"
            failure_kind = "LocalInfrastructure"
        else:
            message = f"SMAPI exited with code {exit_code} after reaching the Hatifect runtime but before producing automated acceptance evidence."
            assertion_id = "HARNESS-PROCESS-BOOT"
            failure_kind = "ProductRuntimeFailure"
        _write_harness_result(metadata, request, "BLOCKED", message, assertion_id, started_at, exception_type="ProcessExit")
        _write_transport_diagnostics(request, metadata, phase="completed", status="BLOCKED")
        return "Failed", "BLOCKED", 2, failure_kind, message, False
    if exit_code != 0:
        message = f"SMAPI exited with code {exit_code} after producing acceptance evidence."
        _write_harness_result(metadata, request, "FAIL", message, "HARNESS-PROCESS-EXIT", started_at, exception_type="ProcessExit")
        _write_transport_diagnostics(request, metadata, phase="completed", status="FAIL")
        return "Failed", "FAIL", 1, "ProductRuntimeFailure", message, False
    if not report_artifact.is_file():
        message = "SMAPI exited without a fresh automated acceptance report."
        _write_harness_result(metadata, request, "BLOCKED", message, "HARNESS-REPORT-MISSING", started_at)
        _write_transport_diagnostics(request, metadata, phase="completed", status="BLOCKED")
        return "Failed", "BLOCKED", 2, "ProductRuntimeFailure", message, False

    validator = _load_module(
        "hatifect_direct_runtime_finalizer",
        Path(metadata["validatorExecutable"]),
    )
    with (artifact / "harness.log").open("a", encoding="utf-8") as stream:
        stream.write(f"{_timestamp()} phase=finalizing acceptance report\n")
    process_path = artifact / "process.json"
    if process_path.is_file():
        process_document = _read_json(process_path)
        process_document["completedAtUtc"] = _timestamp()
        process_document["observedExitCode"] = exit_code
        _atomic_write_json(process_path, process_document, replace=True)
    finalized = validator.command_finalize(SimpleNamespace(
        result=request["resultPath"],
        scenario=request["scenarioId"],
        kind=request["kind"],
        report=str(report_artifact),
        started_at=started_at,
        manifest=metadata["scenarioManifest"],
        run_id=request["requestId"],
        artifact_root=str(artifact),
    ))
    result = _read_json(Path(request["resultPath"]))
    status = validator.validate_result(result, request["scenarioId"])
    expected_exit = _exit_code(status)
    if finalized != expected_exit:
        raise DirectRuntimeError("Harness finalizer exit code conflicts with result status.")
    with (artifact / "harness.log").open("a", encoding="utf-8") as stream:
        stream.write(f"{_timestamp()} status={status} result=result.json\n")
    _write_transport_diagnostics(request, metadata, phase="completed", status=status)
    state = "Completed" if status == "PASS" else "Failed"
    failure_kind = None if status == "PASS" else "ScenarioFailure"
    return state, status, expected_exit, failure_kind, "Automated acceptance evidence finalized.", False



def _direct_metadata(
    repository_root: Path,
    isolated_root: Path,
    smapi_path: Path,
) -> dict[str, Any]:
    """Build verified runtime metadata from the current checkout for direct execution."""
    repository, device, inode = _repository_identity(repository_root)
    isolated = _validate_isolated_root(repository, isolated_root)
    harness = repository / "tools" / "live-harness"
    transport = Path(__file__).resolve(strict=True)
    validator = (harness / "validate.py").resolve(strict=True)
    supervisor = (harness / "run_process.py").resolve(strict=True)
    manifest = (harness / "scenarios.json").resolve(strict=True)
    save_provisioner = (harness / "save_provisioning.py").resolve(strict=True)
    smapi = smapi_path.resolve(strict=True)
    if not os.access(smapi, os.X_OK):
        raise DirectRuntimeError(f"SMAPI executable is unavailable: {smapi}")
    state = _ensure_private_directory(isolated / ".runtime")
    return {
        "protocolVersion": PROTOCOL_VERSION,
        "transportId": TRANSPORT_ID,
        "repositoryRoot": str(repository),
        "repositoryDevice": device,
        "repositoryInode": inode,
        "repositoryHead": _repository_head(repository),
        "transportExecutable": str(transport),
        "transportSha256": _sha256(transport),
        "validatorExecutable": str(validator),
        "validatorSha256": _sha256(validator),
        "processSupervisorExecutable": str(supervisor),
        "processSupervisorSha256": _sha256(supervisor),
        "scenarioManifest": str(manifest),
        "scenarioManifestSha256": _sha256(manifest),
        "saveProvisionerExecutable": str(save_provisioner),
        "saveProvisionerSha256": _sha256(save_provisioner),
        "smapiPath": str(smapi),
        "runtimeStateRoot": str(state),
        "constructedAtUtc": _timestamp(),
    }


def _build_request(
    repository_root: Path,
    metadata: dict[str, Any],
    kind: str,
    scenario_id: str,
    isolated_root: Path,
    artifact_directory: Path,
    result_path: Path,
    save_path: Path | None,
    timeout_seconds: int,
    seed: int,
) -> tuple[dict[str, Any], dict[str, Any]]:
    repository, device, inode = _repository_identity(repository_root)
    repository_head = _repository_head(repository)
    created = _utc_now()
    request = {
        "protocolVersion": PROTOCOL_VERSION,
        "requestType": REQUEST_TYPE,
        "requestId": artifact_directory.name,
        "repositoryRoot": str(repository),
        "repositoryDevice": device,
        "repositoryInode": inode,
        "repositoryHead": repository_head,
        "environmentId": ENVIRONMENT_ID,
        "kind": kind,
        "scenarioId": scenario_id,
        "isolatedRoot": str(isolated_root.resolve()),
        "artifactDirectory": str(artifact_directory.resolve()),
        "resultPath": str(result_path.resolve()),
        "savePath": str(save_path.resolve()) if save_path is not None else None,
        "timeoutSeconds": timeout_seconds,
        "seed": seed,
        "createdAtUtc": _timestamp(created),
        "expiresAtUtc": _timestamp(created + dt.timedelta(seconds=REQUEST_TTL_SECONDS)),
    }
    _atomic_write_json(artifact_directory / "request.json", request)
    _validate_request(request, metadata, now=created)
    return request, metadata


@contextlib.contextmanager
def _direct_run_lock(isolated_root: Path):
    state = _ensure_private_directory(isolated_root / ".runtime")
    lock_path = state / "direct-run.lock"
    with lock_path.open("a+b") as lock:
        os.chmod(lock_path, PRIVATE_FILE_MODE)
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise DirectRuntimeError(
                "Another direct Hatifect SMAPI runtime run owns the isolated environment."
            ) from error
        try:
            yield
        finally:
            fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def _finalize_direct_failure(
    request: dict[str, Any],
    metadata: dict[str, Any],
    active_path: Path,
    error: Exception,
    started_at: float,
) -> tuple[str, str, int, str, str]:
    message = f"Direct runtime transport rejected or failed the request: {error}"
    previous = None
    if active_path.is_file():
        previous = _read_json(active_path).get("lifecycleState")
    state = "Rejected" if "Rejected" in STATE_TRANSITIONS.get(previous, set()) else "Failed"
    failure_kind = "RejectedRequest" if state == "Rejected" else "BrokerFailure"
    if not Path(request["resultPath"]).exists():
        _write_harness_result(
            metadata,
            request,
            "BLOCKED",
            message,
            getattr(
                error,
                "assertion_id",
                "HARNESS-DIRECT-REJECTED" if state == "Rejected" else "HARNESS-DIRECT-FAILURE",
            ),
            started_at,
            exception_type=type(error).__name__,
        )
    if state in STATE_TRANSITIONS.get(previous, set()):
        _transition(active_path, request, state, message)
    else:
        _append_transport_log(request, state, message)
    return state, "BLOCKED", 2, failure_kind, message


def direct(args: argparse.Namespace) -> int:
    repository = Path(args.repository_root)
    isolated = Path(args.isolated_root)
    artifact = Path(args.artifact_directory)
    result_path = Path(args.result)
    started_at = time.time()
    response_started = _utc_now()
    request: dict[str, Any] | None = None
    metadata: dict[str, Any] | None = None
    active_path = artifact / "direct-process-state.json"
    try:
        metadata = _direct_metadata(repository, isolated, Path(args.smapi_path))
        request, metadata = _build_request(
            repository,
            metadata,
            args.kind,
            args.scenario,
            isolated,
            artifact,
            result_path,
            Path(args.save_path) if args.save_path else None,
            args.timeout_seconds,
            args.seed,
        )
        with _direct_run_lock(isolated), _prepared_request_saves(request, metadata):
            _atomic_write_json(active_path, request)
            _transition(
                active_path,
                request,
                "Accepted",
                "Typed request accepted by the direct-process transport lock.",
            )
            try:
                state, status, exit_code, failure_kind, message, _ = _execute_request(
                    request,
                    metadata,
                    active_path,
                    artifact / ".cancel-requested",
                )
                _transition(active_path, request, state, message)
            except (DirectRuntimeError, OSError, ValueError, json.JSONDecodeError) as error:
                state, status, exit_code, failure_kind, message = _finalize_direct_failure(
                    request, metadata, active_path, error, started_at
                )
            response = _response(
                request,
                state,
                status,
                exit_code,
                failure_kind,
                message,
                response_started,
            )
            response_path = artifact / "diagnostics" / "transport-result.json"
            _atomic_write_json(response_path, response)
            _validate_response(_read_json(response_path), request)
            validator = _load_module(
                "hatifect_direct_client_validator",
                Path(metadata["validatorExecutable"]),
            )
            result_status = validator.validate_result(_read_json(result_path), args.scenario)
            if result_status != status or _exit_code(result_status) != exit_code:
                raise DirectRuntimeError(
                    "Direct transport response conflicts with authoritative result.json."
                )
            print(
                f"Direct result: state={state} status={result_status} "
                f"failureKind={failure_kind or 'none'}"
            )
            return exit_code
    except KeyboardInterrupt:
        error: Exception = DirectRuntimeError(
            "Direct runtime transport was interrupted before process supervision completed."
        )
    except (DirectRuntimeError, OSError, ValueError, json.JSONDecodeError) as caught:
        error = caught
    if request is not None and metadata is not None:
        response_path = artifact / "diagnostics" / "transport-result.json"
        if not response_path.exists():
            try:
                state, status, exit_code, failure_kind, message = _finalize_direct_failure(
                    request, metadata, active_path, error, started_at
                )
                response = _response(
                    request,
                    state,
                    status,
                    exit_code,
                    failure_kind,
                    message,
                    response_started,
                )
                _atomic_write_json(response_path, response)
            except Exception:
                pass
    try:
        validator_path = (
            Path(metadata["validatorExecutable"])
            if metadata is not None
            else repository / "tools" / "live-harness" / "validate.py"
        )
        validator = _load_module("hatifect_direct_client_error_writer", validator_path)
        if not result_path.exists():
            validator.write_result(
                result_path,
                "BLOCKED",
                args.scenario,
                str(error),
                run_id=artifact.name,
                duration_ms=max(0, int((time.time() - started_at) * 1000)),
                exceptions=[{"type": type(error).__name__, "message": str(error)}],
                artifacts=validator.collect_artifacts(artifact),
            )
    except Exception:
        pass
    print(f"Hatifect direct runtime transport: BLOCKED: {error}", file=sys.stderr)
    return 2



def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", required=True)
    parser.add_argument("--smapi-path", required=True)
    parser.add_argument("--kind", choices=("smoke", "ui"), required=True)
    parser.add_argument("--scenario", required=True)
    parser.add_argument("--isolated-root", required=True)
    parser.add_argument("--artifact-directory", required=True)
    parser.add_argument("--result", required=True)
    parser.add_argument("--save-path", default=None)
    parser.add_argument("--timeout-seconds", type=int, required=True)
    parser.add_argument("--seed", type=int, default=0)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    return direct(args)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (DirectRuntimeError, OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Hatifect direct runtime transport: BLOCKED: {error}", file=sys.stderr)
        raise SystemExit(2)
