# Подсказки дополнительных элементов

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Recorded source: `a2fe8c84c70119c3708145587e3be8ee06015aab` on the then-local branch
`feature/u06-contribution-help`. At the time of this report, the slice had not been pushed.

## Contract

`UiActionContributionDescriptor` and `UiRouteContributionDescriptor` have additive overloads that
accept non-null `UiLocalizedText` tooltip metadata. The metadata is retained by the immutable
contribution descriptor and can be read through `UiContributionDescriptor.Tooltip`. Existing
constructors remain source compatible and declare no help.

Scene composition resolves contribution help once with its captured locale. Action contributions
attach it to the contributed action button; route and section contributions attach it to their route
button. Each projected node keeps its existing action or route semantic identity and receives a
renderer tooltip identity derived from that node. Runtime does not query the registry or resolve
translations while building later frames.

Hover/focus arbitration, accessibility Description, layout measurement, viewport placement, theme
tokens and frame-only selection reuse the «Общие компоненты, темы и подсказки» (подэтап e) overlay policy. Contribution ordering, availability,
activation and registration ownership are unchanged.

## Evidence

| Requirement | Evidence |
|---|---|
| Immutable localized metadata and exact locale resolution | `RegistryTests.ContributionHelpIsImmutableLocalizedMetadata` |
| Exact RU locale, action/route projection and existing overlay behavior | `TooltipTests.ContributedActionAndRouteResolveLocalizedHelpIntoExistingOverlayPolicy` |
| Focused Runtime scope | `run-eh_eok7m`: 697/697 PASS |
| Full UI regression | `run-1xt4uyt1`: 10 DevTools + 145 Planning + 697 Runtime + 67 Semantics + 16 Tooling.Server + 192 Tooling = 1,127 PASS |
| Existing descriptor compatibility | The same full UI run compiles and executes the existing constructor call sites unchanged |
| Canonical source gate | `run-4yonlzfc`: 1,930 .NET + 503 Python PASS; architecture, API v1, packages, restore/build PASS |
| Isolated package consumer | `hatifect-ui-ca-isolated.f79c_0ld`: 8 UI packages, 47 projected files, 102 CA tests, 2 CA DLLs; UI source absent |
| Public contract | `verify_public_api.py`: API v1 / 7 semantic consumer types PASS; `test_semantic_contracts`: 21 PASS |

The initial compile RED reported seven missing overload/property uses in the new registry and runtime
tests. After the contract was added, compilation also caught the scene composer's stale locale
identifier; binding contribution resolution to the existing captured locale produced the focused
GREEN run without changing the renderer or layout contract.

## Remaining scope

This slice does not attach help to individual collection items or validation-error nodes, define a
display delay, or add consumer-specific tooltip text. It does not claim Flow/CA adoption, native
screenshots, physical controller input, or the full EN/RU/scale/theme matrix. Those items and the
remaining theme/component families keep «Общие компоненты, темы и подсказки» `IN_PROGRESS`.
