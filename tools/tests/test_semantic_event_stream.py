from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR_PATH = ROOT / "tools" / "live-harness" / "validate.py"
DIRECT_RUNTIME_PATH = ROOT / "tools" / "live-harness" / "direct_runtime.py"
SPEC = importlib.util.spec_from_file_location(
    "hatifect_semantic_event_stream", VALIDATOR_PATH
)
assert SPEC is not None and SPEC.loader is not None
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)
DIRECT_SPEC = importlib.util.spec_from_file_location(
    "hatifect_semantic_event_direct_runtime", DIRECT_RUNTIME_PATH
)
assert DIRECT_SPEC is not None and DIRECT_SPEC.loader is not None
DIRECT_RUNTIME = importlib.util.module_from_spec(DIRECT_SPEC)
DIRECT_SPEC.loader.exec_module(DIRECT_RUNTIME)

TIMESTAMP = "2026-09-13T00:00:00Z"


class SemanticEventStreamTests(unittest.TestCase):
    def setUp(self) -> None:
        DIRECT_RUNTIME._SEMANTIC_VALIDATORS.clear()

    def test_records_are_canonical_and_monotonically_ordered(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Scenario.Started",
                fields={},
                timestamp=TIMESTAMP,
            )
            second = HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Preflight.Completed",
                fields={"status": "PASS"},
                timestamp=TIMESTAMP,
            )
            path = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
            lines = path.read_bytes().splitlines(keepends=True)
            events = HARNESS.read_semantic_events(
                root,
                expected_scenario="runtime.boot",
                expected_run_id="run-1",
            )

        self.assertEqual([first["seq"], second["seq"]], [1, 2])
        self.assertEqual([event["seq"] for event in events], [1, 2])
        self.assertTrue(all(line.endswith(b"\n") for line in lines))
        self.assertEqual(
            lines,
            [HARNESS.serialize_semantic_event(event) for event in events],
        )
        self.assertNotIn(b" ", lines[0])

    def test_first_non_start_event_adds_scenario_start(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            recorded = HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Preflight.Completed",
                fields={"status": "PASS"},
                timestamp=TIMESTAMP,
            )
            events = HARNESS.read_semantic_events(root)

        self.assertEqual(recorded["seq"], 2)
        self.assertEqual(
            [event["event"] for event in events],
            ["Scenario.Started", "Preflight.Completed"],
        )

    def test_serialization_is_byte_stable_for_identical_events(self) -> None:
        payloads = []
        for _ in range(2):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                HARNESS.record_semantic_event(
                    root,
                    scenario="flow.ui.player.input",
                    run_id="stable-run",
                    event="Runtime.StateChanged",
                    fields={"state_after": "Running", "state_before": "Launching"},
                    timestamp=TIMESTAMP,
                )
                payloads.append(
                    (root / HARNESS.SEMANTIC_EVENT_FILE_NAME).read_bytes()
                )

        self.assertEqual(payloads[0], payloads[1])

    def test_fields_are_scalar_count_and_text_bounded(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaisesRegex(HARNESS.HarnessError, "count bound"):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-1",
                    event="Preflight.Completed",
                    fields={f"field_{index}": index for index in range(13)},
                    timestamp=TIMESTAMP,
                )
            with self.assertRaisesRegex(HARNESS.HarnessError, "text exceeds"):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-1",
                    event="Preflight.Completed",
                    fields={"message": "x" * (HARNESS.MAX_SEMANTIC_FIELD_TEXT + 1)},
                    timestamp=TIMESTAMP,
                )
            with self.assertRaisesRegex(HARNESS.HarnessError, "bounded scalar"):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-1",
                    event="Preflight.Completed",
                    fields={"payload": {"recursive": "object"}},
                    timestamp=TIMESTAMP,
                )
            for invalid_number in (float("inf"), HARNESS.MAX_SEMANTIC_INTEGER + 1):
                with self.subTest(invalid_number=invalid_number), self.assertRaises(
                    HARNESS.HarnessError
                ):
                    HARNESS.record_semantic_event(
                        root,
                        scenario="runtime.boot",
                        run_id="run-1",
                        event="Preflight.Completed",
                        fields={"value": invalid_number},
                        timestamp=TIMESTAMP,
                    )
            with self.assertRaisesRegex(HARNESS.HarnessError, "serialized-size"):
                HARNESS.record_semantic_event(
                    root,
                    scenario="😀" * HARNESS.MAX_FAILURE_TEXT,
                    run_id="😀" * HARNESS.MAX_FAILURE_TEXT,
                    event="Preflight.Completed",
                    fields={},
                    timestamp=TIMESTAMP,
                )
            self.assertFalse((root / HARNESS.SEMANTIC_EVENT_FILE_NAME).exists())

    def test_malformed_stream_is_rejected_without_replacement(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
            path.write_bytes(b'{"incomplete":true')
            path.chmod(0o600)
            original = path.read_bytes()

            with self.assertRaisesRegex(HARNESS.HarnessError, "incomplete record"):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-1",
                    event="Scenario.Started",
                    fields={},
                    timestamp=TIMESTAMP,
                )

            self.assertEqual(path.read_bytes(), original)

    def test_schema_rejects_unknown_names_components_timestamps_and_sequence(self) -> None:
        record = {
            "format_version": 1,
            "seq": 1,
            "time": TIMESTAMP,
            "scenario": "runtime.boot",
            "run_id": "run-1",
            "component": "Scenario",
            "event": "Scenario.Started",
            "fields": {},
        }
        changes = (
            ({"event": "RawLog.Copied"}, "unsupported"),
            ({"component": "Runtime"}, "component conflicts"),
            ({"time": "2026-09-13T00:00:00"}, "timezone"),
            ({"seq": True}, "sequence is invalid"),
        )
        for change, message in changes:
            with self.subTest(change=change), self.assertRaisesRegex(
                HARNESS.HarnessError, message
            ):
                HARNESS.validate_semantic_event({**record, **change})

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / HARNESS.SEMANTIC_EVENT_FILE_NAME
            path.write_bytes(
                HARNESS.serialize_semantic_event(record)
                + HARNESS.serialize_semantic_event(record)
            )
            path.chmod(0o600)
            with self.assertRaisesRegex(HARNESS.HarnessError, "monotonically"):
                HARNESS.read_semantic_events(Path(directory))

    def test_symlinked_or_overpermissive_stream_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            external = root / "external.jsonl"
            external.write_text("foreign evidence\n", encoding="utf-8")
            stream = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
            stream.symlink_to(external)
            with self.assertRaisesRegex(
                HARNESS.HarnessError, "inspect|regular file"
            ):
                HARNESS.read_semantic_events(root)
            self.assertEqual(external.read_text(encoding="utf-8"), "foreign evidence\n")

            stream.unlink()
            HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Scenario.Started",
                fields={},
                timestamp=TIMESTAMP,
            )
            stream.chmod(0o644)
            with self.assertRaisesRegex(HARNESS.HarnessError, "regular file"):
                HARNESS.read_semantic_events(root)

    def test_foreign_stream_identity_is_rejected_without_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Scenario.Started",
                fields={},
                timestamp=TIMESTAMP,
            )
            path = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
            original = path.read_bytes()

            with self.assertRaisesRegex(HARNESS.HarnessError, "another run"):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-2",
                    event="Scenario.Completed",
                    fields={"duration_ms": 1, "status": "PASS"},
                    timestamp=TIMESTAMP,
                )

            self.assertEqual(path.read_bytes(), original)

    def test_concurrent_writers_preserve_unique_sequence(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def write_event(index: int) -> None:
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-1",
                    event="Runtime.StateChanged",
                    fields={"state_after": f"state-{index}"},
                    timestamp=TIMESTAMP,
                )

            with ThreadPoolExecutor(max_workers=8) as executor:
                list(executor.map(write_event, range(32)))
            events = HARNESS.read_semantic_events(root)

        self.assertEqual(len(events), 33)
        self.assertEqual([event["seq"] for event in events], list(range(1, 34)))

    def test_retention_keeps_only_latest_bounded_events(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for index in range(HARNESS.MAX_RETAINED_SEMANTIC_EVENTS + 10):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-1",
                    event="Runtime.StateChanged",
                    fields={"ordinal": index},
                    timestamp=TIMESTAMP,
                )
            events = HARNESS.read_semantic_events(root)
            path = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
            stream_size = path.stat().st_size

        self.assertEqual(len(events), HARNESS.MAX_RETAINED_SEMANTIC_EVENTS)
        self.assertGreater(events[0]["seq"], 1)
        self.assertLessEqual(stream_size, HARNESS.MAX_SEMANTIC_STREAM_BYTES)

    def test_byte_retention_keeps_newest_large_identity_events(self) -> None:
        run_id = "r" + ("é" * (HARNESS.MAX_FAILURE_TEXT - 1))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for index in range(80):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id=run_id,
                    event="Runtime.StateChanged",
                    fields={"ordinal": index},
                    timestamp=TIMESTAMP,
                )
            events = HARNESS.read_semantic_events(root)
            stream_size = (root / HARNESS.SEMANTIC_EVENT_FILE_NAME).stat().st_size

        self.assertLess(len(events), 81)
        self.assertEqual(events[-1]["fields"]["ordinal"], 79)
        self.assertLessEqual(stream_size, HARNESS.MAX_SEMANTIC_STREAM_BYTES)

    def test_success_run_writes_stream_without_failure_sidecars(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "passed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            events = HARNESS.read_semantic_events(root)
            failure_exists = (root / "failure.json").exists()
            summary_exists = (root / "failure-summary.txt").exists()

        self.assertEqual(
            [event["event"] for event in events],
            ["Scenario.Started", "Result.Published", "Scenario.Completed"],
        )
        self.assertFalse(failure_exists)
        self.assertFalse(summary_exists)

    def test_failure_tail_contains_causal_events_but_not_raw_message(self) -> None:
        raw_message = "raw diagnostic detail that must remain outside semantic events"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            for ordinal in range(20):
                HARNESS.record_semantic_event(
                    root,
                    scenario="runtime.boot",
                    run_id="run-1",
                    event="Runtime.StateChanged",
                    fields={"ordinal": ordinal, "state_after": "Running"},
                    timestamp=TIMESTAMP,
                )
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                raw_message,
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            envelope = HARNESS.validate_failure_artifacts(
                result_path, json.loads(result_path.read_text(encoding="utf-8"))
            )
            stream = (root / HARNESS.SEMANTIC_EVENT_FILE_NAME).read_text(
                encoding="utf-8"
            )

        self.assertEqual(envelope["format_version"], 3)
        self.assertEqual(
            len(envelope["semantic_event_tail"]),
            HARNESS.MAX_FAILURE_TAIL_EVENTS,
        )
        self.assertIn(
            "Runtime.StateChanged",
            [event["event"] for event in envelope["semantic_event_tail"]],
        )
        self.assertIn(
            "Assertion.Failed",
            [event["event"] for event in envelope["semantic_event_tail"]],
        )
        self.assertNotIn(raw_message, stream)
        self.assertLessEqual(
            sum(
                len(HARNESS.serialize_semantic_event(event))
                for event in envelope["semantic_event_tail"]
            ),
            HARNESS.MAX_FAILURE_TAIL_BYTES,
        )

    def test_refresh_failure_tail_captures_late_terminal_transition(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "failed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Runtime.StateChanged",
                fields={"state_before": "Running", "state_after": "Failed"},
                timestamp=TIMESTAMP,
            )
            refreshed = HARNESS.refresh_failure_event_tail(result_path)

        self.assertEqual(
            refreshed["semantic_event_tail"][-1]["event"],
            "Runtime.StateChanged",
        )

    def test_direct_owned_completion_follows_terminal_runtime_state(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            (root / "direct-process-state.json").write_text("{}\n", encoding="utf-8")
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "failed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Validation.Completed",
                fields={"status": "FAIL"},
                timestamp=TIMESTAMP,
            )
            HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Runtime.StateChanged",
                fields={"state_before": "Running", "state_after": "Failed"},
                timestamp=TIMESTAMP,
            )
            HARNESS.complete_scenario(result_path, timestamp=TIMESTAMP)
            HARNESS.complete_scenario(result_path, timestamp=TIMESTAMP)
            events = HARNESS.read_semantic_events(root)
            envelope = json.loads((root / "failure.json").read_text(encoding="utf-8"))

        names = [event["event"] for event in events]
        self.assertLess(names.index("Result.Published"), names.index("Validation.Completed"))
        self.assertLess(names.index("Validation.Completed"), names.index("Runtime.StateChanged"))
        self.assertEqual(names[-1], "Scenario.Completed")
        self.assertEqual(names.count("Scenario.Completed"), 1)
        self.assertEqual(envelope["semantic_event_tail"][-1]["event"], "Scenario.Completed")

    def test_diagnostic_stream_failure_cannot_replace_primary_result(self) -> None:
        for status in ("PASS", "FAIL"):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                stream = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
                stream.write_bytes(b'{"incomplete":true')
                stream.chmod(0o600)
                result_path = root / "result.json"

                HARNESS.write_result(
                    result_path,
                    status,
                    "runtime.boot",
                    "primary outcome",
                    run_id="run-1",
                    failure_timestamp=TIMESTAMP,
                )
                result = json.loads(result_path.read_text(encoding="utf-8"))

                self.assertEqual(result["status"], status)
                self.assertTrue((root / HARNESS.SEMANTIC_EVENT_ERROR_PATH).is_file())
                if status == "FAIL":
                    envelope = HARNESS.validate_failure_artifacts(result_path, result)
                    self.assertEqual(envelope["semantic_event_tail"], [])
                    self.assertIn(
                        HARNESS.SEMANTIC_EVENT_ERROR_PATH,
                        envelope["relevant_artifacts"],
                    )
                else:
                    self.assertFalse((root / "failure.json").exists())

    def test_cleanup_event_is_additive_and_preserves_scenario_root(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "product failed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            original = json.loads((root / "failure.json").read_text(encoding="utf-8"))
            envelope = HARNESS.record_cleanup_failure(
                result_path,
                failure_id="HARNESS-PROCESS-TEARDOWN",
                message="teardown failed",
                timestamp=TIMESTAMP,
            )

        self.assertEqual(
            envelope["root_failure"]["id"], original["root_failure"]["id"]
        )
        self.assertEqual(envelope["cleanup_failures"][0]["id"], "HARNESS-PROCESS-TEARDOWN")
        event_names = [event["event"] for event in envelope["semantic_event_tail"]]
        self.assertLess(
            event_names.index("Cleanup.Failed"),
            max(
                index
                for index, event_name in enumerate(event_names)
                if event_name == "Result.Published"
            ),
        )
        self.assertEqual(event_names[-1], "Scenario.Completed")

    def test_phase_one_failure_envelope_v2_remains_valid(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "failed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            envelope = json.loads((root / "failure.json").read_text(encoding="utf-8"))
            envelope["format_version"] = 2
            del envelope["semantic_event_tail"]

        HARNESS.validate_failure_envelope(envelope)
        self.assertEqual(envelope["format_version"], 2)
        self.assertNotIn("semantic_event_tail", envelope)

    def test_failure_envelope_rejects_malformed_event_tail(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "failed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            envelope = json.loads((root / "failure.json").read_text(encoding="utf-8"))
            envelope["semantic_event_tail"][-1]["component"] = "Runtime"

        with self.assertRaisesRegex(HARNESS.HarnessError, "component conflicts"):
            HARNESS.validate_failure_envelope(envelope)

    def test_authoritative_result_identity_change_resets_prior_stream(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "passed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "failed",
                run_id="run-2",
                failure_timestamp=TIMESTAMP,
            )
            events = HARNESS.read_semantic_events(root)

        self.assertEqual(events[0]["seq"], 1)
        self.assertEqual({event["run_id"] for event in events}, {"run-2"})

    def test_malformed_prior_stream_cannot_block_new_result_identity(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            result_path = root / "result.json"
            HARNESS.write_result(
                result_path,
                "PASS",
                "runtime.boot",
                "passed",
                run_id="run-1",
                failure_timestamp=TIMESTAMP,
            )
            stream = root / HARNESS.SEMANTIC_EVENT_FILE_NAME
            stream.write_bytes(b'{"incomplete":true')
            stream.chmod(0o600)

            HARNESS.write_result(
                result_path,
                "FAIL",
                "runtime.boot",
                "new primary outcome",
                run_id="run-2",
                failure_timestamp=TIMESTAMP,
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            envelope = HARNESS.validate_failure_artifacts(result_path, result)

        self.assertEqual(result["runId"], "run-2")
        self.assertEqual(result["status"], "FAIL")
        self.assertEqual(envelope["semantic_event_tail"], [])
        self.assertIn(
            HARNESS.SEMANTIC_EVENT_ERROR_PATH,
            envelope["relevant_artifacts"],
        )

    def test_artifact_collection_exposes_stream_but_not_lock(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            HARNESS.record_semantic_event(
                root,
                scenario="runtime.boot",
                run_id="run-1",
                event="Scenario.Started",
                fields={},
                timestamp=TIMESTAMP,
            )
            artifacts = HARNESS.collect_artifacts(root)

        self.assertIn(
            {"type": "semantic-events", "path": "semantic-events.jsonl"},
            artifacts,
        )
        self.assertNotIn(
            HARNESS.SEMANTIC_EVENT_LOCK_FILE_NAME,
            [artifact["path"] for artifact in artifacts],
        )

    def test_direct_runtime_publishes_through_pinned_validator(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request = {
                "artifactDirectory": str(root),
                "scenarioId": "runtime.boot",
                "requestId": "run-1",
            }
            metadata = {"validatorExecutable": str(VALIDATOR_PATH)}
            DIRECT_RUNTIME._record_semantic_event(
                metadata,
                request,
                "Runtime.StateChanged",
                {"state_before": "Accepted", "state_after": "Launching"},
            )
            events = HARNESS.read_semantic_events(root)

        self.assertEqual(
            [event["event"] for event in events],
            ["Scenario.Started", "Runtime.StateChanged"],
        )


if __name__ == "__main__":
    unittest.main()
