# Подсказки полей формы

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Recorded source: `f2794599df67e9786ae493e7a1450a24812a5f42` on the then-local branch
`feature/u06-form-help`. At the time of this report, the slice had not been pushed.

## Contract

`UiExperienceBuilder.Tooltip(target, text)` accepts a stable form-field ID after the containing form
has been declared through `Configure`. Calling it before form declaration or with an unknown ID
still fails without changing the builder. The built Experience retains the same immutable localized
tooltip map introduced by «Общие компоненты, темы и подсказки» (подэтап e); no second help channel or device-specific consumer text is added.

Scene composition attaches field help only to focusable controls. Text and number fields receive it
on their input node. Toggle and choice fields project the same resolved help to every option button,
while every node retains the field ID as `SemanticId` and a unique renderer tooltip ID. Hover/focus
selection, layout, placement, theme tokens and frame construction continue to use the «Общие компоненты, темы и подсказки» (подэтап e) policy.

Accessibility keeps the control label as `Name` and publishes help separately as `Description`.
Moving focus between field controls chooses at most one existing overlay and does not rebuild layout.

## Evidence

| Requirement | Evidence |
|---|---|
| Declaration order, stable target and exact RU locale | `ExperienceTextTests.DeclaredFormFieldsAreStableLocalizedTooltipTargets` |
| Text input focus, accessibility and no relayout | `TooltipTests.FormFieldHelpUsesFocusedInputAndAccessibilityDescription` |
| Toggle/choice option projection and one active overlay | `TooltipTests.ChoiceFieldHelpProjectsToEveryFocusableOptionAndShowsOnlyTheFocusedOne` |
| Focused Planning scope | `run-__gjexi6`: 145/145 PASS |
| Focused Runtime scope | `run-ltyixctv`: 695/695 PASS |
| Full UI regression | `run-ctapqcpm`: 10 DevTools + 145 Planning + 695 Runtime + 67 Semantics + 16 Tooling.Server + 192 Tooling = 1,125 PASS |
| Canonical source gate | `run-l34vwaar`: 1,928 .NET + 503 Python PASS; architecture, API v1, packages, restore/build PASS |
| Isolated package consumer | `hatifect-ui-ca-isolated.hzglotde`: 8 UI packages, 47 projected files, 102 CA tests, 2 CA DLLs; UI source absent |
| Public contract | `verify_public_api.py`: API v1 / 7 semantic consumer types PASS; `test_semantic_contracts`: 21 PASS |

Planning RED `run-6_7sv1mb` kept 144 existing tests green and rejected the new field target.
Runtime RED `run-clu24fsd` kept 693 existing tests green and rejected both new form-help cases at the
same builder boundary. The unchanged tests passed after extending target validation and scene
composition.

## Remaining scope

This slice does not attach help to collection items or validation-error nodes, accept contributed
route/action metadata, or define display delay. It does not claim Flow/CA adoption, native
screenshots, physical controller input, or the full EN/RU/scale/theme matrix. Those items and the
remaining theme/component families keep «Общие компоненты, темы и подсказки» `IN_PROGRESS`.
