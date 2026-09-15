# Исходные результаты проверок в игре

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Status: **IN_PROGRESS**. This checkpoint owns the fresh combined runtime baseline and its evidence. The UI task continues «Согласованное обновление данных» and the Flow task continues «Перевозки в игровой сессии»; their implementation and historical runtime results are not relabelled by this report.

## Candidate and environment

- Candidate: `7a9e590080b05fbcb7df3f473ec67a10050d4610`, published in `develop`; UI package authority `1.0.0-alpha.35`.
- Worktree: `${HOME}/Developer/Worktrees/hatifect/345f/Hatifect`.
- Host: macOS 27.0 / Darwin 27.0.0, arm64; Python 3.14.6; x64 .NET SDK/runtime for the existing game-compatible target.
- Actual game reports: Stardew Valley `1.6.15 build 24356`, SMAPI `4.5.2`, Flow `3.0.0-rc.89`, CA Overlay `1.0.0-alpha.1` and Chests Anywhere `1.30.1`.
- UI runtime fingerprint (`sha256-runtime-v2`): `4fdd656edf30171943b96a61487f159f78a64daaadbd364b4ca04914fa564d2f`. The two fake Flow reports identify their Flow runtime as `99a5040d0276b743e62b0ac4993ba18bf94736199dd81e7a66cae295bcf25f44`; these are distinct host inventories from the same prepared candidate. Initial live preparation completed with exit 0; packaging deliberately skipped its own tests and is not RC promotion evidence. The separate C/G/P runs below supply their actual validation results.
- Validation commands use command-local `DOTNET_gcConcurrent=0` for the previously diagnosed Rosetta build issue. The executor is launched separately without that command-local override; validation timing is not a default-GC game performance measurement.
- Chests Anywhere 1.30.1 is copied from the verified local installation into this worktree's isolated Mods directory. Only its DLL, manifest, assets and translations are copied; the isolated instance creates its own default configuration. Original Mods and real saves are not changed.
- Machine-readable environment, dependency hashes, integration file hashes and executor state: `artifacts/q01-runtime-baseline/` in this worktree.

## Completed executor prerequisite

Commits `4fe852e` and `8b36537` remove the obsolete editor requirement from readiness diagnostics and all restart/client messages. `serve` directly starts the canonical supervisor/worker; `doctor` validates transport metadata, SMAPI and a live Ready state. It does not inspect `.vscode/tasks.json`. No public UI/Flow API or persistence format changes are introduced by this prerequisite.

Invocation and process ownership are documented in [RUNTIME.md](RUNTIME.md).

The actual PTY launch reached Ready for this exact worktree and `doctor` returned PASS. The first bounded startup/shutdown probe is retained in `artifacts/runtime-executor-terminal/`. The alpha.35 executor completed this series and was stopped through its owning PTY before integrating the next source revision. Ready is not game acceptance.

Four new regressions in `tools/tests/test_user_session_runtime.py` verify:

- `test_doctor_accepts_ready_executor_without_vscode_configuration`: missing, malformed or unrelated editor task configuration does not block a healthy executor.
- `test_doctor_rejects_invalid_transport_for_ready_executor`: invalid transport still blocks readiness.
- `test_doctor_rejects_unavailable_executor_with_valid_transport`: missing/stale/stopped/running state does not pass Ready.
- `test_submit_without_executor_reports_direct_terminal_command`: an unavailable executor reports the terminal command and does not publish a request.

## Validation evidence

All artifact paths below are relative to this report's worktree. A report for an earlier candidate remains attached to that candidate.

| Candidate | Command | Result |
|---|---|---|
| Combined alpha.34 | `rtk proxy ./tools/hatifect-test tools` | PASS, 334 Python, no failures/skips; `artifacts/validation/run-7i9tmojl/summary.json` |
| Combined alpha.34, before the final message-only correction | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | PASS, 1,445 .NET +334 Python; `artifacts/validation/run-uhmqwkpv/summary.json` |
| Combined alpha.34, final executor source | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | PASS, 1,248 .NET +334 Python; `artifacts/validation/run-gpb6ocgk/summary.json` |
| Combined alpha.34 | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | PASS, 44 projected files, eight packages, 90 tests, two CA DLLs and no UI sources; retained projection `hatifect-ui-ca-isolated.rqdb1izu` under the host temporary directory |
| Combined alpha.35 / `7a9e590` | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | PASS, 1,460 .NET +334 Python; `artifacts/validation/run-5em2s3gm/summary.json` |
| Combined alpha.35 / `7a9e590` | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | PASS, 1,263 .NET +334 Python; `artifacts/validation/run-l3iikt6a/summary.json` |
| Combined alpha.35 / `7a9e590` | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | PASS, 44 projected files, eight packages, 90 tests with no failures/skips, two CA DLLs and no UI sources; log `artifacts/q01-runtime-baseline/isolated-ca.log`, retained projection `hatifect-ui-ca-isolated.li992116` under the host temporary directory |


## Runtime acceptance boundary

Each request below ran against the candidate above with its host's fingerprint. `result.json`, `host-acceptance-report.json`, process/request metadata, transport logs and screenshots are retained under `artifacts/runtime/<request>/`. `artifacts/q01-runtime-baseline/runtime-index.json` records counts and artifact hashes. There are 11 successful requests with 56 passing assertions, plus one retained failed request caused by the incorrect absent fixture.

| Scenario | Request | Actual result |
|---|---|---|
| `save.bootstrap` | `28fbcdd4-6443-4eaa-9bd9-33f82138c3e9` | PASS, one check, 15.457 s; synthetic save creation, return to title and reload |
| UI `all` | `06967b5f-b61c-4e58-9a6f-c2b5fbae34ff` | PASS, 27 host checks plus one performance-budget assertion, 22.508 s |
| `flow.route.basic` | `97f472c9-2b55-4972-a6ef-7de761b7414a` | PASS, six checks, 12.422 s; loaded/delivery/pause/idle/lifecycle/reload using fake cargo |
| `flow.save.isolation` | `7aa232f9-e409-4144-81d2-4cb76286f2b7` | PASS, seven checks, 12.227 s; fake lifecycle plus save isolation |
| `semantic.lifecycle` | `235f96dc-3e37-4063-9c39-2988dcf2b091` | PASS, three checks; screenshot inspected: Diagnostics/Inspector navigation and focused Diagnostics are visible |
| `semantic.inspector` | `ce61f060-afd3-432b-a3a7-fbaeea042979` | PASS, three checks; screenshot inspected: hierarchy, diagnostic details, Reveal source and Close are visible |
| `semantic.overlay` | `f6f243b5-092e-41ed-9dca-dda403d2b605` | Automated PASS, three checks; screenshot is black, so this is not visual acceptance |
| `semantic.chests-anywhere-overlay.absent`, incorrect fixture | `f5e12795-fd31-4cf6-8184-b68a0d1b7a8f` | FAIL: Chests Anywhere was still installed. This is an operator fixture error, not evidence of a product failure. The failed run is retained; the isolated dependency is moved out for the corrected run. |
| `semantic.chests-anywhere-overlay.absent`, corrected fixture | `365ba64a-9bd9-46a3-884a-169704f0f896` | PASS, one check; the isolated CA directory was moved outside Mods, then restored after completion |
| `semantic.chests-anywhere-overlay.incompatible` | `9bc895ac-198a-4cfc-9e5a-b01738016ad6` | PASS, one check; the installed dependency passed preflight and the injected incompatible API was rejected without native leases |
| `semantic.chests-anywhere-overlay.capture-exception` | `69c12cd2-19d2-494b-aa66-e89b673888ae` | PASS, one check; an injected acquisition failure restored native ownership and closed the overlay |
| `semantic.chests-anywhere-overlay.return-to-title` | `8eb8bddd-f4d5-447b-8b6a-46fbd09b87e1` | PASS, two checks; native ownership restored across return to title |

The aggregate measured 620 frames on `semantic-terminal-menu`, Dark theme, 1470×956 logical pixels, scale 1. p95 UI thread time is 0.057710 ms; p99 0.399166 ms; steady allocation 4567.006 B/frame; measure/arrange cache misses both 0. These pass the unchanged 2/4 ms, 16384 B/frame and 0.2 miss-ratio budgets for this measured surface. The result does not measure full Flow throughput or every UI state/theme.

## Observations and open work

- The aggregate screenshot contains the game room while diagnostics still report `terminalOpen: true`. The capture reads `GraphicsDevice.GetBackBufferData` during `Rendered`; [SMAPI 4.5.2 raises that event with the current render target bound](https://github.com/Pathoschild/SMAPI/blob/4.5.2/src/SMAPI/Framework/SCore.cs#L1183-L1238). A subsequent [bounded capture correction](game-validation-capture-fix.md), `85a786f`, moves capture after world/UI composition and validates fresh aggregate/standalone images. It does not relabel this original screenshot as a pass.
- Standalone lifecycle and inspector screenshots show a 1280×720 UI region inside a 1470×956 image. The first captured frame and viewport recompose timing need to be distinguished from steady layout behavior; a positive geometry assertion alone does not establish full-viewport rendering.
- The standalone overlay screenshot is black despite the three lifecycle flags passing. It needs fresh rendered evidence; visible host state is not established by the flags alone.
- Automated pointer/keyboard/controller/text scenarios invoke game input handlers. Physical device input has **not been performed** in this checkpoint. This remains separate from those automated passes.
- Locale switching and scale values 75/100/125/150 were accepted by the runtime. Only the implemented Default/Dark capability preset was validated; there is no fresh visual EN/RU × scale × theme matrix yet.

The subsequent [capture correction](game-validation-capture-fix.md) and [HUD-overlay fixture correction](game-validation-overlay-fixture.md) have their own fresh C/G/P/runtime evidence. Next: address the separate viewport/composition and visual-matrix observations. Full «Проверка текущей сборки в игре» remains IN_PROGRESS while these gaps are unresolved. The baseline above remains attached to `7a9e590` when newer Flow/UI changes are integrated.
