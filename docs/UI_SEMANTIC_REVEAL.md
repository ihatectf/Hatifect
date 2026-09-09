# Semantic reveal для native acceptance

Статус: **implementation C/G/P/review PASS; native visibility IN_PROGRESS**. Source [`ba8dae4`](https://github.com/ihatectf/Hatifect/commit/ba8dae47561bd1ada720391bdd8e24ffdb460b0c), опубликован в `codex/ui-semantic-reveal`. Это UI-owned продолжение root overflow для F13. Полные U05/U06/F13 остаются открытыми.

## Причина и контракт

Fresh Flow request `ecfab0ee-e6d9-4e2b-aaca-90f99022719c` после root overflow прошёл protocol11/11 и сохранил13 capture pairs. Network теперь открывается и выполняет Send, но Result в двух состояниях остаётся ниже viewport. Semantic/text diagnostics не доказывают физическую видимость результата.

Опциональный `IUiSemanticSurfaceRevealAutomationApi` наследует существующий action automation API и предоставляет `RevealAutomation.Reveal(session, semantic)`. Production v1 baseline и существующий action API не изменены. Surface создаётся тем же API instance; facet доступен только в exact TestHarness. Runtime находит единственный semantic scene node в пределах1024 nodes и выполняет не более64 обычных normalized wheel inputs через native adapter. Прямых setters root/collection offset нет.

Успех требует положительных bounds полностью внутри accepted clip, без epsilon. Пассивный focus и action effects сохраняются. Owner/thread/scene/portal checks выполняются до ввода и после каждого input; retirement и scene replacement не могут дать успех. Missing target, неподдерживаемый root route, слишком большой или nested-clipped target, отсутствие progress и исчерпанный budget возвращают false. Ambiguous IDs и сцена более1024 nodes отклоняются. Три на три integer probes ищут root input route вне коллекций; если вся сетка занята коллекциями, метод возвращает false, не меняя их offset. Это ограниченный поддерживаемый маршрут, не полный поиск произвольной свободной точки.

При ошибке позднего input ранее успешно принятые шаги остаются принятыми, как при обычном вводе. Метод не имитирует OS input. `true` подтверждает только accepted layout: consumer обязан дождаться нового completed draw перед PNG. В idle/update/draw новый обход не добавлен; отдельный native PERF этого exact-harness метода не заявляется.

## Проверки

Все команды выполнялись с `rtk proxy env DOTNET_gcConcurrent=0`, build/test SDK `${HOME}/.dotnet/hatifect-x64-8/dotnet`.

| Gate | Команда и evidence | Результат |
|---|---|---|
| Valid RED | `./tools/hatifect-test --project "Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj"`, `run-wtw6h98s` | Build PASS;557 executed,550 passed,7 новых behavioral FAIL при stub false |
| Final scoped | та же команда, `run-t0c1840k` | PASS565, все15 `SurfaceRevealTests` |
| C | `./tools/hatifect-check`, `run-udrftt47` | PASS1660 .NET +386 Python,7 actual TRX |
| G | `./tools/hatifect-check --platform`, `run-v0ujf1lr` | PASS2003 .NET +386 Python,10 actual TRX;15 Runtime +3 Stardew admission cases;2 существующих NETSDK1138 warnings |
| P | `./tools/hatifect-isolated-ui-ca --keep`, `__uurj__` | PASS101;46 projected files,8 packages exactba8,2 CA DLL,UI source absent |
| Independent review | Sol/high, итоговые7 source/test postimages и actual scoped/admission TRX | PASS, оба Low findings исправлены и перепроверены |
| Native visible Result | новый `flow.ui.actions` после consumer integration | IN_PROGRESS; старый ecfab protocolPASS не является visible Result PASS |

В owner worktree `artifacts/semantic-reveal/`: `final-scoped-audit.json`, `c-ba8-audit.json`, `g-ba8-audit.json`, `p-ba8-audit.json`. Последний сохраняет1444 hashes retained isolated workspace `isolated-ba8`; все8 nuspec repository commits совпадают с source, package DLL совпадают с producer Release DLL. Final scoped TRX SHA256 `8b760a251e659f340e771b828591ea5f065eaf3ecb03e0f52a402f8136d301d1`; P TRX SHA256 `16c76083f07b551296eb7d094bd96e39d1e048e5d1c65090735522c2a9fb8fa7`.

Исторический `run-tkh1d51m`: Stardew82/82 PASS, Runtime561/560PASS/1FAIL из-за точного сравнения float height в fixture; overall FAIL. Исправлена проверка четырёх геометрических краёв. `run-t94ol6ek` PASS563 предшествует strict clipping и двум финальным regressions; итоговые C/G относятся к final source.

## Requirement | Evidence

Если не указано иное, точные методы находятся в `SurfaceRevealTests`.

| Requirement | Exact tests |
|---|---|
| Read-only Result, focus и отсутствие action effects | `ReadOnlyResultIsRevealedThroughScrollingWithoutFocusOrActionEffects`; `RevealCanReturnToAnEarlierReadOnlyFieldWithoutMovingFocus` |
| Visible/missing target не меняет frame | `AlreadyVisibleAndMissingTargetsDoNotDispatchInputOrReplaceTheFrame` |
| Owner/thread/retirement/scene guards | `OwnerRejectionPrecedesAnyInputOrAcceptedStateChange`; `ForeignThreadIsRejectedBeforeTheNativeOwnerCallback`; `RetirementAfterAnInputCannotBeReportedAsSuccessfulVisibility`; `AChangedSceneCannotRedirectTheRemainingInputsToANewTarget` |
| Bounded progress и retry | `ConsumedInputWithoutLayoutProgressStopsInsteadOfSpinning`; `SlowInputProgressStillStopsAtTheDocumentedBudget`; `AFailedLaterInputPreservesEarlierAcceptedProgressAndAllowsRetry` |
| Root route не прокручивает коллекции | `UnrelatedCollectionOffsetsArePreservedByTheRootInputRoute`; `ACollectionCoveringTheRootViewportRejectsRevealWithoutScrollingEitherOwner` |
| Portals и oversized scene | `AnOpenPortalRejectsRevealBeforeAnyRootOrPortalInput`; `OversizedSceneIsRejectedBeforeDispatchingInput` |
| Даже subpixel clipping требует ввода | `ASubpixelClippedEdgeRequiresInputInsteadOfAnOptimisticSuccess` устанавливает реальный gap0.001–0.009 и проверяет один input и strict containment |
| Exact harness admission до native access | `SurfaceObservationAdmissionTests.ProductionApiRejectsRevealBeforeTouchingTheHandleOrNativeHelper`; `RevealRejectsNullHandleBeforeNativeAccess`; `RevealRejectsInvalidSemanticIdentityBeforeNativeAccess` |

## Следующий шаг и ограничения

Flow consumer сохраняет порядок элементов Network. Последовательность: Activate Send → observation ожидаемого committed publication → Reveal для `Hatifect.Flow/network/element/result` с обязательным true → новый completed draw → PNG. Перед capture повторная проверка Reveal не должна менять AcceptedFrame; `IsAcceptedFrameRendered` должен оставаться true. Сохранить все13 существующих состояний, просмотреть created/no-route Result и проверить exact source/DLL identity и cleanup.

Новый Stardew partial с native input adapter не исполняется host-free Runtime tests; static source/test pairing не заменяет его native acceptance. Отдельный Scale75 visual FAIL также остаётся открытым. Flow исправляет desired-only transition, ожидание фактических native targets и exact input profile после resize. Следующий UI-owned harness срез применяет ту же native последовательность к ordinary Observation/Environment, сохраняя намеренные synthetic same-callback fault tests.

## Общая интеграция GQ

Source `c2a9931c701fdecb29eb463541af44c805d50a3b` переносит семь exact postimages `ba8dae4` поверх общей версии с R01-a. C `run-17bg7fbk` — PASS1677 .NET +389 Python; G `run-82ai1c75` — PASS2040 .NET +389 Python. Все658 tracked files совпали с frozen source manifest. P `hatifect-ui-ca-isolated.8934bsfn` — PASS101;47 projected files,8 packages с exact source commit, package DLL равны Release producer,4 consumer cache DLL проверены,2 CA DLL,UI source absent. Сохранены1445 files и их hashes.

Evidence: `artifacts/f13-root-common/c-c2a9931-audit.json`, `g-c2a9931-audit.json`, `p-combined-c2a9931-hatifect-ui-ca-isolated.8934bsfn-audit.json`, `source-c2a9931.json`. Все три процесса завершились с exit0. Это общий implementation checkpoint; новый native visible Result, исправление Scale75 и полные F13/U05/U06/Q01 остаются открытыми. Historical13 composed PNG ecfab просмотрены GQ:9 Parcel states без найденных visual defects,2 Network result messages вне viewport; общий historical visual FAIL сохранён.
