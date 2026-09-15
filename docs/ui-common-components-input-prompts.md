# Подсказки управления

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Status: **implementation, scoped validation, C and P PASS; full «Общие компоненты, темы и подсказки» IN_PROGRESS**. Owning source is
`d8e6eb7f6840a99afde371db33bee6a16ed0f2a9` on `feature/u06-input-prompts`.

## Contract

Runtime derives one canonical activation prompt from the accepted input environment. Keyboard uses
`Enter`, controller uses `A`, and mouse-and-keyboard omits the prompt. Environment-free legacy
invocations preserve compatibility: the Controller presentation profile uses `A`; other profiles
remain prompt-free. The mapping matches the Stardew adapter, where Enter/Space and controller A
dispatch Submit.

The policy applies to ordinary and typed action buttons, toggle/choice options, contributed actions
and routes, and Terminal navigation. Consumer action and route labels remain localized content and
never include device text. Collection-row selection prompts are outside this bounded component
slice.

The prompt is an immutable scene value and a separate text primitive. Runtime exposes it separately
as the accessibility shortcut while keeping the action label as the accessible name. It is measured
with `Typography.InputPrompt`, drawn with `Text.InputPrompt`, and separated from the action label by
`Space.S`; the typed Visual properties are `prompt.typography`, `prompt.foreground` and
`prompt.spacing`. Dark, Light and HighContrast resolve the same tokens through their own theme
chain.

Prompt width and the reserved typed-action status line are measured once and stored in the immutable
layout entry. Render consumes that geometry without another text-metrics call. Static keyboard and
controller prompt instances avoid per-button prompt allocation. A prompt change within one Wide
profile requests Measure, Arrange and Render without semantic recomposition or consumer
reactivation.

Public semantic surface API v1, consumer labels, action execution and package dependency direction
are unchanged. The additive public surface consists only of the two Runtime theme tokens; the
repository public-API guard accepts the candidate without a baseline exception.

## Validation

The first common candidate `artifacts/validation/run-jjaw6fa_` is retained as a real RED:
static checks, 503 Python tests, packaging, restore, warning-as-error build, Flow 791, DevTools 10 and
Planning 140 passed; Runtime then reported 671 passed and four failures in
`LayoutTests.TypedActionBarWithFractionalLineHeightRetainsUsableGeometry`. The renderer had requested
text metrics after layout, which the direct render API deliberately does not provide. The final
source moves both new measurements into layout and adds a direct render-planner regression that has
no render-time metrics.

Post-fix focused evidence is **PASS**: 12 Runtime cases cover the eight prompt/profile cases and the
four formerly failing fractional-layout cases; the typed Semantic catalog case passes separately.
The complete Runtime suite then passed 677/677 and Semantics passed 65/65. The first complete Runtime
attempt passed 676 and reported one existing exact-allocation comparison 24 bytes below its warm
baseline. Its exact recheck passed on the same binaries, and the final complete Runtime run passed
677/677; this was lower warm-up noise, not per-portal allocation growth.

Canonical C `artifacts/validation/run-ku2f7tb5` is **PASS**: architecture, public API, package
contract, restore and warning-as-error build passed; Python passed 503/503 and .NET passed 1,890/1,890
with no failures or skips, including Runtime 677 and Semantics 65.

Isolated package/CA P is **PASS** in retained workspace
`/private/var/folders/ly/sph907ln7bv1tc3dxmflc62h0000gn/T/hatifect-ui-ca-isolated.m8qcyejr`:
eight UI packages, 47 projected files, 101 CA tests, two CA deployment DLLs, and no UI source in the
consumer projection.

Game-linked/native visual, physical controller input, Flow/CA consumer observation, EN/RU and scale
validation remain required for the next consumer integration candidate. Tooltip, density,
collection-row prompts and the remaining theme families remain open under full «Общие компоненты, темы и подсказки».
