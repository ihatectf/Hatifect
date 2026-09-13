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

    def test_semantic_lifecycle_is_finalized_after_companion_join(self) -> None:
        script = RUNNER.read_text(encoding="utf-8")
        started = script.index("SemanticAgent.Started")
        spawn = script.index("semantic_agent_pid=$!")
        joined = script.index("cleanup_semantic_agent\ntrap - EXIT INT TERM")
        completed = script.index("SemanticAgent.Completed")
        failed = script.index("SemanticAgent.Failed")
        final_completion = script.rindex('complete-scenario "$result"')

        self.assertLess(started, spawn)
        self.assertLess(joined, completed)
        self.assertLess(joined, failed)
        self.assertLess(completed, final_completion)
        self.assertLess(failed, final_completion)
        self.assertEqual(script.count('complete-scenario "$result"'), 2)


if __name__ == "__main__":
    unittest.main()
