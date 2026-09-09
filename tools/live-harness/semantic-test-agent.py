#!/usr/bin/env python3
"""Stable CLI for the shared semantic model-driven native test engine."""
import importlib.util
from pathlib import Path

_spec = importlib.util.spec_from_file_location("hatifect_semantic_ui_engine", Path(__file__).with_name("semantic_agent_ui.py"))
if _spec is None or _spec.loader is None:
    raise RuntimeError("The checked-in semantic UI engine is unavailable.")
_engine = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_engine)
# Compatibility for existing tooling imports; no monkey-patching of the core module.
for _name in dir(_engine):
    if not _name.startswith("__"):
        globals()[_name] = getattr(_engine, _name)

if __name__ == "__main__":
    raise SystemExit(main())
