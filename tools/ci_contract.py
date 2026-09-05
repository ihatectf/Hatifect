#!/usr/bin/env python3
"""Generate CI from the current solution and reject incomplete required job results."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

from test_inventory import DEFAULT_SOLUTION, Inventory, InventoryError

REQUIRED_JOBS = ("architecture", "build", "tests")


class CiContractError(ValueError):
    pass


def test_matrix(solution: Path = DEFAULT_SOLUTION) -> dict:
    inventory = Inventory(solution)
    return {"include": [
        {"id": project.identifier, "project": project.path}
        for project in inventory.select(tests=True)
    ]}


def verify_gate(results: dict[str, str]) -> None:
    if set(results) != set(REQUIRED_JOBS):
        raise CiContractError("required CI job results are missing or unexpected")
    incomplete = {job: outcome for job, outcome in results.items() if outcome != "success"}
    if incomplete:
        raise CiContractError(f"required CI jobs did not succeed: {incomplete}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    matrix = commands.add_parser("matrix")
    matrix.add_argument("--solution", type=Path, default=DEFAULT_SOLUTION)
    gate = commands.add_parser("gate")
    for job in REQUIRED_JOBS:
        gate.add_argument(f"--{job}", required=True)
    args = parser.parse_args()
    if args.command == "matrix":
        print(json.dumps(test_matrix(args.solution), separators=(",", ":")))
    else:
        verify_gate({job: getattr(args, job) for job in REQUIRED_JOBS})
        print("RESULT: PASS — all required CI jobs succeeded")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (InventoryError, CiContractError) as error:
        print(f"RESULT: FAIL — {error}", file=sys.stderr)
        raise SystemExit(1)
