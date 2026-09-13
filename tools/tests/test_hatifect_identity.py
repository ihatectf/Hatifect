from __future__ import annotations

import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]


class HatifectIdentityTests(unittest.TestCase):
    def test_current_project_roots_exist(self) -> None:
        expected = (
            "Hatifect UI",
            "Hatifect Flow",
            "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay",
        )

        for relative in expected:
            with self.subTest(relative=relative):
                self.assertTrue((ROOT / relative).is_dir())

    def test_first_party_manifests_use_hatifect_identity(self) -> None:
        manifests = tuple(
            path
            for path in ROOT.rglob("manifest.json")
            if "artifacts" not in path.relative_to(ROOT).parts
        )

        self.assertGreaterEqual(len(manifests), 2)
        for path in manifests:
            with self.subTest(path=path.relative_to(ROOT).as_posix()):
                manifest = json.loads(path.read_text(encoding="utf-8"))
                self.assertTrue(manifest["UniqueID"].startswith("Hatifect."))

    def test_chests_anywhere_state_uses_one_current_key(self) -> None:
        source = (
            ROOT
            / "Integrations"
            / "Chests Anywhere"
            / "Hatifect Chests Anywhere Overlay"
            / "ChestsAnywhereOverlayController.cs"
        ).read_text(encoding="utf-8")

        key = '"Hatifect.ChestsAnywhereOverlay/State"'
        self.assertEqual(1, source.count(key))
        self.assertIn("TryGetValue(PlayerStateKey, out raw)", source)
        self.assertEqual(1, source.count("modData[PlayerStateKey]"))


if __name__ == "__main__":
    unittest.main()
