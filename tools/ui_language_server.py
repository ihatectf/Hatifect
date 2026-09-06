#!/usr/bin/env python3
"""Launch the editor-owned Hatifect UI stdio server without writing build output to the protocol stream."""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import sys

from validation import BUILD_PROPERTIES, ValidationError, resolve_dotnet

ROOT = Path(__file__).resolve().parents[1]
PROJECT = Path("Hatifect UI/Hatifect.UI.Tooling.Server/Hatifect.UI.Tooling.Server.csproj")


def main(argv: list[str] | None = None, *, root: Path = ROOT) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--build", action="store_true", help="Build the server first; build logs go to stderr.")
    args = parser.parse_args(argv)
    assembly = root / PROJECT.parent / "bin" / args.configuration / "net6.0/Hatifect.UI.Tooling.Server.dll"
    try:
        dotnet = resolve_dotnet(tests=True)  # The net6.0 server needs the same supported SDK/runtime pair.
        if args.build:
            result = subprocess.run(
                [dotnet, "build", str(root / PROJECT), "-c", args.configuration,
                 "--disable-build-servers", "--verbosity", "minimal", "-warnaserror",
                 "-warnNotAsError:NETSDK1138", *BUILD_PROPERTIES],
                cwd=root, stdout=sys.stderr, stderr=sys.stderr, check=False,
            )
            if result.returncode:
                return result.returncode
        if not assembly.is_file():
            print("Server build is missing. Run tools/hatifect-ui-language-server --build "
                  f"--configuration {args.configuration} once, then configure the editor without --build.", file=sys.stderr)
            return 2
        os.execv(dotnet, [dotnet, str(assembly)])
        return 0
    except (OSError, ValidationError) as error:
        print(f"Hatifect UI language server: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
