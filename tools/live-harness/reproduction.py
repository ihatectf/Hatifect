#!/usr/bin/env python3
"""Bounded, metadata-only targeted reproduction support.

This module deliberately does not launch a runtime.  It validates an old,
request-owned failure and materializes a small request-owned description for a
new run.  The normal smoke/UI wrappers remain the only execution authority.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
import re
import stat
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
RUNTIME_ROOT = ROOT / "artifacts" / "runtime"
VALIDATOR_PATH = ROOT / "tools" / "live-harness" / "validate.py"
SPEC = importlib.util.spec_from_file_location("hatifect_reproduction_validate", VALIDATOR_PATH)
if SPEC is None or SPEC.loader is None:  # pragma: no cover - import failure is fatal
    raise RuntimeError("Cannot load the live-harness validator.")
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


PROTOCOL_VERSION = 2
REQUEST_TYPE = "runScenario"
MAX_DOCUMENT_BYTES = 256 * 1024
MAX_REPRODUCTION_BYTES = 64 * 1024
PHASE = "preflight"
STRATEGY = "replay-preflight"
CHECKPOINT_STATUS = "not-applicable"
CHECKPOINT_REASON = "preflight-only"
UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
HEAD_RE = re.compile(r"^[0-9a-f]{40}$")

REQUEST_FIELDS = {
    "protocolVersion", "requestType", "requestId", "repositoryRoot",
    "repositoryDevice", "repositoryInode", "repositoryHead", "environmentId",
    "kind", "scenarioId", "isolatedRoot", "artifactDirectory", "resultPath",
    "savePath", "timeoutSeconds", "seed", "createdAtUtc", "expiresAtUtc",
}

REPRODUCTION_FIELDS = {
    "formatVersion", "sourceRunId", "sourceScenario", "sourceKind",
    "sourceRootFailureId", "sourceRootPhase", "sourceResultFingerprint",
    "sourceRepositoryHead", "sourceSeed", "targetRunId", "targetScenario",
    "targetKind", "requestedPhase", "effectivePhase", "strategy",
    "checkpointStatus", "checkpointReason", "createdAtUtc",
}


class ReproductionError(ValueError):
    """A source or target failed the reproduction contract."""


def _fail(message: str) -> ReproductionError:
    return ReproductionError(message)


def _canonical_uuid(value: Any, field: str) -> str:
    if not isinstance(value, str) or UUID_RE.fullmatch(value) is None:
        raise _fail(f"{field} must be a canonical UUID.")
    try:
        parsed = uuid.UUID(value)
    except ValueError as error:
        raise _fail(f"{field} must be a canonical UUID.") from error
    if parsed.int == 0 or str(parsed) != value:
        raise _fail(f"{field} must be a canonical UUID.")
    return value


def _directory(path: Path, description: str) -> None:
    try:
        info = path.lstat()
    except OSError as error:
        raise _fail(f"Missing {description}.") from error
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
        raise _fail(f"{description} must be a current-user real directory.")


def _open_directory(path: str | Path, description: str, *, dir_fd: int | None = None) -> int:
    flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags, dir_fd=dir_fd)
    except OSError as error:
        raise _fail(f"Missing {description}.") from error
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
            raise _fail(f"{description} must be a current-user real directory.")
        return descriptor
    except Exception:
        os.close(descriptor)
        raise


def _read_bytes_at(
    directory_descriptor: int,
    name: str,
    description: str,
    maximum: int,
    *,
    optional: bool = False,
) -> bytes | None:
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(name, flags, dir_fd=directory_descriptor)
    except FileNotFoundError:
        if optional:
            return None
        raise _fail(f"Missing {description}.")
    except OSError as error:
        raise _fail(f"Cannot open {description} safely.") from error
    try:
        info = os.fstat(descriptor)
        if (
            not stat.S_ISREG(info.st_mode)
            or info.st_uid != os.getuid()
            or info.st_nlink != 1
        ):
            raise _fail(f"{description} must be a current-user regular file.")
        if info.st_size > maximum:
            raise _fail(f"{description} exceeds its size bound.")
        chunks: list[bytes] = []
        remaining = maximum + 1
        while remaining:
            chunk = os.read(descriptor, min(64 * 1024, remaining))
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
        payload = b"".join(chunks)
        if len(payload) > maximum:
            raise _fail(f"{description} exceeds its size bound.")
        return payload
    except OSError as error:
        raise _fail(f"Cannot read {description} safely.") from error
    finally:
        os.close(descriptor)


def _decode_json(payload: bytes, description: str) -> dict[str, Any]:
    try:
        value = json.loads(payload.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise _fail(f"{description} is not valid JSON.") from error
    if not isinstance(value, dict):
        raise _fail(f"{description} must be a JSON object.")
    return value


def _timestamp(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value or len(value) > 128:
        raise _fail(f"{field} must be a bounded timestamp.")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise _fail(f"{field} must be an RFC3339 timestamp.") from error
    if parsed.tzinfo is None:
        raise _fail(f"{field} must include a timezone.")
    return value


def _repository_head() -> str:
    try:
        completed = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "--verify", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        )
    except (OSError, subprocess.CalledProcessError) as error:
        raise _fail("Cannot determine the current repository HEAD.") from error
    head = completed.stdout.strip()
    if HEAD_RE.fullmatch(head) is None:
        raise _fail("Current repository HEAD is invalid.")
    return head


def _result_fingerprint(result: dict[str, Any]) -> str:
    encoded = json.dumps(result, ensure_ascii=True, separators=(",", ":"), sort_keys=True).encode()
    return hashlib.sha256(encoded).hexdigest()


def _load_scenarios() -> dict[str, dict[str, Any]]:
    try:
        return VALIDATOR.load_manifest()
    except Exception as error:
        raise _fail(f"Cannot load the live scenario manifest: {error}") from error


def _check_owners(scenarios: dict[str, dict[str, Any]], scenario_id: str, kind: str) -> dict[str, str]:
    resolved = VALIDATOR.resolve_scenario(scenarios, scenario_id, kind)
    owners: dict[str, str] = {}
    if scenario_id != "all":
        return owners
    declared = resolved.get("checkOwners")
    if not isinstance(declared, dict):
        raise _fail("Aggregate scenario has no resolved checkOwners contract.")
    for check_id in resolved["checks"]:
        owner = declared.get(check_id)
        if (
            not isinstance(owner, str)
            or not owner
            or owner == scenario_id
            or check_id in owners
        ):
            raise _fail(f"Aggregate check '{check_id}' has an ambiguous or unowned owner.")
        owners[check_id] = owner
    if set(owners) != set(resolved["checks"]):
        raise _fail("Aggregate check owners do not match resolved checks.")
    return owners


def _source_snapshot(source_run_id: str) -> tuple[Path, dict[str, Any], dict[str, Any], dict[str, bytes]]:
    run_id = _canonical_uuid(source_run_id, "sourceRunId")
    runtime_descriptor = _open_directory(RUNTIME_ROOT, "runtime artifact root")
    try:
        source_descriptor = _open_directory(run_id, "source run", dir_fd=runtime_descriptor)
    finally:
        os.close(runtime_descriptor)
    try:
        snapshots = {
            "request.json": _read_bytes_at(
                source_descriptor, "request.json", "request.json", MAX_DOCUMENT_BYTES
            ),
            "result.json": _read_bytes_at(
                source_descriptor, "result.json", "result.json", MAX_DOCUMENT_BYTES
            ),
            "failure.json": _read_bytes_at(
                source_descriptor,
                "failure.json",
                "failure.json",
                VALIDATOR.MAX_FAILURE_ENVELOPE_BYTES,
            ),
            "failure-summary.txt": _read_bytes_at(
                source_descriptor,
                "failure-summary.txt",
                "failure-summary.txt",
                VALIDATOR.MAX_FAILURE_SUMMARY_CHARACTERS * 4,
            ),
        }
        preflight = _read_bytes_at(
            source_descriptor,
            VALIDATOR.PREFLIGHT_FILE_NAME,
            VALIDATOR.PREFLIGHT_FILE_NAME,
            VALIDATOR.MAX_PREFLIGHT_BYTES,
            optional=True,
        )
        if preflight is not None:
            snapshots[VALIDATOR.PREFLIGHT_FILE_NAME] = preflight
    finally:
        os.close(source_descriptor)
    typed_snapshots = {name: payload for name, payload in snapshots.items() if payload is not None}
    source = RUNTIME_ROOT / run_id
    return (
        source,
        _decode_json(typed_snapshots["request.json"], "request.json"),
        _decode_json(typed_snapshots["result.json"], "result.json"),
        typed_snapshots,
    )


def _validate_failure_snapshot(
    result: dict[str, Any], snapshots: dict[str, bytes]
) -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="hatifect-reproduction-evidence.") as directory:
        snapshot_root = Path(directory)
        for name in (
            "result.json",
            "failure.json",
            "failure-summary.txt",
            VALIDATOR.PREFLIGHT_FILE_NAME,
        ):
            payload = snapshots.get(name)
            if payload is not None:
                (snapshot_root / name).write_bytes(payload)
        failure = VALIDATOR.validate_failure_artifacts(
            snapshot_root / "result.json", result
        )
    if not isinstance(failure, dict):
        raise _fail("Source failure envelope is not a strict FAIL envelope.")
    return failure


def _validate_request(
    source: Path,
    request: dict[str, Any],
    source_run_id: str,
    kind: str,
    scenario: str,
    resolved: dict[str, Any],
) -> None:
    if set(request) != REQUEST_FIELDS:
        raise _fail("request.json does not match direct transport protocol v2 fields.")
    if request["protocolVersion"] != PROTOCOL_VERSION or request["requestType"] != REQUEST_TYPE:
        raise _fail("request.json has an unsupported direct transport identity.")
    _canonical_uuid(request["requestId"], "requestId")
    if request["requestId"] != source_run_id:
        raise _fail("request.json requestId does not match the source run.")
    if request["kind"] not in {"smoke", "ui"} or request["kind"] != kind:
        raise _fail("request.json kind is invalid.")
    if request["scenarioId"] != scenario:
        raise _fail("request.json scenario identity is invalid.")
    if not isinstance(request["repositoryRoot"], str) or not request["repositoryRoot"]:
        raise _fail("request.json repositoryRoot is invalid.")
    if Path(request["repositoryRoot"]) != ROOT:
        raise _fail("Source request belongs to another repository root.")
    root_stat = ROOT.stat()
    if (
        type(request["repositoryDevice"]) is not int
        or type(request["repositoryInode"]) is not int
        or request["repositoryDevice"] != root_stat.st_dev
        or request["repositoryInode"] != root_stat.st_ino
    ):
        raise _fail("Source request repository identity is stale.")
    if request["repositoryHead"] != _repository_head() or HEAD_RE.fullmatch(request["repositoryHead"]) is None:
        raise _fail("Source request repository HEAD is stale or invalid.")
    if request["environmentId"] != "isolated-smapi-v1":
        raise _fail("Source request environment identity is invalid.")
    if not isinstance(request["artifactDirectory"], str) or not isinstance(request["resultPath"], str):
        raise _fail("Source request artifact/result paths are invalid.")
    artifact = Path(request["artifactDirectory"])
    result = Path(request["resultPath"])
    if artifact != source or result != source / "result.json":
        raise _fail("Source request artifact/result paths are not request-owned.")
    if not isinstance(request["isolatedRoot"], str) or not request["isolatedRoot"]:
        raise _fail("Source request isolatedRoot is invalid.")
    if request["savePath"] is not None and not isinstance(request["savePath"], str):
        raise _fail("Source request savePath is invalid.")
    if (
        type(request["timeoutSeconds"]) is not int
        or request["timeoutSeconds"] <= 0
        or request["timeoutSeconds"] > resolved["timeoutSeconds"]
    ):
        raise _fail("Source request timeoutSeconds is invalid.")
    if type(request["seed"]) is not int or abs(request["seed"]) > 2_147_483_647:
        raise _fail("Source request seed is invalid.")
    created_text = _timestamp(request["createdAtUtc"], "createdAtUtc")
    expires_text = _timestamp(request["expiresAtUtc"], "expiresAtUtc")
    created = dt.datetime.fromisoformat(created_text.replace("Z", "+00:00"))
    expires = dt.datetime.fromisoformat(expires_text.replace("Z", "+00:00"))
    if expires <= created or (expires - created).total_seconds() > 30:
        raise _fail("Source request validity interval is invalid.")


def _select(source_run_id: str, requested_phase: str = PHASE) -> dict[str, Any]:
    if requested_phase != PHASE:
        raise _fail("Only the preflight reproduction phase is executable.")
    source_id = _canonical_uuid(source_run_id, "sourceRunId")
    source, request, result, snapshots = _source_snapshot(source_id)
    if result.get("status") != "FAIL":
        raise _fail("Only FAIL source runs can be reproduced.")
    if not isinstance(result.get("runId"), str) or result["runId"] != source_id:
        raise _fail("result.json run identity does not match the source run.")
    source_scenario = result.get("scenario")
    if not isinstance(source_scenario, str):
        raise _fail("result.json scenario is invalid.")
    source_kind = request.get("kind")
    if source_kind not in {"smoke", "ui"}:
        raise _fail("request.json kind is invalid.")
    scenarios = _load_scenarios()
    try:
        resolved = VALIDATOR.resolve_scenario(scenarios, source_scenario, source_kind)
    except Exception as error:
        raise _fail(str(error)) from error
    _validate_request(
        source,
        request,
        source_id,
        source_kind,
        source_scenario,
        resolved,
    )
    try:
        VALIDATOR.validate_result(result, source_scenario)
        failure = _validate_failure_snapshot(result, snapshots)
    except Exception as error:
        raise _fail(f"Source failure evidence is invalid: {error}") from error
    if not isinstance(failure, dict) or failure.get("status") != "FAIL":
        raise _fail("Source failure envelope is not a strict FAIL envelope.")
    root = failure.get("root_failure")
    if not isinstance(root, dict) or not isinstance(root.get("id"), str) or not isinstance(root.get("phase"), str):
        raise _fail("Source failure envelope has no bounded root failure.")
    # ``--from preflight`` describes the new run's effective start, not the
    # phase that originally failed.  A strict source FAIL may therefore have
    # a canonical runtime, scenario, validation or cleanup root.
    owners = _check_owners(scenarios, source_scenario, source_kind)
    target_scenario = source_scenario
    if source_scenario == "all":
        target_scenario = owners.get(root["id"], "")
        if not target_scenario:
            raise _fail("Aggregate root failure has no unique named check owner.")
    try:
        target_resolved = VALIDATOR.resolve_scenario(scenarios, target_scenario, source_kind)
    except Exception as error:
        raise _fail(str(error)) from error
    return {
        "source": source,
        "sourceRunId": source_id,
        "sourceScenario": source_scenario,
        "sourceKind": source_kind,
        "sourceRootFailureId": root["id"],
        "sourceRootPhase": root["phase"],
        "sourceResultFingerprint": _result_fingerprint(result),
        "sourceRepositoryHead": request["repositoryHead"],
        "sourceSeed": request["seed"],
        "targetScenario": target_scenario,
        "targetKind": source_kind,
        "targetTimeoutSeconds": target_resolved["timeoutSeconds"],
        "requestedPhase": requested_phase,
        "effectivePhase": PHASE,
    }


def select_source(source_run_id: str, requested_phase: str = PHASE) -> dict[str, Any]:
    return _select(source_run_id, requested_phase)


def _target_directory(value: str) -> tuple[str, int]:
    target = Path(value)
    if not target.is_absolute():
        target = ROOT / target
    if target.parent.resolve() != RUNTIME_ROOT.resolve():
        raise _fail("Target run must be a direct child of artifacts/runtime.")
    _canonical_uuid(target.name, "targetRunId")
    runtime_descriptor = _open_directory(RUNTIME_ROOT, "runtime root")
    try:
        target_descriptor = _open_directory(
            target.name, "target run", dir_fd=runtime_descriptor
        )
    finally:
        os.close(runtime_descriptor)
    return target.name, target_descriptor


def _write_json(directory_descriptor: int, name: str, payload: dict[str, Any]) -> None:
    encoded = (json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")
    if len(encoded) > MAX_REPRODUCTION_BYTES:
        raise _fail("reproduction.json exceeds its serialized-size bound.")
    temporary_name = f".reproduction.{uuid.uuid4()}.tmp"
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(temporary_name, flags, 0o600, dir_fd=directory_descriptor)
    except OSError as error:
        raise _fail("Cannot create private reproduction metadata.") from error
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(
                temporary_name,
                name,
                src_dir_fd=directory_descriptor,
                dst_dir_fd=directory_descriptor,
                follow_symlinks=False,
            )
        except FileExistsError as error:
            raise _fail("Target reproduction metadata already exists.") from error
        os.fsync(directory_descriptor)
    except ReproductionError:
        raise
    except OSError as error:
        raise _fail("Cannot publish reproduction metadata safely.") from error
    finally:
        try:
            os.unlink(temporary_name, dir_fd=directory_descriptor)
        except FileNotFoundError:
            pass


def materialize(source_run_id: str, target_run_dir: str | Path, expected_kind: str, expected_scenario: str, requested_phase: str = PHASE) -> dict[str, Any]:
    if expected_kind not in {"smoke", "ui"}:
        raise _fail("Expected target kind must be smoke or ui.")
    if not isinstance(expected_scenario, str) or not expected_scenario:
        raise _fail("Expected target scenario is required.")
    selection = _select(source_run_id, requested_phase)
    target_name, target_descriptor = _target_directory(str(target_run_dir))
    try:
        if selection["targetKind"] != expected_kind or selection["targetScenario"] != expected_scenario:
            raise _fail("Target kind/scenario does not match the source selection.")
        if target_name == selection["sourceRunId"]:
            raise _fail("Target run must have a fresh run ID.")
        scenarios = _load_scenarios()
        try:
            VALIDATOR.resolve_scenario(scenarios, expected_scenario, expected_kind)
        except Exception as error:
            raise _fail(str(error)) from error
        metadata = {
        "formatVersion": 1,
        "sourceRunId": selection["sourceRunId"],
        "sourceScenario": selection["sourceScenario"],
        "sourceKind": selection["sourceKind"],
        "sourceRootFailureId": selection["sourceRootFailureId"],
        "sourceRootPhase": selection["sourceRootPhase"],
        "sourceResultFingerprint": selection["sourceResultFingerprint"],
        "sourceRepositoryHead": selection["sourceRepositoryHead"],
        "sourceSeed": selection["sourceSeed"],
        "targetRunId": target_name,
        "targetScenario": expected_scenario,
        "targetKind": expected_kind,
        "requestedPhase": selection["requestedPhase"],
        "effectivePhase": selection["effectivePhase"],
        "strategy": STRATEGY,
        "checkpointStatus": CHECKPOINT_STATUS,
        "checkpointReason": CHECKPOINT_REASON,
        "createdAtUtc": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        }
        if set(metadata) != REPRODUCTION_FIELDS:
            raise _fail("Internal reproduction metadata schema drift.")
        _write_json(target_descriptor, "reproduction.json", metadata)
        return metadata
    finally:
        os.close(target_descriptor)


def _print_field(selection: dict[str, Any], field: str) -> None:
    if field == "execution":
        print(f"{selection['targetKind']}\t{selection['targetScenario']}")
        return
    aliases = {
        "scenario": "targetScenario",
        "kind": "targetKind",
        "seed": "sourceSeed",
        "phase": "effectivePhase",
        "rootId": "sourceRootFailureId",
        "rootPhase": "sourceRootPhase",
        "resultFingerprint": "sourceResultFingerprint",
        "repositoryHead": "sourceRepositoryHead",
        "sourceScenario": "sourceScenario",
        "sourceKind": "sourceKind",
    }
    key = aliases.get(field, field)
    if key not in selection:
        raise _fail(f"Unknown selectable field '{field}'.")
    value = selection[key]
    if isinstance(value, str):
        print(value)
    else:
        print(json.dumps(value, sort_keys=True))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate and materialize bounded targeted reproduction metadata.")
    subparsers = parser.add_subparsers(dest="command", required=True)
    select_parser = subparsers.add_parser("select")
    select_parser.add_argument("source_run_id")
    select_parser.add_argument("--field", required=True)
    select_parser.add_argument("--from", dest="requested_phase", default=PHASE)
    materialize_parser = subparsers.add_parser("materialize")
    materialize_parser.add_argument("source_run_id")
    materialize_parser.add_argument("target_run_dir")
    materialize_parser.add_argument("expected_kind", choices=("smoke", "ui"))
    materialize_parser.add_argument("expected_scenario")
    materialize_parser.add_argument("--from", dest="requested_phase", default=PHASE)
    materialize_parser.add_argument("--field", choices=("seed",), default=None)
    args = parser.parse_args(argv)
    try:
        if args.command == "select":
            _print_field(_select(args.source_run_id, args.requested_phase), args.field)
        elif args.command == "materialize":
            metadata = materialize(
                args.source_run_id,
                args.target_run_dir,
                args.expected_kind,
                args.expected_scenario,
                args.requested_phase,
            )
            if args.field == "seed":
                print(metadata["sourceSeed"])
        return 0
    except ReproductionError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
