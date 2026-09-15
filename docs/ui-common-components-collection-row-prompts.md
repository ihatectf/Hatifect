# Подсказки управления строками коллекции

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Recorded source: `373c854a216cb190f3f6457d32470188ad00d03a` on the then-local branch
`feature/u06-row-prompts`. At the time of this report, the slice had not been pushed.

## Contract

A selectable semantic collection receives the same framework-owned activation prompt as buttons and
routes. The accepted input mode selects `Enter` for keyboard and `A` for controller; pointer-first
mouse/keyboard mode omits the prompt. Every materialized selectable row displays the prompt while a
read-only `Browse` collection remains unprompted. Consumer item labels and stable IDs are unchanged.

Scene composition captures one immutable prompt with the collection. Layout measures it once,
reserves a right-side column beside label/supporting text, and retains exact prompt bounds on each
materialized row. Prompt width, height and spacing participate in bounded item-measurement and
adaptive-height cache identities, so an input-mode or theme-metric change cannot reuse stale row
geometry. Frame construction consumes those bounds without text measurement.

The prompt uses `Text.InputPrompt`, `Typography.InputPrompt` and `Space.S` from the active theme.
Accessibility keeps the item label as `Name` and exposes the prompt independently as `Shortcut`.
Changing `Enter` to `A` produces Measure, Arrange and Render invalidation without structural
recomposition or consumer reactivation.

## Evidence

| Requirement | Evidence |
|---|---|
| Mouse/keyboard/controller policy | Three cases in `SelectableCollectionRowsProjectMeasuredFrameworkPrompts` |
| Measured non-overlapping row geometry and theme tokens | The same cases compare label/prompt bounds and active token values |
| Accessibility item shortcut | Every visible `ListItem` retains its name and exposes the expected `Shortcut` |
| Read-only semantics | `ReadOnlyCollectionDoesNotClaimAnActivationPrompt` |
| Narrow invalidation | `CollectionPromptChangeInvalidatesLayoutWithoutStructuralRecompose` |
| Runtime regression and performance gates | `run-yecm0hpz`: 693/693 PASS |
| Full UI regression | `run-02rxmeze`: 10 DevTools + 144 Planning + 693 Runtime + 67 Semantics + 16 Tooling.Server + 192 Tooling = 1,122 PASS |
| Canonical source gate | `run-1qvq7nhn`: 1,925 .NET + 503 Python PASS; architecture, API v1, packages, restore/build PASS |
| Isolated package consumer | `hatifect-ui-ca-isolated.kqcnyr0z`: 8 UI packages, 47 projected files, 102 CA tests, 2 CA DLLs; UI source absent |
| Public contract | `verify_public_api.py`: API v1 / 7 semantic consumer types PASS; `test_semantic_contracts`: 21 PASS |

The first authoring run `run-9zpwjetg` found the incorrect test helper name `List`; the corrected
read-only test uses the public `Browse` entry. `run-owc_z2cy` then preserved the intended framework
RED with 690 existing tests passing and three new prompt cases failing because the collection had no
prompt. `run-eoovjfgh` was blocked only by the sandbox VSTest socket restriction. The unchanged tests
passed outside the sandbox after the runtime implementation.

## Remaining scope

This slice does not add consumer-authored device labels or a row-specific action contract. It does
not claim Flow/CA adoption, in-game screenshots, physical controller input, or the full
EN/RU/scale/theme matrix. Form-field help, contributed metadata and remaining theme/component
families keep «Общие компоненты, темы и подсказки» `IN_PROGRESS`.
