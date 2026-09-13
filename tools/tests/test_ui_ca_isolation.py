import importlib.util
import io
import re
import stat
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from contextlib import contextmanager, redirect_stdout
from pathlib import Path
from types import ModuleType
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
HELPER_PATH = ROOT / "tools" / "ui_ca_isolation.py"
LAUNCHER_PATH = ROOT / "tools" / "hatifect-isolated-ui-ca"
CA_MODULE = "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay"
CA_PROJECT = f"{CA_MODULE}/Hatifect.ChestsAnywhereOverlay.csproj"
ROOT_INFRASTRUCTURE = (
    "Directory.Build.props",
    "Directory.Build.targets",
    "global.json",
    "Hatifect.Build.targets",
    "Hatifect.UI.Packages.props",
)


def _load_helper() -> tuple[ModuleType | None, str | None]:
    if not HELPER_PATH.is_file():
        return None, f"future helper is not implemented: {HELPER_PATH}"
    spec = importlib.util.spec_from_file_location(
        "hatifect_ui_ca_isolation_tests",
        HELPER_PATH,
    )
    if spec is None or spec.loader is None:
        return None, f"unable to create an import spec for {HELPER_PATH}"
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    try:
        spec.loader.exec_module(module)
    except Exception as error:  # pragma: no cover - reported by every contract test
        return None, f"unable to import {HELPER_PATH}: {type(error).__name__}: {error}"
    return module, None


ISOLATION, HELPER_LOAD_ERROR = _load_helper()


def _write_projection_fixture(repository: Path) -> tuple[str, ...]:
    for relative in ROOT_INFRASTRUCTURE:
        path = repository / relative
        path.write_text(f"fixture:{relative}\n", encoding="utf-8")

    expected_module_members = (
        f"{CA_MODULE}/Hatifect.ChestsAnywhereOverlay.csproj",
        f"{CA_MODULE}/ModEntry.cs",
        f"{CA_MODULE}/manifest.json",
        f"{CA_MODULE}/i18n/default.json",
        (
            f"{CA_MODULE}/Hatifect.ChestsAnywhereOverlay.UI.Semantic/"
            "Hatifect.ChestsAnywhereOverlay.UI.Semantic.csproj"
        ),
        f"{CA_MODULE}/tests/BoundaryProbe.cs",
    )
    for relative in expected_module_members:
        path = repository / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(f"fixture:{relative}\n", encoding="utf-8")

    for generated in (
        f"{CA_MODULE}/bin/Release/leaked.dll",
        f"{CA_MODULE}/obj/project.assets.json",
    ):
        path = repository / generated
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("must-not-project\n", encoding="utf-8")

    for forbidden in (
        "Hatifect UI/Hatifect.UI.Experience/Hatifect.UI.Experience.csproj",
        "Hatifect Flow/Hatifect.Flow.csproj",
        "Outside Module/Outside.csproj",
        "unlisted-root.txt",
    ):
        path = repository / forbidden
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("must-not-project\n", encoding="utf-8")

    return tuple(sorted((*ROOT_INFRASTRUCTURE, *expected_module_members)))


class UiCaIsolationTests(unittest.TestCase):
    def require_helper(self) -> ModuleType:
        if ISOLATION is None:
            self.fail(HELPER_LOAD_ERROR or "future isolation helper failed to load")
        return ISOLATION

    def require_api(self, helper: ModuleType, name: str):
        self.assertTrue(
            hasattr(helper, name),
            f"ui_ca_isolation.py must expose {name}",
        )
        return getattr(helper, name)

    @contextmanager
    def isolated_command_fixture(self, test_count: int | None):
        helper = self.require_helper()
        with tempfile.TemporaryDirectory(prefix="hatifect-ui-ca-run-test.") as temporary:
            root = Path(temporary).resolve()
            repository = root / "repository"
            repository.mkdir()
            _write_projection_fixture(repository)
            home = root / "home"
            cache = home / ".nuget/packages"
            cache.mkdir(parents=True)
            for name in helper.REQUIRED_EXTERNAL_PACKAGES:
                (cache / name).write_bytes(b"external package fixture")
            dotnet = root / "dotnet"
            dotnet.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            dotnet.chmod(0o755)
            calls: list[list[str]] = []

            def completed(command, *, cwd, env, check):
                calls.append(command)
                if command[0].endswith("/tools/hatifect-pack-ui"):
                    feed = Path(command[1])
                    feed.mkdir()
                    for name in ("Hatifect.UI.Experience", "Hatifect.UI.Semantics"):
                        (feed / f"{name}.1.0.0.nupkg").write_bytes(b"UI package fixture")
                elif command[1] == "build":
                    output = Path(command[2]).parent / "bin/Release/net6.0"
                    output.mkdir(parents=True)
                    for name in helper.EXPECTED_CA_DLLS:
                        (output / name).write_bytes(b"CA assembly fixture")
                elif command[1] == "test" and test_count is not None:
                    results = (Path(command[command.index("--results-directory") + 1])
                               if "--results-directory" in command else Path(cwd).parent / "test-results")
                    results.mkdir(parents=True, exist_ok=True)
                    trx = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
                    records = ET.SubElement(trx, "Results")
                    for number in range(test_count):
                        ET.SubElement(records, "UnitTestResult", outcome="Passed", executionId=f"test-{number}")
                    summary = ET.SubElement(trx, "ResultSummary", outcome="Completed")
                    ET.SubElement(summary, "Counters", total=str(test_count), executed=str(test_count),
                                  passed=str(test_count), failed="0")
                    ET.ElementTree(trx).write(results / "tests.trx")
                elif command[1] == "msbuild":
                    deploy = next(value.split("=", 1)[1] for value in command
                                  if value.startswith("-p:HatifectDeployRoot="))
                    module = Path(deploy) / CA_MODULE
                    module.mkdir(parents=True)
                    for name in helper.EXPECTED_CA_DLLS:
                        (module / name).write_bytes(b"CA assembly fixture")
                    (module / "manifest.json").write_text("{}\n", encoding="utf-8")
                return subprocess.CompletedProcess(command, 0)

            with patch.object(helper, "ROOT", repository), patch.object(Path, "home", return_value=home), \
                    patch.object(helper.subprocess, "run", side_effect=completed):
                yield helper, dotnet, calls

    def test_projection_allowlist_is_exactly_ca_module_and_root_infrastructure(self) -> None:
        helper = self.require_helper()
        self.assertEqual(
            ROOT_INFRASTRUCTURE,
            tuple(self.require_api(helper, "PROJECTION_ROOT_FILES")),
        )
        self.assertEqual(
            (CA_MODULE,),
            tuple(self.require_api(helper, "PROJECTION_ROOT_DIRECTORIES")),
        )

        with tempfile.TemporaryDirectory(prefix="hatifect-ui-ca-projection-test.") as temporary:
            repository = Path(temporary) / "repository"
            repository.mkdir()
            expected = _write_projection_fixture(repository)

            members = tuple(self.require_api(helper, "projection_members")(repository))

        self.assertEqual(expected, members)
        self.assertFalse(any(member.startswith("Hatifect UI/") for member in members))
        self.assertFalse(any(member.startswith("Hatifect Flow/") for member in members))
        self.assertFalse(any(member.startswith("Hatifect/") for member in members))
        self.assertFalse(any("/bin/" in member or "/obj/" in member for member in members))

    def test_projection_members_reject_symlinks_in_allowlisted_inputs(self) -> None:
        helper = self.require_helper()
        projection_members = self.require_api(helper, "projection_members")
        isolation_error = self.require_api(helper, "IsolationError")

        for location in ("root-infrastructure", "ca-module"):
            with self.subTest(location=location), tempfile.TemporaryDirectory(
                prefix="hatifect-ui-ca-symlink-test."
            ) as temporary:
                repository = Path(temporary) / "repository"
                repository.mkdir()
                _write_projection_fixture(repository)
                external = Path(temporary) / "external.txt"
                external.write_text("outside\n", encoding="utf-8")
                if location == "root-infrastructure":
                    link = repository / "global.json"
                    link.unlink()
                else:
                    link = repository / CA_MODULE / "linked-source.cs"
                link.symlink_to(external)

                with self.assertRaisesRegex(isolation_error, "(?i)symlink"):
                    projection_members(repository)

    def test_build_input_and_package_properties_are_exact_and_absolute(self) -> None:
        helper = self.require_helper()
        self.assertEqual(CA_PROJECT, self.require_api(helper, "CA_PROJECT"))
        msbuild_properties = self.require_api(helper, "msbuild_properties")
        isolation_error = self.require_api(helper, "IsolationError")

        with tempfile.TemporaryDirectory(prefix="hatifect-ui-ca-input-test.") as temporary:
            root = Path(temporary).resolve()
            feed = root / "ui-packages"
            packages = root / "consumer-packages"
            feed.mkdir()
            packages.mkdir()

            self.assertEqual(
                {
                    "HatifectUiLocalFeed": str(feed),
                    "RestoreAdditionalProjectSources": str(feed),
                    "RestorePackagesPath": str(packages),
                },
                msbuild_properties(feed, packages),
            )

            for name, relative_feed, relative_packages in (
                ("feed", Path("relative-feed"), packages),
                ("packages", feed, Path("relative-packages")),
            ):
                with self.subTest(relative=name), self.assertRaisesRegex(
                    isolation_error,
                    "(?i)absolute",
                ):
                    msbuild_properties(relative_feed, relative_packages)

    def test_absolute_path_scanner_detects_utf8_and_utf16(self) -> None:
        helper = self.require_helper()
        scan_absolute_paths = self.require_api(helper, "scan_absolute_paths")

        with tempfile.TemporaryDirectory(prefix="hatifect-ui-ca-scan-test.") as temporary:
            output = Path(temporary) / "output"
            nested = output / "nested"
            nested.mkdir(parents=True)
            (output / "clean.dll").write_bytes(b"binary without machine paths")
            (output / "utf8.dll").write_bytes(
                b"prefix\0/Users/example/work/Hatifect/obj/Release/product.pdb\0suffix"
            )
            (nested / "utf16-le.dll").write_bytes(
                "C:\\Users\\example\\work\\Hatifect\\obj\\product.pdb".encode(
                    "utf-16-le"
                )
            )
            (nested / "utf16-be.dll").write_bytes(
                "/home/runner/work/Hatifect/obj/product.pdb".encode("utf-16-be")
            )

            findings = tuple(scan_absolute_paths(output))

        self.assertEqual(
            ("nested/utf16-be.dll", "nested/utf16-le.dll", "utf8.dll"),
            findings,
        )

    def test_validate_ca_output_accepts_exact_product_dll_set(self) -> None:
        helper = self.require_helper()
        validate_ca_output = self.require_api(helper, "validate_ca_output")

        with tempfile.TemporaryDirectory(prefix="hatifect-ui-ca-output-test.") as temporary:
            output = Path(temporary)
            (output / "Hatifect.ChestsAnywhereOverlay.dll").write_bytes(b"product")
            (output / "Hatifect.ChestsAnywhereOverlay.UI.Semantic.dll").write_bytes(
                b"semantic"
            )
            (output / "manifest.json").write_text("{}\n", encoding="utf-8")

            result = validate_ca_output(output)

        self.assertIsNone(result)

    def test_validate_ca_output_rejects_missing_extra_and_hatifect_ui_dlls(self) -> None:
        helper = self.require_helper()
        validate_ca_output = self.require_api(helper, "validate_ca_output")
        isolation_error = self.require_api(helper, "IsolationError")
        cases = {
            "missing-semantic": (
                ("Hatifect.ChestsAnywhereOverlay.dll",),
                "Hatifect.ChestsAnywhereOverlay.UI.Semantic.dll",
            ),
            "extra-product": (
                (
                    "Hatifect.ChestsAnywhereOverlay.dll",
                    "Hatifect.ChestsAnywhereOverlay.UI.Semantic.dll",
                    "Unexpected.dll",
                ),
                "Unexpected.dll",
            ),
            "ui-runtime": (
                (
                    "Hatifect.ChestsAnywhereOverlay.dll",
                    "Hatifect.ChestsAnywhereOverlay.UI.Semantic.dll",
                    "Hatifect.UI.Experience.dll",
                ),
                "Hatifect.UI.Experience.dll",
            ),
        }
        with tempfile.TemporaryDirectory(prefix="hatifect-ui-ca-output-test.") as temporary:
            root = Path(temporary)
            for name, (dlls, diagnostic) in cases.items():
                with self.subTest(case=name):
                    output = root / name
                    output.mkdir()
                    for dll in dlls:
                        (output / dll).write_bytes(dll.encode("utf-8"))

                    with self.assertRaisesRegex(
                        isolation_error,
                        re.escape(diagnostic),
                    ):
                        validate_ca_output(output)

    def test_launcher_delegates_to_ui_ca_isolation_helper(self) -> None:
        self.assertTrue(
            LAUNCHER_PATH.is_file(),
            f"future launcher is not implemented: {LAUNCHER_PATH}",
        )
        mode = LAUNCHER_PATH.stat().st_mode
        self.assertTrue(mode & stat.S_IXUSR, "isolation launcher must be executable")
        source = LAUNCHER_PATH.read_text(encoding="utf-8")
        self.assertIn("ui_ca_isolation.py", source)

    def test_exit_zero_without_positive_trx_rejects_before_deployment(self) -> None:
        for evidence in (None, 0):
            with self.subTest(evidence=evidence), self.isolated_command_fixture(evidence) as (helper, dotnet, calls):
                output = io.StringIO()
                with redirect_stdout(output), self.assertRaises(helper.IsolationError):
                    helper.run_isolated(dotnet, dotnet)
                self.assertTrue(any(command[1] == "test" for command in calls))
                self.assertFalse(any("-t:DeployHatifectModule" in command for command in calls))
                self.assertNotIn("boundary: PASS", output.getvalue())

    def test_positive_trx_reports_actual_counts_and_uses_fresh_results_each_run(self) -> None:
        result_directories = []
        for _ in range(2):
            with self.isolated_command_fixture(3) as (helper, dotnet, calls):
                output = io.StringIO()
                with redirect_stdout(output):
                    helper.run_isolated(dotnet, dotnet)
                test_command = next(command for command in calls if command[1] == "test")
                self.assertIn("--logger", test_command)
                self.assertIn("trx;LogFileName=tests.trx", test_command)
                result_directories.append(test_command[test_command.index("--results-directory") + 1])
                self.assertIn("2 UI packages", output.getvalue())
                self.assertIn("3 tests passed", output.getvalue())
                self.assertTrue(any("-t:DeployHatifectModule" in command for command in calls))
        self.assertNotEqual(*result_directories)


if __name__ == "__main__":
    unittest.main()
