#!/usr/bin/env python3
"""Fail-closed manifest and evidence validator for the assisted SMAPI harness."""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import math
import uuid
import os
import platform
import re
import sys
import tempfile
import time
from pathlib import Path
from typing import Any


PROTOCOL_VERSION = 1
MANIFEST_FORMAT_VERSION = 1
DEFAULT_TIMEOUT_SECONDS = 1800
DEFAULT_MANIFEST = Path(__file__).with_name("scenarios.json")
IDENTIFIER = re.compile(r"^[a-z][a-z0-9-]*(?:\.[a-z0-9-]+)+$")
MOD_UNIQUE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
STATUSES = {"PASS", "FAIL", "BLOCKED"}
FAILURE_ENVELOPE_FORMAT_VERSION = 2
FAILURE_RECORD_TYPES = {
    "ROOT_FAILURE",
    "CASCADE_SKIPPED",
    "CLEANUP_FAILURE",
    "ADDITIONAL_FAILURE",
}
MAX_FAILURE_TEXT = 2048
MAX_FAILURE_ENVELOPE_BYTES = 256 * 1024
MAX_FAILURE_RECORD_CONTEXT_TEXT = 128
MAX_FAILURE_SUMMARY_CHARACTERS = 8192
MAX_RELEVANT_ARTIFACTS = 12
MAX_CASCADE_RECORDS = 32
MAX_CLEANUP_FAILURES = 8
MAX_ADDITIONAL_FAILURES = 32
HARNESS_CLEANUP_ASSERTION_IDS = frozenset({
    "HARNESS-OPTIONS-RESTORE",
    "HARNESS-PROCESS-TEARDOWN",
    "HARNESS-SAVE-CLEANUP",
})
ENVIRONMENT_SUMMARY_TYPES = {
    "platform": str,
    "machine": str,
    "python_version": str,
    "checkout_sha": str,
    "runner_kind": str,
    "timeout_seconds": int,
    "seed": int,
    "game_version": str,
    "smapi_version": str,
    "runtime_fingerprint": str,
}
REQUIRED_ENVIRONMENT_SUMMARY_FIELDS = frozenset({
    "platform",
    "machine",
    "python_version",
})
FLOW_PERFORMANCE = {'scenarioId': 'flow.chest.performance', 'warmupFrames': 120, 'measurementFrames': 600, 'shipments': 80, 'routes': 3, 'maxOperationsPerTick': 64, 'maximumP95TickMs': 0.25, 'maximumP99TickMs': 1.0, 'maximumAllocatedBytesPerTick': 0}
FLOW_PERFORMANCE_CHECKS = [
    'flow.chest.performance.' + suffix for suffix in (
        'loaded', 'saving', 'saved', 'paused', 'due-work', 'idle',
        'item-fidelity', 'unchanged-files',
    )
]

FLOW_RESOURCES = {'scenarioId': 'flow.chest.resources', 'shipments': 256, 'stations': 32, 'sourceChests': 8, 'routingQueries': 75, 'loads': 7, 'saves': 6, 'attempts': 16, 'warmupFrames': 120, 'measurementFrames': 600, 'maxOperationsPerTick': 64, 'maximumP95TickMs': 0.25, 'maximumP99TickMs': 1.0, 'maximumAllocatedBytesPerTick': 0}
FLOW_RESOURCE_CHECKS = ['flow.chest.resources.loaded', 'flow.chest.resources.routing', 'flow.chest.resources.queues', 'flow.chest.resources.saving', 'flow.chest.resources.saved', 'flow.chest.resources.receipt-growth', 'flow.chest.resources.limits', 'flow.chest.resources.reload', 'flow.chest.resources.item-fidelity', 'flow.chest.resources.idle', 'flow.chest.resources.unchanged-files']

class HarnessError(ValueError):
    def __init__(self, message: str, assertions: list[dict[str, Any]] | None = None):
        super().__init__(message)
        self.assertions = assertions or []


def _is_scenario_identifier(value: Any) -> bool:
    return isinstance(value, str) and (
        value == "all" or IDENTIFIER.fullmatch(value) is not None
    )


def load_manifest(path: Path = DEFAULT_MANIFEST) -> dict[str, dict[str, Any]]:
    with path.open("r", encoding="utf-8") as stream:
        document = json.load(stream)
    if not isinstance(document, dict) or set(document) != {"formatVersion", "scenarios"}:
        raise HarnessError("The live-harness manifest has an invalid field set.")
    if document.get("formatVersion") != MANIFEST_FORMAT_VERSION:
        raise HarnessError("Unsupported live-harness manifest format.")
    raw_scenarios = document.get("scenarios")
    if not isinstance(raw_scenarios, list) or not raw_scenarios:
        raise HarnessError("The live-harness manifest has no scenarios.")

    scenarios: dict[str, dict[str, Any]] = {}
    for scenario in raw_scenarios:
        if not isinstance(scenario, dict):
            raise HarnessError("Every live-harness scenario must be an object.")
        allowed_fields = {
            "id",
            "kind",
            "requiresSave",
            "timeoutSeconds",
            "checks",
            "includes",
            "performance",
            "flowPerformance",
            "flowResources",
            "automation",
            "requiredMods",
            "includeInAll",
        }
        if not set(scenario).issubset(allowed_fields):
            raise HarnessError("A live-harness scenario has an invalid field set.")
        scenario_id = scenario.get("id")
        kind = scenario.get("kind")
        if not _is_scenario_identifier(scenario_id):
            raise HarnessError("Every live-harness scenario needs a non-empty id.")
        if scenario_id in scenarios:
            raise HarnessError(f"Duplicate live-harness scenario '{scenario_id}'.")
        if kind not in {"smoke", "ui"}:
            raise HarnessError(f"Scenario '{scenario_id}' has invalid kind '{kind}'.")
        if not isinstance(scenario.get("requiresSave"), bool):
            raise HarnessError(f"Scenario '{scenario_id}' must declare requiresSave.")
        if not isinstance(scenario.get("includeInAll", True), bool):
            raise HarnessError(f"Scenario '{scenario_id}' has invalid includeInAll.")
        timeout = scenario.get("timeoutSeconds", DEFAULT_TIMEOUT_SECONDS)
        if not isinstance(timeout, int) or isinstance(timeout, bool) or timeout <= 0:
            raise HarnessError(
                f"Scenario '{scenario_id}' must declare a positive timeoutSeconds."
            )
        automation = scenario.get("automation")
        if automation != {
            "protocolVersion": PROTOCOL_VERSION,
            "driver": "Hatifect.TestHarness",
        }:
            raise HarnessError(
                f"Scenario '{scenario_id}' must declare the typed automated TestHarness driver."
            )
        checks = scenario.get("checks", [])
        if (
            not isinstance(checks, list)
            or any(not isinstance(check, str) or not IDENTIFIER.fullmatch(check) for check in checks)
            or len(checks) != len(set(checks))
        ):
            raise HarnessError(f"Scenario '{scenario_id}' has invalid checks.")
        if scenario_id == "all" and checks:
            raise HarnessError("Scenario 'all' must resolve checks from named scenarios.")
        includes = scenario.get("includes", [])
        if (
            not isinstance(includes, list)
            or any(not isinstance(item, str) or not IDENTIFIER.fullmatch(item) for item in includes)
            or len(includes) != len(set(includes))
        ):
            raise HarnessError(f"Scenario '{scenario_id}' has invalid includes.")
        if not checks and not includes:
            raise HarnessError(f"Scenario '{scenario_id}' has no checks or included scenarios.")
        required_mods = scenario.get("requiredMods", [])
        if (
            not isinstance(required_mods, list)
            or any(
                not isinstance(mod_id, str) or not MOD_UNIQUE_ID.fullmatch(mod_id)
                for mod_id in required_mods
            )
            or len(required_mods) != len(set(required_mods))
        ):
            raise HarnessError(f"Scenario '{scenario_id}' has invalid requiredMods.")
        flow_performance = scenario.get("flowPerformance")
        if scenario_id == FLOW_PERFORMANCE["scenarioId"] or "flowPerformance" in scenario:
            if (scenario_id != FLOW_PERFORMANCE["scenarioId"] or kind != "smoke"
                    or scenario["requiresSave"] is not True or scenario.get("includes")
                    or scenario.get("includeInAll") is not False or "performance" in scenario
                    or checks != FLOW_PERFORMANCE_CHECKS or required_mods != ["Hatifect.Flow"]
                    or json.dumps(flow_performance, sort_keys=True) != json.dumps(FLOW_PERFORMANCE, sort_keys=True)):
                raise HarnessError("Flow performance requires its fixed isolated workload and checked-in budgets.")
        if scenario_id == FLOW_RESOURCES["scenarioId"] or "flowResources" in scenario:
            if (scenario_id != FLOW_RESOURCES["scenarioId"] or kind != "smoke"
                    or scenario["requiresSave"] is not True or scenario.get("includes")
                    or scenario.get("includeInAll") is not False or "performance" in scenario or "flowPerformance" in scenario
                    or checks != FLOW_RESOURCE_CHECKS or required_mods != ["Hatifect.Flow"]
                    or json.dumps(scenario.get("flowResources"), sort_keys=True) != json.dumps(FLOW_RESOURCES, sort_keys=True)):
                raise HarnessError("Flow resources require the fixed isolated scale, lifecycle and budgets.")
        scenarios[scenario_id] = scenario

    for scenario in scenarios.values():
        for included in scenario.get("includes", []):
            if included not in scenarios:
                raise HarnessError(
                    f"Scenario '{scenario['id']}' includes unknown scenario '{included}'."
                )
            if scenarios[included]["kind"] != scenario["kind"]:
                raise HarnessError(
                    f"Scenario '{scenario['id']}' includes a different runner kind '{included}'."
                )

    all_scenario = scenarios.get("all")
    if all_scenario is None:
        raise HarnessError("The live-harness manifest must declare scenario 'all'.")
    expected_all_includes = [
        scenario_id
        for scenario_id, scenario in scenarios.items()
        if scenario_id != "all"
        and scenario["kind"] == "ui"
        and scenario.get("includeInAll", True)
    ]
    if all_scenario["includes"] != expected_all_includes:
        raise HarnessError(
            "Scenario 'all' must include every aggregate-enabled UI scenario in declared manifest order."
        )
    return scenarios


def resolve_scenario(
    scenarios: dict[str, dict[str, Any]], scenario_id: str, kind: str
) -> dict[str, Any]:
    try:
        root = scenarios[scenario_id]
    except KeyError as error:
        raise HarnessError(f"Unknown {kind} scenario '{scenario_id}'.") from error
    if root["kind"] != kind:
        raise HarnessError(
            f"Scenario '{scenario_id}' belongs to runner '{root['kind']}', not '{kind}'."
        )

    ordered: list[dict[str, Any]] = []
    visiting: set[str] = set()

    def visit(item: dict[str, Any]) -> None:
        item_id = item["id"]
        if item_id in visiting:
            raise HarnessError(f"Scenario include cycle at '{item_id}'.")
        visiting.add(item_id)
        for child_id in item.get("includes", []):
            visit(scenarios[child_id])
        visiting.remove(item_id)
        ordered.append(item)

    visit(root)
    checks: list[str] = []
    check_owners: dict[str, str] = {}
    for item in ordered:
        owner = item["id"]
        for check in item.get("checks", []):
            existing_owner = check_owners.get(check)
            if existing_owner is not None:
                raise HarnessError(
                    f"Scenario '{scenario_id}' resolves duplicate check '{check}' "
                    f"from '{existing_owner}' and '{owner}'."
                )
            check_owners[check] = owner
            checks.append(check)
    performances = [item["performance"] for item in ordered if "performance" in item]
    flow_performances = [item["flowPerformance"] for item in ordered if "flowPerformance" in item]
    flow_resources = [item["flowResources"] for item in ordered if "flowResources" in item]
    if len(performances) + len(flow_performances) + len(flow_resources) > 1:
        raise HarnessError(f"Scenario '{scenario_id}' resolves multiple performance captures.")
    required_mods: list[str] = []
    seen_required_mods: set[str] = set()
    for item in ordered:
        for mod_id in item.get("requiredMods", []):
            if mod_id not in seen_required_mods:
                seen_required_mods.add(mod_id)
                required_mods.append(mod_id)
    return {
        "id": scenario_id,
        "kind": kind,
        "requiresSave": any(item["requiresSave"] for item in ordered),
        "timeoutSeconds": root.get("timeoutSeconds", DEFAULT_TIMEOUT_SECONDS),
        "checks": checks,
        "performance": performances[0] if performances else None,
        "flowPerformance": flow_performances[0] if flow_performances else None,
        "flowResources": flow_resources[0] if flow_resources else None,
        "automation": root["automation"],
        "requiredMods": required_mods,
    }


def validate_required_mods(resolved: dict[str, Any], mods_root: Path) -> list[str]:
    if not resolved["requiredMods"]:
        return []
    root = mods_root.resolve()
    if not root.is_dir():
        raise HarnessError(f"The isolated Mods directory does not exist: {root}")

    installed: set[str] = set()
    for manifest in sorted(root.rglob("manifest.json")):
        resolved_manifest = manifest.resolve()
        try:
            resolved_manifest.relative_to(root)
        except ValueError as error:
            raise HarnessError(
                f"A mod manifest escapes the isolated Mods directory: {manifest}"
            ) from error
        if not resolved_manifest.is_file():
            continue
        try:
            document = json.loads(resolved_manifest.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise HarnessError(
                f"Could not read isolated mod manifest '{manifest}': {error}"
            ) from error
        unique_id = document.get("UniqueID") if isinstance(document, dict) else None
        if isinstance(unique_id, str) and MOD_UNIQUE_ID.fullmatch(unique_id):
            installed.add(unique_id)

    return [mod_id for mod_id in resolved["requiredMods"] if mod_id not in installed]


def _assertion(
    assertion_id: str,
    status: str,
    subject: str,
    expected: str,
    actual: str,
) -> dict[str, Any]:
    return {
        "id": assertion_id,
        "status": status,
        "subject": subject,
        "expected": expected,
        "actual": actual,
    }


def collect_artifacts(root: Path | None) -> list[dict[str, str]]:
    if root is None or not root.is_dir():
        return []
    artifacts: list[dict[str, str]] = []
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        relative = path.relative_to(root).as_posix()
        if relative in {"result.json", "failure.json", "failure-summary.txt"} or relative.endswith(".tmp"):
            continue
        if relative == "host-acceptance-report.json":
            artifact_type = "acceptance-report"
        elif relative.startswith("smapi-logs/") or relative == "smapi-console.log":
            artifact_type = "smapi-log"
        elif relative.startswith("screenshots/"):
            artifact_type = "screenshot"
        elif relative == "instructions.txt":
            artifact_type = "instructions"
        else:
            artifact_type = "diagnostic"
        artifacts.append({"type": artifact_type, "path": relative})
    return artifacts


def _atomic_write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = _serialized_json(payload)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
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


def _serialized_json(payload: dict[str, Any]) -> bytes:
    return (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")


def _atomic_write_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".tmp", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
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


def _bounded_text(value: Any) -> str:
    return str(value)[:MAX_FAILURE_TEXT]


def _failure_context(
    assertion_id: str,
    status: str,
    *,
    phase: str | None = None,
    failure_class: str | None = None,
    causal_component: str | None = None,
) -> tuple[str, str, str]:
    upper = assertion_id.upper()
    if assertion_id in HARNESS_CLEANUP_ASSERTION_IDS:
        defaults = ("cleanup", "CLEANUP_FAILURE", "runtime-cleanup")
    elif upper == "HARNESS-SEMANTIC-TEST-AGENT":
        defaults = ("input", "AUTOMATION_DRIVER_FAILURE", "semantic-test-agent")
    elif upper == "HARNESS-PREPARE":
        defaults = ("prepare", "PREPARATION_FAILURE", "deployment-preparer")
    elif upper.startswith(("HARNESS-ENV-", "HARNESS-SCENARIO", "HARNESS-SAVE-")):
        defaults = ("preflight", "PREFLIGHT_FAILURE", "harness-preflight")
    elif upper.startswith(("HARNESS-PROCESS-", "HARNESS-DIRECT-", "HARNESS-EXECUTOR-",
                           "HARNESS-CRASH-", "HARNESS-LOCAL-INFRA-")):
        defaults = ("runtime", "RUNTIME_EXECUTOR_FAILURE", "runtime-executor")
    elif upper.startswith(("HARNESS-REPORT-", "HARNESS-RESULT-", "HARNESS-RUNTIME-",
                           "HARNESS-EVIDENCE-")):
        defaults = ("validation", "EVIDENCE_FAILURE", "harness-validator")
    else:
        defaults = (
            "scenario",
            "INFRASTRUCTURE_BLOCK" if status == "BLOCKED" else "ASSERTION_FAILURE",
            "scenario",
        )
    return (
        phase or defaults[0],
        failure_class or defaults[1],
        causal_component or defaults[2],
    )


def _read_bounded_json(
    path: Path,
    *,
    maximum_bytes: int = MAX_FAILURE_ENVELOPE_BYTES,
) -> dict[str, Any] | None:
    try:
        if not path.is_file() or path.stat().st_size > maximum_bytes:
            return None
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else None
    except (OSError, UnicodeError, json.JSONDecodeError):
        return None


def _environment_summary(artifact_root: Path) -> dict[str, Any]:
    summary: dict[str, Any] = {
        "platform": platform.system() or sys.platform,
        "machine": platform.machine() or "unknown",
        "python_version": platform.python_version(),
    }
    request = _read_bounded_json(artifact_root / "request.json")
    if request is not None:
        for source, target, expected_type in (
            ("repositoryHead", "checkout_sha", str),
            ("checkoutSha", "checkout_sha", str),
            ("kind", "runner_kind", str),
            ("timeoutSeconds", "timeout_seconds", int),
            ("seed", "seed", int),
        ):
            value = request.get(source)
            if (
                isinstance(value, expected_type)
                and not isinstance(value, bool)
                and (expected_type is not str or value)
            ):
                summary.setdefault(
                    target,
                    _bounded_text(value) if expected_type is str else value,
                )
    report = _read_bounded_json(artifact_root / "host-acceptance-report.json")
    if report is not None:
        for source, target in (
            ("GameVersion", "game_version"),
            ("SmapiVersion", "smapi_version"),
            ("RuntimeFingerprint", "runtime_fingerprint"),
        ):
            value = report.get(source)
            if isinstance(value, str) and value:
                summary[target] = _bounded_text(value)
    return summary


def _result_fingerprint(result: dict[str, Any]) -> str:
    canonical = json.dumps(
        result,
        ensure_ascii=True,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def _relevant_artifacts(
    result: dict[str, Any],
    artifact_root: Path,
    additions: list[str] | None = None,
) -> list[str]:
    candidates = {"result.json"}
    for artifact in result.get("artifacts", []):
        if isinstance(artifact, dict) and isinstance(artifact.get("path"), str):
            candidates.add(artifact["path"])
    candidates.update(additions or [])
    priority = {
        "result.json": 0,
        "host-acceptance-report.json": 1,
        "diagnostics/runtime.json": 2,
        "diagnostics/semantic-test-agent.json": 3,
        "diagnostics/executor-failure.json": 4,
        "diagnostics/worker-failure.json": 5,
        "diagnostics/transport-result.json": 6,
        "semantic-test-agent.log": 7,
        "smapi.log": 8,
        "harness.log": 9,
    }
    contained: list[str] = []
    for relative in candidates:
        candidate = Path(relative)
        if candidate.is_absolute() or ".." in candidate.parts:
            continue
        if relative == "result.json" or (artifact_root / candidate).is_file():
            contained.append(relative)
    return sorted(
        set(contained),
        key=lambda item: (priority.get(item, 8), item),
    )[:MAX_RELEVANT_ARTIFACTS]


def _executor_failure_context(artifact_root: Path) -> tuple[str, str] | None:
    executor = _read_bounded_json(artifact_root / "diagnostics" / "executor-failure.json")
    if executor is not None:
        exception_type = executor.get("exceptionType")
        message = executor.get("message")
        if isinstance(exception_type, str) and isinstance(message, str):
            return "runtime-executor", _bounded_text(f"{exception_type}: {message}")
    worker = _read_bounded_json(artifact_root / "diagnostics" / "worker-failure.json")
    if worker is not None:
        exit_code = worker.get("workerExitCode")
        message = worker.get("message")
        if isinstance(exit_code, int) and not isinstance(exit_code, bool) and isinstance(message, str):
            return "runtime-worker", _bounded_text(f"Worker exit {exit_code}: {message}")
    return None


def _failure_record(
    assertion: dict[str, Any],
    record_type: str,
    *,
    root_failure_id: str | None = None,
    phase: str | None = None,
    failure_class: str | None = None,
    causal_component: str | None = None,
    timestamp: str | None = None,
) -> dict[str, Any]:
    assertion_id = _bounded_text(assertion["id"])
    expected = _bounded_text(assertion["expected"]) or "not specified"
    actual = _bounded_text(assertion["actual"]) or "not specified"
    resolved_phase, resolved_class, resolved_component = _failure_context(
        assertion_id,
        assertion["status"],
        phase=phase,
        failure_class=failure_class,
        causal_component=causal_component,
    )
    record = {
        "classification": record_type,
        "id": assertion_id,
        "phase": resolved_phase,
        "failure_class": resolved_class,
        "expected": expected,
        "actual": actual,
        "message": actual,
        "causal_component": resolved_component,
        "original_status": assertion["status"],
    }
    if root_failure_id is not None:
        record["root_failure_id"] = root_failure_id
    if timestamp is not None:
        record["timestamp"] = timestamp
    return record


def _failure_remainder(
    classification: str,
    root_failure_id: str | None,
    result: dict[str, Any],
    omitted: int,
) -> dict[str, Any]:
    labels = {
        "ADDITIONAL_FAILURE": "additional",
        "CASCADE_SKIPPED": "cascade",
        "CLEANUP_FAILURE": "cleanup",
    }
    label = labels[classification]
    return _failure_record(
        _assertion(
            f"HARNESS-{label.upper()}-REMAINDER",
            "FAIL",
            result["scenario"],
            f"All {label} failures remain available in result.json.",
            f"{omitted} additional {label} failures remain in result.json.",
        ),
        classification,
        root_failure_id=root_failure_id,
    )


def _bounded_failure_records(
    assertions: list[dict[str, Any]],
    classification: str,
    result: dict[str, Any],
    *,
    root_failure_id: str | None = None,
) -> list[dict[str, Any]]:
    maximum = (
        MAX_CASCADE_RECORDS
        if classification == "CASCADE_SKIPPED"
        else MAX_ADDITIONAL_FAILURES
    )
    records = [
        _failure_record(
            assertion,
            classification,
            root_failure_id=root_failure_id,
        )
        for assertion in assertions[:maximum]
    ]
    if len(assertions) > maximum:
        omitted = len(assertions) - (maximum - 1)
        records = records[:maximum - 1] + [
            _failure_remainder(
                classification,
                root_failure_id,
                result,
                omitted,
            )
        ]
    return records


def _compact_failure_text(value: str) -> str:
    if len(value) <= MAX_FAILURE_RECORD_CONTEXT_TEXT:
        return value
    return value[:MAX_FAILURE_RECORD_CONTEXT_TEXT - 3] + "..."


def _compact_record_context(records: list[dict[str, Any]]) -> None:
    for record in records:
        for field in (
            "id",
            "phase",
            "failure_class",
            "expected",
            "actual",
            "message",
            "causal_component",
        ):
            record[field] = _compact_failure_text(record[field])


def _replace_records_with_budget_remainder(
    envelope: dict[str, Any],
    collection_name: str,
    classification: str,
) -> None:
    records = envelope[collection_name]
    if not records:
        return
    root_failure_id = (
        envelope["root_failure"]["id"]
        if classification != "ADDITIONAL_FAILURE"
        else None
    )
    records[:] = [
        _failure_remainder(
            classification,
            root_failure_id,
            {
                "scenario": envelope["scenario"],
            },
            len(records),
        )
    ]


def _compact_failure_envelope(envelope: dict[str, Any]) -> dict[str, Any]:
    if len(_serialized_json(envelope)) <= MAX_FAILURE_ENVELOPE_BYTES:
        return envelope
    for collection_name in (
        "additional_failures",
        "cascade_records",
        "cleanup_failures",
    ):
        _compact_record_context(envelope[collection_name])
    if len(_serialized_json(envelope)) <= MAX_FAILURE_ENVELOPE_BYTES:
        return envelope
    for collection_name, classification in (
        ("additional_failures", "ADDITIONAL_FAILURE"),
        ("cascade_records", "CASCADE_SKIPPED"),
        ("cleanup_failures", "CLEANUP_FAILURE"),
    ):
        _replace_records_with_budget_remainder(
            envelope,
            collection_name,
            classification,
        )
        if len(_serialized_json(envelope)) <= MAX_FAILURE_ENVELOPE_BYTES:
            return envelope
    for key in sorted(
        set(envelope["environment_summary"])
        - REQUIRED_ENVIRONMENT_SUMMARY_FIELDS,
        reverse=True,
    ):
        del envelope["environment_summary"][key]
        if len(_serialized_json(envelope)) <= MAX_FAILURE_ENVELOPE_BYTES:
            return envelope
    while len(envelope["relevant_artifacts"]) > 1:
        envelope["relevant_artifacts"].pop()
        if len(_serialized_json(envelope)) <= MAX_FAILURE_ENVELOPE_BYTES:
            return envelope
    if len(_serialized_json(envelope)) > MAX_FAILURE_ENVELOPE_BYTES:
        raise HarnessError(
            "Failure envelope root context exceeds its serialized-size budget."
        )
    return envelope


def build_failure_envelope(
    result: dict[str, Any],
    artifact_root: Path,
    *,
    timestamp: str | None = None,
    phase: str | None = None,
    failure_class: str | None = None,
    causal_component: str | None = None,
    cleanup_failures: list[dict[str, str]] | None = None,
    cascade_dependencies: dict[str, str] | None = None,
) -> dict[str, Any] | None:
    validate_result(result)
    if result["status"] == "PASS":
        return None
    if cleanup_failures is not None and len(cleanup_failures) > MAX_CLEANUP_FAILURES:
        raise HarnessError("Failure envelope cleanup input exceeds its deterministic bound.")
    if any(
        not isinstance(failure, dict)
        or failure.get("id") not in HARNESS_CLEANUP_ASSERTION_IDS
        for failure in cleanup_failures or []
    ):
        raise HarnessError(
            "Failure envelope cleanup input must use an explicit harness cleanup ID."
        )
    captured_at = timestamp or dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
    failed_assertions = [
        assertion for assertion in result["assertions"] if assertion["status"] != "PASS"
    ]
    if not failed_assertions:
        failed_assertions = [_assertion(
            "HARNESS-EXECUTION",
            result["status"],
            result["scenario"],
            "The isolated scenario completes successfully.",
            f"Scenario ended with {result['status']}.",
        )]
    cleanup_assertions = [
        assertion for assertion in failed_assertions
        if assertion["id"] in HARNESS_CLEANUP_ASSERTION_IDS
    ]
    causal_assertions = [
        assertion for assertion in failed_assertions if assertion not in cleanup_assertions
    ]
    root_assertion = causal_assertions[0] if causal_assertions else cleanup_assertions.pop(0)
    root = _failure_record(
        root_assertion,
        "ROOT_FAILURE",
        phase=phase,
        failure_class=failure_class,
        causal_component=causal_component,
    )
    if root["id"] in {"HARNESS-RESULT-MISSING", "HARNESS-RESULT-INVALID", "HARNESS-RESULT-CONFLICT"}:
        executor_context = _executor_failure_context(artifact_root)
        if executor_context is not None:
            root["phase"] = "executor"
            root["failure_class"] = "RUNTIME_EXECUTOR_FAILURE"
            root["causal_component"], root["actual"] = executor_context
            root["message"] = root["actual"]
    root_id = root["id"]
    dependencies = cascade_dependencies or {}
    if not isinstance(dependencies, dict) or any(
        not isinstance(dependent, str)
        or not isinstance(cause, str)
        or dependent == root_id
        for dependent, cause in dependencies.items()
    ):
        raise HarnessError("Failure envelope cascade dependencies are invalid.")
    known_causal_ids = {assertion["id"] for assertion in causal_assertions}
    if (
        not set(dependencies) <= known_causal_ids
        or any(cause != root_id for cause in dependencies.values())
    ):
        raise HarnessError(
            "Failure envelope cascade dependencies must explicitly reference the root failure."
        )
    cascade_assertions = [
        assertion
        for assertion in causal_assertions[1:]
        if dependencies.get(assertion["id"]) == root_id
    ]
    additional_assertions = [
        assertion
        for assertion in causal_assertions[1:]
        if assertion not in cascade_assertions
    ]
    cascades = _bounded_failure_records(
        cascade_assertions,
        "CASCADE_SKIPPED",
        result,
        root_failure_id=root_id,
    )
    additional_failures = _bounded_failure_records(
        additional_assertions,
        "ADDITIONAL_FAILURE",
        result,
    )
    cleanup_records = [
        _failure_record(assertion, "CLEANUP_FAILURE", root_failure_id=root_id)
        for assertion in cleanup_assertions
    ]
    extra_artifacts: list[str] = []
    for index, failure in enumerate(cleanup_failures or []):
        assertion = _assertion(
            failure.get("id") or f"HARNESS-CLEANUP-{index + 1}",
            failure.get("status") or "FAIL",
            result["scenario"],
            failure.get("expected") or "Cleanup completes without errors.",
            failure.get("actual") or failure.get("message") or "Cleanup failed.",
        )
        cleanup_records.append(_failure_record(
            assertion,
            "CLEANUP_FAILURE",
            root_failure_id=root_id,
            phase=failure.get("phase") or "cleanup",
            failure_class="CLEANUP_FAILURE",
            causal_component=failure.get("causal_component") or "runtime-cleanup",
            timestamp=failure.get("timestamp") or captured_at,
        ))
        artifact = failure.get("artifact")
        if artifact:
            extra_artifacts.append(artifact)
    if len(cleanup_records) > MAX_CLEANUP_FAILURES:
        omitted = len(cleanup_records) - (MAX_CLEANUP_FAILURES - 1)
        cleanup_records = cleanup_records[:MAX_CLEANUP_FAILURES - 1] + [
            _failure_record(
                _assertion(
                    "HARNESS-CLEANUP-REMAINDER",
                    "FAIL",
                    result["scenario"],
                    "All cleanup failures remain available in result.json.",
                    f"{omitted} additional cleanup failures remain in result.json.",
                ),
                "CLEANUP_FAILURE",
                root_failure_id=root_id,
            )
        ]
    envelope = {
        "format_version": FAILURE_ENVELOPE_FORMAT_VERSION,
        "result_fingerprint": _result_fingerprint(result),
        "scenario": result["scenario"],
        "run_id": result["runId"],
        "phase": root["phase"],
        "timestamp": captured_at,
        "status": result["status"],
        "failure_class": root["failure_class"],
        "root_failure": root,
        "expected": root["expected"],
        "actual": root["actual"],
        "message": root["message"],
        "causal_component": root["causal_component"],
        "cascade_records": cascades,
        "additional_failures": additional_failures,
        "cleanup_failures": cleanup_records,
        "relevant_artifacts": _relevant_artifacts(result, artifact_root, extra_artifacts),
        "environment_summary": _environment_summary(artifact_root),
    }
    _compact_failure_envelope(envelope)
    validate_failure_envelope(envelope)
    return envelope


def validate_failure_envelope(envelope: Any) -> None:
    keys = {
        "format_version", "scenario", "run_id", "phase", "timestamp", "status",
        "failure_class", "root_failure", "expected", "actual", "message",
        "causal_component", "cascade_records", "cleanup_failures",
        "additional_failures", "relevant_artifacts", "environment_summary",
        "result_fingerprint",
    }
    if not isinstance(envelope, dict) or set(envelope) != keys:
        raise HarnessError("Failure envelope does not match format v2 fields.")
    if len(_serialized_json(envelope)) > MAX_FAILURE_ENVELOPE_BYTES:
        raise HarnessError("Failure envelope exceeds its serialized-size budget.")
    if envelope["format_version"] != FAILURE_ENVELOPE_FORMAT_VERSION:
        raise HarnessError("Failure envelope has an unsupported format_version.")
    if envelope["status"] not in {"FAIL", "BLOCKED"}:
        raise HarnessError("Failure envelope must describe a failed or blocked run.")
    for field in (
        "scenario", "run_id", "phase", "timestamp", "failure_class", "expected",
        "actual", "message", "causal_component",
    ):
        _validate_failure_text(envelope[field], f"Failure envelope {field}")
    _parse_timestamp(envelope["timestamp"])
    fingerprint = envelope["result_fingerprint"]
    if (
        not isinstance(fingerprint, str)
        or len(fingerprint) != 64
        or any(character not in "0123456789abcdef" for character in fingerprint)
    ):
        raise HarnessError("Failure envelope result_fingerprint must be a SHA-256 digest.")
    root = envelope["root_failure"]
    record_keys = {
        "classification", "id", "phase", "failure_class", "expected", "actual",
        "message", "causal_component", "original_status",
    }
    if not isinstance(root, dict) or set(root) != record_keys:
        raise HarnessError("Failure envelope must contain exactly one root failure.")
    _validate_failure_record(
        root,
        expected_classification="ROOT_FAILURE",
        root_failure_id=None,
        allow_timestamp=False,
    )
    if root["original_status"] not in {"FAIL", "BLOCKED"}:
        raise HarnessError("Failure envelope root classification is invalid.")
    for field in (
        "phase",
        "failure_class",
        "expected",
        "actual",
        "message",
        "causal_component",
    ):
        if envelope[field] != root[field]:
            raise HarnessError(
                f"Failure envelope {field} conflicts with its root failure."
            )
    for collection_name, classification, requires_root_reference in (
        ("cascade_records", "CASCADE_SKIPPED", True),
        ("cleanup_failures", "CLEANUP_FAILURE", True),
        ("additional_failures", "ADDITIONAL_FAILURE", False),
    ):
        records = envelope[collection_name]
        if not isinstance(records, list):
            raise HarnessError(f"Failure envelope {collection_name} must be an array.")
        maximum = (
            MAX_CASCADE_RECORDS
            if collection_name == "cascade_records"
            else MAX_CLEANUP_FAILURES
            if collection_name == "cleanup_failures"
            else MAX_ADDITIONAL_FAILURES
        )
        if len(records) > maximum:
            raise HarnessError(f"Failure envelope {collection_name} exceeds its bound.")
        for record in records:
            _validate_failure_record(
                record,
                expected_classification=classification,
                root_failure_id=root["id"] if requires_root_reference else None,
                allow_timestamp=collection_name == "cleanup_failures",
            )
    artifacts = envelope["relevant_artifacts"]
    if (
        not isinstance(artifacts, list)
        or len(artifacts) > MAX_RELEVANT_ARTIFACTS
        or artifacts != list(dict.fromkeys(artifacts))
        or "result.json" not in artifacts
    ):
        raise HarnessError("Failure envelope relevant_artifacts are invalid or unbounded.")
    for artifact in artifacts:
        if (
            not isinstance(artifact, str)
            or not artifact
            or len(artifact) > MAX_FAILURE_TEXT
            or Path(artifact).is_absolute()
            or ".." in Path(artifact).parts
        ):
            raise HarnessError("Failure envelope artifact reference is invalid.")
    _validate_environment_summary(envelope["environment_summary"])


def _validate_failure_text(value: Any, description: str) -> None:
    if not isinstance(value, str) or not value or len(value) > MAX_FAILURE_TEXT:
        raise HarnessError(
            f"{description} must be a non-empty string no longer than "
            f"{MAX_FAILURE_TEXT} characters."
        )


def _validate_failure_record(
    record: Any,
    *,
    expected_classification: str,
    root_failure_id: str | None,
    allow_timestamp: bool,
) -> None:
    record_keys = {
        "classification", "id", "phase", "failure_class", "expected", "actual",
        "message", "causal_component", "original_status",
    }
    required = record_keys | (
        {"root_failure_id"} if root_failure_id is not None else set()
    )
    allowed = set(required)
    if allow_timestamp:
        allowed.add("timestamp")
    if (
        not isinstance(record, dict)
        or not required <= set(record)
        or not set(record) <= allowed
    ):
        raise HarnessError(
            f"Failure envelope contains a malformed {expected_classification} record."
        )
    if record["classification"] != expected_classification:
        raise HarnessError("Failure envelope record classification is invalid.")
    if record["classification"] not in FAILURE_RECORD_TYPES:
        raise HarnessError("Failure envelope record has an unknown classification.")
    if record["original_status"] not in {"FAIL", "BLOCKED"}:
        raise HarnessError("Failure envelope record has an invalid original_status.")
    for field in (
        "id",
        "phase",
        "failure_class",
        "expected",
        "actual",
        "message",
        "causal_component",
    ):
        _validate_failure_text(record[field], f"Failure envelope record {field}")
    if root_failure_id is not None and record["root_failure_id"] != root_failure_id:
        raise HarnessError("Failure envelope record does not reference its root.")
    if allow_timestamp and "timestamp" in record:
        _validate_failure_text(
            record["timestamp"],
            "Failure envelope cleanup timestamp",
        )
        _parse_timestamp(record["timestamp"])


def _validate_environment_summary(summary: Any) -> None:
    if (
        not isinstance(summary, dict)
        or not REQUIRED_ENVIRONMENT_SUMMARY_FIELDS <= set(summary)
        or not set(summary) <= set(ENVIRONMENT_SUMMARY_TYPES)
        or len(summary) > len(ENVIRONMENT_SUMMARY_TYPES)
    ):
        raise HarnessError("Failure envelope environment_summary has unsupported fields.")
    for key, value in summary.items():
        expected_type = ENVIRONMENT_SUMMARY_TYPES[key]
        if (
            not isinstance(value, expected_type)
            or isinstance(value, bool)
            or (expected_type is str and (not value or len(value) > MAX_FAILURE_TEXT))
            or (expected_type is int and abs(value) > 2_147_483_647)
        ):
            raise HarnessError(
                f"Failure envelope environment_summary {key} is invalid."
            )


def _failure_summary(envelope: dict[str, Any]) -> str:
    root = envelope["root_failure"]
    artifacts = ", ".join(envelope["relevant_artifacts"]) or "none"
    lines = [
        f"{envelope['status']} {envelope['scenario']} ({envelope['run_id']})",
        f"ROOT_FAILURE [{envelope['failure_class']}] {root['id']}",
        f"Phase: {envelope['phase']}; component: {envelope['causal_component']}",
        f"Expected: {envelope['expected']}",
        f"Actual: {envelope['actual']}",
        f"Cascade records: {len(envelope['cascade_records'])}; additional failures: {len(envelope['additional_failures'])}",
        f"Cleanup failures: {len(envelope['cleanup_failures'])}",
        f"Relevant artifacts: {artifacts}",
    ]
    return "\n".join(lines)[:MAX_FAILURE_SUMMARY_CHARACTERS - 1] + "\n"


def _read_bounded_text(path: Path, maximum_characters: int) -> str | None:
    try:
        with path.open(encoding="utf-8") as stream:
            value = stream.read(maximum_characters + 1)
    except (OSError, UnicodeError):
        return None
    return value if len(value) <= maximum_characters else None


def _remove_failure_artifacts(artifact_root: Path) -> None:
    for name in ("failure.json", "failure-summary.txt"):
        try:
            (artifact_root / name).unlink()
        except FileNotFoundError:
            pass
        except OSError as error:
            raise HarnessError(f"Cannot remove stale {name}.") from error


def write_failure_artifacts(
    result_path: Path,
    result: dict[str, Any],
    **kwargs: Any,
) -> dict[str, Any] | None:
    envelope = build_failure_envelope(result, result_path.parent, **kwargs)
    if envelope is None:
        _remove_failure_artifacts(result_path.parent)
        return None
    _atomic_write_json(result_path.parent / "failure.json", envelope)
    _atomic_write_text(result_path.parent / "failure-summary.txt", _failure_summary(envelope))
    return envelope


def validate_failure_artifacts(
    result_path: Path,
    result: dict[str, Any],
) -> dict[str, Any] | None:
    if result["status"] == "PASS":
        return None
    failure_path = result_path.parent / "failure.json"
    summary_path = result_path.parent / "failure-summary.txt"
    envelope = _read_bounded_json(
        failure_path,
        maximum_bytes=MAX_FAILURE_ENVELOPE_BYTES,
    )
    if envelope is None:
        raise HarnessError("Non-PASS scenario result has no bounded failure.json.")
    validate_failure_envelope(envelope)
    if (
        envelope["scenario"] != result["scenario"]
        or envelope["run_id"] != result["runId"]
        or envelope["status"] != result["status"]
    ):
        raise HarnessError("Failure envelope identity conflicts with result.json.")
    if envelope["result_fingerprint"] != _result_fingerprint(result):
        raise HarnessError("Failure envelope fingerprint conflicts with result.json.")
    _validate_root_result_evidence(envelope["root_failure"], result)
    summary = _read_bounded_text(summary_path, MAX_FAILURE_SUMMARY_CHARACTERS)
    if summary is None:
        raise HarnessError(
            "Non-PASS scenario result has no bounded readable failure-summary.txt."
        )
    if summary != _failure_summary(envelope):
        raise HarnessError("Failure summary conflicts with failure.json.")
    return envelope


def _validate_root_result_evidence(
    root: dict[str, Any],
    result: dict[str, Any],
) -> None:
    failures = [
        assertion
        for assertion in result["assertions"]
        if assertion["status"] != "PASS"
    ]
    if (
        root["id"] == "HARNESS-EXECUTION"
        and not failures
        and root["original_status"] == result["status"]
    ):
        return
    for assertion in failures:
        if (
            assertion["id"] == root["id"]
            and assertion["status"] == root["original_status"]
            and assertion["expected"] == root["expected"]
            and (
                assertion["actual"] == root["actual"]
                or root["id"] in {
                    "HARNESS-RESULT-CONFLICT",
                    "HARNESS-RESULT-INVALID",
                    "HARNESS-RESULT-MISSING",
                }
            )
        ):
            return
    raise HarnessError("Failure envelope root evidence conflicts with result.json.")


def record_cleanup_failure(
    result_path: Path,
    *,
    failure_id: str,
    message: str,
    expected: str = "Cleanup completes without errors.",
    causal_component: str = "runtime-cleanup",
    artifact: str | None = None,
    timestamp: str | None = None,
) -> dict[str, Any]:
    if failure_id not in HARNESS_CLEANUP_ASSERTION_IDS:
        raise HarnessError(
            "Cleanup failure must use an explicit harness-owned cleanup ID."
        )
    result = _read_bounded_json(result_path)
    if result is None:
        raise HarnessError("Cannot record cleanup failure without canonical result.json.")
    cleanup = {
        "id": failure_id,
        "status": "FAIL",
        "phase": "cleanup",
        "expected": expected,
        "actual": message,
        "message": message,
        "causal_component": causal_component,
        "timestamp": timestamp or dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
    }
    if artifact is not None:
        cleanup["artifact"] = artifact
    if result["status"] == "PASS":
        result["status"] = "BLOCKED"
        result["assertions"].append(_assertion(
            failure_id,
            "BLOCKED",
            result["scenario"],
            expected,
            message,
        ))
        _atomic_write_json(result_path, result)
        envelope = build_failure_envelope(
            result,
            result_path.parent,
            timestamp=cleanup["timestamp"],
            phase="cleanup",
            failure_class="CLEANUP_FAILURE",
            causal_component=causal_component,
        )
        if envelope is not None and artifact is not None:
            envelope["relevant_artifacts"] = _relevant_artifacts(
                result, result_path.parent, [artifact]
            )
    else:
        existing = _read_bounded_json(result_path.parent / "failure.json")
        if existing is None:
            envelope = build_failure_envelope(
                result,
                result_path.parent,
                cleanup_failures=[cleanup],
            )
        else:
            validate_failure_envelope(existing)
            envelope = existing
            record = _failure_record(
                _assertion(failure_id, "FAIL", result["scenario"], expected, message),
                "CLEANUP_FAILURE",
                root_failure_id=envelope["root_failure"]["id"],
                phase="cleanup",
                failure_class="CLEANUP_FAILURE",
                causal_component=causal_component,
                timestamp=cleanup["timestamp"],
            )
            if record not in envelope["cleanup_failures"]:
                envelope["cleanup_failures"].append(record)
            envelope["relevant_artifacts"] = _relevant_artifacts(
                result, result_path.parent, [artifact] if artifact else None
            )
            validate_failure_envelope(envelope)
    if envelope is None:
        raise HarnessError("Cleanup failure did not produce a failure envelope.")
    _atomic_write_json(result_path.parent / "failure.json", envelope)
    _atomic_write_text(result_path.parent / "failure-summary.txt", _failure_summary(envelope))
    return envelope


def write_result(
    path: Path,
    status: str,
    scenario: str,
    message: str,
    *,
    run_id: str | None = None,
    duration_ms: int = 0,
    assertions: list[dict[str, Any]] | None = None,
    exceptions: list[dict[str, str]] | None = None,
    artifacts: list[dict[str, str]] | None = None,
    failure_timestamp: str | None = None,
    failure_phase: str | None = None,
    failure_class: str | None = None,
    causal_component: str | None = None,
    cleanup_failures: list[dict[str, str]] | None = None,
    cascade_dependencies: dict[str, str] | None = None,
) -> None:
    if status not in STATUSES:
        raise HarnessError(f"Invalid harness result status '{status}'.")
    if not _is_scenario_identifier(scenario):
        raise HarnessError(f"Invalid harness scenario identifier '{scenario}'.")
    if duration_ms < 0:
        raise HarnessError("Harness result duration must be non-negative.")
    payload = {
        "protocolVersion": PROTOCOL_VERSION,
        "runId": run_id or path.parent.name,
        "scenario": scenario,
        "status": status,
        "durationMs": duration_ms,
        "assertions": assertions or [
            _assertion(
                "HARNESS-EXECUTION",
                status,
                scenario,
                "The isolated scenario completes with structured evidence.",
                message,
            )
        ],
        "exceptions": exceptions or [],
        "artifacts": artifacts or [],
    }
    validate_result(payload, scenario)
    _atomic_write_json(path, payload)
    write_failure_artifacts(
        path,
        payload,
        timestamp=failure_timestamp,
        phase=failure_phase,
        failure_class=failure_class,
        causal_component=causal_component,
        cleanup_failures=cleanup_failures,
        cascade_dependencies=cascade_dependencies,
    )


def validate_result(document: Any, expected_scenario: str | None = None) -> str:
    if not isinstance(document, dict):
        raise HarnessError("Scenario result must be a JSON object.")
    expected_keys = {
        "protocolVersion",
        "runId",
        "scenario",
        "status",
        "durationMs",
        "assertions",
        "exceptions",
        "artifacts",
    }
    if set(document) != expected_keys:
        raise HarnessError("Scenario result does not match protocol schema v1 fields.")
    if document["protocolVersion"] != PROTOCOL_VERSION:
        raise HarnessError("Scenario result has an unsupported protocolVersion.")
    if not isinstance(document["runId"], str) or not document["runId"]:
        raise HarnessError("Scenario result has no runId.")
    scenario = document["scenario"]
    if not _is_scenario_identifier(scenario):
        raise HarnessError("Scenario result has an invalid scenario identifier.")
    if expected_scenario and scenario != expected_scenario:
        raise HarnessError(
            f"Scenario result identity mismatch: expected {expected_scenario!r}, got {scenario!r}"
        )
    status = document["status"]
    if status not in STATUSES:
        raise HarnessError(f"Scenario result has invalid status {status!r}.")
    duration = document["durationMs"]
    if not isinstance(duration, int) or isinstance(duration, bool) or duration < 0:
        raise HarnessError("Scenario result has an invalid durationMs.")
    _validate_assertions(document["assertions"])
    _validate_exceptions(document["exceptions"])
    _validate_artifacts(document["artifacts"])
    return status


def _validate_assertions(assertions: Any) -> None:
    if not isinstance(assertions, list):
        raise HarnessError("Scenario result assertions must be an array.")
    keys = {"id", "status", "subject", "expected", "actual"}
    for assertion in assertions:
        if not isinstance(assertion, dict) or set(assertion) != keys:
            raise HarnessError("Scenario result contains a malformed assertion.")
        if not isinstance(assertion["id"], str) or not assertion["id"]:
            raise HarnessError("Scenario result assertion has no stable id.")
        if assertion["status"] not in STATUSES:
            raise HarnessError("Scenario result assertion has an invalid status.")
        for field in ("subject", "expected", "actual"):
            if not isinstance(assertion[field], str):
                raise HarnessError(f"Scenario result assertion {field} must be a string.")
        if not assertion["subject"]:
            raise HarnessError("Scenario result assertion has no subject.")


def _validate_exceptions(exceptions: Any) -> None:
    if not isinstance(exceptions, list):
        raise HarnessError("Scenario result exceptions must be an array.")
    for exception in exceptions:
        if not isinstance(exception, dict) or set(exception) not in (
            {"type", "message"},
            {"type", "message", "stack"},
        ):
            raise HarnessError("Scenario result contains a malformed exception.")
        if not isinstance(exception["type"], str) or not exception["type"]:
            raise HarnessError("Scenario result exception has no type.")
        if not isinstance(exception["message"], str):
            raise HarnessError("Scenario result exception message must be a string.")
        if "stack" in exception and not isinstance(exception["stack"], str):
            raise HarnessError("Scenario result exception stack must be a string.")


def _validate_artifacts(artifacts: Any) -> None:
    if not isinstance(artifacts, list):
        raise HarnessError("Scenario result artifacts must be an array.")
    for artifact in artifacts:
        if not isinstance(artifact, dict) or set(artifact) != {"type", "path"}:
            raise HarnessError("Scenario result contains a malformed artifact.")
        if not isinstance(artifact["type"], str) or not artifact["type"]:
            raise HarnessError("Scenario result artifact has no type.")
        path = artifact["path"]
        if not isinstance(path, str) or not path or Path(path).is_absolute():
            raise HarnessError("Scenario result artifact path must be non-empty and relative.")
        if ".." in Path(path).parts:
            raise HarnessError("Scenario result artifact path escapes its run directory.")


def _parse_timestamp(value: Any) -> float:
    if not isinstance(value, str) or not value:
        raise HarnessError("Acceptance report has no capture timestamp.")
    normalized = value.replace("Z", "+00:00")
    try:
        parsed = dt.datetime.fromisoformat(normalized)
    except ValueError as error:
        raise HarnessError("Acceptance report has an invalid capture timestamp.") from error
    if parsed.tzinfo is None:
        raise HarnessError("Acceptance report timestamp has no timezone.")
    return parsed.timestamp()


def validate_flow_performance(report: dict[str, Any], expected_run_id: str | None) -> dict[str, Any]:
    def require(condition: bool, message: str) -> None:
        if not condition:
            raise HarnessError("Flow performance: " + message)

    def integer(value: Any) -> bool:
        return type(value) is int and 0 <= value <= 2**63 - 1

    def guid(value: Any) -> bool:
        try:
            return isinstance(value, str) and str(uuid.UUID(value)) == value and uuid.UUID(value).int != 0
        except (ValueError, AttributeError):
            return False

    def digest(value: Any) -> bool:
        return isinstance(value, str) and re.fullmatch(r"[a-f0-9]{64}", value) is not None

    captured = report.get("FlowPerformance")
    require(isinstance(captured, dict), "missing capture")
    require(guid(expected_run_id) and captured.get("RunId") == expected_run_id, "foreign or absent request identity")
    require(type(captured.get("FormatVersion")) is int and captured["FormatVersion"] == 1
            and captured.get("ScenarioId") == FLOW_PERFORMANCE["scenarioId"], "unsupported capture identity")
    require(guid(captured.get("SessionId")), "invalid session identity")
    require(digest(captured.get("RuntimeFingerprint")) and captured["RuntimeFingerprint"] == report.get("RuntimeFingerprint")
            and report.get("RuntimeFingerprintAlgorithm") == "sha256-flow-runtime-v1", "runtime fingerprint mismatch")
    fixed = {"WorldId": 4242424242, "Shipments": 80, "Routes": 3, "Stations": 6, "MaxOperationsPerTick": 64,
             "Loads": 1, "SavingEvents": 1, "SavedEvents": 1}
    require(all(type(captured.get(key)) is int and captured[key] == value for key, value in fixed.items()), "wrong workload or lifecycle")
    require(integer(captured.get("ThreadId")) and captured["ThreadId"] > 0
            and integer(captured.get("StopwatchFrequency")) and captured["StopwatchFrequency"] > 0, "invalid thread or timer")
    require(captured.get("GcOverride") is False and type(captured.get("ServerGc")) is bool, "unreported or overridden GC environment")
    require(all(isinstance(captured.get(key), str) and 0 < len(captured[key]) <= 1024
                for key in ("Runtime", "OperatingSystem", "Architecture")), "missing machine/runtime metadata")
    require(digest(captured.get("SaveTreeHash")), "missing saved tree fingerprint")
    counter_keys = {"TickInvocations", "Now", "PendingOperations", "RouteSearches", "ProcessedOperations",
                    "LastTickProcessedOperations", "RefreshRequests", "CheckpointCaptures", "PhysicalApplyCalls"}

    def counters(value: Any) -> dict[str, int]:
        require(isinstance(value, dict) and set(value) == counter_keys
                and all(integer(item) for item in value.values()), "invalid owner counters")
        require(value["CheckpointCaptures"] == 1 and value["RouteSearches"] > 0
                and value["PendingOperations"] <= 80 and value["LastTickProcessedOperations"] <= 64,
                "missing positive control or exceeded counter bounds")
        return value

    def identity(value: Any) -> None:
        require(isinstance(value, dict) and value.get("SessionId") == captured["SessionId"]
                and type(value.get("ThreadId")) is int and value["ThreadId"] == captured["ThreadId"],
                "window changed session or thread")

    windows = {}
    for name, time_passes, queue in (("Paused", False, 80), ("Idle", True, 0)):
        window = captured.get(name)
        identity(window)
        require(type(window.get("WarmupFrames")) is int and window["WarmupFrames"] == 120
                and type(window.get("Frames")) is int and window["Frames"] == 600, "incomplete warm-up or samples")
        require(window.get("TimePasses") is time_passes and window.get("CachedSnapshotUnchanged") is True,
                "wrong clock or changed cached projection")
        start, end = counters(window.get("Start")), counters(window.get("End"))
        require(end["TickInvocations"] - start["TickInvocations"] == 600 and start["TickInvocations"] >= 120
                and end["Now"] - start["Now"] == (600 if time_passes else 0), "window did not observe exactly one actual tick per frame")
        require(start["PendingOperations"] == end["PendingOperations"] == queue
                and start["LastTickProcessedOperations"] == end["LastTickProcessedOperations"] == 0
                and all(start[key] == end[key] for key in ("RouteSearches", "ProcessedOperations", "RefreshRequests",
                                                          "CheckpointCaptures", "PhysicalApplyCalls")), "steady window performed work")
        require(integer(window.get("AllocatedBytesTotal")) and window["AllocatedBytesTotal"] == 0
                and integer(window.get("MaximumAllocatedBytesPerTick")) and window["MaximumAllocatedBytesPerTick"] == 0,
                "steady tick allocated memory")
        times = [window.get(key) for key in ("P50TickMs", "P95TickMs", "P99TickMs", "MaxTickMs")]
        require(all(type(value) in (int, float) and 0 <= value <= 1_000_000_000 and math.isfinite(value) for value in times)
                and times == sorted(times), "invalid timing percentiles")
        require(times[1] <= FLOW_PERFORMANCE["maximumP95TickMs"] and times[2] <= FLOW_PERFORMANCE["maximumP99TickMs"],
                "steady tick latency exceeds the checked-in budget")
        windows[name] = (start, end)

    due = captured.get("Due")
    identity(due)
    require(integer(due.get("Frames")) and 1 < due["Frames"] <= 600
            and integer(due.get("WorkTicks")) and 1 < due["WorkTicks"] <= due["Frames"]
            and type(due.get("MaximumOperationsPerTick")) is int and due["MaximumOperationsPerTick"] == 64
            and type(due.get("Delivered")) is int and due["Delivered"] == 80, "due queue did not saturate and drain")
    start, end = counters(due.get("Start")), counters(due.get("End"))
    # The final work tick has the reported LastTick count; at least one tick reached the reported maximum.
    work_ticks, last = due["WorkTicks"], end["LastTickProcessedOperations"]
    minimum_operations = last + work_ticks - 1 + (63 if last < 64 else 0)
    maximum_operations = last + (work_ticks - 1) * 64
    require(minimum_operations <= 240 <= maximum_operations, "work tick counts cannot produce the reported operation total")
    require(start == windows["Paused"][1] and end["PendingOperations"] == 0
            and 0 < end["LastTickProcessedOperations"] <= 64
            and end["TickInvocations"] - start["TickInvocations"] == due["Frames"]
            and end["Now"] - start["Now"] == due["Frames"]
            and end["ProcessedOperations"] - start["ProcessedOperations"] == 240
            and end["PhysicalApplyCalls"] - start["PhysicalApplyCalls"] == 160
            and end["RefreshRequests"] - start["RefreshRequests"] == due["WorkTicks"]
            and end["RouteSearches"] == start["RouteSearches"], "due work counters or continuity differ")
    idle_start = windows["Idle"][0]
    require(idle_start["TickInvocations"] - end["TickInvocations"] == 120 and idle_start["Now"] - end["Now"] == 120
            and all(idle_start[key] == end[key] for key in ("PendingOperations", "ProcessedOperations", "RouteSearches",
                                                          "RefreshRequests", "CheckpointCaptures", "PhysicalApplyCalls")),
            "idle warm-up changed retained state or lost actual ticks")
    require(start["ProcessedOperations"] == start["PhysicalApplyCalls"] == start["RefreshRequests"] == 0,
            "work occurred before releasing the paused queue")
    return captured



def validate_flow_resources(report: dict[str, Any], expected_run_id: str | None) -> dict[str, Any]:
    """Validate the fixed producer's measurements and cross-phase authority, not merely green checks."""
    def require(condition: bool, message: str) -> None:
        if not condition:
            raise HarnessError("Flow resources: " + message)

    def integer(value: Any) -> bool:
        return type(value) is int and 0 <= value <= 2**63 - 1

    def guid(value: Any) -> bool:
        try:
            return isinstance(value, str) and str(uuid.UUID(value)) == value and uuid.UUID(value).int != 0
        except (ValueError, AttributeError):
            return False

    def digest(value: Any) -> bool:
        return isinstance(value, str) and re.fullmatch(r"[a-f0-9]{64}", value) is not None

    def obj(value: Any, message: str) -> dict[str, Any]:
        require(isinstance(value, dict), message)
        return value

    def fixed(value: dict[str, Any], expected: dict[str, int]) -> None:
        require(all(type(value.get(k)) is int and value[k] == v for k, v in expected.items()), "wrong fixed counts")

    captured = obj(report.get("FlowResources"), "missing capture")
    require(guid(expected_run_id) and captured.get("RunId") == expected_run_id
            and captured.get("ScenarioId") == FLOW_RESOURCES["scenarioId"], "foreign request or scenario")
    fixed(captured, dict(FormatVersion=1, WorldId=4242424242, Shipments=256, Stations=32, SourceChests=8,
                        RoutingQueries=75, Loads=7, Titles=6, SavingEvents=6, SavedEvents=6,
                        SendRefusals=2, RetryRefusals=256, ReturnRefusals=256))
    require(digest(captured.get("RuntimeFingerprint")) and captured["RuntimeFingerprint"] == report.get("RuntimeFingerprint")
            and report.get("RuntimeFingerprintAlgorithm") == "sha256-flow-runtime-v1", "foreign runtime")
    require(integer(captured.get("ThreadId")) and captured["ThreadId"] > 0
            and integer(captured.get("StopwatchFrequency")) and captured["StopwatchFrequency"] > 0, "invalid owner or timer")
    require(captured.get("GcOverride") is False and type(captured.get("ServerGc")) is bool, "overridden or missing GC metadata")
    require(all(isinstance(captured.get(k), str) and 0 < len(captured[k]) <= 1024
                for k in ("Runtime", "OperatingSystem", "Architecture")) and digest(captured.get("SaveTreeHash")), "missing environment or save fingerprint")
    station_roles = captured.get("StationIds")
    require(isinstance(station_roles, list) and len(station_roles) == 32 and all(guid(v) for v in station_roles)
            and len(set(station_roles)) == 32, "missing distinct fixed station roles")

    def rows(key: str, count: int) -> list[dict[str, Any]]:
        values = captured.get(key)
        require(isinstance(values, list) and len(values) == count and all(isinstance(v, dict) for v in values), "incomplete " + key)
        return values

    def cost(value: Any) -> None:
        value = obj(value, "missing operation measurement")
        require(set(value) == {"ElapsedTicks", "AllocatedBytes"} and all(integer(v) for v in value.values()), "invalid operation measurement")

    counter_keys = {"TickInvocations", "Now", "PendingOperations", "RouteSearches", "ProcessedOperations",
                    "LastTickProcessedOperations", "RefreshRequests", "CheckpointCaptures", "PhysicalApplyCalls"}

    def counters(value: Any) -> dict[str, int]:
        value = obj(value, "missing owner counters")
        require(set(value) == counter_keys and all(integer(v) for v in value.values())
                and value["PendingOperations"] <= 256 and value["LastTickProcessedOperations"] <= 64
                and value["CheckpointCaptures"] <= 1, "invalid or exceeded owner counters")
        return value

    def usage(value: Any, limit: int, used: int | None = None) -> dict[str, int]:
        value = obj(value, "missing resource usage")
        require(set(value) == {"Used", "Limit"} and all(integer(v) for v in value.values())
                and value["Limit"] == limit and value["Used"] <= limit
                and (used is None or value["Used"] == used), "invalid resource usage or capacity")
        return value

    def resources(value: Any, count: int, attempt: int, saving: bool = False, clean: bool = False) -> dict[str, Any]:
        value = obj(value, "missing retained resources")
        require(guid(value.get("SessionId")) and guid(value.get("NetworkId")), "invalid retained identity")
        fixed(value, dict(SaveId=4242424242, State=3 if saving else 0, MaxCharactersPerPayload=65536,
                          MaxCoreCheckpointBytes=64 * 1024 * 1024, MaximumObservedDeliveryAttempts=attempt,
                          ParcelsAtAttemptLimit=count if attempt == 16 else 0))
        require(integer(value.get("Revision")) and integer(value.get("Now")), "invalid retained clock/revision")
        owner = counters(value.get("TickCounters"))
        require(owner["Now"] == value["Now"], "resource and owner clocks differ")
        runtime = obj(value.get("Runtime"), "missing runtime resources")
        for key, limit in {"Stations": 32, "LifetimeLinks": 128, "Cargo": 256, "Shipments": 256, "Parcels": 256,
                           "PendingOperations": 256, "RouteCache": 64, "Events": 256, "IssuedTransfers": 4352}.items():
            expected = {"Stations": 0 if clean else 32, "LifetimeLinks": 0 if clean else 40,
                        "Cargo": count, "Shipments": count, "Parcels": count, "PendingOperations": count if attempt == 0 else 0,
                        "IssuedTransfers": count * (attempt + 1) if attempt else 0}.get(key)
            usage(runtime.get(key), limit, expected)
        fixed(runtime, dict(ActiveLinks=0 if clean else 40, RetiredTransfers=count * (attempt + 1) if attempt else 0,
                            MaxRouteVisits=32, MaxOperationsPerAdvance=64, MaxDeliveryAttempts=16))
        require(integer(runtime.get("RouteSearches")) and runtime["RouteSearches"] == owner["RouteSearches"]
                and runtime["PendingOperations"]["Used"] == owner["PendingOperations"], "runtime counters disagree")
        usage(value.get("Payloads"), 256, count)
        chars = usage(value.get("PayloadCharacters"), 256 * 65536)["Used"]
        require((count == 0 and chars == 0) or count <= chars <= count * 65536, "invalid item payload extent")
        ports = value.get("Ports")
        require(isinstance(ports, list) and len(ports) == (0 if clean else 32), "wrong port set")
        ids = []
        for row in ports:
            row = obj(row, "invalid port row")
            require(guid(row.get("StationId")), "invalid station identity")
            ids.append(row["StationId"])
            port = obj(row.get("Port"), "missing port usage")
            require(row["StationId"] in station_roles, "foreign port station")
            index = station_roles.index(row["StationId"])
            group = 0 if index == 0 else index - 1
            admitted = min(32, max(0, count - group * 32)) if index == 0 or 2 <= index <= 8 else 0
            receipts = count * attempt if index == 1 else admitted if attempt else 0
            usage(port.get("Receipts"), 4096, receipts)
            usage(port.get("Custody"), 1024, admitted if not attempt else 0)
        require(len(set(ids)) == len(ids), "duplicate station resources")
        refusals = obj(value.get("AdmissionRejections"), "missing refusal counters")
        require(set(refusals) == {"Stations", "LifetimeLinks", "RetainedCargo"} and all(integer(v) for v in refusals.values()), "invalid refusals")
        return value

    route_rows = rows("Routes", 75)
    expected_routes = []
    for stations in (2, 16, 32):
        expected_routes.extend([(kind, stations, 0, stations - 1, stations - 1, delta) for kind, delta in (("cold", 1), ("hit", 0))])
    pairs = [(a, b) for a in range(32) for b in range(a + 1, 32) if not (a == 0 and b in (1, 15, 31))][:65]
    expected_routes += [("churn", 32, a, b, b - a, 1) for a, b in pairs]
    expected_routes += [("evicted", 32, 0, 2, 2, 1), ("anchor", 32, 0, 31, 31, 1),
                        ("independent", 32, 0, 31, 31, 0), ("dependent", 32, 0, 31, 1, 1)]
    searches = 0
    cache = 0
    for row, (kind, stations, origin, destination, hops, delta) in zip(route_rows, expected_routes):
        require(row.get("Kind") == kind, "wrong route profile sequence")
        fixed(row, dict(Stations=stations, Origin=origin, Destination=destination, Hops=hops, SearchesBefore=searches, SearchesAfter=searches + delta))
        if kind in ("cold", "churn", "evicted", "anchor"):
            cache = min(64, cache + 1)
        fixed(row, dict(CacheCount=cache)); cost(row.get("Cost")); searches += delta

    saves, restores, waves = rows("Saves", 6), rows("Restores", 7), rows("Waves", 16)
    counts, attempts = [32, 128, 256, 256, 256, 256], [0, 0, 0, 1, 8, 16]
    sessions, network, station_ids = [], None, None
    for index, row in enumerate(restores):
        fixed(row, dict(Index=index + 1))
        require(row.get("HasAggregate") is (index > 0), "wrong restore aggregate presence")
        value = resources(row.get("Resources"), counts[index - 1] if index else 0, attempts[index - 1] if index else 0, clean=index == 0)
        sessions.append(value["SessionId"])
        network = network or value["NetworkId"]
        require(value["NetworkId"] == network, "reload changed network identity")
        require(all(value["TickCounters"][k] == 0 for k in counter_keys - {"Now", "PendingOperations"})
                and value["Runtime"]["RouteCache"]["Used"] == 0, "restore performed work, search or capture")
        if index:
            previous = saves[index - 1]
            require(integer(row.get("CheckpointBytes")) and row["CheckpointBytes"] > 0 and digest(row.get("CheckpointHash"))
                    and row["CheckpointBytes"] == previous.get("CheckpointBytes") and row["CheckpointHash"] == previous.get("CheckpointHash"), "restore read a different checkpoint")
            before = resources(previous.get("Resources"), counts[index - 1], attempts[index - 1], saving=True)
            for key in ("Now", "Payloads", "PayloadCharacters", "Ports"):
                require(value[key] == before[key], "restore changed retained " + key)
            restored_runtime = dict(value["Runtime"])
            restored_runtime["RouteSearches"] = before["Runtime"]["RouteSearches"]
            restored_runtime["RouteCache"] = before["Runtime"]["RouteCache"]
            require(restored_runtime == before["Runtime"], "restore changed retained runtime resources")
        else:
            fixed(row, dict(CheckpointBytes=0)); require(row.get("CheckpointHash") == "", "initial restore contains a checkpoint")
        cost(row.get("Read")); cost(row.get("Restore"))
    require(len(set(sessions)) == 7, "loads reused command authority")
    for index, row in enumerate(saves):
        fixed(row, dict(Index=index + 1))
        value = resources(row.get("Resources"), counts[index], attempts[index], saving=True)
        require(value["SessionId"] == sessions[index] and value["NetworkId"] == network, "save changed owner")
        ids = [port["StationId"] for port in value["Ports"]]
        station_ids = station_ids or ids
        require(ids == station_ids, "save changed stations")
        require(integer(row.get("CheckpointBytes")) and 0 < row["CheckpointBytes"] <= 64 * 1024 * 1024
                and digest(row.get("CheckpointHash")), "missing bounded checkpoint")
        if index:
            require(row["CheckpointBytes"] > saves[index - 1]["CheckpointBytes"], "checkpoint did not retain additional cargo/receipts")
        fixed(value["TickCounters"], dict(CheckpointCaptures=1))
        if index < 3:
            fixed(value["TickCounters"], dict(ProcessedOperations=0, PhysicalApplyCalls=0, RefreshRequests=0,
                                               LastTickProcessedOperations=0, RouteSearches=[searches + 1, 3, 4][index]))
            require(value["Now"] == restores[index]["Resources"]["Now"], "reserved queue advanced before dispatch")
            usage(value["Runtime"]["RouteCache"], 64, [64, 3, 4][index])
        fixed(value["AdmissionRejections"], dict(Stations=0, LifetimeLinks=0, RetainedCargo=0))
        cost(row.get("Capture")); cost(row.get("Write"))

    def timing(row: dict[str, Any]) -> list[float]:
        times = [row.get(key) for key in ("P50TickMs", "P95TickMs", "P99TickMs", "MaxTickMs")]
        require(all(type(v) in (int, float) and math.isfinite(v) and 0 <= v <= 1_000_000_000 for v in times)
                and times == sorted(times), "invalid timing percentiles")
        require(integer(row.get("AllocatedBytesTotal")) and integer(row.get("MaximumAllocatedBytesPerTick"))
                and row["MaximumAllocatedBytesPerTick"] <= row["AllocatedBytesTotal"]
                <= row["MaximumAllocatedBytesPerTick"] * row["Frames"], "invalid allocation measurements")
        return times

    for index, wave in enumerate(waves):
        fixed(wave, dict(Attempt=index + 1, ThreadId=captured["ThreadId"], MaximumOperationsPerTick=64))
        load_index = 3 if index == 0 else 4 if index < 8 else 5
        require(wave.get("SessionId") == sessions[load_index], "wave crossed session authority")
        require(integer(wave.get("Frames")) and 1 < wave["Frames"] <= 600
                and integer(wave.get("WorkTicks")) and 1 < wave["WorkTicks"] <= wave["Frames"], "incomplete work frames")
        start, end = counters(wave.get("Start")), counters(wave.get("End"))
        operations, effects = (768, 512) if index == 0 else (256, 256)
        require(start["PendingOperations"] == 256 and end["PendingOperations"] == 0 and 0 < end["LastTickProcessedOperations"] <= 64
                and end["TickInvocations"] - start["TickInvocations"] == wave["Frames"]
                and end["Now"] - start["Now"] == wave["Frames"]
                and end["ProcessedOperations"] - start["ProcessedOperations"] == operations
                and end["PhysicalApplyCalls"] - start["PhysicalApplyCalls"] == effects
                and end["RefreshRequests"] - start["RefreshRequests"] == wave["WorkTicks"]
                and start["CheckpointCaptures"] == end["CheckpointCaptures"] == 0
                and start["RouteSearches"] == end["RouteSearches"] == 0, "wave operation/effect accounting differs")
        last, work = end["LastTickProcessedOperations"], wave["WorkTicks"]
        require(last + work - 1 + (63 if last < 64 else 0) <= operations <= last + (work - 1) * 64, "impossible bounded work counts")
        previous = restores[load_index]["Resources"]["TickCounters"] if index in (0, 1, 8) else waves[index - 1]["End"]
        require(all(start[key] == previous[key] for key in counter_keys - {"PendingOperations"}), "lost ticks or hidden work before wave")
        if index in (0, 7, 15):
            saved_counters = saves[{0: 3, 7: 4, 15: 5}[index]]["Resources"]["TickCounters"]
            require(all(saved_counters[key] == end[key] for key in ("Now", "ProcessedOperations", "PhysicalApplyCalls", "RefreshRequests")), "save missed settled work")
        timing(wave)

    before, final = resources(captured.get("BeforeIdle"), 256, 16), resources(captured.get("Final"), 256, 16)
    for value in (before, final):
        require(value["SessionId"] == sessions[6] and value["NetworkId"] == network, "idle changed authority")
        fixed(value["AdmissionRejections"], dict(Stations=0, LifetimeLinks=0, RetainedCargo=2))
    require({k: v for k, v in before.items() if k not in ("Now", "TickCounters")}
            == {k: v for k, v in final.items() if k not in ("Now", "TickCounters")}, "idle grew retained resources")
    require(all(before[k] == restores[6]["Resources"][k] for k in ("Runtime", "Ports", "Payloads", "PayloadCharacters", "TickCounters")), "refusals changed authority or journal")
    idle = obj(captured.get("Idle"), "missing idle window")
    fixed(idle, dict(ThreadId=captured["ThreadId"], WarmupFrames=120, Frames=600, AllocatedBytesTotal=0, MaximumAllocatedBytesPerTick=0))
    require(idle.get("SessionId") == sessions[6] and idle.get("TimePasses") is True and idle.get("CachedSnapshotUnchanged") is True, "invalid idle identity, clock or projection")
    start, end = counters(idle.get("Start")), counters(idle.get("End"))
    baseline = before["TickCounters"]
    for value, delta in ((start, 120), (end, 720)):
        require(value["Now"] == baseline["Now"] + delta and value["TickInvocations"] == baseline["TickInvocations"] + delta
                and all(value[k] == baseline[k] for k in counter_keys - {"Now", "TickInvocations"}), "idle performed work or lost ticks")
    require(final["TickCounters"] == end, "final diagnostics missed the idle boundary")
    times = timing(idle)
    require(times[1] <= 0.25 and times[2] <= 1.0, "idle exceeded checked-in latency budgets")
    return captured


def validate_report(
    resolved: dict[str, Any], report: dict[str, Any], started_at: float, *, expected_run_id: str | None = None
) -> dict[str, Any]:
    if report.get("FormatVersion") != 3 or report.get("PerformanceFormatVersion") != 3:
        raise HarnessError(
            "Acceptance report format does not contain semantic v3 telemetry.",
            [_assertion(
                "HARNESS-REPORT-SCHEMA",
                "FAIL",
                resolved["id"],
                "FormatVersion=3 and PerformanceFormatVersion=3",
                f"FormatVersion={report.get('FormatVersion')!r}, "
                f"PerformanceFormatVersion={report.get('PerformanceFormatVersion')!r}",
            )],
        )
    if not report.get("RuntimeFingerprint"):
        raise HarnessError(
            "Acceptance report has no runtime fingerprint.",
            [_assertion(
                "HARNESS-RUNTIME-FINGERPRINT",
                "FAIL",
                resolved["id"],
                "A non-empty runtime fingerprint",
                "missing",
            )],
        )
    if _parse_timestamp(report.get("CapturedAtUtc")) + 1 < started_at:
        raise HarnessError(
            "Acceptance report predates this harness session.",
            [_assertion(
                "HARNESS-REPORT-FRESHNESS",
                "FAIL",
                resolved["id"],
                "The report is captured during this harness session.",
                str(report.get("CapturedAtUtc")),
            )],
        )

    checks = report.get("HostChecks")
    if not isinstance(checks, list):
        raise HarnessError("Acceptance report has no host checks.")
    checks_by_id: dict[str, dict[str, Any]] = {}
    duplicate_check_ids: list[str] = []
    for item in checks:
        if not isinstance(item, dict):
            continue
        check_id = item.get("Id")
        if not isinstance(check_id, str):
            continue
        if check_id in checks_by_id:
            if check_id not in duplicate_check_ids:
                duplicate_check_ids.append(check_id)
            continue
        checks_by_id[check_id] = item
    if duplicate_check_ids:
        raise HarnessError(
            "Duplicate host check ids: " + ", ".join(duplicate_check_ids)
        )
    resolved_check_ids = set(resolved["checks"])
    unexpected = [check_id for check_id in checks_by_id if check_id not in resolved_check_ids]
    if unexpected:
        raise HarnessError("Unexpected host checks: " + ", ".join(unexpected))
    missing = [check for check in resolved["checks"] if check not in checks_by_id]
    failed = [
        check
        for check in resolved["checks"]
        if check in checks_by_id and checks_by_id[check].get("Passed") is not True
    ]
    if missing:
        raise HarnessError(
            "Missing required host checks: " + ", ".join(missing),
            [
                _assertion(
                    check,
                    "FAIL",
                    resolved["id"],
                    "The required host check is recorded and passes.",
                    "missing",
                )
                for check in missing
            ],
        )
    if failed:
        raise HarnessError(
            "Failed required host checks: " + ", ".join(failed),
            [
                _assertion(
                    check,
                    "FAIL",
                    resolved["id"],
                    "The required host check passes.",
                    str(checks_by_id[check].get("Note") or "recorded as failed"),
                )
                for check in failed
            ],
        )

    assertions = [
        _assertion(
            check,
            "PASS",
            resolved["id"],
            "The required host check passes.",
            str(checks_by_id[check].get("Note") or "recorded as passed"),
        )
        for check in resolved["checks"]
    ]

    performance = resolved["performance"]
    performance_evidence: dict[str, Any] = {}
    if performance is not None:
        scenarios = report.get("Scenarios")
        if not isinstance(scenarios, list):
            raise HarnessError("Acceptance report has no performance scenarios.")
        captured = next(
            (
                item
                for item in scenarios
                if isinstance(item, dict) and item.get("Id") == performance["scenarioId"]
            ),
            None,
        )
        if captured is None:
            raise HarnessError(
                f"Missing performance scenario '{performance['scenarioId']}'.",
                [_assertion(
                    f"{performance['scenarioId']}.capture",
                    "FAIL",
                    resolved["id"],
                    "The required performance capture is present.",
                    "missing",
                )],
            )
        frames = captured.get("Frames")
        if not isinstance(frames, int) or frames < performance["minimumFrames"]:
            raise HarnessError(
                f"Performance scenario captured {frames!r} frames; "
                f"at least {performance['minimumFrames']} are required."
            )
        surface_kinds = captured.get("SurfaceKinds")
        if not isinstance(surface_kinds, list):
            raise HarnessError("Performance scenario has no semantic surface identities.")
        missing_surfaces = sorted(set(performance["surfaceKinds"]) - set(surface_kinds))
        if missing_surfaces:
            raise HarnessError(
                "Performance scenario did not exercise required semantic surfaces: "
                + ", ".join(missing_surfaces)
            )

        budgets = (
            ("P95UiThreadMs", "maximumP95UiThreadMs"),
            ("P99UiThreadMs", "maximumP99UiThreadMs"),
            ("SteadyStateAllocatedBytesPerFrame", "maximumAllocatedBytesPerFrame"),
            ("MeasureCacheMissRatio", "maximumLayoutCacheMissRatio"),
            ("ArrangeCacheMissRatio", "maximumLayoutCacheMissRatio"),
        )
        violations = []
        for report_key, manifest_key in budgets:
            value = captured.get(report_key)
            maximum = performance[manifest_key]
            if not isinstance(value, (int, float)) or value < 0 or value > maximum:
                violations.append(f"{report_key}={value!r} > {maximum}")
        if violations:
            raise HarnessError(
                "Performance budget violations: " + "; ".join(violations),
                [_assertion(
                    f"{performance['scenarioId']}.budget",
                    "FAIL",
                    resolved["id"],
                    "All checked-in performance budgets are satisfied.",
                    "; ".join(violations),
                )],
            )
        performance_evidence = {
            "scenario": performance["scenarioId"],
            "frames": frames,
            "p95UiThreadMs": captured["P95UiThreadMs"],
            "p99UiThreadMs": captured["P99UiThreadMs"],
            "allocatedBytesPerFrame": captured["SteadyStateAllocatedBytesPerFrame"],
            "measureCacheMissRatio": captured["MeasureCacheMissRatio"],
            "arrangeCacheMissRatio": captured["ArrangeCacheMissRatio"],
        }
        assertions.append(_assertion(
            f"{performance['scenarioId']}.budget",
            "PASS",
            resolved["id"],
            "All checked-in performance budgets are satisfied.",
            f"frames={frames}, p95={captured['P95UiThreadMs']}, "
            f"p99={captured['P99UiThreadMs']}",
        ))

    if resolved.get("flowPerformance") is not None:
        performance_evidence = validate_flow_performance(report, expected_run_id)
        assertions.append(_assertion(
            FLOW_PERFORMANCE["scenarioId"] + ".budget", "PASS", resolved["id"],
            "The fixed Flow workload satisfies all operation, allocation and latency budgets.",
            "600 paused and 600 idle ticks; bounded 80-parcel delivery completed.",
        ))

    if resolved.get("flowResources") is not None:
        performance_evidence = validate_flow_resources(report, expected_run_id)
        assertions.append(_assertion(
            FLOW_RESOURCES["scenarioId"] + ".budget", "PASS", resolved["id"],
            "The fixed resource scale, confirmed saves, receipt growth and idle budgets are satisfied.",
            "256 parcels, 16 attempts, six confirmed saves and seven loads; 600 idle samples.",
        ))

    return {
        "runtimeFingerprint": report["RuntimeFingerprint"],
        "gameVersion": report.get("GameVersion"),
        "smapiVersion": report.get("SmapiVersion"),
        "checks": resolved["checks"],
        "performance": performance_evidence,
        "assertions": assertions,
    }


def command_check(args: argparse.Namespace) -> int:
    resolved = resolve_scenario(load_manifest(Path(args.manifest)), args.scenario, args.kind)
    if args.json:
        print(json.dumps(resolved, indent=2, sort_keys=True))
    elif args.field:
        value = resolved[args.field]
        print(str(value).lower() if isinstance(value, bool) else value)
    else:
        print(f"Scenario: {resolved['id']} ({resolved['kind']})")
        print(
            "Automation: "
            f"{resolved['automation']['driver']} protocol "
            f"v{resolved['automation']['protocolVersion']}"
        )
    return 0


def command_result(args: argparse.Namespace) -> int:
    write_result(
        Path(args.result),
        args.status,
        args.scenario,
        args.message,
        run_id=args.run_id,
        duration_ms=args.duration_ms,
        assertions=[_assertion(
            args.assertion_id,
            args.status,
            args.scenario,
            args.expected,
            args.message,
        )],
        exceptions=(
            [{"type": args.exception_type, "message": args.message}]
            if args.exception_type else []
        ),
        artifacts=collect_artifacts(Path(args.artifact_root)) if args.artifact_root else [],
    )
    return 0


def validate_ui_runtime_diagnostics(
    resolved: dict[str, Any], artifact_root: Path, run_id: str, started_at: float
) -> None:
    """A report's earlier check verdicts cannot attest final evidence publication."""
    def require(condition: bool, message: str) -> None:
        if not condition:
            raise HarnessError(message)

    def number(value: Any) -> bool:
        return type(value) in (int, float) and math.isfinite(value)

    try:
        root = artifact_root.resolve()
        path = root / "diagnostics" / "runtime.json"
        require(path.resolve().is_relative_to(root) and path.is_file(),
                "The published UI runtime diagnostics file is missing or invalid.")
        payload = json.loads(path.read_text(encoding="utf-8"))
        require(isinstance(payload, dict), "UI runtime diagnostics must be an object.")
        require(type(payload.get("protocolVersion")) is int and payload["protocolVersion"] == PROTOCOL_VERSION,
                "UI runtime diagnostics have an unsupported protocol version.")
        require(payload.get("runId") == run_id and payload.get("scenario") == resolved["id"],
                "UI runtime diagnostics belong to another request or scenario.")
        require("terminalError" in payload and payload["terminalError"] is None,
                "UI runtime diagnostics contain a terminal failure or omit its outcome.")
        require(_parse_timestamp(payload.get("capturedAtUtc")) + 1 >= started_at,
                "UI runtime diagnostics predate this request.")
        require(type(payload.get("processId")) is int and payload["processId"] > 0,
                "UI runtime diagnostics have no valid process identity.")
        if "semantic.viewport.reflow" not in resolved["checks"]:
            return

        captures = payload.get("visualMatrix")
        require(payload.get("visualMatrixRestored") is True,
                "The rendered matrix did not confirm restored settings.")
        require(isinstance(captures, list) and len(captures) == 8,
                "The rendered matrix must retain all eight states.")
        targets = [(locale, scale) for locale in ("en", "ru") for scale in (0.75, 1.0, 1.25, 1.5)]
        previous_frame = 0
        for capture, (locale, scale) in zip(captures, targets):
            require(isinstance(capture, dict), "A rendered matrix capture is not an object.")
            require(capture.get("Locale") == locale and number(capture.get("Scale"))
                    and capture["Scale"] == scale, "The rendered matrix state order is incomplete or duplicated.")
            frame = capture.get("CompletedFrame")
            require(type(frame) is int and frame > previous_frame,
                    "Rendered matrix captures must come from increasing completed frames.")
            previous_frame = frame
            require(isinstance(capture.get("ProbeText"), str) and bool(capture["ProbeText"].strip()),
                    "A rendered matrix capture has no probe text.")
            observation = capture.get("Observation")
            require(isinstance(observation, dict), "A rendered matrix capture has no observation.")
            require(observation.get("GameLocale") == locale and observation.get("SceneLocale") == locale
                    and observation.get("Theme") == "Hatifect.UI/theme/Dark",
                    "Rendered matrix locale or theme does not match the target.")
            require(all(number(observation.get(key)) and abs(observation[key] - scale) < 0.0001
                        for key in ("DesiredScale", "BaseScale")),
                    "Rendered matrix desired and applied scale do not match the target.")
            pixel_scale = observation.get("PixelScale")
            require(number(pixel_scale) and pixel_scale > 0,
                    "Rendered matrix pixel scale must be finite and positive.")
            require(all(type(observation.get(key)) is int and observation[key] > 0 for key in (
                "BackBufferWidth", "BackBufferHeight", "ViewportWidth", "ViewportHeight", "MenuWidth", "MenuHeight")),
                "Rendered matrix geometry must be positive integers.")
            require(all(type(observation.get(key)) is int and observation[key] == 0 for key in ("MenuX", "MenuY")),
                    "Rendered matrix menu origin must match the viewport.")
            for axis in ("Width", "Height"):
                scaled = observation["BackBuffer" + axis] / pixel_scale
                require(math.isfinite(scaled) and observation["Viewport" + axis] == math.ceil(scaled)
                        and observation["Menu" + axis] == observation["Viewport" + axis],
                        "Rendered matrix menu and scaled viewport disagree.")
            require(all(observation.get(key) is True for key in ("HasValidTree", "HasProbeText", "LoadFadeFinished")),
                    "Rendered matrix tree, probe or load-fade confirmation is absent.")
            name = f"screenshots/matrix-{locale}-{int(scale * 100)}-dark"
            for key, suffix in (("Screenshot", ".png"), ("UiLayerScreenshot", "-ui-layer.png")):
                expected = name + suffix
                require(capture.get(key) == expected, "A rendered matrix PNG has an unexpected path.")
                screenshot = root / expected
                require(screenshot.resolve().is_relative_to(root) and screenshot.is_file(),
                        "A required rendered matrix PNG is missing or outside this request.")
                with screenshot.open("rb") as stream:
                    require(stream.read(8) == b"\x89PNG\r\n\x1a\n", "A rendered matrix capture is not a PNG.")
    except (HarnessError, OSError, UnicodeError, ValueError, OverflowError, RecursionError) as error:
        message = f"UI runtime diagnostics: {error}"
        raise HarnessError(message, [_assertion(
            "HARNESS-RUNTIME-DIAGNOSTICS", "FAIL", resolved["id"],
            "Published request-owned diagnostics confirm completion without a terminal failure.", message,
        )]) from error


def command_finalize(args: argparse.Namespace) -> int:
    result_path = Path(args.result)
    duration_ms = max(0, int((time.time() - args.started_at) * 1000))
    artifact_root = Path(args.artifact_root) if args.artifact_root else result_path.parent
    try:
        resolved = resolve_scenario(load_manifest(Path(args.manifest)), args.scenario, args.kind)
        with Path(args.report).open("r", encoding="utf-8") as stream:
            report = json.load(stream)
        evidence = validate_report(resolved, report, args.started_at, expected_run_id=args.run_id)
        if resolved["kind"] == "ui":
            validate_ui_runtime_diagnostics(resolved, artifact_root, args.run_id or result_path.parent.name, args.started_at)
        write_result(
            result_path,
            "PASS",
            args.scenario,
            "All required assisted live checks and telemetry passed.",
            run_id=args.run_id,
            duration_ms=duration_ms,
            assertions=evidence["assertions"],
            artifacts=collect_artifacts(artifact_root),
        )
        return 0
    except (HarnessError, OSError, json.JSONDecodeError) as error:
        write_result(
            result_path,
            "FAIL",
            args.scenario,
            str(error),
            run_id=args.run_id,
            duration_ms=duration_ms,
            assertions=(
                error.assertions
                if isinstance(error, HarnessError) and error.assertions
                else [_assertion(
                    "HARNESS-REPORT-VALIDATION",
                    "FAIL",
                    args.scenario,
                    "The fresh acceptance report satisfies the scenario contract.",
                    str(error),
                )]
            ),
            exceptions=[{"type": type(error).__name__, "message": str(error)}],
            artifacts=collect_artifacts(artifact_root),
        )
        print(f"Live harness validation failed: {error}", file=sys.stderr)
        return 1


def command_validate_result(args: argparse.Namespace) -> int:
    try:
        with Path(args.result).open("r", encoding="utf-8") as stream:
            document = json.load(stream)
        status = validate_result(document, args.scenario)
        if args.require_failure_envelope:
            validate_failure_artifacts(Path(args.result), document)
    except (HarnessError, OSError, json.JSONDecodeError) as error:
        print(f"Invalid scenario result: {error}", file=sys.stderr)
        return 2
    if args.field == "status":
        print(status)
    return 0


def command_validate_deployment(args: argparse.Namespace) -> int:
    try:
        with Path(args.marker).open("r", encoding="utf-8") as stream:
            document = json.load(stream)
        if not isinstance(document, dict) or set(document) != {
            "formatVersion",
            "preparedAtUtc",
            "status",
        }:
            raise HarnessError("Deployment marker does not match format v1 fields.")
        if document["formatVersion"] != 1 or document["status"] != "prepared":
            raise HarnessError("Deployment marker does not identify a prepared deployment.")
        _parse_timestamp(document["preparedAtUtc"])
    except (HarnessError, OSError, json.JSONDecodeError) as error:
        print(f"Invalid isolated deployment marker: {error}", file=sys.stderr)
        return 2
    return 0


def command_validate_required_mods(args: argparse.Namespace) -> int:
    resolved = resolve_scenario(
        load_manifest(Path(args.manifest)), args.scenario, args.kind
    )
    missing = validate_required_mods(resolved, Path(args.mods_root))
    if missing:
        print(
            "Missing required isolated SMAPI mod(s): " + ", ".join(missing),
            file=sys.stderr,
        )
        return 2
    return 0


def command_write_deployment(args: argparse.Namespace) -> int:
    _atomic_write_json(
        Path(args.marker),
        {
            "formatVersion": 1,
            "preparedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
            "status": "prepared",
        },
    )
    return command_validate_deployment(args)


def command_contained(args: argparse.Namespace) -> int:
    root = Path(args.root).resolve()
    candidate = Path(args.path).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as error:
        raise HarnessError(f"Path escapes isolated harness root: {candidate}") from error
    print(candidate)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.set_defaults(function=None)
    subparsers = parser.add_subparsers(dest="command")

    check = subparsers.add_parser("check-scenario")
    check.add_argument("kind", choices=("smoke", "ui"))
    check.add_argument("scenario")
    check.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    check.add_argument("--json", action="store_true")
    check.add_argument(
        "--field",
        choices=("id", "kind", "requiresSave", "timeoutSeconds"),
        default=None,
    )
    check.set_defaults(function=command_check)

    result = subparsers.add_parser("write-result")
    result.add_argument("status", choices=("PASS", "FAIL", "BLOCKED"))
    result.add_argument("scenario")
    result.add_argument("result")
    result.add_argument("message")
    result.add_argument("--run-id", default=None)
    result.add_argument("--duration-ms", type=int, default=0)
    result.add_argument("--assertion-id", default="HARNESS-EXECUTION")
    result.add_argument(
        "--expected",
        default="The isolated scenario completes with structured evidence.",
    )
    result.add_argument("--exception-type", default=None)
    result.add_argument("--artifact-root", default=None)
    result.set_defaults(function=command_result)

    finalize = subparsers.add_parser("finalize")
    finalize.add_argument("kind", choices=("smoke", "ui"))
    finalize.add_argument("scenario")
    finalize.add_argument("report")
    finalize.add_argument("result")
    finalize.add_argument("started_at", type=float)
    finalize.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    finalize.add_argument("--run-id", default=None)
    finalize.add_argument("--artifact-root", default=None)
    finalize.set_defaults(function=command_finalize)

    validate = subparsers.add_parser("validate-result")
    validate.add_argument("result")
    validate.add_argument("scenario", nargs="?", default=None)
    validate.add_argument("--field", choices=("status",), default=None)
    validate.add_argument("--require-failure-envelope", action="store_true")
    validate.set_defaults(function=command_validate_result)

    deployment = subparsers.add_parser("validate-deployment")
    deployment.add_argument("marker")
    deployment.set_defaults(function=command_validate_deployment)

    required_mods = subparsers.add_parser("validate-required-mods")
    required_mods.add_argument("kind", choices=("smoke", "ui"))
    required_mods.add_argument("scenario")
    required_mods.add_argument("mods_root")
    required_mods.add_argument("--manifest", default=str(DEFAULT_MANIFEST))
    required_mods.set_defaults(function=command_validate_required_mods)

    write_deployment = subparsers.add_parser("write-deployment")
    write_deployment.add_argument("marker")
    write_deployment.set_defaults(function=command_write_deployment)

    contained = subparsers.add_parser("assert-contained")
    contained.add_argument("root")
    contained.add_argument("path")
    contained.set_defaults(function=command_contained)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.function is None:
        raise HarnessError("A live-harness command is required.")
    return args.function(args)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except HarnessError as error:
        print(f"Live harness configuration error: {error}", file=sys.stderr)
        raise SystemExit(2)
