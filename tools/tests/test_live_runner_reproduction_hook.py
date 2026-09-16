import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


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
