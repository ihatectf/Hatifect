# Targeted reproduction

Status: Phase 4 implemented. This tooling-only slice does not start Phase 5 AI diagnostic packets
or Phase 6 progressive regression.

Phase 4 adds a bounded metadata-only reproduction entry point:

```text
./tools/hatifect-repro <source-run-id> [--from <preflight>]
```

The only executable phase is `preflight`. An unknown phase or a later phase is
rejected before an existing smoke/UI wrapper, executor, or game process can be
started. The source run must be a canonical UUID directory directly below the
current checkout's `artifacts/runtime` directory.

The source is accepted only when all of the following hold:

- `request.json` has the exact direct transport protocol v2 fields and belongs
  to the current repository device, inode and HEAD;
- `result.json` has the existing protocol v1 schema, the source UUID, and
  status `FAIL`;
- `failure.json` is validated by the canonical failure-envelope validator and
  has matching source scenario, run ID, status and result fingerprint;
- request/result/failure files are bounded, owned by the current user,
  non-symlink regular files;
- named scenarios reproduce the same scenario; aggregate `all` requires the
  root assertion to have one resolved `checkOwners` owner.

The original root retains the phase accepted by the strict failure-envelope reader. Newly written
envelopes use `preflight`, `prepare`, `runtime`, `validation`, or `cleanup`; historical v2/v3
artifacts may still contain their documented legacy phase values. `--from preflight` is the
effective starting phase of the new run; it does not reinterpret or discard the original root
phase.

The helper API is intentionally small:

```text
python3 tools/live-harness/reproduction.py select <source-run-id> --field <field>
python3 tools/live-harness/reproduction.py materialize <source-run-id> <target-run-dir> <kind> <scenario> --from preflight
```

`materialize` revalidates a descriptor-stable source snapshot immediately before writing the fresh
target's `reproduction.json`; the seed used by the normal runner is returned from that same
snapshot. Metadata publication is exclusive, so concurrent writers cannot replace accepted
lineage evidence. The metadata has an exact schema v1, deterministic JSON ordering, and
contains only run/scenario/kind/root identity, result fingerprint, repository
HEAD, seed, phase, strategy, fixed checkpoint status/reason and a timestamp.
It contains no absolute path, environment dump, command, log or user save
data. Existing result, failure, direct transport and user-session protocols
are unchanged.

The validated source triplet is checkpoint evidence for selection, not resumable game state.
Hatifect cannot currently prove a save/process checkpoint safe across request IDs, so later
`--from` values and all runtime-state reuse fail closed. The new run always receives a new UUID,
new request-owned save working copy when required, normal capability preflight, and normal
executor/direct-runtime validation. This makes invalid and stale checkpoint evidence unable to
authorize preparation, process launch, or arbitrary commands.

The wrapper forwards the source seed through the existing test-seed channel and
invokes the normal smoke/UI wrapper for the resolved target scenario. The
existing smoke/UI wrapper materializes the metadata into its newly-created
request-owned target before preflight; failure validation and runtime execution
remain owned by the existing pipeline.
