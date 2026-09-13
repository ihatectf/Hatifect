#!/usr/bin/env python3
"""Validate real-host evidence against RC budgets and the exact packaged Hatifect UI runtime."""
from __future__ import annotations

import argparse
import json
import pathlib
import sys

from runtime_fingerprint import ALGORITHM, compute_runtime_fingerprint
from verify_runtime_perf import finite_number, index_records, validate_report

ROOT = pathlib.Path(__file__).resolve().parents[1]
BUDGET_PATH = ROOT / 'PERFORMANCE_BUDGETS.json'
REQUIREMENTS_PATH = ROOT / 'HOST_ACCEPTANCE_REQUIREMENTS.json'


def non_empty_string(value) -> bool:
    return isinstance(value, str) and bool(value.strip())


def string_values(value, name: str) -> set[str]:
    if not isinstance(value, list) or any(not non_empty_string(item) for item in value):
        raise ValueError(f'{name} must be an array of non-empty strings')
    return set(value)


def display_metrics_key(value, name: str) -> tuple[int, int, float, float]:
    if not isinstance(value, dict):
        raise ValueError(f'{name}: valid DisplayMetrics evidence is required')
    dimensions = tuple(value.get(field) for field in ('LogicalWidth', 'LogicalHeight'))
    if any(type(dimension) is not int or dimension <= 0 for dimension in dimensions):
        raise ValueError(f'{name}: DisplayMetrics dimensions must be positive integers')
    scales = tuple(finite_number(value.get(field), f'{name}.{field}') for field in ('PixelScaleX', 'PixelScaleY'))
    if any(scale <= 0 for scale in scales):
        raise ValueError(f'{name}: DisplayMetrics pixel scales must be positive')
    return (*dimensions, *scales)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=pathlib.Path, help='host-acceptance-report.json captured by Stardew')
    parser.add_argument(
        '--runtime-root',
        type=pathlib.Path,
        required=True,
        help='exact packaged/deployed Hatifect UI directory whose DLLs/assets must match the report',
    )
    return parser.parse_args()


def validate_host_report(report: dict, budgets: dict, requirements: dict, expected_fingerprint: str) -> list[str]:
    errors = validate_report(report, budgets)
    if report.get('FormatVersion') != requirements.get('FormatVersion'):
        errors.append('report FormatVersion does not match HOST_ACCEPTANCE_REQUIREMENTS.json')
    if report.get('PerformanceFormatVersion') != requirements.get('PerformanceFormatVersion'):
        errors.append('report PerformanceFormatVersion does not match HOST_ACCEPTANCE_REQUIREMENTS.json')

    expected_version = requirements.get('HatifectVersion')
    if report.get('HatifectVersion') != expected_version:
        errors.append(f"HatifectVersion {report.get('HatifectVersion')!r} != required {expected_version!r}")

    expected_algorithm = requirements.get('RuntimeFingerprintAlgorithm')
    if expected_algorithm != ALGORITHM:
        errors.append(f'HOST_ACCEPTANCE_REQUIREMENTS.json RuntimeFingerprintAlgorithm must be {ALGORITHM!r}')
    if report.get('RuntimeFingerprintAlgorithm') != expected_algorithm:
        errors.append(
            f"RuntimeFingerprintAlgorithm {report.get('RuntimeFingerprintAlgorithm')!r} != required {expected_algorithm!r}"
        )
    if report.get('RuntimeFingerprint') != expected_fingerprint:
        errors.append(
            'RuntimeFingerprint does not match the exact Hatifect UI runtime supplied by --runtime-root '
            f"({report.get('RuntimeFingerprint')!r} != {expected_fingerprint!r})"
        )

    for field in ('CapturedAtUtc', 'GameVersion', 'SmapiVersion'):
        if not non_empty_string(report.get(field)):
            errors.append(f'{field} must be present and non-empty')

    scenarios = index_records(report.get('Scenarios', []), 'Scenarios')
    for scenario_id, evidence in requirements.get('ScenarioEvidence', {}).items():
        item = scenarios.get(scenario_id)
        if item is None:
            errors.append(f'missing scenario evidence: {scenario_id}')
            continue
        surface_kinds = string_values(item.get('SurfaceKinds', []), f'{scenario_id}.SurfaceKinds')
        theme_ids = string_values(item.get('ThemeIds', []), f'{scenario_id}.ThemeIds')
        display_metrics = item.get('DisplayMetrics', [])
        required_kinds = set(evidence.get('RequiredSurfaceKinds', []))
        if required_kinds and not required_kinds.issubset(surface_kinds):
            errors.append(f'{scenario_id}: missing required SurfaceKinds {sorted(required_kinds - surface_kinds)}')
        allowed_kinds = set(evidence.get('AllowedSurfaceKinds', []))
        if allowed_kinds and (not surface_kinds or not surface_kinds.issubset(allowed_kinds)):
            errors.append(f'{scenario_id}: SurfaceKinds {sorted(surface_kinds)} must be non-empty and within {sorted(allowed_kinds)}')
        min_themes = int(evidence.get('MinThemeIds', 0))
        if len(theme_ids) < min_themes:
            errors.append(f'{scenario_id}: ThemeIds evidence count {len(theme_ids)} < required {min_themes}')
        min_metrics = int(evidence.get('MinDisplayMetrics', 0))
        if not isinstance(display_metrics, list):
            raise ValueError(f'{scenario_id}.DisplayMetrics must be an array')
        valid_metric_keys = {
            display_metrics_key(metrics, f'{scenario_id}.DisplayMetrics')
            for metrics in display_metrics
        }
        if len(valid_metric_keys) < min_metrics:
            errors.append(f'{scenario_id}: distinct DisplayMetrics evidence count {len(valid_metric_keys)} < required {min_metrics}')

    checks = index_records(report.get('HostChecks', []), 'HostChecks')
    for required in requirements.get('RequiredChecks', []):
        check_id = required.get('Id')
        item = checks.get(check_id)
        if item is None:
            errors.append(f'missing host check: {check_id}')
            continue
        if item.get('Passed') is not True:
            errors.append(f'host check failed/not passed: {check_id}')
        expected_theme = required.get('ThemeId')
        if expected_theme and item.get('ThemeId') != expected_theme:
            errors.append(f"{check_id}: ThemeId {item.get('ThemeId')!r} != required {expected_theme!r}")
        display_metrics_key(item.get('DisplayMetrics'), check_id)

    return errors


def main() -> int:
    args = parse_args()
    try:
        report = json.loads(args.report.read_text(encoding='utf-8'))
        budgets = json.loads(BUDGET_PATH.read_text(encoding='utf-8'))
        requirements = json.loads(REQUIREMENTS_PATH.read_text(encoding='utf-8'))
        fingerprint = compute_runtime_fingerprint(args.runtime_root)
        errors = validate_host_report(report, budgets, requirements, fingerprint)
    except (OSError, ValueError, TypeError, KeyError, OverflowError) as exc:
        errors = [str(exc)]

    if errors:
        print('Hatifect UI real-host acceptance gate FAILED:', file=sys.stderr)
        for error in errors:
            print('- ' + error, file=sys.stderr)
        return 1

    print(
        f"Hatifect UI real-host acceptance gate OK: {len(set(budgets['RequiredScenarios']))} performance scenarios + "
        f"{len(requirements['RequiredChecks'])} host checks for {requirements['HatifectVersion']}; exact runtime fingerprint matched"
    )
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
