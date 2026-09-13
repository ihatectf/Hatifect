# Build source identity in linked worktrees

Status: **IN_PROGRESS** for common integration/publication. The recovery hook
passed independent review, 13 SDK regressions, a real loose/packed linked-worktree
build comparison, full G and isolated P. A separate unstable UI allocation test
failed C; its unchanged UI recheck and G passed. The original failure remains
recorded and is being investigated by the UI owner. Fresh common runtime identity
and publication remain required. The reviewed implementation is local commit
`ff87f5232ae7a342a0942f104dca325919a99b5a`; it has not been pushed.

## Failure and ownership

The accepted U03 source checkpoint `9b6028c0561c5e1a8225bcdec7f1bcce64d81ead`
has retained producer/package/runtime evidence in [U03 acceptance](U03_ACCEPTANCE.md).
Later aggregate request `bcbf231d-4f30-46d6-8767-eb05ab1ab0ea` passed 28 behavioral
checks and exited 0, but preparation `run-k4n72nfn` changed all eight UI DLL hashes.
The recorded HEAD and all 637 source hashes were unchanged. The new binaries lost
their informational-version revision suffix and their embedded PDB path changed
from `/_/Hatifect UI/...` to `/_/Hatifect/Hatifect UI/...`. They matched their new producer
outputs, but differed from the accepted P `c5vynfny` packages. Package identity
therefore remains **FAIL** for this aggregate.

An isolated fixture under SDK 8.0.424 reproduced the cause without building the
product: `InitializeSourceControlInformationFromSourceControlManager` returned
the revision and Git `SourceRoot` with a loose branch ref; after packing that ref,
the same HEAD returned only `_GitRepositoryId` and `ScmRepositoryUrl`. Both MSBuild
queries exited 0. This is incomplete SDK metadata discovery in a linked worktree.
The identity guard detected it correctly.

Repository-wide MSBuild infrastructure owns the fix. There is no UI/Flow/CA
behavior change, public API change, persistence migration, SDK upgrade, new
package dependency or version bump.

## Recovery contract

Root `Directory.Build.targets` runs after the SDK source-control discovery target
and before SourceLink URL translation, assembly metadata and source-path mapping.
It only acts when source-control queries are enabled, the SDK found a repository,
and no Git `SourceRoot` was supplied. A pre-existing NuGet cache root is retained
and does not prevent recovery. The fixed read-only commands
`git rev-parse --absolute-git-dir`, `git rev-parse --verify HEAD` and
`git rev-parse --show-toplevel` execute in the
project directory; project paths are not interpolated into shell commands.

The hook requires the CLI Git directory to match the SDK-discovered directory and
the CLI worktree root to match the checkout containing `Directory.Build.targets`.
This rejects inherited `GIT_DIR`/`GIT_WORK_TREE` that select another repository or
root. Before any CLI query, a nonempty actual process `GIT_COMMON_DIR` is rejected:
it can redirect the ref store even when both paths match. An MSBuild property
override cannot hide that environment value. Normal linked worktrees use the
`commondir` file and remain supported. Both identities and the revision are
validated before publishing metadata. An explicit
`SourceRevisionId` is preserved. As in the SDK, the source-root `RevisionId`
describes the actual HEAD, while an explicit assembly/package revision can differ.
Existing Git roots, healthy SDK output and disabled queries retain their behavior.
A source archive or isolated CA projection with no Git repository does not invoke
Git. The CA projection explicitly includes the new targets file.

A failed recovery query fails the build. Silently proceeding would reproduce the
loss of package provenance that this fix addresses. The hook never updates refs,
the index or configuration. Git must be available when recovery is needed.

## Verification and remaining gates

`tools/build_metadata_tests.py` uses real SDK assembly/NuGet/source-path targets
with controlled discovery results and a fake Git executable in an owned temporary
directory. No real Git process or product build is executed by those tests. The
source-archive case also runs the real SDK repository-discovery task.

| Contract | Executable regression |
|---|---|
| Restore revision, informational version, NuGet commit and deterministic root | `test_missing_sdk_metadata_restores_revision_and_deterministic_source_root` |
| Preserve NuGet roots while restoring the missing Git root | `test_nuget_source_root_does_not_hide_missing_git_metadata` |
| Produce identical compiler outputs for healthy and recovered metadata | `test_healthy_and_recovered_metadata_produce_identical_dll_and_pdb` |
| Keep healthy SDK metadata and avoid extra queries | `test_healthy_sdk_metadata_is_unchanged_without_git_process` |
| Preserve explicit assembly/package revision and existing roots | `test_explicit_revision_is_preserved_while_root_describes_actual_head`, `test_explicit_root_is_preserved_without_git_process` |
| Keep absent repositories and disabled queries inert | `test_no_repository_and_disabled_queries_do_not_invent_metadata`, `test_real_sdk_source_archive_does_not_query_git` |
| Fail both command errors and malformed outputs before publishing metadata | `test_failed_git_query_fails_metadata_initialization`, `test_malformed_revision_fails_before_source_root_is_published`, `test_missing_source_root_fails_without_publishing_revision` |
| Reject foreign Git environment selections | `test_foreign_git_directory_or_worktree_cannot_publish_metadata`, `test_common_directory_override_fails_before_queries_or_metadata` |
| Stop the canonical pipeline before restore/build after a failed regression | `test_failed_metadata_regression_stops_before_package_restore_and_build` in `tools/tests/test_validation.py` |

Healthy and recovered roots also pass real SourceLink generation: tests read the
generated JSON and require the exact `/_/*` mapping to the actual source revision.
The compiler comparison builds the same fixture through both paths and compares
DLL and portable PDB bytes. Its simulated repository has no Git index, so only
that fixture disables untracked-source embedding. Product settings are unchanged;
`actual-untracked-files.json` separately records the real SDK query returning no
tracked Language source files for embedding on the packed worktree.
The canonical build executes these SDK-dependent tests after SDK selection.
`hatifect-test tools` and static CI retain their Python-only dependency contract.
The regression executable rejects zero executed tests, failures and skips.

Artifacts are under `artifacts/alpha47-u03-final-integration/` in the integration
worktree. `packed-refs-repro/result.json` retains the original causal reproduction;
`hook-red.log` retains four failing cases out of seven before the hook existed.
Later regression logs keep their own names and outcomes. The failed aggregate's
36 raw files and 28 producer/game DLL snapshots are separately hash-verified in
`all-identity-failure-retention.json`; they do not replace the accepted U03 evidence.

Initial managed review denied `git status` with “Git полностью управляется
пользователем. Работай только с файлами проекта.” A narrower explicit request for
read-only HEAD/status with `GIT_OPTIONAL_LOCKS=0`, followed by metadata-only MSBuild
queries, was approved. No Git writes or alternate execution route were used.
`actual-hook.json` exposed an additional case: the NuGet cache already populated
`SourceRoot`, preventing the original empty-list guard from recovering metadata.
The corrected Git-root guard restored exact `9b6028c` and the actual worktree root
while preserving the NuGet root; `actual-hook-nuget-root.json` retains that result.
`hook-green-sourcelink-final.log` records **10 passed, 0 failed, 0 skipped**.

The initial recovery candidate passed C `run-0h9kh38m` (1,623 .NET +389 Python +10
metadata regressions) and G `run-m8u9op_3` (1,952 .NET +389 Python +10 metadata
regressions). All 17 TRX and the then-current 641-file manifest were audited.
Independent Sol review subsequently found the foreign-Git-environment P1. Two
new subcases failed before the identity checks and passed afterward. The real
negative metadata query against an existing separate fixture also failed before
publishing a revision (`actual-foreign-repository.log`); the current packed
worktree still recovered exact `9b6028c` (`actual-verified-repository.json`).

Those C/G results retain their pre-correction identity in
`build-metadata-pre-review-source.json`. They are superseded by the executed
post-correction gates below. Final C closure, common runtime identity, publication
and the existing Q01 physical Backspace acceptance remain required. Independent Sol review closed the concrete repository-selection findings after
reading the corrected source and the successful 13-test log. This bounded review
does not replace final C/G/P or runtime acceptance.

C `run-klpsq7n4` subsequently passed 1,623 .NET +389 Python +12 metadata
regressions. Its audited source is retained in
`build-metadata-pre-common-dir-source.json`; it predates the final common-directory
guard. The new common-directory regression failed before the guard while Git
directory/root still matched the SDK/checkout. The first guard execution exposed
an MSBuild condition-quoting error; both failure logs are retained. Final checks
must use the corrected Boolean condition and all 13 regressions.

`common-dir-green-2.log` records **13 passed, 0 failed, 0 skipped** (22.763s).
The common-directory test also supplies an empty MSBuild property override and
requires failure before any Git process or metadata publication.


## Reviewed candidate — final executed gates

The source manifest `build-metadata-candidate-source.json` binds the tested
working-tree candidate to base `9b6028c`; it is not an exact-commit claim.

| Gate | Actual result |
|---|---|
| Real Git/SDK linked worktree | `real-packed-build-audit.json`: PASS. Fourteen commands exit 0 in a new owned temporary repository; the loose path used SDK metadata, the packed path invoked recovery. DLL and portable PDB bytes are identical, both packages contain the same exact commit, and SourceLink maps to that commit. Untracked-source embedding remained enabled. Product refs were not modified. |
| C in sandbox | `run-zumys6eu`: FAIL at restore (`NU1301`, `CookieContainer`, `GetDomainName: -1`); static, 389 Python, 13 metadata regressions and UI packaging passed. |
| C on approved host | `run-k270ugto`: FAIL, one existing Runtime allocation test. Expected 82,160 bytes, actual 80,384 bytes with 128 portals. Build, 755 Flow, 9 DevTools and 133 Planning tests passed; Runtime passed 527/528. |
| Unchanged UI recheck without build | `run-o2yefgu0`: PASS 868, six actual TRX. This does not erase the C failure or prove its cause. |
| G on approved host | `run-qqxxiq7u`: PASS 1,952 .NET +389 Python +13 metadata regressions; ten actual TRX and all 641 source hashes audited. |
| P | `dm1v4_rk`: PASS 101 tests, 47 projected files, eight packages, two CA DLLs. All eight nuspec commits equal `9b6028c`; package DLLs equal current producers. Three restore assets/four cached UI DLLs and deployed CA outputs agree. All 1,445 files retained and hash-checked. |

The temporary fixture, logs, source/package snapshots and real-build audit are in
`packed-refs-repro/`. P evidence is retained in
`isolated-p-build-metadata-hatifect-ui-ca-isolated.dm1v4_rk`; its audit is
`p-build-metadata-hatifect-ui-ca-isolated.dm1v4_rk-audit.json`. The bounded reviewed
patch can be integrated by the UI owner without treating the unrelated C failure
as resolved. The UI owner is investigating the exact allocation comparison; no
assertion or budget was weakened here. Common runtime/package identity must be
re-established after subsequent source integration and publication.
