import json
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC_PATH = ROOT / "tools" / "live-harness" / "semantic-tests" / "flow.ui.player.input.json"


class FlowUiPlayerInputWorkflowTests(unittest.TestCase):
    def test_native_probe_waits_for_next_gate_phase(self) -> None:
        document = json.loads(SPEC_PATH.read_text(encoding="utf-8"))
        steps = {step["id"]: step for step in document["steps"]}
        expected = {
            "wait-probe-ready": "Pointer",
            "wait-probe-pointer": "Text",
            "wait-probe-text": "Backspace",
            "wait-probe-backspace": "Tab",
            "wait-probe-complete": "Complete",
        }

        for step_id, phase in expected.items():
            with self.subTest(step=step_id):
                step = steps[step_id]
                self.assertEqual(step["op"], "wait")
                self.assertEqual(
                    step["conditions"],
                    [{"source": "ui", "path": "phase", "equals": phase}],
                )

        capture_assertion = steps["assert-native-phases"]["conditions"]
        self.assertEqual(
            capture_assertion,
            [{
                "source": "ui",
                "path": "captures",
                "containsAll": {
                    "path": "phase",
                    "values": ["Ready", "Pointer", "Text", "Backspace", "Tab"],
                },
            }],
        )


if __name__ == "__main__":
    unittest.main()
