import copy
import importlib.util
import json
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace


ROOT = Path(__file__).resolve().parents[2]
AGENT_PATH = ROOT / "tools" / "live-harness" / "semantic-test-agent.py"
ENGINE_PATH = ROOT / "tools" / "live-harness" / "semantic_agent_ui.py"
INTERACTIONS_PATH = ROOT / "tools" / "live-harness" / "semantic_interactions.py"
SPEC_PATH = ROOT / "tools" / "live-harness" / "semantic-tests" / "flow.ui.player.input.json"
RUNNER_PATH = ROOT / "tools" / "hatifect-live-runner"

SPEC = importlib.util.spec_from_file_location("hatifect_semantic_test_agent", AGENT_PATH)
assert SPEC is not None and SPEC.loader is not None
AGENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AGENT)


class SemanticTestAgentTests(unittest.TestCase):
    def setUp(self) -> None:
        self.document = json.loads(SPEC_PATH.read_text(encoding="utf-8"))

    def test_checked_in_flow_workflow_is_valid_bounded_and_model_driven(self) -> None:
        validated = AGENT.validate_spec(copy.deepcopy(self.document), "flow.ui.player.input")
        self.assertEqual(validated["schemaVersion"], 1)
        self.assertEqual(validated["platform"], "macos-quartz")
        self.assertGreaterEqual(len(validated["steps"]), 90)
        self.assertLessEqual(len(validated["steps"]), AGENT.MAX_STEPS)
        ids = [step["id"] for step in validated["steps"]]
        self.assertEqual(len(ids), len(set(ids)))

        actions = {
            step["selector"]["action"]
            for step in validated["steps"]
            if step["op"] == "activate" and "action" in step.get("selector", {})
        }
        self.assertEqual(actions, {
            "Hatifect.Flow/network/action/register",
            "Hatifect.Flow/network/action/link",
            "Hatifect.Flow/network/action/send",
            "Hatifect.Flow/network/action/send-quantity",
        })
        self.assertEqual(sum(step["op"] == "fill" for step in validated["steps"]), 3)
        self.assertEqual(sum(step["op"] == "select" for step in validated["steps"]), 5)
        self.assertEqual(sum(step["op"] == "reveal" for step in validated["steps"]), 5)
        self.assertEqual(sum(step["op"] == "activate" for step in validated["steps"]), 5)
        self.assertEqual(sum(step["op"] == "focus" for step in validated["steps"]), 1)
        self.assertTrue(all(
            step.get("collection")
            for step in validated["steps"]
            if step["op"] == "select"
        ))
        keys = [step.get("key") for step in validated["steps"] if step["op"] == "key"]
        self.assertIn("K", keys)
        self.assertIn("Tab", keys)
        self.assertIn("Backspace", keys)
        self.assertIn("Escape", keys)

    def test_workflow_is_semantic_not_coordinate_or_timing_playback(self) -> None:
        raw = SPEC_PATH.read_text(encoding="utf-8")
        self.assertNotIn('"sleep"', raw)
        self.assertNotIn('"x"', raw)
        self.assertNotIn('"y"', raw)
        self.assertNotIn('"bounds"', raw)
        self.assertNotIn('"screenX"', raw)
        self.assertNotIn('"screenY"', raw)
        for step in self.document["steps"]:
            selector = step.get("selector")
            if not isinstance(selector, dict):
                continue
            self.assertTrue(set(selector).issubset({
                "semantic", "semanticPrefix", "action", "name", "role", "collection", "node"
            }))
            if "name" in selector:
                self.assertTrue(
                    selector["name"].startswith("${"),
                    "Visible/localized labels must not drive the workflow",
                )
        fills = [step for step in self.document["steps"] if step["op"] == "fill"]
        self.assertTrue(fills)
        self.assertTrue(all(set(step["selector"]) == {"semantic"} for step in fills))

    def test_agent_has_no_direct_hatifect_execution_backdoor(self) -> None:
        body = "\n".join(
            path.read_text(encoding="utf-8")
            for path in (AGENT_PATH, ENGINE_PATH, INTERACTIONS_PATH)
        )
        for forbidden in (
            "ActionAutomation", "InsertAutomationText", "FlowNetworkCommand(", "FlowSendCommand(",
            "OpenPlayerNetwork(", "eval(", "exec(", "os.system", "subprocess.Popen",
        ):
            self.assertNotIn(forbidden, body)
        self.assertIn(
            "CGEventPost",
            (ROOT / "tools/live-harness/macos_native_input_driver.py").read_text(encoding="utf-8"),
        )

    def test_spec_rejects_arbitrary_execution_unknown_ops_and_path_escape(self) -> None:
        for operation in ("shell", "python", "eval", "dispatch"):
            with self.subTest(operation=operation):
                document = copy.deepcopy(self.document)
                document["steps"][0]["op"] = operation
                with self.assertRaisesRegex(AGENT.SemanticAgentError, "Unsupported semantic test operation"):
                    AGENT.validate_spec(document, "flow.ui.player.input")

        document = copy.deepcopy(self.document)
        document["evidence"]["domain"]["path"] = "../foreign.json"
        with self.assertRaisesRegex(AGENT.SemanticAgentError, "below diagnostics"):
            AGENT.validate_spec(document, "flow.ui.player.input")

    def test_spec_rejects_duplicate_steps_ambiguous_predicates_and_foreign_identity(self) -> None:
        document = copy.deepcopy(self.document)
        document["steps"][1]["id"] = document["steps"][0]["id"]
        with self.assertRaisesRegex(AGENT.SemanticAgentError, "unique"):
            AGENT.validate_spec(document, "flow.ui.player.input")

        document = copy.deepcopy(self.document)
        wait = next(step for step in document["steps"] if step["op"] == "wait")
        wait["conditions"][0]["truthy"] = True
        with self.assertRaisesRegex(AGENT.SemanticAgentError, "exactly one predicate"):
            AGENT.validate_spec(document, "flow.ui.player.input")

        with self.assertRaisesRegex(AGENT.SemanticAgentError, "identity/version mismatch"):
            AGENT.validate_spec(copy.deepcopy(self.document), "other.scenario")

    def test_templates_paths_and_transforms_are_deterministic(self) -> None:
        variables = {"count": 3, "name": "alpha"}
        self.assertEqual(AGENT._template("${count}", variables), 3)
        self.assertEqual(AGENT._template("x-${name}-${count}", variables), "x-alpha-3")
        state = {"items": [{"value": "a"}, {"value": "b"}], "Nested": {"Flag": True}}
        self.assertEqual(AGENT._path(state, "items[-1].value"), "b")
        self.assertTrue(AGENT._path(state, "nested.flag"))
        parcel = "12345678-1234-4234-8234-123456789abc"
        self.assertEqual(AGENT._transform(parcel, "uuidHex"), "12345678123442348234123456789abc")
        self.assertEqual(AGENT._transform([1, 2, 3], "count"), 3)

    def test_predicates_cover_state_counts_identity_and_retained_phases(self) -> None:
        predicate = AGENT.SemanticController._predicate
        self.assertTrue(predicate([1, 2], {"countGreaterThan": 1}))
        self.assertTrue(predicate("ready", {"startsWith": "rea"}))
        self.assertTrue(predicate(
            "Delivered · attempts: 1",
            {"containsAny": ["Delivered", "Доставлено"]},
        ))
        self.assertTrue(predicate(
            "00000000-0000-0000-0000-000000000000",
            {"uuidZero": True},
        ))
        captures = [{"phase": "Ready"}, {"phase": "Pointer"}, {"phase": "Text"}]
        self.assertTrue(predicate(
            captures,
            {"containsAll": {"path": "phase", "values": ["Ready", "Text"]}},
        ))
        self.assertFalse(predicate(
            captures,
            {"containsAll": {"path": "phase", "values": ["Tab"]}},
        ))

    def test_pending_evidence_retries_but_contract_failures_do_not(self) -> None:
        controller = object.__new__(AGENT.SemanticController)
        controller.deadline = time.monotonic() + 1
        attempts = 0

        def pending_then_ready():
            nonlocal attempts
            attempts += 1
            if attempts < 3:
                raise AGENT.SemanticEvidencePending("frame not ready")
            return "ready"

        self.assertEqual(controller._wait("pending frame", pending_then_ready, seconds=0.5), "ready")
        self.assertEqual(attempts, 3)
        with self.assertRaisesRegex(AGENT.SemanticAgentError, "foreign identity"):
            controller._wait(
                "fatal identity",
                lambda: (_ for _ in ()).throw(AGENT.SemanticAgentError("foreign identity")),
                seconds=0.5,
            )

        controller.latest = lambda **_kwargs: {"visible": True, "elements": []}
        with self.assertRaises(AGENT.SemanticElementPending):
            controller.find_element(semantic="A/B")
        duplicate = {
            "nodeId": "one",
            "semanticId": "A/B", "actionId": None, "name": "x", "role": "Button",
            "enabled": True,
            "bounds": {"x": 0, "y": 0, "width": 10, "height": 10},
            "clip": {"x": 0, "y": 0, "width": 10, "height": 10},
        }
        controller.latest = lambda **_kwargs: {
            "visible": True,
            "elements": [duplicate, {**duplicate, "nodeId": "two"}],
        }
        with self.assertRaisesRegex(AGENT.SemanticAgentError, "ambiguous"):
            controller.find_element(semantic="A/B")

    def test_minimal_spec_runs_against_detached_evidence_without_native_backend_calls(self) -> None:
        request_id = "11111111-1111-4111-8111-111111111111"
        with tempfile.TemporaryDirectory(prefix="hatifect-semantic-agent-test.") as temporary:
            artifact = Path(temporary) / request_id
            diagnostics = artifact / "diagnostics"
            diagnostics.mkdir(parents=True)
            domain = {
                "requestId": request_id,
                "scenarioId": "example.semantic",
                "value": [1, 2, 3],
                "inputTelemetry": {"latest": {"gameActive": True}},
            }
            ui = {
                "runId": request_id,
                "scenario": "example.semantic",
                "failure": None,
                "latest": {"visible": False, "completedFrame": 1},
            }
            (diagnostics / "domain.json").write_text(json.dumps(domain), encoding="utf-8")
            (diagnostics / "ui.json").write_text(json.dumps(ui), encoding="utf-8")
            spec = {
                "schemaVersion": 1,
                "id": "example.semantic",
                "platform": "macos-quartz",
                "evidence": {
                    "domain": {
                        "path": "diagnostics/domain.json",
                        "identity": {"requestId": "${requestId}", "scenarioId": "${scenarioId}"},
                    },
                    "ui": {
                        "path": "diagnostics/ui.json",
                        "identity": {"runId": "${requestId}", "scenario": "${scenarioId}"},
                    },
                },
                "gameActive": {
                    "source": "domain",
                    "path": "inputTelemetry.latest.gameActive",
                    "activateAppContains": "Game",
                },
                "steps": [
                    {
                        "id": "capture-count", "op": "capture", "source": "domain",
                        "path": "value", "variable": "observed", "transform": "count",
                    },
                    {
                        "id": "assert-count", "op": "assert",
                        "conditions": [{"source": "domain", "path": "value", "countEquals": "${observed}"}],
                    },
                ],
            }
            AGENT.validate_spec(copy.deepcopy(spec), "example.semantic")
            backend = SimpleNamespace()
            controller = AGENT.SemanticController(
                artifact, request_id, "example.semantic", 5, backend, spec, "a" * 64
            )
            controller.run_spec()
            status = json.loads(
                (diagnostics / "semantic-test-agent.json").read_text(encoding="utf-8")
            )
            self.assertEqual(status["state"], "Completed")
            self.assertEqual(status["variables"]["observed"], 3)
            self.assertEqual(status["specSha256"], "a" * 64)

    def test_live_runner_discovers_checked_in_semantic_specs_instead_of_scenario_code(self) -> None:
        runner = RUNNER_PATH.read_text(encoding="utf-8")
        self.assertIn(
            'semantic_test_agent="$TOOLS_DIR/live-harness/semantic-test-agent.py"',
            runner,
        )
        self.assertIn(
            'semantic_tests_root="$TOOLS_DIR/live-harness/semantic-tests"',
            runner,
        )
        self.assertIn('semantic_spec="$semantic_tests_root/$scenario.json"', runner)
        self.assertIn('if [[ ! -f "$semantic_spec" ]]; then', runner)
        self.assertIn('exec python3 "$user_session_runtime"', runner)
        self.assertIn('--scenario-id "$scenario"', runner)
        self.assertNotIn('[[ "$scenario" != "flow.ui.player.input" ]]', runner)


if __name__ == "__main__":
    unittest.main()
