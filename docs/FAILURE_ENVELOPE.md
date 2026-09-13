# Canonical failure envelope

Status: Phase 1 implemented. This change is outside the numbered product roadmap and does not
start or define later diagnostics phases.

## Decision and ownership

`tools/live-harness/validate.py` owns the additive failure projection because it already owns the
canonical `result.json` schema and is used by preflight, direct runtime, save provisioning and
acceptance finalization. The execution path remains:

`hatifect-smoke` / `hatifect-ui-test` → `hatifect-live-runner` → user-session executor →
direct runtime → TestHarness acceptance report → validator.

`result.json` remains protocol v1 and authoritative for existing consumers. Every new non-PASS
result also writes:

- `failure.json`: bounded machine-readable format v1;
- `failure-summary.txt`: an eight-line first-pass diagnosis;
- existing logs, screenshots, reports and transport diagnostics remain unchanged.

The envelope selects the first non-cleanup failed assertion in canonical assertion order as the
single `ROOT_FAILURE`. Later non-cleanup failures are retained as `CASCADE_SKIPPED` records with
their original FAIL/BLOCKED status and a reference to that root; they are not silently converted
into successful skips. Cleanup and restoration errors are `CLEANUP_FAILURE` records referencing
the root. If cleanup is the only failure, it becomes the single root and changes an otherwise
successful run to BLOCKED, matching the existing fail-closed runtime behavior.

The top level contains `scenario`, `run_id`, `phase`, `timestamp`, `status`, `failure_class`,
`root_failure`, `expected`, `actual`, `message`, `causal_component`, `relevant_artifacts` and
`environment_summary`. Text fields are limited to 2,048 characters, cascade context to 32 records,
cleanup context to 8 records, artifact references to 12 sorted request-relative paths, and the
summary to 8,192 characters. Any overflow of result-backed cascades is represented by one bounded
remainder record while the unchanged `result.json` retains every original assertion. Environment
data is an allowlist of platform/runtime identity values; paths, environment variables and log
bodies are not copied. JSON keys and record order are deterministic for identical evidence and
timestamp.

`tools/live-harness/direct_runtime.py` remains responsible for process, game-option and owned-save
cleanup. It supplies cleanup outcomes to the validator so cleanup still runs and remains visible
without replacing an existing scenario root.

## Compatibility risks

- Existing CLI arguments, exit codes, scenario IDs, `result.json` fields and transport schemas are
  unchanged.
- Standalone `validate-result` continues to accept protocol-v1 results without sidecars. Canonical
  entrypoints use the additive `--require-failure-envelope` validation flag.
- Consumers that assume a runtime directory contains exactly one file will now see two additive
  files after non-PASS runs. Consumers must continue to select artifacts by name/type.
- A cleanup-only failure remains BLOCKED. A cleanup failure after an existing FAIL preserves that
  FAIL and its root, while the new envelope exposes cleanup separately.
- The envelope intentionally classifies only existing deterministic assertion IDs and runtime
  boundaries. It performs no probabilistic inference and reads no unbounded log content.

## Baseline and Phase 1 measurement

Measured on 2026-09-13 in this worktree before and after the change with the repository commands
`./tools/hatifect-smoke runtime.boot` and
`./tools/hatifect-smoke flow.ui.player.input`.

| Measurement | Before | After |
|---|---:|---:|
| `runtime.boot` result | BLOCKED, 0 ms | BLOCKED, 0 ms |
| `flow.ui.player.input` result | BLOCKED, 0 ms | BLOCKED, 0 ms |
| `runtime.boot` artifacts | 1 file, 478 bytes | 3 files, 2,058 bytes |
| `flow.ui.player.input` artifacts | 1 file, 494 bytes | 3 files, 2,090 bytes |
| Raw runtime-log size | 0 bytes | 0 bytes |
| Text needed to identify `runtime.boot` root | one 478-byte/18-line JSON file | first 5 summary lines, 298 bytes |
| Text needed to identify `flow.ui.player.input` root | one 494-byte/18-line JSON file | first 5 summary lines, 306 bytes |

Both runs stopped at `HARNESS-ENV-SMAPI`: this worktree has no `tools/hatifect.env`,
`HATIFECT_SMAPI_PATH` or `.smapi-test` runtime root. Therefore a representative successful
scenario duration, a product assertion-failure duration, raw-log volume, downstream runtime
failure count and the source-file count needed for a product diagnosis are **BLOCKED**, not
estimated. The prerequisites are an executable configured SMAPI path, an isolated runtime root,
a prepared deployment and (for `flow.ui.player.input`) a macOS user session with the required
Accessibility/input permissions.

For the observed preflight blocker, no source file was needed for diagnosis before or after; the
canonical message named the missing configuration. The repository-defined first regression scope
after a harness fix is `./tools/hatifect-test tools`; runtime behavior additionally requires the
specific canonical scenario and configured local prerequisites above.
