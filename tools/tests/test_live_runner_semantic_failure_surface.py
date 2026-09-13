import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "tools" / "hatifect-live-runner"


class LiveRunnerSemanticFailureSurfaceTests(unittest.TestCase):
    def test_semantic_failure_marker_surfaces_original_agent_evidence(self) -> None:
        script = RUNNER.read_text(encoding="utf-8")
        marker = 'grep -qx "semantic-test-agent-failed" "$artifact_dir/.cancel-requested"'
        diagnostic = 'the runtime cancellation is secondary.'
        root_failure = '--assertion-id HARNESS-SEMANTIC-TEST-AGENT'
        summary = 'cat "$artifact_dir/failure-summary.txt"'

        self.assertIn(marker, script)
        self.assertIn(diagnostic, script)
        self.assertIn(root_failure, script)
        self.assertIn(summary, script)
        self.assertIn('(( runtime_exit == 0 )) && [[ "$semantic_agent_exit" != "0" ]]', script)
        self.assertNotIn('tail -n 120 "$artifact_dir/semantic-test-agent.log"', script)
        self.assertNotIn('cat "$artifact_dir/diagnostics/semantic-test-agent.json"', script)


if __name__ == "__main__":
    unittest.main()
