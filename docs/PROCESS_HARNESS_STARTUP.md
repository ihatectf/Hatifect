# Бюджет запуска fixture в проверке stdout/stderr

Исправление `0041788d19dd5f0f8b868312985933f787f4ca93` даёт новому исполняемому fixture время пройти штатную проверку запуска macOS. Меняется только `test_fake_executable_captures_stdout_and_stderr`: внутренний бюджет 2 → 10 секунд, внешний watchdog 5 → 15 секунд. Проверки вывода и успешного завершения сохранены. Это ограниченное исправление теста, а не завершение U03/Q01 или общей приёмки alpha.47.

## Наблюдаемая причина

В UI worktree на `f8ea1e6dd0c6df3f3ac5f32eae155ac8948a8701` два полных C завершились с единственным Python FAIL: supervisor вернул 124 вместо 0. Исходный тест создаёт временный executable `fake-smapi` с shebang текущего Python и двумя `print(..., flush=True)`. Supervisor и тест побайтно совпадали с общей базой `08ecdb94102fb7311c1fe21eabc9843f7e4888ef`.

Профили двух принадлежащих тесту дочерних процессов, PID 35725 и 35732, показывали `_dyld_start` и 96 KB footprint. Unified log связал это наблюдение с проверкой исполнения macOS:

| Наблюдение | Фактические данные |
|---|---|
| Запуск PID 35725 | 2026-09-08 05:56:20.776 +03:00 |
| Прерванное ожидание AppleSystemPolicy | 05:56:22.792, примерно через 2.016 секунды |
| Последующая обработка XProtect | 05:56:23.787: временный `fake-smapi` уже отсутствует; ENOENT и Gatekeeper rejection |
| PID 35732 | Та же последовательность, ожидание прервано примерно через 2.024 секунды |
| Отдельный запуск с бюджетом 10 секунд | Тот же Python entry point, побайтно одинаковый fake script и неизменный supervisor; exit 0, оба ожидаемых сообщения за 3.06494 секунды |
| Разрешение ОС для отдельного запуска | 06:07:24.431: `AppleSystemPolicy` разрешает точный `tmpizbvm11f/fake-smapi`; supervisor завершается в 06:07:24.449 |

Эти данные объясняют наблюдаемый конфликт двухсекундного бюджета с обычной проверкой запуска. Поздний Gatekeeper rejection не доказывает первоначальный постоянный запрет: он появился после прерывания ожидания и удаления fixture. Причина продолжительности самой OS assessment не установлена; диагноз не распространяется автоматически на каждый исторический timeout.

Сравнение двух точек входа той же установки Python 3.14.6 arm64 дало A/B/A/B = FAIL/FAIL/PASS/PASS. Поэтому entry point не меняется. Настройки безопасности, quarantine, PATH и установка Python не изменялись. Отдельный десятисекундный диагностический запуск не считается acceptance исходного двухсекундного теста; прежние FAIL сохранены.

## Сохранённые контракты

`tools/live-harness/run_process.py`, `tools/validation.py` и канонические shell entrypoints не меняются. Нет изменений игровых тайм-аутов, отмены, process groups, teardown или UI/Flow API.

| Поведение | Существующая проверка |
|---|---|
| Успешный запуск, stdout и stderr в логе | `test_fake_executable_captures_stdout_and_stderr`: прежние executable, shebang, grace 0.1, exit-0 и обе текстовые assertions |
| Тайм-аут и завершение потомка | `test_process_supervisor_times_out_and_retires_descendants`: прежний deadline 0.3 секунды, exit 124 и проверка исчезновения descendant |
| Отмена и граница владения | `test_cancellation_retires_owned_tree_but_not_unrelated_process`: exit 130, завершение своего child, выживание unrelated process |

Сверка AST подтвердила, что остальные 59 методов файла не изменились; в целевом методе изменены только два бюджета времени. Независимый read-only reviewer не нашёл замечаний. Десять секунд — ограниченный запас относительно наблюдаемой задержки, а не гарантия максимальной продолжительности любой проверки macOS.

## Проверки и evidence

Проверки выполнены в отдельном `feature/process-harness-startup` worktree. Scoped tools запускался до коммита на том же проверенном содержимом файлов; полный C выполнен на точном source commit `0041788d19dd5f0f8b868312985933f787f4ca93`. Общий worktree alpha.47 остался на `08ecdb9`.

| Команда | Результат |
|---|---|
| `rtk proxy ./tools/hatifect-test tools` | PASS 381, failed 0, skipped 0; `run-vj2nc_2z` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | PASS 1604 .NET + 381 Python; `run-_l7idcr5`, семь фактических TRX |

Использован штатный `bin/python3` Python 3.14.6 arm64; C выбрал установленный x64 .NET SDK 8 с .NET 6 runtime. Запуски выполнялись с разрешённым host execution. Локальная переменная GC относится к команде статической сборки и не переносится в игровой runtime.

Integration reviewer проверил индивидуальные Python результаты, ненулевые и согласованные TRX counters/rows и чистый source после C. Исходные логи и TRX скопированы с проверкой SHA-256. Reviewer отдельно проверил исходники и scoped Python evidence; полный C проверен integration reviewer.

Общий каталог сохранённых доказательств: `artifacts/process-harness-diagnostic-20260908/` в GQ worktree. Он содержит `policy-events.log/json`, `longer-observation.json`, `longer-policy-events.log`, исходные owner samples/A-B evidence, `source-diff-audit.json`, `diagnostic-audit.json`, `validation-audit.json`, `independent-review.md`, raw `validation/` и контрольные суммы. Исходные пути внутри raw logs сохранены.

Для этого изменения теста G/P/RUNTIME/VISUAL/PERF — NOT_APPLICABLE. Их обязательность для UI/Flow candidate сохраняется. Исправление не объясняет отдельное зависание игрового bootstrap и не публикует alpha.47.

## Принятие в UI и общую ветку

UI принял точное содержимое исправленного теста как `ab320dcd6fe4599c6292500b334f045cf62ed959`. Его свежий C `run-nmk_m8fc` — PASS: 1 619 .NET + 381 Python; GQ независимо сверил семь TRX, исходный Python log и сохранил raw копию в `artifacts/process-harness-diagnostic-20260908/owner-validation/`. Последующие изменения UI glyph/input/layout имеют собственную приёмку и не получают PASS этого C автоматически.

GQ перенёс точное исправление из `0041788` и этот отчёт поверх общего `79da4aa`. Новый `rtk proxy ./tools/hatifect-test tools`, `run-ck0vhowb`, — PASS: 381 тест, без ошибок и пропусков. Проверены все 381 индивидуальных результата, включая stdout/stderr, короткий timeout с завершением потомка и cancellation с сохранением unrelated process. SHA-256 целого изменённого файла `fb3af937af5eac40a69ace063ec8f6cc4352227ae5450c378db27d2cd30ac731` совпадает с `0041788`; production supervisor и остальные исходники tooling не менялись. Запуск выполнен до integration commit на том же содержимом теста. Evidence: `root-integration-tools-audit.json` в каталоге общей диагностики.

Полный C общего итогового candidate остаётся обязательным и будет выполнен после принятия законченного UI handoff; C на `0041788` и C на `ab320dc` сохраняют собственные source identities. Следующий общий шаг — интеграция проверенных U03 messages, typed CA input и исправления layout в framework, затем C/G/P и оставшаяся native/PERF/input приёмка alpha.47.
