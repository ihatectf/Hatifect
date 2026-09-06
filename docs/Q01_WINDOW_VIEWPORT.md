# Q01: first menu frame and native window observation

Status: **DONE** for the two bounded fixes and window-mode observation; full Q01 remains **IN_PROGRESS**. This checkpoint starts from `54e14e7` (UI alpha.36, F20-a). Presenter implementation is [`3f3029b`](https://github.com/ihatectf/Hatifect/commit/3f3029baf1f73d12024cb3879e546ed48446edfa); runtime entrypoint implementation is [`edbe9e8`](https://github.com/ihatectf/Hatifect/commit/edbe9e80713d27f0553bcd5d3d33686283718d0c). It separates a corrected Stardew presenter defect, a runtime CLI defect, and an observed macOS borderless-window limitation. Earlier [capture](Q01_CAPTURE_FIX.md) and [HUD fixture](Q01_OVERLAY_FIXTURE.md) evidence retain their original source candidates.

## First menu frame

Stardew can establish its UI viewport between the menu's Update and first Draw. The presenter previously synchronized only in Update. A standalone `semantic.lifecycle` request could therefore capture a 1280×720 layout inside a 1470×956 UI texture, with invalid native menu coordinates.

`UiSemanticStardewMenu` now synchronizes the viewport before rendering as well as during Update. The unchanged path compares the cached value and returns; recomposition runs only when the viewport changes. Layout policy remains in Runtime, while Stardew owns the platform dimensions. Public API, package boundaries and persistence formats are unchanged. Acceptance diagnostics now record the actual native menu bounds.

The same canonical standalone scenario supplies before/after evidence:

| Candidate | Request | Host checks | Native menu bounds | UI viewport |
|---|---|---|---|---|
| Diagnostics before the fix | `0fbaef80-f8d7-4b52-9084-ba7e527319c4` | PASS, 3 | x/y `-2147483648`, 1280×720 | 1470×956 |
| Presenter fix | `3cd0dbd7-9772-41fe-819e-07ec1a3b7397` | PASS, 3 | x/y 0, 1470×956 | 1470×956 |

The first report's structural checks passed despite the visible defect; that result is not a first-frame visual pass. The fixed UI-layer image was inspected and contains both complete navigation controls and the bottom status line over the full viewport. Runtime fingerprints are respectively `ec17533975a5173657873e63029c0557f0156d59b51366629afdaf52b0c23ade` and `dc32edf10299c9cd2fb073b81b7b0f7e9f508c42bbc408d0858f6b42ca57b250` (`sha256-runtime-v2`).

## Native window: borderless versus windowed

Both observations below use the fixed DLLs with fingerprint `dc32edf10299c9cd2fb073b81b7b0f7e9f508c42bbc408d0858f6b42ca57b250`, Stardew 1.6.15 build 24356, SMAPI 4.5.2, CA 1.30.1 and the canonical worktree executor. The observer invokes `tools/hatifect-live-runner ui all` against the freshly prepared isolated deployment. It validates the request, repository, isolated root and live SMAPI PID, then captures only that PID's on-screen window using WindowServer metadata and `screencapture -l`. Existing macOS screen-capture access was available. This is a real window observation; it does not provide physical-input evidence.

| Mode | Request | WindowServer bounds | Game back buffer | Inspected result |
|---|---|---|---|---|
| Borderless | `39ad6fe3-69f8-45e6-86e5-b1f83ecb3fd1` | x 0, y 33, 1470×923 | 1470×956 | Top navigation is clipped in the native window and completed-frame PNG; the UI texture is complete. |
| Windowed | `3adf3b1f-1ea8-4d19-90b3-7504bc7a2548` | x 95, y 90, 1280×748 including title bar | 1280×720 | Native window and completed-frame PNG show both complete controls and bottom text. |

The native PNG dimensions are 2940×1846 and 2560×1496 respectively on this Retina display. The 33-pixel difference in the borderless case matches the missing upper content. The A/B result identifies a window-mode-dependent host limitation on this Mac; it does not establish the lower-level SDL/macOS cause or claim borderless support is repaired. No UI offset, screenshot cropping, or game-engine patch was introduced.

Only mutable files under `.smapi-test/isolated/config/StardewValley/` were changed for the windowed observation. Original bytes of `startup_preferences` and `default_options` are saved under `artifacts/q01-window-observation/borderless-config/`. The isolated windowed settings are `windowMode=1` in startup preferences, `fullscreen=false`, `windowedBorderlessFullscreen=false`, and preferred resolution 1280×720 in both files where those fields exist. The installed game's local `StartupPreferences` type confirms mode 1 is windowed. These test settings remain in the isolated environment for subsequent checks. Real game configuration, normal Mods and golden saves were not changed; no decompiled game source is committed.

Each aggregate passed 28 assertions and the unchanged performance budgets. Both measured 620 frames, Dark theme, UI scale 1, zero measure/arrange cache misses:

| Mode | p95 / p99 UI thread, ms | Allocated bytes/frame | Duration |
|---|---|---|---|
| Borderless | 0.042833 / 0.281668 | 4566.852 | 22.070 s |
| Windowed | 0.054252 / 0.340541 | 4565.755 | 23.123 s |

One external native-window capture ran during each performance sample; the windowed observation also overlapped a G-gate process. These are observed budget results, not a controlled timing comparison between modes, an observer-free benchmark or a complete locale/scale matrix. Automatic scale checks currently verify property readback within one execution; they do not establish rendered frames at every scale.

## Runtime entrypoint help

`hatifect-ui-test --help` previously forwarded the flag to the scenario validator. Its argparse help exited successfully, so the wrapper proceeded into deployment preparation. `hatifect-ui-test`, `hatifect-smoke` and `hatifect-live-runner` now handle help and validate argument shape before loading runtime configuration or creating artifacts. Valid scenario calls retain their existing pipeline.

Three subprocess regression tests in `tools/tests/test_runtime_entrypoints.py` verify side-effect-free help, rejection of invalid arguments, and valid calls reaching the pipeline. The help regression fails for all six script/flag combinations against the original `54e14e7` scripts and passes with the fix. `./tools/hatifect-test tools` passes 345 tests, with no failures or skips. The actual public help command now prints usage and exits without preparing a deployment.

## Validation and retained failed attempts

The alpha.36 source candidate passed `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` (G, `run-29_c05le`, 1,500 .NET +345 Python) and `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` (C, `run-h52t8fid`, 1,281 .NET +345 Python). Both summaries report zero test failures. The command-local GC workaround and escalation for working restore/FileSystemWatcher are the previously documented host conditions; no user model/security settings were changed. The tools-only result is `run-1p4fz1xf`. Source and runtime artifact hashes are retained in `artifacts/q01-window-observation/evidence-index.json`; raw evidence remains in that directory and `artifacts/runtime/<request-id>/`.

Integration `aa04181` combines these fixes with published `4737d64` (UI alpha.37/U03-a and completed F20). The staged comparison against that incoming parent contains only the two Q01 implementation commits; all 46 other incoming files retain identical bytes, and the controller preserves the incoming Flow resource exclusion and capture-error shutdown handling (`merge-preservation.json`). None of the preceding alpha.36 measurements is relabelled as alpha.37.

Combined static and package gates use the same command-local build setting:

- G, `./tools/hatifect-check --platform`, `run-oxunrnku`: **PASS**, 1,555 .NET +351 Python. All ten TRX files agree with the summary; zero failures, not-executed, error, timeout, aborted or inconclusive cases.
- C, `./tools/hatifect-check`, `run-mo6_ehgs`: **PASS**, 1,317 .NET +351 Python, with the same successful actual-TRX audit.
- P, `./tools/hatifect-isolated-ui-ca --keep`: **PASS**, 44 exact projected files, eight alpha.37 packages, 90 CA tests and two CA deployment DLLs without UI source or duplicated UI DLLs. Retained workspace `hatifect-ui-ca-isolated.8ou4iayd`; `package-boundary.json` records package SHA256 values, zero source mismatches and actual TRX counters. Full log: `combined-isolated-ca.log`.

Fresh runtime on the combined candidate also passed:

| Command/scenario | Request | Result |
|---|---|---|
| `./tools/hatifect-ui-test semantic.lifecycle` | `6b68ae01-5911-41fc-9dc5-ce938696d34d` | PASS, 3 assertions, 9.702 s; menu x/y 0 and 1280×720 matches viewport; completed-frame image inspected. |
| Observer through canonical `tools/hatifect-live-runner ui all` | `ac90367e-7f6a-4aab-b92a-403eeb970efc` | PASS, 28 assertions, 23.718 s; real native window inspected, both controls and bottom text complete. |
| `./tools/hatifect-smoke flow.route.basic` | `52984fa5-522e-417d-adea-ce9097420b81` | PASS, 6 assertions, 13.351 s. |
| `./tools/hatifect-smoke flow.save.isolation` | `1baed3a9-7913-43da-8db5-df9ecea14bd0` | PASS, 7 assertions, 10.642 s. |

Both UI reports identify alpha.37 and runtime fingerprint `4fc0b3c304ca342338cb28c32859439173ceea60928d6873c97753ffdc2ebab2`. The aggregate measures 620 frames: p95 0.055584 ms, p99 0.360876 ms, 4572.052 B/frame, zero measure/arrange cache misses, Dark theme, 1280×720 at scale 1. The unchanged budgets pass. One external native-window capture ran during this sample; C/G/P had already finished. The WindowServer bounds are 1280×748 including title bar, and the native screenshot is 2560×1496.

Both fake Flow reports use `sha256-flow-runtime-v1`, fingerprint `24750ff64fc5d223423bc246b13c8360aa1c3db96d8451be11520396582d2ec3`. UI and Flow fingerprint algorithms cover different inventories and are not interchangeable. This fresh fake Flow acceptance does not relabel the Flow owner's earlier real-chest resource/performance evidence. All game launches went through the existing canonical executor in this worktree; the executor restarted its worker for changed source and returned to Ready between requests.

- The accidental old `--help` run created request `508da4d9-6f01-47a7-8f13-9801d345af0b` and entered preparation; no game request was submitted. After checking process ownership, only that command's process group was terminated. It is retained as an operator-cancelled preparation, not a successful run.
- Initial diagnostic preparation `7494ac4a` / validation `run-903902cu` was **BLOCKED** by NuGet NU1301 with `System.Net.CookieContainer` / `GetDomainName: -1` inside the sandbox. Repeating the same canonical command outside the sandbox produced successful request `0fbaef80-f8d7-4b52-9084-ba7e527319c4`.
- The earlier CUA attempt found the running game in its inventory, but app binding returned only after the short scenario exited. That attempt supplied no native screenshot or input evidence. The bounded, PID-owned window observations above subsequently succeeded.
- An invocation with the mistyped ID `flow.lifecycle.isolation` was rejected by the manifest before game submission (`763cd3bb-d052-4902-a000-9d273032b5d1`, `combined-flow-isolation.log`). The correct allowlisted `flow.save.isolation` then passed as recorded above. The invocation error is retained; no allowlist or validator was weakened.

## Next ready Q01 work

Physical input and the rendered EN/RU × scale/theme matrix remain unverified. The next ready step is to capture stable rendered states for each supported combination in the isolated windowed environment, then record native input separately. The macOS borderless clipping remains an explicit environment limitation. These pending conditions prevent full Q01 from being marked DONE.
