# Deterministic capability preflight

Status: Phase 3 implemented. This is an additive live-harness diagnostic contract outside the
numbered product roadmap. Phase 4 targeted reproduction is documented separately in
[Targeted reproduction](TARGETED_REPRODUCTION.md); this contract does not define Phase 5 AI
diagnostic packets or Phase 6 progressive regression.

## Ownership and execution boundary

`tools/live-harness/validate.py` owns the capability vocabulary, manifest validation, requirement
resolution, `preflight.json` schema and the projection of a blocked preflight into canonical
`result.json` protocol v1 and the failure envelope. `tools/live-harness/user_session_runtime.py`
owns environment probes because it is the existing boundary that verifies the current worktree
executor before a request is submitted. `save_provisioning.py` owns the read-only save-fixture
probe. The shell runner only preserves ordering:

`scenario validation → capability preflight → save bootstrap/plan → semantic companion → executor submit → direct runtime → game`

A failed required capability therefore cannot start save provisioning, semantic interaction or the
SMAPI process. The preflight does not launch an executor, stop another executor or acquire the
direct-run lock. It accepts only the existing matching worktree executor in `Ready`.

The artifact-writability check creates one private request-owned temporary file in the selected run
directory, calls `fsync`, and removes the file in `finally`. Other probes are read-only. Required
mods are discovered only below the isolated deployment's `Mods` directory. The save probe validates
an existing versioned fixture without creating save directories or working copies.

## Capability vocabulary

Requirements derived from existing scenario fields are always required. The optional additive
`capabilities` manifest field is used only when an existing field cannot express the need. A
declaration is `{ "id": <known-id>, "required": <boolean> }`; unknown fields, unknown IDs,
duplicates, invalid booleans and attempts to downgrade a derived requirement are rejected.
Aggregate scenarios union child requirements, deduplicate them and order them by the fixed
vocabulary below. Required wins over optional.

| Capability | Requirement source | Owner / probe layer |
|---|---|---|
| `artifact-writable` | every scenario | live harness / request artifact |
| `request-parameters` | every scenario | live harness / request validation |
| `smapi-runtime` | every scenario | runtime environment / user-session preflight |
| `isolated-deployment` | every scenario | deployment preparer / isolated deployment |
| `required-mods` | non-empty `requiredMods` | scenario manifest / isolated Mods |
| `isolated-save-fixture` | `requiresSave=true` | save provisioning / isolated save fixture |
| `user-session-executor` | every scenario | user-session runtime / executor state |
| `semantic-workflow` | explicit semantic workflow | semantic test agent / canonical checked-in workflow loader |
| `user-session-gui` | explicit native workflow | user-session runtime / macOS WindowServer |
| `quartz-post-events` | explicit native workflow | native input driver / macOS Quartz |

`flow.ui.player.input` declares the last three requirements because its checked-in semantic workflow
uses physical Quartz input. The GUI probe calls the real WindowServer-facing CoreGraphics API and
the input probe calls `CGPreflightPostEventAccess()` in the current user session. Non-macOS hosts
report these capabilities as unsupported rather than simulating them.

The semantic-workflow probe calls the same canonical loader used again by the semantic companion.
It accepts only the exact scenario-named regular file below `semantic-tests/`, rejects symlinks and
files above 256 KiB, parses JSON, checks scenario identity and validates the complete workflow
schema, operations and selectors. Validation also models the three built-in variables and processes
steps in execution order: `capture`/`discover` names are bounded to 96 characters, the shared
128-name budget counts unique names, and every recursively resolved `${name}` reference must name
an initial or previously defined variable. This preflight load is read-only and does not construct
the Quartz backend, post GUI events or start a process. Runtime boundary validation remains
mandatory so source or workflow changes after preflight still fail closed.

## `preflight.json` format v1

The request-owned report contains exactly:

- `formatVersion`, `scenario`, `runId`, `status`, `createdAtUtc`;
- fixed counts for total, available, required-unavailable and optional-unavailable reports;
- an ordered `capabilities` array with `id`, `requirement`, `status`, `classification`,
  `reasonCode`, `explanation`, `owner`, `probeLayer` and `observedAtUtc`.

Top-level status is `PASS` or `BLOCKED`. Capability status is `available`, `missing`,
`unsupported` or `error`; classification is `available`, `environment-failure`,
`unsupported-capability` or `misconfiguration`. Reason codes use a fixed allowlist. Timestamps
must contain a timezone. The report is limited to 16 capability records, 512 characters per
explanation and 64 KiB serialized UTF-8. It contains no environment dump, absolute user path, raw
log, stack trace or arbitrary object. JSON uses sorted keys, stable capability order, indentation
and literal Unicode, so identical evidence and timestamps serialize identically.

An unavailable optional capability remains in the report and execution continues. Any unavailable
required capability blocks. Multiple required failures become one first failure-envelope root and
stable `ADDITIONAL_FAILURE` records; no cascade relationship is invented.

## Failure and semantic-event integration

When preflight blocks, the validator writes `preflight.json`, canonical `result.json` protocol v1,
`failure.json` format v3 and `failure-summary.txt`. `preflight.json` is a relevant artifact, the
failure phase is `preflight`, and the first unavailable required capability determines the root
classification. Every unavailable required capability, including each `ADDITIONAL_FAILURE`,
retains its typed preflight phase, classification-derived failure class, owner, capability ID,
reason code, expected state and actual observation from the validated report. Classification never
depends on parsing explanatory text.

The existing event vocabulary is unchanged. A blocked run records exactly one `Preflight.Failed`;
a request admitted to direct runtime records exactly one `Preflight.Completed`. The event payload
is bounded scalar data, and the failure envelope receives the normal bounded semantic tail.

## Compatibility and limits

Existing command names, scenario IDs, `result.json` protocol v1, direct transport protocol v2,
user-session protocol v1, raw logs and acceptance-report schemas are unchanged. The manifest gains
only the optional additive `capabilities` field, and successful or blocked runs gain
`preflight.json`. Existing failure sidecars remain authoritative projections of `result.json`.

Preflight proves only availability at its named owner boundary. It does not prove that a later game
operation succeeds, that a GUI permission remains unchanged after the probe, or that a third-party
mod behaves correctly after load. Those outcomes remain owned by the existing scenario and runtime
acceptance.
