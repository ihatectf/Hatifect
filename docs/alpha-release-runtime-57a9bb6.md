# Проверка альфы в игре — 57a9bb6

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Проверено 2026-09-08. Source: `57a9bb6b0db14146811065f9e376314a9b5b11d7`. Все 688 tracked source SHA и 14 установленных DLL совпали с зафиксированным кандидатом. Это подтверждение исходной chest-матрицы, не завершение «Приёмка первой альфы» и не принятая устанавливаемая альфа.

## Общие проверки

- G `run-ifqybjf_`: PASS — 2136 .NET, 392 Python, 13 metadata checks; проверены индивидуальные TRX results.
- P `hatifect-ui-ca-isolated.x4o_c8xw`: PASS — 101 тест, восемь UI packages, 47 projection files. Package DLL совпадают с producer.
- Prepare `run-9uhat0b5`: PASS — 14 deployed DLL совпадают с producer; восемь UI DLL совпадают с проверенными пакетами.
- [CI 34253211868, attempt 2](https://github.com/ihatectf/Hatifect/actions/runs/34253211868): SUCCESS на exact source SHA. Attempt 1 отказал при загрузке артефакта HTTP 403 после успешных тестов; его evidence сохранён отдельно.

## Исходная игровая матрица

| Сценарий | Request ID | Обязательные checks | Статус |
| --- | --- | ---: | --- |
| `flow.chest.roundtrip` | `a0d36827-b4e6-492c-85fd-588a5a3f1525` | 8 | PASS |
| `flow.chest.crash-after-save` | `208e1a07-a786-4edb-ad91-ea425738a9f9` | 9 | PASS |
| `flow.chest.crash-after-delivery` | `1a7a6377-9f8f-4035-ba63-cfd7e733e0e7` | 9 | PASS |
| `flow.chest.crash-after-unsaved-extraction` | `40627545-5086-43fa-baec-c31d6c9b3b5b` | 10 | PASS |
| `flow.chest.crash-after-unsaved-delivery` | `57188294-3678-4717-906d-3296cdf23256` | 10 | PASS |
| `flow.chest.cancellation` | `3f44360b-951b-457e-b66b-025b4878710a` | 10 | PASS |
| `flow.chest.return` | `5d7f5408-a962-4941-911a-f580a8f5207f` | 14 | PASS |
| `flow.chest.crash-after-return` | `dacdf7e7-d9ed-48e3-acee-23137ba3d320` | 15 | PASS |
| `flow.chest.isolation` | `b47d7884-9232-487a-b885-03bc48ae2657` | 12 | PASS |
| `flow.chest.performance` | `47ac34a4-4173-48cb-a5ff-0919e03c503a` | 8 | PASS |
| `flow.chest.resources` | `72775215-76e7-4ed1-8856-44800ab4b1a7` | 11 | PASS |

Итого: 116 исходных checks; validator дополнительно подтвердил два `budget` checks для performance/resources. Каждый результат проверен по точному набору assertion IDs, отсутствию exceptions, process teardown и cleanup. Для crash-сценариев сопоставлены marker, фактический SIGKILL 9 / raw exit −9 и новый PID. Настройки восстановлены с проверкой фактических SHA; request-owned сейвы удалены, включая обе копии isolation. Пользовательский executor оставлен в Ready.

Flow fingerprint host/markers: `c95bbe68f7c18520f58befe882485180901d3dfb82788440c1ef7d9008997547`. Он относится к Flow host/Core/Persistence; идентичность всех остальных DLL подтверждается отдельным 14-DLL audit.

## Производительность

| Измерение | Samples после warmup | p95 tick, мс | p99 tick, мс | Allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Paused, 80 отправлений | 600 | 0.0025 | 0.002958 | 0 |
| Idle после доставки 80 отправлений | 600 | 0.003625 | 0.0045 | 0 |
| Idle при ресурсных лимитах | 600 | 0.00475 | 0.00675 | 0 |

Перевозка 80 отправлений выполнила 240 операций и 160 физических effects; максимум 64 операции за tick. Ресурсный сценарий: 256 отправлений, 32 станции, 75 routing queries, 16 попыток, шесть сохранений и семь загрузок. Измерялся owning Flow tick на x64 .NET 6.0.32 с обычным game GC. Это не измерение времени всего UI frame.

## Открытая приёмка

Обычный ввод `flow.ui.player.input`, request `f681231f-0a99-48f5-bb14-f0c11f8564f1`, завершён BLOCKED: canonical harness не получил свежий acceptance report после штатного закрытия тестовой игры. CUA возвращал `noWindowsAvailable` для coordinate input; успешный возврат pressKey не сопровождался ordinary-entry observation. Ни UI/domain guards, ни точная причина отсутствия события этим не доказаны. 17 диагностических файлов сохранены; процесс вышел с кодом 0, cleanup подтверждён. Следующий отдельный кандидат добавляет bounded pre-window telemetry для различения активности игры, raw keyboard polling и SMAPI events, без ввода или обхода guards.

Не завершены: самостоятельный обычный UI путь, требуемые EN/RU/scale/controller/physical-input и видимые результаты всего пути, coexistence с CA, финальный устанавливаемый архив и изолированная установка. Подготовка runtime не является release archive с завершённой приёмкой. «Управление диагностической перевозкой»/«Настройка станций и маршрутов»/«Отправка груза и работа с ошибками» и «Приёмка первой альфы» остаются IN_PROGRESS.

## Evidence

Raw результаты: `artifacts/runtime/<request-id>/`. Сводный аудит с SHA каждого raw файла: `artifacts/f13-root-common/q02-57a9bb6-runtime-progress.json`; проверяющий скрипт `audit-q02-runtime-progress.py`. Общие проверки: `combined-57a9bb6-run-ifqybjf_-audit.json`, `p-combined-57a9bb6-hatifect-ui-ca-isolated.x4o_c8xw-audit.json`, `prepare-57a9bb6-audit.json`. Input: `native-input-f681231f-audit.json`. Каталог `artifacts` локальный и не входит в Git; этот документ фиксирует идентичность запусков и границы выводов, а не заменяет raw evidence.
