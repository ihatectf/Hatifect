# Подсказки ошибок проверки

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Recorded source: `338e1c0111e177148e80b23ca32b4f8e1af58e93` on the then-local branch
`feature/u06-validation-help`. At the time of this report, the slice had not been pushed.

## Contract

Runtime reads the current validation result once per semantic form field. A nonblank external
`ValidationMessage` or typed field `Error` temporarily replaces the field's ordinary localized help
on every focusable control. The tooltip and accessibility description use the raw error because the
control already exposes the field label as its accessible name. Toggle and choice options share the
field error while retaining their existing node and semantic identities.

The existing inline validation text remains in the form. Errors produced by `UiFormState.Apply()`
keep the existing `Label: error` display; externally supplied validation text remains unchanged.
The inline text is now an accessibility `Alert`. Both the inline alert and error tooltip resolve
`Text.Danger` from the active theme. Clearing the error removes the alert and restores the field's
localized static help.

Keyboard and controller focus expose the current error immediately. Pointer help follows the «Общие компоненты, темы и подсказки» (подэтап j)
theme-owned dwell. The dwell key already contains tooltip text, so changing or clearing an error
restarts a pending hover delay without another timer or traversal. Scene composition introduces no
public API, second consumer source, collection scan, or unbounded state.

## Evidence

| Requirement | Evidence |
|---|---|
| External localized field help is overridden by the current error and restored when it clears | `TooltipTests.ValidationErrorOverridesFieldHelpWithDangerAlertAndClearingItRestoresLocalizedHelp` |
| Typed choice validation reaches every option and keeps one labeled inline alert | `TooltipTests.AppliedChoiceValidationProjectsRawErrorToEveryOptionAndOneLabeledAlert` |
| Label, input and validation retain distinct native projection identities | `NativeInteractionProjectionTests.FieldIdentityDoesNotConfuseLabelInputAndValidation` |
| Initial RED | `run-xqysay7d`: compile RED on the missing validation-alert scene contract |
| Review regression and correction | `run-mf5fkj8p`: 702/703 exposed the old StaticText expectation; focused final `run-9u3q3kmk`: 703/703 PASS |
| Full UI gate | `run-viprhjc4`: 1,135/1,135 PASS |
| Exact host-free gate | `run-c82nn1et`: 1,938 .NET + 503 Python PASS |
| Isolated package consumer | `hatifect-ui-ca-isolated.zepm1frk`: 8 UI packages, 47 projected files, 102 CA tests, 2 CA DLLs; UI source absent |
| Public and architecture contracts | `verify_public_api.py`: API v1 / 7 semantic consumer types; 21 semantic-contract tests; architecture 26 projects / 5 rule groups PASS |

## Remaining scope

This slice does not change validation authoring, validation timing, focus movement, or form apply
semantics. It does not complete the native EN/RU/scale/theme/controller observation matrix or add
consumer-specific field errors. Tooltip fade animation and remaining component/theme families stay
open, so «Общие компоненты, темы и подсказки» remains `IN_PROGRESS`.
