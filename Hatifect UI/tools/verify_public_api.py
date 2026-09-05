#!/usr/bin/env python3
"""Verify the frozen semantic surface contract consumed by Hatifect UI integrations."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SEMANTIC_SURFACE_TYPES = (
    'UiSemanticSurfaceOptions',
    'IUiSemanticSurfaceSession',
    'IUiSemanticSurfaceApi',
    'UiSemanticSurfaceCancelInput',
    'IUiAutomatedAcceptanceContext',
    'UiAutomatedAcceptanceScenario',
    'IUiSemanticSurfaceAutomation',
)


def _project_version(project: Path) -> str:
    tree = ET.parse(project)
    values = [node.text.strip() for node in tree.getroot().iter('Version') if node.text and node.text.strip()]
    if len(values) != 1:
        raise ValueError(f'{project.name} must declare one Version; found {values!r}')
    return values[0]


def make_baseline(root: Path = ROOT) -> dict:
    experience = root / 'Hatifect.UI.Experience'
    relative = 'Hosting/UiSemanticSurfaceContracts.cs'
    return {
        'FormatVersion': 1,
        'SemanticSurface': {
            'Assembly': 'Hatifect.UI.Experience',
            'ContractVersion': _project_version(experience / 'Hatifect.UI.Experience.csproj'),
            'ApiVersion': 1,
            'Policy': 'consumer-source-freeze',
            'PublicTypes': list(SEMANTIC_SURFACE_TYPES),
            'Files': [{
                'Path': relative,
                'Sha256': hashlib.sha256((experience / relative).read_bytes()).hexdigest(),
            }],
        },
    }


def _scrub_comments(text: str) -> str:
    return re.sub(r'//.*?$|/\*.*?\*/', '', text, flags=re.M | re.S)


def verify(root: Path = ROOT) -> list[str]:
    try:
        baseline = json.loads((root / 'PUBLIC_API_BASELINE.json').read_text(encoding='utf-8'))
        if not isinstance(baseline, dict):
            return ['PUBLIC_API_BASELINE.json must contain an object']
        errors: list[str] = []
        if type(baseline.get('FormatVersion')) is not int or baseline['FormatVersion'] != 1:
            errors.append('PUBLIC_API_BASELINE.json: FormatVersion must be 1')
        semantic = baseline.get('SemanticSurface')
        if not isinstance(semantic, dict) or type(semantic.get('ApiVersion')) is not int or semantic['ApiVersion'] != 1:
            errors.append('PUBLIC_API_BASELINE.json: SemanticSurface must declare API v1')
        if baseline != make_baseline(root):
            errors.append('public API baseline: Hatifect.UI.Experience semantic surface changed and requires explicit review')

        source = _scrub_comments(
            (root / 'Hatifect.UI.Experience/Hosting/UiSemanticSurfaceContracts.cs').read_text(encoding='utf-8')
        )
        for name in SEMANTIC_SURFACE_TYPES:
            if not re.search(rf'\bpublic\s+(?:sealed\s+)?(?:record|class|interface|enum)\s+{re.escape(name)}\b', source):
                errors.append(f'public API baseline: missing semantic public type {name}')
        implementation = _scrub_comments(
            (root / 'Hatifect.UI.Stardew/Hosting/UiSemanticSurfaceService.cs').read_text(encoding='utf-8')
        )
        if not re.search(r'\bpublic\s+int\s+ApiVersion\s*=>\s*1\s*;', implementation):
            errors.append('public API baseline: semantic surface implementation must provide API v1')
        return errors
    except (OSError, ValueError, TypeError, ET.ParseError) as exc:
        return [str(exc)]


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == '--write-baseline':
        baseline = ROOT / 'PUBLIC_API_BASELINE.json'
        baseline.write_text(json.dumps(make_baseline(), indent=2) + '\n', encoding='utf-8')
        print(f'wrote {baseline}')
        return 0
    if len(sys.argv) != 1:
        print('usage: verify_public_api.py [--write-baseline]', file=sys.stderr)
        return 2
    errors = verify()
    if errors:
        print('Hatifect UI public API baseline FAILED:', file=sys.stderr)
        for error in errors:
            print('- ' + error, file=sys.stderr)
        return 1
    print(f'Hatifect UI public API baseline OK: API v1, {len(SEMANTIC_SURFACE_TYPES)} semantic consumer types')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
