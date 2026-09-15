# Всплывающие подсказки

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Recorded source: `1e0a660a3c1b21f42e78cd9430c35a7b82430ad5` on the then-local branch
`feature/u06-tooltips`. At the time of this report, the slice had not been pushed.

## Contract

`UiExperienceBuilder.Tooltip(target, text)` attaches immutable `UiLocalizedText` help to an already
declared presented element or action ID. The built Experience exposes a read-only `Tooltips` map.
Unknown targets and duplicate declarations fail before publication. Tooltip text follows the same
exact-locale lookup as other Experience text and never changes the target identity, label, source,
action, or presentation plan.

Scene composition resolves the captured locale once and creates a stable tooltip identity below the
target scene node. The visual policy belongs to the framework and uses the active theme's popup
surface, primary text, body typography, small spacing/radius, and high elevation. Feature Visual
recipes cannot replace this foundation policy in this slice.

Layout measures tooltip text with wrapping and retains its desired size, inset, and viewport clip in
the accepted layout snapshot. It does not add the tooltip to the main layout tree or desired size.
Frame construction chooses pointer hover first and keyboard/controller focus second through the
existing scene index, emits at most one surface/text pair after the base tree, and does not call text
metrics. Placement prefers below, falls back above, then clamps width and height to the viewport.

The accessibility node keeps its existing name/value/shortcut and exposes tooltip text separately as
`Description`. A tooltip never enters focus order or hit testing.

## Evidence

| Requirement | Evidence |
|---|---|
| Stable immutable authoring and exact EN/RU resolution | `ExperienceTextTests.TooltipsRetainStableTargetsAndResolveExactLocales`; invalid target/duplicate test |
| Hover precedence, focus fallback, one overlay | `TooltipTests.HoverTakesPrecedenceAndClearingItReturnsToFocusedTooltip` |
| Keyboard/controller and Dark/Light/HighContrast | Six cases in `FocusShowsOneLocalizedThemeOwnedTooltipWithoutRelayout` |
| Theme-owned primitives, viewport bounds and accessibility description | The same focus matrix checks resolved theme values, geometry, clipping and `Description` |
| No relayout when the active target changes | Focus matrix retains the accepted `LayoutBuilds` count |
| No frame-time measurement | `AcceptedTooltipLayoutBuildsFrameWithoutRenderTimeTextMeasurement` uses rejecting text metrics |
| Correct hot-reload invalidation | `TooltipContractChangeInvalidatesOverlayLayoutWithoutStructuralRecompose` |
| Edge placement | `PlacementUsesAboveFallbackAndClampsOversizedTooltipToViewport` |
| Full UI regression | `run-aricgabx`: 10 DevTools + 144 Planning + 688 Runtime + 67 Semantics + 16 Tooling.Server + 192 Tooling = 1,117 PASS |
| Canonical source gate | `run-4jh9cnaw`: 1,920 .NET + 503 Python PASS; architecture, API v1, packages, restore/build PASS |
| Isolated package consumer | `hatifect-ui-ca-isolated.1lbrxvoj`: 8 UI packages, 47 projected files, 102 CA tests, 2 CA DLLs; UI source absent |
| Public contract | `verify_public_api.py`: API v1 / 7 semantic consumer types PASS; `test_semantic_contracts`: 21 PASS |

The first full UI run `run-gzwxftwf` found an authoring error in the new xUnit assertion. Subsequent
RED runs `run-22upfgiu`, `run-le66c4_h`, and `run-8ooa3mtx` corrected test expectations for resolved
insets, above/clamped placement, content clipping, and float precision. `run-cdkudm9v` was blocked by
the sandbox's VSTest socket restriction; the authorized outside-sandbox runs provide the accepted
evidence above.

## Remaining scope

This slice does not define per-row collection help, form-field help, contributed action/route
metadata, hover delay, or a public timing policy. It has no in-game/native screenshot acceptance and
does not claim Flow or CA consumer adoption. Those items, collection-row prompts, remaining theme
families, and the full EN/RU/scale/controller consumer matrix keep «Общие компоненты, темы и подсказки» `IN_PROGRESS`.
