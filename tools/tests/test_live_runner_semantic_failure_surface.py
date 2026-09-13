import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "tools" / "hatifect-live-runner"


class LiveRunnerSemanticFailureSurfaceTests(unittest.TestCase):
    def test_semantic_failure_marker_surfaces_original_driver_evidence(self) -> None:
        script = RUNNER.read_text(encoding="utf-8")
        marker = 'grep -qx "semantic-test-driver-failed" "$artifact_dir/.cancel-requested"'
        diagnostic = 'the runtime cancellation is secondary.'
        log_tail = 'tail -n 120 "$artifact_dir/semantic-test-driver.log"'
        state_dump = 'cat "$artifact_dir/diagnostics/semantic-test-driver.json"'

        self.assertIn(marker, script)
        self.assertIn(diagnostic, script)
        self.assertIn(log_tail, script)
        self.assertIn(state_dump, script)

        marker_index = script.index(marker)
        legacy_gate_index = script.index('if (( runtime_exit == 0 )) && [[ "$semantic_driver_exit" != "0" ]]')
        self.assertLess(marker_index, legacy_gate_index)


if __name__ == "__main__":
    unittest.main()
