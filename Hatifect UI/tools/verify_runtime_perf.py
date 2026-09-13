#!/usr/bin/env python3
"""Validate a real Stardew/SMAPI semantic UI performance report against the RC budgets."""
from __future__ import annotations

import json
import math
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
BUDGET_PATH = ROOT / 'PERFORMANCE_BUDGETS.json'


def finite_number(value, name: str) -> float:
    if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
        raise ValueError(f'{name} must be a finite non-negative number')
    return float(value)


def index_records(value, name: str) -> dict[str, dict]:
    if not isinstance(value, list):
        raise ValueError(f'{name} must be an array')
    indexed: dict[str, dict] = {}
    for item in value:
        if not isinstance(item, dict) or not isinstance(item.get('Id'), str) or not item['Id'].strip():
            raise ValueError(f'{name} entries must be objects with a non-empty Id')
        identifier = item['Id']
        if identifier in indexed:
            raise ValueError(f'{name}: duplicate Id {identifier}')
        indexed[identifier] = item
    return indexed


def validate_report(report: dict, budgets: dict) -> list[str]:
    if not isinstance(report, dict):
        raise ValueError('performance report must be an object')
    performance_format = report.get('PerformanceFormatVersion', report.get('FormatVersion'))
    if type(performance_format) is not int or performance_format != budgets.get('FormatVersion'):
        raise ValueError('performance report PerformanceFormatVersion does not match PERFORMANCE_BUDGETS.json')

    required_frames = budgets['MeasurementFrames']
    if type(required_frames) is not int or required_frames <= 0:
        raise ValueError('MeasurementFrames must be a positive integer')
    required_ids = budgets['RequiredScenarios']
    if not isinstance(required_ids, list) or not required_ids or any(
        not isinstance(value, str) or not value.strip() for value in required_ids
    ) or len(set(required_ids)) != len(required_ids):
        raise ValueError('RequiredScenarios must contain distinct non-empty scenario ids')
    required = set(required_ids)
    scenarios = index_records(report.get('Scenarios', []), 'Scenarios')
    missing = sorted(required - set(scenarios))
    if missing:
        raise ValueError('missing required scenarios: ' + ', '.join(missing))

    limits = budgets['Budgets']
    errors: list[str] = []
    for scenario_id in sorted(required):
        item = scenarios[scenario_id]
        frames = item.get('Frames')
        if type(frames) is not int or frames < 0:
            raise ValueError(f'{scenario_id}.Frames must be a non-negative integer')
        if frames < required_frames:
            errors.append(f'{scenario_id}: Frames {frames} < required {required_frames}')
        checks = (
            ('P95UiThreadMs', 'MaxP95UiThreadMs'),
            ('P99UiThreadMs', 'MaxP99UiThreadMs'),
            ('SteadyStateAllocatedBytesPerFrame', 'MaxSteadyStateAllocatedBytesPerFrame'),
            ('MeasureCacheMissRatio', 'MaxMeasureCacheMissRatio'),
            ('ArrangeCacheMissRatio', 'MaxArrangeCacheMissRatio'),
        )
        for field, limit_name in checks:
            actual = finite_number(item.get(field), f'{scenario_id}.{field}')
            maximum = finite_number(limits[limit_name], limit_name)
            if actual > maximum:
                errors.append(f'{scenario_id}: {field} {actual:g} > budget {maximum:g}')
    return errors


def main() -> int:
    if len(sys.argv) != 2:
        print('usage: verify_runtime_perf.py <runtime-performance-report.json>', file=sys.stderr)
        return 2
    try:
        budgets = json.loads(BUDGET_PATH.read_text(encoding='utf-8'))
        report = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
        errors = validate_report(report, budgets)
    except (OSError, ValueError, TypeError, KeyError, OverflowError) as exc:
        errors = [str(exc)]
    if errors:
        print('Hatifect UI runtime performance gate FAILED:', file=sys.stderr)
        for error in errors:
            print('- ' + error, file=sys.stderr)
        return 1
    print(f"Hatifect UI runtime performance gate OK: {len(budgets['RequiredScenarios'])} scenarios, >= {budgets['MeasurementFrames']} frames each")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
