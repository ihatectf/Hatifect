import contextlib
import importlib.util
import io
import json
import re
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
UI_TOOLS = ROOT / "Hatifect UI" / "tools"
sys.path.insert(0, str(UI_TOOLS))

FINGERPRINT_SPEC = importlib.util.spec_from_file_location(
    "hatifect_runtime_fingerprint_tests", UI_TOOLS / "runtime_fingerprint.py"
)
assert FINGERPRINT_SPEC is not None and FINGERPRINT_SPEC.loader is not None
FINGERPRINT = importlib.util.module_from_spec(FINGERPRINT_SPEC)
FINGERPRINT_SPEC.loader.exec_module(FINGERPRINT)

HOST_SPEC = importlib.util.spec_from_file_location(
    "hatifect_verify_host_acceptance_tests", UI_TOOLS / "verify_host_acceptance.py"
)
assert HOST_SPEC is not None and HOST_SPEC.loader is not None
HOST = importlib.util.module_from_spec(HOST_SPEC)
HOST_SPEC.loader.exec_module(HOST)


class RuntimeFingerprintTests(unittest.TestCase):
    def create_runtime(self, root: Path) -> None:
        root.mkdir(parents=True)
        for index, name in enumerate(FINGERPRINT.REQUIRED_RUNTIME_DLLS):
            (root / name).write_bytes(f"assembly-{index}-{name}".encode("utf-8"))
        (root / "manifest.json").write_text(
            '{"Name":"Hatifect UI","Version":"test"}\n', encoding="utf-8"
        )
        assets = root / "assets"
        assets.mkdir()
        (assets / "theme.json").write_text('{"theme":"dark"}\n', encoding="utf-8")

    def test_runtime_dll_inventory_matches_python_csharp_and_ui_package_contract(self) -> None:
        package_contract = json.loads(
            (ROOT / "Hatifect.UI.Packages.json").read_text(encoding="utf-8")
        )
        package_dlls = {
            f"{package['Id']}.dll" for package in package_contract["Packages"]
        }
        csharp = (
            ROOT
            / "Hatifect UI/Hatifect.UI.Stardew/Diagnostics/UiRuntimeFingerprint.cs"
        ).read_text(encoding="utf-8")
        inventory_match = re.search(
            r"RequiredRuntimeDlls\s*=\s*\{(?P<body>.*?)\};", csharp, re.DOTALL
        )
        self.assertIsNotNone(inventory_match)
        csharp_dlls = tuple(
            re.findall(r'"(Hatifect\.UI\.[^"]+\.dll)"', inventory_match.group("body"))
        )

        self.assertEqual(8, len(package_dlls))
        self.assertEqual(package_dlls, set(FINGERPRINT.REQUIRED_RUNTIME_DLLS))
        self.assertEqual(FINGERPRINT.REQUIRED_RUNTIME_DLLS, csharp_dlls)
        self.assertEqual(
            (*FINGERPRINT.REQUIRED_RUNTIME_DLLS, "manifest.json"),
            FINGERPRINT.REQUIRED_ROOT_FILES,
        )

    def test_changing_each_required_runtime_dll_changes_the_digest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary) / "Hatifect UI"
            self.create_runtime(runtime)
            baseline = FINGERPRINT.compute_runtime_fingerprint(runtime)

            for name in FINGERPRINT.REQUIRED_RUNTIME_DLLS:
                path = runtime / name
                original = path.read_bytes()
                with self.subTest(name=name):
                    path.write_bytes(original + b"\x00changed")
                    self.assertNotEqual(
                        baseline, FINGERPRINT.compute_runtime_fingerprint(runtime)
                    )
                    path.write_bytes(original)

    def test_removing_each_required_runtime_dll_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary) / "Hatifect UI"
            self.create_runtime(runtime)

            for name in FINGERPRINT.REQUIRED_RUNTIME_DLLS:
                path = runtime / name
                original = path.read_bytes()
                with self.subTest(name=name):
                    path.unlink()
                    with self.assertRaisesRegex(ValueError, re.escape(name)):
                        FINGERPRINT.compute_runtime_fingerprint(runtime)
                    path.write_bytes(original)

    def test_manifest_is_required_and_optional_recursive_assets_are_bound_to_digest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            runtime = Path(temporary) / "Hatifect UI"
            self.create_runtime(runtime)
            nested_asset = runtime / "assets/nested/icon.txt"
            nested_asset.parent.mkdir()
            nested_asset.write_text("icon-a", encoding="utf-8")
            baseline = FINGERPRINT.compute_runtime_fingerprint(runtime)

            manifest = runtime / "manifest.json"
            manifest_contents = manifest.read_text(encoding="utf-8")
            manifest.unlink()
            with self.assertRaisesRegex(ValueError, "manifest.json"):
                FINGERPRINT.compute_runtime_fingerprint(runtime)
            manifest.write_text(manifest_contents, encoding="utf-8")

            assets = runtime / "assets"
            hidden_assets = runtime / "assets-missing"
            assets.rename(hidden_assets)
            without_assets = FINGERPRINT.compute_runtime_fingerprint(runtime)
            self.assertNotEqual(baseline, without_assets)
            hidden_assets.rename(assets)

            manifest.write_text('{"changed":true}\n', encoding="utf-8")
            self.assertNotEqual(baseline, FINGERPRINT.compute_runtime_fingerprint(runtime))
            manifest.write_text(manifest_contents, encoding="utf-8")
            nested_asset.write_text("icon-b", encoding="utf-8")
            self.assertNotEqual(baseline, FINGERPRINT.compute_runtime_fingerprint(runtime))

    def test_v1_host_evidence_is_rejected_by_the_v2_ui_gate(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runtime = root / "Hatifect UI"
            self.create_runtime(runtime)
            requirements = json.loads(
                (ROOT / "Hatifect UI/HOST_ACCEPTANCE_REQUIREMENTS.json").read_text(
                    encoding="utf-8"
                )
            )
            report = root / "report.json"
            report.write_text(
                json.dumps(
                    {
                        "FormatVersion": requirements["FormatVersion"],
                        "PerformanceFormatVersion": requirements[
                            "PerformanceFormatVersion"
                        ],
                        "HatifectVersion": requirements["HatifectVersion"],
                        "RuntimeFingerprintAlgorithm": "sha256-runtime-v1",
                        "RuntimeFingerprint": FINGERPRINT.compute_runtime_fingerprint(
                            runtime
                        ),
                    }
                ),
                encoding="utf-8",
            )
            stderr = io.StringIO()
            with (
                mock.patch.object(
                    HOST,
                    "parse_args",
                    return_value=types.SimpleNamespace(report=report, runtime_root=runtime),
                ),
                mock.patch.object(HOST, "validate_report", return_value=[]),
                contextlib.redirect_stderr(stderr),
            ):
                result = HOST.main()

            self.assertEqual(1, result)
            self.assertIn(
                "RuntimeFingerprintAlgorithm 'sha256-runtime-v1' != required 'sha256-runtime-v2'",
                stderr.getvalue(),
            )


if __name__ == "__main__":
    unittest.main()
