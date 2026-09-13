# Ordinary Window input observation

Status: **implementation/scoped validation PASS; current common and native acceptance pending**. Initial owner source `c00266fd18876c8a2ca329e901e3512c1c969f58`; current local candidate `b23f54c96aee60a8a27f4ec73bb006e9d58a873a`. This is a bounded M3 prerequisite under U05/U06; full U05/U06/U07 and the input/scale/controller matrix remain open.

The observer attaches only in the exact automated TestHarness environment for `flow.ui.player.input`. It observes the actual active semantic Window after its normal production trigger. It does not open a menu, dispatch an action, type text, write source state, or synchronize layout. Flow owns the trigger, intended field, commands, inventories and final domain checks. UI owns semantic geometry, focus, native input correlation and completed frame evidence.

## Initial probe

The sequence is Ready → Pointer → Text → Backspace → Tab → Complete. Ready accepts an already visible Window with a text field. A new SMAPI mouse press and release must land inside the fully visible, enabled focused text field. The exact probe is `native-` followed by the first eight hex characters of the request GUID. Text also requires a fresh callback from the native keyboard subscriber: source writes and `InsertAutomationText` cannot satisfy this counter. Backspace requires a new SMAPI Back event and exactly one removed final character. Tab requires a new SMAPI Tab event and a different visible focus target.

Every phase requires two fresh completed frames with identical accepted scene/frame, counter, focus and text observations. Replacement before completion restarts the probe; a predecessor's events cannot complete a successor window. Hidden or stale frames break stability. Active portals cannot satisfy the root Window gate. Public API signatures are unchanged; native subscriber instrumentation is an internal optional event with no subscriber outside this exact scenario.

## Evidence format and limits

`diagnostics/ui-window-input-progress.json` uses protocolVersion 1 and camelCase properties. It carries runId/scenario, expectedText, phase, completedFrame, failure, latest state and captures. The declared origin is `os-injected`, as agreed for the external CUA controller; SMAPI counters do not prove hardware origin.

A visible latest state contains surfaceEpoch, experience, acceptedSceneVersion/frameVersion, renderedSceneVersion/renderedFrameVersion/renderSequence, viewport, focused element, input observation and elements. Elements include nodeId, semanticId, actionId, role, name/value, enabled/focused, bounds and clip. Bounds are UI coordinates with exclusive right/bottom. Collection rows retain nodeId; not every row has an independent scene semanticId.

Capture occurs in a GameRunner component after world/UI composition, with no bound render target and a fresh matching completed UI render. Each retained capture records its immutable state, optional completed phase, composed PNG and separate UI-layer PNG. Stable accepted-state changes continue to be captured after the initial probe and across normal close/reopen, including repeated Result text in different accepted publications.

Limits are 1024 scene/accessibility nodes, 4096 characters per element name/value, and 128 retained capture pairs. Node and text overflow produce an explicit failure and detach the observer. The capture limit is a hard storage bound with two classes: the five completed probe phases and five scenario action results are required; other stable publications are optional. Ten slots stay reserved until those required keys arrive, so every expected action result increases the retained list length used by `waitCapture`. Optional captures stop accumulating at the current reserved boundary. A required capture beyond the reservation may evict the oldest optional pair; both superseded PNGs are then deleted. The observer fails closed only if all 128 retained pairs are required. Duplicate required keys do not consume capacity. Ordinary disposal removes input subscriptions, the GameRunner component and the previous menu's native text subscription. The observer persists through same-process title/load transitions for the request; process restart remains a separate acceptance step.

Final Flow acceptance must require `failure == null`, `phase == Complete`, matching run/scenario and one surfaceEpoch across initial Pointer/Text/Backspace/Tab captures. It must additionally verify its expected semantic field and command-specific rendered/domain results. Initial Complete alone does not prove the entire route, controller input, hardware origin, full process restart, or locale/scale matrix.

## Capture-retention follow-up

Exact ordinary-input request `7470e72d-470d-4c28-945a-b7cc0bdd4911` on source `1c1897e4520db313e43811a9cd24dcaf31b6e8b4` completed the production-keybind Window path: two station registrations, route creation, whole and partial sends, save/load, and a reopened Delivered state were present in the retained Flow/UI state. The run still ended **FAIL** because the observer had already retained 128 incidental stable frames and set `failure = "Window input capture budget exhausted."` before final host checks. That result is causal evidence for the retention defect; it is not acceptance of the scenario.

Local commits `311b9b1` and `756369c` introduced bounded required/optional retention and removed Result scans from unchanged optional frames. Follow-up `79412fb` keeps ten slots reserved and carries a supported focused action across a later accepted scene transition, because registration clears current focus before its Result appears. It ignores the focus publication itself and requires a non-empty fully visible Result in a later scene. Required action keys use surface epoch plus action ID, so the same register action in the two distinct Windows remains separate while repeated scene publications for one action remain deduplicated. Performance follow-up `b23f54c` restores an outer scene/action guard before enumerating elements, so optional stable frames do not scan for Result. Public API, serialized protocol shape, Flow semantics, package versions and the hard capacity are unchanged.

The focused Stardew test project on current source `b23f54c` is **PASS 107/107**. New cases cover optional saturation followed by ten required records with monotonic list growth, exact optional eviction callbacks, duplicate required keys, fail-closed all-required overflow, capture-key qualification and rejection of same-scene, empty or clipped action results. Common validation for this exact source remains **PENDING**.

Its predecessor `756369c` completed canonical platform validation `run-vi9q00y4` with **PASS: 2316 .NET + 503 Python**, including Stardew **100/100**. Isolated UI/CA projection `hatifect-ui-ca-isolated.0gfx2duy` is **PASS: eight packages, 47 projected files, 101 CA tests and two CA DLLs with UI source absent**. Final prepare `run-43vzimlv` is **PASS** with runtime fingerprint `b1dc19ae48b63a72ab61274868102c64fd3d08a57eb501c094c159e37a961f75` and candidate fingerprint `9419c443b80bbd0b56010c94b0777a4b250dd6284fd24b91bfcc701cf5e4876c`. Normalized `flow.ui.player` request `0021b62e-e953-45df-a9b2-836112570e48` is PASS 11 and exact chest roundtrip `e7c39572-bd73-475a-bc51-5bd64ea43868` is PASS 8 on that predecessor. These results establish the incoming integration state but do not transfer to changed source `b23f54c` and do not substitute for its physical-input observer run.

Fresh exact request `a8624e82-fcdb-4fdf-9a5f-a884d231e36c` on predecessor `756369c` is **BLOCKED before the first semantic step**: `CGPreflightPostEventAccess()` was false for the Python process and the driver reported missing macOS post-event Accessibility permission. The process exited 130, created no Window captures and restored both isolated runtime options without teardown errors. No physical-input request has run on current source `b23f54c`; its native result is **NOT_PROVEN**. The next acceptance step is to run the current candidate from a process identity with confirmed Accessibility trust and verify `failure == null`, `captures.length <= 128`, all five probe phases, all five action-result publications and the final Delivered `latest` state.

## Exact e9cfffc runtime checkpoint

The later local candidate `e9cfffc36922791c93d26457d3be5719378ecb16` includes `b23f54c` together with the F19 admission-feedback changes. Clean exact preparation `run-58sg01xm` and `save.bootstrap` request `5a3e51fd-8107-4978-a41b-6f8ccfdacea2` passed. Exact `flow.ui.player.input` request `b0e6af20-c6c4-4bdf-bf7f-58bd2c9f0092` then stopped before its first semantic step because `CGPreflightPostEventAccess()` was false; `currentStep` is null, the event list is empty, no Flow progress file exists, and isolated options and working-copy cleanup both passed. This request does not test the new `clickPoint`, raw K delivery, capture retention or any player action. Full identities and evidence limits are recorded in [Q02_RUNTIME_E9CFFFC.md](Q02_RUNTIME_E9CFFFC.md).

## Verification

`rtk proxy env DOTNET_gcConcurrent=0 HATIFECT_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet HATIFECT_TEST_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet ./tools/hatifect-test ui --platform`

Run `run-0i2gufzf` completed with **1036 PASS**, seven actual TRX, no failed/skipped tests. Stardew93 includes eleven new WindowInputGate executions. Runtime603 reflects this owner's base before the independent Reveal trace/gap tests; integration preserves the newer Runtime tests.

| Requirement | Evidence |
| --- | --- |
| Fresh stable frames and event sequence | Full five-phase test, duplicate/hidden frame tests, accepted-version stability test |
| Pointer ownership and location | Stale counters, release without a fresh press, wrong-coordinate negative cases |
| Native text and same field | Exact probe, wrong field/value, hidden target, source text without native subscriber event |
| Backspace and Tab | New-event and exact deletion assertions; missing/unchanged/hidden Tab target negatives |
| Window replacement | Successor epoch restarts Ready and cannot consume predecessor input |
| Actual adapter geometry, lifecycle and capture budget | Pending fresh ordinary K/CUA native run |

Exact six-file hashes, seven TRX hashes and eleven test results are retained in `artifacts/window-native-input/source-candidate.json`; schema notes and the handoff patch are alongside it. Independent GQ read-only source/test review found no concrete defect. The adapter's real coordinate mapping and complete native route are not inferred from the pure gate tests.

See [runtime workflow](RUNTIME.md).
