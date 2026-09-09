import copy
import importlib.util
import unittest
from pathlib import Path

PATH = Path(__file__).resolve().parents[1] / "live-harness/semantic_interactions.py"
SPEC = importlib.util.spec_from_file_location("semantic_interactions_under_test", PATH)
UI = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(UI)


def box(x=0, y=0, width=800, height=600):
    return dict(x=x, y=y, width=width, height=height)


def element(node="input", role="TextField", semantic="Example/field/name", value="abc", **extra):
    return dict(nodeId=node, semanticId=semantic, role=role, actionId=None, name="Имя станции",
                value=value, enabled=True, focused=False, selected=False,
                bounds=box(100, 100, 100, 20), clip=box(), **extra)


class Backend:
    def __init__(self, host):
        self.host = host
        self.events = []
        self.caret = 1
        self.drop_click = False
        self.drop_text = False

    def click(self, x, y):
        self.events.append(("click", x, y))
        if self.drop_click:
            return
        for n in self.host.data["elements"]:
            if UI.field(n, "role") not in UI.INTERACTIVE:
                continue
            if UI.inside(UI.rect(n["bounds"]), (x, y)):
                for other in self.host.data["elements"]:
                    other["focused"] = False
                n["focused"] = True
                if n["role"] == "ListItem":
                    n["selected"] = True
                self.caret = 1
                break

    def key(self, code):
        self.events.append(("key", code))
        n = next((n for n in self.host.data["elements"] if n["focused"] and n["role"] == "TextField"), None)
        if n is None:
            return
        if code == UI.END_KEY:
            self.caret = len(n["value"])
        elif code == UI.BACKSPACE_KEY and self.caret:
            n["value"] = n["value"][:self.caret - 1] + n["value"][self.caret:]
            self.caret -= 1

    def text(self, value):
        self.events.append(("text", value))
        if self.drop_text:
            return
        n = next(n for n in self.host.data["elements"] if n["focused"] and n["role"] == "TextField")
        n["value"] = n["value"][:self.caret] + value + n["value"][self.caret:]

    def scroll(self, direction):
        self.events.append(("scroll", direction))
        self.host.scroll(direction)


class Host:
    def __init__(self, elements):
        self.data = dict(visible=True, surfaceEpoch=1, experience="Example/window", completedFrame=1,
                         acceptedSceneVersion=1, renderedSceneVersion=1, frameVersion=1,
                         renderedFrameVersion=1, elements=elements, collections=[])
        self.backend = Backend(self)
        self.records = []
        self.move_hook = None
        self.scroll = lambda direction: None

    def latest(self, **_kwargs):
        self.data["completedFrame"] += 1
        return copy.deepcopy(self.data)

    def move_local(self, x, y):
        self.backend.events.append(("move", x, y))
        if self.move_hook:
            self.move_hook()

    def _local_to_screen(self, _frame, x, y):
        return x, y, 1, 1

    def _wait(self, description, predicate, **_kwargs):
        for _ in range(8):
            result = predicate()
            if result:
                return result
        raise UI.InteractionError("Timed out: " + description)

    def _record(self, action, **kwargs):
        self.records.append((action, kwargs))


class SemanticInteractionsTests(unittest.TestCase):
    def test_operation_selects_input_not_label_or_validation_regardless_of_order(self):
        values = [element("label", "StaticText", value=None), element(), element("error", "StaticText", value=None)]
        for ordering in (values, list(reversed(values)), values[1:] + values[:1]):
            chosen = UI.resolve({"elements": ordering}, {"semantic": "Example/field/name"}, "fill")
            self.assertEqual(chosen["nodeId"], "input")

    def test_no_first_match_fallback_for_two_editable_controls(self):
        with self.assertRaisesRegex(UI.InteractionError, "ambiguous"):
            UI.resolve({"elements": [element("one"), element("two")]}, {"semantic": "Example/field/name"}, "fill")

    def test_duplicate_concrete_identity_is_rejected(self):
        with self.assertRaisesRegex(UI.InteractionError, "duplicate concrete"):
            UI.resolve({"elements": [element(), element()]}, {"semantic": "Example/field/name"})

    def test_unmapped_listitem_uses_existing_item_node_identity_not_scene_heuristics(self):
        item = element("Example/item/42", "ListItem", None, None)
        self.assertIs(UI.resolve({"elements": [item]}, {"semantic": "Example/item/42"}, "select"), item)
        with self.assertRaises(UI.TargetPending):
            UI.resolve({"elements": [element("Example/item/42", "Group", None)]}, {"semantic": "Example/item/42"})

    def test_actions_use_action_identity_and_button_role(self):
        button = element("button", "Button", "Example/actions", None)
        button["actionId"] = "Example/save"
        self.assertIs(UI.resolve({"elements": [element("label", "StaticText"), button]}, {"action": "Example/save"}, "activate"), button)

    def test_value_resolution_ignores_structural_and_label_nodes(self):
        values = [element("group", "Group", value=None), element("label", "StaticText", value=None), element("value", "StaticText", value="Done")]
        self.assertEqual(UI.resolve({"elements": values}, {"semantic": "Example/field/name"}, "value")["nodeId"], "value")

    def test_fill_acquires_focus_moves_caret_to_end_clears_and_types_unicode(self):
        host = Host([element("label", "StaticText", value=None), element(value="A😀Б")])
        UI.SemanticInteractions(host).fill({"semantic": "Example/field/name"}, "Поле 🦦")
        self.assertEqual(host.data["elements"][1]["value"], "Поле 🦦")
        keys = [event for event in host.backend.events if event[0] == "key"]
        self.assertEqual(keys, [("key", UI.END_KEY)] + [("key", UI.BACKSPACE_KEY)] * 3)
        self.assertEqual(sum(event[0] == "click" for event in host.backend.events), 1)

    def test_fill_empty_clears_without_injecting_empty_unicode_event(self):
        host = Host([element()])
        UI.SemanticInteractions(host).fill({"semantic": "Example/field/name"}, "")
        self.assertEqual(host.data["elements"][0]["value"], "")
        self.assertFalse(any(event[0] == "text" for event in host.backend.events))

    def test_fill_is_idempotent_when_exact_value_already_present(self):
        host = Host([element(value="exact")])
        host.data["elements"][0]["focused"] = True
        UI.SemanticInteractions(host).fill({"semantic": "Example/field/name"}, "exact")
        self.assertEqual(host.backend.events, [])

    def test_lost_pointer_event_cannot_cause_typing_in_unfocused_control(self):
        host = Host([element()])
        host.backend.drop_click = True
        with self.assertRaisesRegex(UI.InteractionError, "Timed out"):
            UI.SemanticInteractions(host).fill({"semantic": "Example/field/name"}, "new")
        self.assertFalse(any(e[0] in {"key", "text"} for e in host.backend.events))

    def test_lost_native_text_does_not_report_success(self):
        host = Host([element(value="")])
        host.backend.drop_text = True
        with self.assertRaisesRegex(UI.InteractionError, "Timed out"):
            UI.SemanticInteractions(host).fill({"semantic": "Example/field/name"}, "new")

    def test_disabled_field_never_receives_input(self):
        host = Host([element()])
        host.data["elements"][0]["enabled"] = False
        with self.assertRaisesRegex(UI.InteractionError, "disabled"):
            UI.SemanticInteractions(host).fill({"semantic": "Example/field/name"}, "new")
        self.assertEqual(host.backend.events, [])

    def test_changed_geometry_is_reresolved_after_pointer_calibration(self):
        host = Host([element()])
        changed = False
        def reflow():
            nonlocal changed
            if not changed:
                host.data["elements"][0]["bounds"]["x"] = 400
                changed = True
        host.move_hook = reflow
        UI.SemanticInteractions(host).focus({"semantic": "Example/field/name"})
        clicks = [e for e in host.backend.events if e[0] == "click"]
        self.assertEqual(clicks, [("click", 450, 110)])

    def test_replaced_surface_never_receives_stale_click(self):
        host = Host([element()])
        host.move_hook = lambda: host.data.update(surfaceEpoch=2)
        with self.assertRaisesRegex(UI.InteractionError, "surface changed"):
            UI.SemanticInteractions(host).focus({"semantic": "Example/field/name"})
        self.assertFalse(any(e[0] == "click" for e in host.backend.events))

    def test_target_disabled_during_calibration_never_receives_click(self):
        host = Host([element()])
        host.move_hook = lambda: host.data["elements"][0].update(enabled=False)
        with self.assertRaisesRegex(UI.InteractionError, "became disabled"):
            UI.SemanticInteractions(host).focus({"semantic": "Example/field/name"})
        self.assertFalse(any(e[0] == "click" for e in host.backend.events))

    def test_accepted_but_unrendered_frame_is_not_actionable(self):
        host = Host([element()])
        host.data["renderedFrameVersion"] = 0
        with self.assertRaisesRegex(UI.InteractionError, "Timed out"):
            UI.SemanticInteractions(host).focus({"semantic": "Example/field/name"})
        self.assertEqual(host.backend.events, [])

    def test_activation_is_never_replayed_to_force_a_domain_result(self):
        host = Host([element("save", "Button", "Example/save", None)])
        UI.SemanticInteractions(host).click({"semantic": "Example/save"}, "activate")
        self.assertEqual(sum(e[0] == "click" for e in host.backend.events), 1)

    def test_native_selection_checks_observed_selected_flag_and_is_idempotent(self):
        host = Host([element("Example/row/1", "ListItem", None, None, collectionId="Example/list")])
        ui = UI.SemanticInteractions(host)
        selector = {"semantic": "Example/row/1", "collection": "Example/list"}
        ui.select(selector)
        ui.select(selector)
        self.assertTrue(host.data["elements"][0]["selected"])
        self.assertEqual(sum(e[0] == "click" for e in host.backend.events), 1)

    def test_native_selection_requires_acknowledgement(self):
        host = Host([element("Example/row/1", "ListItem", None, None)])
        host.backend.drop_click = True
        with self.assertRaisesRegex(UI.InteractionError, "Timed out"):
            UI.SemanticInteractions(host).select({"semantic": "Example/row/1"})

    def test_virtualized_selection_searches_only_declared_collection(self):
        host = Host([])
        host.data["collections"] = [dict(nodeId="list", semanticId="Example/list", viewport=box(50, 50, 200, 200), clip=box(), offset=0, maximumOffset=100)]
        def scroll(_direction):
            host.data["collections"][0]["offset"] = 100
            host.data["elements"] = [element("Example/row/99", "ListItem", None, None, collectionId="Example/list")]
        host.scroll = scroll
        UI.SemanticInteractions(host).select({"semantic": "Example/row/99", "collection": "Example/list"})
        self.assertEqual(sum(e[0] == "scroll" for e in host.backend.events), 1)
        self.assertTrue(host.data["elements"][0]["selected"])

    def test_unmaterialized_item_without_collection_is_not_guessed(self):
        host = Host([])
        with self.assertRaisesRegex(UI.InteractionError, "requires a collection"):
            UI.SemanticInteractions(host).select({"semantic": "Example/row/1"})
        self.assertEqual(host.backend.events, [])

    def test_clipped_field_reveals_without_clicking_result_or_wrong_collection(self):
        host = Host([element()])
        host.data["elements"][0].update(bounds=box(100, 700, 100, 20), clip=box())
        host.data["rootScroll"] = dict(viewport=box(), offset=0, maximumOffset=400)
        host.data["collections"] = [dict(nodeId="nested", semanticId="Example/list", viewport=box(0, 0, 790, 590), clip=box(), offset=0)]
        def scroll(_direction):
            host.data["rootScroll"]["offset"] = 200
            host.data["elements"][0]["bounds"]["y"] = 500
        host.scroll = scroll
        UI.SemanticInteractions(host).reveal({"semantic": "Example/field/name"}, "fill")
        move = next(e for e in host.backend.events if e[0] == "move")
        self.assertFalse(UI.inside((0, 0, 790, 590), move[1:]))
        self.assertFalse(any(e[0] == "click" for e in host.backend.events))

    def test_scroll_no_progress_is_bounded(self):
        host = Host([element()])
        host.data["elements"][0].update(bounds=box(100, 700, 100, 20), clip=box())
        host.data["rootScroll"] = dict(viewport=box(), offset=0, maximumOffset=400)
        with self.assertRaisesRegex(UI.InteractionError, "Timed out"):
            UI.SemanticInteractions(host).reveal({"semantic": "Example/field/name"}, "fill")
        self.assertEqual(sum(e[0] == "scroll" for e in host.backend.events), 1)

    def test_root_wheel_discovery_does_not_use_a_fixed_grid(self):
        viewport = (0, 0, 100, 100)
        obstacles = [(0, 0, 17, 100), (18, 0, 82, 100)]
        point = UI.root_point(viewport, obstacles)
        self.assertEqual(point[0], 17)
        self.assertFalse(any(UI.inside(b, point) for b in obstacles))

    def test_root_wheel_fails_when_no_unowned_point_exists(self):
        with self.assertRaisesRegex(UI.InteractionError, "No root wheel"):
            UI.root_point((0, 0, 100, 100), [(0, 0, 100, 100)])

    def test_discovery_returns_controls_not_decorations_and_uses_stable_identity(self):
        host = Host([element("label", "StaticText", value=None), element(), element("row", "ListItem", None, None)])
        catalog = UI.SemanticInteractions(host).discover()
        self.assertEqual([n["role"] for n in catalog], ["TextField", "ListItem"])
        self.assertEqual(host.backend.events, [])

    def test_nonfinite_geometry_is_rejected(self):
        for v in (float("nan"), float("inf"), True, "4"):
            with self.subTest(value=v), self.assertRaises(UI.InteractionError):
                UI.rect(box(x=v))

    def test_unknown_selector_field_is_rejected(self):
        with self.assertRaises(UI.InteractionError):
            UI.resolve({"elements": [element()]}, {"semantic": "Example/field/name", "first": "true"})


if __name__ == "__main__":
    unittest.main()
