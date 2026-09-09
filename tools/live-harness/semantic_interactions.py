"""Native interactions resolved from Hatifect's existing Scene/Accessibility projection.

No Flow IDs, labels, coordinates, domain commands or action-dispatch APIs live here.
The host supplies request-validated snapshots and the existing Quartz backend.
"""
from __future__ import annotations

import math
from typing import Any, Callable

MAX_NODES = 1024
MAX_SCROLLS = 64
MAX_TEXT = 128
INTERACTIVE = frozenset({"TextField", "Button", "ListItem"})
SELECTOR_KEYS = frozenset({"semantic", "semanticPrefix", "action", "name", "role", "collection", "node"})
# Carbon virtual-key code; the Stardew input adapter maps End to UiTextEditAction.End.
END_KEY = 0x77
BACKSPACE_KEY = 0x33


class InteractionError(RuntimeError):
    pass


class TargetPending(InteractionError):
    pass


def field(value: Any, key: str, default: Any = None) -> Any:
    if not isinstance(value, dict):
        return default
    return value[key] if key in value else value.get(key[:1].upper() + key[1:], default)


def rect(value: Any) -> tuple[float, float, float, float]:
    if not isinstance(value, dict):
        raise InteractionError("Missing semantic geometry.")
    values = tuple(field(value, key) for key in ("x", "y", "width", "height"))
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in values):
        raise InteractionError("Semantic geometry must contain finite numbers.")
    x, y, w, h = values
    if w < 0 or h < 0:
        raise InteractionError("Semantic geometry has a negative extent.")
    return float(x), float(y), float(w), float(h)


def intersection(a: tuple, b: tuple) -> tuple:
    left, top = max(a[0], b[0]), max(a[1], b[1])
    right, bottom = min(a[0] + a[2], b[0] + b[2]), min(a[1] + a[3], b[1] + b[3])
    return left, top, max(0.0, right - left), max(0.0, bottom - top)


def inside(r: tuple, point: tuple) -> bool:
    return r[0] <= point[0] < r[0] + r[2] and r[1] <= point[1] < r[1] + r[3]


def visible(node: dict) -> bool:
    b, c = rect(field(node, "bounds")), rect(field(node, "clip"))
    return b[2] > 0 and b[3] > 0 and c[2] > 0 and c[3] > 0 and (
        c[0] <= b[0] and c[1] <= b[1] and c[0] + c[2] >= b[0] + b[2]
        and c[1] + c[3] >= b[1] + b[3]
    )


def center(r: tuple) -> tuple[int, int]:
    point = math.floor(r[0] + r[2] / 2), math.floor(r[1] + r[3] / 2)
    if not inside(r, point):
        raise InteractionError("Semantic geometry contains no usable integer input point.")
    return point


def semantic_identity(node: dict) -> Any:
    # Virtualized rows already use the stable item identity as NodeId (ItemNodeId).
    # An unmapped arbitrary scene node MUST NOT be treated as a semantic control.
    if field(node, "role") == "ListItem":
        return field(node, "itemId") or field(node, "nodeId")
    return field(node, "semanticId")


def validate_selector(selector: Any) -> None:
    if not isinstance(selector, dict) or not selector or not set(selector) <= SELECTOR_KEYS:
        raise InteractionError("Invalid semantic selector keys.")
    if any(not isinstance(v, str) or not v or len(v) > 4096 for v in selector.values()):
        raise InteractionError("Semantic selector values must be bounded nonempty strings.")


def matches(node: dict, selector: dict) -> bool:
    identity = semantic_identity(node)
    if "semantic" in selector and identity != selector["semantic"]:
        return False
    if "semanticPrefix" in selector and (not isinstance(identity, str) or not identity.startswith(selector["semanticPrefix"])):
        return False
    pairs = {"action": "actionId", "name": "name", "role": "role", "node": "nodeId", "collection": "collectionId"}
    return all(field(node, target) == selector[key] for key, target in pairs.items() if key in selector)


def nodes(frame: dict) -> list[dict]:
    values = field(frame, "elements")
    if not isinstance(values, list) or len(values) > MAX_NODES or any(not isinstance(n, dict) for n in values):
        raise InteractionError("Semantic frame has no bounded element list.")
    ids = [field(n, "nodeId") for n in values if field(n, "nodeId") is not None]
    if len(ids) != len(set(ids)):
        raise InteractionError("Semantic frame contains duplicate concrete node identities.")
    return values


def resolve(frame: dict, selector: dict, intent: str = "inspect") -> dict:
    """Select by declared meaning and operation, never tree order/label heuristics."""
    validate_selector(selector)
    found = [n for n in nodes(frame) if matches(n, selector)]
    expected = {"focus": {"TextField"}, "fill": {"TextField"}, "activate": {"Button"},
                "select": {"ListItem"}, "collection": {"List"}}
    if intent in expected:
        found = [n for n in found if field(n, "role") in expected[intent]]
    elif intent == "click":
        found = [n for n in found if field(n, "role") in INTERACTIVE]
    elif intent == "value":
        found = [n for n in found if field(n, "value") is not None]
    elif intent not in {"inspect", "reveal"}:
        raise InteractionError(f"Unknown semantic interaction intent: {intent}")
    if not found:
        raise TargetPending(f"No {intent} target for {selector} in the current semantic frame.")
    if len(found) != 1:
        detail = [{"node": field(n, "nodeId"), "role": field(n, "role"), "semantic": semantic_identity(n)} for n in found[:12]]
        raise InteractionError(f"Semantic selector is ambiguous for {intent}: {selector}; candidates={detail}")
    return found[0]


def stamp(frame: dict) -> tuple:
    keys = ("surfaceEpoch", "experience", "completedFrame", "acceptedSceneVersion", "frameVersion")
    return tuple(field(frame, key) for key in keys)


def same_surface(a: dict, b: dict) -> bool:
    return stamp(a)[:2] == stamp(b)[:2]


def fresh(a: dict, b: dict) -> bool:
    before, after = field(a, "completedFrame"), field(b, "completedFrame")
    return isinstance(before, int) and isinstance(after, int) and same_surface(a, b) and after > before


def root_point(viewport: tuple, obstacles: list[tuple]) -> tuple[int, int]:
    """Find an actual root-wheel hit point outside collection wheel owners."""
    if len(obstacles) > 128:
        raise InteractionError("Too many wheel owners for bounded root-point discovery.")
    obstacles = [intersection(viewport, b) for b in obstacles]
    ys = {math.ceil(viewport[1]), math.ceil(viewport[1] + viewport[3]) - 1}
    for _, y, _, h in obstacles:
        ys.update((math.ceil(y) - 1, math.ceil(y + h)))
    for y in sorted(ys):
        if not viewport[1] <= y < viewport[1] + viewport[3]:
            continue
        intervals = sorted((b[0], b[0] + b[2]) for b in obstacles if b[1] <= y < b[1] + b[3])
        x = math.ceil(viewport[0])
        for left, right in intervals:
            if x < left:
                break
            if x < right:
                x = math.ceil(right)
        point = x, y
        if inside(viewport, point) and not any(inside(b, point) for b in obstacles):
            return point
    raise InteractionError("No root wheel input point outside nested collection owners.")


class SemanticInteractions:
    """Reusable feedback-driven UI interactions over a request-validating driver.

    The driver owns deadlines, evidence identity and platform coordinate calibration.
    This layer owns semantic resolution, effect-free preparation and native postconditions.
    """
    def __init__(self, driver: Any) -> None:
        self.driver = driver
        self.last_target: dict | None = None

    def frame(self) -> dict:
        return self.wait("fully rendered semantic surface", self._frame)

    def _frame(self) -> dict:
        value = self.driver.latest(visible=True)
        if field(value, "visible") is not True:
            raise TargetPending("The semantic surface is not visible.")
        for accepted, rendered in (("acceptedSceneVersion", "renderedSceneVersion"), ("frameVersion", "renderedFrameVersion")):
            if field(value, accepted) is not None and field(value, accepted) != field(value, rendered):
                raise TargetPending("The accepted semantic state has not completed rendering.")
        nodes(value)
        return value

    def wait(self, description: str, predicate: Callable, seconds: float = 20) -> Any:
        def ready():
            try:
                return predicate()
            except TargetPending:
                return False
        return self.driver._wait(description, ready, seconds=seconds)

    def ready(self, selector: dict, intent: str, seconds: float = 20) -> tuple[dict, dict]:
        def get():
            f = self.frame()
            n = resolve(f, selector, intent)
            self.last_target = {"selector": selector, "intent": intent, "node": field(n, "nodeId"), "stamp": stamp(f)}
            return f, n
        return self.wait(f"{intent} target {selector}", get, seconds)

    def after(self, before: dict, predicate: Callable = lambda _f: True) -> dict:
        def ready():
            f = self.frame()
            if not same_surface(before, f):
                raise InteractionError("Semantic surface was replaced during an input operation.")
            return f if fresh(before, f) and predicate(f) else None
        return self.wait("fresh rendered semantic input result", ready)

    @staticmethod
    def collections(frame: dict) -> list[dict]:
        values = field(frame, "collections", [])
        if not isinstance(values, list) or len(values) > 128:
            raise InteractionError("Invalid bounded semantic collection metadata.")
        return values

    def wheel(self, before: dict, region: dict, direction: int, *, root: bool = False) -> dict:
        viewport = intersection(rect(field(region, "viewport")), rect(field(region, "clip", field(region, "viewport"))))
        if viewport[2] <= 0 or viewport[3] <= 0:
            raise InteractionError("The requested wheel owner is outside the viewport.")
        if root:
            obstacles = [intersection(rect(field(c, "viewport")), rect(field(c, "clip"))) for c in self.collections(before)]
            point = root_point(viewport, obstacles)
        else:
            point = center(viewport)
        self.driver.move_local(*point)
        # Pointer calibration can take frames. Never use stale region geometry afterwards.
        current = self.frame()
        if not same_surface(before, current):
            raise InteractionError("Semantic surface changed before wheel input.")
        current_region = field(current, "rootScroll") if root else next(
            (c for c in self.collections(current) if field(c, "nodeId") == field(region, "nodeId")), None)
        if not isinstance(current_region, dict) or any(
            rect(field(current_region, k, field(current_region, "viewport"))) != rect(field(region, k, field(region, "viewport")))
            for k in ("viewport", "clip")
        ):
            raise TargetPending("Wheel-owner geometry changed during pointer calibration.")
        offset = field(current_region, "offset")
        if not isinstance(offset, (int, float)) or not math.isfinite(offset):
            raise InteractionError("Wheel owner has no finite scroll offset.")
        self.driver.backend.scroll(direction * 3)
        self.driver._record("semantic-wheel", owner=field(region, "nodeId", "root"), direction=direction)
        def changed(f):
            r = field(f, "rootScroll") if root else next(
                (c for c in self.collections(f) if field(c, "nodeId") == field(region, "nodeId")), None)
            return isinstance(r, dict) and field(r, "offset") != offset
        return self.after(current, changed)

    def reveal(self, selector: dict, intent: str = "reveal") -> tuple[dict, dict]:
        seen: set[tuple] = set()
        surface = None
        for _ in range(MAX_SCROLLS):
            f, n = self.ready(selector, intent)
            if surface is not None and stamp(f)[:2] != surface:
                raise InteractionError("Semantic surface changed during reveal.")
            surface = stamp(f)[:2]
            if visible(n):
                return f, n
            b = rect(field(n, "bounds"))
            state = (field(n, "nodeId"), b, rect(field(n, "clip")))
            if state in seen:
                raise InteractionError("Semantic reveal made no progress; refusing to loop.")
            seen.add(state)
            collection = next((c for c in self.collections(f) if field(c, "semanticId") == field(n, "collectionId")), None)
            # Root clipping must be repaired before trying the nested wheel owner.
            region = field(f, "rootScroll")
            root = True
            if collection is not None:
                vp = rect(field(collection, "viewport"))
                clipped = intersection(vp, rect(field(collection, "clip")))
                if clipped == vp and (b[1] < vp[1] or b[1] + b[3] > vp[1] + vp[3]):
                    region, root = collection, False
            if not isinstance(region, dict):
                raise InteractionError("No observed scroll owner can reveal the target.")
            vp = rect(field(region, "viewport"))
            if b[2] <= 0 or b[3] <= 0 or b[3] > vp[3] or b[0] < vp[0] or b[0] + b[2] > vp[0] + vp[2]:
                raise InteractionError("Target cannot fit the observed scroll viewport.")
            direction = 1 if b[1] < vp[1] else -1
            try:
                self.wheel(f, region, direction, root=root)
            except TargetPending:
                seen.discard(state)
        raise InteractionError("Semantic reveal exhausted its native scroll budget.")

    def _prepared(self, selector: dict, intent: str) -> tuple[dict, dict, tuple]:
        # Read -> reveal -> calibrate -> re-read. Input never uses cached layout coordinates.
        for _ in range(4):
            before, node = self.reveal(selector, intent)
            if field(node, "enabled") is not True:
                raise InteractionError(f"Semantic {intent} target is disabled: {selector}")
            point = center(rect(field(node, "bounds")))
            self.driver.move_local(*point)
            current = self.frame()
            if not same_surface(before, current):
                raise InteractionError("Semantic surface changed before native input.")
            current_node = resolve(current, selector, intent)
            if field(current_node, "nodeId") != field(node, "nodeId"):
                raise InteractionError("Concrete semantic control changed before native input.")
            if not visible(current_node) or rect(field(current_node, "bounds")) != rect(field(node, "bounds")):
                continue
            if field(current_node, "enabled") is not True:
                raise InteractionError("Semantic target became disabled before native input.")
            return current, current_node, point
        raise InteractionError("Semantic target geometry did not stabilize before native input.")

    def click(self, selector: dict, intent: str = "click") -> tuple[dict, dict]:
        before, node, point = self._prepared(selector, intent)
        x, y, _, _ = self.driver._local_to_screen(before, *point)
        self.driver.backend.click(x, y)
        self.driver._record("semantic-click", node=field(node, "nodeId"), semantic=semantic_identity(node), role=field(node, "role"))
        # This is not an action-success assertion. Product command/result evidence stays authoritative.
        return before, node

    def focus(self, selector: dict) -> tuple[dict, dict]:
        before, node = self.ready(selector, "focus")
        if visible(node) and field(node, "focused") is True and field(node, "enabled") is True:
            return before, node
        before, node = self.click(selector, "focus")
        identity = field(node, "nodeId")
        def focused(f):
            n = resolve(f, selector, "focus")
            if field(n, "nodeId") != identity:
                raise InteractionError("Text control was replaced before focus acknowledgement.")
            return field(n, "focused") is True and field(n, "enabled") is True
        current = self.after(before, focused)
        return current, resolve(current, selector, "focus")

    def fill(self, selector: dict, value: str) -> None:
        if not isinstance(value, str) or len(value) > MAX_TEXT:
            raise InteractionError("Replacement text exceeds the semantic field budget.")
        before, node = self.focus(selector)
        identity = field(node, "nodeId")
        current = field(node, "value")
        if not isinstance(current, str) or len(current) > MAX_TEXT:
            raise InteractionError("Editable control has no bounded string value.")
        if current == value:
            return
        def editing(f):
            n = resolve(f, selector, "fill")
            if field(n, "nodeId") != identity or field(n, "focused") is not True or field(n, "enabled") is not True:
                raise InteractionError("The native text-edit owner changed; refusing further keyboard input.")
            return n
        # Clicking may place the caret in the middle. End is native, not a text API shortcut.
        if current:
            self.driver.backend.key(END_KEY)
            before = self.after(before)
            editing(before)
        for _ in range(len(current)):
            before = self.frame()
            old = field(editing(before), "value")
            self.driver.backend.key(BACKSPACE_KEY)
            before = self.after(before, lambda f: field(editing(f), "value") != old)
        if field(editing(before), "value") != "":
            raise InteractionError("Native Backspace did not clear the complete field.")
        if value:
            latest = self.frame()
            if not same_surface(before, latest):
                raise InteractionError("Semantic surface changed before replacement text.")
            editing(latest)
            before = latest
            self.driver.backend.text(value)
            self.after(before, lambda f: field(editing(f), "value") == value)
        self.driver._record("semantic-filled", semantic=semantic_identity(node), length=len(value))

    def select(self, selector: dict) -> None:
        validate_selector(selector)
        # Missing virtualized rows can only be searched inside an explicitly identified collection.
        seen: set[tuple] = set()
        scan_down = False
        for _ in range(MAX_SCROLLS):
            f = self.frame()
            try:
                n = resolve(f, selector, "select")
                break
            except TargetPending:
                owner = selector.get("collection")
                if owner is None:
                    raise InteractionError("An unmaterialized item requires a collection selector.")
                region = next((c for c in self.collections(f) if field(c, "semanticId") == owner), None)
                if region is None:
                    raise TargetPending("Collection observation is not ready.")
                viewport = rect(field(region, "viewport"))
                clip = rect(field(region, "clip"))
                root = field(f, "rootScroll")
                if intersection(viewport, clip) != viewport:
                    if not isinstance(root, dict):
                        raise InteractionError("Clipped collection has no observed root scroll owner.")
                    root_viewport = rect(field(root, "viewport"))
                    if viewport[3] <= root_viewport[3] or intersection(viewport, clip)[3] <= 0:
                        direction = 1 if viewport[1] < root_viewport[1] else -1
                        self.wheel(f, root, direction, root=True)
                        continue
                offset, maximum = field(region, "offset"), field(region, "maximumOffset")
                if not all(isinstance(v, (int, float)) and math.isfinite(v) for v in (offset, maximum)):
                    raise InteractionError("Collection scroll bounds are unavailable.")
                if offset == 0:
                    scan_down = True
                signature = (field(region, "nodeId"), offset, scan_down)
                if signature in seen or (scan_down and offset >= maximum):
                    raise InteractionError("Requested semantic item was not found in the collection.")
                seen.add(signature)
                self.wheel(f, region, -1 if scan_down else 1)
        else:
            raise InteractionError("Virtualized semantic selection exceeded its scroll budget.")
        if field(n, "selected") is True:
            return
        before, n = self.click(selector, "select")
        identity = field(n, "nodeId")
        def selected(f):
            candidate = resolve(f, selector, "select")
            if field(candidate, "nodeId") != identity:
                raise InteractionError("Selected item identity changed during native input.")
            return field(candidate, "selected") is True
        self.after(before, selected)

    def discover(self) -> list[dict]:
        f = self.frame()
        return [{"semantic": semantic_identity(n), "node": field(n, "nodeId"), "role": field(n, "role"),
                 "action": field(n, "actionId"), "collection": field(n, "collectionId"),
                 "enabled": field(n, "enabled"), "focused": field(n, "focused"), "visible": visible(n)}
                for n in nodes(f) if field(n, "role") in INTERACTIVE and semantic_identity(n) is not None]
