# U03 — typed actions and async lifecycle

Status: **DONE** for the original U03 criteria in `ROADMAP.md`, accepted on source `9b6028c0561c5e1a8225bcdec7f1bcce64d81ead`. This is a verified implementation checkpoint, not publication of the common candidate or completion of Q01/U05.

Experience owns typed requests, results, availability and concurrency policy. Runtime owns admission, immutable capture, action status, owning-thread delivery and retirement. Stardew binds those rules to native surface lifecycles. CA uses the shared typed bindings for all eight existing actions. Safe localized messages remain separate from diagnostic exceptions; frame and accessibility use the same accepted status snapshot. Surface API v1 and persistence formats remain unchanged. The optional exact-harness action automation facet is protected by the public API baseline.

## Original requirements

| Requirement | Verified implementation and evidence |
|---|---|
| Typed request/result and Available, Running, Completed, Rejected/Failed, Cancelled states | `UiAction`, `UiActionResult`, `ActionExecutionTests` and `ActionBindingTests.Messages`; current Runtime suite passed 528 tests. |
| Concurrency, repeat invoke and CanExecute | RejectWhileRunning, RestartLatest and FIFO Queue with maximum capacity 128; admission/capture, supersede, reentry and recovery tests. Dispatcher registrations are bounded to 4096 actions. |
| User messages and observable exceptions | Safe message snapshots and diagnostic exceptions remain separate; unit checks cover outcome messages and frame/accessibility agreement. Fresh common Pump captured EN/RU Running/Completed, and all eight composed/UI-layer PNGs were viewed. |
| Cancellation preserves committed domain effects | `CancellationDropsQueueButPreservesSuccessfulCommittedDomainResult`, CA retirement tests and native save-switch observations retain both committed effects for every old/new surface. |
| Close/replacement fences late completion | Fresh Pump checks root/portal callbacks, close/reopen, HUD Hide and active-menu replacement. Retired callbacks stay zero; results/faults are observed once. |
| Accepted/rejected reload and cleanup failures | Fresh reload passes 18 checks across window, Terminal, HUD and active-menu. Rejected reload retains the draft/pending owner; accepted reload retires the old generation. Three injected cleanup failures remain fenced and release handlers on retry. |
| Save-switch isolation and owning-thread delivery | Fresh save-switch passes 18 checks: four title transitions, five loads, four surface kinds, eight fresh callbacks on owner thread 1, zero retired callbacks/handlers. |
| Subscription cleanup | Publication handlers are removed before retirement/cancellation delivery; native close/reload/save-switch observations confirm final handlers are zero. |
| Concrete consumer uses typed actions | Fresh CA passes five checks and records all eight typed actions through normalized host Tab/Enter, including native handoff, Close, refused retired input and fresh reopen. |

An independent read-only Sol reviewer (`u03_closure_audit`) checked the original requirements against code, assertions and retained results. It reported every original requirement SATISFIED, no missing/contradicted requirement and no actionable finding. It did not run builds/tests/game or edit source. Fresh common Pump and CA removed the need to rely only on earlier owner-native evidence from `3229889`.

## Executed common evidence

Artifact root: `artifacts/alpha47-u03-final-integration/` in the integration worktree. `common-source-9b6028c.json` binds 637 tracked files to the accepted source. Historical owner and earlier common results retain their original identities in [U03_ACTION_MESSAGES.md](U03_ACTION_MESSAGES.md).

| Gate | Actual result |
|---|---|
| C | `run-m50ibs61`: 1,623 .NET + 388 Python, zero failures/skips; seven TRX audited. |
| G | `run-lgeoufuh`: 1,952 .NET + 388 Python, zero failures/skips; ten TRX audited. |
| U | Current Runtime 528, Planning 133, Semantics 60, DevTools 9, Tooling 128, Tooling Server 10; game-linked UI 68 in G. |
| P | `c5vynfny`: 101 CA tests, 46 projected files, eight exact alpha.47 packages, three restore assets and four resolved UI cache DLLs; two deployed CA DLLs, no UI source/runtime copies in the CA module. All 1,444 files retained. |
| Save-switch | `02565027-d1ae-4657-9693-c1872318761c`: PASS 18. |
| Reload | `9fb55869-dbe5-4a63-a56b-c95015a89558`: PASS 18. |
| Pump/messages | `bc091566-553f-4cd1-8cfa-ff8350019da7`: PASS 4, 114 ticks, ten detailed probes; eight message PNGs viewed without findings. |
| Typed CA | `1c110f69-5292-4655-a1f4-1391c1209155`: PASS 5, eight distinct typed action admissions. |

All four native requests exited 0 with no teardown errors and share UI fingerprint `5fc2c2111b8357967c3c2be5343b12feaa0cad4279033d5fe58a13eaa2471480`. At execution/audit, all eight game UI DLLs matched the retained P packages and producer snapshots. Fourteen runtime DLLs were retained separately. Original isolated options bytes/modes and golden files remained intact; request-owned save copies were removed. Raw native directories and hashes are retained under `native-9b6028c/`; audit names include `save-switch-9b6028c-audit.json`, `reload-9b6028c-audit.json`, `semantic.actions.pump-9b6028c-audit.json` and `semantic.chests-anywhere-overlay-9b6028c-audit.json`.

## Limits and next step

Normalized CA Tab/Enter does not prove physical OS/SMAPI keyboard/controller delivery. Q01 Backspace remains open. These message captures do not claim complete product localization or intermediate visual states of every CA action. Full incremental runtime behavior/performance belongs to U05; the broader common acceptance matrix and publication remain separate.

After these accepted runs, aggregate `bcbf231d-4f30-46d6-8767-eb05ab1ab0ea` reported behavioral PASS, but its preparation changed all eight UI DLL hashes without changing HEAD or the 637 source hashes. New DLLs lost the SHA suffix in InformationalVersion and changed their PDB source path. Their game/producer bytes match each other but differ from P `c5vynfny`; the aggregate therefore does **not** receive common package-identity PASS. `all-package-identity-failure.json` retains the discrepancy. The accepted U03 snapshots above are not overwritten or relabeled. The build-metadata cause and a new consistent package/runtime candidate must be resolved before common publication.

U03 now satisfies the dependency for F13. FLOWLINE can continue original fake-session create/dispatch/cancel/retry/diagnostic acceptance; UI continues U05. The integration owner resolves the newly observed build-identity issue and completes the remaining common gates before publishing.
