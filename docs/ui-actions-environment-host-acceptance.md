# Действия и окружение в игровом окне

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Status: **IN_PROGRESS** for the complete «Действия и их завершение» and «Адаптация интерфейса к окружению» roadmap IDs. This bounded implementation combines transactional active asset reload with environment capture and acceptance in the existing Window, Terminal, HUD and active-menu hosts. UI owns Runtime and native host wiring; FLOWLINE owns the independently reviewed environment foundation/capture and subsequent text projection/Parcel consumer. The latter text work is not included here.

Implementation source: [`902d834`](https://github.com/ihatectf/Hatifect/commit/902d83488e501e971ddc24f0fcd852f9068899df). Common alpha45 base `92f48904e766f1c3fac479d84c85a57502d41b75` is integrated in merge `db09ede`, preserving all reviewed production changes and adding six existing GQ diagnostic observations. The immutable alpha46 candidate is [`0cd356f`](https://github.com/ihatectf/Hatifect/commit/0cd356f0fd252a0744fe6a871e46ba631193f876), with all17 version authorities aligned. Its own C/G/P and native reload/environment checks are **PASS**, detailed below. Common integration and publication remain coordinated by GQ; the pre-version evidence keeps its original identity.

## Final alpha46 candidate verification

All commands below ran on unchanged source commit `0cd356f0fd252a0744fe6a871e46ba631193f876`. The48 changed source/test/version hashes relative to published alpha44 `bea00ad` are in `artifacts/u03-u04-alpha46/source-manifest.json`. Documentation-only follow-ups do not change that source attribution.

| Command | Actual result | Evidence under `artifacts/u03-u04-alpha46/` |
|---|---|---|
| `./tools/hatifect-check` | **PASS1528 .NET +366 Python**,7 actual TRX, zero failures/skips | `run-8ihqdzfc`; `c.log`, `c-audit.json` |
| `./tools/hatifect-check --platform` | **PASS1801 .NET +366 Python**,10 actual TRX, zero failures/skips | `run-rk9mu8rd`; `g.log`, `g-audit.json` |
| `./tools/hatifect-isolated-ui-ca --keep` | **PASS90**,44 exact projection files,8 alpha46 package DLLs,3 isolated assets/cache entries,2 deployed CA DLLs; UI source absent | retained `hatifect-ui-ca-isolated.zl7tklqe`; `p.log`, `p-audit.json` |
| `./tools/hatifect-ui-test semantic.actions.reload` | **PASS18**, request `57ee8df6-0147-4417-9a1d-e5bd8376612a` | `native-reload.log`, `native-reload-audit.json` |
| `./tools/hatifect-ui-test semantic.environment` | **PASS25**, request `26d7b6c8-a88c-4df9-a60e-b2e9028e9b26` | `native-environment.log`, `native-environment-audit.json` |

Both native requests report the same alpha46 fingerprint **`a5eed91fe33c86c54c7cfda90d796f4aa40934c6da5edd1760ef606fae06bc06`** and exact checkout. Both processes exit0 with no terminal or teardown errors. All eight package DLLs match the final producer outputs and actual game deployment byte-for-byte; the original bytes of both isolated options files are restored. This additional check is recorded in `native-package-audit.json`.

The fresh reload run confirms all four host owners and all three failed-cleanup/retry paths. Every retired operation is cancelled, read once and has its fault observed, with zero result callbacks; retained delegates emit no notifications or dispatch errors and cleanup retry leaves no handlers. The fresh environment run confirms all25 checks, all eight native owning-thread deliveries and all four automatic environment transitions. Each owner repeats256 unchanged public Synchronize calls with zero allocated bytes, zero source reads and no scene acceptance. First-Show native owner rejection and subsequent retry both pass.

Kepler independently closed source integration/version/scenario review and actual final C/G/P: all48 hashes,17 version-only changes,34 unique scenarios, all17 actual TRX and complete P bytes/cache/deployment evidence. Final independent native audit is **PASS**, with no open findings: actual18/25 outcomes,23 reload artifact hashes,20 environment hashes, both process identities, restored option bytes and an independently recomputed game fingerprint all agree. The native observations retain the Update/Draw attribution and narrow Synchronize allocation limits below. They do not certify total frame performance or product visuals; VISUAL is NOT_APPLICABLE to this bounded functional slice.

## Accepted behavior and ownership

`UiSemanticLiveAssets` prepares assets privately. Its owning host accepts the candidate exactly once within the synchronous preparation scope. Active reload publishes the new scene, invocation, environment and asset metadata before old action cancellation callbacks run. Rejected preparation preserves the last accepted assets and pending actions. Reload of an inactive Terminal section validates that section without replacing the current action generation. Post-acceptance callback failures remain observable.

Window, Terminal, HUD and active-menu surfaces pass one immutable `UiEnvironment` through invocation, planning and composition. It contains the logical viewport, applied scale, input mode, current full locale, selected theme and accessibility preferences with provenance. A change within the same presentation profile still triggers acceptance. An unchanged snapshot is reused. Terminal model identity and pending root/portal work survive ordinary environment recomposition; successful active reload renews the action generation.

Candidate composers and themes remain private until acceptance. Native-owner, configuration, scene-version and Terminal theme-version guards reject stale outer preparation, while preserving an already accepted nested operation. Initial active-menu overlay Show also rechecks its native owner before accepting a prepared environment and installing event handlers; a rejected hidden Show can be retried against the new owner. Retirement is recorded before fallible cleanup, so retained watch and native event delegates cannot act on a closing surface while cleanup is retried.

The change extends internal Runtime/host seams and uses the already introduced environment contract. Existing public opaque surface signatures and consumer-owned semantics remain unchanged. Flow Core/Persistence gain no UI or platform dependency. Legacy raw host factory paths remain available; this is not a claim that every historical caller has migrated to environment capture.

## Source checkpoint and reproducible checks

The pre-version candidate is HEAD `dc3bc5f29ec662b4a5dfeca7dac54bc38dc83935` plus the 21 source/test SHA-256 entries in `artifacts/u04-host-environment/checkpoint-source-manifest.json`. Its unchanged version authorities still say alpha.44. **These results are WIP source validation, not immutable alpha44 release acceptance.** At that stage, the common alpha45 foundation/capture base and aligned alpha46 candidate still required integration and verification; the final section above records those subsequent checks.

Commands run from the UI worktree with `rtk proxy`, explicit `HATIFECT_DOTNET`/`HATIFECT_TEST_DOTNET` pointing to `${HOME}/.dotnet/hatifect-x64-8/dotnet`, and command-local `DOTNET_gcConcurrent=0` for build/test preparation:

| Command | Actual result | Evidence |
|---|---|---|
| `./tools/hatifect-check` | **PASS1528 .NET +366 Python**, seven TRX, zero failures/skips | `run-otekhd4y`; `checkpoint-c.log`, `checkpoint-c-audit.json` |
| `./tools/hatifect-check --platform` | **PASS1801 .NET +366 Python**, ten TRX, zero failures/skips | `run-78seazyj`; `checkpoint-g.log`, `checkpoint-g-audit.json` |
| `./tools/hatifect-isolated-ui-ca --keep` | **PASS90**,44 exact projection files,8 exact producer DLLs,3 isolated package assets,2 exact CA deployment DLLs; UI source absent | retained `hatifect-ui-ca-isolated.kijkn7yj`; `checkpoint-p.log`, `checkpoint-p-audit.json` |
| `./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'` | **PASS478**, including the unchanged four tests that first exposed Terminal theme reentry | `run-1migcsxb`; `terminal-theme-reentry-green.log`, `theme-reentry-green-audit.json` |

The logs and audits above are under `artifacts/u04-host-environment/`. Independent reviewer Kepler verified all21 source hashes, actual C/G counters and individual outcomes, Python execution, all P projection/package/cache/deployment bytes, and the explicit pre-version attribution. No open findings remain in that bounded review.

## Behavioral regressions and native observations

New Runtime tests cover live asset acceptance scope and owner ordering, successful versus rejected reload with pending actions, preservation of nested interaction acceptance, full environment propagation, coherent theme changes and Terminal reentry. The retained test files are `LiveActionReloadTests`, `InteractionPreparationTests`, `TerminalEnvironmentTests`, plus the added live-assets case. Four existing Python lifecycle assertions were updated for the changed call signature and stronger retirement guard without removing their ordering/retirement conditions.

Preserved failures are part of the evidence. Terminal theme reentry initially failed four of478 Runtime tests (`run-ile8rn88`), then the same test-file hash passed all478. The stale outer interaction defect has its own RED/GREEN evidence under `interaction-preparation/`. Native retained-dispatch reload initially failed two of18 checks; the same scenario passed after callback retirement was fixed. Initial environment acceptance failed HUD idle allocation and first-Show native-owner checks; the owner fix and a common warmed measurement call site are separately recorded. A subsequent native delivery timeout was not reported as an allocation pass; the accepted automated background-progress helper made the next complete native run possible.

The canonical executor was reused from this worktree only after `./tools/hatifect-runtime-executor status` reported Ready. The game/executor uses ordinary GC. Runtime data, configuration and deployment remain inside `.smapi-test/isolated`; normal Mods, real saves and golden assets are unchanged.

`./tools/hatifect-ui-test semantic.actions.reload` on the combined pre-version candidate: request **`6d3593a1-ca39-4cba-bec7-0576da8d3502`**, **PASS18**, fingerprint **`e34dd078a37f1e384a776a784affd1ee26e5b9912ab536c7917c1d6318a4514e`**. All four owners preserve rejected reload work and retire successful reload generations. All eight retired operations are cancelled, read exactly once, have observed faults and deliver no result callback. The three failed-cleanup cases replay retained event delegates before cleanup retry: zero notifications, callbacks and dispatch errors; retry leaves zero handlers and exactly one Closed event. Actual report/result, process exit0, empty teardown errors, restored isolated options and all21 unchanged source hashes are recorded in `checkpoint-native-reload-audit.json`.

`./tools/hatifect-ui-test semantic.environment`: request **`f2e780d9-0e73-4a21-9329-3178acbb3a6e`**, **PASS25**, fingerprint **`32f2fcb1cfdbeb317f14b51475542ad4c8599d6d0404a3841c20133b981d56fe`**. This historical candidate precedes the final diagnostic-note correction and the process-start-clock import; its exact18 source hashes remain in `native-environment-automatic-candidate.json`, with actual evidence in `native-environment-automatic-audit.json`. Four owners cover all environment facets, rejected preparation, nested acceptance, unchanged synchronization, native result delivery and automatic environment changes without a consumer Synchronize/Refresh call. Eight results return exactly once on native owner thread1. Each owner performs256 measured unchanged Synchronize calls with zero allocations, zero source reads and no accepted scene-version change. First-Show owner rejection and retry both pass. Process exit0, empty teardown errors and restored isolated options were verified independently.

The automatic Window/Terminal observations can originate from native Update or the existing Draw-time viewport synchronization; they do not prove Update-only environment preparation. HUD/active-menu acceptance uses native Update. The256-call allocation measurement covers unchanged public Synchronize, not total frame cost or the entire Update/Pump path. No full «Адаптация интерфейса к окружению» PERF or product localization/visual acceptance is inferred from it.

## Remaining acceptance and next step

The common alpha45 base and all17 alpha46 versions are integrated; own exact-candidate C/G/P, native reload/environment checks and independent reviews are complete. The unchanged source candidate is ready for common integration/publication by GQ. Preserve historical evidence identities. The next UI-owned slice is «Действия и их завершение» actual A-to-title-to-B save switching with pending root/portal actions, using canonical isolated copies and existing native lifecycle. The required tool ownership is coordinated with GQ; no successor source changes are included in alpha46.

Complete «Действия и их завершение» still requires a concrete typed consumer with observable availability/result/error messages and full save-switch acceptance across the relevant owners. Complete «Адаптация интерфейса к окружению» still requires its remaining planner/text/consumer acceptance and representative PERF evidence; subsequent FLOWLINE text/Parcel changes have their own frozen candidates and follow this host checkpoint. No full «Действия и их завершение»/«Адаптация интерфейса к окружению» DONE, complete «Владение ресурсами интерфейса»–«Одновременное обновление открытых окон» reload transaction, full localization, physical-input or visual certification is claimed here.
