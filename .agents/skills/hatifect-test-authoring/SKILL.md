---
name: hatifect-test-authoring
description: Author focused behavior tests in Hatifect's existing .NET xUnit and Python unittest suites, mapping changed contracts to owners, consumers, meaningful assertions, isolated fixtures, and trustworthy evidence.
---

# Hatifect test authoring

Read root `AGENTS.md`, `docs/DEVELOPMENT.md`, `tools/AGENTS.md` and the affected
local instructions. Inventory the production project, existing tests and direct
and transitive consumers through `Hatifect.slnx`, project references and the
canonical test inventory before adding a case.

Use the framework already owned by the target:

- .NET projects use the existing xUnit packages and local assertion/fixture
  conventions. Do not introduce another test framework.
- `tools/tests/test_*.py` use stdlib Python `unittest`. Preserve discovery and
  import layout; do not introduce pytest.

Test caller-visible behavior rather than implementation shape. Pin the requested
result, state transition, identity, ordering, rollback, status/exception and
side effects. Cover the nearest meaningful success, refusal/error and boundary
partitions; parameterize only one real invariant. Do not add tests for raw count,
Markdown wording, incidental line numbers, private structure or tautological
presence. Mocks and patches model an ownership boundary; they cannot replace the
production behavior being claimed.

Place tests in the project that directly exercises the owning contract, then
include consumers when a public, persisted, package, adapter or lifecycle
boundary changes. For runners and harnesses use minimal temporary solutions,
TRX, manifests and filesystem trees. Assert cleanup, restored bytes/permissions
and absence of leaked processes,
locks or temporary artifacts. Never use normal Mods or real saves, and never
mutate golden assets directly. Runtime tests may consume an immutable golden
save only through the canonical provisioner and a request-owned copy.

Map each requirement to exact test names before declaring completion. Hand the
result to `hatifect-test-selection` for the smallest trustworthy command and
final gate. PASS requires nonzero fresh execution from the same source, no
failed/skipped/expected-failure outcomes and internally consistent evidence.
Unavailable required infrastructure is BLOCKED; a violated assertion or contract
is FAIL. Never weaken an existing test or broaden a suite merely to increase its
count.
