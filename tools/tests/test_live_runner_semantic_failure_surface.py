import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "tools" / "hatifect-live-runner"
VALIDATOR_PATH = ROOT / "tools" / "live-harness" / "validate.py"
VALIDATOR_SPEC = importlib.util.spec_from_file_location(
    "hatifect_live_runner_behavior_validate", VALIDATOR_PATH
)
assert VALIDATOR_SPEC is not None and VALIDATOR_SPEC.loader is not None
VALIDATOR = importlib.util.module_from_spec(VALIDATOR_SPEC)
VALIDATOR_SPEC.loader.exec_module(VALIDATOR)


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

    def test_runtime_and_semantic_failures_preserve_runtime_root_and_exit(self) -> None:
        completed, result, envelope, events = self._run_semantic_failure(
            runtime_status="FAIL",
            runtime_exit=9,
        )

        self.assertEqual(9, completed.returncode, completed.stderr)
        self.assertEqual("FAIL", result["status"])
        self.assertEqual("runtime.stub.failure", envelope["root_failure"]["id"])
        self.assertIn(
            "HARNESS-SEMANTIC-TEST-AGENT",
            [record["id"] for record in envelope["additional_failures"]],
        )
        self._assert_final_semantic_evidence(result, events)

    def test_semantic_only_failure_becomes_blocked_root_and_exit_two(self) -> None:
        completed, result, envelope, events = self._run_semantic_failure(
            runtime_status="PASS",
            runtime_exit=0,
        )

        self.assertEqual(2, completed.returncode, completed.stderr)
        self.assertEqual("BLOCKED", result["status"])
        self.assertEqual(
            "HARNESS-SEMANTIC-TEST-AGENT", envelope["root_failure"]["id"]
        )
        self.assertEqual([], envelope["additional_failures"])
        self._assert_final_semantic_evidence(result, events)

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

    def _run_semantic_failure(self, *, runtime_status: str, runtime_exit: int):
        runtime_root = ROOT / "artifacts" / "runtime"
        runtime_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=runtime_root) as artifact_directory, \
                tempfile.TemporaryDirectory() as stub_directory:
            artifact = Path(artifact_directory)
            result_path = artifact / "result.json"
            python_stub = Path(stub_directory) / "python3"
            python_stub.write_text(
                textwrap.dedent(
                    f"""\
                    #!{sys.executable}
                    import os
                    import subprocess
                    import sys
                    from pathlib import Path

                    real_python = os.environ["HATIFECT_TEST_REAL_PYTHON"]
                    validator = os.environ["HATIFECT_TEST_VALIDATOR"]
                    target = Path(sys.argv[1]).name if len(sys.argv) > 1 else ""
                    if target == "validate.py" or sys.argv[1:2] == ["-c"]:
                        os.execv(real_python, [real_python, *sys.argv[1:]])
                    if target == "semantic-test-agent.py":
                        raise SystemExit(int(os.environ["HATIFECT_TEST_SEMANTIC_EXIT"]))
                    if target == "save_provisioning.py":
                        print("/tmp/hatifect-controlled-save")
                        raise SystemExit(0)
                    if target == "user_session_runtime.py":
                        arguments = sys.argv[2:]
                        if arguments[0] == "preflight":
                            raise SystemExit(0)
                        def option(name):
                            return arguments[arguments.index(name) + 1]
                        status = os.environ["HATIFECT_TEST_RUNTIME_STATUS"]
                        command = [
                            real_python,
                            validator,
                            "write-result",
                            status,
                            option("--scenario"),
                            option("--result"),
                            "controlled runtime outcome",
                            "--run-id",
                            Path(option("--artifact-directory")).name,
                            "--assertion-id",
                            (
                                "runtime.stub.failure"
                                if status == "FAIL"
                                else "runtime.stub.success"
                            ),
                            "--artifact-root",
                            option("--artifact-directory"),
                        ]
                        published = subprocess.run(command, check=False)
                        if published.returncode != 0:
                            raise SystemExit(published.returncode)
                        raise SystemExit(int(os.environ["HATIFECT_TEST_RUNTIME_EXIT"]))
                    os.execv(real_python, [real_python, *sys.argv[1:]])
                    """
                ),
                encoding="utf-8",
            )
            python_stub.chmod(0o700)
            uname_stub = Path(stub_directory) / "uname"
            uname_stub.write_text("#!/bin/sh\nprintf 'Darwin\\n'\n", encoding="utf-8")
            uname_stub.chmod(0o700)
            environment = os.environ.copy()
            environment.update({
                "PATH": f"{stub_directory}{os.pathsep}{environment['PATH']}",
                "HATIFECT_TEST_REAL_PYTHON": sys.executable,
                "HATIFECT_TEST_VALIDATOR": str(VALIDATOR_PATH),
                "HATIFECT_TEST_RUNTIME_STATUS": runtime_status,
                "HATIFECT_TEST_RUNTIME_EXIT": str(runtime_exit),
                "HATIFECT_TEST_SEMANTIC_EXIT": "7",
                "HATIFECT_SMAPI_TEST_ROOT": str(ROOT / ".smapi-test" / "isolated"),
            })
            completed = subprocess.run(
                [
                    str(RUNNER),
                    "smoke",
                    "flow.ui.player.input",
                    str(result_path),
                    str(artifact),
                ],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
                timeout=20,
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            envelope = VALIDATOR.validate_failure_artifacts(result_path, result)
            events = VALIDATOR.read_semantic_events(
                artifact,
                expected_scenario="flow.ui.player.input",
                expected_run_id=artifact.name,
            )
            return completed, result, envelope, events

    def _assert_final_semantic_evidence(self, result, events) -> None:
        event_names = [event["event"] for event in events]
        self.assertIn("SemanticAgent.Failed", event_names)
        self.assertEqual("Scenario.Completed", event_names[-1])
        published = [event for event in events if event["event"] == "Result.Published"]
        self.assertEqual(
            VALIDATOR._result_fingerprint(result),
            published[-1]["fields"]["result_fingerprint"],
        )


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
