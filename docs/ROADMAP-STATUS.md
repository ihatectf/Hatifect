# Roadmap implementation evidence

Date: 2026-09-06. Working branch: `codex/flowline-next-ten-slices`; source baseline: `2f08a4f4db36ef0b1ba58e385fc0ab5044cae867`.

The authoritative scope is [ROADMAP.md](ROADMAP.md), restored verbatim from commit `1e741ef` because this task branch predates that documentation commit. The earlier exploratory Flow multiplayer/adapters/policies list is superseded as an execution plan; no new product work outside the approved roadmap is being added. It does not replace U01–U10, R01–R04, T01–T03 or their acceptance. The Flow foundation is committed and published in task branch as `cdf9d2e86e90f0174251243147f8a2f9480d14e8`; its integration with the verified UI SDK/editor/D01 is owned by the UI task. The full roadmap IDs remain incomplete until all their acceptance and integration evidence are present.

This report preserves separate evidence for Flow foundation `cdf9d2e`, F11 `e31c1ef`/`ff43d0b`, F16-a `54c5af5`, and the combined UI foundation. Flow artifact paths belong to `${HOME}/Developer/Hatifect`; the UI integration section names its own worktree. Earlier checks do not certify a later combined candidate. Shared B01/B02 statuses and D01 are authoritative in [ROADMAP.md](ROADMAP.md) and [SEMANTIC_V2.md](SEMANTIC_V2.md).

## Flow owner implementation status

| IDs | State | Authoritative implementation/evidence | Remaining acceptance |
|---|---|---|---|
| B01 | IN_PROGRESS | Source and contract inventory below; current host-free and platform checks PASS | Commit this inventory and integrate the UI owner baseline |
| B02 | IN_PROGRESS (UI owner) | Shared contract owned by the UI task in codex/roadmap-implementation; this task supplied Flow consumer requirements | Integrate verified D01/U01 commits without competing UI implementation |
| U01 | IN_PROGRESS | Existing typed sources and explicit builder IDs; discovered binding regenerates IDs from localized labels | Preserve identity in binding; typed relation graph; negative diagnostics; Flow/CA fixtures |
| U02 | PLANNED | Collections already have internal revision and stable-ID selection; scalar UiState has no public version | Version/publication contract, typed deltas/reset and atomic related-source publication |
| U03–U10 | PLANNED | Current actions are synchronous; deterministic planner/profiles, layout/input/virtualization and form validation already exist | All specified async/environment/authoring/theme/component/extension acceptance remains required |
| R01–R04 | PLANNED | Existing single-asset Last Known Good and retryable resource teardown | Generation ownership, bundle preparation, migration and multi-host commit/rollback |
| T01–T03 | PLANNED | Existing Tooling/Server, symbols/diagnostics and DevTools | New metadata/protocol, preview matrix, editor workflow and examples |
| F11 | VERIFIED, integration pending | Published e31c1ef + ff43d0b: immutable snapshots, typed commands, observer fencing, provider mode, supported operations, owner-derived availability and stable reasons; evidence below | UI owner is integrating both commits with published D01/UI baseline |
| F12–F13 | IN_PROGRESS | ParcelExperience/ParcelSurface, typed controls and localized projections; host-free behavior tests | New semantic-v2 integration; complete fake-session in-game lifecycle/input evidence |
| F14–F17 | IN_PROGRESS | SaveBoundCargoPort and FlowGameSession: item XML, physical custody, save barrier, fencing; production chest roundtrip recorded below | D03/D04 record, partial stacks and complete supported runtime failure matrix; current candidate rerun |
| F18–F19 | IN_PROGRESS | NetworkExperience: stations/routes, inventory fingerprints, shipping/history, return/recovery | Ordinary player entry without console, Quick/View integration, reason codes, locale/input/scale acceptance |
| F20 | PLANNED | Explicit bounded stations/links/cargo and per-tick work | Measurements, D06, backpressure diagnostics and long-running runtime evidence |
| I01 | IN_PROGRESS | CA continues consuming exact UI packages; prior alpha.30 isolated build passes | Migration to the new semantic/authoring contract and current runtime/visual acceptance |
| Q01–Q03 | IN_PROGRESS | Prior baseline package and game results retained separately | Full specified modernized scope, current fingerprints, matrix, performance/editor evidence and final archive |

## Completed bounded checks, not stage completion

- `./tools/hatifect-test ui --platform`: **PASS**, 277 tests, including 7 new platform screen/keyboard ownership cases. Evidence: `artifacts/validation/run-50gt4vku/summary.json` and TRX. Independent reviewer found and then rechecked the native-menu handoff fix; no open findings in that bounded UI diff. Actual split-screen visual/input acceptance remains unperformed.
- `./tools/hatifect-test flow --platform`: **PASS**, 634 Flow tests + 61 game adapter tests. Evidence: `artifacts/validation/run-nu3kvz6n/summary.json` and TRX. Added mutex/fingerprint admission, peer host lost-reply idempotency, per-peer target isolation, save barrier/disconnect and fault fencing. Newtonsoft wire roundtrip is exercised by the peer host test.
- Earlier single-player candidate: production `flow.chest.roundtrip` request `c64b077c-986a-450e-87a8-8e57a7d0c9f6` passed 8 checks, 347 update frames, 3 loads and 2 Saving/Saved pairs. Full item fidelity and absence of duplicate delivery were observed. Exact earlier candidate evidence is in `.testagent/ten-slices/status.md` and `artifacts/flowline-ten-slices/README.md`; it does not certify later edits.
- `./tools/hatifect-check`: **PASS**, 904 .NET + 297 Python tests, `artifacts/validation/run-lx8bdso7/summary.json`.
- `./tools/hatifect-check --platform`: **PASS**, 1,051 .NET + 297 Python tests, `artifacts/validation/run-91tslfgt/summary.json`.
- `./tools/hatifect-isolated-ui-ca`: **PASS**, 42 projected files, 8 alpha.30 packages, 79 tests, 2 CA DLLs with UI sources absent. Canonical command exit 0 and terminal summary confirmed these counts; its temporary projection/TRX was automatically cleaned. The platform run above separately retains the CA TRX.
- `./tools/hatifect-agent-check --host`: **PASS**, configured project/skill loaded and 217 skills discovered.
- Final peer endpoint review found a transient authority loss incorrectly persisted as recovery when capturing a target. Fixed by the existing live application availability gate; unexpected adapter failures still fence. `./tools/hatifect-test flow --platform`: **PASS**, 634 Flow + 63 Stardew tests, `artifacts/validation/run-1bybxd3f/summary.json`. This is the only code delta after the full checks above. Both before/after snapshot publication cases verify rejection, no persisted recovery, retained replay and successful new sequence after authority returns.
- Initial C check `run-_ze48r98` found two Python source-contract assertions referring to the previous teardown location/signature; updated assertions verify the actual owning event binding and retry state. A later build `run-0e14ytla` stalled in MSBuild GetTargetFrameworks; the exact owned process was diagnosed, terminated and the canonical full checks above passed. The first new authority test run `run-ptlwzwse` reached the final metadata assertion but failed because the Net container enumerable is not its dictionary; corrected the assertion to the station key and reran successfully. No production test was removed or weakened.

## Current foundation runtime and packaging

- `./tools/hatifect-ui-test semantic.lifecycle`: **PASS**, request `293e82f4-f390-4361-a7d5-e41537648de9`; three lifecycle/focus/tree checks, isolated process exit 0. Screenshot inspected: diagnostics/inspector controls and focused diagnostics are present. This is lifecycle acceptance only, not the complete UI visual/input/locale/scale matrix.
- `./tools/hatifect-smoke flow.chest.roundtrip`: **PASS**, request `2b32ce8c-508d-497a-8652-5d9b44e65826`; all 8 checks, 347 frames, 3 loads, 2 Saving + 2 Saved, no errors. Flow runtime fingerprint: `1071d92126a168395176a214be00d7b12200a044a1b9b43990fb04b4e43d779a`. Real chest custody, item fidelity, save/reload and no duplicate delivery verified through the production session.
- Fresh isolated deployment: `artifacts/validation/run-m55a6p6g/summary.json`; packaging tests intentionally skipped because the separate gates above are authoritative. No RC promotion is claimed.
- `python3 tools/release.py verify-package .smapi-test/isolated/Mods/Hatifect`: **PASS**, 14 runtime DLLs. Generated acceptance reports were byte-matched to preserved request artifacts before deleting only those generated files. Archive and DLL hashes: `artifacts/flowline-foundation/`.
- Independent bounded review: UI screen/keyboard handoff fixes and peer endpoint authority/replay fixes rechecked; no remaining findings in the reviewed scopes. Multiplayer client/wiring and split-screen runtime remain unimplemented/unverified and disabled.

## F11-a: revision subscription and projection lifecycle

Owner: Flow semantic consumer; existing application event/snapshot contract and public UI API are unchanged. Both network and parcel views subscribe before their initial read, retain notifications raised during read/projection for the next pump, and reject nested pumps before they can consume dirty state. Failed reads remain retryable; failed construction detaches the subscription; a failed ordinary unsubscribe retires callbacks/actions immediately and retains a handle for retry.

- `./tools/hatifect-test flow`: **PASS**, 646 tests (`run-seoy49ou`), including 12 new cases for both projections.
- Final `./tools/hatifect-check`: **PASS**, 916 .NET + 297 Python tests (`run-3akheh8x`), including the final reentrant-pump guard.
- Independent source/actual TRX review rechecked the subscribe/read and reentrant-read counterexample. Existing snapshot/session/observer tests remain intact. No new UI API, persistence format, game adapter or package boundary is introduced, so separate game/visual/package gates are NOT_APPLICABLE to this bounded F11-a change. Full F11 and later runtime acceptance remain required.
- Limit: a provider whose event removal keeps throwing during a constructor failure cannot provide a usable cleanup handle; this does not imply unconditional cleanup against an arbitrarily broken event implementation. Production Flow application event accessors are ordinary managed events.

## Current next slice

F16-a is published as `54c5af5`. Verify its integration with the published UI foundation, then continue the remaining F16/F17 failure and save-isolation matrix. The UI task owns B02/U01–U10/R/T on codex/roadmap-implementation; its foundation/SDK/editor/D01 integration is published in develop at 3f11e5d (verified by fetch), and F11 commits e31c1ef + ff43d0b are its next integration step. This task owns F11–F20 and real-game acceptance, integrating verified UI commits. F18 ordinary player entry follows integration of the standalone host and U01 identity fix. Multiplayer remains disabled and its exploratory foundation is not a released capability.

## F11-b: owner-derived availability and rejection reasons

Owner: Flow Core/Application and Dispatch. The D01 contract supplies provider mode, supported operations and an immutable five-action availability set with stable rejection codes/reason keys. Reservation projection and execution share route, queue and capacity admission. Semantic consumers localize those reasons in EN/RU and explicitly identify the diagnostic provider. Game sessions and the disabled peer wire preserve the metadata; terminal peer projections release their previous live contents. No persistence envelope or physical inventory/custody format changes.

- `./tools/hatifect-check --platform`: **PASS**, 1,076 .NET + 297 Python tests (`run-ub2txdqz`).
- After retaining the original public constructors and deconstruction signatures, `./tools/hatifect-check`: **PASS**, 927 .NET + 297 Python tests (`run-vh75lnho`).
- Final `./tools/hatifect-test flow --platform`: **PASS**, 657 Flow + 65 Stardew tests (`run-t2mgi20d`), including terminal peer disposal and actual parcel binding-context creation. These are the bounded code deltas after the full platform check.
- Added 11 `FlowAvailabilityTests` cases, serializer/forged-wire coverage in `PeerProtocolTests`, and terminal cleanup coverage in `PeerHostTests`. Existing tests remain intact. Tests cover admission/rejection agreement, stale session/revision, immutable cached reads, provider fencing, EN/RU reasons, legacy constructors and STJ/Newtonsoft roundtrips.
- Binary compatibility: an executable compiled against the foundation Core DLL successfully invoked both original constructors and both generated `Deconstruct` methods after replacing that DLL with the current build, without recompiling the consumer. Evidence: `artifacts/flowline-f11/binary-compatibility.json` and `binary-consumer.cs`.
- Independent review identified and rechecked the constructor compatibility and terminal projection issues. Final source/diff review found no remaining issue in this bounded contract change. Separate physical runtime acceptance remains recorded against the foundation; it is not relabelled as F11 candidate evidence.
- U01 dependency remains explicit: current UI binding regenerates identity from labels, so the full NetworkExperience binding/compilation path with multiword localized labels is still invalid. The UI owner is fixing that shared path. The new parcel availability element uses a valid existing binding label and is tested through `CreateBindingContext`. F12/F18 UI/runtime acceptance is still incomplete.

## F16-a: selected stack quantity

Owner: game-save adapter and physical chest access. `FlowSendCommand.Quantity` is additive and nullable; the original constructor/deconstruction and whole-stack behavior remain. The source fingerprint and quantity are checked under the lease, selected cargo XML is prepared without changing the source quantity, and extraction performs one slot replacement with an untagged remainder. Version 2 of the game aggregate retains the complete source quantity; version 1 whole-stack saves migrate explicitly. Core custody transitions, checkpoint and receipt formats remain unchanged.

- `./tools/hatifect-check --platform`: **PASS**, 1,107 .NET + 297 Python tests (`run-o7zrqs5e`), including 659 Flow and 92 Stardew cases.
- `./tools/hatifect-check`: **PASS**, 929 .NET + 297 Python tests (`run-ogcutlsc`).
- `./tools/hatifect-agent-check --host`: **PASS**, project config/skill loaded, 217 skills discovered. The initial sandboxed host launch exited before audit; the same canonical audit succeeded outside the sandbox without changing settings.
- Added 24 `PartialStackTests` cases, 2 EN/RU quantity form cases, 2 quantity wire cases and a partial quantity variant of the existing stale-on-mutex test. Exact post-write checks distinguish a harmless throwing observer from mutation of the same remainder object; unknown outcomes survive reload without physical replay. Tagging failures retain a saveable fenced aggregate, including when no transfer exists to reconcile.
- Independent reviewer found no actionable regressions in the bounded quantity/custody/migration/consumer diff and independently confirmed the actual final Flow/Stardew TRX, including both tagging-observer cases and legacy JSON without the new field.
- Fresh isolated deployment: `run-lj6ytpwy`. Packaging tests are explicitly SKIPPED by live preparation; separate checks above are the test evidence, and no RC promotion is claimed.
- `./tools/hatifect-smoke flow.chest.roundtrip`: **PASS**, request `d60b0e0c-6d5f-4560-b07c-37bf28b1c906`, 8 checks, 347 frames, 3 loads, 2 actual Saving/Saved pairs, process exit 0 and no errors. Whole8 plus partial13→5+8 preserved total21, the exact untagged source remainder and delivered item fidelity; repeated delivery was rejected. Flow runtime fingerprint: `4b56cbc49bfcdfae5332e67e0db9818aa0707a64fdb4dafc177603a843d356c9`.
- `python3 tools/release.py verify-package .smapi-test/isolated/Mods/Hatifect`: **PASS**. The generated acceptance report was byte-matched to the retained runtime artifact before removing only that generated report. Current package inventory/archive are in `artifacts/flowline-f16/`.
- Full process-crash/reload and production A→B→A acceptance remain incomplete. The current executor launches one process and cleans its owned save on exit; controlled multi-process continuation needs a separate harness increment and must preserve failure reporting for unexpected exits.

## Combined UI SDK/editor and Flow foundation integration

The integration branch `codex/roadmap-implementation` combines `31aaa0b` with Flow foundation `cdf9d2e`, using UI package version `1.0.0-alpha.31`. The following evidence belongs to `/private/tmp/hatifect-ui-next-ten-slices`, independently from the historical Flow checks above.

- Preserved the typed form factories, committed/draft state, optional standalone/Terminal/HUD host API, themes, textures, live asset reload and editor functionality. Added the Flow external validation source and explicit element-ID overloads without changing the frozen `IUiSemanticSurfaceApi` v1 source (SHA-256 `9de29d5a7c53964ae192efb4f4f7590e1dc75a64d2fe94605f86dac3861c6335`). Both consumers require the exact combined package version through the shared package authority.
- Current-draft `IsValid` now shares validation semantics with `Apply` without publishing errors or committing values. Screen guards cover the SDK hosts as well as overlays. Foreign disposal makes a native menu inert immediately and retires only its own slot when the owner screen resumes. Failed watcher subscriptions and teardown retain retryable cleanup.
- `./tools/hatifect-test ui --platform`: **PASS**, 370 tests, `artifacts/validation/run-33413xw6/summary.json`. Six new cases exercise draft validation, mutation/reentrancy, foreign slot retirement/replacement and partial watcher subscription/removal failures. Independent read-only review rechecked the actual native menu guards and all three fixes; no open findings in that scope. Helper tests do not establish actual split-screen visual/input acceptance.
- `./tools/hatifect-check`: **PASS**, 991 .NET + 301 Python, `artifacts/validation/run-r03g7g_3/summary.json`.
- `./tools/hatifect-check --platform`: **PASS**, 1,147 .NET + 301 Python, `artifacts/validation/run-vqyw7gtd/summary.json`.
- `./tools/hatifect-isolated-ui-ca --keep`: **PASS**, 43 projected files, eight freshly built alpha.31 packages, 80 tests, two CA DLLs and no UI source. Projection and TRX retained at `/private/var/folders/ly/sph907ln7bv1tc3dxmflc62h0000gn/T/hatifect-ui-ca-isolated.wyewblmo`.
- `./tools/hatifect-agent-check --host`: **PASS**, project config and skill loaded, 217 skills discovered.
- Earlier `run-i5fgi7bx` failed on the fixed release version assertion; the assertion now requires alpha.31. `run-z_79vs4k` stalled under Rosetta in native process creation; its owned process was sampled and terminated, and that run remains **FAIL**. The successful gates above were new executions after the review fixes.

U01 remains **IN_PROGRESS**: explicit builder IDs are not yet preserved by compiler binding, and localized multiword Flow Network labels still fail `CreateBindingContext`. The next U01 acceptance must exercise the real `NetworkExperience` through binding and compilation. Combined runtime evidence is recorded below; diagnostics lifecycle smoke does not prove that Network screen works. No additional roadmap ID is marked DONE by this integration.

### Native menu callback correction after merge 6b381c0

The first combined `semantic.lifecycle` request `9ff9a650-5342-47f2-bf51-87badd02e018` exposed a real teardown defect before publication. The native log reported a Rosetta synchronous-exception error and the sampled stack repeated two frames. Inspection of the installed game IL established the cause: `Game1.set_activeClickableMenu` calls the previous menu's `IDisposable.Dispose` before storing the replacement. Retiring the slot from inside that callback recursively called the same setter. Canonical cancellation preserved the request as **BLOCKED** (exit 130); the discovered implementation defect is **FAIL**, not an environment-only failure.

`Retire` now only marks the menu inert. Owner-screen callbacks perform deferred exact-instance cleanup under a reentrancy guard with retry after a failed write. `./tools/hatifect-test ui --platform`: **PASS**, 372 tests, `artifacts/validation/run-jxmzuv1b/summary.json`. Two new tests model disposal before native slot assignment and recursive/failed deferred clear. Independent review verified the fix against `artifacts/ui-flow-integration/game-menu-setter.il.txt` and the real `exitThisMenu` ordering. The initial review covered helpers and wiring but missed the setter callback; the corrected review does not substitute for a new game run.

The corrected candidate passed `./tools/hatifect-check --platform`: **PASS**, 1,149 .NET + 301 Python, `artifacts/validation/run-icy5beoe/summary.json`. Fresh `./tools/hatifect-isolated-ui-ca --keep`: **PASS**, eight alpha.31 packages, 43 files, 80 tests and two CA DLLs without UI sources; retained projection `/private/var/folders/ly/sph907ln7bv1tc3dxmflc62h0000gn/T/hatifect-ui-ca-isolated.lbrr_99z`, complete log `artifacts/ui-flow-integration/final-isolation.log`. The correction changes only private Stardew menu ownership and its regressions; host-free code and the previously checked API contract are unchanged.

### Fresh combined runtime, candidate 803c909

- `./tools/hatifect-ui-test semantic.lifecycle`: **PASS**, request `d95c475d-3c45-417d-a72e-3093e1ef45ee`, all three checks and no exceptions. The newly built isolated candidate rendered its diagnostics surface, held one focused node, and closed/reopened with one owner. The screenshot was inspected: Diagnostics/Inspector controls and the focus outline are visible. This validates the lifecycle regression; it does not cover the full locale/input/scale or split-screen matrix.
- `./tools/hatifect-smoke save.bootstrap`: **PASS**, request `5146ce6c-2feb-451b-a196-d6520b35b466`. Stardew created and reload-verified a synthetic fixture in this worktree's isolated save root. Real user saves were not imported.
- `./tools/hatifect-smoke flow.chest.roundtrip`: **PASS**, request `46967acd-30bb-4c93-89ff-ce9c1b95c9e3`, all eight checks, 347 frames, three loads, two Saving/Saved pairs and no errors. The production session retained real chest cargo across saving/loading and did not duplicate delivery. Evidence is under `artifacts/runtime/<request-id>/result.json`; exact counts are in `diagnostics/flow-chest-roundtrip.json`.
- CA's actual third-party mod was absent in this isolated run and the adapter disabled itself normally. These scenarios do not claim CA visual/runtime acceptance. Q01–Q03 and the known U01 Network binding gap remain open.

Next integration is the separately verified Flow F11 sequence `e31c1ef` and `ff43d0b`; then U01 preserves explicit identity/alias/label through the real Network binding/compile path and adds the complete typed graph/wire acceptance. The UI task remains the single `develop` integration owner.

## Flow task integration of UI foundation with F16-a

The Flow task merged published UI foundation `3f11e5d` into its published F16-a `54c5af5`. Only the status document conflicted; all source/code bytes from the incoming UI change match that reviewed upstream commit, and both owners' evidence sections remain. This integration does not replace either runtime candidate's identity.

- `./tools/hatifect-check --platform`: **PASS**, 1,203 .NET + 301 Python, `artifacts/validation/run-v86kdbsx/summary.json` (659 Flow + 92 Stardew).
- `./tools/hatifect-check`: **PASS**, 1,016 .NET + 301 Python, `artifacts/validation/run-5itzrxbq/summary.json`.
- `./tools/hatifect-isolated-ui-ca --keep`: **PASS**, 43 projected files, eight alpha.31 packages, 80 tests, two CA DLLs and no UI source. Retained projection/TRX: `/private/var/folders/ly/sph907ln7bv1tc3dxmflc62h0000gn/T/hatifect-ui-ca-isolated.l_zpwa5h`.
- `./tools/hatifect-agent-check --host`: **PASS**, project config/skill loaded, 217 skills discovered.
- Final source/staged review: `artifacts/flowline-ui-integration/`. Runtime evidence for the full next candidate will be generated by the next F16 crash slice; earlier normal roundtrip evidence remains scoped to its recorded fingerprint. No release or new UI acceptance is claimed by this task-branch integration.
