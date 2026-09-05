# Roadmap implementation evidence

Date: 2026-09-06. Working branch: `codex/flowline-next-ten-slices`; source baseline: `2f08a4f4db36ef0b1ba58e385fc0ab5044cae867`.

The authoritative scope is [ROADMAP.md](ROADMAP.md), restored verbatim from commit `1e741ef` because this task branch predates that documentation commit. The earlier exploratory Flow multiplayer/adapters/policies list is superseded as an execution plan; no new product work outside the approved roadmap is being added. It does not replace U01–U10, R01–R04, T01–T03 or their acceptance. The Flow foundation is committed and published in task branch as `cdf9d2e86e90f0174251243147f8a2f9480d14e8`; its integration with the verified UI SDK/editor/D01 is owned by the UI task. The full roadmap IDs remain incomplete until all their acceptance and integration evidence are present.

## B01: current gaps

| IDs | State | Authoritative implementation/evidence | Remaining acceptance |
|---|---|---|---|
| B01 | IN_PROGRESS | Source and contract inventory below; current host-free and platform checks PASS | Commit this inventory and integrate the UI owner baseline |
| B02 | IN_PROGRESS (UI owner) | Shared contract owned by the UI task in codex/roadmap-implementation; this task supplied Flow consumer requirements | Integrate verified D01/U01 commits without competing UI implementation |
| U01 | IN_PROGRESS | Existing typed sources and explicit builder IDs; discovered binding regenerates IDs from localized labels | Preserve identity in binding; typed relation graph; negative diagnostics; Flow/CA fixtures |
| U02 | PLANNED | Collections already have internal revision and stable-ID selection; scalar UiState has no public version | Version/publication contract, typed deltas/reset and atomic related-source publication |
| U03–U10 | PLANNED | Current actions are synchronous; deterministic planner/profiles, layout/input/virtualization and form validation already exist | All specified async/environment/authoring/theme/component/extension acceptance remains required |
| R01–R04 | PLANNED | Existing single-asset Last Known Good and retryable resource teardown | Generation ownership, bundle preparation, migration and multi-host commit/rollback |
| T01–T03 | PLANNED | Existing Tooling/Server, symbols/diagnostics and DevTools | New metadata/protocol, preview matrix, editor workflow and examples |
| F11 | VERIFIED, integration pending | Immutable `FlowSnapshot`, typed commands/session/revision, observer fencing; provider mode, supported operations, owner-derived action availability and stable reasons; F11-a/b evidence below | Publish F11-b and integrate both commits with D01/UI baseline |
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

Publish F11-b and implement F16 partial-stack acceptance. The UI task owns B02/U01–U10/R/T on codex/roadmap-implementation (ready SDK/editor commits 55ccc3b, 55b8360; B02/D01 contract 1fd9d57). This task owns F11–F20 and real-game acceptance, integrating verified UI commits. F18 ordinary player entry follows integration of the standalone host and U01 identity fix. Multiplayer remains disabled and its exploratory foundation is not a released capability.

## F11-b: owner-derived availability and rejection reasons

Owner: Flow Core/Application and Dispatch. The D01 contract supplies provider mode, supported operations and an immutable five-action availability set with stable rejection codes/reason keys. Reservation projection and execution share route, queue and capacity admission. Semantic consumers localize those reasons in EN/RU and explicitly identify the diagnostic provider. Game sessions and the disabled peer wire preserve the metadata; terminal peer projections release their previous live contents. No persistence envelope or physical inventory/custody format changes.

- `./tools/hatifect-check --platform`: **PASS**, 1,076 .NET + 297 Python tests (`run-ub2txdqz`).
- After retaining the original public constructors and deconstruction signatures, `./tools/hatifect-check`: **PASS**, 927 .NET + 297 Python tests (`run-vh75lnho`).
- Final `./tools/hatifect-test flow --platform`: **PASS**, 657 Flow + 65 Stardew tests (`run-t2mgi20d`), including terminal peer disposal and actual parcel binding-context creation. These are the bounded code deltas after the full platform check.
- Added 11 `FlowAvailabilityTests` cases, serializer/forged-wire coverage in `PeerProtocolTests`, and terminal cleanup coverage in `PeerHostTests`. Existing tests remain intact. Tests cover admission/rejection agreement, stale session/revision, immutable cached reads, provider fencing, EN/RU reasons, legacy constructors and STJ/Newtonsoft roundtrips.
- Binary compatibility: an executable compiled against the foundation Core DLL successfully invoked both original constructors and both generated `Deconstruct` methods after replacing that DLL with the current build, without recompiling the consumer. Evidence: `artifacts/flowline-f11/binary-compatibility.json` and `binary-consumer.cs`.
- Independent review identified and rechecked the constructor compatibility and terminal projection issues. Final source/diff review found no remaining issue in this bounded contract change. Separate physical runtime acceptance remains recorded against the foundation; it is not relabelled as F11 candidate evidence.
- U01 dependency remains explicit: current UI binding regenerates identity from labels, so the full NetworkExperience binding/compilation path with multiword localized labels is still invalid. The UI owner is fixing that shared path. The new parcel availability element uses a valid existing binding label and is tested through `CreateBindingContext`. F12/F18 UI/runtime acceptance is still incomplete.
