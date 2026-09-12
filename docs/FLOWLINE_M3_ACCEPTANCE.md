# Flowline M3/Q02 — current acceptance matrix

Goal: ordinary single-player entry, two supported chests/stations, UI-authored route, real supported items, visible result, safe failure/recovery/save/restart/save switching. Flow owns this scenario, domain and game adapter; GQ owns combined release/Q02. This document records evidence limits and does not redefine DONE around managed tests or fake fixtures.

## Current candidates

- Common F13 stable-menu diagnostic source reported by GQ: `866b1d0`. The actual FlowUiAcceptance.cs bytes match this worktree; no local Git command was used to assert a new HEAD. Native request `bfa4bf7f-edf3-40aa-8914-a7be24254990` belongs to that common runtime.
- Common actions request `209bd59f-341d-4a35-8241-0ea04bee6619` belongs to preceding common actions source, reported `4a7d710`. It is not automatically evidence for any later changed production DLL. GQ retains runtime/source/package identities.
- F18 ordinary entry was integrated by GQ as `d03b736` over Window `da0061d`; the frozen seven-file source manifest is `artifacts/f13/f18-entry-candidate.json`: seven Flow production/test files with hashes. Scoped Flow platform PASS963 (771 Flow +192 Stardew), actual eight new/strengthened cases and seven source hashes recorded in f18-entry-scoped-audit.json. Full C/G and native ordinary-entry evidence remain pending. The Window prerequisite is integrated; common validation and hosted observation are owned by GQ/UI.

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
| Choose source/quantity and send real supported items | Existing fingerprint/quantity command and physical adapter; new clean-network test partitions8→5+3 with full XML | Managed PASS for the typed-command scenario; final native UI inventories must prove actual path |
| No loss/duplication and correct custody | Existing F14–F17/F20 base; new test checks parcel/cargo identity, source/destination,8→5+3 and repeat refusals | Keep accepted base; new UI path and final runtime DLL checks required |
| Useful source/cargo/route/admission errors | Existing route preview, supported inventory filtering, typed owner reasons; some host network errors remain generic ActionUnavailable | INCOMPLETE F19 review: real rejection explanations must be actionable |
| Permitted cancel/retry/return/reconcile | Existing parcel availability/recovery rules and prior provider failure matrix | Final UI happy/refusal/recovery scenario pending; unknown physical outcome must not replay |
| Save/restart before and after delivery | Existing native chest crash/save matrix; new typed-command test restores Reserved and Delivered aggregates over cloned real Chest inventories | New managed test PASS; final native process/save bytes evidence required |
| Save switching preserves separate worlds | Accepted prior real chest isolation base; current fake UI A→B→A PASS | Final ordinary scenario and exact runtime matrix still required |
| UI framework ownership | Existing public Host API; Flow supplies semantics only; UI implements Window overflow | Source boundary respected; final combined consumer/package/runtime checks pending |
| CA coexistence | Common package-consumer checks and earlier native baseline available to GQ | Final fixed candidate combined evidence required |
| Player guide and limitations | FLOWLINE_PLAYER_GUIDE.md draft covers opening/stations/routes/items/pause/recovery/limits | Draft; update from actual accepted behavior and hand off to GQ |
| Delivery to GQ | Exact file manifests, source reviews and raw evidence paths sent directly under user authorization | F13 and F18 integrated by GQ; common validation and final native evidence pending |

## Required validation

F18 requires scoped `./tools/hatifect-test flow --platform`, `./tools/hatifect-check`, `./tools/hatifect-check --platform`; matching UI integration also needs `./tools/hatifect-isolated-ui-ca`. Each success must have nonzero actual executed tests and current source/DLL identity. No deployment to ordinary Mods or real save writes. Heavy runs are serialized with GQ/UI. The common stable-menu source already passed scoped187 and native15 in GQ; rerunning identical owner scoped adds no new evidence.

Current Flow file candidates have no newly claimed local commit: this task's Git access was denied by policy, so the handoff records exact source hashes. This is not permission to bypass that boundary. GQ independently owns common integration and release evidence.

## Next dependency-ready work

1. Complete common validation of the integrated F18/Window candidate; fix failures at their owner.
2. Add the canonical ordinary Window acceptance driver using the UI-owned hosted observation extension and prove the single composite SMAPI proxy, normal entry/selection/forms/actions.
3. Finish F19 actionable real-provider feedback and physical-input/native inventory scenario, then repeat the applicable original acceptance matrix and hand off final Flow assets/evidence to GQ.

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
