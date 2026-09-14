from __future__ import annotations

import json
import os
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
        release = json.loads((ROOT / "Hatifect.Release.json").read_text(encoding="utf-8"))
        declared = tuple(ROOT / module["Manifest"] for module in release["Modules"])
        manifests = set(declared)
        discovered: set[Path] = set()
        generated_directories = {".git", ".smapi-test", "artifacts", "bin", "obj"}
        for directory, names, files in os.walk(ROOT):
            names[:] = [
                name for name in names
                if name not in generated_directories and not name.startswith(".")
            ]
            if "manifest.json" in files:
                discovered.add(Path(directory) / "manifest.json")

        self.assertGreaterEqual(len(manifests), 2)
        self.assertEqual(len(declared), len(manifests))
        self.assertEqual(manifests, discovered)
        for path in manifests:
            with self.subTest(path=path.relative_to(ROOT).as_posix()):
                self.assertTrue(path.is_file())
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
