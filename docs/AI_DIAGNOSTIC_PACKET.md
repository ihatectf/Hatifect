# Bounded AI diagnostic packet

Status: Phase 5 implemented. Phase 6 [progressive regression selection](PROGRESSIVE_REGRESSION.md)
consumes repository ownership independently; this projection does not change `result.json`
protocol v1, failure-envelope format v3, semantic-event format v1, direct transport v2,
user-session protocol v1 or targeted reproduction.

## Ownership and invocation

`tools/live-harness/diagnostic_packet.py` is the single owner of packet validation, projection,
truncation and publication. The run-ID-only entry point is:

```text
./tools/hatifect-diagnostic-packet <run-id>
```

Generation is explicit rather than automatic. This keeps a supplementary packet failure outside
terminal runtime finalization and guarantees that packet generation cannot change a canonical
PASS/FAIL/BLOCKED outcome. Invoke it only after the existing runner has completed terminal result,
cleanup and semantic-companion finalization. The command does not launch the game, executor,
regression tests, network clients, an LLM or an arbitrary command.

The source must be a canonical UUID directory directly below the current checkout's
`artifacts/runtime`. `request.json` must identify the current repository root by path, device,
inode and HEAD. `result.json`, `failure.json`, `failure-summary.txt`, optional `preflight.json` and
the semantic stream are captured through bounded descriptor-relative reads. Their scenario, run
identity and result fingerprint must agree. A stale or malformed sidecar is rejected.

## Container and contents

The request-owned output is one deterministic JSON container:

```text
diagnostic-packet.json
```

It has logical entries in fixed order:

1. `diagnostic-summary.md` — deterministic root summary, never LLM-generated;
2. `failure.json` — bounded projection of the validated canonical failure envelope;
3. `semantic-tail.json` — stable validated tail counts, events and full-stream reference;
4. `preflight-environment-summary.json` — only the existing typed capability and environment
   allowlists;
5. `reproduction.json` — the exact `./tools/hatifect-repro <run-id>` command for a Phase 4-valid
   FAIL, otherwise a typed unavailable reason;
6. `artifacts.json` — request-relative paths, type, bounded size and projection/exclusion reason;
7. `context-manifest.json` — deterministic repository-relative source/test ownership;
8. `packet-manifest.json` — format, size budget, serialized size, files and truncation counts.

The packet contains no source-file contents. Raw logs, screenshots, dumps, binlogs, TRX, binaries,
save files and oversized artifacts remain in the source run. Their references are retained with a
typed reason such as `RAW_LOG_REFERENCE_ONLY`, `BINARY_ARTIFACT_REFERENCE_ONLY` or
`SIZE_BUDGET_REFERENCE_ONLY`. Missing referenced evidence is represented as
`MISSING_REFERENCED_ARTIFACT`; it is never silently treated as included.

## Context selection

`tools/live-harness/diagnostic-context.json` is the checked-in static ownership source. Selection
combines:

- the validated scenario registration and kind from `scenarios.json`;
- aggregate `all` ownership from resolved `checkOwners`;
- the canonical failure envelope's causal component;
- explicit component and scenario-prefix mappings to primary source, scenario tests, direct
  dependencies and secondary expansion candidates.

Every selected path is repository-relative and must resolve through real current-user directories
to an existing, single-link regular file. Symlinks, hard links, traversal, missing paths, unknown
components and equal-specificity scenario mappings fail closed. Selection does not use source
contents, repository search, fuzzy matching, embeddings or an LLM.

## Determinism and bounds

The hard limit is **131,072 bytes (128 KiB)** over the final serialized container, including its
manifest. JSON uses UTF-8, literal Unicode, sorted keys, two-space indentation and a final newline.
No current timestamp is added. Identical canonical artifacts and repository ownership metadata
produce identical bytes.

If the full projection exceeds the limit, reduction is deterministic:

1. remove secondary context candidates from the end;
2. remove oldest semantic-tail events while retaining the full-stream reference and counts;
3. compact only non-root failure explanatory text to 128 characters;
4. omit non-root additional, cascade and cleanup records in that order while retaining original
   and omitted counts.

Root failure identity, phase, class, component, result fingerprint, reproduction status/command,
primary context ownership and artifact references are never removed. If that mandatory core does
not fit, generation fails closed.

## Publication and failure isolation

The generator validates current-user directory ownership and rejects symlink or hard-link inputs.
It serializes the complete packet before publication, writes a mode `0600` temporary file, calls
`fsync`, and publishes through an exclusive hard link under a private current-user lock. A
concurrent identical request observes the existing byte-identical packet; different or malformed
existing content is never overwritten. No authoritative partial packet name is exposed.

On failure, the CLI leaves canonical artifacts byte-for-byte unchanged and attempts to publish the
bounded `diagnostics/diagnostic-packet-error.json`. That sidecar contains only a typed reason,
bounded message, run identity and `canonicalArtifactsChanged: false`; it is not a replacement
result and cannot hide the original failure.
