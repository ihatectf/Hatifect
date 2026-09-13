import copy
import importlib.util
import json
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
DRIVER_PATH = ROOT / "tools/live-harness/semantic-test-driver.py"
SPEC_PATH = ROOT / "tools/live-harness/semantic-tests/flow.ui.player.input.json"


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec is not None and spec.loader is not None
    spec.loader.exec_module(module)
    return module


DRIVER = load("hatifect_semantic_textfield_driver", DRIVER_PATH)
FIXTURE = load("semantic_input_fixture", Path(__file__).with_name("test_semantic_interactions.py"))


class SemanticTextFieldSelectorTests(unittest.TestCase):
    def test_operation_infers_textfield_from_semantic_id_without_tree_order(self):
        controller = object.__new__(DRIVER.SemanticController)
        elements = [
            FIXTURE.element("group", "Group"),
            FIXTURE.element("label", "StaticText"),
            FIXTURE.element(),
        ]
        controller.latest = lambda **_kwargs: {"visible": True, "elements": elements}
        with self.assertRaisesRegex(DRIVER.SemanticDriverError, "ambiguous.*TextField"):
            controller.find_element(semantic="Example/field/name")
        chosen = DRIVER.UI.resolve(
            {"elements": elements},
            {"semantic": "Example/field/name"},
            "fill",
        )
        self.assertIs(chosen, elements[2])

    def test_reference_workflow_uses_generic_field_selection_and_reveal_ops(self):
        doc = json.loads(SPEC_PATH.read_text(encoding="utf-8"))
        DRIVER.validate_spec(copy.deepcopy(doc), "flow.ui.player.input")

        focus = next(s for s in doc["steps"] if s["id"] == "focus-probe-name")
        self.assertEqual(focus["op"], "focus")
        self.assertEqual(focus["selector"], {"semantic": "Hatifect.Flow/network/field/name"})

        fills = [s for s in doc["steps"] if s["op"] == "fill"]
        self.assertEqual(len(fills), 3)
        self.assertTrue(all(set(s["selector"]) == {"semantic"} for s in fills))

        selections = [s for s in doc["steps"] if s["op"] == "select"]
        self.assertEqual(len(selections), 5)
        self.assertTrue(all(s.get("collection") for s in selections))
        self.assertEqual(len([s for s in doc["steps"] if s["op"] == "reveal"]), 5)
        self.assertEqual(len([s for s in doc["steps"] if s["op"] == "activate"]), 5)

    def test_fill_rejects_non_exact_field_selectors(self):
        doc = json.loads(SPEC_PATH.read_text(encoding="utf-8"))
        fill = next(s for s in doc["steps"] if s["op"] == "fill")
        fill["selector"] = {"semanticPrefix": "Example/field/"}
        with self.assertRaisesRegex(DRIVER.SemanticDriverError, "exact semantic field ID"):
            DRIVER.validate_spec(doc, "flow.ui.player.input")

    def test_shared_engine_fills_without_manual_role_or_private_action_calls(self):
        host = FIXTURE.Host([
            FIXTURE.element("label", "StaticText", value=None),
            FIXTURE.element(value="abc"),
        ])
        controller = object.__new__(DRIVER.SemanticController)
        controller.variables = {}
        controller.backend = host.backend
        controller.latest = host.latest
        controller.move_local = host.move_local
        controller._local_to_screen = host._local_to_screen
        controller._wait = host._wait
        controller._record = host._record
        controller.execute({
            "id": "fill",
            "op": "fill",
            "selector": {"semantic": "Example/field/name"},
            "value": "src_test",
        })
        self.assertEqual(host.data["elements"][1]["value"], "src_test")
        self.assertEqual(sum(e[0] == "click" for e in host.backend.events), 1)
        self.assertIn(("text", "src_test"), host.backend.events)
        self.assertTrue(host.data["elements"][1]["focused"])

    def test_new_operations_are_validated_without_mutating_base_registry(self):
        original = set(DRIVER.CORE.ALLOWED_OPS)
        for op, extra in (
            ("fill", {"selector": {"semantic": "Example/field/name"}, "value": "value"}),
            ("focus", {"selector": {"semantic": "Example/field/name"}}),
            ("activate", {"selector": {"action": "Example/save"}}),
            ("select", {"collection": "Example/list", "selector": {"semantic": "Example/item/1"}}),
            ("reveal", {"selector": {"semantic": "Example/result"}}),
            ("discover", {"variable": "controls"}),
            ("fillForm", {"fields": {"Example/field/name": "value"}}),
        ):
            with self.subTest(operation=op):
                doc = json.loads(SPEC_PATH.read_text(encoding="utf-8"))
                doc["steps"] = [{"id": "example", "op": op, **extra}]
                self.assertIs(DRIVER.validate_spec(doc, doc["id"]), doc)
        self.assertEqual(DRIVER.CORE.ALLOWED_OPS, original)

    def test_invalid_new_operation_payloads_fail_before_input(self):
        for op, extra in (
            ("fill", {"selector": {"semantic": "Example/name"}, "value": 5}),
            ("fillForm", {"fields": {}}),
            ("select", {"selector": {"semantic": "Example/item"}, "collection": 5}),
            ("discover", {"variable": "../bad"}),
            ("activate", {"selector": {"action": "Example/save", "first": "yes"}}),
        ):
            doc = json.loads(SPEC_PATH.read_text(encoding="utf-8"))
            doc["steps"] = [{"id": "bad", "op": op, **extra}]
            with self.subTest(operation=op), self.assertRaises(DRIVER.SemanticDriverError):
                DRIVER.validate_spec(doc, doc["id"])

    def test_native_pulse_releases_on_interruption(self):
        backend = object.__new__(DRIVER.SemanticQuartzInput)
        backend._post = mock.Mock()
        with mock.patch.object(DRIVER.time, "sleep", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                backend._pulse(1, 2, 0.1)
        self.assertEqual(backend._post.call_args_list, [mock.call(1), mock.call(2)])

    def test_native_pulse_checks_both_events_before_pressing(self):
        backend = object.__new__(DRIVER.SemanticQuartzInput)
        backend._post = mock.Mock()
        backend._cf = mock.Mock()
        with self.assertRaises(DRIVER.NATIVE.DriverError):
            backend._pulse(1, None, 0.1)
        backend._post.assert_not_called()
        backend._cf.CFRelease.assert_called_once_with(1)


if __name__ == "__main__":
    unittest.main()
