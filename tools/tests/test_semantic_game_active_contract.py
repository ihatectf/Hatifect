import json
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SPEC_PATH = ROOT / "tools" / "live-harness" / "semantic-tests" / "flow.ui.player.input.json"


class SemanticGameActiveContractTests(unittest.TestCase):
    def test_reference_workflow_waits_for_authoritative_game_active_evidence(self) -> None:
        document = json.loads(SPEC_PATH.read_text(encoding="utf-8"))
        steps = document["steps"]
        self.assertGreater(len(steps), 1)

        first = steps[0]
        self.assertEqual(first["id"], "game-active")
        self.assertEqual(first["op"], "wait")
        self.assertGreater(first.get("timeout", 0), 0)
        self.assertEqual(first["conditions"], [{
            "source": "domain",
            "path": "inputTelemetry.latest.gameActive",
            "equals": True,
        }])

        # The harness owns launch/focus. The reference native acceptance must not race
        # System Events process discovery before Flow has published GameActive=true.
        self.assertNotIn("ensureGameActive", [step.get("op") for step in steps])


if __name__ == "__main__":
    unittest.main()
