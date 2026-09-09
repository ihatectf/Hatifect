# R01-a: resource thread ownership

Status: **IN_PROGRESS**, bounded resource-affinity correction on `codex/r01-resource-thread`, base `c738937`. Full R01 generation ownership manifest is not complete. Owning source is internal Runtime `UiSemanticTextureCatalog<T>`; public interfaces, Layout/Scene/UiHostRuntimeSession, common generation identity and Flow domain ownership are unchanged.

## Cause and behavior

Session methods document UI-thread resource access, but the returned texture lease called catalog release directly with no thread check. An isolated diagnostic against the existing compiled Runtime and a managed mock IDisposable observed creation on thread1 and resource disposal on worker4, once, without exception. No game or real GPU resource was used; this is evidence of the catalog path, not an actual GPU crash. Raw source/result/audit are retained in the sibling tooling-common worktree under `artifacts/r01/`.

The catalog now captures its creating managed thread. Register/Resolve check it before decoder/fallback/dictionary work; catalog Dispose and lease Release check it before cleanup or state mutation. A rejected worker lease disposal retains its release delegate, allowing the owner to retry successfully. Repeated owner disposal remains safe; existing registration-reference fencing and resource budgets are preserved. Resolve adds one thread-ID read and integer comparison, with no allocation, lock or traversal. This establishes affinity to the creating thread, not an independent proof that arbitrary callers created the catalog on the game’s UI thread.

## Evidence

| Check | Evidence | Result |
|---|---|---|
| Runtime before the guard | `run-te4j3eqm`, `artifacts/r01/red-audit.json` | Expected **FAIL**: four new cases fail, existing528 pass |
| `rtk proxy env ./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'` after guard | `run-o__2988d`, `artifacts/r01/scoped-audit.json` | **PASS 532**, including all four new cases |
| Independent read-only source review | `artifacts/r01/independent-review.json`, Sol/xhigh | **NO_FINDINGS**; no build/game execution by reviewer |
| `rtk proxy env ./tools/hatifect-check` | `run-z_y2tqv1`, `artifacts/r01/c-audit.json`; seven actual TRX | **PASS 1717 .NET +389 Python** |
| `rtk proxy env ./tools/hatifect-check --platform` | `run-gicute3_`, `artifacts/r01/g-audit.json`; ten actual TRX | **PASS 2077 .NET +389 Python** |
| Native game / PERF | this managed affinity slice | **NOT_APPLICABLE**; no new native/performance claim |

The three direct-operation cases assert rejected worker registration, fallback creation and catalog cleanup before effects, preservation of existing registration, and successful idempotent owner cleanup. The lease case asserts no disposal on worker and successful owner retry exactly once. Existing-fallback cleanup is protected by the same source guard before enumeration; the new test does not separately seed a fallback for that cleanup case. No existing tests or thresholds were weakened.

## Common integration

Owner source `bd9b9a9fd25e5418468fe45c63f9d090556d4744` is integrated without conflicts as `d270ba4ac2c53c89eccae04669abec72bff16aa2`. Both code/test postimages match the reviewed owner commit. GQ independently checked the five owner commit postimages and all nineteen raw TRX from owner C/G/scoped RED/GREEN, including the exact four new cases; retained evidence is under `artifacts/f13-root-common/r01a-owner-evidence/`.

The unchanged common candidate passed `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` (`run-wurmrtm5`: **1662 .NET +389 Python +13 build-metadata tests**) and the same command with `--platform` (`run-0rggmh3w`: **2022 .NET +389 Python**). GQ checked all seventeen actual TRX, all four R01 cases in each suite, and all 654 frozen source hashes. The first C attempt `run-_t3x8tjo` remains **FAIL at restore** with NU1301 while fetching NuGet repository signature information; no .NET tests ran in that attempt. The identical command passed with authorized network access, without disabling signature checks.

`rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` passed in `hatifect-ui-ca-isolated.2qijcw_9`: **101 tests, 47 projected files, eight exact UI packages, two CA DLLs, UI source absent**. Package repository commits and payload/producer hashes were verified; 1445 files are retained under `artifacts/f13-root-common/isolated-p-combined-d270ba4-hatifect-ui-ca-isolated.2qijcw_9/`. The C/G/P audits are `c-d270ba4-run-wurmrtm5-audit.json`, `g-d270ba4-run-0rggmh3w-audit.json`, and `p-combined-d270ba4-hatifect-ui-ca-isolated.2qijcw_9-audit.json` in the same evidence root.

This is a local integration checkpoint; publication and exact remote CI are pending. It does not close full R01, the separate Flow Scale75 visual failure, Network result visibility, or physical Q01 input. Next R01 work is the ownership manifest tied to the existing activation/session lifecycle.

## Remaining R01 scope

A full ownership manifest must distinguish generation-owned actions/callbacks/async work, session-owned activation caches/textures and borrowed consumer sources/Flow domain session. Cached activation owners currently have at-most-one disposal attempts, while texture/session teardown supports retry after cleanup failure; the future manifest must preserve or explicitly resolve that difference. Do not create a second execution engine or infer ownership from a semantic reference. UI reload must not recreate Flow domain state. Common generation identity, complete ownership inventory and the broader R01 acceptance remain open.
