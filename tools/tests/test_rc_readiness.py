import datetime as dt
import importlib.util
import json
import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
TOOLS = ROOT / "tools"
sys.path.insert(0, str(TOOLS))
SPEC = importlib.util.spec_from_file_location(
    "hatifect_rc_readiness_tests", TOOLS / "rc_readiness.py"
)
assert SPEC is not None and SPEC.loader is not None
RC = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RC)


class RcReadinessTests(unittest.TestCase):
    def test_recorded_build_evidence_binds_the_full_two_module_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package_root = Path(directory) / "candidate"
            runtime_root = package_root / "Hatifect UI"
            runtime_root.mkdir(parents=True)
            (runtime_root / "manifest.json").write_text(
                json.dumps({"Version": "1.0.0-alpha.30"}), encoding="utf-8"
            )
            output = Path(directory) / "build-evidence.json"
            with (
                mock.patch.object(RC.release_tool, "load_contract", return_value={}),
                mock.patch.object(RC.release_tool, "verify_source"),
                mock.patch.object(RC.release_tool, "verify_package"),
                mock.patch.object(
                    RC, "compute_runtime_fingerprint", return_value="runtime-value"
                ),
                mock.patch.object(
                    RC, "compute_candidate_fingerprint", return_value="candidate-value"
                ),
            ):
                result = RC.record_build(package_root, output, tests_passed=True)

            evidence = json.loads(output.read_text(encoding="utf-8"))

        self.assertEqual(result, 0)
        self.assertEqual(
            evidence["CandidateFingerprintAlgorithm"],
            RC.CANDIDATE_FINGERPRINT_ALGORITHM,
        )
        self.assertEqual(evidence["CandidateFingerprint"], "candidate-value")
        self.assertEqual(evidence["RuntimeFingerprint"], "runtime-value")

    def test_ready_verification_rejects_a_different_two_module_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package_root = Path(directory) / "candidate"
            runtime_root = package_root / "Hatifect UI"
            runtime_root.mkdir(parents=True)
            (runtime_root / "manifest.json").write_text(
                json.dumps({"Version": "1.0.0-alpha.30"}), encoding="utf-8"
            )
            evidence_path = Path(directory) / "build-evidence.json"
            evidence_path.write_text(
                json.dumps(
                    {
                        "FormatVersion": RC.EVIDENCE_FORMAT_VERSION,
                        "CapturedAtUtc": "2026-09-02T00:00:00Z",
                        "HatifectVersion": "1.0.0-alpha.30",
                        "RuntimeFingerprintAlgorithm": RC.ALGORITHM,
                        "RuntimeFingerprint": "runtime-value",
                        "CandidateFingerprintAlgorithm": RC.CANDIDATE_FINGERPRINT_ALGORITHM,
                        "CandidateFingerprint": "different-candidate",
                        "Configuration": "Release",
                        "SuiteBuildPassed": True,
                        "TestsPassed": True,
                        "TestProjects": RC.TEST_PROJECTS,
                    }
                ),
                encoding="utf-8",
            )
            report = Path(directory) / "unused-report.json"
            with (
                mock.patch.object(RC.release_tool, "load_contract", return_value={}),
                mock.patch.object(RC.release_tool, "verify_source"),
                mock.patch.object(RC.release_tool, "verify_package"),
                mock.patch.object(
                    RC, "compute_runtime_fingerprint", return_value="runtime-value"
                ),
                mock.patch.object(
                    RC, "compute_candidate_fingerprint", return_value="candidate-value"
                ),
                mock.patch.object(RC.subprocess, "run") as run,
            ):
                result = RC.verify_ready(
                    package_root,
                    evidence_path,
                    report,
                    report,
                    report,
                    report,
                    report,
                    report,
                )

        self.assertEqual(result, 1)
        run.assert_not_called()

    def test_build_evidence_timestamp_requires_utc(self) -> None:
        expected = dt.datetime(2026, 8, 27, 20, 0, tzinfo=dt.timezone.utc).timestamp()

        self.assertEqual(
            RC._parse_utc_timestamp("2026-08-27T20:00:00Z", "CapturedAtUtc"),
            expected,
        )
        for value in (None, "", "2026-08-27T20:00:00", "2026-08-27T23:00:00+03:00"):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    RC._parse_utc_timestamp(value, "CapturedAtUtc")

    def test_automated_report_older_than_build_evidence_is_rejected(self) -> None:
        report = {
            "FormatVersion": 3,
            "PerformanceFormatVersion": 3,
            "HatifectVersion": "1.0.0",
            "RuntimeFingerprintAlgorithm": RC.ALGORITHM,
            "RuntimeFingerprint": "runtime-fingerprint",
            "CapturedAtUtc": "2026-08-27T19:59:00Z",
        }
        build_captured_at = RC._parse_utc_timestamp(
            "2026-08-27T20:00:00Z", "CapturedAtUtc"
        )

        with self.assertRaisesRegex(
            RC.live_harness.HarnessError, "predates this harness session"
        ):
            RC._validate_automated_evidence(
                report,
                "all",
                "1.0.0",
                "runtime-fingerprint",
                build_captured_at,
            )

    def test_automated_promotion_matrix_is_exact_and_ordered(self) -> None:
        self.assertEqual(
            RC.AUTOMATED_PROMOTION_SCENARIOS,
            (
                "all",
                "semantic.chests-anywhere-overlay.absent",
                "semantic.chests-anywhere-overlay.incompatible",
                "semantic.chests-anywhere-overlay.capture-exception",
                "semantic.chests-anywhere-overlay.return-to-title",
            ),
        )

    def test_automated_evidence_resolves_the_requested_matrix_scenario(self) -> None:
        report = {
            "HatifectVersion": "1.0.0",
            "RuntimeFingerprintAlgorithm": RC.ALGORITHM,
            "RuntimeFingerprint": "runtime-fingerprint",
        }
        scenarios = {"sentinel": object()}
        resolved = {"id": "semantic.chests-anywhere-overlay.absent"}
        with (
            mock.patch.object(RC.live_harness, "load_manifest", return_value=scenarios),
            mock.patch.object(
                RC.live_harness, "resolve_scenario", return_value=resolved
            ) as resolve,
            mock.patch.object(RC.live_harness, "validate_report") as validate,
        ):
            RC._validate_automated_evidence(
                report,
                "semantic.chests-anywhere-overlay.absent",
                "1.0.0",
                "runtime-fingerprint",
                123.0,
            )

        resolve.assert_called_once_with(
            scenarios, "semantic.chests-anywhere-overlay.absent", "ui"
        )
        validate.assert_called_once_with(resolved, report, started_at=123.0)

    def test_return_to_title_evidence_requires_ca_owned_scenario(self) -> None:
        report = {
            "HatifectVersion": "1.0.0",
            "RuntimeFingerprintAlgorithm": RC.ALGORITHM,
            "RuntimeFingerprint": "runtime-fingerprint",
        }
        scenarios = {"sentinel": object()}
        scenario_id = "semantic.chests-anywhere-overlay.return-to-title"
        resolved = {"id": scenario_id}
        with (
            mock.patch.object(RC.live_harness, "load_manifest", return_value=scenarios),
            mock.patch.object(
                RC.live_harness, "resolve_scenario", return_value=resolved
            ) as resolve,
            mock.patch.object(RC.live_harness, "validate_report"),
        ):
            RC._validate_automated_evidence(
                report,
                scenario_id,
                "1.0.0",
                "runtime-fingerprint",
                123.0,
            )

        resolve.assert_called_once_with(scenarios, scenario_id, "ui")


if __name__ == "__main__":
    unittest.main()
