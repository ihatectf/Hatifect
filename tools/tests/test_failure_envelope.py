from __future__ import annotations

import importlib.util
import json
import copy
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

    def test_new_generation_uses_closed_phase_mapping(self) -> None:
        cases = (
            ("HARNESS-ENV-SMAPI", "preflight"),
            ("HARNESS-PREFLIGHT-SMAPI-RUNTIME", "preflight"),
            ("HARNESS-REPRODUCTION-CHECKPOINT", "preflight"),
            ("HARNESS-PREPARE", "prepare"),
            ("HARNESS-SAVE-BOOTSTRAP-INVALID", "prepare"),
            ("HARNESS-PROCESS-BOOT", "runtime"),
            ("HARNESS-REPORT-VALIDATION", "validation"),
            ("HARNESS-OPTIONS-RESTORE", "cleanup"),
            ("product.host.assertion", "runtime"),
        )
        for assertion_id, expected_phase in cases:
            with self.subTest(assertion_id=assertion_id):
                with tempfile.TemporaryDirectory() as directory:
                    result_path = Path(directory) / "result.json"
                    HARNESS.write_result(
                        result_path,
                        "BLOCKED" if assertion_id.startswith("HARNESS-") else "FAIL",
                        "runtime.boot",
                        "observed failure",
                        run_id="phase-run",
                        assertions=[self._assertion(
                            assertion_id,
                            "BLOCKED" if assertion_id.startswith("HARNESS-") else "FAIL",
                            "expected",
                            "observed failure",
                        )],
                        failure_timestamp=TIMESTAMP,
                    )
                    envelope = self._read_envelope(result_path)

                self.assertEqual(envelope["phase"], expected_phase)
                self.assertEqual(envelope["root_failure"]["phase"], expected_phase)
                for collection in (
                    "cascade_records",
                    "additional_failures",
                    "cleanup_failures",
                ):
                    self.assertTrue(all(
                        record["phase"] in HARNESS.FAILURE_PHASES
                        for record in envelope[collection]
                    ))

    def test_generation_rejects_legacy_or_unknown_phase_override(self) -> None:
        for invalid_phase in ("input", "scenario", "executor", "unknown"):
            with self.subTest(invalid_phase=invalid_phase):
                with tempfile.TemporaryDirectory() as directory:
                    with self.assertRaisesRegex(
                        HARNESS.HarnessError,
                        "Generated failure phase",
                    ):
                        HARNESS.write_result(
                            Path(directory) / "result.json",
                            "FAIL",
                            "runtime.boot",
                            "observed failure",
                            failure_phase=invalid_phase,
                        )

    def test_root_context_override_can_confirm_but_not_redefine_mapping(self) -> None:
        canonical = {
            "failure_phase": "runtime",
            "failure_class": "ASSERTION_FAILURE",
            "causal_component": "scenario",
        }
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "failed",
                run_id="canonical-context-run",
                assertions=[self._assertion("runtime.boot.loaded", "FAIL", "boot", "failed")],
                failure_timestamp=TIMESTAMP,
                **canonical,
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            HARNESS.validate_failure_artifacts(result_path, result)

        for field, value in (
            ("failure_phase", "prepare"),
            ("failure_class", "PREPARATION_FAILURE"),
            ("causal_component", "custom-preparer"),
        ):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                overrides = dict(canonical)
                overrides[field] = value
                result_path = Path(directory) / "result.json"
                with self.assertRaisesRegex(HARNESS.HarnessError, "canonical assertion mapping"):
                    HARNESS.write_result(
                        result_path,
                        "FAIL",
                        "runtime.boot",
                        "failed",
                        assertions=[self._assertion(
                            "runtime.boot.loaded", "FAIL", "boot", "failed"
                        )],
                        **overrides,
                    )
                self.assertFalse(result_path.exists())
                self.assertFalse((result_path.parent / "failure.json").exists())
                self.assertFalse(
                    (result_path.parent / HARNESS.SEMANTIC_EVENT_FILE_NAME).exists()
                )

    def test_historical_v2_and_v3_phase_names_remain_readable(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = self._write_failed_result(Path(directory))
            current = self._read_envelope(result_path)

        for format_version in (
            HARNESS.LEGACY_FAILURE_ENVELOPE_FORMAT_VERSION,
            HARNESS.FAILURE_ENVELOPE_FORMAT_VERSION,
        ):
            for legacy_phase in sorted(HARNESS.LEGACY_FAILURE_PHASES):
                with self.subTest(format_version=format_version, phase=legacy_phase):
                    historical = copy.deepcopy(current)
                    historical["format_version"] = format_version
                    if format_version == HARNESS.LEGACY_FAILURE_ENVELOPE_FORMAT_VERSION:
                        historical.pop("semantic_event_tail", None)
                    historical["phase"] = legacy_phase
                    historical["root_failure"]["phase"] = legacy_phase
                    for collection in (
                        "cascade_records",
                        "additional_failures",
                        "cleanup_failures",
                    ):
                        for record in historical[collection]:
                            record["phase"] = legacy_phase
                    HARNESS.validate_failure_envelope(historical)

    def test_generated_supplementary_records_keep_closed_phases(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "multiple failures",
                run_id="phase-record-run",
                assertions=[
                    self._assertion("runtime.boot.root", "FAIL", "root", "failed"),
                    self._assertion("runtime.boot.dependent", "BLOCKED", "dependent", "skipped"),
                ],
                failure_timestamp=TIMESTAMP,
                cascade_dependencies={"runtime.boot.dependent": "runtime.boot.root"},
                cleanup_failures=[{
                    "id": "HARNESS-SAVE-CLEANUP",
                    "status": "FAIL",
                    "actual": "cleanup failed",
                }],
            )
            envelope = self._read_envelope(result_path)

        records = [
            envelope["root_failure"],
            *envelope["cascade_records"],
            *envelope["additional_failures"],
            *envelope["cleanup_failures"],
        ]
        self.assertTrue(records)
        self.assertTrue(all(record["phase"] in HARNESS.FAILURE_PHASES for record in records))

    def test_reproduction_artifact_is_typed_and_prioritized_after_preflight(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("result.json", "preflight.json", "reproduction.json"):
                (root / name).write_text("{}\n", encoding="utf-8")

            artifacts = HARNESS.collect_artifacts(root)
            relevant = HARNESS._relevant_artifacts({"artifacts": []}, root)

        by_path = {artifact["path"]: artifact["type"] for artifact in artifacts}
        self.assertEqual(by_path["reproduction.json"], "reproduction")
        self.assertLess(relevant.index("preflight.json"), relevant.index("reproduction.json"))

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

        self.assertEqual(envelope["phase"], "runtime")
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
                    self._assertion("semantic.lifecycle.focus", "BLOCKED", "focused", "unavailable"),
                    self._assertion("semantic.lifecycle.close-reopen", "BLOCKED", "reopened", "not attempted"),
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
            and record["original_status"] == "BLOCKED"
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

    def test_cleanup_only_failure_does_not_depend_on_best_effort_semantic_events(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "passed",
                run_id="cleanup-without-events",
            )
            with mock.patch.object(HARNESS, "try_record_semantic_event", return_value=False):
                envelope = HARNESS.record_cleanup_failure(
                    result_path,
                    failure_id="HARNESS-SAVE-CLEANUP",
                    message="save cleanup failed",
                    causal_component="save-provisioning",
                    timestamp=TIMESTAMP,
                )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            strict = HARNESS.validate_failure_artifacts(result_path, result)

        self.assertEqual("BLOCKED", result["status"])
        self.assertEqual("save-provisioning", envelope["causal_component"])
        self.assertEqual(envelope, strict)

    def test_cleanup_component_mismatch_is_rejected_before_publication(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "passed",
                run_id="cleanup-component-run",
            )
            result_before = result_path.read_bytes()
            event_path = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
            events_before = event_path.read_bytes()

            with self.assertRaisesRegex(HARNESS.HarnessError, "canonical ID"):
                HARNESS.record_cleanup_failure(
                    result_path,
                    failure_id="HARNESS-SAVE-CLEANUP",
                    message="save cleanup failed",
                    causal_component="x" * (HARNESS.MAX_SEMANTIC_FIELD_TEXT + 1),
                    timestamp=TIMESTAMP,
                )

            self.assertEqual(result_before, result_path.read_bytes())
            self.assertEqual(events_before, event_path.read_bytes())
            self.assertFalse((root / "failure.json").exists())

    def test_strict_validator_authenticates_complete_cleanup_projection(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "runtime failed",
                run_id="cleanup-projection-run",
                assertions=[self._assertion(
                    "runtime.boot.loaded", "FAIL", "boot", "failed"
                )],
                cleanup_failures=[
                    {
                        "id": "HARNESS-SAVE-CLEANUP",
                        "status": "FAIL",
                        "expected": "save removed",
                        "actual": "save remained",
                        "causal_component": "save-provisioning",
                    },
                    {
                        "id": "HARNESS-OPTIONS-RESTORE",
                        "status": "FAIL",
                        "expected": "options restored",
                        "actual": "restore failed",
                        "causal_component": "runtime-options",
                    },
                ],
                failure_timestamp=TIMESTAMP,
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            envelope = HARNESS.validate_failure_artifacts(result_path, result)
            self.assertEqual(
                ["HARNESS-SAVE-CLEANUP", "HARNESS-OPTIONS-RESTORE"],
                [
                    assertion["id"]
                    for assertion in result["assertions"]
                    if assertion["id"] in HARNESS.HARNESS_CLEANUP_ASSERTION_IDS
                ],
            )

            mutations = {}
            removed = copy.deepcopy(envelope)
            removed["cleanup_failures"].pop(0)
            mutations["removed"] = removed
            duplicated = copy.deepcopy(envelope)
            duplicated["cleanup_failures"].append(
                copy.deepcopy(duplicated["cleanup_failures"][0])
            )
            mutations["duplicated"] = duplicated
            reordered = copy.deepcopy(envelope)
            reordered["cleanup_failures"].reverse()
            mutations["reordered"] = reordered
            for field, value in (
                ("phase", "runtime"),
                ("failure_class", "ASSERTION_FAILURE"),
                ("causal_component", "runtime-process"),
                ("original_status", "BLOCKED"),
                ("root_failure_id", "fabricated-root"),
            ):
                damaged = copy.deepcopy(envelope)
                damaged["cleanup_failures"][0][field] = value
                mutations[field] = damaged
            remainder = copy.deepcopy(envelope)
            remainder["cleanup_failures"].append(
                HARNESS._failure_remainder(
                    "CLEANUP_FAILURE",
                    envelope["root_failure"]["id"],
                    result,
                    1,
                )
            )
            mutations["fabricated-remainder"] = remainder

            for name, damaged in mutations.items():
                with self.subTest(mutation=name):
                    (root / "failure.json").write_text(
                        json.dumps(damaged), encoding="utf-8"
                    )
                    (root / "failure-summary.txt").write_text(
                        HARNESS._failure_summary(damaged), encoding="utf-8"
                    )
                    with self.assertRaisesRegex(
                        HARNESS.HarnessError,
                        "causal records|does not reference",
                    ):
                        HARNESS.validate_failure_artifacts(result_path, result)

    def test_typed_preflight_context_conflict_is_rejected_before_publication(self) -> None:
        resolved = HARNESS.resolve_scenario(
            HARNESS.load_manifest(), "runtime.boot", "smoke"
        )
        outcomes = {
            requirement["id"]: {
                "status": "available",
                "classification": "available",
                "reasonCode": "AVAILABLE",
                "explanation": "Capability is available.",
            }
            for requirement in resolved["capabilities"]
        }
        unavailable = next(
            requirement["id"]
            for requirement in resolved["capabilities"]
            if requirement["requirement"] == "required"
        )
        outcomes[unavailable] = {
            "status": "error",
            "classification": "misconfiguration",
            "reasonCode": "PROBE_NOT_RUN",
            "explanation": "The required owner probe was not run.",
        }

        for field in ("failure_phase", "failure_class", "causal_component"):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                result_path = root / "result.json"
                HARNESS.publish_preflight(
                    root,
                    result_path,
                    resolved,
                    "preflight-conflict-run",
                    outcomes,
                    timestamp=TIMESTAMP,
                )
                result = json.loads(result_path.read_text(encoding="utf-8"))
                envelope = json.loads((root / "failure.json").read_text(encoding="utf-8"))
                before = {
                    name: (root / name).read_bytes()
                    for name in (
                        "result.json",
                        "failure.json",
                        "failure-summary.txt",
                        HARNESS.SEMANTIC_EVENT_FILE_NAME,
                    )
                }
                canonical = {
                    "failure_phase": envelope["phase"],
                    "failure_class": envelope["failure_class"],
                    "causal_component": envelope["causal_component"],
                }
                conflicting = {
                    "failure_phase": "runtime",
                    "failure_class": "ASSERTION_FAILURE",
                    "causal_component": "scenario",
                }
                canonical[field] = conflicting[field]

                with self.assertRaisesRegex(
                    HARNESS.HarnessError, "canonical assertion mapping"
                ):
                    HARNESS.write_result(
                        result_path,
                        result["status"],
                        result["scenario"],
                        "preflight remained blocked",
                        run_id=result["runId"],
                        duration_ms=result["durationMs"],
                        assertions=result["assertions"],
                        exceptions=result["exceptions"],
                        artifacts=result["artifacts"],
                        failure_timestamp=TIMESTAMP,
                        **canonical,
                    )

                self.assertEqual(
                    before,
                    {name: (root / name).read_bytes() for name in before},
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
                self._assertion(assertion_id, "BLOCKED", text, text)
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
                self._assertion("runtime.boot.check-0", "FAIL", "pass", "failed"),
                *[
                    self._assertion(
                        f"runtime.boot.check-{index}", "BLOCKED", "pass", "skipped"
                    )
                    for index in range(1, 40)
                ],
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
                "BLOCKED",
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
        cleanup_failures = [
            {
                "id": cleanup_id,
                "expected": text,
                "actual": text,
                "message": text,
                "causal_component": HARNESS.CLEANUP_CAUSAL_COMPONENTS[cleanup_id],
                "timestamp": TIMESTAMP,
            }
            for index in range(len(HARNESS.HARNESS_CLEANUP_ASSERTION_IDS))
            for cleanup_id in [
                sorted(HARNESS.HARNESS_CLEANUP_ASSERTION_IDS)[
                    index % len(HARNESS.HARNESS_CLEANUP_ASSERTION_IDS)
                ]
            ]
        ]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                text,
                run_id=text,
                assertions=assertions,
                failure_timestamp=TIMESTAMP,
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

    def test_strict_validator_rejects_reordered_fabricated_omitted_or_duplicate_causal_records(self) -> None:
        mutations = (
            "reordered", "fabricated", "omitted", "duplicate", "reclassified",
            "root-context", "unnecessary-remainder",
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                result_path = Path(directory) / "result.json"
                HARNESS.write_result(
                    result_path,
                    "FAIL",
                    "runtime.boot",
                    "multiple failures",
                    run_id="strict-projection-run",
                    assertions=[
                        self._assertion("runtime.boot.first", "FAIL", "first", "failed"),
                        self._assertion("runtime.boot.second", "FAIL", "second", "failed"),
                    ],
                    failure_timestamp=TIMESTAMP,
                )
                result = json.loads(result_path.read_text(encoding="utf-8"))
                envelope = json.loads(
                    (result_path.parent / "failure.json").read_text(encoding="utf-8")
                )
                if mutation == "reordered":
                    old_root = envelope["root_failure"]
                    new_root = envelope["additional_failures"].pop(0)
                    new_root["classification"] = "ROOT_FAILURE"
                    old_root["classification"] = "ADDITIONAL_FAILURE"
                    envelope["root_failure"] = new_root
                    envelope["additional_failures"] = [old_root]
                    for field in (
                        "phase", "failure_class", "expected", "actual", "message",
                        "causal_component",
                    ):
                        envelope[field] = new_root[field]
                elif mutation == "fabricated":
                    invented = copy.deepcopy(envelope["additional_failures"][0])
                    invented["id"] = "runtime.boot.invented"
                    envelope["additional_failures"].append(invented)
                elif mutation == "omitted":
                    envelope["additional_failures"] = []
                elif mutation == "reclassified":
                    reclassified = envelope["additional_failures"].pop(0)
                    reclassified["classification"] = "CASCADE_SKIPPED"
                    reclassified["root_failure_id"] = envelope["root_failure"]["id"]
                    envelope["cascade_records"] = [reclassified]
                elif mutation == "root-context":
                    root_record = envelope["root_failure"]
                    root_record["phase"] = "validation"
                    root_record["failure_class"] = "EVIDENCE_FAILURE"
                    root_record["causal_component"] = "fabricated"
                    for field in ("phase", "failure_class", "causal_component"):
                        envelope[field] = root_record[field]
                elif mutation == "unnecessary-remainder":
                    envelope["additional_failures"] = [HARNESS._failure_remainder(
                        "ADDITIONAL_FAILURE",
                        None,
                        result,
                        1,
                    )]
                else:
                    envelope["additional_failures"].append(
                        copy.deepcopy(envelope["additional_failures"][0])
                    )
                (result_path.parent / "failure.json").write_text(
                    json.dumps(envelope), encoding="utf-8"
                )
                (result_path.parent / "failure-summary.txt").write_text(
                    HARNESS._failure_summary(envelope), encoding="utf-8"
                )

                with self.assertRaisesRegex(
                    HARNESS.HarnessError,
                    "first causal|causal records",
                ):
                    HARNESS.validate_failure_artifacts(result_path, result)

    def test_strict_validator_reconstructs_compacted_remainders_and_bucket_order(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result_path = Path(directory) / "result.json"
            assertions = [
                self._assertion("runtime.root", "FAIL", "root", "failed"),
                self._assertion("runtime.early", "FAIL", "early", "failed"),
                *[
                    self._assertion(
                        f"runtime.cascade-{index}", "BLOCKED", "run", "skipped"
                    )
                    for index in range(HARNESS.MAX_CASCADE_RECORDS + 3)
                ],
                *[
                    self._assertion(
                        f"runtime.additional-{index}", "FAIL", "pass", "failed"
                    )
                    for index in range(HARNESS.MAX_ADDITIONAL_FAILURES + 3)
                ],
            ]
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "many failures",
                run_id="strict-remainder-run",
                assertions=assertions,
                failure_timestamp=TIMESTAMP,
                cascade_dependencies={
                    assertion["id"]: assertions[0]["id"]
                    for assertion in assertions
                    if assertion["status"] == "BLOCKED"
                },
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            envelope = HARNESS.validate_failure_artifacts(result_path, result)
            self.assertEqual("runtime.root", envelope["root_failure"]["id"])
            self.assertEqual("runtime.early", envelope["additional_failures"][0]["id"])

            for mutation in ("omit-retained", "tamper-remainder"):
                damaged = copy.deepcopy(envelope)
                if mutation == "omit-retained":
                    damaged["additional_failures"].pop(0)
                else:
                    remainder = damaged["additional_failures"][-1]
                    remainder["expected"] = "fabricated"
                    remainder["actual"] = "fabricated"
                    remainder["message"] = "fabricated"
                (result_path.parent / "failure.json").write_text(
                    json.dumps(damaged), encoding="utf-8"
                )
                (result_path.parent / "failure-summary.txt").write_text(
                    HARNESS._failure_summary(damaged), encoding="utf-8"
                )
                with self.assertRaisesRegex(HARNESS.HarnessError, "causal records"):
                    HARNESS.validate_failure_artifacts(result_path, result)

    def test_secondary_semantic_failure_preserves_independent_runtime_fail_root(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "runtime failed",
                run_id="semantic-merge-run",
                assertions=[
                    self._assertion("runtime.boot.first", "FAIL", "boot", "failed"),
                    self._assertion("runtime.boot.second", "FAIL", "ready", "failed"),
                ],
                failure_timestamp=TIMESTAMP,
            )

            merged = HARNESS.record_secondary_failure(
                result_path,
                scenario="runtime.boot",
                run_id="semantic-merge-run",
                assertion_id="HARNESS-SEMANTIC-TEST-AGENT",
                expected="Semantic companion completes.",
                message="Semantic companion exited 1.",
                exception_type="ProcessExit",
                artifact_root=root,
            )
            envelope = HARNESS.validate_failure_artifacts(result_path, merged)

        self.assertEqual("FAIL", merged["status"])
        self.assertEqual("runtime.boot.first", envelope["root_failure"]["id"])
        self.assertEqual(
            ["runtime.boot.second", "HARNESS-SEMANTIC-TEST-AGENT"],
            [record["id"] for record in envelope["additional_failures"]],
        )

    def test_secondary_semantic_failure_is_blocked_root_without_runtime_fail(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "runtime passed",
                run_id="semantic-only-run",
                assertions=[
                    self._assertion("runtime.boot.loaded", "PASS", "boot", "loaded"),
                ],
                failure_timestamp=TIMESTAMP,
            )

            merged = HARNESS.record_secondary_failure(
                result_path,
                scenario="runtime.boot",
                run_id="semantic-only-run",
                assertion_id="HARNESS-SEMANTIC-TEST-AGENT",
                expected="Semantic companion completes.",
                message="Semantic companion exited 1.",
                exception_type="ProcessExit",
                artifact_root=root,
            )
            envelope = HARNESS.validate_failure_artifacts(result_path, merged)

        self.assertEqual("BLOCKED", merged["status"])
        self.assertEqual("HARNESS-SEMANTIC-TEST-AGENT", envelope["root_failure"]["id"])
        self.assertEqual([], envelope["additional_failures"])

    def test_secondary_semantic_failure_preserves_blocker_cleanup_and_publishes_fingerprint(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "BLOCKED",
                "runtime.boot",
                "runtime blocked",
                run_id="semantic-preserve-run",
                assertions=[
                    self._assertion("HARNESS-PROCESS-BOOT", "BLOCKED", "boot", "blocked"),
                ],
                cleanup_failures=[{
                    "id": "HARNESS-SAVE-CLEANUP",
                    "status": "FAIL",
                    "actual": "cleanup failed",
                    "timestamp": TIMESTAMP,
                }],
                failure_timestamp=TIMESTAMP,
            )

            merged = HARNESS.record_secondary_failure(
                result_path,
                scenario="runtime.boot",
                run_id="semantic-preserve-run",
                assertion_id="HARNESS-SEMANTIC-TEST-AGENT",
                expected="Semantic companion completes.",
                message="Semantic companion exited 1.",
                exception_type="ProcessExit",
                artifact_root=root,
            )
            envelope = HARNESS.validate_failure_artifacts(result_path, merged)
            events = HARNESS.read_semantic_events(
                root,
                expected_scenario="runtime.boot",
                expected_run_id="semantic-preserve-run",
            )

        self.assertEqual("BLOCKED", merged["status"])
        self.assertEqual("HARNESS-PROCESS-BOOT", envelope["root_failure"]["id"])
        self.assertEqual(
            ["HARNESS-SEMANTIC-TEST-AGENT"],
            [record["id"] for record in envelope["additional_failures"]],
        )
        self.assertEqual(
            ["HARNESS-SAVE-CLEANUP"],
            [record["id"] for record in envelope["cleanup_failures"]],
        )
        published = [event for event in events if event["event"] == "Result.Published"]
        self.assertEqual(
            HARNESS._result_fingerprint(merged),
            published[-1]["fields"]["result_fingerprint"],
        )

    def test_result_rejects_duplicate_assertion_identity(self) -> None:
        assertion = self._assertion("runtime.boot.duplicate", "FAIL", "boot", "failed")
        result = {
            "protocolVersion": 1,
            "runId": "duplicate-run",
            "scenario": "runtime.boot",
            "status": "FAIL",
            "durationMs": 1,
            "assertions": [assertion, copy.deepcopy(assertion)],
            "exceptions": [],
            "artifacts": [],
        }

        with self.assertRaisesRegex(HARNESS.HarnessError, "must be unique"):
            HARNESS.validate_result(result)

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
