# Проверка клавиатуры, мыши и текстового ввода

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Status: **IN_PROGRESS**; full native acceptance is not complete. Implementation [`14db772`](https://github.com/ihatectf/Hatifect/commit/14db7729ff0f28147d9ac6c6d1e5a7db97defba6) starts from `1872c7a`, UI alpha.39. Integration `fe5b10bed13bb69f799ac0caa60ab8cfb26cce61` preserves published alpha.40 `c1de25760a5bc5752b773c92de10a4a10df53a10`: all 29 incoming files matched that exact parent before this report was added. Follow-up source is [`edad4b1`](https://github.com/ihatectf/Hatifect/commit/edad4b1e89c11860290b21ad74b5f07314e892a8). Full «Проверка текущей сборки в игре» remains IN_PROGRESS.

Ownership: the GQ task owns integration, the common harness and cross-system acceptance. Product implementation remains with the separate UI and FLOWLINE tasks. The bounded Search/Tab and Backspace fixes here follow direct user reports during «Проверка текущей сборки в игре» observation; they do not transfer ownership of subsequent UI/Flow roadmap slices.

## Defects observed

The user reported that Tab did not expose Search and text could not be entered. The native capture confirmed that Tab moved from the Inspector navigation button into a row representing the inspected Terminal Utility slot. Inspector used each captured node's live ID directly as the identity of its inspection entry. This aliases the actual navigation button with its representation in the Inspector collection, so sequential focus interprets the navigation button as a collection item.

The owning DevTools projection now gives each inspection entry a stable ID under its Inspector Experience, retaining the original captured node as the row value. Selection resolves the row through the existing bounded metadata index; details and source reveal still use the original captured node. Filtering preserves row IDs. This follows the existing globally scoped identity contract and introduces no new public API, collection identity scheme or persistence format.

Separately, the shared Runtime render planner drew an empty input's empty value without a visible name. It now draws the semantic name as a hint while the value is empty. Editing, accessibility value, caret, selection and scrolling continue to use the actual value. Typing suppresses the hint; clearing restores it. The change adds no extra primitive or traversal, and no consumer-specific layout or color.

The user subsequently reported that Backspace did not delete text. Read-only inspection of the installed Stardew 1.6.15 `KeyboardDispatcher` established that Back/Tab/Enter are delivered as command characters (`\b`, `\t`, `\r`). The shared Stardew keyboard lease ignored that callback. It now normalizes those commands into the existing input adapter and ignores their duplicate special-key callback. Other special keys still follow their normal path. Owning screen, selection and exact subscriber guards remain in force. Both menu and overlay use this lease. The decompiled local game type is retained only in ignored artifacts and is not distributed.

## Native observation fixture

`rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-ui-test semantic.input.native` prepares a fresh isolated deployment and submits it to the canonical worktree executor. The optional scenario is excluded from `all` and requires the isolated world. It observes input; it never invokes menu input handlers or sets the search text itself.

The sequence is: click Inspector, press Tab, click the visible Search field, type the displayed `native-<request prefix>` exactly, then press Backspace once. Every phase requires two distinct stable completed frames, active game focus and valid visible geometry. Mouse press/release and Tab counters come from SMAPI; Tab must focus the actual text field. The text must match this request's probe and be focused. The final phase requires a new SMAPI Backspace event and exactly one removed final character. Repeated or older frames cannot confirm a phase.

For each accepted phase the fixture retains a completed-frame screenshot and a separate UI-layer screenshot (up to five pairs), bounded observations, and `native-input-progress.json` at phase transitions. Each phase has a bounded update wait; the overall request timeout is 1,800 seconds. Failure retains its first cause, removes subscriptions and requests owned-game exit. No observation work runs in the aggregate performance window.

Follow-up `edad4b1` corrects a native-fixture defect: the recursive geometry predicate rejected zero-area descendants of a legitimate empty search result. Native acceptance now checks finite visible root/field geometry and clip intersection; an empty sibling list cannot invalidate the field. Fresh SMAPI events, exact text, focus, active game, viewport bounds and two completed frames remain required. A single last observation is retained and the atomic progress file is refreshed every 60 completed native frames, so a stalled phase has inspectable current text/counters/geometry. This bounded diagnostic work is excluded from aggregate performance measurement.

These correlated game events and rendered outcomes establish the exercised native input path. SMAPI does not distinguish physical hardware from OS-injected input. The external observer's provenance must accompany a result; a direct transport check, screenshot or partial manual interaction alone cannot certify physical-device acceptance.

## Reproduction and regression evidence

| Requirement | Evidence |
|---|---|
| «При нажатии Tab не вылезает search и невозможно ввести текст» | `InspectorTests.TerminalInspectorTabReachesSearchInsteadOfAnInspectedNavigationRow`: real Terminal click → one focus step → editable field → query/filter/render. Original FAIL expected field identity, actual null; corrected Inspector suite PASS7. |
| Empty Search is visible without becoming editable content | `AccessibilityTests.EmptyTextFieldShowsItsNameWithoutInsertingHintIntoEditableState`: original FAIL expected Search, actual empty; checks hint, empty source/accessibility value, caret, edit and clear. Corrected Accessibility + initial native gate PASS15. |
| «не работает удаление текста на Backspace» | `ScreenOwnershipTests.KeyboardCommandCharactersDispatchExactlyOnceAndRespectOwnership`: original FAIL3 for Back/Tab/Enter; corrected full ScreenOwnership suite PASS18. Verifies one command, no duplicate, unknown input ignored, and refusal after screen/subscriber loss. |
| Native evidence cannot substitute stale events, hidden/unfocused targets or a different request's text | Seven `UiNativeInputGateTests` facts, including `BackspaceRequiresANewEventAndExactlyOneRenderedDeletion`: PASS7 before the geometry follow-up. |
| Existing harness contracts continue to hold | Initial tools gate `run-nzir8jgl`: PASS351 Python; after adding Backspace, focused `test_live_harness.py`: PASS46. These precede incoming alpha.40's additional tests and retain their own source identity. |
| Empty results preserve visible editable Search; invisible targets remain invalid | Extended real Terminal Inspector regression PASS7; final native gate suite PASS13, including `EmptyResultsDoNotHideTheVisibleRootOrSearchField`, four empty/clipped boundary cases and `OverflowingAreaCannotConfirmInput`. |

Exact commands, clean summaries and TRX files are retained under `artifacts/q01-native-input/`. The test-generation scope expanded after the user reports; ignored `.testagent/research.md`, `plan.md` and `status.md` record the target map and review. Static Roslyn pairing matched the existing suites; it is not coverage. Source/assertion review with test-gap-analysis and assertion-quality found the bounded regressions protected, with native provenance left to actual observation. No synthetic mutation score, independent review or test-count-based completeness claim is made.

Four earlier alpha.39 native attempts remain **FAIL**, each with owned process exit 0 and no teardown errors:

| Request | Duration | Last phase reached |
|---|---:|---|
| `5252cabe-2d22-46f3-85bd-4acc42a3f6d7` | 103.691 s | Pointer recorded; keyboard/text absent |
| `a8e09acd-cbad-4059-a780-4a0acd6542e7` | 97.483 s | Old keyboard check accepted a different focus target; text absent |
| `e9ee554f-bfad-4097-add9-7aa62f3c4fa9` | 73.082 s | Corrected Search/Tab visible; typed text had additional characters; exact text absent |
| `b72df5d8-e8db-476a-b1bf-c032b621f845` | 90.382 s | Search focused; exact text absent, preceding keyboard-command fix |

The first two attempts helped expose the incorrect focus check; the final fixture specifically requires text-field focus. The latter two confirm partial UI behavior by native screenshots and user reports, but do not become full PASS. CUA action attempts encountered changed or closed windows and are not counted as completed input injection. Raw progress, screenshots and failure results retain their original candidate identities.

Three later alpha.40 attempts also remain FAIL: `5da6d7d4-a339-4936-9477-c927bb73014f` (278.932 s, exact probe absent), `9958c0cf-8b45-4cfe-8183-e32405532852` (337.301 s, Text timeout), and `e3ad069b-fc5b-4d7b-b6fd-054c580d6d42` (132.523 s, stopped after diagnosis). In `9958c0cf`, CUA successfully entered the exact text and a separate Backspace visibly removed its final character; these actions do not turn an incomplete host result into PASS. In `e3ad069b`, the added last observation recorded exact `native-e3ad069b`, focused visible field, fresh pointer press/release and `ValidGeometry=false`, exposing the recursive empty-result predicate. CUA coordinate actions intermittently returned `noWindowsAvailable`; successful and rejected actions are distinguished in the observation transcript. No alternative event-injection mechanism was used.

## Final acceptance

The final native request `753737ca-a8b1-4e50-ade5-9f6d9e3619f0` ran against `edad4b1` after fresh prepare `run-t4gh22s1`. It recorded **PASS for pointer, Tab focus and exact text**. `native-753737ca` rendered in focused Search on completed frame 2778 with fresh SMAPI press/release, valid root/field geometry and an empty result list. CUA `BackSpace` visibly produced `native-753737c`, but SMAPI's `BackspacePressed` remained zero. The probe was restored and physical Backspace was requested; no qualifying physical event was recorded before the bounded timeout. Consequently the overall scenario remains **FAIL**, specifically `semantic.input.native.backspace`, duration 387.487 s. Owned PID85484 exited 0 with no teardown errors. This is not full native or physical-device PASS.

Environment: UI alpha.40, Stardew `1.6.15 build 24356`, SMAPI `4.5.2`, Dark theme, 1280×720, scale 1, ordinary game GC. Runtime fingerprint: `6843303da6f70fd35156a2218f26035d22af937082c6eb3a812fa145968984ca` (`sha256-runtime-v2`). Build commands alone use command-local `DOTNET_gcConcurrent=0` for the existing Rosetta toolchain condition; no personal runtime setting changes.

| Combined source check | Result |
|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | PASS, `run-tqxp9awg`: 1,377 .NET and 355 Python, zero failures/skips. Seven TRX files were separately checked against every actual result. |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | PASS, `run-ft4t9bl2`: 1,618 .NET and 355 Python, zero failures/skips. Ten TRX files were separately tallied against every actual result. |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | PASS90; retained `hatifect-ui-ca-isolated.naugiqjf`: 44 exact projected files, eight alpha.40 packages matching producer DLLs, three isolated assets files, two exact CA DLLs and no UI source tree. |
| Fresh UI/CA aggregate through `rtk proxy ./tools/hatifect-live-runner ui all …` | PASS28, request `43a63e05-9399-4f98-a8b6-e331685e0e70`, 32.134 s, same UI fingerprint as native. Eight matrix states, settings restored, 27 passing host checks plus performance budget, null terminal error. PID88887 exited0 without teardown errors. |

Next acceptance step: finish a native request with a qualifying physical Backspace event, retain its completed-frame deletion capture and explicit input provenance. Original matrix, Flow and performance evidence retain their earlier candidate identities. Normal Mods, real saves and golden assets are unchanged.

Aggregate measured620 frames: p95/p99 UI-thread cost0.037958/0.271708ms, steady allocation4356.981B/frame, measure/arrange miss ratios0. No build, CUA or image audit overlapped this window. Native observation is excluded from aggregate. All12 changed source/test/tool files still match the source hash index after C/G/P and runtime. This is an observed budget result for the captured candidate, not a controlled benchmark of every update shape.


## Integration with the independently published alpha.41

The first atomic push found a newer develop and failed without updating either ref. Verified alpha.40 checkpoint `4b502ed` was then published on `feature/hatifect-roadmap`. Exact incoming `6ef1b1257d1d0e7089abad2d92f45a827fac73cc` (UI owner implementation `8a45917`) was merged as `9006131204d463cf2ce92b36386043b4431fb568`. Its26 non-conflicting files remain byte-identical and both roadmap histories are retained. No additional product implementation was added here. The combined source index contains37 non-Markdown inputs.

C `run-ha3cenlb` passes1,396 .NET +355 Python; G `run-remfjoux` passes1,637 .NET +355 Python. All17 actual TRX suites were separately tallied with zero failures/skips. This includes the owner's19 retirement cases together with the input regressions. P is PASS90 (`hatifect-ui-ca-isolated.mjczvzxq`): 44 exact projection files, eight alpha.41 packages matching producers, three isolated assets files and two exact CA DLLs. The partial physical-input evidence above retains alpha.40 identity and full «Проверка текущей сборки в игре» remains IN_PROGRESS.


Fresh combined runtime outcomes:

| Scenario | Request | Result |
|---|---|---|
| First `hatifect-ui-test all` | `bea6c556-21fc-4431-9720-ebead73cd5bc` | FAIL,20.477s: valid UI tree/probe but load fade remained1.0204 while the game was paused outside focus; no matrix/performance acceptance. |
| UI/CA aggregate with isolated out-of-focus pause disabled | `291142fa-49ac-4ce5-97e6-da7c6086ec1b` | PASS28,31.695s; eight matrix states, restored settings, null terminal error. |
| `flow.route.basic` | `d406faee-9c96-407b-85a1-6e0a1229224e` | PASS6,11.256s; fake delivery/pause/idle/lifecycle/reload. |
| `flow.save.isolation` | `b0a216f2-8275-4db3-ae95-95b534f638ab` | PASS7,10.545s; separate-save fake session isolation. |

UI fingerprint is `bbefa735a14038526ff960f6fe939c77bd9ae9dbaeed69bd06445965855dca00`; both Flow runs use `452b30c788ed0768ea815e84ffa3755553a1027f030d1d4ceeac727b2a469434`. The first aggregate failed an environmental precondition and is retained as FAIL. Only the two isolated startup/default-options files temporarily changed `pauseWhenOutOfFocus` from true to false; original bytes and hashes are retained and both files were restored exactly after the last run. Normal configuration and golden saves were not edited. All four owned processes exited0 without teardown errors, and the reused executor is Ready.

The passing aggregate measured620 frames: p95/p99 0.035916/0.274375ms,4349.884B/frame, zero measure/arrange misses. No build/CUA/image audit overlapped its measurement. All37 combined non-Markdown inputs still match commit `9006131`. The physical native Backspace check remains pending with its earlier FAIL; no alpha.41 native-input PASS is claimed. The next GQ acceptance step is physical-input completion under explicitly recorded focus conditions; UI/Flowline product ownership remains separate.
