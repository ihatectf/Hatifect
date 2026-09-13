# U06-j theme-owned tooltip display delay

Source: `53a388e2115678f9374041812b67095f7402fdcc` on local branch
`feature/u06-tooltip-delay`. This slice is local only and has not been pushed.

## Contract

Keyboard/controller focus continues to expose semantic help immediately. Pointer hover becomes the
visible tooltip target only after it remains on the same eligible node for the resolved tooltip
visual's `Motion.Normal` duration. The base themes resolve that duration to 160 ms; a derived theme
can set it to zero for immediate pointer help without changing consumer semantics.

Raw hover remains available to visual states during the dwell. A focused tooltip remains visible
until pointer dwell completes, then hover takes precedence. Changing targets resets elapsed time;
leaving a target clears visible hover help immediately. Equivalent scene reconciliation retains the
pending dwell, including read-only collection rows, while changed tooltip text or delay restarts it.
Disabled actions do not retain hover eligibility.

`UiHostRuntimeSession` owns elapsed interaction time and builds exactly one new frame when a pending
tooltip crosses its threshold. Ordinary ticks perform constant-time target checks and do not measure
text, traverse collections, build layout, or build frames. The portal host advances its existing
runtime snapshot. Stardew menus use `GameTime.ElapsedGameTime`; overlays use one 60 Hz update quantum
only while their existing input-routing gate is open, so native occlusion pauses dwell with input and
draw.

## Evidence

| Requirement | Evidence |
|---|---|
| Immediate focus, delayed hover precedence and immediate focus fallback | `TooltipTests.HoverTakesPrecedenceAndClearingItReturnsToFocusedTooltip` |
| Target reset, one threshold frame and no layout rebuild | `TooltipTests.ChangingHoverTargetRestartsDelayAndCrossingThresholdBuildsOneFrameWithoutLayout` |
| Theme-owned zero-duration policy | `TooltipTests.ThemeCanChooseImmediatePointerHelp` |
| Read-only collection dwell survives equivalent reconciliation | `TooltipTests.ReadOnlyCollectionItemHelpCanBeHoveredWithoutBecomingActionable` |
| Initial RED | `run-cv3x6ojv`: compile RED with 10 missing delay/host/snapshot contract uses |
| Review regression and correction | `run-dwi7cv7v`: 2 disabled-action hover retention failures; focused final `run-zxgg_4x6`: 701/701 PASS |
| Exact host-free gate | `run-x8mdn5e9`: 1,936 .NET + 503 Python PASS |
| Exact platform gate | `run-keui6nvt`: 2,399 .NET + 503 Python PASS, including 107 UI Stardew, 254 Flow Stardew and 102 CA tests |
| Isolated package consumer | `hatifect-ui-ca-isolated.c_do62d1`: 8 UI packages, 47 projected files, 102 CA tests, 2 CA DLLs; UI source absent |
| Public and architecture contracts | `verify_public_api.py`: API v1 / 7 semantic consumer types; 21 semantic-contract tests; architecture PASS |

The first platform attempt `run-6q7no7za` stopped with exit 130 after its build log made no progress
for six minutes. No test failure was reported. The final exact platform run above completed normally.

## Remaining scope

This slice defines visibility dwell and does not animate tooltip opacity or movement. It does not add
validation-error help or consumer-specific tooltip text. Native screenshots, physical pointer timing,
the full EN/RU/scale/theme matrix and Flow/CA adoption remain open, so U06 stays `IN_PROGRESS`.
