# Ordinary Window input observation

Status: **implementation/scoped/review PASS; combined native input acceptance pending**. Owner source `c00266fd18876c8a2ca329e901e3512c1c969f58`. This is a bounded M3 prerequisite under U05/U06; full U05/U06/U07 and the input/scale/controller matrix remain open.

The observer attaches only in the exact automated TestHarness environment for `flow.ui.player.input`. It observes the actual active semantic Window after its normal production trigger. It does not open a menu, dispatch an action, type text, write source state, or synchronize layout. Flow owns the trigger, intended field, commands, inventories and final domain checks. UI owns semantic geometry, focus, native input correlation and completed frame evidence.

## Initial probe

The sequence is Ready → Pointer → Text → Backspace → Tab → Complete. Ready accepts an already visible Window with a text field. A new SMAPI mouse press and release must land inside the fully visible, enabled focused text field. The exact probe is `native-` followed by the first eight hex characters of the request GUID. Text also requires a fresh callback from the native keyboard subscriber: source writes and `InsertAutomationText` cannot satisfy this counter. Backspace requires a new SMAPI Back event and exactly one removed final character. Tab requires a new SMAPI Tab event and a different visible focus target.

Every phase requires two fresh completed frames with identical accepted scene/frame, counter, focus and text observations. Replacement before completion restarts the probe; a predecessor's events cannot complete a successor window. Hidden or stale frames break stability. Active portals cannot satisfy the root Window gate. Public API signatures are unchanged; native subscriber instrumentation is an internal optional event with no subscriber outside this exact scenario.

## Evidence format and limits

`diagnostics/ui-window-input-progress.json` uses protocolVersion 1 and camelCase properties. It carries runId/scenario, expectedText, phase, completedFrame, failure, latest state and captures. The declared origin is `os-injected`, as agreed for the external CUA controller; SMAPI counters do not prove hardware origin.

A visible latest state contains surfaceEpoch, experience, acceptedSceneVersion/frameVersion, renderedSceneVersion/renderedFrameVersion/renderSequence, viewport, focused element, input observation and elements. Elements include nodeId, semanticId, actionId, role, name/value, enabled/focused, bounds and clip. Bounds are UI coordinates with exclusive right/bottom. Collection rows retain nodeId; not every row has an independent scene semanticId.

Capture occurs in a GameRunner component after world/UI composition, with no bound render target and a fresh matching completed UI render. Each retained capture records its immutable state, optional completed phase, composed PNG and separate UI-layer PNG. Stable accepted-state changes continue to be captured after the initial probe and across normal close/reopen, including repeated Result text in different accepted publications.

Limits are 1024 scene/accessibility nodes, 4096 characters per element name/value, and 128 retained capture pairs. Exceeding a limit produces an explicit failure and detaches the observer. Ordinary disposal removes input subscriptions, the GameRunner component and the previous menu's native text subscription. The observer persists through same-process title/load transitions for the request; process restart remains a separate acceptance step.

Final Flow acceptance must require `failure == null`, `phase == Complete`, matching run/scenario and one surfaceEpoch across initial Pointer/Text/Backspace/Tab captures. It must additionally verify its expected semantic field and command-specific rendered/domain results. Initial Complete alone does not prove the entire route, controller input, hardware origin, full process restart, or locale/scale matrix.

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
