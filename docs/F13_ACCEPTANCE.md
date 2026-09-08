# F13: управление изолированной fake session

Статус: **IN_PROGRESS**. F13-a опубликован в [`7d62ab5`](https://github.com/ihatectf/Hatifect/commit/7d62ab509ac6987df76938c4f34612669b3de16f), база — `203788f71da542470450e1b6aebf69f4a17135fb`, ветка `codex/flow-f13-actions`. Эта запись описывает проверенный объём consumer implementation и оставшуюся приёмку; полный F13 ещё не завершён.

## Зависимости и границы

F12 принят на общем source `08ecdb9` ([приёмка](F12_ACCEPTANCE.md)). U03 typed action contract и его reload/save-switch retirement проверены на source `9b6028c0561c5e1a8225bcdec7f1bcce64d81ead`: request `02565027-d1ae-4657-9693-c1872318761c` (18 save-switch checks) и `9fb55869-dbe5-4a63-a56b-c95015a89558` (18 reload checks), fingerprint `5fc2c2111b8357967c3c2be5343b12feaa0cad4279033d5fe58a13eaa2471480`. Локальный аудит `artifacts/integration/u03-dependency-native-audit.json` сверяет request/fingerprint и 43 raw hashes. Это подтверждает входной контракт; полная общая alpha47 acceptance и публикация U03 не объявляются завершёнными.

Owning implementation F13-a — Flow.UI.Semantic. Core, persistence и публичные UI API не изменены. Reserve (UI Dispatch), Cancel и RetryDelivery используют существующий доменный контракт через typed host. ReconcileTransfer и ReturnToSource сохраняют прежнюю реализацию; новые операции и гарантии возврата груза не вводятся.

## Реализованный checkpoint

- Request захватывает publication ID/version и domain session/revision/parcel/action. Перед effect проверяется актуальность владельца и domain availability.
- Host отвергает повторный запрос во время выполнения. Consumer дополнительно закрывает reentry и запрос до публикации результата. Команда выполняется один раз.
- Исходный domain result хранится до успешного atomic Pump; ошибка projection не повторяет effect. Другие открытые projections получают revision владельца.
- Applied отображается как Success с исходным result; domain Faulted — Failure с безопасным сообщением; остальные domain статусы — Rejected с соответствующим code. Настоящее исключение сохраняется отдельно от пользовательского текста.
- EN/RU availability/messages берутся из двух ограниченных enum-sized caches. Новые строки и словари при каждом availability read не создаются; это source inspection, не PERF-замер.
- Existing Parcel lifecycle/publication/locale tests вызывают действия через настоящий owning host. Native F12 retired-action probe тоже использует action automation вместо недопустимого direct invocation typed action.

## Выполненные проверки

| Проверка | Evidence | Результат |
|---|---|---|
| `./tools/hatifect-test flow` | `artifacts/validation/run-44k3yqtv`, `artifacts/f13/scoped-audit.json` | PASS 768/768, в том числе 13 новых cases |
| `./tools/hatifect-check` | `artifacts/validation/run-_bw4rs5h`, `artifacts/f13/c-audit.json` | PASS 1713 .NET +388 Python; семь TRX, zero failures/skips |
| Независимый source/test review | агент `t01_client_review`, Sol/xhigh; scoped source и actual TRX | Доказанных дефектов нет; native/create/G не входили в review |
| `./tools/hatifect-check --platform` | `run-_qxi68gc`, `artifacts/f13/g-audit.json` | PASS 2042 .NET +388 Python; до последней коррекции ожидания disposed-handle в native probe |
| `./tools/hatifect-test flow --platform` | `run-ch9uafob`, `artifacts/f13/platform-final-audit.json` | PASS 928/928 после последней коррекции native probe |
| Native input/action/result matrix | текущий checkpoint | NOT_RUN |

Первый G `run-141445rr` собрал candidate, но VSTest был остановлен sandbox при открытии локального сокета (SocketException 13); повторный запуск с необходимым доступом завершился PASS. При втором проходе исправлено ожидание retired native handle: owning action API может бросить ObjectDisposedException, и harness сохраняет проверки отсутствия read/effect/publication. Другие исключения не скрываются.

Новые 13 cases проверяют typed metadata/owner requirement, точный command capture, шесть domain outcomes, две projections, reentry и повторный dispatch, retry существующей доставки, запрет cancel в InTransit/Arrived/Delivered, разделение exception и безопасного текста. Existing publication regressions подтверждают retry projection без повторения команды и retirement после committed effect. Исторический `run-077y5n98` завершился build FAIL из-за отсутствовавшего namespace import; исправление проверено последующими scoped/C runs.

## Оставшаяся приёмка

1. Свежий isolated native regression на immutable source; G и финальный platform Flow regression выполнены.
2. Native create использует существующие NetworkExperience shipping actions и FlowSendCommand поверх bounded diagnostic IFlowNetworkApplication façade. Прямое provisioning runtime не заменяет UI create. Production shipping создаёт parcel и пробует Reserve; отдельная Created fixture проверяет самостоятельный Dispatch.
3. В изолированной игре подтвердить create/dispatch/cancel/retry, сообщения отказа, повторный click, stale screen, running operation и обновление другой projection. Сохранить request/source/fingerprint, фактический ввод и rendered observations с EN/RU/input/scale coverage.
4. Выполнить второй проход по итоговому diff, записать commit/publish evidence и только после полной матрицы пересмотреть статус F13.

Synthetic golden fixture скопирована в отдельный `.smapi-test/fixtures` текущего worktree с проверкой hash; исходник не изменён (`artifacts/f13/fixture-copy-audit.json`). Реальные saves/Mods не используются. Параллельная общая package source-identity проблема исследуется integration owner; чужая историческая runtime acceptance не заменяет новую приёмку этого checkpoint.

## F13-b: подготовка fake create

Implementation опубликован в [`f20ad24`](https://github.com/ihatectf/Hatifect/commit/f20ad24068a72915679b771aa9a20fdc4a6e3bb6). Независимый Sol/xhigh reviewer подтвердил финальные source/20cases и actual targeted TRX: доказанных дефектов нет; найденный inactive-owner gap закрыт. Native и production chest extraction в этот review не входили.

Diagnostic `FlowUiAcceptanceWorld` предоставляет существующий `IFlowNetworkApplication` поверх того же FlowRuntime/FlowApplication. Fixture ограничена двумя станциями, одним synthetic source stack и одним lifetime create. Она проверяет session/revision, endpoints, slot, полный fingerprint, quantity, route и running-create fence до effect; создаёт реальный aggregate и вызывает TryReserve, как production shipping. Отдельные helper-ы меняют только fake destination acceptance и продвигают fake logical clock. Core/public inventory/persistence contracts не изменены. Оставшийся исходный стек не предлагается для второго create в этой ограниченной fixture; это не обещание поведения production inventory.

`run-83tshysz` — **PASS174** platform Flow.Stardew tests, включая14 новых cases; actual TRX и имена — `artifacts/f13/network-scoped-audit.json`. Проверены whole/selected quantity, точная reservation capacity, восемь invalid captures без мутации, disconnected route, reentry из revision observer, delivery rejection/retry и paused/closed owner. Timeline теста исправлен по существующему контракту: departure1, arrival9, retry10; исходные два FAIL сохранены, доменный алгоритм не менялся. G `run-okzctzow` — **PASS2056 .NET +388 Python** (10 actual TRX). После него найдены и исправлены inactive-owner guards перед PrepareNetwork/AcceptNetworkDelivery; `run-y5l7rw6h` — **PASS180**, в том числе все20 новых cases (шесть дополнительных Paused/RecoveryRequired/Closed regressions), actual evidence — `artifacts/f13/network-platform-final-audit.json`. Финальный C `run-819ojiny` — **PASS1713 .NET +388 Python**; семь actual TRX проверены (`artifacts/f13/network-c-final-audit.json`). Native create ещё не запущен и F13 остаётся IN_PROGRESS.

Следующая native интеграция — отдельный `flow.ui.actions`: exact manifest и Flow report routing, отдельный driver через ModEntry lifecycle, существующие NetworkExperience/ParcelSurface и ShowParcel, отдельные Network/Parcel observations. F12 fixed15 checks/19 observations сохраняются. ActionAutomation обеспечивает normalized Tab/Enter для root action buttons; сама по себе она не предоставляет selection/OS input. UI owner подтвердил отсутствие публичного normalized selection facet: source/destination/slot допустимы как явные synthetic fixture preconditions, а Send проверяется через owning Activate. Настройку synthetic selections нельзя выдавать за физический ввод. Actual create/dispatch/cancel/retry и их rendered results должны быть приняты свежим native run, а не существованием fixture или unit PASS.


## F13-c: native action harness candidate

Harness candidate published as `0b101bf` on `codex/flow-f13-actions`, after `2bf46bc`:
`flow.ui.actions` is a separate single-copy smoke scenario with eleven required checks.
The production `ShowNetwork` path and existing `ShowParcel` path create the opaque surfaces;
Send, Dispatch, Cancel and Retry use the owning normalized Tab/Enter action service.
Source/destination/slot selection is explicitly synthetic fixture setup. The shared completed-frame
observer retains the F12 parcel field/action assertions and adds network expectations; F12's
fifteen-check, nineteen-observation matrix is unchanged.

The new matrix exercises one create followed by a repeated input, actual rejected delivery and
retry of the same parcel, a disconnected route rejection, dispatch/cancel on a separate Created
fixture, stale admission after pause, restored availability, retired handles and read-only save/options
restoration. This initial matrix uses EN at scale 1; it does not replace F12 locale/scale evidence,
claim physical OS input, or close the remaining F13 running/cross-projection acceptance by itself.

- `./tools/hatifect-test tools`, `run-01dgrmi8`: **PASS389**, zero failures/skips.
- First Flow platform run `run-_f69bfyb`: **FAIL** at compilation (missing `Hatifect.UI` namespace), corrected.
- `./tools/hatifect-test flow --platform`, `run-ldtcbwys`: **PASS948** (Flow768, Stardew180); actual TRX
  counters and all result outcomes audited in `artifacts/f13/native-platform-audit.json`.
- Actual tree-sitter static pairing of tools: 16 source files and 22 test files, no unpaired source;
  raw heuristic output `artifacts/f13/native-test-pairing.json`. This is not execution coverage.
- Assertion and source-to-outcome review: `artifacts/f13/native-assertion-review.json`; exact manifest
  requirements and report routing include missing/false checks and near-miss scenario IDs.
- `./tools/hatifect-check --platform`, `run-bo4mx343`: **PASS2062 .NET +389 Python**, all ten actual TRX audited in `artifacts/f13/native-g-audit.json`.
- Independent bounded source review (Sol/xhigh): **NO_FINDINGS** after the namespace correction; `artifacts/f13/native-independent-review.json`. Reviewer did not run the game.
- `./tools/hatifect-check`, `run-y8qiwj1y`: **PASS1713 .NET +389 Python**, all seven actual TRX audited in `artifacts/f13/native-c-audit.json`.
- These are source-candidate checks. The newer combined package/native results and actual failures are recorded below. Full F13 remains **IN_PROGRESS**.


## Combined native verification: source 7dcfd4f

The published combined source is [`7dcfd4f`](https://github.com/ihatectf/Hatifect/commit/7dcfd4f527925c7441ea2342b363d7a4fd921fc3): F13 harness candidate plus the UI owner’s source-notification fix `62be88a5915f747f08073d6ff6f54093c509f8d0`. Source changes now trigger synchronization even when the consumer already pumped its projection. Public API shape and Flow Core/persistence are unchanged.

| Verification | Actual evidence | Result |
|---|---|---|
| `rtk proxy env ./tools/hatifect-check --platform` | `run-0h3mi9co`, `artifacts/f13/native-combined-g-audit.json`; ten actual TRX | **PASS** 2073 .NET +389 Python |
| `rtk proxy env ./tools/hatifect-isolated-ui-ca --keep` | `artifacts/f13/native-combined-p-audit.json`; retained isolated TRX/packages | **PASS** 101 CA tests; all eight package repository commits equal `7dcfd4f`, package/producer/deployed DLL hashes match |
| `rtk proxy env ./tools/hatifect-smoke flow.ui.actions` | request `20e15c7a-9585-4293-9e7d-4b60ad2fef3e`, `artifacts/f13/native-first-failure-audit.json` | **FAIL** opening Network, before the first action/capture |
| Deterministic standard-host reproduction | `NetworkHostedViewportReproductionTests.NetworkExperienceOpensAtTheStandardHostedViewport`, `run-o5k9m59z`, `artifacts/f13/native-overflow-red-reproduction.json` | **FAIL** 1/769; all existing 768 Flow tests pass |
| `rtk proxy env ./tools/hatifect-smoke flow.ui.isolation` | request `f1799025-1031-49b6-9c5b-45b0bf0c4fd1`, `artifacts/f13/native-f12-regression-audit.json` | Automated **PASS** 15/15 checks, 19 capture pairs; visual **FAIL** at scale75 |

Both native requests use fingerprint `c9002a130765da06b4a62570c6955a858cabeddb12a4ed77c735fb62748f7ebd`, Stardew Valley 1.6.15 build 24356 / SMAPI 4.5.2, and the isolated root `.smapi-test/flow-actions`. The first prepare attempt correctly rejected `.smapi-test` itself; after selecting its child root, canonical live preparation passed (`run-ch66npib`). Synthetic fixture copies and all original hashes remain unchanged. Both requests restored owned options and removed their working saves; ordinary user Mods/saves were not used.

The F13 native failure is `UiLayoutException` at the generic Network root `Hatifect.Flow/network/scene/host`: required minimum `144x1128.9` exceeds the available height. Standard 1280x720 hosted composition reproduces the same failure with minimum `144x1152.9`. There are zero native action captures, commands and effects; this run proves the defect, not the action matrix. The temporary reproduction fixture is retained unchanged in `artifacts/f13/NetworkHostedViewportReproductionTests.cs` with its actual failing TRX provenance. It must become a permanent regression with the owning fix; no existing tests were removed or weakened. UI owns the generic root-layout correction.

F12 regression raw audit verifies 55 files, 38 PNG hashes, 15 host/result checks, 19 fresh rendered observations, six retired surfaces, three loads/two title transitions, zero final subscribers, two expected fixture effects, two removed working saves and restored settings. All 19 composed PNGs were inspected. At scale 0.75 the right edge of the Parcel panel and Return action is physically clipped in both composed and UI-layer images; this is distinct from intentional ellipsis seen on longer disabled labels at larger scales. The snapshot reports logical viewport 1960x1275. This visual finding was handed to the UI owner, and the automated PASS is not a visual PASS or a new unconditional F12 acceptance.

Next: integrate the owning UI fixes, preserve the standard viewport and acceptance assertions, reprepare the immutable combined source, rerun both native scenarios, then complete F13 running/cross-projection evidence. The full F13 remains **IN_PROGRESS**.


## Two-host projection regression

`TwoHostsRejectReentryAndRepeatedDispatchThenObserveTheSameCommittedDomainState` now recomposes both real test hosts after Dispatch and Cancel and checks each accepted scene’s State text and matching accessibility value. Retained initial and Scheduled frames remain unchanged after later publications. `rtk proxy env ./tools/hatifect-test flow --project 'Hatifect Flow/tests/Hatifect.Flow.Tests/Hatifect.Flow.Tests.csproj'`, `run-gda21f26`: **PASS 768**, exact test and actual TRX verified in `artifacts/f13/cross-projection-scoped-audit.json`. This strengthens existing cross-projection evidence without changing production behavior; it does not claim two native overlays or asynchronous-operation rendering.

Final `rtk proxy env ./tools/hatifect-check`, `run-1hckz9x7`: **PASS 1713 .NET +389 Python**; all seven actual TRX are verified in `artifacts/f13/cross-projection-c-audit.json`. Independent read-only source/evidence review found no defects in the added test or recorded native outcomes (`artifacts/f13/native-checkpoint-independent-review.json`). The current test-only change requires no new game run; the explicitly failed native cases above remain unresolved.


## Synchronous running-operation probe candidate

After the published two-host checkpoint `de7ddbb`, the native driver temporarily subscribes to the diagnostic owner’s real revision publication around the first Send. While create is still executing, the callback attempts the same owning normalized Send action once; the scenario requires a clean rejection, exactly one create effect, no parcel command effect and removal of the callback in `finally`. Raw `sendReentry` records attempts, admission, before/after effect counts and any exception. This strengthens the existing `repeated` assertion without changing the eleven required checks or thirteen planned observations.

Parcel domain commands return synchronously. Runtime `RejectWhileRunning` is exercised by the real hosted reentry test; the native callback proves synchronous owner reentry only, and the retry-pending frame represents an already scheduled domain delivery. None of these is a claim that a long-lived asynchronous `Running` frame was rendered. No artificial delay or production async API is introduced. This compiled native probe remains a candidate until accepted after the UI root fix.

`rtk proxy env ./tools/hatifect-test flow --platform`, `run-8zx1siod`: **PASS 948** (768 Flow +180 Stardew); actual counters/outcomes and driver hash are recorded in `artifacts/f13/native-reentry-platform-audit.json`. Native execution of this new probe is still pending the owning UI fix.

Independent bounded source review: **NO_FINDINGS**, `artifacts/f13/native-reentry-independent-review.json`. The nested rejection currently occurs at the owning UI dispatch fence; this probe does not independently assert a domain `OperationPending` rejection code. Missing/repeated callbacks, admitted input, exceptions, duplicate effects and retained subscriptions all fail explicitly.

Full `rtk proxy env ./tools/hatifect-check --platform`, `run-za06cqmu`: **PASS 2073 .NET +389 Python**. All ten actual TRX and the driver hash are verified in `artifacts/f13/native-reentry-g-audit.json`. This passes source validation only; the native probe remains **NOT_RUN** and the preceding action scenario remains **FAIL** until the UI fix and immutable-source rerun.


## Root overflow integration candidate

UI source `4a28752efd7df46c7b415198e8781839635da3ce` is integrated as `396abe5`; all four owning postimages match exactly (`artifacts/f13/root-integration-source.json`). The fix adds bounded vertical scrolling and atomic focus reveal for exact native centered overlays. UI role-cache fixes `05aed6534250b471c6460e39deb4895aa1a3e346` / `6148b33ae0d6969bf274100d2664bfbb546a1933` are integrated as `20c5968` / `bc11296`. Combined Runtime `run-aeidhn0m` **PASS 542** before the test seam extension; actual TRX is audited in `artifacts/f13/root-runtime-audit.json`.

The unchanged diagnostic fixture still failed at ordinary Window policy (`run-zqreo1my`, 768 PASS /1 FAIL). Owning path inspection established that production `ParcelSurface.Show` always calls `CreateActiveMenuOverlay`, whose policy is `UiProvisionalHostPolicies.OverlayCentered`; the old `ExperienceTextProbe` used ordinary Window. The original fixture and failures remain retained. UI authorized a test-only optional `centeredOverlay = false` constructor argument preserving the existing default. The new `NetworkNativeCenteredOverlayTests.NetworkExperienceOpensAtTheNativeCenteredOverlayViewport` uses the exact production policy at the unchanged 1280x720 viewport, requires one semantic Send with its exact scene/button ID and retains the Result-field assertion.

`run-f2a82bfv` failed to compile the initial seam because the registry parameter is named `host`, not `policy`; corrected. `run-586at92o` successfully opened the root and exposed a latent assertion error: `ExperienceTextAction.Id` is a scene-node ID, while `Action.Id` is the semantic ID. The new test now checks both identities explicitly. `rtk proxy env ./tools/hatifect-test flow --project 'Hatifect Flow/tests/Hatifect.Flow.Tests/Hatifect.Flow.Tests.csproj'`, `run-eytmuw81`: **PASS 769**. Raw evidence and exact historical distinctions are in `artifacts/f13/root-native-policy-scoped-audit.json`. The full graph/package/native rerun is pending; neither this managed pass nor the policy correction clears the earlier actual native or Scale75 visual failures.

Combined G attempt `run-oxqh2dlu` stalled during build (owned PID31328, no CPU/log progress or child process for more than eight minutes). A two-second native process sample retained wait stacks but did not establish the root cause. SIGTERM did not terminate it; only that verified build process was stopped with SIGKILL. The canonical run records **FAIL**, build exit -9. `artifacts/f13/stalled-build-audit.json` and `stalled-build-sample.txt` retain the diagnosis; retry `run-6og_rl4x` uses unchanged source/check settings. No test or analyzer was disabled.

Retry `run-6og_rl4x` completed **PASS2088 .NET +389 Python**: ten actual TRX hashes, counters and every case outcome are verified in `artifacts/f13/root-combined-g-audit.json`. The unchanged canonical build succeeded in60.14 seconds; the stopped attempt is retained separately and is not a source regression PASS. C/P/native remain pending.


## Fresh action checkpoint a09f237 and remaining visual evidence

Source [`a09f237`](https://github.com/ihatectf/Hatifect/commit/a09f2378a180a07bcb4771e0b6a30134383e8b7e) is published. C `run-i7di4f2f` **PASS1728 .NET +389 Python**, G `run-6og_rl4x` **PASS2088 +389**, isolated P **PASS101 /46 projected files /8 packages**. All eight repository commits equal a09f237 and package/producer/prepared game DLLs match. Independent policy/source review **NO_FINDINGS**; exact production-policy regression remains managed evidence. Artifacts: `root-combined-{c,g,p}-audit.json`, `root-policy-commit-source.json`, `root-policy-independent-review.json` under `artifacts/f13/`.

Canonical live prepare `run-7ez3sf2y` succeeded. Fresh `rtk proxy env ./tools/hatifect-smoke flow.ui.actions`, request `ecfab0ee-e6d9-4e2b-aaca-90f99022719c`, completed **PASS11/11**, process exit0,322 driver frames,13 capture pairs. Fingerprint `66014d27b243b400d91888eb6879d272dd6f9f194778a67ff0dc4d2fc0a0bb18`. Actual Send owner reentry: one attempt, rejected, no exception, create effects1→1; worlds end with create counts1/0/0, command/effect counts1/0/2, subscribers0, four retired surfaces. Settings restored and one request-owned save removed. `root-native-actions-audit.json` retains request/source, raw hashes and all26 PNG hashes.

All13 composed frames were inspected. The nine Parcel states display their expected state/result/availability. Network opens without the prior minimum-height failure, but `network-created` and `no-route-result` feedback is below the visible viewport. Its semantic/text presence is **not visible-result acceptance**. UI owns a bounded normalized scroll/reveal seam to expose the result before a fresh capture; no consumer geometry workaround or semantic reorder is made. This run proves action effects and synchronous reentry, not physical OS input, a long-lived asynchronous Running frame, or full F13 completion. Earlier Scale75 visual FAIL remains open.

## Native scale-transition correction candidate

The existing F12 `SetEnvironment` and F13 `SetEnglish` assigned base and desired scales together. Installed Game1 IL shows that `_update` only queues native `refreshWindowSettings` when those values differ; assigning both bypassed UI render-target recreation. UI owner supplied exact IL and game DLL hash `8937c582cad1c1127017944778c4102467bf299aea44869491f28ab0ec84cd73`. No UI bridge/model/API changes are required.

Both diagnostic drivers now request desired scale only. The shared completed-frame observer requires two consecutive consistent native frames, including actual `uiScreen` dimensions matching the logical viewport and applied scale matching the requested frame. Retained value diagnostics contain base/desired/applied scale, window/logical viewport, UI/game targets, back buffer and graphics-device dimensions. No GPU resource is retained by this probe.

Restoration writes the separately captured original base/desired pair and locale/input settings, checks the exact snapshot, and calls the public native `refreshWindowSettings()`. Successful title/final transitions wait for fresh settled draws; a pre-existing pending desired scale is allowed to resume naturally and is explicitly recorded alongside the original pair. F12 retains exactly15 checks/19 scene observations; the three world-restoration frames are separate bounded diagnostics. F13 retains11 checks/13 observations. Failure cleanup restores the settings snapshot and records errors; it cannot claim successful settled restoration.

Platform scoped `run-hnbfdij7` **PASS183**, including three new cases for stale physical UI target under a changed logical viewport, pending/missing targets breaking consecutive evidence, and fresh-draw requirements after restoration reset. Actual TRX is audited in `scale-native-scoped-audit.json`. Full checks and fresh native visual acceptance remain pending; historical FAIL is not cleared by these managed tests.

Scale candidate G `run-9p6_dsv0` **PASS2091 .NET +389 Python**; ten actual TRX verified in `artifacts/f13/scale-native-g-audit.json`. Five source/test postimages are retained in `scale-source-candidate.json`. Native verification and visible-result reveal remain open.

Final C `run-55kbqvep` **PASS1728 .NET +389 Python**, seven actual TRX audited in `artifacts/f13/scale-native-c-audit.json`. Native/PERF claims are unchanged until the fresh isolated run.


### Native input-profile transition follow-up

Source `84a2ed152b3a770eb2d4e427925a4b9412873a29` passed its C/G/scoped checks and was published. Fresh F12 request `9ab48dc9-5943-4777-99e6-b329579f4527` is **FAIL** at Scale75 before its capture: base/desired/applied are0.75, but native gamepad mode is Auto instead of requested ForceOff, with the same Options instance. Five earlier captures exist; no Scale75 or settled-restoration PASS is claimed. Exact settings snapshot and runner files were restored; process exited0, with the required-check failure preserved in `artifacts/f13/scale-first-native-failure.json`. The precise native writer of the mode is still under investigation; the observed change followed the real window transition.

The diagnostic follow-up applies the owned input profile once after native target dimensions settle, then waits two fresh completed draws before accepting content. It does the same for the captured original input profile during restoration. Exact mode/controller assertions remain in place; the callback is not repeatedly applied on every frame. Four settling regressions now include a once-only post-resize profile write followed by fresh presentation. Scoped `run-x9uyldgz` **PASS184**, actual TRX in `scale-profile-scoped-audit.json`. Final combined checks and fresh native evidence remain pending for this follow-up.

Canonical live prepare builds seven host-free packages plus the Stardew adapter directly. `scale-native-package-identity.json` verifies those actual seven package/producer/deployed identities and the direct adapter DLL; the initial audit expected eight packages and failed before writing. The separate isolated P101/eight-package evidence belongs to a09f237 and is not relabeled as this prepare result.

### Exact action input profile review correction

Independent review found that F13 could accept `gamepadMode=Auto` with `gamepadControls=false`: the UI input facet only reflects the latter. Both Network and Parcel observations now use one settling guard that requires the exact ForceOff/false pair before arming capture. The regression `ActionProfileRejectsAutoRestoredAfterItsOneTimeApplication` changes mode to Auto after the one-time callback, completes two fresh frames and requires rejection; it also rejects ForceOff/true. Reviewer confirmed resolution with no additional source findings.

Scoped `run-1qdc1sdh` **PASS185**; final `rtk proxy env ./tools/hatifect-check --platform`, `run-swlyn97h`, **PASS2093 .NET +389 Python**, ten actual TRX verified. Five source/test hashes remained unchanged through this final run. Evidence: `artifacts/f13/scale-profile-exact-mode-audit.json`, `scale-profile-exact-mode-g-audit.json` and `scale-profile-review-resolution.json`. Earlier G `run-1qcodzsp` PASS2092 and C `run-g_ug9fgj` PASS1728 +389 predate this last correction and are retained separately. Initial sandbox run `run-u0m6y500` failed restoring NuGet dependencies with NU1301/GetDomainName; the authorized retry succeeded.

Git operations are currently blocked by automatic review with the explicit reason that Git is wholly user managed and work must be limited to project files. No alternative Git path was attempted. The current correction has file/hash evidence only; no new commit or native result is claimed. Scale75 visual acceptance and visible Network result remain open pending fresh canonical native evidence and the owning Reveal integration. Full F13 remains **IN_PROGRESS**.

The final host-free check of that exact input-profile correction, `run-823ghnnd`, subsequently passed **1728 .NET +389 Python**, with seven actual TRX verified in `scale-profile-exact-mode-c-audit.json`. This is the pre-Reveal checkpoint.

### Owning Reveal integration and fresh-frame capture

UI supplied seven frozen source/test files from `ba8dae47561bd1ada720391bdd8e24ffdb460b0c`; their exact SHA-256 transfer is recorded in `artifacts/f13/reveal-owner-file-integration.json`. The optional Reveal API extends the harness action API; production v1 is unchanged. UI owns bounded normalized wheel input and accepted-clip visibility. The Flow driver requests the same API instance used to create its surfaces, waits for the accepted exact committed Result, and reveals `Hatifect.Flow/network/element/result` for the two nonempty Network results. All 13 existing observations and domain action/effect transitions remain intact.

Reveal runs during Update only. The observer records instance, accepted scene/frame and completed-pass count after Reveal; the later Draw callback captures only when that same frame is rendered on a strictly newer pass. Recomposition or a layout change invalidates the previous visibility proof and requires another Update reveal. No geometry or input is added to the Draw callback. Captured evidence records `RevealedSemantic`. Two `FlowUiRevealFrameTests` reject same-pass, foreign-instance, missing-render, changed-scene/layout and mismatched-render evidence. Independent source review found no issues; it is not native pixel evidence.

| Combined file candidate check | Evidence | Result |
|---|---|---|
| Scoped Flow Stardew | `run-7xnvnzrk`, `reveal-consumer-scoped-audit.json` | **PASS187**, including both new cases |
| `rtk proxy env ./tools/hatifect-check --platform` | `run-sdbz9zr2`, `reveal-combined-g-audit.json`, ten actual TRX | **PASS2113 .NET +389 Python** |
| `rtk proxy env ./tools/hatifect-check` | `run-okcaq0j9`, `reveal-combined-c-audit.json`, seven actual TRX | **PASS1743 .NET +389 Python** |
| `rtk proxy env ./tools/hatifect-isolated-ui-ca --keep` | retained `hatifect-ui-ca-isolated.819shkhi`, `reveal-combined-p-audit.json` | **PASS101**, 46 projected files, eight packages, two CA DLLs, UI source absent |

Each package DLL equals its producer output. Package repository metadata describes the existing checkout; it does not prove a new commit for the file candidate. Current source hashes, scoped/full evidence and the separate seven-file UI dependency are retained in `reveal-final-file-handoff.json`. The initial scoped launch permission review timed out before a process was created; its allowed single retry ran successfully.

Fresh native acceptance is still unavailable in this task. No separate game/serve rejection occurred: the actual rejection was Git access with a files-only instruction. Canonical `direct_runtime.py` builds and validates requests using `_repository_head`, which invokes `/usr/bin/git ... rev-parse HEAD`; this dependency was inspected, not removed or invoked through a workaround. No fresh Scale75 visual PASS, visible Network Result PASS, new commit or publication is claimed. Full F13 remains **IN_PROGRESS**.

## Native scale owner correction after common actions

GQ reported common source4a actions209bd59f PASS11 with13 inspected composed captures and visible created/no-route Results. The independent isolation request ed43e616 failed atScale75 after5observations,176frames. Raw lifecycle/log and native DLL hashes were audited in `artifacts/f13/scale-menu-retirement-audit.json`: exact GameMenu is reconstructed by Game1.SetWindowSize, so the original overlay loses its native owner. The full native closing stack was not instrumented; code and timing support this fixture cause, with no logged UI exception.

The one-file candidate `scale-stable-menu-candidate.json` adopts the existing UI environment harness pattern of a stable IClickableMenu cover. It preserves real desiredUIScale/render-target invalidation, fresh draws,15checks/19observations, retained source/action/publication and A→B→A. An additional exact native-cover identity assertion detects unintended replacement. Managed/native execution remains PENDING. This is not standalone product acceptance or a real GameMenu replacement test; production lost-owner retirement stays unchanged and replacement acceptance remains separate.

F13 common stable-menu correction: GQ integrated source `866b1d0`; current diagnostic source bytes match common. Native `bfa4bf7f-edf3-40aa-8914-a7be24254990` **PASS15**, all19observations/6views,3loads/2titles,542frames,3exact settled restorations,exit0/no teardown errors. Flow independently viewed75/100/125/150 and RUcontroller composed PNG: bounded surface, no prior75right clipping; disabled action labels may ellipsize with full reason text below. Exact raw/source hashes: `artifacts/f13/common-stable-menu-native-audit.json`. Earlier native FAIL remains historical. Physical input, full action locale matrix and actual inventory UI scenario remain required; this does not mark F13/F18/F19 DONE.
