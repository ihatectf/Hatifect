import copy
import datetime as dt
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR_PATH = ROOT / "tools" / "live-harness" / "validate.py"
SPEC = importlib.util.spec_from_file_location(
    "hatifect_capability_preflight", VALIDATOR_PATH
)
assert SPEC is not None and SPEC.loader is not None
HARNESS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HARNESS)


class CapabilityPreflightTests(unittest.TestCase):
    timestamp = "2026-09-13T04:00:00Z"

    def setUp(self) -> None:
        self.scenarios = HARNESS.load_manifest()

    @staticmethod
    def _available_outcomes(resolved):
        return {
            requirement["id"]: {
                "status": "available",
                "classification": "available",
                "reasonCode": "AVAILABLE",
                "explanation": "The owner-layer probe confirmed this capability.",
            }
            for requirement in resolved["capabilities"]
        }

    @staticmethod
    def _scenario(
        scenario_id,
        *,
        requires_save=False,
        required_mods=None,
        capabilities=None,
        includes=None,
    ):
        return {
            "id": scenario_id,
            "kind": "ui",
            "requiresSave": requires_save,
            "timeoutSeconds": 60,
            "checks": [] if includes else [f"{scenario_id}.check"],
            "includes": includes or [],
            "automation": {
                "protocolVersion": 1,
                "driver": "Hatifect.TestHarness",
            },
            "requiredMods": required_mods or [],
            "capabilities": capabilities or [],
        }

    def test_current_scenarios_derive_only_needed_capabilities(self) -> None:
        lifecycle = HARNESS.resolve_scenario(
            self.scenarios, "semantic.lifecycle", "ui"
        )
        player = HARNESS.resolve_scenario(
            self.scenarios, "flow.ui.player", "smoke"
        )
        physical_input = HARNESS.resolve_scenario(
            self.scenarios, "flow.ui.player.input", "smoke"
        )

        self.assertEqual(
            [item["id"] for item in lifecycle["capabilities"]],
            [
                "artifact-writable",
                "request-parameters",
                "smapi-runtime",
                "isolated-deployment",
                "user-session-executor",
            ],
        )
        self.assertIn(
            {"id": "required-mods", "requirement": "required"},
            player["capabilities"],
        )
        self.assertIn(
            {"id": "isolated-save-fixture", "requirement": "required"},
            player["capabilities"],
        )
        self.assertEqual(
            [item["id"] for item in physical_input["capabilities"]][-3:],
            [
                "semantic-workflow",
                "user-session-gui",
                "quartz-post-events",
            ],
        )

    def test_aggregate_capabilities_are_unioned_deduplicated_and_stably_ordered(self) -> None:
        scenarios = {
            "semantic.one": self._scenario(
                "semantic.one",
                required_mods=["Author.Mod"],
                capabilities=[
                    {"id": "quartz-post-events", "required": False},
                ],
            ),
            "semantic.two": self._scenario(
                "semantic.two",
                requires_save=True,
                capabilities=[
                    {"id": "quartz-post-events", "required": True},
                    {"id": "user-session-gui", "required": False},
                ],
            ),
            "all": self._scenario(
                "all",
                includes=["semantic.one", "semantic.two"],
            ),
        }

        resolved = HARNESS.resolve_scenario(scenarios, "all", "ui")

        self.assertEqual(
            resolved["capabilities"],
            [
                {"id": "artifact-writable", "requirement": "required"},
                {"id": "request-parameters", "requirement": "required"},
                {"id": "smapi-runtime", "requirement": "required"},
                {"id": "isolated-deployment", "requirement": "required"},
                {"id": "required-mods", "requirement": "required"},
                {"id": "isolated-save-fixture", "requirement": "required"},
                {"id": "user-session-executor", "requirement": "required"},
                {"id": "user-session-gui", "requirement": "optional"},
                {"id": "quartz-post-events", "requirement": "required"},
            ],
        )

    def test_unknown_malformed_duplicate_and_downgrade_declarations_fail_closed(self) -> None:
        invalid_declarations = (
            [{"id": "unknown-capability", "required": True}],
            [{"id": "user-session-gui"}],
            [
                {"id": "user-session-gui", "required": True},
                {"id": "user-session-gui", "required": True},
            ],
            [{"id": "artifact-writable", "required": False}],
        )
        for declarations in invalid_declarations:
            with self.subTest(declarations=declarations):
                scenario = self._scenario(
                    "semantic.one",
                    capabilities=declarations,
                )
                document = {
                    "formatVersion": 1,
                    "scenarios": [
                        scenario,
                        self._scenario("all", includes=["semantic.one"]),
                    ],
                }
                with tempfile.TemporaryDirectory() as directory:
                    manifest = Path(directory) / "scenarios.json"
                    manifest.write_text(json.dumps(document), encoding="utf-8")
                    with self.assertRaises(HARNESS.HarnessError):
                        HARNESS.load_manifest(manifest)

    def test_all_required_available_produces_pass_report(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "semantic.lifecycle", "ui"
        )

        report = HARNESS.build_preflight_report(
            resolved,
            "run-available",
            self._available_outcomes(resolved),
            timestamp=self.timestamp,
        )

        self.assertEqual(report["status"], "PASS")
        self.assertEqual(report["counts"]["requiredUnavailable"], 0)
        self.assertEqual(report["counts"]["optionalUnavailable"], 0)

    def test_optional_missing_is_reported_but_does_not_block(self) -> None:
        resolved = copy.deepcopy(
            HARNESS.resolve_scenario(self.scenarios, "semantic.lifecycle", "ui")
        )
        resolved["capabilities"].append(
            {"id": "quartz-post-events", "requirement": "optional"}
        )
        outcomes = self._available_outcomes(resolved)
        outcomes["quartz-post-events"] = {
            "status": "missing",
            "classification": "environment-failure",
            "reasonCode": "QUARTZ_ACCESSIBILITY_DENIED",
            "explanation": "Quartz post-event access is not granted.",
        }

        report = HARNESS.build_preflight_report(
            resolved,
            "run-optional",
            outcomes,
            timestamp=self.timestamp,
        )

        self.assertEqual(report["status"], "PASS")
        self.assertEqual(report["counts"]["optionalUnavailable"], 1)

    def test_single_missing_required_publishes_canonical_blocked_artifacts(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "semantic.lifecycle", "ui"
        )
        outcomes = self._available_outcomes(resolved)
        outcomes["smapi-runtime"] = {
            "status": "missing",
            "classification": "environment-failure",
            "reasonCode": "SMAPI_UNAVAILABLE",
            "explanation": "No executable SMAPI runtime is configured.",
        }
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory)
            result_path = artifact / "result.json"

            report = HARNESS.publish_preflight(
                artifact,
                result_path,
                resolved,
                "run-missing",
                outcomes,
                timestamp=self.timestamp,
            )

            result = json.loads(result_path.read_text(encoding="utf-8"))
            failure = json.loads(
                (artifact / "failure.json").read_text(encoding="utf-8")
            )
            events = HARNESS.read_semantic_events(
                artifact,
                expected_scenario=resolved["id"],
                expected_run_id="run-missing",
            )
            self.assertEqual(report["status"], "BLOCKED")
            self.assertEqual(result["status"], "BLOCKED")
            self.assertEqual(
                failure["root_failure"]["id"],
                "HARNESS-PREFLIGHT-SMAPI-RUNTIME",
            )
            self.assertEqual(failure["phase"], "preflight")
            self.assertIn("preflight.json", failure["relevant_artifacts"])
            self.assertTrue((artifact / "failure-summary.txt").is_file())
            self.assertEqual(
                [event["event"] for event in events].count("Preflight.Failed"),
                1,
            )
            self.assertNotIn(
                "Preflight.Completed",
                [event["event"] for event in events],
            )

    def test_mixed_missing_required_preserve_typed_context_for_root_and_additional_failures(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "flow.ui.player.input", "smoke"
        )
        outcomes = self._available_outcomes(resolved)
        for capability_id, status, classification, reason in (
            (
                "smapi-runtime",
                "missing",
                "environment-failure",
                "SMAPI_UNAVAILABLE",
            ),
            (
                "semantic-workflow",
                "error",
                "misconfiguration",
                "SEMANTIC_WORKFLOW_INVALID",
            ),
            (
                "user-session-gui",
                "unsupported",
                "unsupported-capability",
                "PLATFORM_UNSUPPORTED",
            ),
        ):
            outcomes[capability_id] = {
                "status": status,
                "classification": classification,
                "reasonCode": reason,
                "explanation": f"Capability {capability_id} is unavailable.",
            }
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory)
            result_path = artifact / "result.json"
            HARNESS.publish_preflight(
                artifact,
                result_path,
                resolved,
                "run-multiple",
                outcomes,
                timestamp=self.timestamp,
            )
            failure = json.loads(
                (artifact / "failure.json").read_text(encoding="utf-8")
            )
            result = json.loads(result_path.read_text(encoding="utf-8"))
            validated = HARNESS.validate_failure_artifacts(result_path, result)

        self.assertEqual(validated, failure)
        self.assertEqual(
            failure["root_failure"]["id"],
            "HARNESS-PREFLIGHT-SMAPI-RUNTIME",
        )
        self.assertEqual(failure["cascade_records"], [])
        self.assertEqual(
            [item["id"] for item in failure["additional_failures"]],
            [
                "HARNESS-PREFLIGHT-SEMANTIC-WORKFLOW",
                "HARNESS-PREFLIGHT-USER-SESSION-GUI",
            ],
        )
        records = [failure["root_failure"], *failure["additional_failures"]]
        self.assertEqual(
            [
                (
                    item["phase"],
                    item["failure_class"],
                    item["causal_component"],
                )
                for item in records
            ],
            [
                (
                    "preflight",
                    "PREFLIGHT_ENVIRONMENT_FAILURE",
                    "runtime-environment",
                ),
                (
                    "preflight",
                    "PREFLIGHT_MISCONFIGURATION",
                    "semantic-test-agent",
                ),
                (
                    "preflight",
                    "PREFLIGHT_UNSUPPORTED_CAPABILITY",
                    "user-session-runtime",
                ),
            ],
        )
        for record, capability_id, reason_code in zip(
            records,
            (
                "smapi-runtime",
                "semantic-workflow",
                "user-session-gui",
            ),
            (
                "SMAPI_UNAVAILABLE",
                "SEMANTIC_WORKFLOW_INVALID",
                "PLATFORM_UNSUPPORTED",
            ),
            strict=True,
        ):
            self.assertIn(capability_id, record["expected"])
            self.assertIn(capability_id, record["actual"])
            self.assertIn(reason_code, record["actual"])

    def test_report_serialization_and_order_are_deterministic_and_bounded(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "flow.ui.player.input", "smoke"
        )
        outcomes = self._available_outcomes(resolved)

        first = HARNESS.build_preflight_report(
            resolved,
            "run-deterministic",
            outcomes,
            timestamp=self.timestamp,
        )
        second = HARNESS.build_preflight_report(
            resolved,
            "run-deterministic",
            dict(reversed(list(outcomes.items()))),
            timestamp=self.timestamp,
        )

        encoded = HARNESS._serialized_json(first)
        self.assertEqual(encoded, HARNESS._serialized_json(second))
        self.assertLessEqual(len(first["capabilities"]), HARNESS.MAX_PREFLIGHT_CAPABILITIES)
        self.assertLessEqual(len(encoded), HARNESS.MAX_PREFLIGHT_BYTES)

    def test_failure_validation_rejects_lost_additional_preflight_context(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "flow.ui.player.input", "smoke"
        )
        outcomes = self._available_outcomes(resolved)
        for capability_id, status, classification, reason in (
            (
                "smapi-runtime",
                "missing",
                "environment-failure",
                "SMAPI_UNAVAILABLE",
            ),
            (
                "semantic-workflow",
                "error",
                "misconfiguration",
                "SEMANTIC_WORKFLOW_INVALID",
            ),
        ):
            outcomes[capability_id] = {
                "status": status,
                "classification": classification,
                "reasonCode": reason,
                "explanation": f"Capability {capability_id} is unavailable.",
            }
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory)
            result_path = artifact / "result.json"
            HARNESS.publish_preflight(
                artifact,
                result_path,
                resolved,
                "run-context-tamper",
                outcomes,
                timestamp=self.timestamp,
            )
            failure_path = artifact / "failure.json"
            failure = json.loads(failure_path.read_text(encoding="utf-8"))
            failure["additional_failures"][0]["phase"] = "scenario"
            failure_path.write_text(json.dumps(failure), encoding="utf-8")
            result = json.loads(result_path.read_text(encoding="utf-8"))

            with self.assertRaisesRegex(
                HARNESS.HarnessError,
                "loses typed capability preflight context",
            ):
                HARNESS.validate_failure_artifacts(result_path, result)

    def test_failure_validation_rejects_omitted_or_replaced_preflight_failure(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "flow.ui.player.input", "smoke"
        )
        outcomes = self._available_outcomes(resolved)
        for capability_id, classification, reason in (
            (
                "smapi-runtime",
                "environment-failure",
                "SMAPI_UNAVAILABLE",
            ),
            (
                "semantic-workflow",
                "misconfiguration",
                "SEMANTIC_WORKFLOW_INVALID",
            ),
        ):
            outcomes[capability_id] = {
                "status": "error",
                "classification": classification,
                "reasonCode": reason,
                "explanation": f"Capability {capability_id} is unavailable.",
            }
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory)
            result_path = artifact / "result.json"
            HARNESS.publish_preflight(
                artifact,
                result_path,
                resolved,
                "run-context-records",
                outcomes,
                timestamp=self.timestamp,
            )
            failure_path = artifact / "failure.json"
            original = json.loads(failure_path.read_text(encoding="utf-8"))
            result = json.loads(result_path.read_text(encoding="utf-8"))
            mutations = []
            omitted = copy.deepcopy(original)
            omitted["additional_failures"] = []
            mutations.append(omitted)
            replaced = copy.deepcopy(original)
            replaced["additional_failures"][0]["id"] = "HARNESS-EXECUTION"
            mutations.append(replaced)

            for failure in mutations:
                with self.subTest(
                    additional=failure["additional_failures"]
                ):
                    failure_path.write_text(
                        json.dumps(failure),
                        encoding="utf-8",
                    )
                    with self.assertRaisesRegex(
                        HARNESS.HarnessError,
                        "capability record set conflicts",
                    ):
                        HARNESS.validate_failure_artifacts(
                            result_path,
                            result,
                        )

    def test_report_rejects_unbounded_or_absolute_path_explanations(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "semantic.lifecycle", "ui"
        )
        for explanation in (
            "x" * (HARNESS.MAX_PREFLIGHT_EXPLANATION + 1),
            "Failure at /Users/example/private-file",
        ):
            outcomes = self._available_outcomes(resolved)
            outcomes["smapi-runtime"] = {
                "status": "missing",
                "classification": "environment-failure",
                "reasonCode": "SMAPI_UNAVAILABLE",
                "explanation": explanation,
            }
            with self.subTest(explanation=explanation[:32]):
                with self.assertRaises(HARNESS.HarnessError):
                    HARNESS.build_preflight_report(
                        resolved,
                        "run-bounds",
                        outcomes,
                        timestamp=self.timestamp,
                    )

    def test_report_contains_only_bounded_scalar_allowlisted_evidence(self) -> None:
        resolved = HARNESS.resolve_scenario(
            self.scenarios, "flow.ui.player.input", "smoke"
        )
        report = HARNESS.build_preflight_report(
            resolved,
            "run-safe",
            self._available_outcomes(resolved),
            timestamp=dt.datetime.now(dt.timezone.utc).isoformat(),
        )
        encoded = json.dumps(report)

        for forbidden in (
            '"environment":',
            '"traceback":',
            '"stack":',
            '"rawLog":',
            '"secret":',
        ):
            self.assertNotIn(forbidden, encoded)
        for capability in report["capabilities"]:
            self.assertTrue(
                all(
                    not isinstance(value, (dict, list))
                    for value in capability.values()
                )
            )


if __name__ == "__main__":
    unittest.main()
