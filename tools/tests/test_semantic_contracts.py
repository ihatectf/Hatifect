import copy
import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
UI_ROOT = ROOT / "Hatifect UI"
UI_TOOLS = UI_ROOT / "tools"
sys.path.insert(0, str(UI_TOOLS))


def load_verifier(name: str):
    spec = importlib.util.spec_from_file_location(
        "semantic_contract_tests_" + name, UI_TOOLS / (name + ".py")
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


API = load_verifier("verify_public_api")
PERFORMANCE = load_verifier("verify_runtime_perf")
HOST = load_verifier("verify_host_acceptance")
FINGERPRINT = load_verifier("runtime_fingerprint")


class SemanticPublicApiTests(unittest.TestCase):
    def create_api_fixture(self, root: Path) -> None:
        for relative in (
            "PUBLIC_API_BASELINE.json",
            "Hatifect.UI.Experience/Hatifect.UI.Experience.csproj",
            "Hatifect.UI.Experience/Hosting/UiSemanticSurfaceContracts.cs",
            "Hatifect.UI.Stardew/Hosting/UiSemanticSurfaceService.cs",
        ):
            target = root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(UI_ROOT / relative, target)

    def test_hatifect_semantic_snapshot_passes_without_legacy_sources_or_writes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_api_fixture(root)
            baseline_path = root / "PUBLIC_API_BASELINE.json"
            before = baseline_path.read_bytes()
            baseline = json.loads(before)

            self.assertEqual({"FormatVersion", "SemanticSurface"}, set(baseline))
            self.assertEqual(1, baseline["SemanticSurface"]["ApiVersion"])
            self.assertEqual(
            "9de29d5a7c53964ae192efb4f4f7590e1dc75a64d2fe94605f86dac3861c6335",
                baseline["SemanticSurface"]["Files"][0]["Sha256"],
            )
            self.assertEqual([], API.verify(root))
            self.assertEqual(before, baseline_path.read_bytes())

    def test_source_tamper_requires_explicit_baseline_review(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_api_fixture(root)
            contract = root / "Hatifect.UI.Experience/Hosting/UiSemanticSurfaceContracts.cs"
            contract.write_text(
                contract.read_text(encoding="utf-8").replace(
                    "IUiSemanticSurfaceApi", "IUiChangedSurfaceApi"
                ),
                encoding="utf-8",
            )

            self.assertIn("semantic surface changed and requires explicit review", "\n".join(API.verify(root)))

    def test_snapshot_tamper_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_api_fixture(root)
            path = root / "PUBLIC_API_BASELINE.json"
            baseline = json.loads(path.read_text(encoding="utf-8"))
            baseline["SemanticSurface"]["Files"][0]["Sha256"] = "0" * 64
            path.write_text(json.dumps(baseline), encoding="utf-8")

            self.assertIn("semantic surface changed and requires explicit review", "\n".join(API.verify(root)))

    def test_implementation_version_change_cannot_hide_behind_commented_v1(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_api_fixture(root)
            implementation = root / "Hatifect.UI.Stardew/Hosting/UiSemanticSurfaceService.cs"
            implementation.write_text(
                implementation.read_text(encoding="utf-8").replace(
                    "public int ApiVersion => 1;", "public int ApiVersion => 2;"
                ) + "\n// public int ApiVersion => 1;\n",
                encoding="utf-8",
            )

            self.assertIn("implementation must provide API v1", "\n".join(API.verify(root)))

    def test_missing_or_invalid_baseline_reports_a_validation_error(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.create_api_fixture(root)
            path = root / "PUBLIC_API_BASELINE.json"
            baseline = json.loads(path.read_text(encoding="utf-8"))
            for value, message in (([], "must contain an object"), ({}, "FormatVersion must be 1")):
                with self.subTest(value=value):
                    path.write_text(json.dumps(value), encoding="utf-8")
                    self.assertIn(message, "\n".join(API.verify(root)))
            baseline["SemanticSurface"]["ApiVersion"] = True
            path.write_text(json.dumps(baseline), encoding="utf-8")
            self.assertIn("must declare API v1", "\n".join(API.verify(root)))
            path.unlink()
            self.assertIn("PUBLIC_API_BASELINE.json", "\n".join(API.verify(root)))


class SemanticReportTests(unittest.TestCase):
    def setUp(self) -> None:
        self.budgets = json.loads((UI_ROOT / "PERFORMANCE_BUDGETS.json").read_text(encoding="utf-8"))
        self.requirements = json.loads((UI_ROOT / "HOST_ACCEPTANCE_REQUIREMENTS.json").read_text(encoding="utf-8"))
        self.fingerprint = "a" * 64
        metrics = {"LogicalWidth": 1280, "LogicalHeight": 720, "PixelScaleX": 1.0, "PixelScaleY": 1.0}
        self.report = {
            "FormatVersion": 3,
            "PerformanceFormatVersion": 3,
            "HatifectVersion": self.requirements["HatifectVersion"],
            "RuntimeFingerprintAlgorithm": "sha256-runtime-v2",
            "RuntimeFingerprint": self.fingerprint,
            "CapturedAtUtc": "2026-09-05T12:00:00Z",
            "GameVersion": "1.6.15",
            "SmapiVersion": "4.3.2",
            "Scenarios": [{
                "Id": "semantic.performance",
                "Frames": 600,
                "P95UiThreadMs": 2.0,
                "P99UiThreadMs": 4.0,
                "SteadyStateAllocatedBytesPerFrame": 16384,
                "MeasureCacheMissRatio": 0.2,
                "ArrangeCacheMissRatio": 0.2,
                "SurfaceKinds": ["semantic-terminal-menu"],
                "ThemeIds": ["Hatifect.UI:theme/Dark"],
                "DisplayMetrics": [copy.deepcopy(metrics)],
            }],
            "HostChecks": [{
                "Id": required["Id"],
                "Passed": True,
                "CapturedAtUtc": "2026-09-05T12:00:00Z",
                "ThemeId": "Hatifect.UI:theme/Dark",
                "DisplayMetrics": copy.deepcopy(metrics),
            } for required in self.requirements["RequiredChecks"]],
        }

    def validate_host(self, report: dict) -> list[str]:
        return HOST.validate_host_report(report, self.budgets, self.requirements, self.fingerprint)

    def test_complete_semantic_report_at_exact_budgets_passes(self) -> None:
        self.assertEqual([], PERFORMANCE.validate_report(self.report, self.budgets))
        self.assertEqual([], self.validate_host(self.report))

    def test_every_budget_and_minimum_frame_count_is_enforced(self) -> None:
        for field, value, error in (
            ("Frames", 599, "Frames 599 < required 600"),
            ("P95UiThreadMs", 2.01, "P95UiThreadMs 2.01 > budget 2"),
            ("P99UiThreadMs", 4.01, "P99UiThreadMs 4.01 > budget 4"),
            ("SteadyStateAllocatedBytesPerFrame", 16385, "SteadyStateAllocatedBytesPerFrame 16385 > budget 16384"),
            ("MeasureCacheMissRatio", 0.21, "MeasureCacheMissRatio 0.21 > budget 0.2"),
            ("ArrangeCacheMissRatio", 0.21, "ArrangeCacheMissRatio 0.21 > budget 0.2"),
        ):
            with self.subTest(field=field):
                report = copy.deepcopy(self.report)
                report["Scenarios"][0][field] = value
                self.assertEqual(["semantic.performance: " + error], PERFORMANCE.validate_report(report, self.budgets))

    def test_non_numeric_nonfinite_and_negative_metrics_are_rejected(self) -> None:
        for field in (
            "P95UiThreadMs", "P99UiThreadMs", "SteadyStateAllocatedBytesPerFrame",
            "MeasureCacheMissRatio", "ArrangeCacheMissRatio",
        ):
            for value in (True, None, "0", -0.1, float("nan"), float("inf")):
                with self.subTest(field=field, value=value):
                    report = copy.deepcopy(self.report)
                    report["Scenarios"][0][field] = value
                    with self.assertRaisesRegex(ValueError, field + " must be a finite non-negative number"):
                        PERFORMANCE.validate_report(report, self.budgets)

    def test_frames_are_not_coerced_from_boolean_string_or_fraction(self) -> None:
        for value in (True, "600", 600.9, -1):
            with self.subTest(value=value):
                report = copy.deepcopy(self.report)
                report["Scenarios"][0]["Frames"] = value
                with self.assertRaisesRegex(ValueError, "Frames must be a non-negative integer"):
                    PERFORMANCE.validate_report(report, self.budgets)

    def test_missing_and_duplicate_scenarios_cannot_pass(self) -> None:
        report = copy.deepcopy(self.report)
        report["Scenarios"] = []
        with self.assertRaisesRegex(ValueError, "missing required scenarios: semantic.performance"):
            PERFORMANCE.validate_report(report, self.budgets)
        report["Scenarios"] = [copy.deepcopy(self.report["Scenarios"][0]), copy.deepcopy(self.report["Scenarios"][0])]
        report["Scenarios"][0]["P95UiThreadMs"] = 100
        with self.assertRaisesRegex(ValueError, "Scenarios: duplicate Id semantic.performance"):
            PERFORMANCE.validate_report(report, self.budgets)

    def test_report_and_scenario_shapes_fail_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "report must be an object"):
            PERFORMANCE.validate_report([], self.budgets)
        for value, message in ((None, "must be an array"), ([None], "entries must be objects"), ([{"Id": ""}], "non-empty Id")):
            with self.subTest(value=value):
                report = copy.deepcopy(self.report)
                report["Scenarios"] = value
                with self.assertRaisesRegex(ValueError, message):
                    PERFORMANCE.validate_report(report, self.budgets)

    def test_missing_or_failed_host_checks_are_rejected(self) -> None:
        report = copy.deepcopy(self.report)
        missing = report["HostChecks"].pop()
        self.assertEqual(["missing host check: " + missing["Id"]], self.validate_host(report))
        for value in (False, 1, "true", None):
            with self.subTest(value=value):
                report = copy.deepcopy(self.report)
                report["HostChecks"][0]["Passed"] = value
                self.assertEqual(
                    ["host check failed/not passed: " + report["HostChecks"][0]["Id"]],
                    self.validate_host(report),
                )

    def test_duplicate_host_check_cannot_replace_a_failure(self) -> None:
        report = copy.deepcopy(self.report)
        report["HostChecks"].append(copy.deepcopy(report["HostChecks"][0]))
        report["HostChecks"][0]["Passed"] = False
        with self.assertRaisesRegex(ValueError, "HostChecks: duplicate Id semantic.lifecycle.visual"):
            self.validate_host(report)

    def test_retired_surface_and_missing_theme_or_display_evidence_are_rejected(self) -> None:
        for field, value, message in (
            ("SurfaceKinds", ["menu"], "missing required SurfaceKinds ['semantic-terminal-menu']"),
            ("ThemeIds", [], "ThemeIds evidence count 0 < required 1"),
            ("DisplayMetrics", [], "distinct DisplayMetrics evidence count 0 < required 1"),
        ):
            with self.subTest(field=field):
                report = copy.deepcopy(self.report)
                report["Scenarios"][0][field] = value
                self.assertEqual(["semantic.performance: " + message], self.validate_host(report))

    def test_invalid_display_metrics_in_scenarios_and_checks_are_rejected(self) -> None:
        for location in ("scenario", "check"):
            for field, value, message in (
                ("LogicalWidth", True, "dimensions must be positive integers"),
                ("LogicalHeight", 0, "dimensions must be positive integers"),
                ("PixelScaleX", float("inf"), "PixelScaleX must be a finite non-negative number"),
                ("PixelScaleY", 0, "pixel scales must be positive"),
            ):
                with self.subTest(location=location, field=field):
                    report = copy.deepcopy(self.report)
                    metrics = report["Scenarios"][0]["DisplayMetrics"][0] if location == "scenario" else report["HostChecks"][0]["DisplayMetrics"]
                    metrics[field] = value
                    with self.assertRaisesRegex(ValueError, message):
                        self.validate_host(report)

    def test_report_must_match_the_exact_runtime_fingerprint(self) -> None:
        report = copy.deepcopy(self.report)
        report["RuntimeFingerprint"] = "b" * 64
        self.assertIn("RuntimeFingerprint does not match the exact Hatifect UI runtime", "\n".join(self.validate_host(report)))

    def test_cli_accepts_complete_fixture_and_rejects_malformed_reports_without_traceback(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runtime = root / "runtime"
            runtime.mkdir()
            for name in FINGERPRINT.REQUIRED_RUNTIME_DLLS:
                (runtime / name).write_bytes(b"fixture-assembly")
            (runtime / "manifest.json").write_text("{}", encoding="utf-8")
            report = copy.deepcopy(self.report)
            report["RuntimeFingerprint"] = FINGERPRINT.compute_runtime_fingerprint(runtime)
            report_path = root / "report.json"
            for name, extra in (
                ("verify_runtime_perf.py", []),
                ("verify_host_acceptance.py", ["--runtime-root", str(runtime)]),
            ):
                with self.subTest(verifier=name):
                    command = [sys.executable, str(UI_TOOLS / name), str(report_path), *extra]
                    report_path.write_text(json.dumps(report), encoding="utf-8")
                    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn("gate OK", result.stdout)
                    for invalid in ("{", "[]"):
                        report_path.write_text(invalid, encoding="utf-8")
                        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True)
                        self.assertEqual(1, result.returncode, result.stderr)
                        self.assertIn("gate FAILED", result.stderr)
                        self.assertNotIn("Traceback", result.stderr)


if __name__ == "__main__":
    unittest.main()
