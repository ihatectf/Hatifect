import concurrent.futures
import datetime as dt
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
MODULE_PATH = ROOT / "tools" / "live-harness" / "reproduction.py"
SPEC = importlib.util.spec_from_file_location("hatifect_reproduction_tests", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
REPRO = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPRO)


def _timestamp() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


class ReproductionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.runtime_root = Path(tempfile.mkdtemp(prefix="hatifect-repro-"))
        self.addCleanup(shutil.rmtree, self.runtime_root, True)
        REPRO.RUNTIME_ROOT = self.runtime_root
        self.source_id = str(uuid.uuid4())
        self.source = self.runtime_root / self.source_id
        self.source.mkdir(mode=0o700)
        self.addCleanup(shutil.rmtree, self.source, True)
        self.head = subprocess.run(
            ["git", "-C", str(ROOT), "rev-parse", "--verify", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()

    def _request(self, scenario: str = "runtime.boot", kind: str = "smoke", *, run_id: str | None = None) -> dict:
        run_id = run_id or self.source_id
        root_stat = ROOT.stat()
        created = dt.datetime.now(dt.timezone.utc)
        now = created.isoformat().replace("+00:00", "Z")
        expires = (created + dt.timedelta(seconds=30)).isoformat().replace("+00:00", "Z")
        request = {
            "protocolVersion": 2,
            "requestType": "runScenario",
            "requestId": run_id,
            "repositoryRoot": str(ROOT),
            "repositoryDevice": root_stat.st_dev,
            "repositoryInode": root_stat.st_ino,
            "repositoryHead": self.head,
            "environmentId": "isolated-smapi-v1",
            "kind": kind,
            "scenarioId": scenario,
            "isolatedRoot": str(ROOT / ".smapi-test"),
            "artifactDirectory": str(self.source),
            "resultPath": str(self.source / "result.json"),
            "savePath": None,
            "timeoutSeconds": 600,
            "seed": 7,
            "createdAtUtc": now,
            "expiresAtUtc": expires,
        }
        return request

    def _source(self, scenario: str = "runtime.boot", kind: str = "smoke", *, root_id: str = "CHECK-FAIL", phase: str = "runtime") -> None:
        request = self._request(scenario, kind)
        result = {
            "protocolVersion": 1,
            "runId": self.source_id,
            "scenario": scenario,
            "status": "FAIL",
            "durationMs": 12,
            "assertions": [{
                "id": root_id,
                "status": "FAIL",
                "subject": "bounded reproduction test",
                "expected": "success",
                "actual": "failure",
            }],
            "exceptions": [],
            "artifacts": [],
        }
        (self.source / "request.json").write_text(json.dumps(request), encoding="utf-8")
        (self.source / "result.json").write_text(json.dumps(result), encoding="utf-8")
        REPRO.VALIDATOR.write_failure_artifacts(
            self.source / "result.json",
            result,
            timestamp=_timestamp(),
            phase=phase,
        )

    def _target(self) -> Path:
        target = self.runtime_root / str(uuid.uuid4())
        target.mkdir(mode=0o700)
        self.addCleanup(shutil.rmtree, target, True)
        return target

    def test_select_accepts_runtime_root_and_materialize_is_exact_and_bounded(self) -> None:
        self._source()
        selected = REPRO.select_source(self.source_id)
        self.assertEqual(selected["targetScenario"], "runtime.boot")
        self.assertEqual(selected["sourceRootPhase"], "runtime")
        target = self._target()

        metadata = REPRO.materialize(self.source_id, target, "smoke", "runtime.boot")
        self.assertEqual(set(metadata), REPRO.REPRODUCTION_FIELDS)
        document = json.loads((target / "reproduction.json").read_text(encoding="utf-8"))
        self.assertEqual(document, metadata)
        self.assertEqual(document["sourceRunId"], self.source_id)
        self.assertEqual(document["targetRunId"], target.name)
        self.assertEqual(document["sourceRootPhase"], "runtime")
        self.assertEqual(document["effectivePhase"], "preflight")
        self.assertNotIn(str(ROOT), json.dumps(document))
        self.assertLess((target / "reproduction.json").stat().st_size, REPRO.MAX_REPRODUCTION_BYTES)

    def test_unknown_or_later_requested_phase_fails_before_materialization(self) -> None:
        self._source()
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id, "runtime")
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id, "future")

    def test_pass_and_blocked_sources_are_not_reproduction_inputs(self) -> None:
        for status in ("PASS", "BLOCKED"):
            with self.subTest(status=status):
                source = self.source
                request = self._request()
                result = {
                    "protocolVersion": 1,
                    "runId": self.source_id,
                    "scenario": "runtime.boot",
                    "status": status,
                    "durationMs": 0,
                    "assertions": [],
                    "exceptions": [],
                    "artifacts": [],
                }
                (source / "request.json").write_text(json.dumps(request), encoding="utf-8")
                (source / "result.json").write_text(json.dumps(result), encoding="utf-8")
                if status == "FAIL":
                    REPRO.VALIDATOR.write_failure_artifacts(source / "result.json", result)
                with self.assertRaises(REPRO.ReproductionError):
                    REPRO.select_source(self.source_id)
                for path in source.iterdir():
                    path.unlink()

    def test_source_identity_and_failure_fingerprint_are_strict(self) -> None:
        self._source()
        request = json.loads((self.source / "request.json").read_text(encoding="utf-8"))
        request["repositoryHead"] = "0" * 40
        (self.source / "request.json").write_text(json.dumps(request), encoding="utf-8")
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id)

        self._source()
        request = json.loads((self.source / "request.json").read_text(encoding="utf-8"))
        request["timeoutSeconds"] = 86_401
        (self.source / "request.json").write_text(json.dumps(request), encoding="utf-8")
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id)

        self._source()
        request = json.loads((self.source / "request.json").read_text(encoding="utf-8"))
        request["expiresAtUtc"] = request["createdAtUtc"]
        (self.source / "request.json").write_text(json.dumps(request), encoding="utf-8")
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id)

        self._source()
        result = json.loads((self.source / "result.json").read_text(encoding="utf-8"))
        result["durationMs"] = 99
        (self.source / "result.json").write_text(json.dumps(result), encoding="utf-8")
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id)

    def test_aggregate_all_requires_unique_named_root_owner(self) -> None:
        scenarios = REPRO._load_scenarios()
        resolved = REPRO.VALIDATOR.resolve_scenario(scenarios, "all", "ui")
        root_id = resolved["checks"][0]
        self._source("all", "ui", root_id=root_id, phase="runtime")
        selected = REPRO.select_source(self.source_id)
        self.assertEqual(selected["targetScenario"], REPRO._check_owners(scenarios, "all", "ui")[root_id])

        shutil.rmtree(self.source)
        self.source.mkdir(mode=0o700)
        self._source("all", "ui", root_id="HARNESS-UNOWNED", phase="runtime")
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id)

    def test_symlink_source_files_are_rejected(self) -> None:
        self._source()
        result = self.source / "result.json"
        replacement = self.source / "result-copy.json"
        replacement.write_bytes(result.read_bytes())
        result.unlink()
        result.symlink_to(replacement)
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id)

        result.unlink()
        replacement.replace(result)
        failure = self.source / "failure.json"
        failure_copy = self.source / "failure-copy.json"
        failure_copy.write_bytes(failure.read_bytes())
        failure.unlink()
        failure.symlink_to(failure_copy)
        with self.assertRaises(REPRO.ReproductionError):
            REPRO.select_source(self.source_id)

    def test_existing_target_metadata_is_never_overwritten(self) -> None:
        self._source()
        target = self._target()
        metadata = target / "reproduction.json"
        metadata.write_text("foreign\n", encoding="utf-8")

        with self.assertRaises(REPRO.ReproductionError):
            REPRO.materialize(self.source_id, target, "smoke", "runtime.boot")

        self.assertEqual(metadata.read_text(encoding="utf-8"), "foreign\n")

    def test_source_snapshot_survives_path_replacement_without_following_it(self) -> None:
        self._source()
        moved = self.runtime_root / "moved-source"
        original_read = REPRO._read_bytes_at
        replaced = False

        def replace_after_request(*args, **kwargs):
            nonlocal replaced
            payload = original_read(*args, **kwargs)
            if args[1] == "request.json" and not replaced:
                replaced = True
                self.source.rename(moved)
                self.source.mkdir(mode=0o700)
                (self.source / "request.json").write_text("{}", encoding="utf-8")
            return payload

        with mock.patch.object(REPRO, "_read_bytes_at", side_effect=replace_after_request):
            selected = REPRO.select_source(self.source_id)

        self.assertTrue(replaced)
        self.assertEqual(selected["sourceResultFingerprint"], REPRO._result_fingerprint(
            json.loads((moved / "result.json").read_text(encoding="utf-8"))
        ))

    def test_concurrent_materialization_publishes_exactly_once(self) -> None:
        self._source()
        target = self._target()

        def publish():
            return REPRO.materialize(
                self.source_id, target, "smoke", "runtime.boot"
            )

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            futures = [executor.submit(publish) for _ in range(2)]
        successes = [future.result() for future in futures if future.exception() is None]
        failures = [future.exception() for future in futures if future.exception() is not None]

        self.assertEqual(len(successes), 1)
        self.assertEqual(len(failures), 1)
        self.assertIsInstance(failures[0], REPRO.ReproductionError)
        self.assertEqual(
            json.loads((target / "reproduction.json").read_text(encoding="utf-8")),
            successes[0],
        )


if __name__ == "__main__":
    unittest.main()
