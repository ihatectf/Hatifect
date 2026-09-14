"""Shared semantic-agent composition. The CLI and imported consumers use this engine."""
from __future__ import annotations

import copy
import importlib.util
import os
import stat
from pathlib import Path
from typing import Any


def _module(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Missing checked-in semantic module: {filename}")
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


CORE = _module("hatifect_semantic_core", "semantic_test_agent.py")
UI = _module("hatifect_semantic_interactions", "semantic_interactions.py")
POINTER = _module("hatifect_native_button_lease", "native_button_lease.py")
for _name in dir(CORE):
    if not _name.startswith("__"):
        globals()[_name] = getattr(CORE, _name)

EXTRA_OPS = {"focus", "fill", "activate", "select", "reveal", "discover", "fillForm"}
VARIABLE_PRODUCER_OPS = frozenset({"capture", "discover"})
EXTRA_STEP_FIELDS = {
    "focus": ({"selector"}, set()),
    "fill": ({"selector", "value"}, set()),
    "activate": ({"selector"}, set()),
    "select": ({"selector"}, {"collection"}),
    "reveal": ({"selector"}, set()),
    "discover": ({"variable"}, set()),
    "fillForm": ({"fields"}, set()),
}


def _validate_extra_step(step: dict[str, Any]) -> None:
    operation = step["op"]
    required, optional = EXTRA_STEP_FIELDS[operation]
    common = {"id", "op", "timeout"}
    if not required <= set(step) or not set(step) <= common | required | optional:
        raise SemanticAgentError(
            f"Semantic {operation} step has an invalid field set."
        )
    if "selector" in step:
        try:
            UI.validate_selector(step["selector"])
        except UI.InteractionError as error:
            raise SemanticAgentError(str(error)) from error
    if operation == "fill" and not CORE._bounded_string(
        step["value"], allow_empty=True
    ):
        raise SemanticAgentError(
            "Semantic field replacement requires a bounded string/template."
        )
    if operation == "select" and "collection" in step and not CORE._bounded_string(
        step["collection"]
    ):
        raise SemanticAgentError(
            "select collection must be an exact semantic ID/template."
        )
    if operation == "fillForm":
        fields = step["fields"]
        if (
            not isinstance(fields, dict)
            or not 1 <= len(fields) <= 32
            or any(
                not CORE._bounded_string(key)
                or not CORE._bounded_string(value, allow_empty=True)
                for key, value in fields.items()
            )
        ):
            raise SemanticAgentError(
                "fillForm requires 1-32 exact semantic field IDs and bounded string/template values."
            )
    if operation == "discover":
        CORE._validate_variable_name(step["variable"], operation)


def validate_spec(document: dict[str, Any], scenario_id: str) -> dict[str, Any]:
    # Reuse the v1 identity/path/budget validator; new operations are validated here,
    # not enabled by mutating the base module's operation registry or controller class.
    translated = copy.deepcopy(document)
    for index, step in enumerate(translated.get("steps", [])):
        if not isinstance(step, dict):
            continue
        op = step.get("op")
        if isinstance(op, str) and op in EXTRA_OPS:
            normalized = {
                key: value
                for key, value in step.items()
                if key in {"id", "timeout"}
            }
            normalized["op"] = "click"
            if op in {"discover", "fillForm"}:
                normalized["selector"] = {
                    "semantic": "validated-by-ui-contract"
                }
            else:
                normalized["selector"] = step.get("selector")
            translated["steps"][index] = normalized
            step = normalized
        selector = step.get("selector")
        if isinstance(selector, dict):
            step["selector"] = {k: v for k, v in selector.items() if k not in {"collection", "node"}}
            if not step["selector"]:
                step["selector"] = {"semantic": "validated-by-ui-contract"}
    CORE._validate_spec(
        translated,
        scenario_id,
        None,
    )
    variables = CORE._initial_variable_environment(document)
    for step in document["steps"]:
        op = step["op"]
        if op in EXTRA_OPS:
            _validate_extra_step(step)
        if op in {"focus", "fill", "activate", "select", "reveal", "click", "replaceText", "waitElement", "waitCapture"}:
            try:
                UI.validate_selector(step.get("selector"))
            except UI.InteractionError as error:
                raise SemanticAgentError(str(error)) from error
        if op in {"fill", "replaceText"}:
            selector = step["selector"]
            if "semantic" not in selector or not set(selector) <= {"semantic", "role"}:
                raise SemanticAgentError("replaceText/fill requires an exact semantic field ID with optional role.")
        CORE._validate_step_variable_environment(
            step,
            variables,
            VARIABLE_PRODUCER_OPS,
        )
    return document


def _reject_json_constant(value: str) -> None:
    raise SemanticAgentError(
        f"Semantic spec contains invalid JSON constant: {value}."
    )


def _unique_json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise SemanticAgentError(
                f"Semantic spec contains duplicate JSON field: {key}."
            )
        value[key] = item
    return value


def load_spec(path: Path, scenario_id: str):
    root = (Path(__file__).resolve().parent / "semantic-tests").resolve(strict=True)
    path_info = path.lstat()
    if (
        stat.S_ISLNK(path_info.st_mode)
        or not stat.S_ISREG(path_info.st_mode)
        or path_info.st_nlink != 1
    ):
        raise SemanticAgentError(
            "Semantic spec must be one checked-in regular file."
        )
    resolved = path.resolve(strict=True)
    if resolved.parent != root or resolved.name != f"{scenario_id}.json":
        raise SemanticAgentError("Semantic spec must be the checked-in exact scenario file.")
    if not 0 < path_info.st_size <= MAX_SPEC_BYTES:
        raise SemanticAgentError("Semantic spec exceeds its byte budget.")
    descriptor = os.open(
        resolved,
        os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0),
    )
    with os.fdopen(descriptor, "rb") as stream:
        opened_info = os.fstat(stream.fileno())
        if (
            not stat.S_ISREG(opened_info.st_mode)
            or opened_info.st_nlink != 1
            or (opened_info.st_dev, opened_info.st_ino)
            != (path_info.st_dev, path_info.st_ino)
        ):
            raise SemanticAgentError(
                "Semantic spec changed during bounded validation."
            )
        encoded = stream.read(MAX_SPEC_BYTES + 1)
    if len(encoded) != path_info.st_size:
        raise SemanticAgentError("Semantic spec changed during bounded validation.")
    document = json.loads(
        encoded,
        object_pairs_hook=_unique_json_object,
        parse_constant=_reject_json_constant,
    )
    if not isinstance(document, dict):
        raise SemanticAgentError("Semantic test spec root must be an object.")
    return validate_spec(document, scenario_id), hashlib.sha256(encoded).hexdigest()


class SemanticController(CORE.SemanticController):
    @property
    def controls(self):
        if not hasattr(self, "_controls"):
            self._controls = UI.SemanticInteractions(self)
        return self._controls

    def find_element(self, **selector):
        selector = {key: value for key, value in selector.items() if value is not None}
        try:
            return UI.resolve(self.latest(visible=True), selector)
        except UI.TargetPending as error:
            raise SemanticElementPending(str(error)) from error
        except UI.InteractionError as error:
            raise SemanticAgentError(str(error)) from error

    def ensure_visible(self, selector):
        return self.controls.reveal(selector, self._intent(selector))[1]

    @staticmethod
    def _intent(selector, conditions=()):
        if "action" in selector:
            return "activate"
        if selector.get("role") in {"TextField", "Button", "ListItem", "List"}:
            return {"TextField": "fill", "Button": "activate", "ListItem": "select", "List": "collection"}[selector["role"]]
        if any(condition.get("path") == "value" for condition in conditions):
            return "value"
        return "inspect"

    def click_selector(self, selector, require_enabled=True):
        if not require_enabled:
            # Historical result-reveal steps must reveal text, not send meaningless clicks.
            self.controls.reveal(selector, "value")
        else:
            self.controls.click(selector)

    def replace_selector(self, selector, value):
        self.controls.fill(selector, value)

    def replace_field(self, semantic, value):
        self.controls.fill({"semantic": semantic}, value)

    def wait_element(self, step):
        selector = self._selector(step["selector"])
        conditions = _resolve(step.get("conditions", []), self.variables)
        intent = self._intent(selector, conditions)
        def ready():
            f = self.controls.frame()
            n = UI.resolve(f, selector, intent)
            if step.get("visible", True) and not UI.visible(n):
                _, n = self.controls.reveal(selector, intent)
            return all(self._predicate(_path(n, c.get("path", "")), c) for c in conditions)
        self.controls.wait(f"semantic element {selector}", ready, float(step.get("timeout", 45)))

    def wait_capture(self, step):
        after = int(_template(step.get("afterCount", 0), self.variables))
        selector = self._selector(step["selector"])
        conditions = _resolve(step.get("conditions", []), self.variables)
        intent = self._intent(selector, conditions)
        def captured():
            captures = self.value("ui", step.get("path", "captures"))
            if not isinstance(captures, list):
                return False
            for capture in captures[after:]:
                state = _field(capture, "state")
                if not isinstance(state, dict) or _field(state, "visible") is not True:
                    continue
                try:
                    n = UI.resolve(state, selector, intent)
                except UI.TargetPending:
                    continue
                if (not step.get("visible", True) or UI.visible(n)) and all(
                    self._predicate(_path(n, c.get("path", "")), c) for c in conditions):
                    return True
            return False
        self.controls.wait("fresh semantic capture", captured, float(step.get("timeout", 45)))

    def execute(self, step):
        try:
            op = step["op"]
            resolved = _resolve(step, self.variables)
            selector = self._selector(step["selector"]) if "selector" in step else None
            if op in {"fill", "replaceText"}:
                self.replace_selector(selector, resolved["value"])
            elif op == "focus":
                self.controls.focus(selector)
            elif op == "activate":
                self.controls.click(selector, "activate")
            elif op == "select":
                if "collection" in resolved:
                    selector = {**selector, "collection": resolved["collection"]}
                self.controls.select(selector)
            elif op == "reveal":
                self.controls.reveal(selector, self._intent(selector, [{"path": "value"}]))
            elif op == "fillForm":
                # Resolve every supplied field before the first edit. Values are test inputs,
                # never guessed from labels; unrelated fields are never changed.
                for semantic, value in resolved["fields"].items():
                    _, n = self.controls.ready({"semantic": semantic}, "fill")
                    if _field(n, "enabled") is not True or len(value) > UI.MAX_TEXT:
                        raise SemanticAgentError("fillForm preflight rejected a field or value.")
                for semantic, value in resolved["fields"].items():
                    self.controls.fill({"semantic": semantic}, value)
            elif op == "discover":
                variable = resolved["variable"]
                _ensure_variable_capacity(self.variables, variable)
                self.variables[variable] = self.controls.discover()
            else:
                if op in {"key", "text"}:
                    self._wait("active game before native keyboard input", self._game_active, seconds=15)
                if op == "text":
                    f = self.controls.frame()
                    fields = [n for n in UI.nodes(f) if _field(n, "role") == "TextField" and _field(n, "focused") is True]
                    if len(fields) != 1 or _field(fields[0], "enabled") is not True:
                        raise SemanticAgentError("Native text requires one enabled focused semantic field.")
                return super().execute(step)
            self._record("semantic-operation-completed", operation=op, step=step["id"])
        except UI.TargetPending as error:
            raise SemanticElementPending(str(error)) from error
        except UI.InteractionError as error:
            raise SemanticAgentError(str(error)) from error

    def _status(self, state, action, failure=None):
        _atomic_json(self.status_path, {
            "protocolVersion": PROTOCOL_VERSION, "requestId": self.request_id,
            "scenarioId": self.scenario_id, "specId": self.spec["id"], "specSha256": self.spec_sha256,
            "state": state, "action": action, "currentStep": getattr(self, "current_step", None),
            "failure": failure, "capturedAtUtc": NATIVE._utc(),
            "origin": "semantic model -> native Quartz input -> observed postcondition",
            "lastTarget": getattr(getattr(self, "_controls", None), "last_target", None),
            "variables": {k: v for k, v in self.variables.items() if k != "requestId"},
            "events": self.events[-NATIVE.MAX_EVENTS:],
        })

    def run_spec(self):
        global_deadline = self.deadline
        for index, step in enumerate(self.spec["steps"]):
            self.current_step = {"id": step["id"], "op": step["op"], "index": index + 1}
            self.deadline = min(global_deadline, time.monotonic() + float(step.get("timeout", 45)))
            self._status("Running", step["id"])
            try:
                self.execute(step)
            finally:
                self.deadline = global_deadline
        self._status("Completed", "semantic-test-complete")


class SemanticQuartzInput(NATIVE.QuartzInput):
    """The same authorized Quartz transport, with balanced native key/mouse pulses."""
    def _pulse(self, down, up, hold):
        if not down or not up:
            if down:
                self._cf.CFRelease(down)
            if up:
                self._cf.CFRelease(up)
            raise NATIVE.DriverError("Quartz could not allocate both halves of an input pulse.")
        try:
            self._post(down)
            time.sleep(hold)
        finally:
            self._post(up)
        time.sleep(NATIVE.EVENT_GAP_SECONDS)

    def key(self, keycode, *, flags=0):
        down = self._app.CGEventCreateKeyboardEvent(None, keycode, True)
        up = self._app.CGEventCreateKeyboardEvent(None, keycode, False)
        if down:
            self._app.CGEventSetFlags(down, flags)
        if up:
            self._app.CGEventSetFlags(up, flags)
        self._pulse(down, up, NATIVE.KEY_HOLD_SECONDS)

    def hold_left_button(self, x, y):
        return POINTER.hold_left_button(self, NATIVE.CGPoint, x, y)

    def click(self, x, y):
        raise NATIVE.DriverError("Semantic clicks require an observed left-button lease.")


def main():
    args = CORE.build_parser().parse_args()
    controller = None
    artifact = Path(args.artifact_directory)
    try:
        spec, digest = load_spec(Path(args.spec), args.scenario_id)
        backend = SemanticQuartzInput()
        controller = SemanticController(artifact, args.request_id, args.scenario_id, args.timeout_seconds, backend, spec, digest)
        controller.run_spec()
        return 0
    except Exception as error:
        try:
            if controller is not None:
                controller._status("Failed", "semantic-test-failed", f"{type(error).__name__}: {error}")
            else:
                _atomic_json(artifact / "diagnostics/semantic-test-agent.json", {
                    "protocolVersion": PROTOCOL_VERSION, "requestId": args.request_id,
                    "scenarioId": args.scenario_id, "state": "Failed", "currentStep": None,
                    "action": "semantic-test-initialization", "failure": f"{type(error).__name__}: {error}", "events": []})
        except OSError:
            pass
        print(f"Hatifect semantic test agent: BLOCKED: {type(error).__name__}: {error}", file=sys.stderr)
        return 2
