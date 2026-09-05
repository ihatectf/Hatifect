import json
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
UI_ROOT = ROOT / "Hatifect UI"
CA_ROOT = (
    ROOT
    / "Integrations"
    / "Chests Anywhere"
    / "Hatifect Chests Anywhere Overlay"
)
PRODUCT_PROJECT = CA_ROOT / "Hatifect.ChestsAnywhereOverlay.csproj"
SEMANTIC_PROJECT = (
    CA_ROOT
    / "Hatifect.ChestsAnywhereOverlay.UI.Semantic"
    / "Hatifect.ChestsAnywhereOverlay.UI.Semantic.csproj"
)
SEMANTIC_SURFACE_CONTRACT = (
    UI_ROOT
    / "Hatifect.UI.Experience"
    / "Hosting"
    / "UiSemanticSurfaceContracts.cs"
)
SEMANTIC_ACCEPTANCE = (
    CA_ROOT
    / "UI"
    / "Semantic"
    / "SemanticChestsAnywhereOverlayAcceptanceScenarios.cs"
)
CA_ADAPTER = CA_ROOT / "Integration" / "ChestsAnywhereAdapter.cs"
CA_MOD_ENTRY = CA_ROOT / "ModEntry.cs"
EXACT_CENTRAL_UI_VERSION = "[$(HatifectUiPackageVersion)]"


def _local_name(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def _project_root(path: Path) -> ET.Element:
    return ET.parse(path).getroot()


def _items(project: ET.Element, name: str) -> list[ET.Element]:
    return [element for element in project.iter() if _local_name(element) == name]


def _metadata(item: ET.Element, name: str) -> str:
    attribute = item.get(name)
    if attribute is not None:
        return attribute.strip()
    child = next(
        (element for element in item if _local_name(element) == name),
        None,
    )
    return "" if child is None or child.text is None else child.text.strip()


def _include(item: ET.Element) -> str:
    return (item.get("Include") or "").replace("\\", "/")


def _production_sources(root: Path) -> tuple[Path, ...]:
    return tuple(
        sorted(
            path
            for path in root.rglob("*.cs")
            if not {"bin", "obj", "tests"}.intersection(
                path.relative_to(root).parts
            )
        )
    )


class ChestsAnywhereUiBoundaryTests(unittest.TestCase):
    def test_ui_projects_do_not_grant_internals_to_consumers_or_examples(self) -> None:
        forbidden_targets = ("hatifect.chestsanywhereoverlay", "hatifect.ui.examples")
        grants: list[str] = []
        for project_path in UI_ROOT.rglob("*.csproj"):
            project = _project_root(project_path)
            for item in _items(project, "InternalsVisibleTo"):
                target = _include(item)
                if target.casefold().startswith(forbidden_targets):
                    grants.append(f"{project_path.relative_to(ROOT)} -> {target}")
            for item in _items(project, "AssemblyAttribute"):
                attribute = _include(item)
                if not attribute.endswith("InternalsVisibleToAttribute"):
                    continue
                target = _metadata(item, "_Parameter1")
                if target.casefold().startswith(forbidden_targets):
                    grants.append(f"{project_path.relative_to(ROOT)} -> {target}")

        source_pattern = re.compile(
            r"InternalsVisibleTo(?:Attribute)?\s*\(\s*\"([^\"]+)\""
        )
        ui_sources = sorted(
            path
            for path in UI_ROOT.rglob("*.cs")
            if not {"bin", "obj"}.intersection(path.relative_to(UI_ROOT).parts)
        )
        for source_path in ui_sources:
            source = source_path.read_text(encoding="utf-8")
            for match in source_pattern.finditer(source):
                target = match.group(1)
                if target.casefold().startswith(forbidden_targets):
                    grants.append(f"{source_path.relative_to(ROOT)} -> {target}")

        self.assertEqual([], grants, "UI consumers and examples must use public contracts")

    def test_ca_product_project_references_only_its_semantic_project(self) -> None:
        project = _project_root(PRODUCT_PROJECT)
        references = [_include(item) for item in _items(project, "ProjectReference")]

        self.assertEqual(
            [
                "Hatifect.ChestsAnywhereOverlay.UI.Semantic/"
                "Hatifect.ChestsAnywhereOverlay.UI.Semantic.csproj"
            ],
            references,
        )

    def test_ca_compile_dependencies_use_versioned_ui_packages_only(self) -> None:
        for project_path in (SEMANTIC_PROJECT, PRODUCT_PROJECT):
            with self.subTest(project=project_path.relative_to(ROOT)):
                project = _project_root(project_path)
                packages = [
                    item
                    for item in _items(project, "PackageReference")
                    if _include(item).startswith("Hatifect.UI.")
                ]

                self.assertEqual(
                    ["Hatifect.UI.Experience"],
                    [_include(item) for item in packages],
                )
                package = packages[0]
                self.assertEqual(
                    EXACT_CENTRAL_UI_VERSION,
                    _metadata(package, "Version"),
                )
                self.assertEqual("compile", _metadata(package, "IncludeAssets"))
                self.assertEqual("all", _metadata(package, "PrivateAssets"))

                local_versions = _items(project, "HatifectUiPackageVersion")
                self.assertEqual([], local_versions, "CA must not override the central UI version")
                consumers = [
                    (item.text or "").strip().casefold()
                    for item in _items(project, "HatifectUiPackageConsumer")
                ]
                self.assertEqual(["true"], consumers)
                imports = [
                    (item.get("Project") or "").replace("\\", "/")
                    for item in _items(project, "Import")
                ]
                self.assertTrue(
                    any(path.endswith("Hatifect.UI.Packages.props") for path in imports),
                    "CA package consumers must import the central package contract",
                )

                ui_project_references = [
                    _include(item)
                    for item in _items(project, "ProjectReference")
                    if "Hatifect UI/" in _include(item)
                ]
                self.assertEqual([], ui_project_references)

                ui_assembly_references = [
                    _include(item)
                    for item in _items(project, "Reference")
                    if _include(item).startswith("Hatifect.UI.")
                ]
                self.assertEqual([], ui_assembly_references)
                ui_hint_paths = [
                    (item.text or "").strip()
                    for item in _items(project, "HintPath")
                    if "Hatifect.UI." in (item.text or "")
                ]
                self.assertEqual([], ui_hint_paths)

        semantic = _project_root(SEMANTIC_PROJECT)
        self.assertEqual([], _items(semantic, "ProjectReference"))

    def test_ca_projects_have_no_linked_source_foreign_import_or_hintpath_escape(self) -> None:
        expected_imports = {
            SEMANTIC_PROJECT: ["../../../../Hatifect.UI.Packages.props"],
            PRODUCT_PROJECT: [
                "../../../Hatifect.UI.Packages.props",
                "../../../Hatifect.Build.targets",
            ],
        }
        expected_compile_removes = {
            SEMANTIC_PROJECT: [],
            PRODUCT_PROJECT: [
                "Hatifect.ChestsAnywhereOverlay.UI.Semantic/**/*.cs",
                "tests/**/*.cs",
            ],
        }

        for project_path in (SEMANTIC_PROJECT, PRODUCT_PROJECT):
            with self.subTest(project=project_path.relative_to(ROOT)):
                project = _project_root(project_path)
                imports = [
                    (item.get("Project") or "").replace("\\", "/")
                    for item in _items(project, "Import")
                ]
                self.assertEqual(expected_imports[project_path], imports)

                compile_items = _items(project, "Compile")
                self.assertEqual(
                    expected_compile_removes[project_path],
                    [
                        (item.get("Remove") or "").replace("\\", "/")
                        for item in compile_items
                    ],
                )
                self.assertTrue(all(item.get("Include") is None for item in compile_items))
                self.assertEqual([], _items(project, "Link"))
                self.assertEqual([], _items(project, "Reference"))
                self.assertEqual([], _items(project, "HintPath"))

        reflection_escape = re.compile(
            r'(?:Assembly\.Load|Assembly\.GetType|Type\.GetType)\s*\([^\n]*Hatifect\.UI',
            re.IGNORECASE,
        )
        violations = [
            str(path.relative_to(ROOT))
            for path in _production_sources(CA_ROOT)
            if reflection_escape.search(path.read_text(encoding="utf-8"))
        ]
        self.assertEqual([], violations)

    def test_ca_source_does_not_import_ui_runtime_scene_or_platform_types(self) -> None:
        forbidden = (
            "Hatifect.UI.Runtime.Scene",
            "Hatifect.UI.Planning",
            "Hatifect.UI.Stardew",
            "Microsoft.Xna.Framework.Graphics",
            "GraphicsDevice",
        )
        violations: list[str] = []
        for source_path in _production_sources(CA_ROOT):
            source = source_path.read_text(encoding="utf-8")
            for token in forbidden:
                if token in source:
                    violations.append(f"{source_path.relative_to(ROOT)}: {token}")

        self.assertEqual(
            [],
            violations,
            "CA production source must consume only the host-neutral UI surface",
        )

    def test_public_semantic_surface_contract_exposes_no_platform_or_scene_types(self) -> None:
        source = SEMANTIC_SURFACE_CONTRACT.read_text(encoding="utf-8")
        expected_contracts = (
            "IUiSemanticSurfaceApi",
            "IUiSemanticSurfaceSession",
            "UiSemanticSurfaceOptions",
            "IUiSemanticSurfaceAutomation",
        )
        for contract in expected_contracts:
            with self.subTest(contract=contract):
                self.assertRegex(source, rf"\bpublic\s+[^\n]*\b{contract}\b")

        forbidden = (
            "Hatifect.UI.Runtime",
            "Hatifect.UI.Planning",
            "Hatifect.UI.Stardew",
            "Microsoft.Xna.Framework",
            "StardewModdingAPI",
            "StardewValley",
            "UiScene",
            "UiRect",
            "UiInteractionSnapshot",
            "GraphicsDevice",
            "SpriteBatch",
            "SpriteFont",
            "Texture2D",
            "IClickableMenu",
            "IModHelper",
        )
        exposed = [token for token in forbidden if token in source]

        self.assertEqual(
            [],
            exposed,
            "The public semantic surface must remain host-neutral and Scene-opaque",
        )

    def test_public_semantic_surface_contract_declares_one_shot_lifecycle(self) -> None:
        source = SEMANTIC_SURFACE_CONTRACT.read_text(encoding="utf-8")

        self.assertIn("Opaque single-use lifecycle handle", source)
        self.assertIn("Dismissal is terminal", source)
        self.assertIn("rejected after terminal dismissal", source)
        self.assertIn("outside-pointer dismissal policy are immutable", source)
        self.assertIn("changed, this terminally retires the surface", source)

    def test_live_acceptance_exercises_normalized_cancel_and_native_handoff(self) -> None:
        source = SEMANTIC_ACCEPTANCE.read_text(encoding="utf-8")
        entry = CA_MOD_ENTRY.read_text(encoding="utf-8")

        self.assertIn(
            "automation.Cancel(firstSurface!, UiSemanticSurfaceCancelInput.ControllerBack)",
            source,
        )
        self.assertGreaterEqual(
            source.count("UiSemanticSurfaceCancelInput.Escape"),
            1,
        )
        self.assertIn("repeatedOpenKeptOneSession", source)
        self.assertIn("OpenThroughNativeToggle(adapter, controller)", source)
        self.assertIn("_automatedTogglePulsePending = true", (CA_ROOT / "Integration" / "ChestsAnywhereAdapter.cs").read_text(encoding="utf-8"))
        self.assertNotIn("controller.Show()", source)
        self.assertIn("handoffSurface.Synchronize();", source)
        self.assertIn("controller.Shutdown();", source)
        self.assertIn("bool shutdownRestored = IsRestored", source)
        self.assertIn(
            "OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e) => _navigator.Shutdown();",
            entry,
        )
        self.assertIn("adapter.HasCurrentAutomatedOverlayLease", source)
        self.assertIn("&& IsRestored(controller, frontend, adapter)", source)

    def test_compatible_acceptance_completes_after_aggregating_cleanup_failures(self) -> None:
        source = SEMANTIC_ACCEPTANCE.read_text(encoding="utf-8")
        compatible = source.split("private static void ExecuteCompatible(", 1)[1].split(
            "private static void ExecuteAbsent(", 1
        )[0]
        cleanup = compatible.split("finally", 1)[1]

        self.assertIn("Attempt(controller.Hide, cleanupFailures);", cleanup)
        self.assertIn("Attempt(adapter.EndSession, cleanupFailures);", cleanup)
        self.assertIn("Attempt(controller.Update, cleanupFailures);", cleanup)
        aggregate = "failure = failure == null ? cleanup : new AggregateException(failure, cleanup);"
        self.assertIn(aggregate, cleanup)
        self.assertGreater(cleanup.index("recorder.Complete(failure);"), cleanup.index(aggregate))
        self.assertEqual(1, compatible.count("recorder.Complete(failure);"))

    def test_incompatible_live_acceptance_observes_the_registered_adapter(self) -> None:
        source = SEMANTIC_ACCEPTANCE.read_text(encoding="utf-8")
        adapter = CA_ADAPTER.read_text(encoding="utf-8")
        entry = CA_MOD_ENTRY.read_text(encoding="utf-8")

        self.assertIn("context => ExecuteIncompatible(adapter, context)", source)
        self.assertIn("bool passed = !adapter.IsSupported", source)
        self.assertIn("&& !adapter.HasSuppressedNativeSelectors", source)
        self.assertIn("&& !adapter.HasMutedNativeToggle", source)
        self.assertIn("&& adapter.HasRejectedAutomatedIncompatibleApi", source)
        self.assertNotIn("IncompatibleApiProbe", source)
        self.assertIn("ChestsAnywhereAdapter.CreateForCurrentHost(Helper, Monitor)", entry)
        self.assertIn("IsExactAutomatedScenario(IncompatibleAutomationScenario)", adapter)
        self.assertIn("new AutomatedIncompatibleApiFixture()", adapter)
        self.assertIn("_captureOverlay = CaptureOverlayForBoundary;", adapter)
        self.assertIn("object? installedApi = ResolveRawApi(helper);", adapter)
        self.assertIn("&& installedApi != null", adapter)

    def test_ca_title_lifecycle_observes_before_cleanup(self) -> None:
        source = SEMANTIC_ACCEPTANCE.read_text(encoding="utf-8")
        self.assertIn("UiAutomatedAcceptanceScenario.ForReturnToTitle(", source)
        self.assertIn("beforeReturnToTitle: context => PrepareReturnToTitle", source)
        self.assertIn("afterReturnedToTitle: context => VerifyReturnToTitle", source)
        verification = source.split("private static void VerifyReturnToTitle(", 1)[1].split("private static void ExecuteCompatible(", 1)[0]
        self.assertIn("!controller.HasActiveSessionForAcceptance", verification)
        self.assertIn("TryReleaseAutomatedLeaseAfterReturnedToTitle", verification)
        self.assertNotIn("controller.Shutdown()", verification)
        self.assertNotIn("controller.Hide()", verification)

    def test_capture_exception_live_acceptance_uses_production_capture_boundary(self) -> None:
        source = SEMANTIC_ACCEPTANCE.read_text(encoding="utf-8")
        adapter = CA_ADAPTER.read_text(encoding="utf-8")

        self.assertIn(
            'CaptureExceptionScenario = "semantic.chests-anywhere-overlay.capture-exception"',
            source,
        )
        self.assertIn("order: 730", source)
        self.assertIn("requiresWorld: true", source)
        self.assertIn("adapter.ArmAutomatedCaptureFailure();", source)
        self.assertIn("controller.Update();", source)
        self.assertIn("&& !adapter.IsSupported", source)
        self.assertIn("&& IsRestored(controller, frontend, adapter)", source)
        self.assertIn("CaptureOverlayForBoundary", adapter)
        self.assertIn("ChestsAnywhereCaptureBoundary.TryAcquire", adapter)
        self.assertIn("IsExactAutomatedScenario(CaptureExceptionAutomationScenario)", adapter)

    def test_ca_manifest_has_only_required_ui_and_optional_chests_anywhere(self) -> None:
        manifest = json.loads((CA_ROOT / "manifest.json").read_text(encoding="utf-8"))
        dependencies = manifest["Dependencies"]
        actual = {
            dependency["UniqueID"]: dependency["IsRequired"]
            for dependency in dependencies
        }

        self.assertEqual(2, len(dependencies))
        self.assertEqual(
            {
                "Hatifect.UI": True,
                "Pathoschild.ChestsAnywhere": False,
            },
            actual,
        )


if __name__ == "__main__":
    unittest.main()
