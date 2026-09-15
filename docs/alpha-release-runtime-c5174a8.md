# Проверка альфы в игре — c5174a8

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Проверено 2026-09-12. Source: `c5174a8c4a9bf1f3a28d7420bb11d16705a6f96d` на локальной ветке `feature/q02-local`. Кандидат объединяет bounded Window capture retention (`311b9b1`, `79412fb`, `b23f54c`) и точные «Отправка груза и работа с ошибками» resource-limit причины (`acb1279`). Это подтверждённый промежуточный кандидат, а не завершение «Приёмка первой альфы» и не принятая устанавливаемая альфа. Удалённый Git не изменялся.

## Общие проверки

- G `artifacts/validation/run-0rgtui7v/summary.json`: PASS — 2 329 .NET и 503 Python теста, без failed/skipped; tooling setup, architecture, public API, UI packages, restore, игровые references и Release build также PASS.
- P `./tools/hatifect-isolated-ui-ca`: PASS — 101 CA тест, восемь UI packages и 47 projection files. Проверка выполнена на точном UI source `b23f54c`; последующие изменения до `c5174a8` затрагивают только Flow и документацию, поэтому evidence применимо к неизменной UI package boundary.
- Prepare `artifacts/validation/run-4llj99cx/summary.json`: PASS. Runtime fingerprint `18fceefeb407ad2390743c0a697cf3267d776e7056fd2bc1b8bb1a076e154f50`; candidate fingerprint `67845449374b901e3237d76f588ba432a04645de6e0e990e32ffcbb499a31a44`.
- Точный Flow platform scope: `artifacts/validation/run-4_0w8228` — PASS 797 Flow + 249 Stardew adapter tests, без failed/skipped.

## Целостный Window-путь

| Сценарий | Request ID | Assertions | Статус |
| --- | --- | ---: | --- |
| `flow.ui.player` | `add6b461-5108-4e51-bc12-1603fadbb64a` | 11 | PASS |

Сценарий прошёл через production Window и owning normalized actions: две реальные станции на сундуках, маршрут, whole/partial отправки, видимые результаты, два сохранения и загрузки, повторное открытие с `Delivered`, сохранность количества и отсутствие дублирования. Это проверяет целостность UI/domain/runtime-контракта, но normalized actions не являются доказательством физического ввода.

## Исходная игровая матрица

| Сценарий | Request ID | Assertions в result | Статус |
| --- | --- | ---: | --- |
| `flow.chest.roundtrip` | `746e137e-889f-4de3-94d2-208aa2d891f5` | 8 | PASS |
| `flow.chest.crash-after-save` | `4e2e85bd-ab87-4a9b-9386-e98ee47e592d` | 9 | PASS |
| `flow.chest.crash-after-delivery` | `90ca34e5-49a9-4144-9b72-4608737b2644` | 9 | PASS |
| `flow.chest.crash-after-unsaved-extraction` | `8ae05ac6-4242-4e0a-a65b-594a9d77682b` | 10 | PASS |
| `flow.chest.crash-after-unsaved-delivery` | `da920e56-03c8-4527-a214-cfb694b06944` | 10 | PASS |
| `flow.chest.cancellation` | `fe2ce161-d65a-4542-ba27-ce558f9474d2` | 10 | PASS |
| `flow.chest.return` | `ac98e735-ab94-4f39-a4c0-e78a5f489e8a` | 14 | PASS |
| `flow.chest.crash-after-return` | `5c4a1dca-43de-4179-8c1a-4936833f02e4` | 15 | PASS |
| `flow.chest.isolation` | `99459756-2303-4dfb-baec-05aba62dc2a4` | 12 | PASS |
| `flow.chest.performance` | `e262f285-8279-4a3c-97c1-d2a93bb466b6` | 9 | PASS |
| `flow.chest.resources` | `77d0333f-88b9-424b-b21a-92e9794f469e` | 12 | PASS |

Матрица содержит исходные 116 обязательных checks и два дополнительных `budget` assertion в performance/resources, всего 118/118 PASS. Вместе с Window-путём кандидат имеет 129/129 успешных runtime assertions.

Для каждого run повторно проверены request/scenario/status, точный `repositoryHead`, assertion count/status, отсутствие исключений у PASS-сценариев, завершённый lifecycle, process exit 0, пустые teardown errors, восстановление обоих runtime option files и cleanup request-owned сейвов. Для isolation удалены обе рабочие копии. После аудита runtime executor остановлен; `status` ожидаемо возвращает `BLOCKED: User-session executor process is unavailable`.

## Физический ввод

| Сценарий | Request ID | Assertions | Статус |
| --- | --- | ---: | --- |
| `flow.ui.player.input` | `521bde15-3a64-4deb-abfd-0040dc53fed0` | 1 | BLOCKED |

Exact native run относится к тому же `c5174a8`, но macOS вернул `CGPreflightPostEventAccess()=false` для Python runtime. Semantic test driver остановился на initialization до первого шага: `currentStep=null`, `events=[]`. Harness корректно отменил игровой процесс с exit 130, восстановил runtime options, удалил тестовый сейв и не оставил teardown errors. Этот run не подтверждает и не опровергает исправление capture retention.

Следующий повтор должен использовать неизменный кандидат после выдачи Accessibility процессу `/Library/Frameworks/Python.framework/Versions/3.14/bin/python3`. PASS требует полный physical-input путь, не более 128 captures, все пять probe phases, пять action-result evidence и финальный `Delivered`.

## Открытая приёмка

«Управление диагностической перевозкой»/«Настройка станций и маршрутов»/«Отправка груза и работа с ошибками» и «Приёмка первой альфы» остаются **IN_PROGRESS**. До «Приёмка первой альфы» DONE ещё нужны:

1. physical keyboard/pointer path на точном кандидате, включая Tab/Search/Backspace, повторные и устаревшие действия;
2. целостные EN/RU, 75/100/125/150% и controller проверки с видимым, а не только семантически присутствующим текстом;
3. точная runtime coexistence с Chests Anywhere;
4. финальный installable archive, inventory/dependencies, инструкция первого запуска и проверенная изолированная установка.

Raw evidence находится в `artifacts/runtime/<request-id>/`. Сводный hash-аудит: `artifacts/f13-root-common/q02-c5174a8-runtime-audit.json`; в нём сохранены SHA result/state/process/runtime-options/cleanup файлов, итог `auditFailures=0` и границы выводов. `artifacts` остаётся локальным и не входит в Git; этот документ связывает evidence с исходниками и не заменяет raw файлы.
