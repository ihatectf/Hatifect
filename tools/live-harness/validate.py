#!/usr/bin/env python3
"""Fail-closed manifest and evidence validator for the assisted SMAPI harness."""

from __future__ import annotations

import argparse
import datetime as dt
import json
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
    if len(performances) > 1:
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


def validate_report(
    resolved: dict[str, Any], report: dict[str, Any], started_at: float
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
        evidence = validate_report(resolved, report, args.started_at)
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
