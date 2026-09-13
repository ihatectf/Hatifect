import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


class RuntimeEntrypointTests(unittest.TestCase):
    def test_help_does_not_initialize_runtime_or_prepare_a_deployment(self):
        for entrypoint in ("hatifect-ui-test", "hatifect-smoke", "hatifect-live-runner"):
            for flag in ("-h", "--help"):
                with self.subTest(entrypoint=entrypoint, flag=flag):
                    completed, initialized = self._invoke(entrypoint, [flag])
                    self.assertEqual(completed.returncode, 0, completed.stderr)
                    self.assertIn("Usage:", completed.stdout)
                    self.assertEqual(completed.stderr, "")
                    self.assertFalse(initialized)

    def test_invalid_arguments_fail_before_runtime_initialization(self):
        cases = [(name, args)
                 for name in ("hatifect-ui-test", "hatifect-smoke")
                 for args in ([], [""], ["--unknown"], ["--help", "all"], ["all", "extra"])]
        cases.extend(("hatifect-live-runner", args) for args in (
            [], ["--help", "extra"], ["ui", "all"],
            ["invalid", "all", "result.json", "artifacts"],
            ["ui", "--help", "result.json", "artifacts"],
            ["ui", "all", "", "artifacts"],
            ["ui", "all", "result.json", "artifacts", "extra"],
        ))
        for entrypoint, args in cases:
            with self.subTest(entrypoint=entrypoint, args=args):
                completed, initialized = self._invoke(entrypoint, args)
                self.assertEqual(completed.returncode, 2, completed.stdout + completed.stderr)
                self.assertIn("Usage:", completed.stderr)
                self.assertFalse(initialized)

    def test_valid_argument_shapes_still_enter_the_runtime_pipeline(self):
        cases = (
            ("hatifect-ui-test", ["all"]),
            ("hatifect-ui-test", ["semantic.lifecycle"]),
            ("hatifect-smoke", ["flow.route.basic"]),
            ("hatifect-live-runner", ["ui", "all", "result.json", "artifacts"]),
            ("hatifect-live-runner", ["smoke", "flow.route.basic", "result.json", "artifacts"]),
        )
        for entrypoint, args in cases:
            with self.subTest(entrypoint=entrypoint, args=args):
                completed, initialized = self._invoke(entrypoint, args)
                self.assertEqual(completed.returncode, 97)
                self.assertTrue(initialized)

    @staticmethod
    def _invoke(entrypoint, args):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Path(directory)
            script = fixture / entrypoint
            shutil.copy2(ROOT / "tools" / entrypoint, script)
            # This process boundary substitutes the runtime pipeline, not shell parsing.
            # Any configuration/build/game initialization is observable and stops here.
            (fixture / "_common.sh").write_text(
                'touch "$HATIFECT_ENTRYPOINT_TEST_MARKER"\nexit 97\n', encoding="utf-8"
            )
            marker = fixture / "initialized"
            environment = os.environ.copy()
            environment["HATIFECT_ENTRYPOINT_TEST_MARKER"] = str(marker)
            completed = subprocess.run(
                ["bash", str(script), *args], cwd=fixture, env=environment,
                capture_output=True, text=True, check=False, timeout=5,
            )
            return completed, marker.exists()


if __name__ == "__main__":
    unittest.main()
