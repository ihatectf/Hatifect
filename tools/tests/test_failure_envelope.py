from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR_PATH = ROOT / "tools" / "live-harness" / "validate.py"
SPEC = importlib.util.spec_from_file_location("hatifect_failure_envelope", VALIDATOR_PATH)
assert SPEC is not None and SPEC.loader is not None
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)

TIMESTAMP = "2026-09-13T00:00:00Z"


class FailureEnvelopeTests(unittest.TestCase):
    def test_ordinary_assertion_failure_has_exactly_one_root(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "observed false",
                run_id="test-run",
                assertions=[self._assertion(
                    "runtime.boot.loaded",
                    "FAIL",
                    "Runtime boot completes.",
                    "observed false",
                )],
                failure_timestamp=TIMESTAMP,
            )

            envelope = self._read_envelope(result_path)

        self.assertEqual(envelope["failure_class"], "ASSERTION_FAILURE")
        self.assertEqual(envelope["root_failure"]["classification"], "ROOT_FAILURE")
        self.assertEqual(envelope["root_failure"]["id"], "runtime.boot.loaded")
        self.assertEqual(envelope["cascade_records"], [])
        self.assertEqual(envelope["cleanup_failures"], [])

    def test_runtime_executor_failure_is_classified_without_reading_raw_log(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "smapi.log").write_text("large runtime log\n", encoding="utf-8")
            (root / "diagnostics").mkdir()
            (root / "diagnostics" / "executor-failure.json").write_text(
                json.dumps({
                    "exceptionType": "RuntimeError",
                    "message": "worker transport failed",
                }),
                encoding="utf-8",
            )
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "BLOCKED",
                "runtime.boot",
                "SMAPI exited before acceptance evidence.",
                run_id="test-run",
                assertions=[self._assertion(
                    "HARNESS-RESULT-MISSING",
                    "BLOCKED",
                    "The executor publishes result.json.",
                    "No result.json was produced.",
                )],
                artifacts=[
                    {"type": "diagnostic", "path": "diagnostics/executor-failure.json"},
                    {"type": "smapi-log", "path": "smapi.log"},
                ],
                failure_timestamp=TIMESTAMP,
            )

            envelope = self._read_envelope(result_path)
            summary = (root / "failure-summary.txt").read_text(encoding="utf-8")

        self.assertEqual(envelope["phase"], "executor")
        self.assertEqual(envelope["failure_class"], "RUNTIME_EXECUTOR_FAILURE")
        self.assertEqual(envelope["causal_component"], "runtime-executor")
        self.assertEqual(envelope["actual"], "RuntimeError: worker transport failed")
        self.assertIn(
            "diagnostics/executor-failure.json",
            envelope["relevant_artifacts"],
        )
        self.assertIn("smapi.log", envelope["relevant_artifacts"])
        self.assertIn("ROOT_FAILURE [RUNTIME_EXECUTOR_FAILURE]", summary)
        self.assertNotIn("large runtime log", summary)

    def test_dependent_failures_become_deterministic_cascade_records(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "semantic.lifecycle",
                "multiple checks failed",
                run_id="test-run",
                assertions=[
                    self._assertion("semantic.lifecycle.visual", "FAIL", "visible", "missing"),
                    self._assertion("semantic.lifecycle.focus", "FAIL", "focused", "unavailable"),
                    self._assertion("semantic.lifecycle.close-reopen", "FAIL", "reopened", "not attempted"),
                ],
                failure_timestamp=TIMESTAMP,
                cascade_dependencies={
                    "semantic.lifecycle.focus": "semantic.lifecycle.visual",
                    "semantic.lifecycle.close-reopen": "semantic.lifecycle.visual",
                },
            )

            envelope = self._read_envelope(result_path)

        self.assertEqual(envelope["root_failure"]["id"], "semantic.lifecycle.visual")
        self.assertEqual(
            [record["id"] for record in envelope["cascade_records"]],
            ["semantic.lifecycle.focus", "semantic.lifecycle.close-reopen"],
        )
        self.assertTrue(all(
            record["classification"] == "CASCADE_SKIPPED"
            and record["root_failure_id"] == "semantic.lifecycle.visual"
            and record["original_status"] == "FAIL"
            for record in envelope["cascade_records"]
        ))

    def test_product_restoration_assertions_are_not_harness_cleanup(self) -> None:
        cases = (
            ("semantic.observation", "semantic.observation.restored"),
            ("flow.ui.names", "flow.ui.names.restored"),
            ("flow.ui.player.en-075", "flow.ui.player.en-075.profile-restored"),
        )
        for scenario, assertion_id in cases:
            with self.subTest(assertion_id=assertion_id), tempfile.TemporaryDirectory() as directory:
                result_path = Path(directory) / "result.json"
                HARNESS.write_result(
                    result_path,
                    "FAIL",
                    scenario,
                    "restoration observation failed",
                    run_id="test-run",
                    assertions=[self._assertion(
                        assertion_id,
                        "FAIL",
                        "owners retire",
                        "source retained",
                    )],
                    failure_timestamp=TIMESTAMP,
                )
                envelope = self._read_envelope(result_path)

                self.assertEqual(envelope["root_failure"]["id"], assertion_id)
                self.assertEqual(envelope["failure_class"], "ASSERTION_FAILURE")
                self.assertEqual(envelope["cleanup_failures"], [])

    def test_product_failed_cleanup_assertion_is_not_harness_cleanup(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "semantic.actions.reload",
                "reload cleanup observation failed",
                run_id="test-run",
                assertions=[self._assertion(
                    "semantic.actions.reload.hud-hide.failed-cleanup",
                    "FAIL",
                    "surface retired",
                    "surface remained visible",
                )],
                failure_timestamp=TIMESTAMP,
            )
            envelope = self._read_envelope(result_path)

        self.assertEqual(
            envelope["root_failure"]["id"],
            "semantic.actions.reload.hud-hide.failed-cleanup",
        )
        self.assertEqual(envelope["failure_class"], "ASSERTION_FAILURE")
        self.assertEqual(envelope["cleanup_failures"], [])

    def test_independent_failures_remain_visible_without_cascade_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "semantic.lifecycle",
                "independent checks failed",
                run_id="test-run",
                assertions=[
                    self._assertion("semantic.lifecycle.visual", "FAIL", "visible", "missing"),
                    self._assertion("semantic.lifecycle.focus", "FAIL", "focused", "unavailable"),
                ],
                failure_timestamp=TIMESTAMP,
            )
            envelope = self._read_envelope(result_path)

        self.assertEqual(envelope["cascade_records"], [])
        self.assertEqual(
            [record["id"] for record in envelope["additional_failures"]],
            ["semantic.lifecycle.focus"],
        )
        self.assertEqual(
            envelope["additional_failures"][0]["classification"],
            "ADDITIONAL_FAILURE",
        )

    def test_cleanup_failure_after_root_remains_separate_and_subordinate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "flow.route.basic",
                "delivery assertion failed",
                run_id="test-run",
                assertions=[self._assertion(
                    "flow.route.basic.delivery",
                    "FAIL",
                    "cargo delivered",
                    "cargo retained",
                )],
                failure_timestamp=TIMESTAMP,
            )

            HARNESS.record_cleanup_failure(
                result_path,
                failure_id="HARNESS-SAVE-CLEANUP",
                message="owned save could not be removed",
                causal_component="save-provisioning",
                timestamp="2026-09-13T00:00:01Z",
            )
            envelope = self._read_envelope(result_path)

        self.assertEqual(envelope["root_failure"]["id"], "flow.route.basic.delivery")
        self.assertEqual(len(envelope["cleanup_failures"]), 1)
        cleanup = envelope["cleanup_failures"][0]
        self.assertEqual(cleanup["classification"], "CLEANUP_FAILURE")
        self.assertEqual(cleanup["root_failure_id"], "flow.route.basic.delivery")
        self.assertEqual(cleanup["causal_component"], "save-provisioning")

    def test_cleanup_only_failure_becomes_root_without_losing_its_component(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            diagnostics = root / "diagnostics"
            diagnostics.mkdir()
            (diagnostics / "save-provisioning.json").write_text(
                "{}\n",
                encoding="utf-8",
            )
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "flow.route.basic",
                "passed",
                run_id="test-run",
                assertions=[self._assertion(
                    "flow.route.basic.delivery",
                    "PASS",
                    "delivered",
                    "delivered",
                )],
                failure_timestamp=TIMESTAMP,
            )

            envelope = HARNESS.record_cleanup_failure(
                result_path,
                failure_id="HARNESS-SAVE-CLEANUP",
                message="owned save could not be removed",
                causal_component="save-provisioning",
                artifact="diagnostics/save-provisioning.json",
                timestamp="2026-09-13T00:00:01Z",
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))

        self.assertEqual(result["status"], "BLOCKED")
        self.assertEqual(envelope["root_failure"]["id"], "HARNESS-SAVE-CLEANUP")
        self.assertEqual(envelope["failure_class"], "CLEANUP_FAILURE")
        self.assertEqual(envelope["causal_component"], "save-provisioning")
        self.assertIn(
            "diagnostics/save-provisioning.json",
            envelope["relevant_artifacts"],
        )

    def test_successful_scenario_keeps_existing_result_contract_without_failure_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "passed",
                run_id="test-run",
                assertions=[self._assertion(
                    "runtime.boot.loaded",
                    "PASS",
                    "loaded",
                    "loaded",
                )],
                failure_timestamp=TIMESTAMP,
            )

            result = json.loads(result_path.read_text(encoding="utf-8"))
            validated = HARNESS.validate_failure_artifacts(result_path, result)

        self.assertEqual(result["protocolVersion"], 1)
        self.assertEqual(result["status"], "PASS")
        self.assertIsNone(validated)
        self.assertFalse((root / "failure.json").exists())
        self.assertFalse((root / "failure-summary.txt").exists())

    def test_successful_rewrite_removes_previous_failure_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = self._write_failed_result(root)
            self.assertTrue((root / "failure.json").is_file())
            self.assertTrue((root / "failure-summary.txt").is_file())

            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "passed",
                run_id="test-run",
                assertions=[self._assertion(
                    "runtime.boot.loaded",
                    "PASS",
                    "loaded",
                    "loaded",
                )],
                failure_timestamp=TIMESTAMP,
            )

        self.assertFalse((root / "failure.json").exists())
        self.assertFalse((root / "failure-summary.txt").exists())

    def test_strict_validator_rejects_oversized_failure_summary_without_loading_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = self._write_failed_result(Path(directory))
            result = json.loads(result_path.read_text(encoding="utf-8"))
            (result_path.parent / "failure-summary.txt").write_text(
                "x" * (HARNESS.MAX_FAILURE_SUMMARY_CHARACTERS + 1),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(
                HARNESS.HarnessError,
                "bounded readable failure-summary",
            ):
                HARNESS.validate_failure_artifacts(result_path, result)

    def test_serialization_is_byte_stable_for_identical_evidence(self) -> None:
        payloads = []
        summaries = []
        for _ in range(2):
            with tempfile.TemporaryDirectory() as directory:
                result_path = Path(directory) / "result.json"
                HARNESS.write_result(
                    result_path,
                    "FAIL",
                    "runtime.boot",
                    "failure",
                    run_id="stable-run",
                    assertions=[
                        self._assertion("runtime.boot.loaded", "FAIL", "loaded", "missing"),
                        self._assertion("runtime.boot.ready", "FAIL", "ready", "not attempted"),
                    ],
                    failure_timestamp=TIMESTAMP,
                )
                payloads.append((result_path.parent / "failure.json").read_bytes())
                summaries.append(
                    (result_path.parent / "failure-summary.txt").read_bytes()
                )

        self.assertEqual(payloads[0], payloads[1])
        self.assertEqual(summaries[0], summaries[1])

    def test_maximum_unicode_root_context_round_trips_with_literal_utf8(self) -> None:
        text = "\U0001f600" * HARNESS.MAX_FAILURE_TEXT
        payloads = []
        for _ in range(2):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                result_path = root / "result.json"
                HARNESS.write_result(
                    result_path,
                    "FAIL",
                    "runtime.boot",
                    text,
                    run_id=text,
                    assertions=[self._assertion(text, "FAIL", text, text)],
                    failure_timestamp=TIMESTAMP,
                    failure_phase=text,
                    failure_class=text,
                    causal_component=text,
                )

                result = json.loads(result_path.read_text(encoding="utf-8"))
                failure_path = root / "failure.json"
                summary_path = root / "failure-summary.txt"
                envelope = HARNESS.validate_failure_artifacts(result_path, result)
                payload = failure_path.read_bytes()
                self.assertTrue(result_path.is_file())
                self.assertTrue(failure_path.is_file())
                self.assertTrue(summary_path.is_file())
                self.assertLessEqual(
                    failure_path.stat().st_size,
                    HARNESS.MAX_FAILURE_ENVELOPE_BYTES,
                )
                self.assertIn(text.encode("utf-8"), payload)
                self.assertEqual(envelope["root_failure"]["id"], text)
                self.assertEqual(
                    envelope["result_fingerprint"],
                    HARNESS._result_fingerprint(result),
                )
                payloads.append(payload)

        self.assertEqual(payloads[0], payloads[1])

    def test_compaction_preserves_unique_supplementary_assertion_ids(self) -> None:
        prefix = "cascade-" + "c" * (
            HARNESS.MAX_FAILURE_TEXT - len("cascade-") - 2
        )
        cascade_ids = [f"{prefix}{index:02d}" for index in range(
            HARNESS.MAX_CASCADE_RECORDS
        )]
        text = "x" * HARNESS.MAX_FAILURE_TEXT
        root_id = "runtime.boot.loaded"
        assertions = [
            self._assertion(root_id, "FAIL", "loaded", "missing"),
            *[
                self._assertion(assertion_id, "FAIL", text, text)
                for assertion_id in cascade_ids
            ],
        ]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "failure",
                run_id="collision-run",
                assertions=assertions,
                failure_timestamp=TIMESTAMP,
                cascade_dependencies={
                    assertion_id: root_id for assertion_id in cascade_ids
                },
            )

            result = json.loads(result_path.read_text(encoding="utf-8"))
            envelope = HARNESS.validate_failure_artifacts(result_path, result)
            records = envelope["cascade_records"]
            retained = [
                record for record in records
                if record["id"] != "HARNESS-CASCADE-REMAINDER"
            ]
            retained_ids = [record["id"] for record in retained]

            self.assertLessEqual(
                (root / "failure.json").stat().st_size,
                HARNESS.MAX_FAILURE_ENVELOPE_BYTES,
            )
            self.assertEqual(len(retained_ids), len(set(retained_ids)))
            self.assertTrue(set(retained_ids).issubset(set(cascade_ids)))
            self.assertTrue(all(
                any(assertion["id"] == record["id"] for assertion in result["assertions"])
                for record in retained
            ))
            if len(retained) < len(cascade_ids):
                self.assertEqual(records[-1]["id"], "HARNESS-CASCADE-REMAINDER")
            else:
                self.assertEqual(retained_ids, cascade_ids)

    def test_explicit_cascade_context_is_bounded_without_changing_legacy_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            assertions = [
                self._assertion(f"runtime.boot.check-{index}", "FAIL", "pass", "failed")
                for index in range(40)
            ]
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "many failures",
                run_id="bounded-run",
                assertions=assertions,
                failure_timestamp=TIMESTAMP,
                cascade_dependencies={
                    assertion["id"]: assertions[0]["id"]
                    for assertion in assertions[1:]
                },
            )

            envelope = self._read_envelope(result_path)
            result = json.loads(result_path.read_text(encoding="utf-8"))

        self.assertEqual(len(envelope["cascade_records"]), HARNESS.MAX_CASCADE_RECORDS)
        self.assertEqual(
            envelope["cascade_records"][-1]["id"],
            "HARNESS-CASCADE-REMAINDER",
        )
        self.assertIn("8 additional", envelope["cascade_records"][-1]["actual"])
        self.assertEqual(len(result["assertions"]), 40)

    def test_maximum_envelope_context_round_trips_within_shared_byte_budget(self) -> None:
        text = "x" * HARNESS.MAX_FAILURE_TEXT
        root_id = "root-" + "r" * (HARNESS.MAX_FAILURE_TEXT - len("root-"))
        cascade_assertions = [
            self._assertion(
                f"cascade-{index}-" + "c" * (
                    HARNESS.MAX_FAILURE_TEXT - len(f"cascade-{index}-")
                ),
                "FAIL",
                text,
                text,
            )
            for index in range(HARNESS.MAX_CASCADE_RECORDS)
        ]
        additional_assertions = [
            self._assertion(
                f"additional-{index}-" + "a" * (
                    HARNESS.MAX_FAILURE_TEXT - len(f"additional-{index}-")
                ),
                "FAIL",
                text,
                text,
            )
            for index in range(HARNESS.MAX_ADDITIONAL_FAILURES)
        ]
        assertions = [
            self._assertion(root_id, "FAIL", text, text),
            *cascade_assertions,
            *additional_assertions,
        ]
        artifacts = [
            "artifact-" + str(index) + "-" + "p" * (
                HARNESS.MAX_FAILURE_TEXT - len(f"artifact-{index}-")
            )
            for index in range(HARNESS.MAX_RELEVANT_ARTIFACTS - 1)
        ]
        environment = {
            key: (2_147_483_647 if value is int else text)
            for key, value in HARNESS.ENVIRONMENT_SUMMARY_TYPES.items()
        }
        cleanup_failures = [
            {
                "id": sorted(HARNESS.HARNESS_CLEANUP_ASSERTION_IDS)[
                    index % len(HARNESS.HARNESS_CLEANUP_ASSERTION_IDS)
                ],
                "expected": text,
                "actual": text,
                "message": text,
                "causal_component": text,
                "timestamp": TIMESTAMP,
            }
            for index in range(HARNESS.MAX_CLEANUP_FAILURES)
        ]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            with mock.patch.object(
                HARNESS,
                "_relevant_artifacts",
                return_value=["result.json", *artifacts],
            ), mock.patch.object(
                HARNESS,
                "_environment_summary",
                return_value=environment,
            ):
                HARNESS.write_result(
                    result_path,
                    "FAIL",
                    "runtime.boot",
                    text,
                    run_id=text,
                    assertions=assertions,
                    failure_timestamp=TIMESTAMP,
                    failure_phase=text,
                    failure_class=text,
                    causal_component=text,
                    cleanup_failures=cleanup_failures,
                    cascade_dependencies={
                        assertion["id"]: root_id
                        for assertion in cascade_assertions
                    },
                )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            failure_path = root / "failure.json"
            envelope = HARNESS.validate_failure_artifacts(result_path, result)
            self.assertLessEqual(
                failure_path.stat().st_size,
                HARNESS.MAX_FAILURE_ENVELOPE_BYTES,
            )
            self.assertEqual(envelope["root_failure"]["id"], root_id)
            self.assertEqual(
                envelope["result_fingerprint"],
                HARNESS._result_fingerprint(result),
            )
            self.assertTrue(all(
                len(record["actual"]) <= HARNESS.MAX_FAILURE_RECORD_CONTEXT_TEXT
                for record in envelope["cascade_records"]
            ))

    def test_strict_validator_rejects_stale_sidecar_with_changed_root(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "first root",
                run_id="test-run",
                assertions=[self._assertion(
                    "runtime.boot.loaded",
                    "FAIL",
                    "loaded",
                    "missing",
                )],
                failure_timestamp=TIMESTAMP,
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            result["assertions"] = [self._assertion(
                "runtime.boot.ready",
                "FAIL",
                "ready",
                "not ready",
            )]
            result_path.write_text(json.dumps(result), encoding="utf-8")
            strict = subprocess.run(
                [
                    sys.executable,
                    str(VALIDATOR_PATH),
                    "validate-result",
                    str(result_path),
                    "runtime.boot",
                    "--require-failure-envelope",
                ],
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(strict.returncode, 2)
        self.assertIn("fingerprint conflicts", strict.stderr)

    def test_top_level_root_contradiction_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = self._write_failed_result(Path(directory))
            envelope = self._read_envelope(result_path)
            envelope["message"] = "contradictory message"

        with self.assertRaisesRegex(HARNESS.HarnessError, "conflicts with its root"):
            HARNESS.validate_failure_envelope(envelope)

    def test_text_field_longer_than_bound_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = self._write_failed_result(Path(directory))
            envelope = self._read_envelope(result_path)
            oversized = "x" * (HARNESS.MAX_FAILURE_TEXT + 1)
            envelope["actual"] = oversized
            envelope["message"] = oversized
            envelope["root_failure"]["actual"] = oversized
            envelope["root_failure"]["message"] = oversized

        with self.assertRaisesRegex(HARNESS.HarnessError, "no longer than"):
            HARNESS.validate_failure_envelope(envelope)

    def test_unknown_or_oversized_environment_summary_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = self._write_failed_result(Path(directory))
            envelope = self._read_envelope(result_path)
            unknown = json.loads(json.dumps(envelope))
            unknown["environment_summary"]["unapproved"] = "value"
            oversized = json.loads(json.dumps(envelope))
            oversized["environment_summary"]["platform"] = "x" * (
                HARNESS.MAX_FAILURE_TEXT + 1
            )

        with self.assertRaisesRegex(HARNESS.HarnessError, "unsupported fields"):
            HARNESS.validate_failure_envelope(unknown)
        with self.assertRaisesRegex(HARNESS.HarnessError, "environment_summary platform"):
            HARNESS.validate_failure_envelope(oversized)

    def test_standalone_v1_failure_validation_remains_backward_compatible(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            result_path.write_text(json.dumps({
                "protocolVersion": 1,
                "runId": "legacy-run",
                "scenario": "runtime.boot",
                "status": "BLOCKED",
                "durationMs": 1,
                "assertions": [self._assertion(
                    "HARNESS-EXECUTION",
                    "BLOCKED",
                    "complete",
                    "blocked",
                )],
                "exceptions": [],
                "artifacts": [],
            }), encoding="utf-8")

            compatible = subprocess.run(
                [
                    sys.executable,
                    str(VALIDATOR_PATH),
                    "validate-result",
                    str(result_path),
                    "runtime.boot",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            strict = subprocess.run(
                [
                    sys.executable,
                    str(VALIDATOR_PATH),
                    "validate-result",
                    str(result_path),
                    "runtime.boot",
                    "--require-failure-envelope",
                ],
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(compatible.returncode, 0, compatible.stderr)
        self.assertEqual(strict.returncode, 2)
        self.assertIn("no bounded failure.json", strict.stderr)

    @staticmethod
    def _assertion(assertion_id: str, status: str, expected: str, actual: str):
        return {
            "id": assertion_id,
            "status": status,
            "subject": "scenario",
            "expected": expected,
            "actual": actual,
        }

    def _write_failed_result(self, root: Path) -> Path:
        result_path = root / "result.json"
        HARNESS.write_result(
            result_path,
            "FAIL",
            "runtime.boot",
            "missing",
            run_id="test-run",
            assertions=[self._assertion(
                "runtime.boot.loaded",
                "FAIL",
                "loaded",
                "missing",
            )],
            failure_timestamp=TIMESTAMP,
        )
        return result_path

    @staticmethod
    def _read_envelope(result_path: Path):
        result = json.loads(result_path.read_text(encoding="utf-8"))
        envelope = json.loads(
            (result_path.parent / "failure.json").read_text(encoding="utf-8")
        )
        HARNESS.validate_failure_envelope(envelope)
        HARNESS.validate_failure_artifacts(result_path, result)
        return envelope


if __name__ == "__main__":
    unittest.main()
