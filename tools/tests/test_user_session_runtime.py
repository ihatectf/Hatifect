import copy
import contextlib
import datetime as dt
import fcntl
import importlib.util
import io
import json
import os
import shutil
import tempfile
import threading
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "live-harness" / "user_session_runtime.py"
SPEC = importlib.util.spec_from_file_location("hatifect_user_session_runtime", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
RUNTIME = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUNTIME)
REAL_LOAD_MODULE = RUNTIME._load_module
VALIDATOR = REAL_LOAD_MODULE(
    "hatifect_user_session_test_validator",
    ROOT / "tools" / "live-harness" / "validate.py",
)
SEMANTIC_SOURCE_ROOT = ROOT / "tools" / "live-harness"
SEMANTIC_SOURCE_FILES = (
    "semantic-test-agent.py",
    "semantic_agent_ui.py",
    "semantic_test_agent.py",
    "semantic_interactions.py",
    "native_button_lease.py",
    "macos_native_input_driver.py",
)


class _FakeDirect:
    MAX_TIMEOUT_SECONDS = 7200

    def __init__(self, repository: Path, checkout_sha: str):
        self.repository = repository.resolve()
        self.checkout_sha = checkout_sha

    def _repository_identity(self, repository: Path):
        resolved = repository.resolve(strict=True)
        info = resolved.stat()
        return resolved, info.st_dev, info.st_ino

    def _repository_head(self, _repository: Path):
        return self.checkout_sha

    def _contained(self, root: Path, candidate: Path):
        resolved_root = root.resolve(strict=True)
        resolved = candidate.resolve(strict=False)
        resolved.relative_to(resolved_root)
        return resolved

    def _validate_isolated_root(self, _repository: Path, isolated: Path):
        return isolated.resolve(strict=True)


class UserSessionRuntimeTests(unittest.TestCase):
    def test_roundtrip_request_rejects_legacy_and_other_run_save_names(self) -> None:
        save_root = self.isolated / 'config/StardewValley/Saves'
        save_root.mkdir(parents=True)
        self.request['scenarioId'] = 'flow.chest.roundtrip'
        self.validator.resolve_scenario.return_value['requiresSave'] = True
        spec = importlib.util.spec_from_file_location('test_real_save_naming', ROOT / 'tools/live-harness/save_provisioning.py')
        provisioner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(provisioner)
        self.direct._load_module = lambda *_: provisioner
        for name in (f'HatifectHarness_{uuid.UUID(self.request_id).hex}', 'HatifectHarness' + uuid.uuid4().hex + '_4242424242'):
            self.request['savePath'] = str(save_root / name)
            with self.assertRaisesRegex(RUNTIME.UserSessionRuntimeError, 'planned isolated working copy'):
                RUNTIME._validate_request(self.request, self.repository)
        self.request['savePath'] = str(save_root / provisioner._working_name(self.request_id, 'flow.chest.roundtrip'))
        self.assertEqual(self.request, RUNTIME._validate_request(self.request, self.repository))

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="hatifect-user-session-test.")
        self.repository = Path(self.temporary.name) / "repository"
        self.repository.mkdir()
        (self.repository / "tools" / "live-harness").mkdir(parents=True)
        self.request_id = str(uuid.uuid4())
        self.artifact = self.repository / "artifacts" / "runtime" / self.request_id
        self.artifact.mkdir(parents=True)
        self.isolated = self.repository / ".smapi-test" / "isolated"
        self.isolated.mkdir(parents=True)
        self.checkout_sha = "a" * 40
        self.direct = _FakeDirect(self.repository, self.checkout_sha)
        now = dt.datetime.now(dt.timezone.utc)
        info = self.repository.stat()
        self.request = {
            "protocolVersion": RUNTIME.PROTOCOL_VERSION,
            "requestType": RUNTIME.REQUEST_TYPE,
            "requestId": self.request_id,
            "repositoryRoot": str(self.repository),
            "repositoryDevice": info.st_dev,
            "repositoryInode": info.st_ino,
            "checkoutSha": self.checkout_sha,
            "kind": "ui",
            "scenarioId": "semantic.lifecycle",
            "isolatedRoot": str(self.isolated),
            "artifactDirectory": str(self.artifact),
            "resultPath": str(self.artifact / "result.json"),
            "savePath": None,
            "timeoutSeconds": 1800,
            "seed": 0,
            "createdAtUtc": RUNTIME._timestamp(now),
            "expiresAtUtc": RUNTIME._timestamp(
                now + dt.timedelta(seconds=RUNTIME.REQUEST_TTL_SECONDS)
            ),
        }
        self.validator = mock.Mock()
        self.validator.load_manifest.return_value = {}
        self.validator.resolve_scenario.return_value = {
            "id": "semantic.lifecycle",
            "kind": "ui",
            "requiresSave": False,
            "timeoutSeconds": 1800,
            "capabilities": [
                {"id": "artifact-writable", "requirement": "required"},
            ],
        }
        self.validator.PREFLIGHT_FILE_NAME = "preflight.json"
        self.validator.MAX_PREFLIGHT_BYTES = 64 * 1024
        self.validator.read_preflight_report.return_value = {
            "status": "PASS",
            "createdAtUtc": self.request["createdAtUtc"],
        }
        self.direct_patch = mock.patch.object(
            RUNTIME, "_direct_runtime", return_value=self.direct
        )
        self.load_patch = mock.patch.object(
            RUNTIME, "_load_module", return_value=self.validator
        )
        self.direct_patch.start()
        self.load_patch.start()

    def tearDown(self) -> None:
        self.load_patch.stop()
        self.direct_patch.stop()
        self.temporary.cleanup()

    def _install_semantic_workflow(
        self,
        *,
        document: dict | None = None,
        encoded: bytes | None = None,
        symlink: bool = False,
    ) -> Path:
        target_root = self.repository / "tools" / "live-harness"
        for name in SEMANTIC_SOURCE_FILES:
            shutil.copy2(SEMANTIC_SOURCE_ROOT / name, target_root / name)
        workflow_root = target_root / "semantic-tests"
        workflow_root.mkdir(exist_ok=True)
        workflow = workflow_root / "flow.ui.player.input.json"
        if encoded is None:
            value = document
            if value is None:
                value = json.loads(
                    (
                        SEMANTIC_SOURCE_ROOT
                        / "semantic-tests"
                        / "flow.ui.player.input.json"
                    ).read_text(encoding="utf-8")
                )
            encoded = json.dumps(value).encode("utf-8")
        if symlink:
            external = self.repository / "foreign-workflow.json"
            external.write_bytes(encoded)
            workflow.symlink_to(external)
        else:
            workflow.write_bytes(encoded)
        return workflow

    def _semantic_workflow(self, steps: list[dict]) -> dict:
        document = json.loads(
            (
                SEMANTIC_SOURCE_ROOT
                / "semantic-tests"
                / "flow.ui.player.input.json"
            ).read_text(encoding="utf-8")
        )
        document["steps"] = steps
        return document

    def _probe_semantic_workflow(self) -> dict[str, str]:
        def load(name: str, path: Path):
            if path.name == "validate.py":
                return self.validator
            return REAL_LOAD_MODULE(name, path)

        resolved = {
            "id": "flow.ui.player.input",
            "timeoutSeconds": 1200,
            "capabilities": [
                {"id": "semantic-workflow", "requirement": "required"},
            ],
        }
        with mock.patch.object(RUNTIME, "_load_module", side_effect=load):
            return RUNTIME._probe_capabilities(
                self.repository,
                resolved,
                self.isolated,
                self.artifact,
                "",
                "1200",
                "0",
            )["semantic-workflow"]

    def test_typed_request_accepts_exact_workspace_identity(self) -> None:
        validated = RUNTIME._validate_request(
            self.request, self.repository, now=RUNTIME._parse_timestamp(self.request["createdAtUtc"])
        )

        self.assertEqual(validated["requestId"], self.request_id)
        self.assertEqual(validated["scenarioId"], "semantic.lifecycle")
        self.assertEqual(validated["checkoutSha"], self.checkout_sha)

    def test_artifact_writability_probe_removes_its_request_owned_file(self) -> None:
        self.assertTrue(RUNTIME._artifact_writable(self.artifact))
        self.assertEqual(list(self.artifact.glob(".preflight-write-probe-*.tmp")), [])

    def test_valid_semantic_workflow_is_available_without_runtime_side_effects(self) -> None:
        self._install_semantic_workflow()
        with (
            mock.patch.object(
                RUNTIME, "_macos_gui_available", side_effect=AssertionError
            ),
            mock.patch.object(
                RUNTIME, "_quartz_post_events_available", side_effect=AssertionError
            ),
            mock.patch("subprocess.Popen", side_effect=AssertionError),
            mock.patch("subprocess.run", side_effect=AssertionError),
            mock.patch.object(RUNTIME.ctypes, "CDLL", side_effect=AssertionError),
        ):
            outcome = self._probe_semantic_workflow()

        self.assertEqual(outcome["status"], "available")
        self.assertEqual(outcome["classification"], "available")

    def test_malformed_semantic_workflow_is_a_misconfiguration(self) -> None:
        self._install_semantic_workflow(encoded=b"{")

        outcome = self._probe_semantic_workflow()

        self.assertEqual(
            (outcome["status"], outcome["classification"], outcome["reasonCode"]),
            ("error", "misconfiguration", "SEMANTIC_WORKFLOW_INVALID"),
        )

    def test_invalid_semantic_workflow_publishes_canonical_blocked_result(self) -> None:
        self._install_semantic_workflow(encoded=b"{")
        resolved = {
            "id": "flow.ui.player.input",
            "capabilities": [
                {"id": "semantic-workflow", "requirement": "required"},
            ],
        }

        report = VALIDATOR.publish_preflight(
            self.artifact,
            self.artifact / "result.json",
            resolved,
            self.request_id,
            {"semantic-workflow": self._probe_semantic_workflow()},
            timestamp=self.request["createdAtUtc"],
        )
        result = json.loads(
            (self.artifact / "result.json").read_text(encoding="utf-8")
        )
        failure = json.loads(
            (self.artifact / "failure.json").read_text(encoding="utf-8")
        )

        self.assertEqual(report["status"], "BLOCKED")
        self.assertEqual(result["status"], "BLOCKED")
        self.assertEqual(
            result["assertions"][0]["id"],
            "HARNESS-PREFLIGHT-SEMANTIC-WORKFLOW",
        )
        self.assertIn(
            "SEMANTIC_WORKFLOW_INVALID",
            result["assertions"][0]["actual"],
        )
        self.assertEqual(failure["phase"], "preflight")
        self.assertEqual(
            failure["failure_class"], "PREFLIGHT_MISCONFIGURATION"
        )
        self.assertEqual(
            failure["causal_component"], "semantic-test-agent"
        )

    def test_oversized_semantic_workflow_is_a_misconfiguration(self) -> None:
        self._install_semantic_workflow(encoded=b" " * (256 * 1024 + 1))

        outcome = self._probe_semantic_workflow()

        self.assertEqual(
            (outcome["status"], outcome["classification"], outcome["reasonCode"]),
            ("error", "misconfiguration", "SEMANTIC_WORKFLOW_INVALID"),
        )

    def test_foreign_semantic_workflow_identity_is_a_misconfiguration(self) -> None:
        document = json.loads(
            (
                SEMANTIC_SOURCE_ROOT
                / "semantic-tests"
                / "flow.ui.player.input.json"
            ).read_text(encoding="utf-8")
        )
        document["id"] = "foreign.scenario"
        self._install_semantic_workflow(document=document)

        outcome = self._probe_semantic_workflow()

        self.assertEqual(
            (outcome["status"], outcome["classification"], outcome["reasonCode"]),
            ("error", "misconfiguration", "SEMANTIC_WORKFLOW_INVALID"),
        )

    def test_invalid_semantic_workflow_schema_operation_and_selector_are_rejected(self) -> None:
        source = json.loads(
            (
                SEMANTIC_SOURCE_ROOT
                / "semantic-tests"
                / "flow.ui.player.input.json"
            ).read_text(encoding="utf-8")
        )
        invalid_documents = []
        invalid_schema = copy.deepcopy(source)
        invalid_schema["unexpected"] = True
        invalid_documents.append(invalid_schema)
        invalid_operation = copy.deepcopy(source)
        invalid_operation["steps"][0]["op"] = "execute"
        invalid_documents.append(invalid_operation)
        invalid_selector = copy.deepcopy(source)
        selector_step = next(
            step for step in invalid_selector["steps"] if "selector" in step
        )
        selector_step["selector"]["coordinate"] = "1,1"
        invalid_documents.append(invalid_selector)

        for document in invalid_documents:
            with self.subTest(step=document["steps"][0]["op"]):
                self._install_semantic_workflow(document=document)
                outcome = self._probe_semantic_workflow()
                self.assertEqual(
                    (
                        outcome["status"],
                        outcome["classification"],
                        outcome["reasonCode"],
                    ),
                    ("error", "misconfiguration", "SEMANTIC_WORKFLOW_INVALID"),
                )

    def test_semantic_workflow_rejects_missing_operation_operands(self) -> None:
        source = json.loads(
            (
                SEMANTIC_SOURCE_ROOT
                / "semantic-tests"
                / "flow.ui.player.input.json"
            ).read_text(encoding="utf-8")
        )
        cases = (
            ("key", "key"),
            ("text", "value"),
            ("move", "source"),
        )
        for operation, field in cases:
            with self.subTest(operation=operation, field=field):
                document = copy.deepcopy(source)
                step = next(
                    item
                    for item in document["steps"]
                    if item["op"] == operation
                )
                del step[field]
                self._install_semantic_workflow(document=document)

                outcome = self._probe_semantic_workflow()

                self.assertEqual(
                    (
                        outcome["status"],
                        outcome["classification"],
                        outcome["reasonCode"],
                    ),
                    ("error", "misconfiguration", "SEMANTIC_WORKFLOW_INVALID"),
                )

    def test_semantic_workflow_rejects_invalid_operation_condition_payloads(self) -> None:
        source = json.loads(
            (
                SEMANTIC_SOURCE_ROOT
                / "semantic-tests"
                / "flow.ui.player.input.json"
            ).read_text(encoding="utf-8")
        )
        invalid_documents = []
        missing_wait_conditions = copy.deepcopy(source)
        wait = next(
            item
            for item in missing_wait_conditions["steps"]
            if item["op"] == "wait"
        )
        del wait["conditions"]
        invalid_documents.append(
            ("missing wait conditions", missing_wait_conditions)
        )
        foreign_element_source = copy.deepcopy(source)
        wait_element = next(
            item
            for item in foreign_element_source["steps"]
            if item["op"] == "waitElement"
        )
        wait_element["conditions"][0]["source"] = "domain"
        invalid_documents.append(
            ("foreign element condition source", foreign_element_source)
        )
        invalid_after_count = copy.deepcopy(source)
        wait_capture = next(
            item
            for item in invalid_after_count["steps"]
            if item["op"] == "waitCapture"
        )
        wait_capture["afterCount"] = {"nested": "count"}
        invalid_documents.append(("nested afterCount", invalid_after_count))

        for label, document in invalid_documents:
            with self.subTest(case=label):
                self._install_semantic_workflow(document=document)

                outcome = self._probe_semantic_workflow()

                self.assertEqual(
                    (
                        outcome["status"],
                        outcome["classification"],
                        outcome["reasonCode"],
                    ),
                    ("error", "misconfiguration", "SEMANTIC_WORKFLOW_INVALID"),
                )

    def test_variable_contract_failures_block_probe_without_side_effects(self) -> None:
        capture_overflow = [
            {
                "id": f"capture-{index}",
                "op": "capture",
                "source": "domain",
                "variable": f"captured{index}",
            }
            for index in range(126)
        ]
        mixed_overflow = capture_overflow[:124] + [
            {
                "id": f"discover-{index}",
                "op": "discover",
                "variable": f"discovered{index}",
            }
            for index in range(2)
        ]
        invalid_workflows = (
            (
                "capture overflow",
                self._semantic_workflow(capture_overflow),
            ),
            (
                "mixed overflow",
                self._semantic_workflow(mixed_overflow),
            ),
            (
                "unbounded capture name",
                self._semantic_workflow(
                    [
                        {
                            "id": "capture-long",
                            "op": "capture",
                            "source": "domain",
                            "variable": "v" * 97,
                        }
                    ]
                ),
            ),
            (
                "unknown template",
                self._semantic_workflow(
                    [
                        {
                            "id": "unknown-template",
                            "op": "text",
                            "value": "${missing}",
                        }
                    ]
                ),
            ),
            (
                "forward template",
                self._semantic_workflow(
                    [
                        {
                            "id": "forward-template",
                            "op": "text",
                            "value": "${later}",
                        },
                        {
                            "id": "capture-later",
                            "op": "capture",
                            "source": "domain",
                            "variable": "later",
                        },
                    ]
                ),
            ),
        )
        with (
            mock.patch.object(
                RUNTIME, "_macos_gui_available", side_effect=AssertionError
            ),
            mock.patch.object(
                RUNTIME, "_quartz_post_events_available", side_effect=AssertionError
            ),
            mock.patch("subprocess.Popen", side_effect=AssertionError),
            mock.patch("subprocess.run", side_effect=AssertionError),
            mock.patch.object(RUNTIME.ctypes, "CDLL", side_effect=AssertionError),
        ):
            for label, document in invalid_workflows:
                with self.subTest(case=label):
                    self._install_semantic_workflow(document=document)

                    outcome = self._probe_semantic_workflow()

                    self.assertEqual(
                        (
                            outcome["status"],
                            outcome["classification"],
                            outcome["reasonCode"],
                        ),
                        (
                            "error",
                            "misconfiguration",
                            "SEMANTIC_WORKFLOW_INVALID",
                        ),
                    )

    def test_symlinked_semantic_workflow_is_a_misconfiguration(self) -> None:
        self._install_semantic_workflow(symlink=True)

        outcome = self._probe_semantic_workflow()

        self.assertEqual(
            (outcome["status"], outcome["classification"], outcome["reasonCode"]),
            ("error", "misconfiguration", "SEMANTIC_WORKFLOW_INVALID"),
        )

    def test_all_capability_probes_have_an_actual_available_path(self) -> None:
        capability_ids = (
            "artifact-writable",
            "request-parameters",
            "smapi-runtime",
            "isolated-deployment",
            "required-mods",
            "isolated-save-fixture",
            "user-session-executor",
            "semantic-workflow",
            "user-session-gui",
            "quartz-post-events",
        )
        resolved = {
            "id": "flow.ui.player.input",
            "timeoutSeconds": 1200,
            "requiredMods": ["Required.Mod"],
            "capabilities": [
                {"id": capability_id, "requirement": "required"}
                for capability_id in capability_ids
            ],
        }
        smapi = self.repository / "smapi"
        smapi.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        smapi.chmod(0o700)
        for path in (
            self.isolated / "deployment.json",
            self.isolated / "deployment.identity.json",
            self.isolated / "Mods" / "Hatifect" / "Hatifect UI" / "manifest.json",
        ):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("{}\n", encoding="utf-8")
        (
            self.repository / ".smapi-test" / "user-session-runtime"
        ).mkdir(parents=True)
        validator = mock.Mock()
        validator.validate_required_mods.return_value = []
        deployment = mock.Mock()
        provisioner = mock.Mock()
        semantic = mock.Mock()

        def load(_name: str, path: Path):
            return {
                "validate.py": validator,
                "deployment_identity.py": deployment,
                "save_provisioning.py": provisioner,
                "semantic-test-agent.py": semantic,
            }[path.name]

        with (
            mock.patch.object(RUNTIME, "_load_module", side_effect=load),
            mock.patch.object(RUNTIME, "_ready_state", return_value={}),
            mock.patch.object(RUNTIME.sys, "platform", "darwin"),
            mock.patch.object(RUNTIME, "_macos_gui_available", return_value=True),
            mock.patch.object(
                RUNTIME, "_quartz_post_events_available", return_value=True
            ),
        ):
            outcomes = RUNTIME._probe_capabilities(
                self.repository,
                resolved,
                self.isolated,
                self.artifact,
                str(smapi),
                "1200",
                "0",
            )

        self.assertEqual(list(outcomes), list(capability_ids))
        self.assertEqual(
            {
                capability_id: (
                    outcome["status"],
                    outcome["reasonCode"],
                )
                for capability_id, outcome in outcomes.items()
            },
            {
                capability_id: ("available", "AVAILABLE")
                for capability_id in capability_ids
            },
        )
        validator.validate_deployment_marker.assert_called_once_with(
            self.isolated / "deployment.json"
        )
        validator.validate_required_mods.assert_called_once()
        deployment.validate_identity.assert_called_once()
        provisioner.probe_fixture.assert_called_once_with(self.isolated, smapi)
        semantic.load_spec.assert_called_once()

    def test_owner_probe_failures_have_representative_typed_outcomes(self) -> None:
        cases = (
            (
                "request-parameters",
                "",
                "1200",
                "invalid",
                "REQUEST_PARAMETERS_INVALID",
            ),
            ("smapi-runtime", "", "1200", "0", "SMAPI_UNAVAILABLE"),
            (
                "isolated-deployment",
                "",
                "1200",
                "0",
                "DEPLOYMENT_UNAVAILABLE",
            ),
            ("required-mods", "", "1200", "0", "REQUIRED_MOD_MISSING"),
            (
                "isolated-save-fixture",
                "",
                "1200",
                "0",
                "SAVE_FIXTURE_UNAVAILABLE",
            ),
            (
                "user-session-executor",
                "",
                "1200",
                "0",
                "EXECUTOR_UNAVAILABLE",
            ),
        )
        self.validator.validate_required_mods.return_value = ["Required.Mod"]
        for capability_id, smapi, timeout, seed, reason in cases:
            with self.subTest(capability=capability_id):
                resolved = {
                    "id": "flow.ui.player.input",
                    "timeoutSeconds": 1200,
                    "requiredMods": ["Required.Mod"],
                    "capabilities": [
                        {"id": capability_id, "requirement": "required"},
                    ],
                }
                outcomes = RUNTIME._probe_capabilities(
                    self.repository,
                    resolved,
                    self.isolated,
                    self.artifact,
                    smapi,
                    timeout,
                    seed,
                )
                self.assertEqual(outcomes[capability_id]["reasonCode"], reason)
                self.assertNotEqual(outcomes[capability_id]["status"], "available")

    def test_deployment_identity_save_fixture_and_executor_owner_failures_are_typed(self) -> None:
        for path in (
            self.isolated / "deployment.json",
            self.isolated / "deployment.identity.json",
            self.isolated / "Mods" / "Hatifect" / "Hatifect UI" / "manifest.json",
        ):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("{}\n", encoding="utf-8")
        smapi = self.repository / "smapi"
        smapi.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        smapi.chmod(0o700)
        state_root = self.repository / ".smapi-test" / "user-session-runtime"
        state_root.mkdir(parents=True)
        validator = mock.Mock()
        deployment = mock.Mock()
        deployment.validate_identity.side_effect = ValueError("foreign deployment")
        provisioner = mock.Mock()
        provisioner.probe_fixture.side_effect = FileNotFoundError

        def load(_name: str, path: Path):
            return {
                "validate.py": validator,
                "deployment_identity.py": deployment,
                "save_provisioning.py": provisioner,
            }[path.name]

        cases = (
            (
                "isolated-deployment",
                "error",
                "misconfiguration",
                "DEPLOYMENT_INVALID",
            ),
            (
                "isolated-save-fixture",
                "missing",
                "environment-failure",
                "SAVE_FIXTURE_UNAVAILABLE",
            ),
            (
                "user-session-executor",
                "missing",
                "environment-failure",
                "EXECUTOR_UNAVAILABLE",
            ),
        )
        with (
            mock.patch.object(RUNTIME, "_load_module", side_effect=load),
            mock.patch.object(
                RUNTIME, "_ready_state", side_effect=ValueError("not Ready")
            ),
        ):
            for capability_id, status, classification, reason in cases:
                with self.subTest(capability=capability_id):
                    outcomes = RUNTIME._probe_capabilities(
                        self.repository,
                        {
                            "id": "flow.ui.player.input",
                            "timeoutSeconds": 1200,
                            "requiredMods": [],
                            "capabilities": [
                                {
                                    "id": capability_id,
                                    "requirement": "required",
                                },
                            ],
                        },
                        self.isolated,
                        self.artifact,
                        str(smapi),
                        "1200",
                        "0",
                    )
                    self.assertEqual(
                        (
                            outcomes[capability_id]["status"],
                            outcomes[capability_id]["classification"],
                            outcomes[capability_id]["reasonCode"],
                        ),
                        (status, classification, reason),
                    )

        deployment.validate_identity.assert_called_once()
        provisioner.probe_fixture.assert_called_once_with(self.isolated, smapi)

    def test_non_macos_gui_and_quartz_are_deterministically_unsupported(self) -> None:
        resolved = {
            "id": "flow.ui.player.input",
            "capabilities": [
                {"id": "user-session-gui", "requirement": "required"},
                {"id": "quartz-post-events", "requirement": "required"},
            ],
        }
        with (
            mock.patch.object(RUNTIME.sys, "platform", "linux"),
            mock.patch.object(
                RUNTIME, "_macos_gui_available", side_effect=AssertionError
            ),
            mock.patch.object(
                RUNTIME, "_quartz_post_events_available", side_effect=AssertionError
            ),
        ):
            outcomes = RUNTIME._probe_capabilities(
                self.repository,
                resolved,
                self.isolated,
                self.artifact,
                "",
                "1200",
                "0",
            )

        for capability_id in ("user-session-gui", "quartz-post-events"):
            self.assertEqual(
                (
                    outcomes[capability_id]["status"],
                    outcomes[capability_id]["classification"],
                    outcomes[capability_id]["reasonCode"],
                ),
                (
                    "unsupported",
                    "unsupported-capability",
                    "PLATFORM_UNSUPPORTED",
                ),
            )

    def test_macos_gui_and_quartz_probes_run_at_user_session_boundary(self) -> None:
        resolved = {
            "id": "flow.ui.player.input",
            "capabilities": [
                {"id": "user-session-gui", "requirement": "required"},
                {"id": "quartz-post-events", "requirement": "required"},
            ],
        }
        with (
            mock.patch.object(RUNTIME.sys, "platform", "darwin"),
            mock.patch.object(
                RUNTIME, "_macos_gui_available", return_value=False
            ) as gui_probe,
            mock.patch.object(
                RUNTIME, "_quartz_post_events_available", return_value=False
            ) as quartz_probe,
        ):
            outcomes = RUNTIME._probe_capabilities(
                self.repository,
                resolved,
                self.isolated,
                self.artifact,
                "",
                "1200",
                "0",
            )

        gui_probe.assert_called_once_with()
        quartz_probe.assert_called_once_with()
        self.assertEqual(outcomes["user-session-gui"]["status"], "missing")
        self.assertEqual(
            outcomes["quartz-post-events"]["reasonCode"],
            "QUARTZ_ACCESSIBILITY_DENIED",
        )

    def test_blocked_gui_preflight_never_enters_direct_runtime(self) -> None:
        self.validator.read_preflight_report.return_value = {
            "status": "BLOCKED",
            "createdAtUtc": self.request["createdAtUtc"],
        }
        self.direct._direct_metadata = mock.Mock()
        self.direct.direct = mock.Mock()

        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "capability preflight did not pass",
        ):
            RUNTIME._process_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )

        self.direct._direct_metadata.assert_not_called()
        self.direct.direct.assert_not_called()

    def test_request_identity_mismatches_fail_closed(self) -> None:
        mutations = {
            "protocolVersion": RUNTIME.PROTOCOL_VERSION + 1,
            "requestId": str(uuid.uuid4()),
            "scenarioId": "unknown.scenario",
            "checkoutSha": "b" * 40,
        }
        for field, value in mutations.items():
            with self.subTest(field=field):
                changed = dict(self.request)
                changed[field] = value
                if field == "requestId":
                    changed["artifactDirectory"] = self.request["artifactDirectory"]
                if field == "scenarioId":
                    self.validator.resolve_scenario.side_effect = ValueError("unknown scenario")
                else:
                    self.validator.resolve_scenario.side_effect = None
                with self.assertRaises((RUNTIME.UserSessionRuntimeError, ValueError)):
                    RUNTIME._validate_request(
                        changed,
                        self.repository,
                        now=RUNTIME._parse_timestamp(self.request["createdAtUtc"]),
                    )

    def test_typed_rejection_result_checks_protocol_id_scenario_and_sha(self) -> None:
        result = RUNTIME._executor_result(
            self.request,
            state="Rejected",
            status="BLOCKED",
            exit_code=2,
            message="rejected",
            direct_path=None,
        )
        RUNTIME._validate_result(result, self.request)

        for field, value in (
            ("protocolVersion", 99),
            ("requestId", str(uuid.uuid4())),
            ("scenarioId", "runtime.boot"),
            ("checkoutSha", "b" * 40),
        ):
            with self.subTest(field=field):
                changed = dict(result)
                changed[field] = value
                with self.assertRaisesRegex(
                    RUNTIME.UserSessionRuntimeError,
                    "protocolVersion, requestId, scenario, or checkout SHA",
                ):
                    RUNTIME._validate_result(changed, self.request)

    def test_atomic_mailbox_publication_has_no_partial_leftovers(self) -> None:
        mailbox = self.repository / ".smapi-test" / "user-session-runtime" / "request.json"

        RUNTIME._atomic_write_json(mailbox, self.request, replace=True)
        replacement = dict(self.request)
        replacement["seed"] = 1
        RUNTIME._atomic_write_json(mailbox, replacement, replace=True)

        self.assertEqual(json.loads(mailbox.read_text(encoding="utf-8"))["seed"], 1)
        self.assertEqual(list(mailbox.parent.glob("*.tmp")), [])
        self.assertEqual(list(mailbox.parent.glob(".*.tmp")), [])

    def test_mailbox_reader_rejects_symlinked_document(self) -> None:
        target = self.repository / "target.json"
        target.write_text("{}\n", encoding="utf-8")
        link = self.repository / "request-link.json"
        link.symlink_to(target)

        with self.assertRaises(OSError):
            RUNTIME._read_json(link)

    def test_workspace_mailbox_rejects_symlinked_smapi_test_root(self) -> None:
        repository = Path(self.temporary.name) / "symlinked-repository"
        repository.mkdir()
        external = Path(self.temporary.name) / "external-state"
        external.mkdir()
        (repository / ".smapi-test").symlink_to(external, target_is_directory=True)

        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "Unsafe user-session state directory",
        ):
            RUNTIME._state_root(repository)

    def test_server_source_digest_set_fails_closed_after_change(self) -> None:
        source = Path(self.temporary.name) / "transport.py"
        source.write_text("original\n", encoding="utf-8")
        digests = RUNTIME._capture_digests((source,))

        RUNTIME._verify_digests(digests)
        source.write_text("changed\n", encoding="utf-8")

        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "source changed",
        ):
            RUNTIME._verify_digests(digests)

    def test_running_source_drift_defers_restart_without_request_cancellation(self) -> None:
        restart_pending = threading.Event()
        with mock.patch.object(
            RUNTIME,
            "_verify_digests",
            side_effect=RUNTIME.WorkerRestartRequested("changed"),
        ):
            RUNTIME._observe_running_source_drift({}, restart_pending)

        self.assertTrue(restart_pending.is_set())

    def test_request_rejects_symlinked_artifact_directory(self) -> None:
        target = self.repository / "artifacts" / "runtime" / f"target-{self.request_id}"
        target.mkdir()
        self.artifact.rmdir()
        self.artifact.symlink_to(target, target_is_directory=True)

        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "must not be a symlink",
        ):
            RUNTIME._validate_request(
                self.request,
                self.repository,
                now=RUNTIME._parse_timestamp(self.request["createdAtUtc"]),
            )

    def test_accepted_executor_failure_without_direct_evidence_stays_blocked(self) -> None:
        result = RUNTIME._executor_result(
            self.request,
            state="Failed",
            status="BLOCKED",
            exit_code=2,
            message="executor failure",
            direct_path=None,
        )

        self.assertEqual(RUNTIME._validate_result(result, self.request)["status"], "BLOCKED")

    def test_unexpected_accepted_request_failure_is_typed_and_next_request_can_run(self) -> None:
        completed = RUNTIME._executor_result(
            self.request,
            state="Completed",
            status="PASS",
            exit_code=0,
            message="complete",
            direct_path=self.artifact / "diagnostics" / "transport-result.json",
        )

        with mock.patch.object(
            RUNTIME,
            "_process_request",
            side_effect=[RuntimeError("unexpected request failure"), completed],
        ), mock.patch.object(RUNTIME, "_validate_result", side_effect=lambda result, _request: result):
            failed = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )
            recovered = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )

        self.assertEqual(failed["state"], "Failed")
        self.assertEqual(failed["status"], "BLOCKED")
        self.assertEqual(failed["exitCode"], 2)
        self.assertIn("RuntimeError: unexpected request failure", failed["message"])
        evidence = RUNTIME._read_json(
            self.artifact / "diagnostics" / "executor-failure.json"
        )
        self.assertEqual(evidence["exceptionType"], "RuntimeError")
        self.assertIn("unexpected request failure", evidence["traceback"])
        self.assertEqual(recovered["status"], "PASS")

    def test_timed_out_child_request_does_not_poison_the_next_request(self) -> None:
        timed_out = RUNTIME._executor_result(
            self.request,
            state="TimedOut",
            status="BLOCKED",
            exit_code=2,
            message="child timeout",
            direct_path=self.artifact / "diagnostics" / "transport-result.json",
        )
        completed = RUNTIME._executor_result(
            self.request,
            state="Completed",
            status="PASS",
            exit_code=0,
            message="complete",
            direct_path=self.artifact / "diagnostics" / "transport-result.json",
        )
        with mock.patch.object(
            RUNTIME,
            "_process_request",
            side_effect=[timed_out, completed],
        ), mock.patch.object(RUNTIME, "_validate_result", side_effect=lambda result, _request: result):
            first = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )
            second = RUNTIME._process_accepted_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )

        self.assertEqual((first["state"], first["status"]), ("TimedOut", "BLOCKED"))
        self.assertEqual((second["state"], second["status"]), ("Completed", "PASS"))

    def test_single_run_lock_rejects_concurrent_owner(self) -> None:
        lock_path = self.repository / ".smapi-test" / "user-session-runtime" / "serve.lock"
        lock_path.parent.mkdir(parents=True)
        with lock_path.open("a+b") as first:
            fcntl.flock(first.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaisesRegex(
                RUNTIME.UserSessionRuntimeError, "already owns"
            ):
                with RUNTIME._exclusive_lock(lock_path, "already owns"):
                    self.fail("concurrent executor lock must not be acquired")

    def test_executor_delegates_to_canonical_direct_runtime_and_binds_result(self) -> None:
        direct_request = {
            "repositoryHead": self.checkout_sha,
            "requestId": self.request_id,
            "scenarioId": "semantic.lifecycle",
        }
        direct_result = {
            "requestId": self.request_id,
            "scenarioId": "semantic.lifecycle",
            "state": "Completed",
            "status": "PASS",
            "exitCode": 0,
            "message": "complete",
        }
        transport_path = self.artifact / "diagnostics" / "transport-result.json"

        def execute(_args):
            transport_path.parent.mkdir(parents=True)
            (self.artifact / "request.json").write_text(
                json.dumps(direct_request), encoding="utf-8"
            )
            transport_path.write_text(json.dumps(direct_result), encoding="utf-8")
            return 0

        delegated = mock.Mock()
        delegated._direct_metadata.return_value = {"repositoryHead": self.checkout_sha}
        delegated.direct.side_effect = execute
        delegated._read_json.side_effect = lambda path: json.loads(
            Path(path).read_text(encoding="utf-8")
        )
        delegated._validate_response.side_effect = lambda document, _request: document

        with mock.patch.object(
            RUNTIME, "_validate_request", return_value=self.request
        ), mock.patch.object(RUNTIME, "_direct_runtime", return_value=delegated):
            result = RUNTIME._process_request(
                self.request,
                self.repository,
                Path("/game/StardewModdingAPI"),
            )

        delegated.direct.assert_called_once()
        self.assertEqual(result["status"], "PASS")
        self.assertEqual(result["requestId"], self.request_id)
        self.assertEqual(result["scenarioId"], "semantic.lifecycle")
        self.assertEqual(result["checkoutSha"], self.checkout_sha)
        self.assertEqual(result["directTransportResultPath"], str(transport_path))

    def test_status_accepts_stale_ready_checkout_but_rejects_stale_running_checkout(self) -> None:
        root = self.repository / ".smapi-test" / "user-session-runtime"
        root.mkdir(parents=True, exist_ok=True)
        state = RUNTIME._state_document(
            self.repository,
            str(uuid.uuid4()),
            RUNTIME._timestamp(),
            "Ready",
            "ready",
        )
        RUNTIME._atomic_write_json(root / "state.json", state, replace=True)

        output = io.StringIO()
        with mock.patch.object(RUNTIME, "_state_root", return_value=root), \
             contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            self.assertEqual(
                RUNTIME.status(SimpleNamespace(repository_root=str(self.repository))),
                0,
            )
            state["checkoutSha"] = "b" * 40
            RUNTIME._atomic_write_json(root / "state.json", state, replace=True)
            self.assertEqual(
                RUNTIME.status(SimpleNamespace(repository_root=str(self.repository))),
                0,
            )
            state["lifecycleState"] = "Running"
            state["requestId"] = self.request_id
            state["scenarioId"] = "semantic.lifecycle"
            RUNTIME._atomic_write_json(root / "state.json", state, replace=True)
            self.assertEqual(
                RUNTIME.status(SimpleNamespace(repository_root=str(self.repository))),
                2,
            )

    def test_status_validates_expanded_supervisor_worker_identity(self) -> None:
        supervisor_id = str(uuid.uuid4())
        worker_id = str(uuid.uuid4())
        state = RUNTIME._state_document(
            self.repository,
            supervisor_id,
            RUNTIME._timestamp(),
            "Ready",
            "ready",
        )
        state.update(
            {
                "supervisorId": supervisor_id,
                "supervisorPid": state["pid"],
                "workerId": worker_id,
                "workerPid": state["pid"] + 1,
                "workerGeneration": 2,
                "restartReason": "Worker source digest changed",
                "restartCount": 1,
                "workerSourceDigest": "c" * 64,
                "acceptedCheckoutSha": self.checkout_sha,
                "heartbeat": state["heartbeatAtUtc"],
                "activeRequestId": None,
            }
        )

        validated = RUNTIME._validate_state(state, self.repository, require_live=False)
        self.assertEqual(validated["supervisorId"], supervisor_id)
        self.assertEqual(validated["workerId"], worker_id)
        changed = dict(state)
        changed["activeRequestId"] = self.request_id
        with self.assertRaisesRegex(
            RUNTIME.UserSessionRuntimeError,
            "aliases or counters",
        ):
            RUNTIME._validate_state(changed, self.repository, require_live=False)

    def test_doctor_accepts_ready_executor_without_vscode_configuration(self) -> None:
        state_path = self.repository / ".smapi-test" / "user-session-runtime" / "state.json"
        state = RUNTIME._state_document(
            self.repository, str(uuid.uuid4()), RUNTIME._timestamp(), "Ready", "ready"
        )
        RUNTIME._atomic_write_json(state_path, state, replace=True)
        self.direct._direct_metadata = mock.Mock()
        args = SimpleNamespace(
            repository_root=str(self.repository),
            isolated_root=str(self.isolated),
            smapi_path="/game/StardewModdingAPI",
        )
        task_path = self.repository / ".vscode" / "tasks.json"

        for task_content in (None, "invalid JSON", '{"tasks": []}'):
            with self.subTest(task_content=task_content):
                if task_content is not None:
                    task_path.parent.mkdir(exist_ok=True)
                    task_path.write_text(task_content, encoding="utf-8")
                self.direct._direct_metadata.reset_mock()
                output = io.StringIO()
                with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                    exit_code = RUNTIME.doctor(args)

                self.assertEqual(exit_code, 0, output.getvalue())
                self.assertIn("RESULT: PASS", output.getvalue())
                self.assertIn("executor is live and ready", output.getvalue())
                self.direct._direct_metadata.assert_called_once_with(
                    self.repository.resolve(), self.isolated, Path(args.smapi_path)
                )

    def test_doctor_rejects_invalid_transport_for_ready_executor(self) -> None:
        state_path = self.repository / ".smapi-test" / "user-session-runtime" / "state.json"
        state = RUNTIME._state_document(
            self.repository, str(uuid.uuid4()), RUNTIME._timestamp(), "Ready", "ready"
        )
        RUNTIME._atomic_write_json(state_path, state, replace=True)
        self.direct._direct_metadata = mock.Mock(side_effect=ValueError("Invalid transport metadata"))
        output = io.StringIO()

        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            exit_code = RUNTIME.doctor(SimpleNamespace(
                repository_root=str(self.repository),
                isolated_root=str(self.isolated),
                smapi_path="/game/StardewModdingAPI",
            ))

        self.assertEqual(exit_code, 2)
        self.assertIn("BLOCKED: Invalid transport metadata", output.getvalue())
        self.assertIn("executor is live and ready", output.getvalue())
        self.assertNotIn("RESULT: PASS", output.getvalue())

    def test_doctor_rejects_unavailable_executor_with_valid_transport(self) -> None:
        state_path = self.repository / ".smapi-test" / "user-session-runtime" / "state.json"
        self.direct._direct_metadata = mock.Mock()
        for lifecycle in (None, "Ready", "Stopped", "Running"):
            with self.subTest(lifecycle=lifecycle):
                if lifecycle is not None:
                    state = RUNTIME._state_document(
                        self.repository, str(uuid.uuid4()), RUNTIME._timestamp(), lifecycle, "unavailable"
                    )
                    if lifecycle == "Ready":
                        state["heartbeatAtUtc"] = RUNTIME._timestamp(
                            dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=1)
                        )
                    RUNTIME._atomic_write_json(state_path, state, replace=True)
                output = io.StringIO()
                with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                    exit_code = RUNTIME.doctor(SimpleNamespace(
                        repository_root=str(self.repository),
                        isolated_root=str(self.isolated),
                        smapi_path="/game/StardewModdingAPI",
                    ))

                self.assertEqual(exit_code, 2)
                self.assertIn("PASS: canonical direct-process transport", output.getvalue())
                self.assertIn("RESULT: BLOCKED", output.getvalue())
                self.assertNotIn("executor is live and ready", output.getvalue())

    def test_submit_without_executor_reports_direct_terminal_command(self) -> None:
        output = io.StringIO()
        with mock.patch.object(RUNTIME, "_build_request", return_value=self.request), \
             mock.patch.object(RUNTIME.time, "monotonic", side_effect=[0, RUNTIME.READY_WAIT_SECONDS]), \
             contextlib.redirect_stderr(output):
            exit_code = RUNTIME.submit(SimpleNamespace(repository_root=str(self.repository)))

        self.assertEqual(exit_code, 2)
        self.assertIn("rtk proxy ./tools/hatifect-runtime-executor serve", output.getvalue())
        self.assertNotIn("VS Code", output.getvalue())
        state_root = self.repository / ".smapi-test" / "user-session-runtime"
        self.assertFalse((state_root / "request.json").exists())

    def test_vscode_task_is_explicit_process_singleton(self) -> None:
        task_document = json.loads(
            (ROOT / ".vscode" / "tasks.json").read_text(encoding="utf-8")
        )
        task = task_document["tasks"][0]

        self.assertEqual(task["type"], "process")
        self.assertEqual(
            task["command"],
            "${workspaceFolder}/tools/hatifect-runtime-executor",
        )
        self.assertEqual(task["args"], ["serve"])
        self.assertEqual(task["options"], {"cwd": "${workspaceFolder}"})
        self.assertTrue(task["isBackground"])
        self.assertEqual(task["runOptions"], {"instanceLimit": 1})
        self.assertNotIn("runOn", task["runOptions"])
        wrapper = (ROOT / "tools" / "hatifect-runtime-executor").read_text(encoding="utf-8")
        self.assertIn('supervisor="$TOOLS_DIR/live-harness/user_session_supervisor.py"', wrapper)
        self.assertIn('exec python3 "$supervisor"', wrapper)
        self.assertNotIn('"$executor" serve', wrapper)

    def test_new_executor_start_atomically_replaces_stale_heartbeat_state(self) -> None:
        state_path = self.repository / ".smapi-test" / "user-session-runtime" / "state.json"
        stale = RUNTIME._state_document(
            self.repository,
            str(uuid.uuid4()),
            RUNTIME._timestamp(dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=1)),
            "Stopped",
            "stale",
        )
        stale["heartbeatAtUtc"] = RUNTIME._timestamp(
            dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=1)
        )
        RUNTIME._atomic_write_json(state_path, stale, replace=True)
        replacement = RUNTIME._state_document(
            self.repository,
            str(uuid.uuid4()),
            RUNTIME._timestamp(),
            "Ready",
            "fresh",
        )

        RUNTIME._atomic_write_json(state_path, replacement, replace=True)

        observed = RUNTIME._read_json(state_path)
        self.assertEqual(observed["executorId"], replacement["executorId"])
        self.assertEqual(observed["lifecycleState"], "Ready")
        self.assertNotEqual(observed["heartbeatAtUtc"], stale["heartbeatAtUtc"])

    def test_task_wiring_does_not_launch_an_editor_or_gui_bootstrap(self) -> None:
        task = json.loads(
            (ROOT / ".vscode" / "tasks.json").read_text(encoding="utf-8")
        )["tasks"][0]

        self.assertEqual(
            {task["command"], *task["args"]},
            {"${workspaceFolder}/tools/hatifect-runtime-executor", "serve"},
        )

    def test_executor_publishes_idle_and_running_heartbeats_every_second(self) -> None:
        implementation = MODULE_PATH.read_text(encoding="utf-8")

        self.assertIn("heartbeat_due = now + 1.0", implementation)
        self.assertIn("heartbeat_stop.wait(1.0)", implementation)
        self.assertIn('executor_state("Ready"', implementation)
        self.assertIn('executor_state(\n                                        "Running"', implementation)
        self.assertIn('checkout_sha = trusted["checkoutSha"]', implementation)

    def test_executor_shutdown_requests_direct_runtime_cancellation(self) -> None:
        implementation = MODULE_PATH.read_text(encoding="utf-8")

        self.assertIn('if stop_requested:', implementation)
        self.assertIn(
            '"User-session executor shutdown requested"',
            implementation,
        )

    def test_executor_implementation_has_no_forbidden_bootstrap_transport(self) -> None:
        implementation = MODULE_PATH.read_text(encoding="utf-8")
        supervisor = (
            ROOT / "tools" / "live-harness" / "user_session_supervisor.py"
        ).read_text(encoding="utf-8")
        task = (ROOT / ".vscode" / "tasks.json").read_text(encoding="utf-8")
        wrapper = (ROOT / "tools" / "hatifect-runtime-executor").read_text(encoding="utf-8")
        combined = implementation + supervisor + task + wrapper

        for forbidden in (
            "Terminal.app",
            "LaunchAgent",
            "launchctl",
            "Apple Events",
            "osascript",
            "sudo",
        ):
            self.assertNotIn(forbidden, combined)


if __name__ == "__main__":
    unittest.main()
