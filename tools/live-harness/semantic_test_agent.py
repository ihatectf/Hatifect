#!/usr/bin/env python3
"""Deterministic semantic live-test agent for Hatifect.

The agent is intentionally outside Stardew/SMAPI. It consumes checked-in declarative
scenario specs plus request-owned detached evidence, then performs only ordinary
macOS Quartz input through the existing native backend. Specs cannot execute code,
invoke Hatifect automation APIs, or weaken acceptance assertions.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import sys
import time
import uuid
from pathlib import Path
from typing import Any, Callable

PROTOCOL_VERSION = 1
SPEC_VERSION = 1
MAX_SPEC_BYTES = 256 * 1024
MAX_STEPS = 256
MAX_STEP_SECONDS = 600.0
MAX_VARIABLES = 128
MAX_VARIABLE_NAME_LENGTH = 96
MAX_TEMPLATE_LENGTH = 4096
MAX_CONDITIONS = 64
INITIAL_VARIABLE_NAMES = frozenset({
    "requestId",
    "requestHex",
    "scenarioId",
})
CORE_VARIABLE_PRODUCERS = frozenset({"capture"})
ALLOWED_OPS = {
    "capture", "ensureGameActive", "wait", "assert", "move", "clickPoint", "key", "text",
    "click", "replaceText", "waitElement", "waitCapture", "waitUi",
}
TRANSFORMS = {
    "count",
    "string",
    "uuidHex",
    "uuidCanonical",
    "lower",
    "upper",
}
STEP_FIELDS = {
    "capture": ({"source", "variable"}, {"path", "transform"}),
    "ensureGameActive": (set(), set()),
    "wait": ({"conditions"}, set()),
    "assert": ({"conditions"}, set()),
    "move": ({"source", "path"}, set()),
    "clickPoint": ({"source", "path"}, set()),
    "key": ({"key"}, set()),
    "text": ({"value"}, set()),
    "click": ({"selector"}, {"requireEnabled"}),
    "replaceText": ({"selector", "value"}, set()),
    "waitElement": ({"selector"}, {"conditions", "visible"}),
    "waitCapture": (
        {"selector"},
        {"afterCount", "conditions", "path", "visible"},
    ),
    "waitUi": (set(), {"experience", "visible"}),
}
SELECTOR_FIELDS = {"semantic", "semanticPrefix", "action", "name", "role"}
KEYS = {
    "K": 40,
    "Tab": 48,
    "Backspace": 51,
    "Escape": 53,
}
_TEMPLATE = re.compile(r"\$\{([A-Za-z][A-Za-z0-9_]*)\}")
_VARIABLE_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]*$")
_SEGMENT = re.compile(r"^([A-Za-z0-9_-]+)((?:\[-?\d+\])*)$")


class SemanticAgentError(RuntimeError):
    pass


class SemanticEvidencePending(SemanticAgentError):
    """A request-owned observation is valid but not ready for the next semantic action."""
    pass


class SemanticElementPending(SemanticEvidencePending):
    pass


def _load_native():
    path = Path(__file__).with_name("macos_native_input_driver.py")
    spec = importlib.util.spec_from_file_location("hatifect_semantic_native_backend", path)
    if spec is None or spec.loader is None:
        raise SemanticAgentError("The checked-in macOS native input backend is unavailable.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


NATIVE = _load_native()


def _read_bounded_json(path: Path, limit: int) -> dict[str, Any]:
    info = os.lstat(path)
    if not os.path.isfile(path) or os.path.islink(path) or info.st_nlink != 1:
        raise SemanticAgentError(f"Semantic evidence/spec must be one regular file: {path}")
    if not 0 < info.st_size <= limit:
        raise SemanticAgentError(f"Semantic evidence/spec exceeds its byte budget: {path}")
    with path.open("r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise SemanticAgentError(f"Semantic evidence/spec root must be an object: {path}")
    return value


def _atomic_json(path: Path, payload: dict[str, Any]) -> None:
    NATIVE._atomic_json(path, payload)


def _field(value: Any, name: str, default: Any = None) -> Any:
    return NATIVE._field(value, name, default)


def _template(value: Any, variables: dict[str, Any]) -> Any:
    if not isinstance(value, str):
        return value
    if len(value) > MAX_TEMPLATE_LENGTH:
        raise SemanticAgentError("Semantic test template exceeds its length budget.")
    exact = _TEMPLATE.fullmatch(value)
    if exact:
        name = exact.group(1)
        if name not in variables:
            raise SemanticAgentError(f"Unknown semantic test variable: {name}")
        return variables[name]

    def replace(match: re.Match[str]) -> str:
        name = match.group(1)
        if name not in variables:
            raise SemanticAgentError(f"Unknown semantic test variable: {name}")
        return str(variables[name])

    return _TEMPLATE.sub(replace, value)


def _path(value: Any, path: str) -> Any:
    if path == "":
        return value
    current = value
    for raw in path.split("."):
        match = _SEGMENT.fullmatch(raw)
        if match is None:
            raise SemanticAgentError(f"Invalid semantic evidence path segment: {raw!r}")
        current = _field(current, match.group(1))
        suffix = match.group(2)
        if suffix:
            for current_index in (int(item) for item in re.findall(r"\[(-?\d+)\]", suffix)):
                if not isinstance(current, list) or not current:
                    return None
                if current_index < -len(current) or current_index >= len(current):
                    return None
                current = current[current_index]
        if current is None:
            return None
    return current


def _resolve(value: Any, variables: dict[str, Any]) -> Any:
    if isinstance(value, str):
        return _template(value, variables)
    if isinstance(value, list):
        return [_resolve(item, variables) for item in value]
    if isinstance(value, dict):
        return {key: _resolve(item, variables) for key, item in value.items()}
    return value


def _validate_variable_name(value: Any, operation: str) -> str:
    if (
        not isinstance(value, str)
        or len(value) > MAX_VARIABLE_NAME_LENGTH
        or _VARIABLE_NAME.fullmatch(value) is None
    ):
        raise SemanticAgentError(
            f"Semantic {operation} variable is invalid."
        )
    return value


def _ensure_variable_capacity(
    variables: dict[str, Any] | set[str],
    name: str,
) -> None:
    if name not in variables and len(variables) >= MAX_VARIABLES:
        raise SemanticAgentError("Semantic test variable budget exhausted.")


def _validate_template_references(
    value: Any,
    variables: set[str],
) -> None:
    if isinstance(value, str):
        for match in _TEMPLATE.finditer(value):
            name = match.group(1)
            if name not in variables:
                raise SemanticAgentError(
                    f"Unknown semantic test variable: {name}"
                )
        return
    if isinstance(value, list):
        for item in value:
            _validate_template_references(item, variables)
        return
    if isinstance(value, dict):
        for item in value.values():
            _validate_template_references(item, variables)


def _initial_variable_environment(
    document: dict[str, Any],
) -> set[str]:
    variables = set(INITIAL_VARIABLE_NAMES)
    for declaration in document["evidence"].values():
        _validate_template_references(
            declaration["identity"],
            variables,
        )
    return variables


def _validate_step_variable_environment(
    step: dict[str, Any],
    variables: set[str],
    producer_operations: frozenset[str],
) -> None:
    _validate_template_references(step, variables)
    if step["op"] not in producer_operations:
        return
    name = _validate_variable_name(step["variable"], step["op"])
    _ensure_variable_capacity(variables, name)
    variables.add(name)


def _transform(value: Any, transform: str | list[str] | None) -> Any:
    transforms = [] if transform is None else ([transform] if isinstance(transform, str) else transform)
    result = value
    for item in transforms:
        if item == "count":
            if not isinstance(result, (list, dict, str)):
                raise SemanticAgentError("count transform requires a list, object, or string.")
            result = len(result)
        elif item == "string":
            result = str(result)
        elif item == "uuidHex":
            result = uuid.UUID(str(result)).hex
        elif item == "uuidCanonical":
            result = str(uuid.UUID(str(result)))
        elif item == "lower":
            result = str(result).lower()
        elif item == "upper":
            result = str(result).upper()
        else:
            raise SemanticAgentError(f"Unsupported semantic test transform: {item}")
    return result


def _safe_evidence_path(relative: str) -> Path:
    path = Path(relative)
    if path.is_absolute() or ".." in path.parts or not path.parts or path.parts[0] != "diagnostics":
        raise SemanticAgentError("Semantic evidence paths must stay below diagnostics/.")
    return path


def _bounded_string(value: Any, *, allow_empty: bool = False) -> bool:
    return (
        isinstance(value, str)
        and (allow_empty or bool(value))
        and len(value) <= MAX_TEMPLATE_LENGTH
    )


def _validate_path_expression(value: Any) -> None:
    if not _bounded_string(value, allow_empty=True):
        raise SemanticAgentError(
            "Semantic test path must be a bounded string."
        )
    if value and any(_SEGMENT.fullmatch(segment) is None for segment in value.split(".")):
        raise SemanticAgentError("Semantic test path has an invalid segment.")


def _validate_selector(selector: Any) -> None:
    if (
        not isinstance(selector, dict)
        or not selector
        or not set(selector) <= SELECTOR_FIELDS
        or any(not _bounded_string(value) for value in selector.values())
    ):
        raise SemanticAgentError("Semantic test selector is invalid.")


def _validate_source(value: Any) -> None:
    if not isinstance(value, str) or value not in {"domain", "ui", "latest"}:
        raise SemanticAgentError("Semantic test source is invalid.")


def _validate_transform(value: Any) -> None:
    transforms = value if isinstance(value, list) else [value]
    if (
        not transforms
        or len(transforms) > 8
        or any(
            not isinstance(transform, str) or transform not in TRANSFORMS
            for transform in transforms
        )
    ):
        raise SemanticAgentError("Semantic test transform is invalid.")


def _validate_count_operand(value: Any) -> None:
    if isinstance(value, bool):
        raise SemanticAgentError("Semantic count predicate is invalid.")
    if isinstance(value, int) and value >= 0:
        return
    if isinstance(value, str) and _TEMPLATE.fullmatch(value):
        return
    raise SemanticAgentError("Semantic count predicate is invalid.")


def _validate_condition(
    condition: Any,
    *,
    source_required: bool = True,
) -> None:
    if not isinstance(condition, dict):
        raise SemanticAgentError("Semantic test conditions must be objects.")
    if source_required:
        _validate_source(condition.get("source"))
    elif "source" in condition:
        raise SemanticAgentError(
            "Semantic element conditions must not declare an evidence source."
        )
    _validate_path_expression(condition.get("path", ""))
    predicates = {
        "equals", "notEquals", "truthy", "falsy", "nonNull", "startsWith", "contains",
        "containsAny", "regex", "countEquals", "countGreaterThan", "countAtLeast", "uuidZero",
        "containsAll",
    }
    present = predicates.intersection(condition)
    expected_fields = set(present)
    if source_required:
        expected_fields.add("source")
    if "path" in condition:
        expected_fields.add("path")
    if (
        len(present) != 1
        or set(condition) != expected_fields
    ):
        raise SemanticAgentError("Each semantic condition must declare exactly one predicate.")
    predicate = next(iter(present))
    operand = condition[predicate]
    if predicate in {"truthy", "falsy", "nonNull", "uuidZero"}:
        if not isinstance(operand, bool):
            raise SemanticAgentError(
                f"Semantic {predicate} predicate must be boolean."
            )
    elif predicate in {"startsWith", "contains", "regex"}:
        if not _bounded_string(operand):
            raise SemanticAgentError(
                f"Semantic {predicate} predicate must be a bounded string."
            )
        if predicate == "regex" and _TEMPLATE.fullmatch(operand) is None:
            try:
                re.compile(operand)
            except re.error as error:
                raise SemanticAgentError(
                    "Semantic regex predicate is invalid."
                ) from error
    elif predicate == "containsAny":
        if (
            not isinstance(operand, list)
            or not 1 <= len(operand) <= 32
            or any(not _bounded_string(item) for item in operand)
        ):
            raise SemanticAgentError(
                "Semantic containsAny predicate is invalid."
            )
    elif predicate in {"countEquals", "countGreaterThan", "countAtLeast"}:
        _validate_count_operand(operand)
    elif predicate == "containsAll":
        if not isinstance(operand, dict) or set(operand) != {"path", "values"}:
            raise SemanticAgentError(
                "Semantic containsAll predicate is invalid."
            )
        _validate_path_expression(operand["path"])
        values = operand["values"]
        if (
            not isinstance(values, list)
            or not 1 <= len(values) <= 64
            or any(
                isinstance(item, (dict, list))
                or (isinstance(item, str) and len(item) > MAX_TEMPLATE_LENGTH)
                for item in values
            )
        ):
            raise SemanticAgentError(
                "Semantic containsAll predicate is invalid."
            )
    elif isinstance(operand, (dict, list)) or (
        isinstance(operand, str) and len(operand) > MAX_TEMPLATE_LENGTH
    ):
        raise SemanticAgentError(
            f"Semantic {predicate} predicate is invalid."
        )


def _validate_conditions(
    value: Any,
    *,
    required: bool,
    source_required: bool,
) -> None:
    if (
        not isinstance(value, list)
        or len(value) > MAX_CONDITIONS
        or (required and not value)
    ):
        raise SemanticAgentError("Semantic test conditions are invalid.")
    for condition in value:
        _validate_condition(condition, source_required=source_required)


def _validate_step(step: Any) -> None:
    if not isinstance(step, dict):
        raise SemanticAgentError("Semantic test steps must be objects.")
    operation = step.get("op")
    if not isinstance(operation, str) or operation not in ALLOWED_OPS:
        raise SemanticAgentError(
            f"Unsupported semantic test operation: {operation!r}"
        )
    required, optional = STEP_FIELDS[operation]
    common = {"id", "op", "timeout"}
    if not required <= set(step) or not set(step) <= common | required | optional:
        raise SemanticAgentError(
            f"Semantic {operation} step has an invalid field set."
        )
    step_id = step.get("id")
    if not isinstance(step_id, str) or not step_id or len(step_id) > 96:
        raise SemanticAgentError(
            "Semantic test step IDs must be bounded strings."
        )
    timeout = step.get("timeout", 45)
    if (
        not isinstance(timeout, (int, float))
        or isinstance(timeout, bool)
        or not 0 < timeout <= MAX_STEP_SECONDS
    ):
        raise SemanticAgentError(
            f"Semantic test step {step_id} has an invalid timeout."
        )
    if "source" in step:
        _validate_source(step["source"])
    if "path" in step:
        _validate_path_expression(step["path"])
    if "selector" in step:
        _validate_selector(step["selector"])
    if "conditions" in step:
        _validate_conditions(
            step["conditions"],
            required=operation in {"wait", "assert"},
            source_required=operation in {"wait", "assert"},
        )
    if "visible" in step and not isinstance(step["visible"], bool):
        raise SemanticAgentError("Semantic visibility must be boolean.")
    if "requireEnabled" in step and not isinstance(
        step["requireEnabled"], bool
    ):
        raise SemanticAgentError("Semantic requireEnabled must be boolean.")
    if operation == "capture":
        _validate_variable_name(step["variable"], operation)
        if "transform" in step:
            _validate_transform(step["transform"])
    elif operation == "key" and (
        not isinstance(step["key"], str) or step["key"] not in KEYS
    ):
        raise SemanticAgentError(
            f"Unsupported semantic key: {step['key']!r}"
        )
    elif operation in {"text", "replaceText"} and not _bounded_string(
        step["value"], allow_empty=True
    ):
        raise SemanticAgentError(
            f"Semantic {operation} value must be a bounded string."
        )
    elif operation == "waitCapture":
        if "afterCount" in step:
            _validate_count_operand(step["afterCount"])
    elif operation == "waitUi":
        if "experience" in step and not _bounded_string(step["experience"]):
            raise SemanticAgentError(
                "Semantic waitUi experience must be a bounded string."
            )


def _validate_spec(
    document: dict[str, Any],
    scenario_id: str,
    producer_operations: frozenset[str] | None,
) -> dict[str, Any]:
    if not isinstance(document, dict):
        raise SemanticAgentError("Semantic test spec root must be an object.")
    if set(document) != {"schemaVersion", "id", "platform", "evidence", "gameActive", "steps"}:
        raise SemanticAgentError("Semantic test spec has an invalid top-level field set.")
    if (
        type(document["schemaVersion"]) is not int
        or document["schemaVersion"] != SPEC_VERSION
        or not isinstance(document["id"], str)
        or document["id"] != scenario_id
    ):
        raise SemanticAgentError("Semantic test spec identity/version mismatch.")
    if document["platform"] != "macos-quartz":
        raise SemanticAgentError("Semantic test spec requires an unsupported execution platform.")
    evidence = document["evidence"]
    if not isinstance(evidence, dict) or set(evidence) != {"domain", "ui"}:
        raise SemanticAgentError("Semantic test spec must declare domain and ui evidence.")
    for name, declaration in evidence.items():
        if not isinstance(declaration, dict) or set(declaration) != {"path", "identity"}:
            raise SemanticAgentError(f"Semantic {name} evidence declaration is invalid.")
        if not _bounded_string(declaration["path"]):
            raise SemanticAgentError(
                f"Semantic {name} evidence path is invalid."
            )
        _safe_evidence_path(declaration["path"])
        identity = declaration["identity"]
        if not isinstance(identity, dict) or not identity or len(identity) > 8:
            raise SemanticAgentError(f"Semantic {name} evidence identity is invalid.")
        if any(
            not _bounded_string(key)
            or len(key) > 96
            or not _bounded_string(value)
            for key, value in identity.items()
        ):
            raise SemanticAgentError(f"Semantic {name} evidence identity must use string fields/templates.")
    game = document["gameActive"]
    if not isinstance(game, dict) or set(game) != {"source", "path", "activateAppContains"}:
        raise SemanticAgentError("Semantic test gameActive declaration is invalid.")
    _validate_source(game["source"])
    _validate_path_expression(game["path"])
    if not _bounded_string(game["activateAppContains"]):
        raise SemanticAgentError("Semantic test activation target must be non-empty.")
    steps = document["steps"]
    if not isinstance(steps, list) or not 1 <= len(steps) <= MAX_STEPS:
        raise SemanticAgentError("Semantic test spec has an invalid step count.")
    ids: set[str] = set()
    variables = (
        _initial_variable_environment(document)
        if producer_operations is not None
        else None
    )
    for step in steps:
        _validate_step(step)
        step_id = step["id"]
        if step_id in ids:
            raise SemanticAgentError("Semantic test step IDs must be unique bounded strings.")
        ids.add(step_id)
        if variables is not None:
            _validate_step_variable_environment(
                step,
                variables,
                producer_operations,
            )
    return document


def validate_spec(document: dict[str, Any], scenario_id: str) -> dict[str, Any]:
    return _validate_spec(
        document,
        scenario_id,
        CORE_VARIABLE_PRODUCERS,
    )


def load_spec(path: Path, scenario_id: str) -> tuple[dict[str, Any], str]:
    root = Path(__file__).resolve().parent / "semantic-tests"
    resolved = path.resolve(strict=True)
    if resolved.parent != root or resolved.name != f"{scenario_id}.json":
        raise SemanticAgentError("Semantic test spec must be the checked-in exact scenario file.")
    encoded = resolved.read_bytes()
    if not 0 < len(encoded) <= MAX_SPEC_BYTES:
        raise SemanticAgentError("Semantic test spec exceeds its byte budget.")
    document = json.loads(encoded)
    if not isinstance(document, dict):
        raise SemanticAgentError("Semantic test spec root must be an object.")
    return validate_spec(document, scenario_id), hashlib.sha256(encoded).hexdigest()


class SemanticController(NATIVE.Controller):
    def __init__(
        self,
        artifact: Path,
        request_id: str,
        scenario_id: str,
        timeout: float,
        backend: Any,
        spec: dict[str, Any],
        spec_sha256: str,
    ) -> None:
        self.artifact = artifact.resolve(strict=True)
        self.request_id = str(uuid.UUID(request_id))
        if self.artifact.name != self.request_id:
            raise SemanticAgentError("Semantic test artifact identity does not match the request UUID.")
        self.scenario_id = scenario_id
        self.deadline = time.monotonic() + timeout
        self.backend = backend
        self.spec = spec
        self.spec_sha256 = spec_sha256
        self.events: list[dict[str, Any]] = []
        self.offset_x = 0.0
        self.offset_y = 0.0
        self.variables: dict[str, Any] = {
            "requestId": self.request_id,
            "requestHex": uuid.UUID(self.request_id).hex,
            "scenarioId": self.scenario_id,
        }
        if set(self.variables) != INITIAL_VARIABLE_NAMES:
            raise SemanticAgentError(
                "Semantic initial variable environment is inconsistent."
            )
        evidence = spec["evidence"]
        self.flow_path = self.artifact / _safe_evidence_path(evidence["domain"]["path"])
        self.ui_path = self.artifact / _safe_evidence_path(evidence["ui"]["path"])
        self.status_path = self.artifact / "diagnostics" / "semantic-test-agent.json"
        self._status("Starting", "load-spec")

    def _status(self, state: str, action: str, failure: str | None = None) -> None:
        _atomic_json(self.status_path, {
            "protocolVersion": PROTOCOL_VERSION,
            "requestId": self.request_id,
            "scenarioId": self.scenario_id,
            "specId": self.spec["id"],
            "specSha256": self.spec_sha256,
            "state": state,
            "action": action,
            "origin": "checked-in semantic spec -> macOS Quartz CGEventPost",
            "capturedAtUtc": NATIVE._utc(),
            "failure": failure,
            "variables": {key: value for key, value in self.variables.items() if key not in {"requestId"}},
            "events": self.events[-NATIVE.MAX_EVENTS:],
        })

    def _record(self, action: str, **detail: Any) -> None:
        self.events.append({"atUtc": NATIVE._utc(), "action": action, **detail})
        if len(self.events) > NATIVE.MAX_EVENTS * 2:
            self.events = self.events[-NATIVE.MAX_EVENTS:]
        self._status("Running", action)

    def _wait(self, description: str, predicate: Callable[[], Any], *, seconds: float = 45.0) -> Any:
        end = min(self.deadline, time.monotonic() + seconds)
        last_pending: Exception | None = None
        while time.monotonic() < end:
            try:
                value = predicate()
                if value:
                    return value
            except (FileNotFoundError, json.JSONDecodeError, SemanticEvidencePending) as error:
                last_pending = error
            time.sleep(NATIVE.POLL_SECONDS)
        detail = f" ({last_pending})" if last_pending else ""
        raise SemanticAgentError(f"Timed out waiting for {description}{detail}.")

    def _document(self, source: str) -> dict[str, Any]:
        if source == "domain":
            declaration = self.spec["evidence"]["domain"]
            path = self.flow_path
        elif source in {"ui", "latest"}:
            declaration = self.spec["evidence"]["ui"]
            path = self.ui_path
        else:
            raise SemanticAgentError(f"Unknown semantic evidence source: {source}")
        value = _read_bounded_json(path, NATIVE.MAX_JSON_BYTES)
        for key, expected in declaration["identity"].items():
            actual = _field(value, key)
            if actual != _template(expected, self.variables):
                raise SemanticAgentError(f"{source} evidence belongs to another request/scenario at {key}.")
        failure = _field(value, "failure")
        if source in {"ui", "latest"} and failure:
            raise SemanticAgentError("UI semantic observer failed: " + str(failure))
        if source == "latest":
            latest = _field(value, "latest")
            if not isinstance(latest, dict):
                raise SemanticEvidencePending("UI semantic evidence has no latest frame yet.")
            return latest
        return value

    def flow(self) -> dict[str, Any]:
        return self._document("domain")

    def ui(self) -> dict[str, Any]:
        return self._document("ui")

    def latest(self, *, visible: bool | None = None) -> dict[str, Any]:
        latest = self._document("latest")
        if visible is not None and bool(_field(latest, "visible", False)) != visible:
            raise SemanticEvidencePending("UI visibility has not reached the requested semantic state yet.")
        return latest

    def _game_active(self) -> bool:
        try:
            declaration = self.spec["gameActive"]
            value = _path(self._document(declaration["source"]), declaration["path"])
            return value is True
        except (FileNotFoundError, json.JSONDecodeError, SemanticEvidencePending):
            return False

    def ensure_game_active(self) -> None:
        declaration = self.spec["gameActive"]
        self._wait("game evidence to appear", lambda: self._document(declaration["source"]), seconds=60)
        if self._game_active():
            return
        app = declaration["activateAppContains"].replace('"', '\\"')
        script = (
            'tell application "System Events" to set frontmost of first application process '
            f'whose name contains "{app}" to true'
        )
        completed = NATIVE.subprocess.run(
            ["/usr/bin/osascript", "-e", script], capture_output=True, text=True, timeout=10, check=False
        )
        self._record("activate-game", returnCode=completed.returncode)
        if completed.returncode != 0:
            raise SemanticAgentError(
                "Could not activate the game in the owning GUI session: " + completed.stderr.strip()
            )
        self._wait("the game to become active", self._game_active, seconds=15)

    def value(self, source: str, path: str = "") -> Any:
        return _path(self._document(source), path)

    def _selector(self, raw: dict[str, Any]) -> dict[str, str]:
        resolved = _resolve(raw, self.variables)
        return {key: str(value) for key, value in resolved.items()}

    def find_element(
        self,
        *,
        semantic: str | None = None,
        semanticPrefix: str | None = None,
        action: str | None = None,
        name: str | None = None,
        role: str | None = None,
    ) -> dict[str, Any]:
        matches: list[dict[str, Any]] = []
        elements = _field(self.latest(visible=True), "elements")
        if not isinstance(elements, list):
            raise SemanticAgentError("Visible semantic frame has no element list.")
        for element in elements:
            if not isinstance(element, dict):
                continue
            element_semantic = _field(element, "semanticId")
            if semantic is not None and element_semantic != semantic:
                continue
            if semanticPrefix is not None and (
                not isinstance(element_semantic, str) or not element_semantic.startswith(semanticPrefix)
            ):
                continue
            if action is not None and _field(element, "actionId") != action:
                continue
            if name is not None and _field(element, "name") != name:
                continue
            if role is not None and _field(element, "role") != role:
                continue
            matches.append(element)
        if not matches:
            raise SemanticElementPending(
                "Semantic element is not present in the current frame for selector "
                f"{semantic=}, {semanticPrefix=}, {action=}, {name=}, {role=}."
            )
        if len(matches) != 1:
            raise SemanticAgentError(
                "Semantic selector is ambiguous: "
                f"{semantic=}, {semanticPrefix=}, {action=}, {name=}, {role=}; found {len(matches)}."
            )
        return matches[0]

    def ensure_visible(self, selector: dict[str, str]) -> dict[str, Any]:
        for _ in range(24):
            element = self.find_element(**selector)
            if self._fully_visible(element):
                return element
            _bx, by, _bw, _bh = self._rect(_field(element, "bounds"))
            cx, cy, cw, ch = self._rect(_field(element, "clip"))
            self.move_local(cx + cw / 2, cy + ch / 2)
            self.backend.scroll(5 if by < cy else -5)
            self._record("semantic-scroll", selector=selector, direction="up" if by < cy else "down")
            before = int(_field(self.latest(), "completedFrame", -1))
            self._wait(
                "fresh semantic frame after scroll",
                lambda: int(_field(self.latest(), "completedFrame", -1)) > before,
                seconds=4,
            )
        raise SemanticAgentError("Could not reveal semantic element within the scroll budget.")

    def click_selector(self, selector: dict[str, str], require_enabled: bool = True) -> None:
        element = self.ensure_visible(selector)
        if require_enabled and not bool(_field(element, "enabled", False)):
            raise SemanticAgentError(f"Semantic element is disabled: {selector}")
        bx, by, bw, bh = self._rect(_field(element, "bounds"))
        x, y = bx + bw / 2, by + bh / 2
        self.move_local(x, y)
        latest = self.latest(visible=True)
        screen_x, screen_y, _sx, _sy = self._local_to_screen(latest, x, y)
        self.backend.click(screen_x, screen_y)
        self._record(
            "semantic-click",
            semantic=_field(element, "semanticId"),
            actionId=_field(element, "actionId"),
            role=_field(element, "role"),
        )

    def _condition_value(self, condition: dict[str, Any]) -> Any:
        return self.value(condition["source"], condition.get("path", ""))

    @staticmethod
    def _predicate(value: Any, condition: dict[str, Any]) -> bool:
        if "equals" in condition:
            return value == condition["equals"]
        if "notEquals" in condition:
            return value != condition["notEquals"]
        if "truthy" in condition:
            return bool(value) is bool(condition["truthy"])
        if "falsy" in condition:
            return (not bool(value)) is bool(condition["falsy"])
        if "nonNull" in condition:
            return (value is not None) is bool(condition["nonNull"])
        if "startsWith" in condition:
            return isinstance(value, str) and value.startswith(str(condition["startsWith"]))
        if "contains" in condition:
            needle = condition["contains"]
            return needle in value if isinstance(value, (str, list, dict)) else False
        if "containsAny" in condition:
            needles = condition["containsAny"]
            return isinstance(value, str) and isinstance(needles, list) and any(str(item) in value for item in needles)
        if "regex" in condition:
            return isinstance(value, str) and re.search(str(condition["regex"]), value) is not None
        if "countEquals" in condition:
            return isinstance(value, (list, dict, str)) and len(value) == int(condition["countEquals"])
        if "countGreaterThan" in condition:
            return isinstance(value, (list, dict, str)) and len(value) > int(condition["countGreaterThan"])
        if "countAtLeast" in condition:
            return isinstance(value, (list, dict, str)) and len(value) >= int(condition["countAtLeast"])
        if "uuidZero" in condition:
            try:
                zero = uuid.UUID(str(value)).int == 0
            except (ValueError, AttributeError):
                return False
            return zero is bool(condition["uuidZero"])
        if "containsAll" in condition:
            declaration = condition["containsAll"]
            if not isinstance(value, list) or not isinstance(declaration, dict):
                return False
            item_path = declaration.get("path", "")
            expected = declaration.get("values")
            if not isinstance(expected, list):
                return False
            observed = {_path(item, item_path) for item in value}
            return set(expected).issubset(observed)
        raise SemanticAgentError("Semantic condition has no supported predicate.")

    def condition(self, raw: dict[str, Any]) -> bool:
        condition = _resolve(raw, self.variables)
        return self._predicate(self._condition_value(condition), condition)

    def _conditions(self, conditions: list[dict[str, Any]]) -> bool:
        return all(self.condition(condition) for condition in conditions)

    def wait_conditions(self, conditions: list[dict[str, Any]], timeout: float, description: str) -> None:
        self._wait(description, lambda: self._conditions(conditions), seconds=timeout)

    def wait_element(self, step: dict[str, Any]) -> None:
        selector = self._selector(step["selector"])
        conditions = _resolve(step.get("conditions", []), self.variables)

        def ready() -> bool:
            try:
                element = self.find_element(**selector)
            except SemanticElementPending:
                return False
            if step.get("visible", True) and not self._fully_visible(element):
                return False
            return all(
                self._predicate(_path(element, condition.get("path", "")), condition)
                for condition in conditions
            )

        self._wait(f"semantic element {selector}", ready, seconds=float(step.get("timeout", 45)))

    def wait_capture(self, step: dict[str, Any]) -> None:
        after = int(_template(step.get("afterCount", 0), self.variables))
        selector = self._selector(step["selector"])
        conditions = _resolve(step.get("conditions", []), self.variables)

        def captured() -> bool:
            captures = self.value("ui", step.get("path", "captures"))
            if not isinstance(captures, list):
                return False
            for capture in captures[after:]:
                state = _field(capture, "state")
                elements = _field(state, "elements")
                if not isinstance(elements, list):
                    continue
                for element in elements:
                    if not isinstance(element, dict):
                        continue
                    semantic = _field(element, "semanticId")
                    if "semantic" in selector and semantic != selector["semantic"]:
                        continue
                    if "semanticPrefix" in selector and (
                        not isinstance(semantic, str) or not semantic.startswith(selector["semanticPrefix"])
                    ):
                        continue
                    if "action" in selector and _field(element, "actionId") != selector["action"]:
                        continue
                    if "name" in selector and _field(element, "name") != selector["name"]:
                        continue
                    if "role" in selector and _field(element, "role") != selector["role"]:
                        continue
                    if step.get("visible", True) and not self._fully_visible(element):
                        continue
                    if all(
                        self._predicate(_path(element, condition.get("path", "")), condition)
                        for condition in conditions
                    ):
                        return True
            return False

        self._wait("fresh semantic capture", captured, seconds=float(step.get("timeout", 45)))

    def execute(self, step: dict[str, Any]) -> None:
        op = step["op"]
        resolved = _resolve(step, self.variables)
        timeout = float(step.get("timeout", 45))
        if op == "capture":
            variable = resolved["variable"]
            _ensure_variable_capacity(self.variables, variable)
            value = self.value(resolved["source"], resolved.get("path", ""))
            value = _transform(value, resolved.get("transform"))
            self.variables[variable] = value
            self._record("capture", variable=variable)
        elif op == "ensureGameActive":
            self.ensure_game_active()
            self._record("ensure-game-active")
        elif op == "wait":
            self.wait_conditions(resolved["conditions"], timeout, f"step {step['id']}")
            self._record("wait-satisfied", step=step["id"])
        elif op == "assert":
            if not self._conditions(resolved["conditions"]):
                raise SemanticAgentError(f"Semantic assertion failed at step {step['id']}.")
            self._record("assert-satisfied", step=step["id"])
        elif op == "move":
            point = self.value(resolved["source"], resolved["path"])
            if not isinstance(point, dict):
                raise SemanticAgentError(f"Semantic move point is missing at step {step['id']}.")
            self.move_local(float(_field(point, "x")), float(_field(point, "y")))
            self._record("move", step=step["id"])
        elif op == "clickPoint":
            point = self.value(resolved["source"], resolved["path"])
            if not isinstance(point, dict):
                raise SemanticAgentError(f"Semantic clickPoint target is missing at step {step['id']}.")
            self.click_local(float(_field(point, "x")), float(_field(point, "y")))
            self._record("click-point", step=step["id"])
        elif op == "key":
            key = resolved["key"]
            if key not in KEYS:
                raise SemanticAgentError(f"Unsupported semantic key: {key}")
            self.backend.key(KEYS[key])
            self._record("key", key=key, step=step["id"])
        elif op == "text":
            value = str(resolved["value"])
            self.backend.text(value)
            self._record("text", step=step["id"], length=len(value))
        elif op == "click":
            self.click_selector(self._selector(step["selector"]), bool(step.get("requireEnabled", True)))
        elif op == "replaceText":
            selector = self._selector(step["selector"])
            if "semantic" not in selector or len(selector) != 1:
                raise SemanticAgentError("replaceText currently requires one exact semantic field ID.")
            self.replace_field(selector["semantic"], str(resolved["value"]))
            self._record("replace-text", semantic=selector["semantic"])
        elif op == "waitElement":
            self.wait_element(step)
            self._record("wait-element-satisfied", step=step["id"])
        elif op == "waitCapture":
            self.wait_capture(step)
            self._record("wait-capture-satisfied", step=step["id"])
        elif op == "waitUi":
            conditions = [
                {"source": "latest", "path": "visible", "equals": bool(resolved.get("visible", True))}
            ]
            if "experience" in resolved:
                conditions.append({"source": "latest", "path": "experience", "equals": resolved["experience"]})
            self.wait_conditions(conditions, timeout, f"UI state for {step['id']}")
            self._record("wait-ui-satisfied", step=step["id"])
        else:
            raise SemanticAgentError(f"Unhandled semantic test operation: {op}")

    def run_spec(self) -> None:
        for index, step in enumerate(self.spec["steps"]):
            self._status("Running", f"{index + 1}/{len(self.spec['steps'])}:{step['id']}")
            self.execute(step)
        self._status("Completed", "semantic-test-complete")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--spec", required=True)
    parser.add_argument("--artifact-directory", required=True)
    parser.add_argument("--request-id", required=True)
    parser.add_argument("--scenario-id", required=True)
    parser.add_argument("--timeout-seconds", type=float, required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    artifact = Path(args.artifact_directory)
    controller: SemanticController | None = None
    try:
        spec, digest = load_spec(Path(args.spec), args.scenario_id)
        backend = NATIVE.QuartzInput()
        controller = SemanticController(
            artifact, args.request_id, args.scenario_id, args.timeout_seconds, backend, spec, digest
        )
        controller.run_spec()
        return 0
    except Exception as error:
        if isinstance(error, (KeyboardInterrupt, SystemExit)):
            raise
        if controller is not None:
            try:
                controller._status("Failed", "semantic-test-failed", f"{type(error).__name__}: {error}")
            except Exception:
                pass
        else:
            try:
                _atomic_json(artifact / "diagnostics" / "semantic-test-agent.json", {
                    "protocolVersion": PROTOCOL_VERSION,
                    "requestId": args.request_id,
                    "scenarioId": args.scenario_id,
                    "specId": args.scenario_id,
                    "specSha256": None,
                    "state": "Failed",
                    "action": "semantic-test-initialization",
                    "origin": "checked-in semantic spec -> macOS Quartz CGEventPost",
                    "capturedAtUtc": NATIVE._utc(),
                    "failure": f"{type(error).__name__}: {error}",
                    "variables": {},
                    "events": [],
                })
            except Exception:
                pass
        print(f"Hatifect semantic test agent: BLOCKED: {type(error).__name__}: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
