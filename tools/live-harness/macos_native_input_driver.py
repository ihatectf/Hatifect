#!/usr/bin/env python3
"""State-driven macOS Quartz input controller for flow.ui.player.input.

This process is intentionally outside Stardew/SMAPI. It reads request-owned detached
acceptance evidence and posts ordinary macOS keyboard/mouse events. It never imports
Hatifect runtime code or invokes semantic action automation.
"""
from __future__ import annotations

import argparse
import ctypes
import datetime as dt
import json
import os
import platform
import stat
import subprocess
import tempfile
import time
import uuid
from pathlib import Path
from typing import Any, Callable

PROTOCOL_VERSION = 1
SCENARIO = "flow.ui.player.input"
MAX_JSON_BYTES = 32 * 1024 * 1024
POLL_SECONDS = 0.05
EVENT_GAP_SECONDS = 0.015
MAX_EVENTS = 192

K_KEY = 40
TAB_KEY = 48
BACKSPACE_KEY = 51
ESCAPE_KEY = 53


class DriverError(RuntimeError):
    pass


def _utc() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def _field(value: dict[str, Any], name: str, default: Any = None) -> Any:
    if name in value:
        return value[name]
    pascal = name[:1].upper() + name[1:]
    return value.get(pascal, default)


def _read_json(path: Path) -> dict[str, Any]:
    info = os.lstat(path)
    if not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_nlink != 1:
        raise DriverError(f"Driver evidence must be one regular request-owned file: {path}")
    if not 0 < info.st_size <= MAX_JSON_BYTES:
        raise DriverError(f"Driver evidence exceeds its bounded JSON budget: {path}")
    with path.open("r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise DriverError(f"Driver evidence root must be an object: {path}")
    return value


def _atomic_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        os.fchmod(descriptor, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


class CGPoint(ctypes.Structure):
    _fields_ = [("x", ctypes.c_double), ("y", ctypes.c_double)]


class QuartzInput:
    def __init__(self) -> None:
        if platform.system() != "Darwin":
            raise DriverError("The native player-input controller requires macOS.")
        self._app = ctypes.CDLL(
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices"
        )
        self._cf = ctypes.CDLL(
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"
        )
        self._configure()
        if hasattr(self._app, "CGPreflightPostEventAccess"):
            self._app.CGPreflightPostEventAccess.restype = ctypes.c_bool
            self._app.CGRequestPostEventAccess.restype = ctypes.c_bool
            if not self._app.CGPreflightPostEventAccess():
                self._app.CGRequestPostEventAccess()
                raise DriverError(
                    "macOS has not granted post-event Accessibility permission to this Python runtime. "
                    "Grant the requested permission once, then rerun the scenario."
                )

    def _configure(self) -> None:
        self._app.CGEventCreateKeyboardEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint16, ctypes.c_bool]
        self._app.CGEventCreateKeyboardEvent.restype = ctypes.c_void_p
        self._app.CGEventKeyboardSetUnicodeString.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_uint16)
        ]
        self._app.CGEventSetFlags.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
        self._app.CGEventPost.argtypes = [ctypes.c_uint32, ctypes.c_void_p]
        self._app.CGEventCreateMouseEvent.argtypes = [
            ctypes.c_void_p, ctypes.c_uint32, CGPoint, ctypes.c_uint32
        ]
        self._app.CGEventCreateMouseEvent.restype = ctypes.c_void_p
        self._app.CGEventCreateScrollWheelEvent.argtypes = [
            ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_int32
        ]
        self._app.CGEventCreateScrollWheelEvent.restype = ctypes.c_void_p
        self._cf.CFRelease.argtypes = [ctypes.c_void_p]

    def _post(self, event: int) -> None:
        if not event:
            raise DriverError("Quartz refused to create an input event.")
        try:
            self._app.CGEventPost(0, event)  # kCGHIDEventTap
        finally:
            self._cf.CFRelease(event)

    def key(self, keycode: int, *, flags: int = 0) -> None:
        for down in (True, False):
            event = self._app.CGEventCreateKeyboardEvent(None, keycode, down)
            if flags:
                self._app.CGEventSetFlags(event, flags)
            self._post(event)
            time.sleep(EVENT_GAP_SECONDS)

    def text(self, value: str) -> None:
        if not value or len(value) > 256:
            raise DriverError("Native text injection requires 1-256 characters.")
        encoded = value.encode("utf-16-le")
        length = len(encoded) // 2
        units = (ctypes.c_uint16 * length).from_buffer_copy(encoded)
        for down in (True, False):
            event = self._app.CGEventCreateKeyboardEvent(None, 0, down)
            if not event:
                raise DriverError("Quartz refused to create a Unicode keyboard event.")
            self._app.CGEventKeyboardSetUnicodeString(event, length, units)
            self._post(event)
            time.sleep(EVENT_GAP_SECONDS)

    def move(self, x: float, y: float) -> None:
        self._post(self._app.CGEventCreateMouseEvent(None, 5, CGPoint(x, y), 0))

    def click(self, x: float, y: float) -> None:
        self.move(x, y)
        time.sleep(EVENT_GAP_SECONDS)
        self._post(self._app.CGEventCreateMouseEvent(None, 1, CGPoint(x, y), 0))
        time.sleep(EVENT_GAP_SECONDS)
        self._post(self._app.CGEventCreateMouseEvent(None, 2, CGPoint(x, y), 0))

    def scroll(self, lines: int) -> None:
        if lines == 0:
            return
        self._post(self._app.CGEventCreateScrollWheelEvent(None, 1, 1, lines))


class Controller:
    def __init__(self, artifact: Path, request_id: str, timeout: float, backend: QuartzInput) -> None:
        self.artifact = artifact.resolve(strict=True)
        self.request_id = str(uuid.UUID(request_id))
        if self.artifact.name != self.request_id:
            raise DriverError("Native input artifact identity does not match the request UUID.")
        self.deadline = time.monotonic() + timeout
        self.backend = backend
        self.flow_path = self.artifact / "diagnostics" / "flow-player-input-progress.json"
        self.ui_path = self.artifact / "diagnostics" / "ui-window-input-progress.json"
        self.status_path = self.artifact / "diagnostics" / "native-input-driver.json"
        self.events: list[dict[str, Any]] = []
        self.offset_x = 0.0
        self.offset_y = 0.0
        self._status("Starting", "waiting-for-product-evidence")

    def _status(self, state: str, action: str, failure: str | None = None) -> None:
        _atomic_json(self.status_path, {
            "protocolVersion": PROTOCOL_VERSION,
            "requestId": self.request_id,
            "scenarioId": SCENARIO,
            "state": state,
            "action": action,
            "origin": "macOS Quartz CGEventPost at kCGHIDEventTap",
            "capturedAtUtc": _utc(),
            "failure": failure,
            "events": self.events[-MAX_EVENTS:],
        })

    def _record(self, action: str, **detail: Any) -> None:
        self.events.append({"atUtc": _utc(), "action": action, **detail})
        if len(self.events) > MAX_EVENTS * 2:
            self.events = self.events[-MAX_EVENTS:]
        self._status("Running", action)

    def _wait(self, description: str, predicate: Callable[[], Any], *, seconds: float = 45.0) -> Any:
        end = min(self.deadline, time.monotonic() + seconds)
        last_error: Exception | None = None
        while time.monotonic() < end:
            try:
                value = predicate()
                if value:
                    return value
            except (FileNotFoundError, json.JSONDecodeError) as error:
                last_error = error
            time.sleep(POLL_SECONDS)
        detail = f" ({last_error})" if last_error else ""
        raise DriverError(f"Timed out waiting for {description}{detail}.")

    def flow(self) -> dict[str, Any]:
        value = _read_json(self.flow_path)
        if _field(value, "requestId") != self.request_id or _field(value, "scenarioId") != SCENARIO:
            raise DriverError("Flow input progress belongs to another request or scenario.")
        return value

    def ui(self) -> dict[str, Any]:
        value = _read_json(self.ui_path)
        if _field(value, "runId") != self.request_id or _field(value, "scenario") != SCENARIO:
            raise DriverError("UI input progress belongs to another request or scenario.")
        failure = _field(value, "failure")
        if failure:
            raise DriverError("UI native-input observer failed: " + str(failure))
        return value

    def latest(self, *, visible: bool | None = None) -> dict[str, Any]:
        root = self.ui()
        latest = _field(root, "latest")
        if not isinstance(latest, dict):
            raise DriverError("UI input progress has no latest frame.")
        if visible is not None and bool(_field(latest, "visible", False)) != visible:
            raise DriverError("UI visibility does not match the requested driver operation.")
        return latest

    def _game_active(self) -> bool:
        try:
            latest = _field(_field(self.flow(), "inputTelemetry", {}), "latest", {})
            return bool(_field(latest, "gameActive", False))
        except (FileNotFoundError, DriverError):
            return False

    def ensure_game_active(self) -> None:
        self._wait("the game process to publish input telemetry", lambda: self.flow(), seconds=60)
        if self._game_active():
            return
        script = (
            'tell application "System Events" to set frontmost of first application process '
            'whose name contains "Stardew Valley" to true'
        )
        completed = subprocess.run(
            ["/usr/bin/osascript", "-e", script], capture_output=True, text=True, timeout=10, check=False
        )
        self._record("activate-game", returnCode=completed.returncode)
        if completed.returncode != 0:
            raise DriverError("Could not activate Stardew Valley in the owning GUI session: " + completed.stderr.strip())
        self._wait("Stardew Valley to become active", self._game_active, seconds=15)

    @staticmethod
    def _rect(value: Any) -> tuple[float, float, float, float]:
        if not isinstance(value, dict):
            raise DriverError("UI geometry rectangle is missing.")
        return (
            float(_field(value, "x")), float(_field(value, "y")),
            float(_field(value, "width")), float(_field(value, "height")),
        )

    def _local_to_screen(self, latest: dict[str, Any], x: float, y: float) -> tuple[float, float, float, float]:
        viewport = _field(latest, "viewport")
        window = _field(latest, "window")
        if not isinstance(viewport, dict) or not isinstance(window, dict):
            raise DriverError("UI observer did not publish window/viewport geometry.")
        position = _field(window, "position")
        client = _field(window, "clientBounds")
        if not isinstance(position, dict):
            raise DriverError("UI observer did not publish the global window position.")
        cx, cy, cw, ch = self._rect(client)
        vw, vh = float(_field(viewport, "width")), float(_field(viewport, "height"))
        if min(cw, ch, vw, vh) <= 0:
            raise DriverError("UI observer published non-positive window geometry.")
        sx, sy = cw / vw, ch / vh
        screen_x = float(_field(position, "x")) + cx + x * sx + self.offset_x
        screen_y = float(_field(position, "y")) + cy + y * sy + self.offset_y
        return screen_x, screen_y, sx, sy

    def move_local(self, x: float, y: float) -> None:
        for attempt in range(4):
            before = self._wait(
                "the UI observer to publish window and pointer geometry",
                lambda: self.latest(),
                seconds=15,
            )
            before_frame = int(_field(before, "completedFrame", -1))
            screen_x, screen_y, sx, sy = self._local_to_screen(before, x, y)
            self.backend.move(screen_x, screen_y)

            def observed() -> dict[str, Any] | None:
                latest = self.latest()
                pointer = _field(latest, "pointer")
                return latest if int(_field(latest, "completedFrame", -1)) > before_frame and isinstance(pointer, dict) else None

            after = self._wait("the game-local mouse position", observed, seconds=4)
            pointer = _field(after, "pointer")
            px, py = float(_field(pointer, "x")), float(_field(pointer, "y"))
            dx, dy = x - px, y - py
            if abs(dx) <= 3 and abs(dy) <= 3:
                self._record("move-pointer", localX=x, localY=y, screenX=screen_x, screenY=screen_y, attempts=attempt + 1)
                return
            self.offset_x += dx * sx
            self.offset_y += dy * sy
        raise DriverError("Could not calibrate Quartz screen coordinates to the game-local pointer.")

    @staticmethod
    def _fully_visible(element: dict[str, Any]) -> bool:
        bx, by, bw, bh = Controller._rect(_field(element, "bounds"))
        cx, cy, cw, ch = Controller._rect(_field(element, "clip"))
        return bw > 0 and bh > 0 and cx <= bx and cy <= by and cx + cw >= bx + bw and cy + ch >= by + bh

    def _elements(self) -> list[dict[str, Any]]:
        latest = self.latest(visible=True)
        elements = _field(latest, "elements")
        if not isinstance(elements, list):
            raise DriverError("Visible Window frame has no semantic element list.")
        return [value for value in elements if isinstance(value, dict)]

    def find_element(
        self, *, semantic: str | None = None, semantic_prefix: str | None = None,
        action: str | None = None, name: str | None = None,
    ) -> dict[str, Any]:
        matches = []
        for element in self._elements():
            element_semantic = _field(element, "semanticId")
            if semantic is not None and element_semantic != semantic:
                continue
            if semantic_prefix is not None and (not isinstance(element_semantic, str) or not element_semantic.startswith(semantic_prefix)):
                continue
            if action is not None and _field(element, "actionId") != action:
                continue
            if name is not None and _field(element, "name") != name:
                continue
            matches.append(element)
        if len(matches) != 1:
            raise DriverError(
                f"Expected one semantic element, found {len(matches)}: semantic={semantic!r}, "
                f"prefix={semantic_prefix!r}, action={action!r}, name={name!r}."
            )
        return matches[0]

    def ensure_visible(self, selector: dict[str, str]) -> dict[str, Any]:
        for _ in range(24):
            element = self.find_element(**selector)
            if self._fully_visible(element):
                return element
            bx, by, bw, bh = self._rect(_field(element, "bounds"))
            cx, cy, cw, ch = self._rect(_field(element, "clip"))
            self.move_local(cx + cw / 2, cy + ch / 2)
            self.backend.scroll(5 if by < cy else -5)
            self._record("scroll", direction="up" if by < cy else "down", semantic=_field(element, "semanticId"))
            before = int(_field(self.latest(), "completedFrame", -1))
            self._wait(
                "a fresh frame after semantic scrolling",
                lambda: int(_field(self.latest(), "completedFrame", -1)) > before,
                seconds=4,
            )
        raise DriverError("Could not reveal the requested semantic element within the scroll budget.")

    def click_element(self, **selector: str) -> None:
        element = self.ensure_visible(selector)
        bx, by, bw, bh = self._rect(_field(element, "bounds"))
        x, y = bx + bw / 2, by + bh / 2
        self.move_local(x, y)
        latest = self.latest(visible=True)
        screen_x, screen_y, _sx, _sy = self._local_to_screen(latest, x, y)
        self.backend.click(screen_x, screen_y)
        self._record("click-element", semantic=_field(element, "semanticId"), actionId=_field(element, "actionId"), name=_field(element, "name"))

    def move_fixture(self, key: str) -> None:
        fixture = _field(self.flow(), "fixture")
        point = _field(fixture, key) if isinstance(fixture, dict) else None
        if not isinstance(point, dict):
            raise DriverError(f"Flow fixture did not publish {key}.")
        self.move_local(float(_field(point, "x")), float(_field(point, "y")))

    def wait_stage(self, stage: str, *, seconds: float = 45) -> dict[str, Any]:
        def matches() -> dict[str, Any] | None:
            value = self.flow()
            return value if _field(value, "stage") == stage else None

        return self._wait(f"Flow stage {stage}", matches, seconds=seconds)

    def wait_visible(self) -> dict[str, Any]:
        def visible() -> dict[str, Any] | None:
            latest = self.latest()
            return latest if bool(_field(latest, "visible", False)) \
                and _field(latest, "experience") == "Hatifect.Flow/network" else None

        return self._wait("the production Flow Window to become visible", visible, seconds=30)

    def wait_hidden(self) -> None:
        self._wait(
            "the production Flow Window to close",
            lambda: not bool(_field(self.latest(), "visible", False)),
            seconds=20,
        )

    def press_k_open(self, fixture_key: str, *, expect_empty: bool = False) -> None:
        self.ensure_game_active()
        self.move_fixture(fixture_key)
        before = self.flow()
        attempts = len(_field(before, "entryAttempts", []))
        openings = len(_field(before, "openings", []))
        self.backend.key(K_KEY)
        self._record("press-k", target=fixture_key)

        def opened() -> dict[str, Any] | None:
            value = self.flow()
            current_attempts = _field(value, "entryAttempts", [])
            current_openings = _field(value, "openings", [])
            if len(current_attempts) <= attempts or len(current_openings) <= openings:
                return None
            target = str(_field(current_openings[-1], "target", ""))
            zero = target == str(uuid.UUID(int=0))
            if zero != expect_empty:
                raise DriverError(f"Ordinary K opening captured the wrong target identity: {target}")
            return value

        self._wait("an admitted ordinary K opening", opened, seconds=20)
        self.wait_visible()

    def press_busy_k(self) -> None:
        before = len(_field(self.flow(), "entryAttempts", []))
        self.backend.key(K_KEY)
        self._record("press-k-while-window-open")

        def rejected() -> bool:
            attempts = _field(self.flow(), "entryAttempts", [])
            if len(attempts) <= before:
                return False
            last = attempts[-1]
            return bool(_field(last, "menuOpen", False)) and not bool(_field(last, "guardsPassed", True))

        self._wait("the busy ordinary entry to be rejected", rejected, seconds=15)

    def close_window(self) -> None:
        self.backend.key(ESCAPE_KEY)
        self._record("escape-window")
        self.wait_hidden()

    def wait_phase(self, phase: str) -> None:
        def matches() -> dict[str, Any] | None:
            value = self.ui()
            return value if _field(value, "phase") == phase else None

        self._wait(f"native UI phase {phase}", matches, seconds=25)

    def wait_field(self, semantic: str, value: str) -> None:
        self._wait(
            f"field {semantic} to contain {value!r}",
            lambda: True if _field(self.find_element(semantic=semantic), "value") == value else False,
            seconds=20,
        )

    def replace_field(self, semantic: str, value: str) -> None:
        self.click_element(semantic=semantic)
        current = str(_field(self.find_element(semantic=semantic), "value", "") or "")
        if len(current) > 128:
            raise DriverError("Text field exceeds the native replacement budget.")
        for _ in current:
            self.backend.key(BACKSPACE_KEY)
        if current:
            self.wait_field(semantic, "")
        self.backend.text(value)
        self._record("replace-field", semantic=semantic, value=value)
        self.wait_field(semantic, value)

    @staticmethod
    def _capture_has_result(capture: dict[str, Any]) -> bool:
        state = _field(capture, "state")
        if not isinstance(state, dict):
            return False
        for element in _field(state, "elements", []):
            if isinstance(element, dict) and _field(element, "semanticId") == "Hatifect.Flow/network/element/result":
                return bool(_field(element, "value", "")) and Controller._fully_visible(element)
        return False

    def activate_and_wait_result(self, action: str, command_number: int) -> None:
        before_ui = self.ui()
        before_captures = len(_field(before_ui, "captures", []))
        before_commands = len(_field(self.flow(), "commands", []))
        self.click_element(action=action)

        self._wait(
            f"Flow command {command_number} to enter through the ordinary Window",
            lambda: len(_field(self.flow(), "commands", [])) > before_commands,
            seconds=20,
        )
        self.ensure_visible({"semantic": "Hatifect.Flow/network/element/result"})

        def captured() -> bool:
            captures = _field(self.ui(), "captures", [])
            return any(
                isinstance(value, dict) and self._capture_has_result(value)
                for value in captures[before_captures:]
            )

        self._wait("a fresh visible command-result capture", captured, seconds=25)
        self._record("command-result-captured", action=action, command=command_number)

    def run(self) -> None:
        self.ensure_game_active()
        first = self.wait_stage("ordinary-entry-source", seconds=90)
        fixture = _field(first, "fixture")
        if not isinstance(fixture, dict):
            raise DriverError("Ordinary input fixture metadata is missing.")
        source_name = str(_field(fixture, "sourceName"))
        destination_name = str(_field(fixture, "destinationName"))
        probe = str(_field(fixture, "probeText"))
        if not source_name.startswith("src_") or not destination_name.startswith("dst_") or not probe.startswith("native-"):
            raise DriverError("Ordinary input fixture names are malformed.")

        # First opening + the exact native pointer/text/backspace/tab proof.
        self.press_k_open("sourceScreen")
        self.wait_phase("Ready")
        self.click_element(semantic="Hatifect.Flow/network/field/name")
        self.wait_phase("Pointer")
        self.backend.text(probe)
        self._record("type-probe", value=probe)
        self.wait_phase("Text")
        self.backend.key(BACKSPACE_KEY)
        self._record("backspace-probe")
        self.wait_phase("Backspace")
        self.backend.key(TAB_KEY)
        self._record("tab-probe")
        self.wait_phase("Complete")
        required = {"Ready", "Pointer", "Text", "Backspace", "Tab"}
        self._wait(
            "all five native phase captures",
            lambda: required.issubset({str(_field(value, "phase")) for value in _field(self.ui(), "captures", [])}),
            seconds=20,
        )
        self.press_busy_k()

        # Source station.
        self.replace_field("Hatifect.Flow/network/field/name", source_name)
        self.activate_and_wait_result("Hatifect.Flow/network/action/register", 1)
        self.wait_stage("ordinary-entry-destination")
        self.close_window()

        # Destination station.
        self.press_k_open("destinationScreen")
        self.replace_field("Hatifect.Flow/network/field/name", destination_name)
        self.activate_and_wait_result("Hatifect.Flow/network/action/register", 2)
        self.wait_stage("author-route")
        self.close_window()

        # Empty-target opening; author the route and both shipments through real pointer input.
        self.press_k_open("emptyScreen", expect_empty=True)
        self.click_element(semantic_prefix="Hatifect.Flow/network/source/", name=source_name)
        self.click_element(semantic_prefix="Hatifect.Flow/network/destination/", name=destination_name)
        self._wait(
            "the route action to become enabled",
            lambda: bool(_field(self.find_element(action="Hatifect.Flow/network/action/link"), "enabled", False)),
            seconds=15,
        )
        self.activate_and_wait_result("Hatifect.Flow/network/action/link", 3)
        self.wait_stage("send-whole")

        self.click_element(semantic="Hatifect.Flow/network/slot/0")
        self.activate_and_wait_result("Hatifect.Flow/network/action/send", 4)
        self.wait_stage("send-five")

        self.click_element(semantic="Hatifect.Flow/network/slot/1")
        self.replace_field("Hatifect.Flow/network/field/quantity", "5")
        self.activate_and_wait_result("Hatifect.Flow/network/action/send-quantity", 5)
        self.wait_stage("close-window-for-transport")
        self.close_window()

        # Transport/save/reload is product-owned. Reopen via K and select the delivered partial parcel.
        delivered = self.wait_stage("ordinary-reopen-delivered-history", seconds=300)
        fixture = _field(delivered, "fixture")
        partial = str(_field(fixture, "partialParcelId")) if isinstance(fixture, dict) else ""
        parcel = uuid.UUID(partial)
        if parcel.int == 0:
            raise DriverError("Delivered-history stage has no partial parcel identity.")
        self.press_k_open("emptyScreen", expect_empty=True)
        history_semantic = "Hatifect.Flow/network/history/" + parcel.hex
        self.click_element(semantic=history_semantic)
        expected_prefix = str(parcel) + " · "

        def delivered_visible() -> bool:
            element = self.find_element(semantic="Hatifect.Flow/network/element/history-detail")
            value = str(_field(element, "value", ""))
            return self._fully_visible(element) and value.startswith(expected_prefix) and (
                " · Delivered · attempts: 1" in value or " · Доставлено · попыток: 1" in value
            )

        self._wait("the delivered partial shipment history detail", delivered_visible, seconds=30)
        self._record("delivered-history-visible", parcel=str(parcel))
        self._status("Completed", "native-player-input-complete")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifact-directory", required=True)
    parser.add_argument("--request-id", required=True)
    parser.add_argument("--timeout-seconds", type=float, required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    artifact = Path(args.artifact_directory)
    controller: Controller | None = None
    try:
        backend = QuartzInput()
        controller = Controller(artifact, args.request_id, args.timeout_seconds, backend)
        controller.run()
        return 0
    except (DriverError, OSError, ValueError, json.JSONDecodeError, subprocess.SubprocessError) as error:
        if controller is not None:
            controller._status("Failed", "native-player-input-failed", str(error))
        else:
            try:
                _atomic_json(artifact / "diagnostics" / "native-input-driver.json", {
                    "protocolVersion": PROTOCOL_VERSION,
                    "requestId": args.request_id,
                    "scenarioId": SCENARIO,
                    "state": "Failed",
                    "action": "driver-initialization",
                    "origin": "macOS Quartz CGEventPost at kCGHIDEventTap",
                    "capturedAtUtc": _utc(),
                    "failure": str(error),
                    "events": [],
                })
            except Exception:
                pass
        print(f"Hatifect native input driver: BLOCKED: {error}", file=os.sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
