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
        self.assertIn('record-secondary-failure "$scenario" "$result"', script)
        self.assertNotIn('runtime-result.secondary.json', script)
        semantic_branch = script[script.index('if [[ "$semantic_failure_marker" == "true" ]]'):]
        record_secondary = semantic_branch.index(
            'record-secondary-failure "$scenario" "$result"'
        )
        preserve_runtime_exit = semantic_branch.index(
            'if (( runtime_exit != 0 )); then\n    exit "$runtime_exit"\n  fi\n  exit 2'
        )
        self.assertNotIn(
            'write-result BLOCKED "$scenario" "$result"',
            script[script.index("semantic_failure_marker=false"):],
        )
        self.assertLess(record_secondary, preserve_runtime_exit)
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


class WrapperReproductionHookTests(unittest.TestCase):
    def test_reproduction_hook_is_fixed_and_precedes_every_downstream_action(self) -> None:
        for wrapper_name in ("hatifect-smoke", "hatifect-ui-test"):
            with self.subTest(wrapper=wrapper_name):
                script = (ROOT / "tools" / wrapper_name).read_text(encoding="utf-8")
                allocation = script.index('run_dir="$(new_runtime_run_dir)"')
                materialize = script.index('python3 "$reproduction" materialize')
                scenario_validation = script.index('python3 "$validator" check-scenario')
                runner = script.index('"$TOOLS_DIR/hatifect-live-runner"')

                self.assertLess(allocation, materialize)
                self.assertLess(materialize, scenario_validation)
                self.assertLess(materialize, runner)
                if wrapper_name == "hatifect-ui-test":
                    preparation = script.index('"$TOOLS_DIR/hatifect-live-prepare"')
                    self.assertLess(materialize, preparation)

                self.assertIn(
                    'reproduction="$TOOLS_DIR/live-harness/reproduction.py"',
                    script,
                )
                self.assertIn(
                    'reproduction_from_phase="${HATIFECT_REPRO_FROM_PHASE:-preflight}"',
                    script,
                )
                self.assertIn("HARNESS-REPRODUCTION-CHECKPOINT", script)
                self.assertIn('export HATIFECT_TEST_SEED="$reproduction_seed"', script)
                self.assertNotIn("HATIFECT_REPRO_COMMAND", script)
                self.assertNotIn("HATIFECT_REPRO_PATH", script)


if __name__ == "__main__":
    unittest.main()
