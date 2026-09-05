#!/usr/bin/env python3
"""Record canonical Release build evidence and verify exact-runtime Hatifect UI RC readiness."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

import release as release_tool
import test_inventory

ROOT = Path(__file__).resolve().parents[1]
HATIFECT_TOOLS = ROOT / 'Hatifect UI' / 'tools'
sys.path.insert(0, str(HATIFECT_TOOLS))
from runtime_fingerprint import ALGORITHM, compute_runtime_fingerprint  # noqa: E402
from candidate_fingerprint import (  # noqa: E402
    ALGORITHM as CANDIDATE_FINGERPRINT_ALGORITHM,
    compute_candidate_fingerprint,
)

LIVE_HARNESS_VALIDATOR = ROOT / 'tools' / 'live-harness' / 'validate.py'
LIVE_HARNESS_SPEC = importlib.util.spec_from_file_location(
    'hatifect_live_harness_rc', LIVE_HARNESS_VALIDATOR
)
if LIVE_HARNESS_SPEC is None or LIVE_HARNESS_SPEC.loader is None:
    raise RuntimeError('could not load the live-harness validator')
live_harness = importlib.util.module_from_spec(LIVE_HARNESS_SPEC)
LIVE_HARNESS_SPEC.loader.exec_module(live_harness)

EVIDENCE_FORMAT_VERSION = 1
TEST_PROJECTS = list(test_inventory.test_project_paths())
AUTOMATED_PROMOTION_SCENARIOS = (
    'all',
    'semantic.chests-anywhere-overlay.absent',
    'semantic.chests-anywhere-overlay.incompatible',
    'semantic.chests-anywhere-overlay.capture-exception',
    'semantic.chests-anywhere-overlay.return-to-title',
)


def _hatifect_root(package_root: Path) -> Path:
    return package_root.resolve() / 'Hatifect UI'


def _hatifect_version(runtime_root: Path) -> str:
    manifest = json.loads((runtime_root / 'manifest.json').read_text(encoding='utf-8'))
    value = manifest.get('Version')
    if not isinstance(value, str) or not value.strip():
        raise ValueError('packaged Hatifect UI manifest has no Version')
    return value.strip()


def _parse_utc_timestamp(value: object, field_name: str) -> float:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f'{field_name} must be a non-empty UTC timestamp')
    normalized = value.strip()
    if normalized.endswith('Z'):
        normalized = normalized[:-1] + '+00:00'
    parsed = datetime.fromisoformat(normalized)
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise ValueError(f'{field_name} must include a UTC offset')
    if parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        raise ValueError(f'{field_name} must be expressed in UTC')
    return parsed.timestamp()


def _validate_automated_evidence(
    automated: dict[str, object],
    scenario_id: str,
    version: str,
    fingerprint: str,
    build_captured_at: float,
) -> None:
    if automated.get('HatifectVersion') != version:
        raise ValueError(
            f"automated report HatifectVersion {automated.get('HatifectVersion')!r} != packaged {version!r}"
        )
    if automated.get('RuntimeFingerprintAlgorithm') != ALGORITHM:
        raise ValueError('automated report runtime fingerprint algorithm is unsupported')
    if automated.get('RuntimeFingerprint') != fingerprint:
        raise ValueError('automated report does not belong to the exact packaged Hatifect UI runtime')
    scenarios = live_harness.load_manifest()
    resolved = live_harness.resolve_scenario(
        scenarios,
        scenario_id,
        'ui',
    )
    live_harness.validate_report(
        resolved, automated, started_at=build_captured_at
    )


def record_build(package_root: Path, output: Path, tests_passed: bool) -> int:
    contract = release_tool.load_contract()
    release_tool.verify_source(contract)
    release_tool.verify_package(contract, package_root)

    runtime_root = _hatifect_root(package_root)
    evidence = {
        'FormatVersion': EVIDENCE_FORMAT_VERSION,
        'CapturedAtUtc': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
        'HatifectVersion': _hatifect_version(runtime_root),
        'RuntimeFingerprintAlgorithm': ALGORITHM,
        'RuntimeFingerprint': compute_runtime_fingerprint(runtime_root),
        'CandidateFingerprintAlgorithm': CANDIDATE_FINGERPRINT_ALGORITHM,
        'CandidateFingerprint': compute_candidate_fingerprint(package_root),
        'Configuration': 'Release',
        'SuiteBuildPassed': True,
        'TestsPassed': bool(tests_passed),
        'TestProjects': TEST_PROJECTS,
    }
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    print(
        f"RC build evidence written: {output}; tests={'passed' if tests_passed else 'SKIPPED/not eligible for RC'}; "
        f"runtime={evidence['RuntimeFingerprint']}; candidate={evidence['CandidateFingerprint']}"
    )
    return 0


def verify_ready(
    package_root: Path,
    build_evidence_path: Path,
    host_report: Path,
    automated_all_report: Path,
    automated_absent_report: Path,
    automated_incompatible_report: Path,
    automated_capture_exception_report: Path,
    automated_return_to_title_report: Path,
) -> int:
    contract = release_tool.load_contract()
    release_tool.verify_source(contract)
    release_tool.verify_package(contract, package_root)

    runtime_root = _hatifect_root(package_root)
    version = _hatifect_version(runtime_root)
    fingerprint = compute_runtime_fingerprint(runtime_root)
    candidate_fingerprint = compute_candidate_fingerprint(package_root)
    evidence = json.loads(build_evidence_path.read_text(encoding='utf-8'))
    errors: list[str] = []
    build_captured_at = 0.0

    if evidence.get('FormatVersion') != EVIDENCE_FORMAT_VERSION:
        errors.append(f"build evidence FormatVersion {evidence.get('FormatVersion')!r} != {EVIDENCE_FORMAT_VERSION}")
    if evidence.get('HatifectVersion') != version:
        errors.append(f"build evidence HatifectVersion {evidence.get('HatifectVersion')!r} != packaged {version!r}")
    if evidence.get('RuntimeFingerprintAlgorithm') != ALGORITHM:
        errors.append('build evidence runtime fingerprint algorithm is unsupported')
    if evidence.get('RuntimeFingerprint') != fingerprint:
        errors.append('build evidence does not belong to the exact packaged Hatifect UI runtime')
    if evidence.get('CandidateFingerprintAlgorithm') != CANDIDATE_FINGERPRINT_ALGORITHM:
        errors.append('build evidence candidate fingerprint algorithm is unsupported')
    if evidence.get('CandidateFingerprint') != candidate_fingerprint:
        errors.append('build evidence does not belong to the exact three-module candidate')
    if evidence.get('Configuration') != 'Release':
        errors.append('build evidence must come from Configuration=Release')
    if evidence.get('SuiteBuildPassed') is not True:
        errors.append('canonical suite Release build is not recorded as passed')
    if evidence.get('TestsPassed') is not True:
        errors.append('canonical test suite was skipped or is not recorded as passed')
    if evidence.get('TestProjects') != TEST_PROJECTS:
        errors.append('build evidence test-project set does not match the canonical RC gate')
    try:
        build_captured_at = _parse_utc_timestamp(
            evidence.get('CapturedAtUtc'), 'build evidence CapturedAtUtc'
        )
    except (TypeError, ValueError) as error:
        errors.append(str(error))

    if errors:
        print('Hatifect UI RC readiness FAILED:', file=sys.stderr)
        for error in errors:
            print('- ' + error, file=sys.stderr)
        return 1

    verifier = HATIFECT_TOOLS / 'verify_host_acceptance.py'
    result = subprocess.run(
        [sys.executable, str(verifier), str(host_report.resolve()), '--runtime-root', str(runtime_root)],
        cwd=str(ROOT),
        text=True,
    )
    if result.returncode != 0:
        return result.returncode

    automated_reports = (
        automated_all_report,
        automated_absent_report,
        automated_incompatible_report,
        automated_capture_exception_report,
        automated_return_to_title_report,
    )
    for scenario_id, report_path in zip(
        AUTOMATED_PROMOTION_SCENARIOS, automated_reports, strict=True
    ):
        automated = json.loads(report_path.read_text(encoding='utf-8'))
        _validate_automated_evidence(
            automated,
            scenario_id,
            version,
            fingerprint,
            build_captured_at,
        )

    print(
        f'Hatifect UI RC readiness OK for {version}: source/package contract + Release build + canonical tests + '
        'exact-runtime operator and all/absent/incompatible/capture-exception/return-to-title Stardew acceptance match '
        f'UI fingerprint {fingerprint}; candidate fingerprint {candidate_fingerprint}'
    )
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)

    record = sub.add_parser('record-build', help='write evidence after the canonical Release build/package step')
    record.add_argument('package_root', type=Path)
    record.add_argument('output', type=Path)
    record.add_argument('--tests-passed', action='store_true', help='set only when release.sh ran every canonical test project')

    verify = sub.add_parser(
        'verify',
        help='require build/test/package evidence plus exact-runtime operator and automated Stardew evidence',
    )
    verify.add_argument('package_root', type=Path)
    verify.add_argument('build_evidence', type=Path)
    verify.add_argument('host_report', type=Path)
    verify.add_argument('automated_all_report', type=Path)
    verify.add_argument('automated_absent_report', type=Path)
    verify.add_argument('automated_incompatible_report', type=Path)
    verify.add_argument('automated_capture_exception_report', type=Path)
    verify.add_argument('automated_return_to_title_report', type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.command == 'record-build':
        return record_build(args.package_root, args.output, args.tests_passed)
    return verify_ready(
        args.package_root,
        args.build_evidence,
        args.host_report,
        args.automated_all_report,
        args.automated_absent_report,
        args.automated_incompatible_report,
        args.automated_capture_exception_report,
        args.automated_return_to_title_report,
    )


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError, TypeError, json.JSONDecodeError, release_tool.ReleaseError) as exc:
        print(f'Hatifect UI RC readiness FAILED: {exc}', file=sys.stderr)
        raise SystemExit(1)
