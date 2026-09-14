---
name: hatifect-diagnostics
description: Diagnose or change Hatifect live-harness failures, failure envelopes, semantic event streams, and bounded runtime artifacts. Use for non-PASS runs or diagnostic-contract work; not for ordinary product changes without harness evidence.
---

# Hatifect diagnostics

Read root `AGENTS.md`, `tools/AGENTS.md`, `docs/FAILURE_ENVELOPE.md` and
`docs/SEMANTIC_EVENTS.md`. Read `docs/RUNTIME.md` only when the failure involves
executor lifecycle or an actual runtime run.

Start from bounded evidence in this order:

1. `failure-summary.txt` for the first-pass diagnosis;
2. `failure.json` for the root failure, causal records, cleanup failures and
   semantic tail;
3. `result.json` for the authoritative status and assertions;
4. only the referenced raw artifact needed to resolve remaining ambiguity.

Keep these invariants:

- `result.json` remains authoritative; diagnostics never convert or hide its
  PASS/FAIL/BLOCKED outcome.
- Preserve the first causal failure. Cascades, independent failures and cleanup
  failures remain distinct.
- Events contain bounded scalar transitions, not raw logs, stack traces,
  screenshots, environment dumps or arbitrary objects.
- Preserve event ordering, request identity, fixed vocabulary, retention and
  format-version compatibility. Diagnostic publication is best-effort and its
  own failure remains a separate bounded artifact.
- Do not broaden scenario behavior, retry a full suite, or read every log before
  the bounded evidence proves that it is necessary.

The contract owner is `tools/live-harness/validate.py`; direct lifecycle facts
come from `direct_runtime.py`, and semantic-companion finalization belongs to
`tools/hatifect-live-runner`. Test the smallest affected contract first, then use
the repository commands selected by `hatifect-test-selection`.
