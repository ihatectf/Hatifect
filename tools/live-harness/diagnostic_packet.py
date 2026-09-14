#!/usr/bin/env python3
"""Deterministic, bounded diagnostic packet projection for one failed run."""

from __future__ import annotations

import argparse
import copy
import datetime as dt
import fcntl
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
from types import ModuleType
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
RUNTIME_ROOT = ROOT / "artifacts" / "runtime"
VALIDATOR_PATH = Path(__file__).with_name("validate.py")
REPRODUCTION_PATH = Path(__file__).with_name("reproduction.py")
CONTEXT_PATH = Path(__file__).with_name("diagnostic-context.json")

PACKET_FILE_NAME = "diagnostic-packet.json"
PACKET_ERROR_PATH = "diagnostics/diagnostic-packet-error.json"
PACKET_LOCK_FILE_NAME = ".diagnostic-packet.lock"
PACKET_FORMAT_VERSION = 1
PACKET_ERROR_FORMAT_VERSION = 1
MAX_PACKET_BYTES = 128 * 1024
MAX_PACKET_ERROR_BYTES = 8 * 1024
MAX_DOCUMENT_BYTES = 256 * 1024
MAX_CONTEXT_BYTES = 64 * 1024
MAX_REPRODUCTION_DETAIL = 512
MAX_SUMMARY_TEXT = 512
UUID_RE = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"
)
HEAD_RE = re.compile(r"^[0-9a-f]{40}$")

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

PACKET_FILE_ORDER = (
    "diagnostic-summary.md",
    "failure.json",
    "semantic-tail.json",
    "preflight-environment-summary.json",
    "reproduction.json",
    "artifacts.json",
    "context-manifest.json",
    "packet-manifest.json",
)

RAW_LOG_NAMES = {
    "harness.log",
    "semantic-test-agent.log",
    "smapi-console.log",
    "smapi.log",
    "transport.log",
}
DISALLOWED_SUFFIXES = {
    ".binlog",
    ".dll",
    ".dmp",
    ".exe",
    ".pdb",
    ".png",
    ".sav",
    ".tar",
    ".trx",
    ".zip",
}


class DiagnosticPacketError(ValueError):
    """Canonical evidence cannot be projected into a safe packet."""

    def __init__(self, reason_code: str, message: str) -> None:
        super().__init__(message)
        self.reason_code = reason_code


def _fail(reason_code: str, message: str) -> DiagnosticPacketError:
    return DiagnosticPacketError(reason_code, message)


def _load_module(name: str, path: Path) -> ModuleType:
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise _fail("MODULE_UNAVAILABLE", f"Cannot load {path.name}.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _canonical_uuid(value: Any, field: str) -> str:
    if not isinstance(value, str) or UUID_RE.fullmatch(value) is None:
        raise _fail("INVALID_RUN_ID", f"{field} must be a canonical UUID.")
    try:
        parsed = uuid.UUID(value)
    except ValueError as error:
        raise _fail("INVALID_RUN_ID", f"{field} must be a canonical UUID.") from error
    if parsed.int == 0 or str(parsed) != value:
        raise _fail("INVALID_RUN_ID", f"{field} must be a canonical UUID.")
    return value


def _open_directory(
    path: str | Path,
    description: str,
    *,
    dir_fd: int | None = None,
) -> int:
    flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags, dir_fd=dir_fd)
    except OSError as error:
        raise _fail("OWNERSHIP_VIOLATION", f"Cannot open {description} safely.") from error
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid():
            raise _fail(
                "OWNERSHIP_VIOLATION",
                f"{description} must be a current-user real directory.",
            )
        return descriptor
    except Exception:
        os.close(descriptor)
        raise


def _relative_parts(relative: str, description: str) -> tuple[str, ...]:
    path = Path(relative)
    parts = path.parts
    if (
        not relative
        or path.is_absolute()
        or not parts
        or any(part in {"", ".", ".."} for part in parts)
    ):
        raise _fail("PATH_TRAVERSAL", f"{description} is not a safe relative path.")
    return parts


def _open_parent_at(
    root_descriptor: int,
    relative: str,
    description: str,
) -> tuple[int, str]:
    parts = _relative_parts(relative, description)
    descriptor = os.dup(root_descriptor)
    try:
        for part in parts[:-1]:
            child = _open_directory(part, description, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        return descriptor, parts[-1]
    except Exception:
        os.close(descriptor)
        raise


def _read_bytes_at(
    root_descriptor: int,
    relative: str,
    description: str,
    maximum: int,
    *,
    optional: bool = False,
) -> bytes | None:
    parent, name = _open_parent_at(root_descriptor, relative, description)
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        try:
            descriptor = os.open(name, flags, dir_fd=parent)
        except FileNotFoundError:
            if optional:
                return None
            raise _fail("MISSING_ARTIFACT", f"Missing {description}.")
        except OSError as error:
            raise _fail(
                "OWNERSHIP_VIOLATION", f"Cannot open {description} safely."
            ) from error
        try:
            info = os.fstat(descriptor)
            if (
                not stat.S_ISREG(info.st_mode)
                or info.st_uid != os.getuid()
                or info.st_nlink != 1
            ):
                raise _fail(
                    "OWNERSHIP_VIOLATION",
                    f"{description} must be a current-user, single-link regular file.",
                )
            if info.st_size > maximum:
                raise _fail("SIZE_BOUND_EXCEEDED", f"{description} exceeds its size bound.")
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
                raise _fail("SIZE_BOUND_EXCEEDED", f"{description} exceeds its size bound.")
            return payload
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)


def _stat_file_at(
    root_descriptor: int,
    relative: str,
    description: str,
) -> os.stat_result | None:
    try:
        parent, name = _open_parent_at(root_descriptor, relative, description)
    except DiagnosticPacketError as error:
        if isinstance(error.__cause__, FileNotFoundError):
            return None
        raise
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        try:
            descriptor = os.open(name, flags, dir_fd=parent)
        except FileNotFoundError:
            return None
        except OSError as error:
            raise _fail(
                "OWNERSHIP_VIOLATION", f"Cannot inspect {description} safely."
            ) from error
        try:
            info = os.fstat(descriptor)
            if (
                not stat.S_ISREG(info.st_mode)
                or info.st_uid != os.getuid()
                or info.st_nlink != 1
            ):
                raise _fail(
                    "OWNERSHIP_VIOLATION",
                    f"{description} must be a current-user, single-link regular file.",
                )
            return info
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)


def _decode_json(payload: bytes, description: str) -> dict[str, Any]:
    try:
        value = json.loads(payload.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise _fail("MALFORMED_ARTIFACT", f"{description} is not valid JSON.") from error
    if not isinstance(value, dict):
        raise _fail("MALFORMED_ARTIFACT", f"{description} must be a JSON object.")
    return value


def _serialize(payload: Any) -> bytes:
    return (
        json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    ).encode("utf-8")


def _bounded_text(value: Any, maximum: int = MAX_SUMMARY_TEXT) -> str:
    text = " ".join(str(value).split())
    if len(text) <= maximum:
        return text
    return text[: maximum - 3] + "..."


def _git_head() -> str:
    try:
        completed = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "--verify", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        )
    except (OSError, subprocess.CalledProcessError) as error:
        raise _fail("REPOSITORY_IDENTITY_INVALID", "Cannot determine repository HEAD.") from error
    head = completed.stdout.strip()
    if HEAD_RE.fullmatch(head) is None:
        raise _fail("REPOSITORY_IDENTITY_INVALID", "Repository HEAD is invalid.")
    return head


def _validate_request(
    request: dict[str, Any],
    *,
    run_path: Path,
    run_id: str,
    scenario: str,
    resolved: dict[str, Any],
) -> None:
    if set(request) != REQUEST_FIELDS:
        raise _fail(
            "REQUEST_IDENTITY_INVALID",
            "request.json does not match direct transport protocol v2 fields.",
        )
    if request["protocolVersion"] != 2 or request["requestType"] != "runScenario":
        raise _fail("REQUEST_IDENTITY_INVALID", "request.json transport identity is invalid.")
    if not isinstance(request["requestId"], str) or request["requestId"] != run_id:
        raise _fail("REQUEST_IDENTITY_INVALID", "request.json run identity mismatches.")
    _canonical_uuid(request["requestId"], "requestId")
    if (
        not isinstance(request["scenarioId"], str)
        or not isinstance(request["kind"], str)
        or request["scenarioId"] != scenario
        or request["kind"] != resolved["kind"]
    ):
        raise _fail("REQUEST_IDENTITY_INVALID", "request.json scenario identity mismatches.")
    if (
        not isinstance(request["artifactDirectory"], str)
        or Path(request["artifactDirectory"]) != run_path
    ):
        raise _fail("REQUEST_IDENTITY_INVALID", "request.json artifact directory is not request-owned.")
    if (
        not isinstance(request["resultPath"], str)
        or Path(request["resultPath"]) != run_path / "result.json"
    ):
        raise _fail("REQUEST_IDENTITY_INVALID", "request.json result path is not request-owned.")
    if (
        not isinstance(request["repositoryRoot"], str)
        or Path(request["repositoryRoot"]) != ROOT
    ):
        raise _fail("REPOSITORY_IDENTITY_INVALID", "Run belongs to another repository root.")
    root_info = ROOT.stat()
    if (
        type(request["repositoryDevice"]) is not int
        or type(request["repositoryInode"]) is not int
        or request["repositoryDevice"] != root_info.st_dev
        or request["repositoryInode"] != root_info.st_ino
    ):
        raise _fail("REPOSITORY_IDENTITY_INVALID", "Run repository identity is stale.")
    if request["repositoryHead"] != _git_head():
        raise _fail("REPOSITORY_IDENTITY_INVALID", "Run repository HEAD is stale.")
    if request["environmentId"] != "isolated-smapi-v1":
        raise _fail("REQUEST_IDENTITY_INVALID", "Run environment identity is invalid.")
    if not isinstance(request["isolatedRoot"], str) or not request["isolatedRoot"]:
        raise _fail("REQUEST_IDENTITY_INVALID", "Run isolated root is invalid.")
    if request["savePath"] is not None and not isinstance(request["savePath"], str):
        raise _fail("REQUEST_IDENTITY_INVALID", "Run save path is invalid.")
    if (
        type(request["timeoutSeconds"]) is not int
        or request["timeoutSeconds"] <= 0
        or request["timeoutSeconds"] > resolved["timeoutSeconds"]
    ):
        raise _fail("REQUEST_IDENTITY_INVALID", "Run timeout is invalid.")
    if (
        type(request["seed"]) is not int
        or abs(request["seed"]) > 2_147_483_647
    ):
        raise _fail("REQUEST_IDENTITY_INVALID", "Run seed is invalid.")
    try:
        created = request["createdAtUtc"]
        expires = request["expiresAtUtc"]
        if (
            not isinstance(created, str)
            or not isinstance(expires, str)
            or len(created) > 128
            or len(expires) > 128
        ):
            raise ValueError
        created_at = dt.datetime.fromisoformat(created.replace("Z", "+00:00"))
        expires_at = dt.datetime.fromisoformat(expires.replace("Z", "+00:00"))
        if (
            created_at.tzinfo is None
            or expires_at.tzinfo is None
            or expires_at <= created_at
            or (expires_at - created_at).total_seconds() > 30
        ):
            raise ValueError
    except (TypeError, ValueError) as error:
        raise _fail("REQUEST_IDENTITY_INVALID", "Run validity interval is invalid.") from error


def _validate_failure_snapshot(
    validator: ModuleType,
    result: dict[str, Any],
    snapshots: dict[str, bytes],
) -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="hatifect-diagnostic-evidence.") as directory:
        root = Path(directory)
        for name in (
            "result.json",
            "failure.json",
            "failure-summary.txt",
            validator.PREFLIGHT_FILE_NAME,
        ):
            payload = snapshots.get(name)
            if payload is not None:
                (root / name).write_bytes(payload)
        try:
            failure = validator.validate_failure_artifacts(root / "result.json", result)
        except Exception as error:
            raise _fail(
                "FAILURE_EVIDENCE_INVALID",
                f"Canonical failure evidence is invalid: {error}",
            ) from error
    if not isinstance(failure, dict):
        raise _fail("FAILURE_EVIDENCE_INVALID", "Run has no strict failure envelope.")
    return failure


def _read_semantic_events(
    validator: ModuleType,
    payload: bytes | None,
    *,
    scenario: str,
    run_id: str,
) -> list[dict[str, Any]]:
    if payload is None:
        return []
    with tempfile.TemporaryDirectory(prefix="hatifect-diagnostic-events.") as directory:
        root = Path(directory)
        stream_path = root / validator.SEMANTIC_EVENT_FILE_NAME
        stream_path.write_bytes(payload)
        stream_path.chmod(0o600)
        try:
            return validator.read_semantic_events(
                root,
                expected_scenario=scenario,
                expected_run_id=run_id,
            )
        except Exception as error:
            raise _fail(
                "SEMANTIC_STREAM_INVALID",
                f"Canonical semantic event stream is invalid: {error}",
            ) from error


def _expected_semantic_tail(
    validator: ModuleType,
    events: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    tail: list[dict[str, Any]] = []
    size = 0
    for event in reversed(events[-validator.MAX_FAILURE_TAIL_EVENTS :]):
        event_size = len(validator.serialize_semantic_event(event))
        if tail and size + event_size > validator.MAX_FAILURE_TAIL_BYTES:
            break
        if event_size > validator.MAX_FAILURE_TAIL_BYTES:
            continue
        tail.append(event)
        size += event_size
    return list(reversed(tail))


def _read_source_snapshot(
    run_id: str,
    validator: ModuleType,
) -> tuple[
    int,
    Path,
    dict[str, bytes],
    dict[str, Any],
    dict[str, Any],
    dict[str, Any],
    dict[str, Any] | None,
    list[dict[str, Any]],
]:
    runtime_descriptor = _open_directory(RUNTIME_ROOT, "runtime artifact root")
    try:
        run_descriptor = _open_directory(run_id, "source run", dir_fd=runtime_descriptor)
    finally:
        os.close(runtime_descriptor)
    run_path = RUNTIME_ROOT / run_id
    try:
        snapshots: dict[str, bytes] = {}
        bounds = {
            "request.json": MAX_DOCUMENT_BYTES,
            "result.json": MAX_DOCUMENT_BYTES,
            "failure.json": validator.MAX_FAILURE_ENVELOPE_BYTES,
            "failure-summary.txt": validator.MAX_FAILURE_SUMMARY_CHARACTERS * 4,
        }
        for name, maximum in bounds.items():
            payload = _read_bytes_at(run_descriptor, name, name, maximum)
            assert payload is not None
            snapshots[name] = payload
        preflight_bytes = _read_bytes_at(
            run_descriptor,
            validator.PREFLIGHT_FILE_NAME,
            validator.PREFLIGHT_FILE_NAME,
            validator.MAX_PREFLIGHT_BYTES,
            optional=True,
        )
        if preflight_bytes is not None:
            snapshots[validator.PREFLIGHT_FILE_NAME] = preflight_bytes
        semantic_bytes = _read_bytes_at(
            run_descriptor,
            validator.SEMANTIC_EVENT_FILE_NAME,
            validator.SEMANTIC_EVENT_FILE_NAME,
            validator.MAX_SEMANTIC_STREAM_BYTES,
            optional=True,
        )
        if semantic_bytes is not None:
            snapshots[validator.SEMANTIC_EVENT_FILE_NAME] = semantic_bytes

        request = _decode_json(snapshots["request.json"], "request.json")
        result = _decode_json(snapshots["result.json"], "result.json")
        try:
            validator.validate_result(result)
        except Exception as error:
            raise _fail("RESULT_INVALID", f"Canonical result.json is invalid: {error}") from error
        if result["status"] == "PASS":
            raise _fail("PASS_NOT_DIAGNOSTIC", "A PASS run has no failure diagnostic packet.")
        if result["runId"] != run_id:
            raise _fail("RESULT_IDENTITY_INVALID", "result.json run identity mismatches.")
        failure = _validate_failure_snapshot(validator, result, snapshots)
        try:
            resolved = validator.resolve_scenario(
                validator.load_manifest(), result["scenario"], request.get("kind")
            )
        except Exception as error:
            raise _fail(
                "SCENARIO_IDENTITY_INVALID",
                f"Run scenario registration is invalid: {error}",
            ) from error
        _validate_request(
            request,
            run_path=run_path,
            run_id=run_id,
            scenario=result["scenario"],
            resolved=resolved,
        )

        preflight: dict[str, Any] | None = None
        if preflight_bytes is not None:
            preflight = _decode_json(preflight_bytes, validator.PREFLIGHT_FILE_NAME)
            try:
                validator.validate_preflight_report(
                    preflight,
                    expected_scenario=result["scenario"],
                    expected_run_id=run_id,
                    expected_requirements=resolved["capabilities"],
                )
            except Exception as error:
                raise _fail(
                    "PREFLIGHT_INVALID",
                    f"Canonical preflight report is invalid: {error}",
                ) from error

        events = _read_semantic_events(
            validator,
            semantic_bytes,
            scenario=result["scenario"],
            run_id=run_id,
        )
        expected_tail = _expected_semantic_tail(validator, events)
        if (
            failure["format_version"] == validator.FAILURE_ENVELOPE_FORMAT_VERSION
            and failure["semantic_event_tail"] != expected_tail
        ):
            raise _fail(
                "SEMANTIC_TAIL_STALE",
                "failure.json semantic tail does not match the canonical stream.",
            )
        return (
            run_descriptor,
            run_path,
            snapshots,
            request,
            result,
            failure,
            preflight,
            events,
        )
    except Exception:
        os.close(run_descriptor)
        raise


def _load_context_mapping(repository_descriptor: int) -> dict[str, Any]:
    relative = CONTEXT_PATH.relative_to(ROOT).as_posix()
    payload = _read_bytes_at(
        repository_descriptor,
        relative,
        "diagnostic context mapping",
        MAX_CONTEXT_BYTES,
    )
    assert payload is not None
    mapping = _decode_json(payload, "diagnostic context mapping")
    if set(mapping) != {"formatVersion", "components", "scenarioRules"}:
        raise _fail("CONTEXT_MAPPING_INVALID", "Context mapping field set is invalid.")
    if mapping["formatVersion"] != 1:
        raise _fail("CONTEXT_MAPPING_INVALID", "Context mapping format is unsupported.")
    components = mapping["components"]
    rules = mapping["scenarioRules"]
    if not isinstance(components, dict) or not components or not isinstance(rules, list):
        raise _fail("CONTEXT_MAPPING_INVALID", "Context mapping content is invalid.")
    return mapping


def _validate_context_entry(entry: Any, description: str) -> dict[str, Any]:
    fields = {
        "directDependencies",
        "primarySourceFiles",
        "scenarioTestFiles",
        "secondaryExpansionCandidates",
    }
    if not isinstance(entry, dict) or set(entry) != fields:
        raise _fail("CONTEXT_MAPPING_INVALID", f"{description} field set is invalid.")
    for field in fields:
        values = entry[field]
        if (
            not isinstance(values, list)
            or any(not isinstance(value, str) or not value for value in values)
            or values != list(dict.fromkeys(values))
        ):
            raise _fail("CONTEXT_MAPPING_INVALID", f"{description} {field} is invalid.")
    return entry


def _scenario_rule(
    rules: list[Any],
    scenario: str,
    kind: str,
) -> dict[str, Any]:
    matches: list[tuple[int, dict[str, Any]]] = []
    for rule in rules:
        if not isinstance(rule, dict) or set(rule) != {
            "pattern",
            "kinds",
            "component",
            "context",
        }:
            raise _fail("CONTEXT_MAPPING_INVALID", "Scenario context rule is malformed.")
        pattern = rule["pattern"]
        kinds = rule["kinds"]
        if (
            not isinstance(pattern, str)
            or not pattern
            or not isinstance(kinds, list)
            or any(value not in {"smoke", "ui"} for value in kinds)
        ):
            raise _fail("CONTEXT_MAPPING_INVALID", "Scenario context rule identity is invalid.")
        if pattern.endswith("*"):
            prefix = pattern[:-1]
            matched = bool(prefix) and scenario.startswith(prefix)
            score = len(prefix)
        else:
            matched = scenario == pattern
            score = len(pattern) + 10_000
        if matched and kind in kinds:
            matches.append((score, rule))
    if not matches:
        raise _fail("CONTEXT_OWNER_UNKNOWN", f"Scenario '{scenario}' has no context owner.")
    matches.sort(key=lambda item: item[0], reverse=True)
    if len(matches) > 1 and matches[0][0] == matches[1][0]:
        raise _fail("CONTEXT_OWNER_AMBIGUOUS", f"Scenario '{scenario}' has ambiguous context owners.")
    return matches[0][1]


def _stable_union(*collections: list[str]) -> list[str]:
    values: list[str] = []
    seen: set[str] = set()
    for collection in collections:
        for value in collection:
            if value not in seen:
                seen.add(value)
                values.append(value)
    return values


def _verify_repository_files(
    repository_descriptor: int,
    paths: list[str],
) -> None:
    for relative in paths:
        info = _stat_file_at(
            repository_descriptor,
            relative,
            f"context path '{relative}'",
        )
        if info is None:
            raise _fail("CONTEXT_PATH_MISSING", f"Context path '{relative}' does not exist.")


def _build_context_manifest(
    validator: ModuleType,
    request: dict[str, Any],
    result: dict[str, Any],
    failure: dict[str, Any],
) -> dict[str, Any]:
    repository_descriptor = _open_directory(ROOT, "repository root")
    try:
        mapping = _load_context_mapping(repository_descriptor)
        resolved = validator.resolve_scenario(
            validator.load_manifest(), result["scenario"], request["kind"]
        )
        owned_scenario = result["scenario"]
        ownership_source = "scenario-registration"
        if result["scenario"] == "all":
            check_owners = resolved.get("checkOwners")
            if not isinstance(check_owners, dict):
                raise _fail("CONTEXT_OWNER_AMBIGUOUS", "Aggregate scenario has no checkOwners.")
            owner = check_owners.get(failure["root_failure"]["id"])
            if owner is not None:
                if not isinstance(owner, str) or not owner:
                    raise _fail("CONTEXT_OWNER_AMBIGUOUS", "Aggregate root owner is invalid.")
                owned_scenario = owner
                ownership_source = "scenario-registration/checkOwners"

        rule = _scenario_rule(mapping["scenarioRules"], owned_scenario, request["kind"])
        scenario_context = _validate_context_entry(
            rule["context"], f"scenario rule '{rule['pattern']}'"
        )
        component_name = failure["causal_component"]
        if component_name == "scenario":
            component_name = rule["component"]
        component = mapping["components"].get(component_name)
        if component is None:
            raise _fail(
                "CONTEXT_OWNER_UNKNOWN",
                f"Causal component '{failure['causal_component']}' has no context owner.",
            )
        component_context = _validate_context_entry(
            component, f"component '{component_name}'"
        )
        direct_dependencies = _stable_union(
            component_context["directDependencies"],
            scenario_context["directDependencies"],
        )
        primary = _stable_union(
            component_context["primarySourceFiles"],
            scenario_context["primarySourceFiles"],
        )
        tests = _stable_union(
            component_context["scenarioTestFiles"],
            scenario_context["scenarioTestFiles"],
        )
        secondary = _stable_union(
            component_context["secondaryExpansionCandidates"],
            scenario_context["secondaryExpansionCandidates"],
        )
        _verify_repository_files(repository_descriptor, primary + tests + secondary)
        return {
            "scenario": result["scenario"],
            "resolvedScenarioOwner": owned_scenario,
            "rootFailureId": failure["root_failure"]["id"],
            "rootComponent": failure["causal_component"],
            "resolvedComponentOwner": component_name,
            "directDependencies": direct_dependencies,
            "primarySourceFiles": primary,
            "scenarioTestFiles": tests,
            "secondaryExpansionCandidates": secondary,
            "secondaryExpansionOmitted": 0,
            "ownershipSource": (
                f"tools/live-harness/scenarios.json + {ownership_source} + "
                "tools/live-harness/diagnostic-context.json"
            ),
            "repositoryHead": request["repositoryHead"],
        }
    finally:
        os.close(repository_descriptor)


def _semantic_projection(
    validator: ModuleType,
    failure: dict[str, Any],
    events: list[dict[str, Any]],
) -> dict[str, Any]:
    tail = copy.deepcopy(
        failure.get("semantic_event_tail")
        if failure["format_version"] == validator.FAILURE_ENVELOPE_FORMAT_VERSION
        else _expected_semantic_tail(validator, events)
    )
    completed = any(
        event["event"] == "Scenario.Completed"
        and event["fields"].get("result_fingerprint") == failure["result_fingerprint"]
        for event in tail
    )
    return {
        "formatVersion": validator.SEMANTIC_EVENT_FORMAT_VERSION,
        "includedCount": len(tail),
        "excludedCount": len(events) - len(tail),
        "fullStreamPath": (
            validator.SEMANTIC_EVENT_FILE_NAME if events else None
        ),
        "completionStatus": "confirmed" if completed else "unavailable",
        "events": tail,
    }


def _preflight_environment_projection(
    preflight: dict[str, Any] | None,
    failure: dict[str, Any],
) -> dict[str, Any]:
    projected: dict[str, Any] | None = None
    if preflight is not None:
        projected = {
            "formatVersion": preflight["formatVersion"],
            "scenario": preflight["scenario"],
            "runId": preflight["runId"],
            "status": preflight["status"],
            "counts": preflight["counts"],
            "capabilities": [
                {
                    "id": item["id"],
                    "requirement": item["requirement"],
                    "status": item["status"],
                    "classification": item["classification"],
                    "reasonCode": item["reasonCode"],
                    "owner": item["owner"],
                    "probeLayer": item["probeLayer"],
                }
                for item in preflight["capabilities"]
            ],
        }
    return {
        "environmentSummary": copy.deepcopy(failure["environment_summary"]),
        "preflight": projected,
    }


def _select_reproduction(run_id: str) -> dict[str, Any]:
    reproduction = _load_module("hatifect_diagnostic_reproduction", REPRODUCTION_PATH)
    return reproduction.select_source(run_id)


def _reproduction_projection(
    run_id: str,
    result: dict[str, Any],
) -> dict[str, Any]:
    if result["status"] != "FAIL":
        return {
            "available": False,
            "command": None,
            "reasonCode": "STATUS_BLOCKED",
            "detail": "Targeted reproduction is available only for a validated FAIL run.",
        }
    try:
        selection = _select_reproduction(run_id)
    except Exception as error:
        return {
            "available": False,
            "command": None,
            "reasonCode": "SOURCE_NOT_REPRODUCIBLE",
            "detail": (
                "Validated Phase 4 reproduction selection rejected this run "
                f"({type(error).__name__})."
            )[:MAX_REPRODUCTION_DETAIL],
        }
    return {
        "available": True,
        "command": f"./tools/hatifect-repro {run_id}",
        "reasonCode": None,
        "detail": None,
        "targetScenario": selection["targetScenario"],
        "effectivePhase": selection["effectivePhase"],
    }


def _artifact_exclusion(path: str, artifact_type: str, size: int) -> str:
    name = Path(path).name.lower()
    suffix = Path(path).suffix.lower()
    if name in RAW_LOG_NAMES or artifact_type in {"smapi-log"}:
        return "RAW_LOG_REFERENCE_ONLY"
    if suffix in DISALLOWED_SUFFIXES or artifact_type == "screenshot":
        return "BINARY_ARTIFACT_REFERENCE_ONLY"
    if size > 32 * 1024:
        return "SIZE_BUDGET_REFERENCE_ONLY"
    return "SOURCE_ARTIFACT_REFERENCE_ONLY"


def _artifact_references(
    run_descriptor: int,
    validator: ModuleType,
    result: dict[str, Any],
    failure: dict[str, Any],
) -> list[dict[str, Any]]:
    types = {
        artifact["path"]: artifact["type"]
        for artifact in result["artifacts"]
        if isinstance(artifact, dict)
    }
    candidates = _stable_union(
        ["result.json", "failure.json", "failure-summary.txt"],
        list(failure["relevant_artifacts"]),
    )
    projected = {
        "result.json",
        "failure.json",
        validator.PREFLIGHT_FILE_NAME,
        validator.SEMANTIC_EVENT_FILE_NAME,
        "reproduction.json",
    }
    references: list[dict[str, Any]] = []
    for relative in candidates:
        info = _stat_file_at(
            run_descriptor,
            relative,
            f"artifact '{relative}'",
        )
        artifact_type = types.get(relative, "canonical")
        if info is None:
            references.append(
                {
                    "path": relative,
                    "artifactType": artifact_type,
                    "sizeBytes": None,
                    "inclusionStatus": "excluded",
                    "exclusionReason": "MISSING_REFERENCED_ARTIFACT",
                }
            )
            continue
        if relative in projected:
            status = "projected"
            reason = "BOUNDED_CANONICAL_PROJECTION"
        else:
            status = "reference-only"
            reason = _artifact_exclusion(relative, artifact_type, info.st_size)
        references.append(
            {
                "path": relative,
                "artifactType": artifact_type,
                "sizeBytes": info.st_size,
                "inclusionStatus": status,
                "exclusionReason": reason,
            }
        )
    return references


def _failure_projection(failure: dict[str, Any]) -> dict[str, Any]:
    envelope = copy.deepcopy(failure)
    return {
        "projectionFormatVersion": 1,
        "sourcePath": "failure.json",
        "sourceResultFingerprint": failure["result_fingerprint"],
        "recordCounts": {
            "cascade": len(failure["cascade_records"]),
            "additional": len(failure["additional_failures"]),
            "cleanup": len(failure["cleanup_failures"]),
        },
        "omittedRecordCounts": {
            "cascade": 0,
            "additional": 0,
            "cleanup": 0,
        },
        "envelope": envelope,
    }


def _markdown_value(value: Any) -> str:
    return _bounded_text(value, MAX_SUMMARY_TEXT).replace("`", "'")


def _diagnostic_summary(
    result: dict[str, Any],
    failure: dict[str, Any],
    reproduction: dict[str, Any],
) -> str:
    root = failure["root_failure"]
    reproduction_text = (
        reproduction["command"]
        if reproduction["available"]
        else f"unavailable ({reproduction['reasonCode']})"
    )
    return "\n".join(
        (
            "# Hatifect diagnostic summary",
            "",
            f"- Run ID: `{result['runId']}`",
            f"- Scenario: `{result['scenario']}`",
            f"- Status: `{result['status']}`",
            f"- Root failure ID: `{root['id']}`",
            f"- Execution phase: `{failure['phase']}`",
            f"- Failure class: `{failure['failure_class']}`",
            f"- Causal component: `{failure['causal_component']}`",
            f"- Expected: {_markdown_value(failure['expected'])}",
            f"- Actual: {_markdown_value(failure['actual'])}",
            f"- Message: {_markdown_value(failure['message'])}",
            f"- Result fingerprint: `{failure['result_fingerprint']}`",
            f"- Additional failures: `{len(failure['additional_failures'])}`",
            f"- Cleanup failures: `{len(failure['cleanup_failures'])}`",
            f"- Reproduction: `{reproduction_text}`",
            "",
        )
    )


def _packet_document(
    *,
    summary: str,
    failure: dict[str, Any],
    semantic: dict[str, Any],
    preflight: dict[str, Any],
    reproduction: dict[str, Any],
    artifacts: list[dict[str, Any]],
    context: dict[str, Any],
    manifest: dict[str, Any],
) -> dict[str, Any]:
    content = {
        "diagnostic-summary.md": summary,
        "failure.json": failure,
        "semantic-tail.json": semantic,
        "preflight-environment-summary.json": preflight,
        "reproduction.json": reproduction,
        "artifacts.json": {"artifacts": artifacts},
        "context-manifest.json": context,
        "packet-manifest.json": manifest,
    }
    return {
        "formatVersion": PACKET_FORMAT_VERSION,
        "files": [
            {
                "path": path,
                "mediaType": (
                    "text/markdown"
                    if path.endswith(".md")
                    else "application/json"
                ),
                "content": content[path],
            }
            for path in PACKET_FILE_ORDER
        ],
    }


def _encode_with_size(
    build: Any,
    manifest: dict[str, Any],
) -> bytes:
    for _ in range(12):
        encoded = _serialize(build())
        size = len(encoded)
        if manifest["serializedBytes"] == size:
            return encoded
        manifest["serializedBytes"] = size
    raise _fail("PACKET_SIZE_UNSTABLE", "Packet serialized size did not stabilize.")


def _compact_non_root_text(projection: dict[str, Any]) -> bool:
    changed = False
    envelope = projection["envelope"]
    for collection in ("additional_failures", "cascade_records", "cleanup_failures"):
        for record in envelope[collection]:
            for field in ("expected", "actual", "message"):
                compacted = _bounded_text(record[field], 128)
                if compacted != record[field]:
                    record[field] = compacted
                    changed = True
    return changed


def _drop_non_root_record(projection: dict[str, Any]) -> bool:
    envelope = projection["envelope"]
    for collection, count_name in (
        ("additional_failures", "additional"),
        ("cascade_records", "cascade"),
        ("cleanup_failures", "cleanup"),
    ):
        if envelope[collection]:
            envelope[collection].pop()
            projection["omittedRecordCounts"][count_name] += 1
            return True
    return False


def _build_bounded_packet(
    *,
    summary: str,
    failure: dict[str, Any],
    semantic: dict[str, Any],
    preflight: dict[str, Any],
    reproduction: dict[str, Any],
    artifacts: list[dict[str, Any]],
    context: dict[str, Any],
) -> bytes:
    manifest = {
        "formatVersion": PACKET_FORMAT_VERSION,
        "budgetBytes": MAX_PACKET_BYTES,
        "serializedBytes": 0,
        "resultFingerprint": failure["sourceResultFingerprint"],
        "files": list(PACKET_FILE_ORDER),
        "truncation": {
            "secondaryContextOmitted": 0,
            "semanticEventsOmitted": semantic["excludedCount"],
            "nonRootTextCompacted": False,
            "nonRootRecordsOmitted": 0,
        },
    }

    def build() -> dict[str, Any]:
        return _packet_document(
            summary=summary,
            failure=failure,
            semantic=semantic,
            preflight=preflight,
            reproduction=reproduction,
            artifacts=artifacts,
            context=context,
            manifest=manifest,
        )

    encoded = _encode_with_size(build, manifest)
    while len(encoded) > MAX_PACKET_BYTES and context["secondaryExpansionCandidates"]:
        context["secondaryExpansionCandidates"].pop()
        context["secondaryExpansionOmitted"] += 1
        manifest["truncation"]["secondaryContextOmitted"] += 1
        encoded = _encode_with_size(build, manifest)
    while len(encoded) > MAX_PACKET_BYTES and semantic["events"]:
        semantic["events"].pop(0)
        if failure["envelope"].get("semantic_event_tail"):
            failure["envelope"]["semantic_event_tail"].pop(0)
        semantic["includedCount"] -= 1
        semantic["excludedCount"] += 1
        manifest["truncation"]["semanticEventsOmitted"] += 1
        encoded = _encode_with_size(build, manifest)
    if len(encoded) > MAX_PACKET_BYTES and _compact_non_root_text(failure):
        manifest["truncation"]["nonRootTextCompacted"] = True
        encoded = _encode_with_size(build, manifest)
    while len(encoded) > MAX_PACKET_BYTES and _drop_non_root_record(failure):
        manifest["truncation"]["nonRootRecordsOmitted"] += 1
        encoded = _encode_with_size(build, manifest)
    if len(encoded) > MAX_PACKET_BYTES:
        raise _fail(
            "MANDATORY_CORE_EXCEEDS_BUDGET",
            "Mandatory diagnostic packet core exceeds the hard serialized-size budget.",
        )
    return encoded


def _publication_lock(run_descriptor: int) -> int:
    flags = os.O_CREAT | os.O_RDWR | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(PACKET_LOCK_FILE_NAME, flags, 0o600, dir_fd=run_descriptor)
    except OSError as error:
        raise _fail("PUBLICATION_FAILED", "Cannot open packet publication lock.") from error
    info = os.fstat(descriptor)
    if (
        not stat.S_ISREG(info.st_mode)
        or info.st_uid != os.getuid()
        or info.st_nlink != 1
        or stat.S_IMODE(info.st_mode) != 0o600
    ):
        os.close(descriptor)
        raise _fail("OWNERSHIP_VIOLATION", "Packet publication lock is not private.")
    fcntl.flock(descriptor, fcntl.LOCK_EX)
    return descriptor


def _write_exclusive_at(
    directory_descriptor: int,
    target: str,
    payload: bytes,
    *,
    maximum: int,
) -> str:
    if len(payload) > maximum:
        raise _fail("SIZE_BOUND_EXCEEDED", f"{target} exceeds its size bound.")
    temporary = f".{target}.{uuid.uuid4().hex}.tmp"
    flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(temporary, flags, 0o600, dir_fd=directory_descriptor)
    except OSError as error:
        raise _fail("PUBLICATION_FAILED", f"Cannot create private {target}.") from error
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        try:
            os.link(
                temporary,
                target,
                src_dir_fd=directory_descriptor,
                dst_dir_fd=directory_descriptor,
                follow_symlinks=False,
            )
        except FileExistsError:
            existing = _read_bytes_at(
                directory_descriptor,
                target,
                target,
                maximum,
            )
            if existing != payload:
                raise _fail(
                    "PACKET_ALREADY_PUBLISHED",
                    f"{target} already exists with different content.",
                )
            return "existing"
        except OSError as error:
            raise _fail("PUBLICATION_FAILED", f"Cannot publish {target} safely.") from error
        os.fsync(directory_descriptor)
        return "created"
    finally:
        try:
            os.unlink(temporary, dir_fd=directory_descriptor)
        except FileNotFoundError:
            pass


def _ensure_directory_at(
    parent_descriptor: int,
    name: str,
    description: str,
) -> int:
    try:
        os.mkdir(name, 0o700, dir_fd=parent_descriptor)
    except FileExistsError:
        pass
    except OSError as error:
        raise _fail("PUBLICATION_FAILED", f"Cannot create {description}.") from error
    return _open_directory(name, description, dir_fd=parent_descriptor)


def _verify_source_snapshot(
    run_descriptor: int,
    snapshots: dict[str, bytes],
    validator: ModuleType,
) -> None:
    names = (
        "request.json",
        "result.json",
        "failure.json",
        "failure-summary.txt",
        validator.PREFLIGHT_FILE_NAME,
        validator.SEMANTIC_EVENT_FILE_NAME,
    )
    bounds = {
        "request.json": MAX_DOCUMENT_BYTES,
        "result.json": MAX_DOCUMENT_BYTES,
        "failure.json": validator.MAX_FAILURE_ENVELOPE_BYTES,
        "failure-summary.txt": validator.MAX_FAILURE_SUMMARY_CHARACTERS * 4,
        validator.PREFLIGHT_FILE_NAME: validator.MAX_PREFLIGHT_BYTES,
        validator.SEMANTIC_EVENT_FILE_NAME: validator.MAX_SEMANTIC_STREAM_BYTES,
    }
    for name in names:
        current = _read_bytes_at(
            run_descriptor,
            name,
            name,
            bounds[name],
            optional=name not in {
                "request.json",
                "result.json",
                "failure.json",
                "failure-summary.txt",
            },
        )
        if current != snapshots.get(name):
            raise _fail(
                "SOURCE_CHANGED_DURING_GENERATION",
                f"{name} changed while the diagnostic packet was being generated.",
            )


def _publish_error(run_id: str, error: DiagnosticPacketError) -> None:
    try:
        runtime_descriptor = _open_directory(RUNTIME_ROOT, "runtime artifact root")
        try:
            run_descriptor = _open_directory(run_id, "source run", dir_fd=runtime_descriptor)
        finally:
            os.close(runtime_descriptor)
        try:
            diagnostics = _ensure_directory_at(
                run_descriptor, "diagnostics", "diagnostics directory"
            )
            try:
                payload = _serialize(
                    {
                        "formatVersion": PACKET_ERROR_FORMAT_VERSION,
                        "runId": run_id,
                        "reasonCode": error.reason_code,
                        "message": _bounded_text(error, 2048),
                        "packetPath": PACKET_FILE_NAME,
                        "canonicalArtifactsChanged": False,
                    }
                )
                _write_exclusive_at(
                    diagnostics,
                    Path(PACKET_ERROR_PATH).name,
                    payload,
                    maximum=MAX_PACKET_ERROR_BYTES,
                )
            finally:
                os.close(diagnostics)
        finally:
            os.close(run_descriptor)
    except DiagnosticPacketError:
        return


def generate_packet(run_id: str) -> dict[str, Any]:
    run_id = _canonical_uuid(run_id, "runId")
    validator = _load_module("hatifect_diagnostic_validate", VALIDATOR_PATH)
    (
        run_descriptor,
        run_path,
        snapshots,
        request,
        result,
        failure,
        preflight,
        events,
    ) = _read_source_snapshot(run_id, validator)
    try:
        reproduction = _reproduction_projection(run_id, result)
        context = _build_context_manifest(validator, request, result, failure)
        semantic = _semantic_projection(validator, failure, events)
        preflight_projection = _preflight_environment_projection(preflight, failure)
        references = _artifact_references(
            run_descriptor, validator, result, failure
        )
        failure_projection = _failure_projection(failure)
        summary = _diagnostic_summary(result, failure, reproduction)
        encoded = _build_bounded_packet(
            summary=summary,
            failure=failure_projection,
            semantic=semantic,
            preflight=preflight_projection,
            reproduction=reproduction,
            artifacts=references,
            context=context,
        )
        lock = _publication_lock(run_descriptor)
        try:
            _verify_source_snapshot(run_descriptor, snapshots, validator)
            publication = _write_exclusive_at(
                run_descriptor,
                PACKET_FILE_NAME,
                encoded,
                maximum=MAX_PACKET_BYTES,
            )
        finally:
            fcntl.flock(lock, fcntl.LOCK_UN)
            os.close(lock)
        return {
            "status": publication,
            "path": (run_path / PACKET_FILE_NAME).relative_to(ROOT).as_posix(),
            "sizeBytes": len(encoded),
            "sha256": hashlib.sha256(encoded).hexdigest(),
        }
    finally:
        os.close(run_descriptor)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Generate one bounded Hatifect AI diagnostic packet."
    )
    parser.add_argument("run_id")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        result = generate_packet(args.run_id)
    except DiagnosticPacketError as error:
        if isinstance(args.run_id, str) and UUID_RE.fullmatch(args.run_id):
            _publish_error(args.run_id, error)
        print(
            f"Hatifect diagnostic packet: {error.reason_code}: {error}",
            file=sys.stderr,
        )
        return 2
    except (KeyError, OSError, TypeError, ValueError) as error:
        bounded = _fail(
            "UNEXPECTED_GENERATION_FAILURE",
            f"Unexpected packet generation failure ({type(error).__name__}).",
        )
        if isinstance(args.run_id, str) and UUID_RE.fullmatch(args.run_id):
            _publish_error(args.run_id, bounded)
        print(
            f"Hatifect diagnostic packet: {bounded.reason_code}: {bounded}",
            file=sys.stderr,
        )
        return 2
    print(
        f"{result['path']} ({result['sizeBytes']} bytes, "
        f"sha256={result['sha256']}, {result['status']})"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
