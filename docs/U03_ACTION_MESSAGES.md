# U03 — action messages and captured CA requests

Status: IN_PROGRESS. The implementation checkpoints are local source f8ea1e6dd0c6df3f3ac5f32eae155ac8948a8701 (framework messages) and c262196559a83eb418d60ccab47e27a31c3dd9f3 (typed CA). Candidate ab320dcd6fe4599c6292500b334f045cf62ed959 adds verified owning process-test fix0041788. This document does not close complete U03 or claim native acceptance that has not run.

## Contract and ownership

Experience adds immutable UiActionMessage with code, UiLocalizedText and optional semantic field. UiActionRejection.Localized retains legacy fallback fields. UiActionResult<T>.DomainFailure carries normal domain failure without a synthetic exception; Failure(exception,message) separates diagnostics from safe user text. Existing Failure(Exception), rejection constructors and exact ordinal localization behavior remain compatible.

Runtime captures action status after callback-bearing preparation and uses the same accepted snapshot for rendered text and accessibility Value. A locale-only change updates retained result text. Version/owner changes during preparation reject the candidate; the old frame remains accepted and the next Pump retries. Typed buttons reserve a message line to retain geometry between Running and terminal states. Diagnostic exception strings and arbitrary successful values are never used as user messages. Comparison of recreated equivalent localized availability messages uses the private immutable Dictionary without per-refresh interface-enumerator allocations.

CA binds all eight existing command IDs with RejectWhileRunning and the session publication owner. Each request captures one publication view, selected stable storage/category and view preservation values. The synchronous provider effect, validation and atomic publication remain inside the shared Request guard, including observers. A stale captured version is rejected before provider access; a version change or retirement inside provider blocks late publication without reversing its effect. Rejected open publishes the complete returned provider state with no false handoff, then returns Rejected. UI packages remain exact; no Runtime project reference was added. Open/Favorite graph descriptors now identify actual request/receipt payloads and retain their Selection target and UiSymbolId? source.

The CA package tests inspect actual binding delegates through one test-only reflection seam. They do not simulate Runtime dispatch or prove native input delivery. Public session facades continue using the same guarded execution path and retain their exception behavior.

## Evidence so far

All paths below are within the ui-action-messages worktree.

- Framework scoped Runtime run-bet7ribo: PASS524,15newcases. Earlier observed failures for locale invalidation, callback retirement/focus and57,344B/1024refreshes are preserved in .testagent/status.md; corrected allocation regression bounds1024equivalent cached refreshes to64B.
- CA run-e4umb18w: buildFAIL3xUnit1031 in new test methods; fixed with await, assertions retained. run-ai1dwwji PASS100. run-l345r7k8 PASS101 after additional publication-version and descriptor checks.
- Framework P at f8ea: retained artifacts/u03-action-messages/isolated-p and isolated-p-audit.json,90tests,44projection,8exactpackages,2CA DLLs; independent audit PASS.
- Typed CA P at c262: retained ca-isolated-p and ca-isolated-p-audit.json,101tests,46projection,8exactpackages,2CA DLLs. Independent reviewer checked all1297artifact hashes,101individual TRX outcomes,package/cache/producer/deployment identity and source projection. This is not a newly executed P at ab320dc.
- Canonical C at ab320dc: run-nmk_m8fc PASS1619.NET+381Python; all7TRX individual results/counters verified. Source manifest source-ab320dc.json includes21postimages checked against commit blobs and clean working tree.
- Canonical G at ab320dc: run-7x25bsac PASS 1,939 .NET + 381 Python; all 10 TRX individual outcomes and counters verified.
- Final P at ab320dc: ahc73s27 PASS 101, 46 projection files, eight exact alpha.47 packages. Retained final-isolated-p and final-isolated-p-audit.json; independent reviewer verified 1,452/1,452 file hashes, all package revisions and 21 source postimages.
- Native bootstrap 09d028b5-aa79-46c0-9893-ae66b7627750: PASS, game-created synthetic save and actual title/reload, exit 0, 12,333 ms, no teardown errors, options restored.
- Native semantic.actions.pump fe3f54c4-a666-4a51-bd39-d6c5db295239 at ab320dc: PASS 4. Runtime fingerprint 3ee0c7c84c9e03cf703916656573b10750af3748dae253fe4f74e509db4b2905. Four message captures EN/RU x Running/Completed, each composed and UI-layer PNG inspected (eight images). Initial inspection found stable button geometry, Running disabled and Completed enabled; independent review subsequently found an unsupported ellipsis glyph in RU Running (historical VISUAL FAIL; repair evidence below); accepted render and accessibility text match. Exactly one completion on owner thread 1 per message probe, worker thread 5, original language restored. Process exit 0, teardown errors empty, options restored and working-copy cleanup PASS. Diagnostic fixture labels remain English; this verifies framework action messages, not complete product localization.
- Native PERF fa4b7e6d-df3e-4d87-bf29-4494a2cf95ba: PASS 2, 620 frames, p95 0.05825 ms, p99 0.577374 ms, 5377.187 bytes/frame, measure/arrange miss ratio 0.0016129. All checked-in budgets passed; this is the existing diagnostic terminal workload, not a CA throughput measurement. Options restored, exit 0, no teardown errors. All eight game UI DLL hashes match the retained final P package DLLs. Raw inventories: artifacts/u03-action-messages/native-ab320dc-audit.json.
- Actual CA typed input acceptance remains pending.

Source reviewers reported no remaining findings for final framework fixes and CA capture/reentry/retirement/descriptors. Their package evidence audits are separate from source review. CA static pairing:24source/10test,16paired/8unpaired,1orphan; both changed session partials pair with both Experience test partials. This is a parse-only heuristic, not line/branch coverage.

## Preserved infrastructure diagnostics

Canonical C at f8ea failed twice (run-cflobnl6,run-p9ocnxz0):380/381Python, logging fake-executable exit124, .NET not run. Bounded original test replays and A/B entrypoint comparison did not prove an interpreter-entrypoint cause. Two owned-child samples showed early dyld startup; GQ correlated OS assessment records with a successful unchanged diagnostic process after approximately3seconds. Owning test-only fix0041788 increases the output-success fixture launch budget while retaining production timeout/cancellation behavior and every assertion. Historical failures remain in process-investigation.md and process-* artifacts.

Initial native save.bootstrap e9274e14-1b02-46a1-b9bb-74662d23c899 at f8ea was diagnostically interrupted after493085ms without save/progress. Result BLOCKED/exit130; this was not an1800s timeout or failed UI assertion. Owned process teardownErrors empty, both options files restored to initial absence, no save files. Native sample is insufficient to identify managed cause; dotnet-stack could not connect even using the games isolated TMPDIR. Root native action-message/PERF acceptance has not inherited a PASS from this attempt.

The second bootstrap 21160d19-c08e-4788-9c16-e64a5ef08ae6 used the supported short root /private/tmp/hatifect-smapi-test.u03.VcZPkP. Managed diagnostics connected and twice showed main thread in MonoGame GraphicsDevice.PlatformPresent. CUA independently reported a locked Mac and failed automatic unlock; causal linkage is not proven. Owned executor was interrupted after 323,003 ms (BLOCKED/exit 130), all three owned processes terminated, options restored, no saves created. After desktop became accessible, canonical recover-bootstrap recovered that exact reservation; the fresh bootstrap and action-message runs above passed. Historical BLOCKED results remain unchanged.

The facade-based CA scenario was insufficient at the earlier checkpoint. The final typed-input implementation and evidence below supersede that pending item; full U03 closure still requires the original roadmap acceptance audit.


## Literal ellipsis repair

Independent review of the first native captures found a real VISUAL FAIL: the Russian SmallFont does not contain U+2026, so the fitting Running message drew the default glyph. Source checkpoint `68b57e4b634543eff21fbce5cb2ee5c330e39f07` resolves literal ellipsis to three supported periods before native measurement/wrapping on a text-layout cache miss. Semantic text is unchanged; fonts supporting the glyph retain it. No public API changes.

The six focused regression cases first produced FAIL 5 / PASS 60 (`run-zvhhpfaq`), then PASS 65 (`run-mymbiuaz`). Both used `./tools/hatifect-test ui --platform --project "Hatifect UI/tests/Hatifect.UI.Stardew.Tests/Hatifect.UI.Stardew.Tests.csproj"` with the configured x64 SDK. Two earlier command-selection errors executed no tests and provide no behavioral evidence.

| Requirement | Evidence |
|---|---|
| Literal ellipsis with sufficient width uses supported glyphs in Clip, Wrap and Ellipsis | `LiteralEllipsisUsesMeasuredFallbackEvenWhenTheWholeMessageFits` (three cases; exact output, width and font membership) |
| A supported ellipsis remains unchanged, even without a period glyph | `SupportedLiteralEllipsisRemainsUnchanged` |
| Wrapping accounts for the expanded fallback width | `WrappingUsesExpandedLiteralEllipsisWidth` |
| Missing literal and fallback glyphs fail explicitly without truncation | `UnrepresentableLiteralEllipsisFailsEvenWithoutTruncation` |
| Existing native text behavior remains accepted | Scoped `run-mymbiuaz`: PASS 65, zero skipped |

Fresh `semantic.actions.pump` run `25cb2662-ba8c-493c-bfec-f9300e68a39e` at this checkpoint passed four assertions with fingerprint `8d0744635cac2a8cf8929d5694069d90beb2ddf45e1ab191d937141126cf3f2d`. Both RU Running images now visibly show periods instead of the fallback-cross. The process exited 0, teardown errors were empty and both temporary options files were restored. Broader final C/G/P and PERF still refer to the explicitly named earlier candidate until repeated for the completed CA input slice; they are not relabeled as checks of this repair.

Independent follow-up review closed the literal-ellipsis P2: source diff, all 65 TRX outcomes, all eight fresh message PNGs and options/save cleanup were checked. No remaining finding in this bounded repair.


## Typed native input and final candidate

Implementation `028b999a8647975805b4c71b0024619ef7852086` adds the optional `IUiSemanticSurfaceActionAutomationApi` facet without changing v1. The exact harness traverses at most 256 normalized Tab inputs and submits Enter once to a unique accepted root button. Disabled automation, foreign handles, wrong owner thread/screen, retired owners, portals and replaced action identities are rejected. Consumer obtains one richer API instance for both creation and automation because separate SMAPI API requests create separate owners. Production direct delegates are not exposed. Admission does not claim asynchronous completion, completed rendering or physical OS events.

CA acceptance now exercises all eight typed command IDs through that input facet. It retains exact native storage handoff, Controller B/Escape, single-owner, fresh reopen and lease restoration assertions; Close additionally proves refusal of input to its retired handle. Review caught and removed redundant synchronization of an already retired handoff surface. The static boundary assertion was updated in `2a24545`; `run-cpcoa8ty` passed all 381 Python tests. Earlier C `run-6cnbie6l` remains FAIL 380/381 for its obsolete synchronization expectation.

Native `83ec5b02-91b3-4324-a711-bec8b1fe0230` was BLOCKED before game launch by absent isolated Chests Anywhere. All 19 files of the existing isolated dependency were verified against the GQ handoff inventory, copied only to the new isolated Mods root, and verified again. The next run `863a5727-54e4-4982-bbca-f933e4c19a61` exposed an owning layout defect: FAIL 5 before the first input, `Layout constraints cannot be satisfied.` No assertion or consumer layout policy was weakened.

Source `32298890a4b5f652c45231989d0fe11392b8860e` makes the allocation compression test use the existing 0.01 minimum tolerance. Subtracting headings/insets can otherwise leave a tiny deficit with zero compressible slack. Typed buttons keep their reserved second line. No API change, new allocation, callback or traversal is introduced by this repair. `TypedActionBarWithFractionalLineHeightRetainsUsableGeometry` reproduces the error through real composition and Build: RED `run-7i7_vt8y` 527 Passed / 1 Failed out of 528; GREEN `run-nx20m8ii` 528 Passed, zero skipped. Four fractional metrics retain all eight button geometries and rendered labels. The impossible-viewport regression remains unchanged. Initial fixture build `run-tpvs13_w` failed on C# 12 syntax; it was corrected to the existing C# 10 without changing project settings. The earlier simpler fixture `run-zotot6bq` passed 527 and did not reproduce the native failure.

Final checks below all refer to immutable source `3229889`, not the earlier message checkpoint:

| Gate | Result and retained evidence |
|---|---|
| C | `run-0g3fyooi`: PASS 1,623 .NET + 381 Python; seven TRX independently audited |
| G | `run-cyphd2kx`: PASS 1,952 .NET + 381 Python; ten TRX independently audited |
| P | `5b8s8yi4`: PASS 101 CA tests, 46 projected files, eight alpha.47 packages, two CA DLLs, no UI source/deployed UI copies |
| Native CA | `2845c6c5-fa7b-46b6-bec0-784b79bd573b`: PASS 5; eight typed action IDs and exact lifecycle assertions |
| Native messages/Pump | `114b2918-bdb8-41db-b8aa-cdb3603e4080`: PASS 4, 105 ticks; all eight EN/RU composed/UI-layer PNGs independently viewed without findings |
| PERF | `3450c271-f315-4189-9706-4b5602e005e2`: PASS 2, 620 frames; p95 0.052959 ms, p99 0.421459 ms, 5338.194 bytes/frame; measure/arrange miss ratio 0.0016129 |

The three native requests share UI runtime fingerprint `e166a1213096194c2a858a94ea086b44580ce555d596dfb08c547672cfd45778`, exit 0, empty teardown errors and restored options. Request-owned saves are absent after cleanup where a save was requested; PERF is title-only. Twelve process observations spanning active PERF and completion found no dotnet/MSBuild/testhost competition; eight include only the owned game and four follow its exit. GQ and FLOWLINE held a quiet window. This is the existing terminal diagnostic workload, not CA throughput.

Independent audits verified 35 source postimages, all 1,303 retained P file hashes, eight package revisions/DLL hashes, three restore assets, four resolved UI cache DLLs, and both deployed CA DLLs. Parent verified all eight package/producer/game DLL identities and retained separate producer/game snapshots after the final run. Artifacts: `artifacts/u03-action-messages/{source-3229889.json,c-3229889-trx-audit.json,g-3229889-trx-audit.json,isolated-p-3229889-audit.json,native-3229889-audit.json,producer-game-3229889/}`.

| Requirement | Evidence |
|---|---|
| Captured typed requests reject stale versions before provider effects | `CapturedStorageRequestRejectsChangedPublicationBeforeAnyEffect` |
| Provider retirement preserves committed effects without late publication | `RetirementInsideProviderPreservesCommittedEffectWithoutPublishingLateResult` |
| Safe messages remain separate from diagnostic exceptions | `TypedProviderFailureKeepsAcceptedProjectionAndSeparatesDiagnosticText`, `WorkerCompletionKeepsAcceptedRunningMessageUntilPumpAndHidesDiagnosticException` |
| Production action automation refuses access before native helpers | `ProductionApiRejectsActionInputBeforeTouchingTheHandleOrNativeHelper` |
| Fractional layout retains typed button geometry while impossible layouts fail | `TypedActionBarWithFractionalLineHeightRetainsUsableGeometry`, `ImpossibleViewportFailsInsteadOfCollapsingInteractiveControls` |
| Concrete native consumer traverses typed input and retires owners | Native CA `2845c6c5`: five checks, all eight command IDs |

The action-message and concrete typed CA slice is implemented and verified. Full U03 remains IN_PROGRESS pending the original acceptance closure audit and shared integration. Native CA is synchronous and its final capture occurs after retirement; no intermediate CA visual matrix or physical input acceptance is claimed. Q01 physical input and full product localization remain separate work. Next ready step: hand off this candidate, reconcile the original U03 criteria with earlier reload/save-switch evidence, then proceed to the next dependency-ready UI slice.
