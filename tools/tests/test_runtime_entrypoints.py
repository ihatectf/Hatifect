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

    def test_reproduction_rejection_is_fail_closed_before_downstream_actions(self):
        cases = (
            ("hatifect-ui-test", "ui", "all"),
            ("hatifect-smoke", "smoke", "flow.route.basic"),
        )
        for entrypoint, kind, scenario in cases:
            with self.subTest(entrypoint=entrypoint):
                completed, calls, prepared, ran, run_dir = self._invoke_wrapper_pipeline(
                    entrypoint,
                    scenario,
                    source_run_id="11111111-1111-4111-8111-111111111111",
                    from_phase="runtime",
                    materialize_exit=23,
                )

                self.assertEqual(completed.returncode, 2, completed.stdout + completed.stderr)
                self.assertGreaterEqual(len(calls), 2)
                self.assertIn(
                    "reproduction.py materialize "
                    f"11111111-1111-4111-8111-111111111111 {run_dir} "
                    f"{kind} {scenario} --from runtime",
                    calls[0],
                )
                self.assertIn(
                    f"validate.py write-result BLOCKED {scenario} {run_dir}/result.json",
                    calls[1],
                )
                self.assertIn("--run-id 22222222-2222-4222-8222-222222222222", calls[1])
                self.assertIn("--assertion-id HARNESS-REPRODUCTION-CHECKPOINT", calls[1])
                self.assertIn(f"--artifact-root {run_dir}", calls[1])
                self.assertFalse(any("check-scenario" in call for call in calls))
                self.assertFalse(prepared)
                self.assertFalse(ran)

    def test_successful_reproduction_materialization_precedes_normal_pipeline(self):
        cases = (
            ("hatifect-ui-test", "ui", "all", True),
            ("hatifect-smoke", "smoke", "flow.route.basic", False),
        )
        for entrypoint, kind, scenario, expects_prepare in cases:
            with self.subTest(entrypoint=entrypoint):
                completed, calls, prepared, ran, run_dir = self._invoke_wrapper_pipeline(
                    entrypoint,
                    scenario,
                    source_run_id="11111111-1111-4111-8111-111111111111",
                )

                self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
                materialize = next(
                    index for index, call in enumerate(calls)
                    if "reproduction.py materialize" in call
                )
                scenario_check = next(
                    index for index, call in enumerate(calls)
                    if "validate.py check-scenario" in call
                )
                runner = next(
                    index for index, call in enumerate(calls)
                    if call.startswith("runner ")
                )
                self.assertLess(materialize, scenario_check)
                self.assertLess(scenario_check, runner)
                self.assertIn(
                    f"{run_dir} {kind} {scenario} --from preflight",
                    calls[materialize],
                )
                self.assertEqual(prepared, expects_prepare)
                self.assertTrue(ran)
                self.assertTrue(any("seed=31" in call for call in calls))

    def test_ordinary_wrapper_invocations_skip_reproduction(self):
        cases = (
            ("hatifect-ui-test", "all", True),
            ("hatifect-smoke", "flow.route.basic", False),
        )
        for entrypoint, scenario, expects_prepare in cases:
            with self.subTest(entrypoint=entrypoint):
                completed, calls, prepared, ran, _ = self._invoke_wrapper_pipeline(
                    entrypoint,
                    scenario,
                )

                self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
                self.assertFalse(any("reproduction.py" in call for call in calls))
                self.assertTrue(any("validate.py check-scenario" in call for call in calls))
                self.assertEqual(prepared, expects_prepare)
                self.assertTrue(ran)

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
            environment.pop("HATIFECT_REPRO_SOURCE_RUN_ID", None)
            environment.pop("HATIFECT_REPRO_FROM_PHASE", None)
            environment["HATIFECT_ENTRYPOINT_TEST_MARKER"] = str(marker)
            completed = subprocess.run(
                ["bash", str(script), *args], cwd=fixture, env=environment,
                capture_output=True, text=True, check=False, timeout=5,
            )
            return completed, marker.exists()

    @staticmethod
    def _invoke_wrapper_pipeline(
        entrypoint,
        scenario,
        *,
        source_run_id=None,
        from_phase=None,
        materialize_exit=0,
    ):
        with tempfile.TemporaryDirectory(prefix="hatifect-entrypoint-test.") as directory:
            fixture = Path(directory)
            tools = fixture / "tools"
            live_harness = tools / "live-harness"
            live_harness.mkdir(parents=True)
            shutil.copy2(ROOT / "tools" / entrypoint, tools / entrypoint)
            (tools / "_common.sh").write_text(
                'TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"\n'
                'need_cmd() { :; }\n'
                'say() { :; }\n'
                'new_runtime_run_dir() {\n'
                '  mkdir -p "$HATIFECT_TEST_RUN_DIR"\n'
                '  printf \'%s\\n\' "$HATIFECT_TEST_RUN_DIR"\n'
                '}\n'
                'validate_result_json() { return 0; }\n',
                encoding="utf-8",
            )
            for name in ("validate.py", "reproduction.py"):
                (live_harness / name).write_text("", encoding="utf-8")

            fake_bin = fixture / "bin"
            fake_bin.mkdir()
            fake_python = fake_bin / "python3"
            fake_python.write_text(
                "#!/bin/bash\n"
                "set -eu\n"
                "printf 'python %s\\n' \"$*\" >> \"$HATIFECT_TEST_CALLS\"\n"
                "case \"${1:-}:${2:-}\" in\n"
                "  *reproduction.py:materialize) printf '31\\n'; exit \"$HATIFECT_TEST_MATERIALIZE_EXIT\" ;;\n"
                "  *validate.py:check-scenario) exit 0 ;;\n"
                "  *validate.py:validate-result) printf 'PASS\\n'; exit 0 ;;\n"
                "  *validate.py:write-result) exit 0 ;;\n"
                "  *) exit 96 ;;\n"
                "esac\n",
                encoding="utf-8",
            )
            fake_python.chmod(0o700)

            prepare_marker = fixture / "prepared"
            prepare = tools / "hatifect-live-prepare"
            prepare.write_text(
                "#!/bin/bash\n"
                "set -eu\n"
                "printf 'prepare\\n' >> \"$HATIFECT_TEST_CALLS\"\n"
                "touch \"$HATIFECT_TEST_PREPARE_MARKER\"\n",
                encoding="utf-8",
            )
            prepare.chmod(0o700)

            runner_marker = fixture / "ran"
            runner = tools / "hatifect-live-runner"
            runner.write_text(
                "#!/bin/bash\n"
                "set -eu\n"
                "printf 'runner %s seed=%s\\n' \"$*\" \"${HATIFECT_TEST_SEED:-}\" >> \"$HATIFECT_TEST_CALLS\"\n"
                "touch \"$HATIFECT_TEST_RUNNER_MARKER\"\n"
                "mkdir -p \"$(dirname \"$3\")\"\n"
                "printf '{}\\n' > \"$3\"\n",
                encoding="utf-8",
            )
            runner.chmod(0o700)

            calls_path = fixture / "calls.txt"
            run_dir = fixture / "artifacts" / "runtime" / "22222222-2222-4222-8222-222222222222"
            environment = os.environ.copy()
            environment.pop("HATIFECT_REPRO_SOURCE_RUN_ID", None)
            environment.pop("HATIFECT_REPRO_FROM_PHASE", None)
            environment.update(
                {
                    "PATH": f"{fake_bin}:{environment['PATH']}",
                    "HATIFECT_TEST_CALLS": str(calls_path),
                    "HATIFECT_TEST_RUN_DIR": str(run_dir),
                    "HATIFECT_TEST_PREPARE_MARKER": str(prepare_marker),
                    "HATIFECT_TEST_RUNNER_MARKER": str(runner_marker),
                    "HATIFECT_TEST_MATERIALIZE_EXIT": str(materialize_exit),
                }
            )
            if source_run_id is not None:
                environment["HATIFECT_REPRO_SOURCE_RUN_ID"] = source_run_id
            if from_phase is not None:
                environment["HATIFECT_REPRO_FROM_PHASE"] = from_phase

            completed = subprocess.run(
                ["bash", str(tools / entrypoint), scenario],
                cwd=fixture,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
                timeout=5,
            )
            calls = (
                calls_path.read_text(encoding="utf-8").splitlines()
                if calls_path.exists()
                else []
            )
            return (
                completed,
                calls,
                prepare_marker.exists(),
                runner_marker.exists(),
                str(run_dir),
            )


if __name__ == "__main__":
    unittest.main()
