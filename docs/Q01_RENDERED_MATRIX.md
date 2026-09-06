# Q01: rendered locale/scale matrix and resize correction

Status: **DONE** for the bounded rendered diagnostic matrix, resize correction and failure-shutdown check. Implementation is [`ae2f829`](https://github.com/ihatectf/Hatifect/commit/ae2f829ec0be02f302df522247d20dd8e557f825), starting from `d3e90925c0868b9c2dbf6b46be2fc3c122335b3c`, UI alpha.37. It extends the [first-frame and native-window checkpoint](Q01_WINDOW_VIEWPORT.md). Final combined acceptance uses alpha.39 integration `b3265fa` below. Full Q01 also requires separate native-input evidence and remains **IN_PROGRESS**.

## What the matrix establishes

The previous `semantic.locale-scale-theme` implementation changed locale and `desiredUIScale`, read them back, then restored them during one execution. Stardew applies `baseUIScale` during a later update. Those checks did not establish that any intermediate scale was drawn.

The isolated acceptance controller now advances through English and Russian at 75%, 100%, 125% and 150%, using the implemented Dark preset. Each state must match its game and scene locale, requested and applied scales, back-buffer-derived viewport, native menu origin and dimensions, valid semantic geometry, visible diagnostic text probe and completed load fade. Two distinct consistent completed frames are required. A mismatch resets confirmation; 300 unsuccessful frames fail the state. The bounded capture list holds exactly eight states.

For each accepted state the controller retains a completed-frame PNG, a separate UI-layer PNG and its observed values in `diagnostics/runtime.json`. Locale/scale changes and successful aggregate continuation run on Update. Original locale and scale are restored and observed for two frames before continuation. Aggregate resumes at the next scenario, runs the CA contributions and ends with performance. Failure cleanup retains the first typed cause, attempts to restore settings and requests owned-game exit even if writing matrix diagnostics fails.

The probe is test-mode-only. The Russian text includes `ё`, `щ` and `№`; navigation labels (`Diagnostics`, `Inspector`) remain English in both locales. This diagnostic probe is not a production localization catalog. This matrix proves rendered states and glyph visibility for this fixture, not complete UI/Flow/CA translation, every product state, every viewport or a new theme implementation.

## Resize defect reproduced by the new check

Before the presenter correction, request `d545ca6d-db85-443c-a5ea-f89658b4617a` captured EN at 75%, then **FAIL**ed at 100% after 300 frames. Desired, base and pixel scales were all 1; viewport and menu dimensions were 1280×720. Both native menu coordinates were `-2147483648`. Its eight required checks failed, duration was 16.677 s and fingerprint was `3820ab840205f6fc11db87f5d974af263d11d84d11c5de9d7051e39c078c04c9`. The original typed error and stack remain in runtime diagnostics; restoration was not verified as rendered.

The installed game's `IClickableMenu.gameWindowSizeChanged` positions a menu relative to unused window margins. For a full-viewport menu those margins are zero: the relative-position calculation divides zero by zero and can yield NaN, then `int.MinValue`, even on a no-op resize. `UiSemanticStardewMenu` now overrides that callback without applying the relative-margin formula. Its existing Update/Draw viewport synchronization remains the sole owner of native bounds and recomposes only for an actual viewport change.

This is a Stardew presenter correction. Layout, visual policy, focus and rendering remain in their existing framework owners. Public API, package boundary, package version and persistence formats are unchanged; there is no game-engine patch or arbitrary UI offset. Matrix-only traversal/capture allocations occur outside the steady performance window. No unbounded frame history was added.

The first corrected candidate passed standalone request `e6744b95-12df-426c-a213-9bf69293e71e` (8 assertions, 11.927 s) and aggregate `618fe930-8a51-438e-b6e6-e23fb8b1500e` (28 assertions, 22.535 s). Both used fingerprint `810355ce8dd6c5fc19496f0a6c329e9a061c703847043232861216e40bca2e20`, retained eight states and verified restoration. All eight standalone completed-frame PNGs were inspected: navigation, focus border and probe text were visible, including Cyrillic. These runs precede the final matrix diagnostics-failure guard and retain their own candidate identity.

## Regression coverage

The pure rendered-state gate is linked into the existing UI Runtime test project without a game dependency. The focused run executed 14 cases successfully, with no failures/skips: `artifacts/q01-rendered-matrix/tests/rendered-state.trx`.

| Requirement | Test evidence |
|---|---|
| Requested scale alone cannot accept an undrawn state | `UiRenderedStateGateTests.DesiredScaleReadbackCannotAcceptFramesBeforeAppliedScaleAndViewportChange` |
| Duplicate and older callbacks cannot confirm a second frame or reaccept | `UiRenderedStateGateTests.DuplicateOrOlderCallbacksDoNotCountAsAnotherRenderedFrame` |
| A resized valid frame needs fresh stable confirmation | `UiRenderedStateGateTests.ResizeRequiresTwoNewConsistentCompletedFrames` |
| Locale, theme, scale, viewport/menu, tree, probe and fade mismatches reset confirmation | `UiRenderedStateGateTests.InvalidIntermediateFrameBreaksConfirmation` — 11 cases |
| Each failed required scale check remains FAIL and retains its diagnostic note | `LiveHarnessTests.test_ui_scale_failures_remain_structured_runtime_diagnostics` — four scale fixtures |
| First terminal error retains reason/type/message/stack and atomic diagnostic ordering | Existing `LiveHarnessTests.test_terminal_failure_is_retained_as_one_typed_diagnostic` with its obsolete method delimiter updated |

The Python scale test now validates a report's actual rejection and structured assertion instead of looking for the removed same-tick helper's source strings. The first G attempt (`run-wh0he6ji`) stopped at two obsolete Python bindings before the .NET stage. A subsequent focused run exposed a mistaken fixture key (`scenario` instead of protocol `subject`); the fixture was corrected. No production assertion, performance threshold or test was disabled. Final tools gate `run-a9opskxh` passed 351 tests, zero failures/skips.

## Final candidate evidence

Implementation `ae2f829` passed C `run-cfjkx_b7` (1,331 .NET +351 Python) and G `run-t82jh76e` (1,569 .NET +351 Python). All seven C and ten G TRX files were audited: nonzero tests, matching total/executed/passed and individual Passed outcomes, no error/skip outcomes. These results belong to the alpha.37 candidate.

Integration [`b3265fa`](https://github.com/ihatectf/Hatifect/commit/b3265fa3b7f23b2794588f56afcc51d59148859f) includes published alpha.39 `3fb56b9` (Terminal identity `8a3545d` and action admission/retirement `6444272`). All 25 incoming files retained exact bytes at integration before the Q01 documentation updates; all eight Q01 implementation/test files retain their reviewed hashes. No shared source conflict or contract change was introduced during integration. The first supplemental comparison read a moving shared `origin/develop` ref and rejected the alpha.38 comparison; comparison against each exact merge parent confirmed preservation. The unfinished alpha.38 G (`run-0b0h4nrk`) was deliberately cancelled before final acceptance when alpha.39 became available. Its verified process group was terminated and checked empty before another build; it is not a PASS.

Final combined static gates, with actual TRX counters and individual outcomes checked directly against their summaries:

| Command | Evidence | Result |
|---|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | `run-4zcdaasw` | **PASS**, 1,588 .NET +351 Python, ten successful TRX files |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | `run-p1av7g44` | **PASS**, 1,350 .NET +351 Python, seven successful TRX files |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | `hatifect-ui-ca-isolated.lifvyszj` | **PASS**, 44 exact projected files, eight alpha.39 packages, 90 CA tests, two CA DLLs, UI source absent |

Both static gates include all 329 Runtime cases, including the 14 new matrix cases and the incoming action/Terminal regressions. No skipped, not-executed, error, timeout, aborted or inconclusive result was accepted. P was additionally audited against all current projection files, each packaged producer DLL, three assets files using only the isolated consumer cache, all 90 individual test outcomes and both deployed CA DLLs; no UI DLL duplication was present. Package hashes and audit results are in `alpha39-package-audit.json`. Exact source SHA256 values are retained in `artifacts/q01-rendered-matrix/evidence-index.json`; all raw logs and runtime artifacts remain in the owning worktree `${HOME}/Developer/Worktrees/Codex/345f/Hatifect`. Failed and intermediate successful requests are retained separately.

Fresh runtime on `b3265fa`, Stardew 1.6.15 build 24356, SMAPI 4.5.2 and CA 1.30.1:

| Command/scenario | Request | Actual result |
|---|---|---|
| `./tools/hatifect-ui-test semantic.locale-scale-theme` | `bd277a1d-2e9c-4e09-87f1-cb36fc304417` | **PASS**, 8 assertions, 16.761 s, eight captured states and verified restoration |
| `tools/hatifect-live-runner ui all` against that freshly prepared deployment | `d5ee5e15-7a23-4762-8446-9fadfa587bf0` | **PASS**, 28 assertions, 21.976 s; matrix, CA and performance complete |
| Same canonical matrix runner with request-local I/O fault fixture | `aec3a3bc-015b-411b-a73a-14b8a0b8d778` | Expected **FAIL**, 8 failed assertions, 9.536 s; game exits 0 and executor returns Ready |
| `tools/hatifect-live-runner smoke flow.route.basic` against the same deployment | `5e1c0a65-06bd-48df-b7eb-f13af7dfbbfd` | **PASS**, 6 assertions, 9.805 s |
| `tools/hatifect-live-runner smoke flow.save.isolation` against the same deployment | `f143d416-1db1-4508-928e-ffe4f5283ba2` | **PASS**, 7 assertions, 10.215 s |

The positive UI runs use `sha256-runtime-v2`, fingerprint `b5353b9f1ddf22c86606a49a9a523792e2ac701c520802577abec83f4d5b3098`, UI alpha.39. All eight final standalone completed-frame PNGs were inspected. Native menu origin stays 0,0; back buffer stays 1280×720. Viewports are 1707×960, 1280×720, 1024×576 and 854×480 for 75/100/125/150%, respectively. Locale/scale/theming observations match each target; the accepted completed-frame numbers are 4,6,8,10,12,14,16,18. Diagnostic controls, focus border and the probe remain visible, including Cyrillic. Separate UI-layer files are retained; the eight completed-frame images provide the visual inspection evidence.

Aggregate measures 620 frames after restoration, at scale 1 and Dark theme: p95 0.041501 ms, p99 0.285708 ms, 4541.458 B/frame, zero measure/arrange cache misses. Existing budgets pass. No build or native-window observer overlaps this measurement. This is the diagnostic fixture's steady performance window, not a general benchmark of all product views.

The failure fixture creates directories at the first matrix PNG path and `diagnostics/runtime.json` inside a new request-owned artifact directory. Screenshot creation fails with `UnauthorizedAccessException`; publishing the diagnostic then fails with `IOException: Is a directory`. The first screenshot failure remains in the retained `runtime.json.tmp` with reason/type/message/stack; the final diagnostic JSON cannot be published and is not represented as valid evidence. The secondary error is logged, all eight required checks remain FAIL with `HARNESS-VISUAL-MATRIX-CAPTURE-EXCEPTION`, and `process.json` records exit 0 with an empty teardown-error list. Transport reports `ScenarioFailure`, not timeout or teardown failure. Executor status subsequently confirmed Ready with no active request. This actual failure run closes the matrix callback shutdown check without weakening normal acceptance or fabricating a PASS screenshot.

Both fresh fake Flow runs use `sha256-flow-runtime-v1`, fingerprint `4540912142242796c446029ca28537340391fda9d38cf04ed4701d89998dfb88`. That algorithm covers its own inventory and is not interchangeable with the UI fingerprint. These are current fake-provider lifecycle/isolation results, not a relabelling of the Flow owner's prior real-chest F20 resource measurements. All five final requests identify source commit `b3265fa`; only documentation changed after their tested compiled inputs were frozen.

All launches use the canonical worktree executor and isolated test root. Build/check/prepare commands use the documented command-local `DOTNET_gcConcurrent=0`; the executor and game run with ordinary GC. Normal Mods, real saves, golden assets and personal Codex settings are unchanged. Codex host audit is **NOT_APPLICABLE** to this slice.

## Remaining Q01 scope

Native/physical input is not proved by these programmatic transport checks or screenshots. The prior macOS borderless clipping limitation remains documented; the rendered matrix uses isolated windowed 1280×720. Product-specific empty/loading/error/action states and broader locale/layout coverage belong to their respective acceptance slices. Next ready Q01 work is separate native-input observation with explicit distinction between OS-injected events and physical hardware input.


## Integration review: late final diagnostic failure

During alpha.40 integration, independent review found a later failure boundary that the first-PNG fault above does not exercise. If all eight matrix checks are already PASS and only final diagnostics/runtime.json publication fails, the previous finalizer could return overall PASS. The original alpha.39 positive evidence remains valid, but the old failure-shutdown result does not establish this late boundary.

The alpha.40 finalizer correction requires current request-owned published diagnostics with no terminal failure, complete matrix/restoration and consistent per-state observations/PNG paths before returning PASS. It preserves the controller's first check verdicts and reports final evidence failure separately. Temporary-file reproduction, focused tests and final static/runtime results are tracked in [the integration report](ROADMAP-STATUS.md#alpha40-integration-and-q01-final-diagnostic-guard). The correction's fresh runtime acceptance is pending until that report records the actual late-only fault and positive matrix.
