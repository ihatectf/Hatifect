# Flowline M3/Q02 — current acceptance matrix

Goal: ordinary single-player entry, two supported chests/stations, UI-authored route, real supported items, visible result, safe failure/recovery/save/restart/save switching. Flow owns this scenario, domain and game adapter; GQ owns combined release/Q02. This document records evidence limits and does not redefine DONE around managed tests or fake fixtures.

## Current candidates

- Common F13 stable-menu diagnostic source reported by GQ: `866b1d0`. The actual FlowUiAcceptance.cs bytes match this worktree; no local Git command was used to assert a new HEAD. Native request `bfa4bf7f-edf3-40aa-8914-a7be24254990` belongs to that common runtime.
- Common actions request `209bd59f-341d-4a35-8241-0ea04bee6619` belongs to preceding common actions source, reported `4a7d710`. It is not automatically evidence for any later changed production DLL. GQ retains runtime/source/package identities.
- F18 ordinary entry was integrated by GQ as `d03b736` over Window `da0061d`; the frozen seven-file source manifest is `artifacts/f13/f18-entry-candidate.json`: seven Flow production/test files with hashes. Scoped Flow platform PASS963 (771 Flow +192 Stardew), actual eight new/strengthened cases and seven source hashes recorded in f18-entry-scoped-audit.json. Full C/G and native ordinary-entry evidence remain pending. The Window prerequisite is integrated; common validation and hosted observation are owned by GQ/UI.
- Current local GQ checkpoint: `c5174a8c4a9bf1f3a28d7420bb11d16705a6f96d`. G PASS 2 329 .NET + 503 Python; applicable P PASS101; prepare PASS. Normalized production Window `flow.ui.player` PASS11 and the original chest matrix PASS116 plus two budget assertions. Exact physical-input request `521bde15-3a64-4deb-abfd-0040dc53fed0` stopped before its first semantic step because macOS Accessibility preflight was false. Exact IDs and evidence limits are recorded in [Q02_RUNTIME_C5174A8.md](Q02_RUNTIME_C5174A8.md).
- Post-checkpoint local composition includes typed Send owner `ff4562d` as integration `1a83a2b` and typed Network owner `ffe59ff` as integration `aca70b0`. Its exact Stardew adapter suite has 254 PASS and its architecture/public API checks pass. The `c5174a8` common/runtime evidence remains historical and is not promoted to the changed Flow DLL; new G/prepare/runtime evidence is pending.

## Requirement-to-evidence matrix

| Requirement | Current evidence | Status / remaining proof |
| --- | --- | --- |
| F13 visible create/dispatch/cancel/retry results | Common actions PASS11; all13 composed inspected by GQ; Flow independently inspected created/no-route Result PNG | PASS for the EN fake fixture layer; fresh final-candidate binding remains |
| F13 repeat/stale/retired commands have no extra effect | Same native report: reentry1 rejected, effects1→1; stale/retired assertions PASS | PASS bounded normalized-action fixture; physical-repeat path still needed |
| F13 localized feedback | Isolation EN/RU state/cargo/availability captures; actions Result currently EN | INCOMPLETE: action feedback EN/RU on final candidate |
| F13 scale75/100/125/150 with retained sources/actions | Isolation PASS15/19; source/action/publication identity and stable-native-owner guard; five scale/controller frames viewed by Flow | PASS bounded fake Parcel matrix; ordinary Network matrix remains |
| F13 reopen and A→B→A isolation | Isolation3loads2titles6views19observations; old subscriptions/actions retired;3exact settled restorations | PASS fake UI/lifecycle layer |
| Physical keyboard/pointer/controller input | Existing actions use normalized Tab/Enter; controller capture selects profile | INCOMPLETE: real native events/focus/action results; normalized input is insufficient |
| Ordinary opening without console or pre-existing menu | ModEntry.PlayerInterface uses configurable world input and existing CreateSurface(Window) | Managed compile/scoped PASS; native normal-entry proof pending |
| Supported current chest selection | PreparePlayerTarget clears prior token, checks Active and supported chest; rechecks physical identity in CaptureTarget | Managed PASS: stale/unsupported/replaced/recovery tests; actual input admission still pending |
| Two stations and directed route from clean allowed state | Existing typed RegisterStation/AddLink; new clean-network game-reference test uses these commands | Managed typed-command scenario PASS; actual UI registration/link/save reopening needed |
| Route editing policy | Rename keeps identity; rebind refuses queued/extraction-uncertain origin, moves no items; RemoveLink preserves admitted in-flight work; station deletion not exposed | Existing owner behavior; expose limitations and verify final UI actions |
| Choose source/quantity and send real supported items | Existing fingerprint/quantity command and physical adapter; `35cbcb0` clears UI selection when refreshed slot/fingerprint changes and requires explicit selection before retry; clean-network test partitions8→5+3 with full XML | Managed PASS for the typed-command and selection/revalidation scenarios; final native UI inventories must prove actual path |
| No loss/duplication and correct custody | Existing F14–F17/F20 base; new test checks parcel/cargo identity, source/destination,8→5+3 and repeat refusals | Keep accepted base; new UI path and final runtime DLL checks required |
| Useful source/cargo/route/admission errors | Typed route reasons; `acb1279` adds exact EN/RU persistent-limit results; `1a83a2b` adds typed Send provider/state/pending results; `aca70b0` adds typed Network provider/topology/invalid/rebind-pending results, all with no rejected mutation | Managed owner-layer PASS for named route, resource, Send and Network failures; current exact native EN/RU visibility remains pending, while unexpected host callback faults stay diagnostic failures |
| Permitted cancel/retry/return/reconcile | Existing parcel availability/recovery rules and prior provider failure matrix | Final UI happy/refusal/recovery scenario pending; unknown physical outcome must not replay |
| Save/restart before and after delivery | Exact `c5174a8` chest crash/save matrix and normalized Window path restore Reserved/Delivered aggregates over real Chest inventories | PASS on current candidate; physical-input binding to the same path remains |
| Save switching preserves separate worlds | Exact `c5174a8` isolation PASS12; cleanup removed both request-owned working copies | PASS current original matrix; final installed-candidate regression remains |
| UI framework ownership | Existing public Host API; Flow supplies semantics only; UI implements Window overflow | Source boundary respected; G/P/prepare and production Window PASS on `c5174a8` |
| CA coexistence | Applicable package isolation PASS101 on unchanged UI source, eight packages and 47 projected files | Exact in-game coexistence on the final installed candidate still required |
| Player guide and limitations | FLOWLINE_PLAYER_GUIDE.md covers opening/stations/routes/items/pause/recovery/persistent limits and is synchronized with local `aca70b0` | Source guide updated; installed/runtime behavior and final GQ handoff remain pending |
| Delivery to GQ | Exact file manifests, source reviews and raw evidence paths sent directly under user authorization | F13/F18/F19 integrated locally through `aca70b0`; current Flow changes need new common/runtime evidence before final native/release acceptance |

## Required validation

F18 requires scoped `./tools/hatifect-test flow --platform`, `./tools/hatifect-check`, `./tools/hatifect-check --platform`; matching UI integration also needs `./tools/hatifect-isolated-ui-ca`. Each success must have nonzero actual executed tests and current source/DLL identity. No deployment to ordinary Mods or real save writes. Heavy runs are serialized with GQ/UI. The common stable-menu source already passed scoped187 and native15 in GQ; rerunning identical owner scoped adds no new evidence.

Current Flow admission-feedback candidate is committed locally through `aca70b0` (`acb1279` persistent limits, `1a83a2b` typed Send feedback and `aca70b0` typed Network authoring feedback); no remote branch, push or PR is part of this task. GQ independently owns final local integration composition and release evidence.

Exact local runtime checkpoint [e9cfffc](Q02_RUNTIME_E9CFFFC.md) proves clean packaging and bootstrap for this candidate. The physical-input request stopped before its first semantic step because macOS denied post-event access, so it provides no evidence for `clickPoint`, key K, Window captures or visible F19 results. F13/F18/F19/Q02 remain IN_PROGRESS.

The code-equivalent local integration `3a34cbb` subsequently passed exact Flow platform validation `run-7pzafv_n` with 797 Core/Persistence and 254 Stardew adapter tests. Common G `run-jaeobkd2` passed 2,334 .NET and 503 Python tests with no failures or skips. Later commits through `b890832` changed documentation only; these static results apply to the unchanged code, while package/runtime evidence remains bound to its recorded exact source.

Latest local checkpoint [5d017ae](Q02_INTEGRATION_5D017AE.md) includes U06 shared status/input prompts,
typed Flow/CA status consumers and F19 cargo reselection. Exact Flow platform is PASS 803 + 254,
common G is PASS 2,366 .NET + 503 Python, CA platform is PASS 102 and isolated UI→CA P is PASS.
Runtime evidence still requires a fresh prepare and scenario matrix on the next frozen docs HEAD.

## Next dependency-ready work

1. Retry canonical exact prepare on unchanged local candidate `046267e` when the NuGet repository-signature endpoint is available; `run-r7i4ztb4` is BLOCKED at restore, while common G/P are complete.
2. After macOS grants Accessibility to the Python runtime, run `flow.ui.player.input` on that fixed composition and require the complete phase/action-result capture set through final `Delivered`.
3. Complete exact-candidate EN/RU, scale, controller, CA coexistence and installed-archive acceptance; hand exact Flow assets, dependencies and evidence to GQ.

F13/F18/F19 remain IN_PROGRESS until their original criteria are proven. Q02 overall belongs to GQ.

## Native input protocol reuse and Flow supplement

Existing protocol: `semantic.input.native`, implemented by UI `UiAutomatedAcceptanceController.NativeInput.cs` and `UiNativeInputGate.cs`. Retain its bounded phase wait, foreground check, real SMAPI event counters, two stable completed rendered postconditions, request-derived text probe, captures and external origin declaration. Historical request `b91f5a8b-cb26-4955-a46f-64bf3bc31999` failed with zero input events; process exit0 and cleanup do not turn it into acceptance. Do not start another unattended physical session without an available operator. OS-injected input must be labelled as such.

The Flow evidence supplement must correlate the ordinary Window scenario with the same input evidence and runtime/request identity:

1. Arm against one live surface instance, accepted/rendered frame, completed pass, Flow session/revision and the exact current physical source fingerprint. Record only immutable evidence prepared during Update; no inventory reads, source callbacks or command execution during Draw.
2. Observe new foreground SMAPI events after the baseline. A counter increase alone is insufficient: bind the expected UI action identity, command request and result, then require the matching later committed publication and fresh completed rendered Result. Do not use normalized automation to satisfy the physical-event requirement.
3. For send admission, verify one new parcel/cargo with the selected quantity and unchanged total physical quantity before extraction. A duplicate/stale command must add no parcel, cargo, receipt or physical delta. The game remains paused while the menu owns it; delivery is a later ordinary close/tick/reopen milestone.
4. At extraction/delivery/refusal/return/save/reload milestones, retain exact domain identities/states, physical source/destination XML hashes and quantities, retained custody and receipt outcome. Compare against independent expected totals; the journal projection is not a substitute for actual inventories. Keep reads bounded to the isolated scenario's known chests/items.
5. A retired surface/session or changed candidate invalidates pending evidence. Failure retains diagnostics and original cleanup policy; it never triggers implicit replay or success.

Framework prerequisite: current observation/action/reveal APIs admit only same-owner active-menu overlays, while ordinary Window returns a standalone session. SMAPI documents provider GetApi invocation caching; the provider method returning a new bridge does not prove a new owner for every registry request. Generic proxy cross-casts are still not a supported contract. UI therefore owns a bounded hosted-surface observation extension and a supported way to create and observe on one API instance, preserving foreign/thread/screen/retired rejection. Flow does not obtain internal menu/scene objects through reflection or private casts. This dependency was reported to UI and GQ; the protocol itself is not duplicated.

### Hosted observation dependency correction

GQ reported candidate `2a007f2` with F18/Window and Observation validation262/42. UI explicitly confirmed that those Observation changes cover existing overlays; standalone Window Capture/Activate/Reveal/Cancel admission is a separate pending UI slice. The proposed consumer public composite `IFlowUiHostAcceptanceApi : IUiSemanticHostApi, IUiSemanticSurfaceRevealAutomationApi` must be obtained with a single `GetApi` and proven in native SMAPI. No normalized text/collection-selection facet exists in the current public API. A first bounded driver may prepare semantic forms/selections explicitly, but must label them as fixture preparation and must not count them as player input or full ordinary-entry acceptance.

### Canonical prepared Window candidate

`flow.ui.player` is an additive canonical scenario over `FlowChestRoundtripAcceptance`: the original eight real-chest checks remain required, with `window-actions`, `visible-results`, and `window-reopen` added. It admits whole8 and partial5 from a13 stack through the production Window factory and owning normalized actions, retains two actual save cycles/three loads and original item-fidelity/no-duplication checks, then reopens delivered history. The candidate uses EN/100% and temporarily leases ForceOff/false input, restoring the original pair with fresh completed frames before real saves and after final reopen. Request-owned canonical save naming, report module ownership, required UI+Flow mods and fail-closed check validation are wired explicitly. This source is not native evidence: hosted admission and the composite SMAPI proxy remain unproven, and seeded forms/selections do not prove ordinary input. Manifest: `artifacts/f13/player-window-driver-candidate.json`. Targeted tooling: save provisioning41, direct runtime44, live harness54 tests PASS. Initial scoped run5o0_mikg failed compilation on ambiguous target-typed Observe construction (zero tests executed); explicit frame type fixed, run8ozn014q pending.

Final strengthened F19 + canonical Window driver scoped: `run-8ozn014q`, exit0, PASS964 (771Flow+193Stardew), zero failures/skips. New resource/counter assertions executed PASS; Window driver compiled but native scenario remains NOT_RUN. Audits: `artifacts/f13/f19-route-reason-final-scoped-audit.json` and `artifacts/f13/player-window-driver-scoped-audit.json`. Historical run0126 covers the earlier test; run5o0 failed compile before test execution.

Common driver ownership review found an additional prerequisite: UI `UiAutomatedAcceptanceController.TryStart` must bypass exact `flow.ui.player`, like the other Flow-owned scenarios. GQ assigned this to UI; Flow does not modify the UI controller. Until integrated, the prepared scenario is not runnable as a valid single-owner native acceptance.

Driver review fixes: game-session surface failures now fail chest acceptance. The canonical save starts Auto/false, so a temporary owned input lease replaces the earlier invalid unchanged-settings precondition; owner identity and strict ForceOff remain checked, restoration completes before the driver can proceed to actual Saving. Native proof is pending. Final13file scoped run-alhi9dpv is in progress.

Final13file player driver scoped: run-alhi9dpv/handle7530, terminalexit0, PASS964 (771Flow+193Stardew), zero failed/skipped; source hashes and both actualTRX audited in `artifacts/f13/player-window-driver-final-scoped-audit.json`. Includes owned input profile lease and pump-failure propagation. Full tooling390PASS applies to unchanged six tooling files. Native scenario/profile/composite remains NOT_RUN; final common C/G/P and original matrix remain pending.

Ordinary trigger and OS/human input run card: `docs/FLOWLINE_NATIVE_INPUT_ACCEPTANCE.md`. Fifteen concrete steps bind current form/action IDs, K/busy-menu/reopen behavior, Tab/ShiftTab/Search/Backspace, whole/partial inventory effects and original lifecycle matrix. Source inspection confirms the existing dogfood native gate cannot run unchanged on a field-containing ordinary Window. UI owns its next diagnostic observer/phase adapter; no new public input API or Flow private UI access is proposed. This card is prepared, not executed.

Common candidate `3ba57ab` (GQ-reported) platform run `run-xtx8d3yb`: PASS2094.NET+391Python; Flow independently parsed all10actualTRX/individualResults and verified common13driver+3F19source hashes. Audit: `artifacts/f13/common-player-platform-audit.json`. GQ confirmed terminalexit0. Package/CA run follows; native `flow.ui.player` remains pending.

First real Window smoke is now FAIL, not pending: request0a7f36d7 on3ba57ab created2realstations+1route999/180 and reached accepted result observation, but owning Reveal returnedfalse before route-created freshframe capture. Five prior captures are valid; no shipment/save completed. Flow control-flow audit found no skipped expected-value wait. UI owns bounded bounds/offset trace for the next diagnostic run. Exact evidence: `artifacts/f13/common-player-first-native-failure-audit.json`.

Ordinary-input candidate handoff: exactly16 intended files at `${HOME}/Developer/Hatifect/artifacts/flow-player-input-candidate/artifacts/f13/player-input-final-candidate.json`, with commonBeforeSHA and final source SHA; no full source-copy overlay. Scoped `run-tik3mwji` **PASS991** (771Flow+220Stardew), 22reader cases +5journal cases; full tooling `run-zsnvfh2z` **PASS391**, no failures/skips, bothhandles reapedexit0. The final reader uses exact native field/name, per-command EN/RU results and serialized float rectangle edges; incorrect/missing/clipped/stale evidence fails closed. Initial failed geometry fixture run-tsk1iay9 is retained separately. Native ordinary input remains NOT_RUN. GQ owns integration with UIobserver c00266f, fixed-candidate G/P/prepare/native and final Q02. F13/F18/F19 stay IN_PROGRESS; original recovery/crash/save-switch/EN-RU-scale-controller-CA requirements remain open until their exact-candidate evidence exists.
