from __future__ import annotations

import concurrent.futures
import copy
import hashlib
import importlib.util
import json
import os
import shutil
import stat
import subprocess
import tempfile
import unittest
import uuid
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
PACKET_PATH = ROOT / "tools" / "live-harness" / "diagnostic_packet.py"
SPEC = importlib.util.spec_from_file_location("hatifect_diagnostic_packet_tests", PACKET_PATH)
assert SPEC is not None and SPEC.loader is not None
PACKET = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKET)

VALIDATE_PATH = ROOT / "tools" / "live-harness" / "validate.py"
VALIDATE_SPEC = importlib.util.spec_from_file_location("hatifect_diagnostic_validate_tests", VALIDATE_PATH)
assert VALIDATE_SPEC is not None and VALIDATE_SPEC.loader is not None
VALIDATE = importlib.util.module_from_spec(VALIDATE_SPEC)
VALIDATE_SPEC.loader.exec_module(VALIDATE)

TIMESTAMP = "2026-09-13T00:00:00Z"


class DiagnosticPacketTests(unittest.TestCase):
    def setUp(self) -> None:
        scratch = ROOT / "artifacts" / "test-temp"
        scratch.mkdir(parents=True, exist_ok=True)
        self.work = Path(tempfile.mkdtemp(prefix="hatifect-diagnostic-", dir=scratch))
        self.addCleanup(shutil.rmtree, self.work, True)
        self.runtime = self.work / "runtime"
        self.runtime.mkdir(mode=0o700)
        self.old_runtime = PACKET.RUNTIME_ROOT
        PACKET.RUNTIME_ROOT = self.runtime
        self.addCleanup(setattr, PACKET, "RUNTIME_ROOT", self.old_runtime)
        self.run_id = str(uuid.uuid4())

    def _request(self, scenario: str = "runtime.boot", kind: str = "smoke") -> dict:
        info = PACKET.ROOT.stat()
        head = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "--verify", "HEAD"],
            check=True, capture_output=True, text=True,
        ).stdout.strip()
        now = "2026-09-13T18:00:00Z"
        return {
            "protocolVersion": 2, "requestType": "runScenario", "requestId": self.run_id,
            "repositoryRoot": str(PACKET.ROOT), "repositoryDevice": info.st_dev,
            "repositoryInode": info.st_ino, "repositoryHead": head,
            "environmentId": "isolated-smapi-v1", "kind": kind, "scenarioId": scenario,
            "isolatedRoot": str(self.work / "isolated"), "artifactDirectory": str(self.source),
            "resultPath": str(self.source / "result.json"), "savePath": None,
            "timeoutSeconds": 600, "seed": 7, "createdAtUtc": now,
            "expiresAtUtc": "2026-09-13T18:00:30Z",
        }

    @property
    def source(self) -> Path:
        return self.runtime / self.run_id

    def _make_run(
        self, *, scenario: str = "runtime.boot", kind: str = "smoke",
        status: str = "FAIL", root_id: str = "runtime.boot.loaded",
        events: int = 0, artifacts: list[dict[str, str]] | None = None,
        extra_records: int = 0,
    ) -> None:
        self.source.mkdir(mode=0o700)
        result = {
            "protocolVersion": 1, "runId": self.run_id, "scenario": scenario,
            "status": status, "durationMs": 12,
            "assertions": [{
                "id": root_id, "status": status, "subject": "bounded diagnostic",
                "expected": "success", "actual": "failure",
            }],
            "exceptions": [], "artifacts": artifacts or [],
        }
        result["assertions"].extend(
            {
                "id": f"additional.failure.{index}",
                "status": status,
                "subject": "bounded diagnostic",
                "expected": "e" * 2048,
                "actual": "a" * 2048,
            }
            for index in range(extra_records)
        )
        (self.source / "request.json").write_text(
            json.dumps(self._request(scenario, kind)), encoding="utf-8"
        )
        (self.source / "result.json").write_text(json.dumps(result), encoding="utf-8")
        for artifact in artifacts or []:
            path = self.source / artifact["path"]
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"x" * (40 * 1024 if path.suffix == ".log" else 8))
        for index in range(events):
            VALIDATE.record_semantic_event(
                self.source, scenario=scenario, run_id=self.run_id,
                event="Runtime.StateChanged", fields={"state": str(index)},
                timestamp=TIMESTAMP,
            )
        if events:
            (self.source / VALIDATE.SEMANTIC_EVENT_FILE_NAME).chmod(0o600)
        VALIDATE.write_failure_artifacts(
            self.source / "result.json", result, timestamp=TIMESTAMP,
            phase="runtime",
        )

    def _packet(self) -> dict:
        with mock.patch.object(
            PACKET, "_select_reproduction",
            return_value={"targetScenario": "runtime.boot", "effectivePhase": "preflight"},
        ):
            return PACKET.generate_packet(self.run_id)

    def _files(self) -> dict[str, object]:
        document = json.loads((self.source / PACKET.PACKET_FILE_NAME).read_text())
        return {item["path"]: item["content"] for item in document["files"]}

    def test_packet_is_byte_deterministic_with_authoritative_files_and_fields(self) -> None:
        self._make_run()
        first = self._packet()
        first_bytes = (self.source / PACKET.PACKET_FILE_NAME).read_bytes()
        second = self._packet()
        self.assertEqual(second["status"], "existing")
        self.assertEqual(first_bytes, (self.source / PACKET.PACKET_FILE_NAME).read_bytes())
        document = json.loads(first_bytes)
        self.assertEqual([item["path"] for item in document["files"]], list(PACKET.PACKET_FILE_ORDER))
        self.assertEqual(document["formatVersion"], 1)
        manifest = self._files()["packet-manifest.json"]
        self.assertEqual(manifest["files"], list(PACKET.PACKET_FILE_ORDER))
        summary = self._files()["diagnostic-summary.md"]
        for field in (
            "Run ID", "Scenario", "Status", "Root failure ID", "Execution phase",
            "Failure class", "Causal component", "Expected", "Actual", "Message",
            "Result fingerprint", "Additional failures", "Cleanup failures",
            "Reproduction",
        ):
            self.assertIn(field, summary)
        self.assertEqual(first["sha256"], hashlib.sha256(first_bytes).hexdigest())
        self.assertEqual(set(self._files()), set(PACKET.PACKET_FILE_ORDER))
        self.assertEqual(stat.S_IMODE((self.source / PACKET.PACKET_FILE_NAME).stat().st_mode), 0o600)
        self.assertEqual(list(self.source.glob(".diagnostic-packet.json.*.tmp")), [])

    def test_hard_budget_truncates_secondary_semantic_and_records_but_keeps_identity(self) -> None:
        self._make_run(events=0)
        self._packet()
        files = self._files()
        raw = PACKET._build_bounded_packet(
            summary="summary",
            failure=files["failure.json"],
            semantic={"formatVersion": 1, "includedCount": 0, "excludedCount": 0, "fullStreamPath": None, "completionStatus": "unavailable", "events": []},
            preflight={"environmentSummary": {}, "preflight": None},
            reproduction=files["reproduction.json"],
            artifacts=[],
            context={
                "scenario": "runtime.boot", "resolvedScenarioOwner": "runtime.boot",
                "rootFailureId": "runtime.boot.loaded", "rootComponent": "runtime",
                "resolvedComponentOwner": "runtime-executor", "directDependencies": [],
                "primarySourceFiles": [], "scenarioTestFiles": [],
                "secondaryExpansionCandidates": [f"source/{i}" for i in range(20000)],
                "secondaryExpansionOmitted": 0, "ownershipSource": "test",
                "repositoryHead": "0" * 40,
            },
        )
        manifest = json.loads(raw)["files"][-1]["content"]
        self.assertLessEqual(len(raw), PACKET.MAX_PACKET_BYTES)
        failure = files["failure.json"]
        self.assertEqual(failure["sourceResultFingerprint"], manifest["resultFingerprint"])
        self.assertEqual(failure["envelope"]["root_failure"]["id"], "runtime.boot.loaded")
        self.assertEqual(files["reproduction.json"]["command"], f"./tools/hatifect-repro {self.run_id}")
        self.assertGreater(manifest["truncation"]["secondaryContextOmitted"], 0)
        self.assertEqual(manifest["serializedBytes"], len(raw))

    def test_semantic_tail_counts_and_bounds_are_projected(self) -> None:
        self._make_run(events=20)
        events = VALIDATE.read_semantic_events(
            self.source, expected_scenario="runtime.boot", expected_run_id=self.run_id
        )
        failure = json.loads((self.source / "failure.json").read_text())
        semantic = PACKET._semantic_projection(VALIDATE, failure, events)
        self.assertLessEqual(semantic["includedCount"], VALIDATE.MAX_FAILURE_TAIL_EVENTS)
        self.assertEqual(semantic["includedCount"] + semantic["excludedCount"], len(events))
        self.assertLessEqual(
            sum(len(VALIDATE.serialize_semantic_event(event)) for event in semantic["events"]),
            VALIDATE.MAX_FAILURE_TAIL_BYTES,
        )
        self.assertEqual(semantic["completionStatus"], "unavailable")

    def test_typed_reproduction_for_fail_and_blocked(self) -> None:
        fail = {"status": "FAIL"}
        with mock.patch.object(PACKET, "_select_reproduction", return_value={
            "targetScenario": "runtime.boot", "effectivePhase": "preflight"
        }):
            self.assertEqual(
                PACKET._reproduction_projection(self.run_id, fail)["command"],
                f"./tools/hatifect-repro {self.run_id}",
            )
        blocked = PACKET._reproduction_projection(self.run_id, {"status": "BLOCKED"})
        self.assertEqual(blocked["reasonCode"], "STATUS_BLOCKED")
        self.assertFalse(blocked["available"])

    def test_artifact_references_exclude_raw_binary_and_missing_with_reasons(self) -> None:
        artifacts = [
            {"type": "smapi-log", "path": "smapi.log"},
            {"type": "screenshot", "path": "screen.png"},
            {"type": "text", "path": "missing.txt"},
        ]
        self._make_run(artifacts=artifacts)
        (self.source / "missing.txt").unlink()
        self._packet()
        references = self._files()["artifacts.json"]["artifacts"]
        by_path = {item["path"]: item for item in references}
        self.assertEqual(by_path["smapi.log"]["exclusionReason"], "RAW_LOG_REFERENCE_ONLY")
        self.assertEqual(by_path["screen.png"]["exclusionReason"], "BINARY_ARTIFACT_REFERENCE_ONLY")
        self.assertEqual(by_path["missing.txt"]["exclusionReason"], "MISSING_REFERENCED_ARTIFACT")

    def test_artifact_below_missing_intermediate_directory_is_reported_missing(self) -> None:
        artifacts = [{"type": "text", "path": "missing/nested/evidence.txt"}]
        self._make_run(artifacts=artifacts)
        shutil.rmtree(self.source / "missing")

        self._packet()

        references = self._files()["artifacts.json"]["artifacts"]
        item = next(
            reference for reference in references
            if reference["path"] == "missing/nested/evidence.txt"
        )
        self.assertEqual(item["inclusionStatus"], "excluded")
        self.assertEqual(item["exclusionReason"], "MISSING_REFERENCED_ARTIFACT")

    def test_artifact_below_intermediate_symlink_remains_ownership_violation(self) -> None:
        artifacts = [{"type": "text", "path": "linked/evidence.txt"}]
        self._make_run(artifacts=artifacts)
        linked = self.source / "linked"
        shutil.rmtree(linked)
        real = self.source / "real"
        real.mkdir()
        (real / "evidence.txt").write_text("evidence", encoding="utf-8")
        linked.symlink_to(real, target_is_directory=True)

        with self.assertRaises(PACKET.DiagnosticPacketError) as captured:
            self._packet()

        self.assertEqual("OWNERSHIP_VIOLATION", captured.exception.reason_code)

    def test_preflight_projection_is_typed_and_environment_allowlisted(self) -> None:
        failure = {"environment_summary": {"status": "BLOCKED", "reason": "x"}}
        preflight = {
            "formatVersion": 1, "scenario": "runtime.boot", "runId": self.run_id,
            "status": "BLOCKED", "counts": {"required": 1, "passed": 0, "blocked": 1},
            "capabilities": [{
                "id": "smapi", "requirement": "required", "status": "BLOCKED",
                "classification": "UNAVAILABLE", "reasonCode": "MISSING",
                "owner": "runtime-environment", "probeLayer": "environment",
                "extra": "not projected",
            }],
        }
        projection = PACKET._preflight_environment_projection(preflight, failure)
        self.assertNotIn("extra", projection["preflight"]["capabilities"][0])
        self.assertEqual(projection["environmentSummary"], failure["environment_summary"])

    def test_valid_preflight_is_projected_and_mismatched_identity_is_rejected(self) -> None:
        self._make_run()
        resolved = VALIDATE.resolve_scenario(
            VALIDATE.load_manifest(), "runtime.boot", "smoke"
        )
        outcomes = {
            item["id"]: {
                "status": "available",
                "classification": "available",
                "reasonCode": "AVAILABLE",
                "explanation": "Available.",
            }
            for item in resolved["capabilities"]
        }
        report = VALIDATE.build_preflight_report(
            resolved, self.run_id, outcomes, timestamp=TIMESTAMP
        )
        VALIDATE.write_preflight_report(self.source / "preflight.json", report)
        self._packet()
        projection = self._files()["preflight-environment-summary.json"]["preflight"]
        self.assertEqual(projection["counts"], report["counts"])
        self.assertNotIn("createdAtUtc", projection)
        self.assertNotIn("observedAtUtc", projection["capabilities"][0])

        (self.source / PACKET.PACKET_FILE_NAME).unlink()
        report["runId"] = str(uuid.uuid4())
        (self.source / "preflight.json").write_text(json.dumps(report), encoding="utf-8")
        with self.assertRaisesRegex(PACKET.DiagnosticPacketError, "preflight"):
            PACKET.generate_packet(self.run_id)

    def test_context_is_stable_and_aggregate_owner_uses_check_owners(self) -> None:
        self._make_run(scenario="all", kind="ui", root_id="semantic.lifecycle.visual")
        self._packet()
        context = self._files()["context-manifest.json"]
        resolved = VALIDATE.resolve_scenario(VALIDATE.load_manifest(), "all", "ui")
        expected_owner = resolved["checkOwners"]["semantic.lifecycle.visual"]
        self.assertEqual(context["resolvedScenarioOwner"], expected_owner)
        self.assertIn("checkOwners", context["ownershipSource"])
        self.assertTrue(all(not Path(path).is_absolute() for path in context["primarySourceFiles"]))
        self.assertTrue(all(not Path(path).is_absolute() for path in context["scenarioTestFiles"]))

    def test_every_registered_scenario_and_aggregate_owner_has_valid_context(self) -> None:
        scenarios = VALIDATE.load_manifest()
        head = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "--verify", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        for scenario_id, scenario in scenarios.items():
            resolved = VALIDATE.resolve_scenario(
                scenarios, scenario_id, scenario["kind"]
            )
            roots = (
                list(resolved["checkOwners"])
                if scenario_id == "all"
                else [resolved["checks"][0]]
            )
            for root_id in roots:
                with self.subTest(scenario=scenario_id, root_id=root_id):
                    context = PACKET._build_context_manifest(
                        VALIDATE,
                        {"kind": scenario["kind"], "repositoryHead": head},
                        {"scenario": scenario_id},
                        {
                            "causal_component": "scenario",
                            "root_failure": {"id": root_id},
                        },
                    )
                    self.assertTrue(context["primarySourceFiles"])
                    self.assertTrue(context["scenarioTestFiles"])

    def test_every_emitted_causal_component_has_an_explicit_owner(self) -> None:
        descriptor = os.open(ROOT, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        self.addCleanup(os.close, descriptor)
        mapping = PACKET._load_context_mapping(descriptor)
        emitted = {
            definition["owner"]
            for definition in VALIDATE.CAPABILITY_DEFINITIONS.values()
        } | {
            "deployment-preparer",
            "harness-preflight",
            "harness-validator",
            "reproduction-planner",
            "runtime-cleanup",
            "runtime-executor",
            "runtime-options",
            "runtime-process",
            "runtime-worker",
            "save-provisioner",
            "save-provisioning",
            "scenario-manifest",
            "user-session-runtime",
        }
        self.assertEqual(emitted - set(mapping["components"]), set())

    def test_unknown_and_ambiguous_context_fail_closed(self) -> None:
        with self.assertRaisesRegex(PACKET.DiagnosticPacketError, "no context owner"):
            PACKET._scenario_rule([], "unknown", "smoke")
        rules = [
            {"pattern": "x*", "kinds": ["smoke"], "component": "a",
             "context": {}},
            {"pattern": "x*", "kinds": ["smoke"], "component": "b",
             "context": {}},
        ]
        with self.assertRaisesRegex(PACKET.DiagnosticPacketError, "ambiguous"):
            PACKET._scenario_rule(rules, "x.test", "smoke")

    def test_context_paths_reject_traversal_symlink_hard_link_and_non_owner(self) -> None:
        descriptor = os.open(ROOT, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        self.addCleanup(os.close, descriptor)
        with self.assertRaises(PACKET.DiagnosticPacketError):
            PACKET._verify_repository_files(descriptor, ["../outside"])
        with tempfile.TemporaryDirectory(prefix="hatifect-diagnostic-links-") as directory:
            root = Path(directory)
            (root / "real.txt").write_text("x")
            (root / "link.txt").symlink_to(root / "real.txt")
            local = os.open(root, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
            self.addCleanup(os.close, local)
            with self.assertRaises(PACKET.DiagnosticPacketError):
                PACKET._stat_file_at(local, "link.txt", "link")
            os.link(root / "real.txt", root / "hard.txt")
            with self.assertRaises(PACKET.DiagnosticPacketError):
                PACKET._stat_file_at(local, "hard.txt", "hard")

    def test_canonical_symlink_and_hard_link_inputs_are_rejected(self) -> None:
        for link_kind in ("symlink", "hardlink"):
            with self.subTest(link_kind=link_kind):
                if self.source.exists():
                    shutil.rmtree(self.source)
                self._make_run()
                original = self.source / "failure.original.json"
                failure = self.source / "failure.json"
                failure.rename(original)
                if link_kind == "symlink":
                    failure.symlink_to(original)
                else:
                    os.link(original, failure)
                with self.assertRaises(PACKET.DiagnosticPacketError):
                    PACKET.generate_packet(self.run_id)

    def test_identity_mismatch_stale_fingerprint_run_and_scenario_is_rejected(self) -> None:
        self._make_run()
        request = json.loads((self.source / "request.json").read_text())
        request["repositoryHead"] = "0" * 40
        (self.source / "request.json").write_text(json.dumps(request))
        with self.assertRaisesRegex(PACKET.DiagnosticPacketError, "stale"):
            PACKET.generate_packet(self.run_id)
        shutil.rmtree(self.source)
        self._make_run()
        result = json.loads((self.source / "result.json").read_text())
        result["durationMs"] = 99
        (self.source / "result.json").write_text(json.dumps(result))
        with self.assertRaises(PACKET.DiagnosticPacketError):
            PACKET.generate_packet(self.run_id)
        for field, value in (("runId", str(uuid.uuid4())), ("scenario", "save.bootstrap")):
            shutil.rmtree(self.source)
            self._make_run()
            result = json.loads((self.source / "result.json").read_text())
            result[field] = value
            (self.source / "result.json").write_text(json.dumps(result))
            with self.subTest(field=field), self.assertRaises(PACKET.DiagnosticPacketError):
                PACKET.generate_packet(self.run_id)

    def test_request_run_and_scenario_mismatch_are_rejected(self) -> None:
        for field, value in (
            ("requestId", str(uuid.uuid4())),
            ("scenarioId", "save.bootstrap"),
        ):
            with self.subTest(field=field):
                if self.source.exists():
                    shutil.rmtree(self.source)
                self._make_run()
                request = json.loads((self.source / "request.json").read_text())
                request[field] = value
                (self.source / "request.json").write_text(json.dumps(request))
                with self.assertRaisesRegex(
                    PACKET.DiagnosticPacketError, "request.json"
                ):
                    PACKET.generate_packet(self.run_id)

    def test_unknown_causal_component_drift_fails_strict_validation(self) -> None:
        self._make_run()
        failure = json.loads((self.source / "failure.json").read_text())
        failure["causal_component"] = "unknown-component"
        failure["root_failure"]["causal_component"] = "unknown-component"
        (self.source / "failure.json").write_text(
            json.dumps(failure, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        (self.source / "failure-summary.txt").write_text(
            VALIDATE._failure_summary(failure), encoding="utf-8"
        )
        with self.assertRaisesRegex(PACKET.DiagnosticPacketError, "causal records conflict"):
            PACKET.generate_packet(self.run_id)

    def test_failure_replacement_between_snapshot_and_publication_is_rejected(self) -> None:
        self._make_run()
        original_verify = PACKET._verify_source_snapshot

        def replace_then_verify(run_descriptor, snapshots, validator):
            failure = json.loads((self.source / "failure.json").read_text())
            failure["message"] = "concurrent replacement"
            (self.source / "failure.json").write_text(
                json.dumps(failure, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
                encoding="utf-8",
            )
            return original_verify(run_descriptor, snapshots, validator)

        with mock.patch.object(
            PACKET, "_select_reproduction",
            return_value={"targetScenario": "runtime.boot", "effectivePhase": "preflight"},
        ), mock.patch.object(
            PACKET, "_verify_source_snapshot", side_effect=replace_then_verify
        ):
            with self.assertRaisesRegex(
                PACKET.DiagnosticPacketError, "changed while"
            ):
                PACKET.generate_packet(self.run_id)
        self.assertFalse((self.source / PACKET.PACKET_FILE_NAME).exists())

    def test_legacy_v2_failure_gets_stream_derived_tail(self) -> None:
        self._make_run(events=3)
        failure = json.loads((self.source / "failure.json").read_text())
        failure["format_version"] = VALIDATE.LEGACY_FAILURE_ENVELOPE_FORMAT_VERSION
        del failure["semantic_event_tail"]
        (self.source / "failure.json").write_text(
            json.dumps(failure, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        (self.source / "failure-summary.txt").write_text(
            VALIDATE._failure_summary(failure), encoding="utf-8"
        )
        self._packet()
        semantic = self._files()["semantic-tail.json"]
        expected = VALIDATE.read_semantic_events(
            self.source,
            expected_scenario="runtime.boot",
            expected_run_id=self.run_id,
        )
        self.assertEqual(semantic["includedCount"], len(expected))
        self.assertEqual(semantic["events"], expected)

    def test_concurrent_generation_is_exclusive_and_atomic(self) -> None:
        self._make_run()

        def publish() -> dict:
            return PACKET.generate_packet(self.run_id)

        # Patch once around both workers. Per-worker patches race while restoring the
        # same module attribute and can make one worker execute the real selector.
        with mock.patch.object(
            PACKET,
            "_select_reproduction",
            return_value={
                "targetScenario": "runtime.boot",
                "effectivePhase": "preflight",
            },
        ):
            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
                futures = [pool.submit(publish), pool.submit(publish)]
                results = [future.result() for future in futures]
        self.assertEqual(sorted(item["status"] for item in results), ["created", "existing"])
        self.assertEqual(len(list(self.source.glob(".diagnostic-packet.json.*.tmp"))), 0)
        self.assertTrue((self.source / PACKET.PACKET_FILE_NAME).is_file())

    def test_existing_different_packet_is_never_overwritten(self) -> None:
        self._make_run()
        target = self.source / PACKET.PACKET_FILE_NAME
        target.write_bytes(b"foreign")
        target.chmod(0o600)
        with mock.patch.object(
            PACKET, "_select_reproduction",
            return_value={"targetScenario": "runtime.boot", "effectivePhase": "preflight"},
        ):
            with self.assertRaisesRegex(
                PACKET.DiagnosticPacketError, "different content"
            ):
                PACKET.generate_packet(self.run_id)
        self.assertEqual(target.read_bytes(), b"foreign")

    def test_cli_accepts_only_one_run_id_and_help_has_no_runtime_side_effect(self) -> None:
        entrypoint = ROOT / "tools" / "hatifect-diagnostic-packet"
        for arguments, expected in (
            (["--help"], 0),
            ([], 2),
            (["--unknown"], 2),
            ([self.run_id, "extra"], 2),
        ):
            with self.subTest(arguments=arguments):
                completed = subprocess.run(
                    [str(entrypoint), *arguments],
                    cwd=ROOT,
                    capture_output=True,
                    text=True,
                    check=False,
                    timeout=5,
                )
                self.assertEqual(completed.returncode, expected)
                self.assertIn("Usage:", completed.stdout + completed.stderr)

    def test_generation_only_reads_git_identity_and_leaves_canonical_bytes_unchanged_on_failure(self) -> None:
        self._make_run()
        originals = {name: (self.source / name).read_bytes() for name in ("result.json", "failure.json")}
        real_run = PACKET.subprocess.run
        calls: list[list[str]] = []
        def observed(*args, **kwargs):
            calls.append(args[0])
            return real_run(*args, **kwargs)
        with mock.patch.object(PACKET.subprocess, "run", side_effect=observed):
            self._packet()
        self.assertEqual([call[:4] for call in calls], [["git", "-C", str(ROOT), "rev-parse"]])
        self.assertEqual(originals["result.json"], (self.source / "result.json").read_bytes())
        self.assertEqual(originals["failure.json"], (self.source / "failure.json").read_bytes())

    def test_bounded_separate_error_is_published_without_touching_sources(self) -> None:
        self._make_run()
        original = (self.source / "result.json").read_bytes()
        error = PACKET.DiagnosticPacketError("TEST_FAILURE", "bounded diagnostic failure")
        with mock.patch.object(PACKET, "_build_context_manifest", side_effect=error):
            with self.assertRaises(PACKET.DiagnosticPacketError) as raised:
                PACKET.generate_packet(self.run_id)
        self.assertTrue(raised.exception.reason_code)
        PACKET._publish_error(self.run_id, raised.exception)
        error = json.loads((self.source / "diagnostics/diagnostic-packet-error.json").read_text())
        self.assertLessEqual((self.source / "diagnostics/diagnostic-packet-error.json").stat().st_size,
                             PACKET.MAX_PACKET_ERROR_BYTES)
        self.assertFalse((self.source / PACKET.PACKET_FILE_NAME).exists())
        self.assertEqual(original, (self.source / "result.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
