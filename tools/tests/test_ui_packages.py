import hashlib
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
import sys
from unittest.mock import patch

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
            self.verify_feed(contract, feed, host_free=True)
            with self.assertRaisesRegex(ValueError, "8 nupkg files; found 7"):
                self.verify_feed(contract, feed)
            stardew = next(p for p in contract["Packages"] if p["Id"] == "Hatifect.UI.Stardew")
            self.write_package(feed, contract, stardew)
            self.verify_feed(contract, feed)
            with self.assertRaisesRegex(ValueError, "7 nupkg files; found 8"):
                self.verify_feed(contract, feed, host_free=True)

    def test_feed_rejects_dependency_version_that_only_contains_the_pinned_version(self) -> None:
        contract = ui_packages.load_contract()
        for dependency_version in (contract["Version"] + "0", f"({contract['Version']},)"):
            with self.subTest(version=dependency_version), tempfile.TemporaryDirectory() as temporary:
                feed = Path(temporary)
                for package in contract["Packages"]:
                    self.write_package(feed, contract, package, dependency_version=dependency_version)
                with self.assertRaisesRegex(ValueError, "is not bound to"):
                    self.verify_feed(contract, feed)

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
                self.verify_feed(contract, feed)

    def test_feed_rejects_dll_changed_after_its_package_was_created(self) -> None:
        contract = ui_packages.load_contract()
        with tempfile.TemporaryDirectory() as temporary:
            feed = Path(temporary)
            for package in contract["Packages"]:
                self.write_package(feed, contract, package)
            self.verify_feed(contract, feed)

            package = contract["Packages"][0]
            producer = self.producer_path(feed, package)
            original = producer.read_bytes()
            producer.write_bytes(original[:-1] + b"!")
            with self.assertRaisesRegex(ValueError, f"{package['Id']}: packaged DLL differs"):
                self.verify_feed(contract, feed)

            # A failed audit never replaces either artifact to make them agree.
            with zipfile.ZipFile(feed / f"{package['Id']}.{contract['Version']}.nupkg") as archive:
                self.assertEqual(original, archive.read(f"lib/net6.0/{package['Id']}.dll"))
            self.assertEqual(original[:-1] + b"!", producer.read_bytes())

    def test_feed_rejects_missing_producer_even_with_complete_packages(self) -> None:
        contract = ui_packages.load_contract()
        with tempfile.TemporaryDirectory() as temporary:
            feed = Path(temporary)
            for package in contract["Packages"]:
                self.write_package(feed, contract, package)
            package = contract["Packages"][-1]
            producer = self.producer_path(feed, package)
            producer.unlink()

            with self.assertRaisesRegex(ValueError, f"{package['Id']}: missing final Release producer"):
                self.verify_feed(contract, feed)
            self.assertFalse(producer.exists())

    def test_producer_mismatch_precedes_dependency_and_path_errors(self) -> None:
        contract = ui_packages.load_contract()
        package = min(contract["Packages"], key=lambda item: item["Id"])
        with tempfile.TemporaryDirectory() as temporary:
            feed = Path(temporary)
            for item in contract["Packages"]:
                self.write_package(feed, contract, item, dependency_version="0.0.0")
            producer = self.producer_path(feed, package)
            packaged_bytes = producer.read_bytes()
            producer_bytes = packaged_bytes + b" changed after packing"
            producer.write_bytes(producer_bytes)
            path = feed / f"{package['Id']}.{contract['Version']}.nupkg"
            with zipfile.ZipFile(path, "a") as archive:
                archive.writestr("repository.txt", str((feed / "producer").resolve()).encode())
            archive_bytes = path.read_bytes()

            with self.assertRaises(ValueError) as error:
                self.verify_feed(contract, feed)

            self.assertEqual(
                f"{package['Id']}: packaged DLL differs from final Release producer: {producer}; "
                f"packaged SHA-256={hashlib.sha256(packaged_bytes).hexdigest()}, "
                f"producer SHA-256={hashlib.sha256(producer_bytes).hexdigest()}",
                str(error.exception),
            )
            self.assertEqual(archive_bytes, path.read_bytes())
            self.assertEqual(producer_bytes, producer.read_bytes())

    def test_dependency_error_precedes_repository_path_leak(self) -> None:
        contract = ui_packages.load_contract()
        package = min(contract["Packages"], key=lambda item: item["Id"])
        with tempfile.TemporaryDirectory() as temporary:
            feed = Path(temporary)
            for item in contract["Packages"]:
                self.write_package(feed, contract, item)
            self.write_package(feed, contract, {**package, "Dependencies": []})
            path = feed / f"{package['Id']}.{contract['Version']}.nupkg"
            leak = str((feed / "producer").resolve()).encode()
            with zipfile.ZipFile(path, "a") as archive:
                archive.writestr("repository.txt", leak)
            archive_bytes = path.read_bytes()

            with self.assertRaises(ValueError) as error:
                self.verify_feed(contract, feed)

            self.assertEqual(
                f"{package['Id']}: UI dependencies [] != {sorted(package['Dependencies'])}",
                str(error.exception),
            )
            self.assertEqual(archive_bytes, path.read_bytes())

            # Repair only the dependencies; the same path leak must then surface.
            self.write_package(feed, contract, package)
            with zipfile.ZipFile(path, "a") as archive:
                archive.writestr("repository.txt", leak)
            with self.assertRaises(ValueError) as error:
                self.verify_feed(contract, feed)
            self.assertEqual(
                f"{package['Id']}: repository path leaked into repository.txt",
                str(error.exception),
            )

    def test_each_sorted_package_is_fully_checked_before_the_next(self) -> None:
        contract = ui_packages.load_contract()
        first, *_, last = sorted(contract["Packages"], key=lambda item: item["Id"])
        with tempfile.TemporaryDirectory() as temporary:
            feed = Path(temporary)
            for package in contract["Packages"]:
                self.write_package(feed, contract, package)
            self.write_package(feed, contract, {**first, "Dependencies": []})
            missing_producer = self.producer_path(feed, last)
            missing_producer.unlink()

            with self.assertRaises(ValueError) as error:
                self.verify_feed(contract, feed)

            self.assertEqual(
                f"{first['Id']}: UI dependencies [] != {sorted(first['Dependencies'])}",
                str(error.exception),
            )
            self.assertFalse(missing_producer.exists())

            self.write_package(feed, contract, first)
            with self.assertRaises(ValueError) as error:
                self.verify_feed(contract, feed)
            self.assertEqual(
                f"{last['Id']}: missing final Release producer: {missing_producer}",
                str(error.exception),
            )

    @staticmethod
    def verify_feed(contract: dict, feed: Path, *, host_free: bool = False) -> None:
        with patch.object(ui_packages, "ROOT", feed / "producer"):
            ui_packages.verify_feed(contract, feed, host_free=host_free)

    @staticmethod
    def producer_path(feed: Path, package: dict) -> Path:
        return (
            feed / "producer" / Path(package["Project"]).parent
            / "bin/Release/net6.0" / f"{package['Id']}.dll"
        )

    @staticmethod
    def write_package(feed: Path, contract: dict, package: dict, *, dependency_version: str | None = None) -> None:
        identity = package["Id"]
        version = contract["Version"]
        payload = f"{identity} DLL fixture".encode()
        producer = UiPackageProjectionTests.producer_path(feed, package)
        producer.parent.mkdir(parents=True, exist_ok=True)
        producer.write_bytes(payload)
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
            archive.writestr(f"lib/net6.0/{identity}.dll", payload)


if __name__ == "__main__":
    unittest.main()
