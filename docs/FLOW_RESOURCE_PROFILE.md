# Flowline resource profile — F20

Status: **DONE for the bounded ordinary-chest single-player provider**. F20-a diagnostics and this F20-b profile establish the existing retention limits, overflow behavior and actual runtime costs. This is not acceptance of the future F18/F19 UI or unlimited save throughput. Publication is recorded in [ROADMAP-STATUS.md](ROADMAP-STATUS.md).

## Reproduction and measurement boundary

Use the canonical executor and an isolated deployment as described in [RUNTIME.md](RUNTIME.md), then run `./tools/hatifect-smoke flow.chest.resources`. The manifest defines the workload; a request cannot change its scales, checks or budgets. The scenario provisions one owned golden-save copy and never modifies the golden source, ordinary Mods or real saves. The game must use its ordinary GC configuration.

The report binds the request UUID, Flow DLL fingerprint, world, seven distinct session IDs,32 fixed station roles, owning thread, timer frequency and runtime/OS metadata. It measures one existing production operation at each boundary:

- `PreviewRoute`: elapsed time and current-thread allocation for each of75 queries. Register2,16,32 stations, compare cold/cache hit, churn65 distinct routes beyond the64-entry cache, then prove eviction and independent/dependent topology invalidation. All topology changes precede admission.
- `FlowGameSession.Tick`: one actual dispatch call per observed frame, including empty ticks between due work. Each wave reports percentiles, allocations and the full start/end counters. Observation, fixture validation and logging occur outside the measured interval.
- `BeginSave`: the whole checkpoint capture, validation and encoding operation. This is not an isolated codec microbenchmark. `WriteSaveData` is measured separately; neither interval claims to include all of Stardew's save I/O.
- `ReadSaveData` and construction of `FlowGameSession`: separate intervals, including the constructor's validation, restore, port attachment and application projection. Every restore must produce zero physical effects, route searches and extra checkpoint captures.

Cold routing and the first save/load measurements can include JIT and initialization; individual samples do not establish machine-independent timing bounds.

The initial session is clean. Queues32,128,256 are each confirmed through a real Saving/Saved pair, title cleanup and reload while dispatch is paused. The32 stations consist of eight sources, one completely full destination and23 routing chests. Each source contains32 distinct one-unit wine items; source0 also retains an unadmitted overflow item across every save. All40 links are fixed before the first reservation.

After load4, the256 queued parcels drain through ordinary production ticks. The full destination rejects each attempt. Actual saves/reloads after attempts1,8,16 retain256,2048,4096 destination receipts. Total workload: **4608 processed operations**, **4352 physical inventory calls**, **4352 issued/retired capabilities**, with a maximum of64 operations per tick. Source ports together retain256 extraction receipts. The scenario compares full item XML for every source, destination filler and serialized cargo payload at save/load boundaries.

After load7, direct and typed sends reject cargo257 without tagging or removing the saved overflow item. Retry and ReturnToSource reject attempt17 for each256 parcels with `RetryLimit`, without changing command revision, queue, capabilities or receipts. A120-frame warm-up and600 measured idle ticks must keep the cached snapshot, retained resources and saved tree unchanged; measured allocation is0 bytes, p95≤0.25ms and p99≤1ms. These budgets cover the Flow tick only.

## Existing resource limits

| Owner | Bound | Retention behavior |
| --- | ---: | --- |
| Game session | 32 stations | No automatic removal |
| Network | 128 lifetime links | Removing a link does not restore lifetime capacity |
| Game session/Core | 256 cargo, shipments and parcels | Delivery, cancellation, item collection and reload retain history |
| Dispatch queue | 256 pending operations | Admission refuses additional pending work |
| Dispatch | 64 operations per tick | Backlog continues on following ticks |
| Routing | 32 visited stations; 64 cached plans | Cache eviction is bounded and dependency revisions invalidate plans |
| Parcel | 16 delivery/return attempts combined | Attempt17 refuses work |
| Authority | 4352 issued capabilities | `256 × (1 + 16)`; retired capabilities remain recorded |
| Port journal | 4096 receipts; 1024 custody entries | Host cargo256 bounds reachable custody more tightly |
| Event history | 256 entries | Bounded retained history |
| Core checkpoint | 64MiB encoded bytes | Separate from item payload XML and game-save JSON overhead |
| Item payloads | 256 × 65,536 characters | Bounded serialization; actual character usage is reported separately |

`hatifect_flow diagnostics` reads these owners and shows remaining capacity. It does not capture a checkpoint, search routes, acquire an inventory lease or perform an effect. Its per-port custody is the transport journal projection; actual chest contents may have changed after delivery. Admission refusal counts belong to the current load.

D06 decision: **retain the bounded limits for this MVP and refuse new admissions when full**. Saturation tests, actual save/load measurements and the final idle window show that this workload reaches a fixed retained size; compaction is not needed to prevent growth beyond these limits. It does not reclaim cargo history or receipts. Any future compaction requires a separately proven recovery horizon and compatible migration; manual journal deletion is unsupported.

## Verification

- `./tools/hatifect-test flow --platform`: **PASS725 Flow +133 Stardew**, `artifacts/validation/run-_7ay09me/summary.json`. The19 new measurement cases reject lost ticks/ownership, incomplete waves and hidden effects/checkpoints/searches. Six retention cases cover32/128/256 parcels with either destination or source journal saturation, known-receipt replay before reload, old-capability rejection after reload and byte-identical checkpoint restoration.
- `./tools/hatifect-test tools`: **PASS347 Python**, `artifacts/validation/run-8py78xl8/summary.json`. The resource fixtures cover missing checks, foreign requests, changed lifecycle/workload, journal growth, route dependency results and malformed numeric measurements. Fixtures are synthetic validation data, not performance evidence.
- Initial new test run `run-voa405c6` was **FAIL6** because it incorrectly expected a pre-reload capability object to remain valid. The corrected tests require rejection of that retired session authority; production recovery was not changed.
- Independent C# review found no actionable defect and read both final scoped TRX files. The subsequent actual runtime gate below confirms the pause/title/SaveGameMenu lifecycle.

- Full `env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform`: **PASS1507 .NET +348 Python**, `artifacts/validation/run-qb_fc3jg/summary.json`; all tests executed, zero failures/skips. The command-local GC setting is the existing SDK/Rosetta build workaround and is absent from the game process.
- Independent Python review found three coordinated false-PASS variants: hidden work in queued saves, detached route counters and receipt reassignment to another station. All are now rejected by explicit zero-work/clock checks, searches72/3/4 and cache64/3/4 linked to the measured routing profile, and exact station-role ownership. New regression cases and independent replay closed all three findings.
- Preliminary actual run `b120e372-200f-4e16-8154-19bdf1ca7690` completed seven loads/six saves/six titles and the full workload. Its original validator returned PASS12. It predates mandatory station-role IDs and is deliberately rejected by the final validator; it is lifecycle evidence, not the final runtime gate.

- Final `env DOTNET_gcConcurrent=0 ./tools/hatifect-check`: **PASS1269 .NET +348 Python**, `artifacts/validation/run-t8vqdf3n/summary.json`; zero failures/skips. All18 compiled/tooling files match their pre-gate SHA-256 values.

## Actual final candidate

Evidence belongs to `${HOME}/Developer/Hatifect`. The actual game used .NET6.0.32 X64 on macOS27.0, Stardew1.6.15 build24356 and SMAPI4.5.2, with ordinary workstation GC and no build GC override. No build ran alongside either final game measurement. This is an observed machine/workload profile; other machine load and first-call initialization are not controlled benchmark variables.

- Resource request **`f68d1e6b-4737-4d6d-980c-3f3146f476c1` — PASS12**, process43885 exited0 after42.180s. Seven loads, six confirmed saves and six title closures completed. All75 route rows, queue scales32/128/256,16 work waves, exact physical XML, two cargo refusals and256 Retry plus256 Return refusals passed the final strict validator.
- PERF regression **`a488defd-3448-4a8d-93b1-abe2e0831a5c` — PASS9** on the same deployed DLLs. Paused600 samples: p95/p99 **0.003250/0.003541ms**; idle600: **0.004375/0.005625ms**, both0 allocated bytes. The80-parcel queue completed240 operations/160 physical calls with max64 per tick.
- Both original host reports bind Flow fingerprint **`b954c613428089e5cb84156dabd8742931997d99729bea15d6062ff86c52e44b`**. Source/report hashes, original process and SMAPI logs, metric extraction and golden validation are indexed in `artifacts/flowline-f20-resource-profile/`; original reports remain under their `artifacts/runtime/<request>/` directories.
- The exact deployed archive has21 files and14 DLLs; SHA-256 **`3d81d338cd32b82ce70cbff1eff809ffe5841558eb39506b67be87858bab48d4`**. Every archive entry matches the tested isolated deployment. The generated acceptance report was removed from the deployment only after its bytes matched the retained raw report. Golden fixture validation passed after both runs. This archive is evidence for F20, not a claim of final Q03 release readiness.

| Retained cargo / settled attempts | Destination receipts | Core bytes | Capture ms | WriteSaveData ms | ReadSaveData ms | Construct/restore ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 / 0 | 0 | 61,384 | 95.721 | 8.992 | 9.459 | 33.628 |
| 128 / 0 | 0 | 204,517 | 9.071 | 1.748 | 2.612 | 21.481 |
| 256 / 0 | 0 | 395,361 | 14.379 | 1.864 | 3.145 | 36.040 |
| 256 / 1 | 256 | 533,344 | 28.149 | 1.739 | 3.215 | 45.372 |
| 256 / 8 | 2,048 | 1,131,104 | 39.055 | 3.136 | 6.780 | 65.976 |
| 256 / 16 | 4,096 | 1,816,928 | 65.037 | 3.599 | 7.250 | 88.332 |

These are single operation observations, including the colder initial32-cargo capture; they do not imply monotonic per-scale timing. Final capture allocated29,894,400 bytes and construction/restore33,445,520 bytes. The406,572 retained XML characters are separate from the Core image. Save/read/restore are bounded cold operations and remain allocation-heavy at the limit.

The initial256-parcel wave took12 active ticks,768 operations and512 physical calls; maximum measured tick was24.735334ms with at most7,124,848 allocated bytes per tick. Each subsequent retry wave took4 ticks; the largest retry tick was5.398ms. These active costs include item serialization and host snapshot publication and can cause frame spikes; the zero-allocation claim applies only to steady idle/paused ticks. The resource idle window itself measured p95/p99 **0.003583/0.005875ms**, max0.013167ms and0 allocated bytes. At the final reload all4352 capabilities were retired,4352 receipts retained across the fixed stations,256 cargo remained in parcel custody, and further admission/attempts did not increase these counts.

This evidence supports D06's bounded-first choice, with the explicit lifetime limit of256 cargo per save. It does not demonstrate a 60fps active-work guarantee or indefinite new admissions. Player-facing presentation of limits remains in the dependent F18/F19 UI work. No public API, persistence format or dependency is changed. Existing tests and acceptance budgets are not weakened; UI package-boundary and visual checks are NOT_APPLICABLE to the private scenario delegation.
