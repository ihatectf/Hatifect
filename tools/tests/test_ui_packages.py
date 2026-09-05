import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ui_packages


class UiPackageProjectionTests(unittest.TestCase):
    def test_host_free_projection_excludes_only_stardew_and_remains_closed(self) -> None:
        contract = ui_packages.load_contract()
        complete = ui_packages.selected_packages(contract)
        projected = ui_packages.selected_packages(contract, host_free=True)
        ids = {package["Id"] for package in projected}
        self.assertEqual(8, len(complete))
        self.assertEqual(7, len(projected))
        self.assertEqual({"Hatifect.UI.Stardew"}, {p["Id"] for p in complete} - ids)
        for package in projected:
            self.assertLessEqual(set(package["Dependencies"]), ids)
        self.assertEqual(8, len(contract["Packages"]))

    def test_partial_feed_requires_explicit_mode_and_rejects_extra_stardew(self) -> None:
        contract = ui_packages.load_contract()
        with tempfile.TemporaryDirectory(prefix="hatifect-package-projection.") as temporary:
            feed = Path(temporary)
            for package in ui_packages.selected_packages(contract, host_free=True):
                self.write_package(feed, contract, package)
            ui_packages.verify_feed(contract, feed, host_free=True)
            with self.assertRaisesRegex(ValueError, "8 nupkg files; found 7"):
                ui_packages.verify_feed(contract, feed)
            stardew = next(p for p in contract["Packages"] if p["Id"] == "Hatifect.UI.Stardew")
            self.write_package(feed, contract, stardew)
            ui_packages.verify_feed(contract, feed)
            with self.assertRaisesRegex(ValueError, "7 nupkg files; found 8"):
                ui_packages.verify_feed(contract, feed, host_free=True)

    def test_feed_rejects_dependency_version_that_only_contains_the_pinned_version(self) -> None:
        contract = ui_packages.load_contract()
        for dependency_version in (contract["Version"] + "0", f"({contract['Version']},)"):
            with self.subTest(version=dependency_version), tempfile.TemporaryDirectory() as temporary:
                feed = Path(temporary)
                for package in contract["Packages"]:
                    self.write_package(feed, contract, package, dependency_version=dependency_version)
                with self.assertRaisesRegex(ValueError, "is not bound to"):
                    ui_packages.verify_feed(contract, feed)

    def test_pack_preserves_the_release_compiler_profile_without_shipping_symbols(self) -> None:
        launcher = (ui_packages.ROOT / "tools/hatifect-pack-ui").read_text()
        properties = ET.parse(ui_packages.ROOT / "Hatifect.UI.Packages.props").getroot()

        self.assertIn("-c Release", launcher)
        self.assertNotIn("-p:DebugSymbols=", launcher)
        self.assertNotIn("-p:DebugType=", launcher)
        self.assertEqual(["false"], [item.text for item in properties.iter("IncludeSymbols")])

    def test_feed_still_rejects_debug_symbol_payload(self) -> None:
        contract = ui_packages.load_contract()
        with tempfile.TemporaryDirectory() as temporary:
            feed = Path(temporary)
            for package in contract["Packages"]:
                self.write_package(feed, contract, package)
            identity = contract["Packages"][0]["Id"]
            path = feed / f"{identity}.{contract['Version']}.nupkg"
            with zipfile.ZipFile(path, "a") as archive:
                archive.writestr(f"lib/net6.0/{identity}.pdb", b"symbol fixture")

            with self.assertRaisesRegex(ValueError, "unexpected binary payload"):
                ui_packages.verify_feed(contract, feed)

    @staticmethod
    def write_package(feed: Path, contract: dict, package: dict, *, dependency_version: str | None = None) -> None:
        identity = package["Id"]
        version = contract["Version"]
        dependencies = "".join(
            f'<dependency id="{dependency}" version="{dependency_version or f"[{version}]"}" />'
            for dependency in package["Dependencies"]
        )
        with zipfile.ZipFile(feed / f"{identity}.{version}.nupkg", "w") as archive:
            archive.writestr(
                f"{identity}.nuspec",
                f"<package><metadata><id>{identity}</id><version>{version}</version>"
                f"<dependencies>{dependencies}</dependencies></metadata></package>",
            )
            archive.writestr(f"lib/net6.0/{identity}.dll", b"fixture")


if __name__ == "__main__":
    unittest.main()
