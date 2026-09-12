# Прокрутка высокого centered overlay

Статус: **implementation PASS; native acceptance IN_PROGRESS**. Source [`4a28752`](https://github.com/ihatectf/Hatifect/commit/4a28752efd7df46c7b415198e8781839635da3ce). Это исправление UI Runtime для существующего Network consumer в F13. Полные U05, U06 и F13 остаются открытыми.

## Причина и поведение

Игровой `CreateActiveMenuOverlay` выбирает `OverlayCentered` и ограничивает bounds доступным viewport. Затем Runtime отклонял измеренный минимум Network 144×1128.9 до первого кадра. Consumer не должен обходить это ограничение частными размерами.

Runtime сохраняет полную высоту содержимого и прокручивает его внутри отдельного content viewport под неподвижным заголовком. Controls сохраняют минимумы и положительную геометрию; clipping, rendering и hit testing используют один accepted layout. Offset ограничен extent и сохраняется при обновлении того же root. Обычный `Window`, недостаточная ширина overlay и недоступная content area сохраняют строгий отказ.

Wheel обслуживает коллекцию под указателем, если она совпала по content bounds и clip; этот маршрут не переключается на root даже на границе collection offset. Root обслуживает ввод вне коллекций. Пассивная прокрутка сохраняет focus. Явная navigation подготавливает root reveal и раскрытие виртуального элемента одной транзакцией; новые layout, accessibility, frame и focus принимаются после успешных callbacks. Ошибка подготовки, reentry или retirement сохраняют прежнее принятое состояние и возможность retry.

Публичный semantic API, persistence, Flow commands и геометрия адаптера не изменены. Draw использует готовый frame; root layout подготавливается по событию ввода. Отдельный native PERF этого поведения ещё не выполнен.

## Проверки

Команды выполнялись с SDK и test SDK `${HOME}/.dotnet/hatifect-x64-8/dotnet`, `DOTNET_gcConcurrent=0`.

| Проверка | Команда / run | Результат |
|---|---|---|
| Scoped | `./tools/hatifect-test --project "Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj"`; `run-9hbz7aor` | **PASS 550**, включая 13 новых regressions; precommit на замороженных исходниках, затем повторено C |
| C | `./tools/hatifect-check`; `run-3kvmwvd3` | **PASS 1645 .NET + 386 Python**, 7 actual TRX |
| G | `./tools/hatifect-check --platform`; `run-wemzt490` | **PASS 1985 .NET + 386 Python**, 10 actual TRX; 2 существующих NETSDK1138 warnings |
| P | `./tools/hatifect-isolated-ui-ca --keep`; `xyjh_v7u` | **PASS 101**, 46 projected files, 8 packages exact4a, 2 CA DLL без UI source |
| Independent review | Sol/high, итоговый source и actual scoped TRX | **PASS** после исправления двух Medium findings об атомарности focus; retirement guard проверен отдельно |
| Native / visual | Свежий `flow.ui.actions` | **IN_PROGRESS** |

Raw audits сохранены в owner worktree под `artifacts/root-overflow/`: `scoped-4a-audit.json`, `c-4a-audit.json`, `g-4a-audit.json`, `p-4a-audit.json`, `independent-review.txt`. P audit содержит hashes всех 1330 retained files. Все восемь nuspec repository commits совпадают с source; package DLL совпадают с producer Release DLL.

Валидный RED `run-1bsyz5wx`: 541 executed, 538 passed, 3 failed на высоте root. Предшествующий fixture с недопустимым symbol ID не является доказательством production RED.

## Requirement | Evidence

Все методы находятся в `RootOverflowTests`.

Requirements | Exact tests
--- | ---
Preserve positive controls and clipped viewport | OversizedCenteredOverlayPreservesControlGeometryInsideAClippedViewport; OrdinaryWindowAndImpossibleCenteredWidthStillRejectInsufficientSpace
Bound root scroll, preserve passive focus and clamp resize | RootWheelClampsAtBothEndsAndDoesNotRevealPassiveFocus; RootOffsetSurvivesAnUpdateAndResetsWhenContentFits
Explicit focus and nested virtual collection | ExplicitNavigationRevealsTheLastControlAndWrapsToTheFirst; CollectionWheelKeepsRootOffsetAndNavigationRevealsVirtualItems
Failure preserves accepted state and retry | FailedRootScrollAndSceneUpdatePreserveAcceptedPresentationAndOffset; FailedFocusRevealKeepsThePreviouslyAcceptedFocusAndFrame; FailureDuringNestedCollectionRevealDoesNotAcceptThePreparedRootScroll
Stationary heading, hitboxes, real action effects | ScrolledControlsUseCurrentHitboxesAndKeepTheHeaderAndActionDispatcher
Reentry, retirement, invalid delta | RootScrollRejectsReentrantMutationAndNonFiniteInput; FocusPreparationFencesLegacyTextSourceReentry; RetirementDuringVisibleFocusPreparationCannotAcceptTheCandidate

## Ограничения и следующий шаг

Исторический `NetworkHostedViewportReproductionTests` использовал `ExperienceTextProbe` с обычным `Window`. Native consumer использует `OverlayCentered`. Поэтому исходный FAIL `run-o5k9m59z` и повторный FAIL `run-zqreo1my` сохраняются как диагностика строгого Window, без утверждения о совпадении native policy. Flow добавил отдельный `NetworkNativeCenteredOverlayTests.NetworkExperienceOpensAtTheNativeCenteredOverlayViewport` с прежним viewport 1280×720, semantic Send ID, scene button ID и Result. Actual TRX `run-eytmuw81` проверен UI: **PASS 769/769**, включая этот метод. Это пока WIP source Flow на cherry-pick `396abe5` (root4a) и `20c5968`/`bc11296` (role correction), не immutable handoff. После handoff обязательны свежий native run, проверка source/DLL identity и просмотр кадров.

Независимый Scale75 **visual FAIL** на широком Flow экране (`f1799025-1031-49b6-9c5b-45b0bf0c4fd1`) остаётся открытым. Raw UI layer уже теряет правые пиксели; финальная композиция масштабирует обрезанный слой. Вертикальная прокрутка не доказывает исправление этого дефекта. Native contract исследован ниже; следующий шаг — исправление перехода scale в owning Flow harness и свежая игровая проверка.

После добавления документации `python3 tools/validation.py static` (`run-iep_8f16`) — **PASS**: instructions/config, architecture, semantic public API и 386 Python tests, без failures/skips. Это не повтор native или .NET acceptance.

## Scale75: причина в переходе окружения harness

GQ повторил обрезание на общем source `6dbdbea9c86cb87c510981d0c69d8802ee805875`, request `3025f832-785e-4bab-94ad-b4ba05d2ba0d`: protocol **PASS 15**, source identity/cleanup **PASS**, но visual **FAIL**. UI отдельно просмотрел composed и raw UI-layer PNG 1280×720: правый край панели и Return обрезаны уже в raw layer. Протокольный PASS не закрывает визуальный отказ.

Read-only PE/IL исследование локальной игровой сборки выявило штатную последовательность. `Game1._update` сравнивает `baseUIScale` и `desiredUIScale`; при различии принимает desired и вызывает `refreshWindowSettings`. Этот публичный метод ставит обработку изменения окна в очередь `GameRunner`. На следующем draw `Window_ClientSizeChanged` приводит к `SetWindowSize`, который создаёт `uiScreen` размером `ceil(localMultiplayerWindow / uiScale)`. Финальная композиция уже масштабирует этот target на `uiScale`.

`FlowUiAcceptance.SetEnvironment` записывал оба поля одновременно: `baseUIScale = desiredUIScale = scale`. Это устраняет различие, по которому игра инициирует пересоздание target. Неправильный переход harness согласуется с наблюдением: при 75% logical viewport вырос, но raw UI target остался физического размера. Версия о необходимом дополнительном масштабировании UI bridge отклонена; пока нет основания менять production placement или logical environment.

Согласованный следующий срез Flow Diagnostics: записывать только desired scale и ждать штатного applied scale и размеров target на завершённом draw. При restoration сохранить исходные base/desired, запросить штатную native invalidation и отдельно учитывать исходный pending desired transition. Не создавать render targets самостоятельно. Свежий native run должен подтвердить размеры `uiScreen`, полный Return и отсутствие обрезания. До этого оба исторических visual FAIL остаются открытыми.

Диагностика не запускала игру и не загружала её assembly для исполнения. Raw reader/IL и hashes сохранены в owner worktree `artifacts/root-overflow/scale75-native-il/identity.json`; SHA-256 игровой DLL — `8937c582cad1c1127017944778c4102467bf299aea44869491f28ab0ec84cd73`. Основные методы: `_update` token `06000B00`, `refreshWindowSettings` token `06000ACE` (Public, HideBySig), `SetWindowSize` token `06000AD0`. Это доказательство локального native contract, не замена fresh runtime acceptance.

## Follow-up: видимость Result

Flow actual-policy regression зафиксирован в `a09f2378a180a07bcb4771e0b6a30134383e8b7e`; fresh native `ecfab0ee-e6d9-4e2b-aaca-90f99022719c` прошёл protocol11/11,13 capture pairs,322frames, identity/cleanup. Это закрывает отказ открытия Network и подтверждает действия, но два Result ниже viewport: visual acceptance остаётся открытой. UI добавил [bounded semantic Reveal](UI_SEMANTIC_REVEAL.md), source `ba8dae4`, C1660/G2003 +386Python/P101 exact8packages PASS. Следующий native run должен показать сам Result после reveal и нового completed draw.
