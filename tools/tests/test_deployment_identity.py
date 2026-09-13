import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "tools" / "live-harness" / "deployment_identity.py"
SPEC = importlib.util.spec_from_file_location("hatifect_deployment_identity", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
IDENTITY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IDENTITY)


class DeploymentIdentityTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="hatifect-deployment-identity.")
        self.root = Path(self.temporary.name)
        self.mods = self.root / "Mods"
        self.hatifect = self.mods / "Hatifect"
        self.ui = self.hatifect / "Hatifect UI"
        self.flow = self.hatifect / "Hatifect Flow"
        self.overlay = self.hatifect / "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay"
        self.ui.mkdir(parents=True)
        self.flow.mkdir(parents=True)
        self.overlay.mkdir(parents=True)
        (self.ui / "manifest.json").write_text('{"Version":"1.0.0-alpha.47"}\n', encoding="utf-8")
        (self.ui / "Hatifect.UI.Stardew.dll").write_bytes(b"ui-runtime")
        (self.flow / "manifest.json").write_text('{"Version":"3.0.0-rc.89"}\n', encoding="utf-8")
        (self.flow / "Hatifect.Flow.dll").write_bytes(b"flow-runtime")
        (self.overlay / "manifest.json").write_text('{"Version":"1.0.0-alpha.1"}\n', encoding="utf-8")
        (self.overlay / "config.json").write_text('{"Enabled":true}\n', encoding="utf-8")
        self.contract = self.root / "Hatifect.Release.json"
        self.contract.write_text(json.dumps({
            "FormatVersion": 1,
            "PackageRoot": "Hatifect",
            "Modules": [
                {
                    "Path": "Hatifect UI",
                    "AllowedRootFiles": ["manifest.json", "Hatifect.UI.Stardew.dll"],
                    "AllowedRuntimeFiles": [],
                },
                {
                    "Path": "Hatifect Flow",
                    "AllowedRootFiles": ["manifest.json", "Hatifect.Flow.dll"],
                    "AllowedRuntimeFiles": [],
                },
                {
                    "Path": "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay",
                    "AllowedRootFiles": ["manifest.json", "config.json"],
                    "AllowedRuntimeFiles": [],
                },
            ],
        }), encoding="utf-8")
        self.marker = self.root / "deployment.identity.json"
        self.head = "a" * 40

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write(self):
        return IDENTITY.write_identity(self.marker, self.head, self.mods, self.contract)

    def validate(self, head=None):
        return IDENTITY.validate_identity(self.marker, head or self.head, self.mods, self.contract)

    def test_writer_binds_exact_checkout_and_release_files_atomically(self) -> None:
        written = self.write()
        validated = self.validate()

        self.assertEqual(validated, written)
        self.assertEqual(validated["repositoryHead"], self.head)
        self.assertEqual(validated["fileCount"], 6)
        self.assertEqual(list(self.root.glob(".deployment.identity.json.*.tmp")), [])

    def test_different_checkout_is_rejected_before_runtime(self) -> None:
        self.write()

        with self.assertRaisesRegex(
            IDENTITY.DeploymentIdentityError,
            "Deployment belongs to checkout",
        ):
            self.validate("b" * 40)

    def test_changed_release_file_is_rejected(self) -> None:
        self.write()
        (self.ui / "Hatifect.UI.Stardew.dll").write_bytes(b"stale-or-mutated-runtime")

        with self.assertRaisesRegex(
            IDENTITY.DeploymentIdentityError,
            "no longer match the prepared deployment fingerprint",
        ):
            self.validate()

    def test_runtime_acceptance_evidence_does_not_change_deployment_identity(self) -> None:
        self.write()
        evidence = self.ui / ".acceptance"
        evidence.mkdir()
        (evidence / "host-acceptance-report.json").write_text("{}\n", encoding="utf-8")

        self.validate()
        (evidence / "host-acceptance-report.json").write_text('{"changed":true}\n', encoding="utf-8")
        self.validate()

    def test_runtime_generated_module_config_does_not_change_identity(self) -> None:
        self.write()
        (self.flow / "config.json").write_text('{"OpenNetwork":"K"}\n', encoding="utf-8")

        self.validate()
        (self.flow / "config.json").write_text('{"OpenNetwork":"F8"}\n', encoding="utf-8")
        self.validate()

    def test_packaged_config_remains_release_owned_and_fingerprinted(self) -> None:
        self.write()
        (self.overlay / "config.json").write_text('{"Enabled":false}\n', encoding="utf-8")

        with self.assertRaisesRegex(
            IDENTITY.DeploymentIdentityError,
            "no longer match the prepared deployment fingerprint",
        ):
            self.validate()

    def test_unexpected_non_runtime_file_is_rejected(self) -> None:
        self.write()
        (self.flow / "Injected.dll").write_bytes(b"unexpected")

        with self.assertRaisesRegex(
            IDENTITY.DeploymentIdentityError,
            "unexpected non-runtime file",
        ):
            self.validate()


if __name__ == "__main__":
    unittest.main()
