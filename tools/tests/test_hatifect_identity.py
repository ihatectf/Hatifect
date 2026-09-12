from __future__ import annotations

from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
OLD_TITLE = "Hatifect"
OLD_LOWER = "hatifect"
OLD_UPPER = "HATIFECT"
OLD_TOKENS = (OLD_TITLE, OLD_LOWER, OLD_UPPER)

HISTORICAL_EVIDENCE = {
    "docs/F12_ACCEPTANCE.md",
    "docs/F12_FLOW_ISOLATION_ADMISSION.md",
    "docs/F13_ACCEPTANCE.md",
    "docs/FLOWLINE_M3_ACCEPTANCE.md",
    "docs/FLOWLINE_NATIVE_INPUT_ACCEPTANCE.md",
    "docs/FLOW_NATIVE_EVIDENCE_POLLING.md",
    "docs/FLOW_PROVIDER_CONSISTENCY.md",
    "docs/FLOW_RESOURCE_PROFILE.md",
    "docs/Q01_ALPHA42_INTEGRATION.md",
    "docs/Q01_ALPHA43_INTEGRATION.md",
    "docs/Q01_ALPHA44_INTEGRATION.md",
    "docs/Q01_ALPHA47_COMBINED_ACCEPTANCE.md",
    "docs/Q01_BACKGROUND_RUNTIME.md",
    "docs/Q01_CAPTURE_FIX.md",
    "docs/Q01_NATIVE_INPUT.md",
    "docs/Q01_OVERLAY_FIXTURE.md",
    "docs/Q01_PACKAGE_IDENTITY.md",
    "docs/Q01_RENDERED_MATRIX.md",
    "docs/Q01_RUNTIME_BASELINE.md",
    "docs/Q01_WINDOW_VIEWPORT.md",
    "docs/Q02_ALPHA_INSTALL.md",
    "docs/Q02_INPUT_027273A.md",
    "docs/Q02_INTEGRATION_5D017AE.md",
    "docs/Q02_RUNTIME_57A9BB6.md",
    "docs/Q02_RUNTIME_57C9EF4.md",
    "docs/Q02_RUNTIME_C5174A8.md",
    "docs/Q02_RUNTIME_E9CFFFC.md",
    "docs/R01_RESOURCE_OWNERSHIP.md",
    "docs/ROADMAP-STATUS.md",
    "docs/ROADMAP_BASELINE.md",
    "docs/T01_ACCEPTANCE.md",
    "docs/U03_ACTION_MESSAGES.md",
    "docs/U03_SAVE_SWITCH_ACCEPTANCE.md",
    "docs/U03_U04_ALPHA46_INTEGRATION.md",
    "docs/U03_U04_F12_ALPHA47_INTEGRATION.md",
    "docs/U03_U04_HOST_ACCEPTANCE.md",
    "docs/U04_ACCEPTANCE.md",
    "docs/U04_ALPHA45_INTEGRATION.md",
    "docs/U04_NATIVE_TEXT_FALLBACK.md",
    "docs/U04_SEMANTIC_HEADINGS.md",
    "docs/U04_SURFACE_OBSERVATION.md",
    "docs/U05_INCREMENTAL_RUNTIME.md",
    "docs/U06_COLLECTION_ITEM_HELP.md",
    "docs/U06_COLLECTION_ROW_PROMPTS.md",
    "docs/U06_CONSUMER_STATUS_POLICY.md",
    "docs/U06_CONTRIBUTION_HELP.md",
    "docs/U06_FORM_FIELD_HELP.md",
    "docs/U06_INPUT_PROMPTS.md",
    "docs/U06_STATUS_COMPONENT.md",
    "docs/U06_TOOLTIPS.md",
    "docs/U06_TOOLTIP_DELAY.md",
    "docs/U06_TYPED_DENSITY.md",
    "docs/U06_VALIDATION_HELP.md",
    "docs/UI_FRACTIONAL_LAYOUT.md",
    "docs/UI_HOSTED_OBSERVATION.md",
    "docs/UI_ROOT_OVERFLOW.md",
    "docs/UI_SEMANTIC_REVEAL.md",
    "docs/UI_SEMANTIC_SDK.md",
    "docs/UI_WINDOW_INPUT_OBSERVATION.md",
}

SKIP_PARTS = {
    ".git",
    ".testagent",
    ".smapi-test",
    ".nuget",
    "artifacts",
    "bin",
    "obj",
    "packages",
    "TestResults",
    "__pycache__",
}


def project_files() -> tuple[Path, ...]:
    return tuple(
        path
        for path in ROOT.rglob("*")
        if path.is_file() and not SKIP_PARTS.intersection(path.relative_to(ROOT).parts)
    )


def strip_allowed_legacy(relative: str, content: str) -> str:
    if relative in HISTORICAL_EVIDENCE or relative == "docs/HATIFECT_MIGRATION.md":
        return ""

    replacements = (
        "https://github.com/ihatectf/" + OLD_TITLE,
        "${HOME}/Developer/Worktrees/Codex/345f/" + OLD_TITLE,
        "${HOME}/Developer/" + OLD_TITLE,
        OLD_TITLE + "-clean-baseline.zip",
        OLD_TITLE + " → Hatifect",
    )
    for literal in replacements:
        content = content.replace(literal, "")

    if relative.endswith("/ChestsAnywhereOverlayController.cs"):
        content = content.replace(OLD_TITLE + ".ChestsAnywhereOverlay/State", "")
        content = content.replace(OLD_TITLE + ".Storage.ChestsAnywhere/State", "")

    flow_legacy_literals = (
        OLD_TITLE + ".Flow/Station",
        OLD_TITLE + ".Flow/Cargo",
        "smapi/mod-data/" + OLD_LOWER + ".flow/flowline-v1",
        OLD_TITLE + ".Flow",
    )
    flow_legacy_files = {
        "Hatifect Flow/README.md",
        "Hatifect Flow/RENAMING.md",
        "Hatifect Flow/Sessions/FlowGameSession.cs",
        "Hatifect Flow/Sessions/FlowSaveDataMigration.cs",
        "Hatifect Flow/Inventory/ChestInventoryAccess.cs",
        "Hatifect Flow/tests/Hatifect.Flow.Stardew.Tests/FlowBrandMigrationTests.cs",
    }
    if relative in flow_legacy_files:
        for literal in flow_legacy_literals:
            content = content.replace(literal, "")

    if relative == "Hatifect Flow/RENAMING.md":
        for literal in (
            OLD_TITLE,
            OLD_LOWER,
            OLD_UPPER,
        ):
            content = content.replace(literal, "")

    return content


class HatifectIdentityTests(unittest.TestCase):
    def test_current_paths_and_content_use_hatifect_identity(self) -> None:
        path_violations: list[str] = []
        content_violations: list[str] = []

        for path in project_files():
            relative = path.relative_to(ROOT).as_posix()
            if any(token in relative for token in OLD_TOKENS):
                path_violations.append(relative)
            try:
                content = path.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                continue
            remaining = strip_allowed_legacy(relative, content)
            if any(token in remaining for token in OLD_TOKENS):
                content_violations.append(relative)

        self.assertEqual([], path_violations, "old product identity remains in current paths")
        self.assertEqual([], content_violations, "unclassified old product identity remains in current content")

    def test_ca_player_state_reads_both_pre_rename_keys_before_writing_hatifect(self) -> None:
        source = (
            ROOT
            / "Integrations"
            / "Chests Anywhere"
            / "Hatifect Chests Anywhere Overlay"
            / "ChestsAnywhereOverlayController.cs"
        ).read_text(encoding="utf-8")

        current = '"Hatifect.ChestsAnywhereOverlay/State"'
        previous = '"' + OLD_TITLE + '.ChestsAnywhereOverlay/State"'
        oldest = '"' + OLD_TITLE + '.Storage.ChestsAnywhere/State"'
        self.assertLess(source.index(current), source.index(previous))
        self.assertLess(source.index(previous), source.index(oldest))
        self.assertIn("TryGetValue(PreviousPlayerStateKey, out raw)", source)
        self.assertIn("TryGetValue(LegacyPlayerStateKey, out raw)", source)
        self.assertEqual(1, source.count("modData[PlayerStateKey]"))
        self.assertNotIn("modData[PreviousPlayerStateKey]", source)
        self.assertNotIn("modData[LegacyPlayerStateKey]", source)


if __name__ == "__main__":
    unittest.main()
