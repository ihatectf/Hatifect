#!/usr/bin/env python3
"""Build and test CA Overlay from a projection with no UI source tree."""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Iterable

from validation import ValidationError, read_trx


ROOT = Path(__file__).resolve().parents[1]
PROJECTION_ROOT_FILES = (
    "Directory.Build.props",
    "global.json",
    "Hatifect.Build.targets",
    "Hatifect.UI.Packages.props",
)
PROJECTION_ROOT_DIRECTORIES = (
    "Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay",
)
CA_MODULE = PROJECTION_ROOT_DIRECTORIES[0]
CA_PROJECT = f"{CA_MODULE}/Hatifect.ChestsAnywhereOverlay.csproj"
CA_TEST_PROJECT = (
    f"{CA_MODULE}/tests/Hatifect.ChestsAnywhereOverlay.Tests/"
    "Hatifect.ChestsAnywhereOverlay.Tests.csproj"
)
EXPECTED_CA_DLLS = {
    "Hatifect.ChestsAnywhereOverlay.dll",
    "Hatifect.ChestsAnywhereOverlay.UI.Semantic.dll",
}
REQUIRED_EXTERNAL_PACKAGES = {
    "pathoschild.stardew.modbuildconfig.4.3.2.nupkg",
    "microsoft.net.test.sdk.17.13.0.nupkg",
    "xunit.2.9.3.nupkg",
    "xunit.runner.visualstudio.2.8.2.nupkg",
}
GENERATED_DIRECTORIES = {"bin", "obj"}


class IsolationError(ValueError):
    """Raised when the isolated boundary cannot be proven fail-closed."""


def _require_plain_file(path: Path, subject: str) -> None:
    if path.is_symlink():
        raise IsolationError(f"{subject} must not be a symlink: {path}")
    if not path.is_file():
        raise IsolationError(f"{subject} is missing or is not a file: {path}")


def projection_members(repository: Path) -> tuple[str, ...]:
    """Return the exact regular-file allowlist copied into the CA projection."""
    repository = repository.resolve()
    if not repository.is_dir():
        raise IsolationError(f"repository root is missing: {repository}")

    members: list[str] = []
    for relative in PROJECTION_ROOT_FILES:
        source = repository / relative
        _require_plain_file(source, "projection infrastructure file")
        members.append(relative)

    for relative in PROJECTION_ROOT_DIRECTORIES:
        source_root = repository / relative
        if source_root.is_symlink():
            raise IsolationError(f"projection directory must not be a symlink: {source_root}")
        if not source_root.is_dir():
            raise IsolationError(f"projection directory is missing: {source_root}")
        for current, directory_names, file_names in os.walk(source_root, followlinks=False):
            current_path = Path(current)
            retained_directories: list[str] = []
            for name in directory_names:
                child = current_path / name
                if child.is_symlink():
                    raise IsolationError(f"projection input contains a symlink: {child}")
                if name not in GENERATED_DIRECTORIES:
                    retained_directories.append(name)
            directory_names[:] = retained_directories
            for name in file_names:
                child = current_path / name
                _require_plain_file(child, "projection input")
                members.append(child.relative_to(repository).as_posix())

    return tuple(sorted(members))


def msbuild_properties(feed: Path, packages: Path) -> dict[str, str]:
    """Create the exact absolute restore boundary used by the isolated consumer."""
    if not feed.is_absolute() or not packages.is_absolute():
        raise IsolationError("UI feed and isolated package cache paths must be absolute")
    return {
        "HatifectUiLocalFeed": str(feed),
        "RestoreAdditionalProjectSources": str(feed),
        "RestorePackagesPath": str(packages),
    }


_UTF8_ABSOLUTE_PATH = re.compile(
    rb"(?:[A-Za-z]:\\(?:Users|ProgramData|Temp)\\|/(?:Users|home|private|tmp|var/tmp)/)"
)
_TEXT_ABSOLUTE_PATH = re.compile(
    r"(?:[A-Za-z]:\\(?:Users|ProgramData|Temp)\\|/(?:Users|home|private|tmp|var/tmp)/)"
)


def _contains_absolute_path(payload: bytes) -> bool:
    if _UTF8_ABSOLUTE_PATH.search(payload):
        return True
    for encoding in ("utf-16-le", "utf-16-be"):
        decoded = payload.decode(encoding, errors="ignore")
        if _TEXT_ABSOLUTE_PATH.search(decoded):
            return True
    return False


def scan_absolute_paths(root: Path) -> tuple[str, ...]:
    """Return regular output files containing common machine-absolute path forms."""
    root = root.resolve()
    if not root.is_dir():
        raise IsolationError(f"output scan root is missing: {root}")
    findings: list[str] = []
    for path in sorted(root.rglob("*"), key=lambda value: value.relative_to(root).as_posix()):
        if path.is_symlink():
            raise IsolationError(f"output scan encountered a symlink: {path}")
        if path.is_file() and _contains_absolute_path(path.read_bytes()):
            findings.append(path.relative_to(root).as_posix())
    return tuple(findings)


def validate_ca_output(output: Path) -> None:
    """Require exactly the two CA-owned runtime assemblies and no UI-owned DLL."""
    output = output.resolve()
    if not output.is_dir():
        raise IsolationError(f"CA output directory is missing: {output}")
    actual = {
        path.relative_to(output).as_posix()
        for path in output.rglob("*.dll")
        if path.is_file()
    }
    if actual != EXPECTED_CA_DLLS:
        missing = sorted(EXPECTED_CA_DLLS - actual)
        extra = sorted(actual - EXPECTED_CA_DLLS)
        details: list[str] = []
        if missing:
            details.append("missing " + ", ".join(missing))
        if extra:
            details.append("unexpected " + ", ".join(extra))
        raise IsolationError("CA output DLL set is not exact: " + "; ".join(details))


def _copy_projection(repository: Path, projection: Path, members: Iterable[str]) -> None:
    projection.mkdir(parents=True, exist_ok=False)
    for relative in members:
        source = repository / relative
        destination = projection / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination, follow_symlinks=False)


def _prepare_external_feed(destination: Path) -> None:
    """Mirror cached non-UI packages into a flat, read-only source for the fresh consumer cache."""
    destination.mkdir(parents=True, exist_ok=False)
    cache_roots = (
        Path.home() / ".nuget/packages",
        ROOT / "artifacts/ui-consumer-packages",
    )
    for cache_root in cache_roots:
        if not cache_root.is_dir():
            continue
        for source in sorted(cache_root.rglob("*.nupkg")):
            if source.name.casefold().startswith("hatifect.ui."):
                continue
            target = destination / source.name.casefold()
            if target.exists():
                if target.read_bytes() != source.read_bytes():
                    raise IsolationError(f"conflicting cached package payloads: {source.name}")
                continue
            shutil.copy2(source, target)

    available = {path.name.casefold() for path in destination.glob("*.nupkg")}
    missing = sorted(REQUIRED_EXTERNAL_PACKAGES - available)
    if missing:
        raise IsolationError(
            "external package cache is incomplete; restore the canonical solution once before "
            "running the offline isolated gate: " + ", ".join(missing)
        )
    if any(path.name.casefold().startswith("hatifect.ui.") for path in destination.glob("*.nupkg")):
        raise IsolationError("UI packages must come only from the newly produced UI feed")


def _run(command: list[str], *, cwd: Path, environment: dict[str, str]) -> None:
    printable = " ".join(command)
    print(f"[isolated] {printable}", flush=True)
    completed = subprocess.run(command, cwd=cwd, env=environment, check=False)
    if completed.returncode != 0:
        raise IsolationError(
            f"isolated command failed with exit code {completed.returncode}: {printable}"
        )


def _property_arguments(properties: dict[str, str]) -> list[str]:
    return [f"-p:{name}={value}" for name, value in sorted(properties.items())]


def run_isolated(build_dotnet: Path, test_dotnet: Path, *, keep: bool = False) -> Path | None:
    for executable, subject in ((build_dotnet, "build dotnet"), (test_dotnet, "test dotnet")):
        if not executable.is_absolute() or not executable.is_file() or not os.access(executable, os.X_OK):
            raise IsolationError(f"{subject} must be an absolute executable path: {executable}")

    temporary = Path(tempfile.mkdtemp(prefix="hatifect-ui-ca-isolated.")).resolve()
    projection = temporary / "projection"
    feed = temporary / "ui-packages"
    external_feed = temporary / "external-packages"
    packages = temporary / "consumer-packages"
    deploy_root = temporary / "deploy"
    scratch = temporary / "scratch"
    test_results = temporary / "test-results"
    packages.mkdir()
    scratch.mkdir()
    test_results.mkdir()

    environment = os.environ.copy()
    environment.update(
        {
            "DOTNET_CLI_HOME": str(temporary / "dotnet-home"),
            "NUGET_PACKAGES": str(packages),
            "NUGET_HTTP_CACHE_PATH": str(temporary / "nuget-http-cache"),
            "NUGET_PLUGINS_CACHE_PATH": str(temporary / "nuget-plugins-cache"),
            "TMPDIR": str(scratch),
            "HATIFECT_DOTNET": str(build_dotnet),
            "HATIFECT_TEST_DOTNET": str(test_dotnet),
            "DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK": "1",
        }
    )

    try:
        members = projection_members(ROOT)
        _copy_projection(ROOT, projection, members)
        for forbidden in ("Hatifect UI", "Hatifect Flow", "Hatifect"):
            if (projection / forbidden).exists():
                raise IsolationError(f"forbidden source tree entered the projection: {forbidden}")

        pack_environment = environment.copy()
        # UI production is outside the consumer projection. Reuse the machine's normal external
        # dependency cache for packing; only the source-absent consumer must prove a fresh cache.
        pack_environment.pop("NUGET_PACKAGES", None)
        pack_environment["DOTNET_CLI_HOME"] = str(Path.home())
        pack_environment.pop("NUGET_HTTP_CACHE_PATH", None)
        pack_environment.pop("NUGET_PLUGINS_CACHE_PATH", None)
        _run(
            [str(ROOT / "tools/hatifect-pack-ui"), str(feed)],
            cwd=ROOT,
            environment=pack_environment,
        )
        package_count = sum(path.is_file() for path in feed.glob("*.nupkg"))
        _prepare_external_feed(external_feed)
        properties = msbuild_properties(feed, packages)
        property_arguments = _property_arguments(properties)
        product = projection / CA_PROJECT
        tests = projection / CA_TEST_PROJECT

        _run(
            [
                str(build_dotnet),
                "restore",
                str(product),
                "--verbosity",
                "minimal",
                "--disable-parallel",
                "-m:1",
                "-p:NuGetAudit=false",
                "--source",
                str(feed),
                "--source",
                str(external_feed),
                *property_arguments,
            ],
            cwd=projection,
            environment=environment,
        )
        _run(
            [
                str(build_dotnet),
                "build",
                str(product),
                "-c",
                "Release",
                "--no-restore",
                "--disable-build-servers",
                "--verbosity",
                "minimal",
                "-warnaserror",
                "-p:UseSharedCompilation=false",
                "-nodeReuse:false",
                "-m:1",
                "-p:HatifectBuildSuite=false",
                "-p:HatifectDeploySuite=false",
                "-p:NuGetAudit=false",
                *property_arguments,
            ],
            cwd=projection,
            environment=environment,
        )
        _run(
            [
                str(test_dotnet),
                "restore",
                str(tests),
                "--verbosity",
                "minimal",
                "--disable-parallel",
                "-m:1",
                "-p:NuGetAudit=false",
                "--source",
                str(feed),
                "--source",
                str(external_feed),
                *property_arguments,
            ],
            cwd=projection,
            environment=environment,
        )
        _run(
            [
                str(test_dotnet),
                "test",
                str(tests),
                "--logger",
                "trx;LogFileName=tests.trx",
                "--results-directory",
                str(test_results),
                "-c",
                "Release",
                "--no-restore",
                "--disable-build-servers",
                "--verbosity",
                "minimal",
                "-warnaserror",
                "-p:UseSharedCompilation=false",
                "-nodeReuse:false",
                "-m:1",
                "-p:HatifectBuildSuite=false",
                "-p:HatifectDeploySuite=false",
                "-p:NuGetAudit=false",
                *property_arguments,
            ],
            cwd=projection,
            environment=environment,
        )
        try:
            test_counts = read_trx(test_results / "tests.trx")
        except ValidationError as error:
            raise IsolationError(f"isolated test evidence rejected: {error}") from error

        output = product.parent / "bin/Release/net6.0"
        validate_ca_output(output)
        _run(
            [
                str(build_dotnet),
                "msbuild",
                str(product),
                "-t:DeployHatifectModule",
                "-p:Configuration=Release",
                "-p:HatifectBuildSuite=false",
                "-p:HatifectDeploySuite=false",
                f"-p:HatifectDeployRoot={deploy_root}",
                "-p:NuGetAudit=false",
                *property_arguments,
            ],
            cwd=projection,
            environment=environment,
        )
        deployed_module = deploy_root / CA_MODULE
        validate_ca_output(deployed_module)
        if not (deployed_module / "manifest.json").is_file():
            raise IsolationError("deployed CA module is missing manifest.json")

        scan_roots = (output, deployed_module, feed)
        path_findings = {
            root.name: scan_absolute_paths(root)
            for root in scan_roots
            if root.is_dir()
        }
        leaking = {name: paths for name, paths in path_findings.items() if paths}
        if leaking:
            raise IsolationError(f"machine-absolute paths leaked into isolated evidence: {leaking}")

        print(
            "Hatifect UI + CA isolated boundary: PASS "
            f"({len(members)} projected files, {package_count} UI packages, "
            f"{test_counts['passed']} tests passed, {len(EXPECTED_CA_DLLS)} CA DLLs, UI source absent)",
            flush=True,
        )
        return temporary if keep else None
    except Exception:
        if keep:
            print(f"Isolated workspace retained at: {temporary}", file=sys.stderr)
        raise
    finally:
        if not keep:
            shutil.rmtree(temporary, ignore_errors=True)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("run",))
    parser.add_argument("--build-dotnet", type=Path, required=True)
    parser.add_argument("--test-dotnet", type=Path, required=True)
    parser.add_argument("--keep", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        retained = run_isolated(
            args.build_dotnet.resolve(),
            args.test_dotnet.resolve(),
            keep=args.keep,
        )
        if retained is not None:
            print(f"Isolated workspace retained at: {retained}")
        return 0
    except (IsolationError, OSError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
