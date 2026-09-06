#!/usr/bin/env python3
"""Fail-closed manifest and evidence validator for the assisted SMAPI harness."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import uuid
import os
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
        if relative == "result.json" or relative.endswith(".tmp"):
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
    encoded = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")
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


def command_finalize(args: argparse.Namespace) -> int:
    result_path = Path(args.result)
    duration_ms = max(0, int((time.time() - args.started_at) * 1000))
    artifact_root = Path(args.artifact_root) if args.artifact_root else result_path.parent
    try:
        resolved = resolve_scenario(load_manifest(Path(args.manifest)), args.scenario, args.kind)
        with Path(args.report).open("r", encoding="utf-8") as stream:
            report = json.load(stream)
        evidence = validate_report(resolved, report, args.started_at, expected_run_id=args.run_id)
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
