# Q01: воспроизводимые фоновые runtime-запуски

Ограниченное исправление harness — **DONE**. Implementation: [`4c860dd53c1be9cfc53b670c6f3f81d4a65ff2c1`](https://github.com/ihatectf/Hatifect/commit/4c860dd53c1be9cfc53b670c6f3f81d4a65ff2c1), основание `0ea8bb1`, UI alpha.41. Полный Q01 остаётся **IN_PROGRESS**: этот срез не завершает [физическую проверку Backspace](Q01_NATIVE_INPUT.md).

## Причина и область

Предыдущий запрос `bea6c556-21fc-4431-9720-ebead73cd5bc` завершился FAIL: load fade оставался1.0204 при включённой паузе игры вне фокуса. Ручное отключение этой настройки только в изолированной конфигурации позволило тому же кандидату пройти aggregate. Этот FAIL и первоначальные условия сохранены в отчёте нативного ввода.

Owning implementation — `tools/live-harness/direct_runtime.py`. Перед запуском SMAPI общий runner задаёт `pauseWhenOutOfFocus=false` в двух изолированных документах: `startup_preferences` и `default_options`. Оба XML проверяются до первой записи. Временная настройка охватывает один принадлежащий запросу процесс либо две части фиксированного crash/reload-сценария. По окончании восстанавливаются исходные байты и права; изначально отсутствовавшие файлы удаляются. В диагностике остаются исходные файлы, hashes и результат восстановления каждого документа. Ошибка восстановления не даёт успешного результата; подготовка настроек не классифицируется как отказ product crash evidence.

Изменение не затрагивает UI/Flow public API, persistence, packages, acceptance criteria или обработчики ввода. Дополнительная работа ограничена двумя файлами до запуска и после завершения процесса; update/draw/layout/dispatch не изменены. Требования к Game1.IsActive и физическим событиям native-сценария сохранены. Обычные Mods, настройки и реальные сейвы не участвуют.

## Проверки

Узкая команда `rtk proxy python3 -m unittest tools.tests.test_direct_runtime` — PASS36. Добавлены семь тестов, существующая проверка запуска дополнена наблюдением эффективной конфигурации внутри процесса runner и её отсутствия после завершения. Тесты используют временные fixtures и stdlib unittest; новые зависимости не нужны.

| Requirement | Evidence |
|---|---|
| «исправлять выявленные дефекты»: временная настройка и точное восстановление после нормального выхода/ошибки игры | `test_background_options_restore_exact_bytes_after_game_changes_and_failure` проверяет true/false, соседние zoom/music значения, байты и права после записи игры. |
| Тот же запрос: отсутствующие файлы и фактическое подключение к запуску | `test_background_options_create_missing_settings_only_for_owned_run`; дополненный `test_execute_request_uses_fixed_argument_list_and_game_directory`. |
| Тот же запрос: частичная подготовка и отказ восстановления | `test_background_options_roll_back_partial_preparation`; `test_background_options_attempt_all_restorations_and_report_failure` проверяет попытку восстановить второй документ и сохранённые исходники при RestoreFailed. |
| Тот же запрос: некорректная конфигурация и сохранение изоляции | `test_background_options_validate_both_files_before_mutating_either`; `test_background_options_reject_links_without_changing_the_target` проверяет directory/file symlink и hardlink. |
| Тот же запрос: точная классификация ошибки | `test_crash_options_failure_is_not_misreported_as_product_crash_evidence`. |
| «выполнять обязательные статические, пакетные и изолированные runtime-проверки» | Финальный C и три выполненных runtime-запроса ниже; applicability G/P определена по фактическому tooling-only diff. |

`rtk proxy ./tools/hatifect-test tools` — PASS361, `run-56vjiam3`, до последнего теста классификации. После финальной правки `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` — **PASS1,396 .NET +362 Python**, `run-pxp3dxim`, без failures/skips. Все семь TRX дополнительно сверены с индивидуальными outcomes; audit сохранён в `artifacts/q01-background-options/check-audit.json`. Предварительный C `run-9z8v6jyg` относится к предыдущему состоянию Python и не подменяет финальный run.

G/P/host audit — **NOT_APPLICABLE** для этого изменения runner: игровые adapters, API, package boundary и Codex configuration не менялись. Их предыдущие выполненные результаты сохраняют собственную идентичность в [отчёте alpha.41](Q01_NATIVE_INPUT.md). Для свежего runtime каноническая `hatifect-ui-test all` подготовила текущую изолированную поставку (`run-vqnen6qg`). Command-local `DOTNET_gcConcurrent=0` использовался при сборке; минимальное окружение игры его не наследует.

| Сценарий | Запрос | Результат |
|---|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-ui-test all` | `7ed3036b-0af5-49e5-966d-1e4c8777b98b` | **PASS28**,22.682s. Исходная пауза true, восемь matrix states, runtime settings restored, terminalError null. PID96403 exit0, teardown errors отсутствуют. |
| Канонический `hatifect-live-runner smoke runtime.boot` без обоих файлов настроек | `746aa695-01d3-4d50-8222-07204100bb27` | **PASS1**,7.447s. Игра приняла минимальные XML; временные файлы удалены. Проверочная обвязка вернула исходные тестовые настройки. PID96537 exit0. |
| Канонический `hatifect-live-runner smoke flow.chest.crash-after-save` | `7a08e72f-e342-4b87-879d-d71c39583992` | **PASS9**,27.198s. PID96626 получил ожидаемый raw exit -9; новый PID96652 завершился0 без teardown errors. Файлы настроек восстановлены после обеих частей. |

Оба UI-запроса относятся к UI fingerprint `912e7b7dc41eff9d5fa0271ce7d472ca011b008ea56ff5991c78883898d8b619` (`sha256-runtime-v2`); Flow — `807b5ddc8bbc651a3cfeb8be2a89eff5fc66486450a674a5ad0cdacab965e62d` (`sha256-flow-runtime-v1`). Среда: Stardew1.6.15 build24356, SMAPI4.5.2, UI alpha.41/Dark. Aggregate измерил620 кадров при1280×720scale1: p95/p99 0.042041/0.298417ms, steady allocation4544.4 B/frame, measure/arrange miss ratios0. Сборки, CUA и image audit не пересекались с измерением.

`artifacts/q01-background-options/runtime-audit.json` связывает результаты с source commit `4c860dd`, проверяет PASS каждого assertion, завершение процессов и восстановление настроек. Исходники runner/tests после проверок совпадают с сохранёнными hashes. Исходные hashes конфигурации до/после совпали: startup `966cacbaac45a181f19efe2ccdcf1581f64147c0ef82187676bd4de704133ebb`, defaults `1d638f881dc5102d9f07363fe56d21286893aa03478b5e623f380c85899a0b16`. Переиспользованный executor29126 вернулся в Ready; worker generation9 принял `4c860dd`.

## Ограничения и следующий шаг

Принудительный SIGKILL самого executor или выключение ОС не дают выполнить восстановление в finally; исходники остаются в артефактах, и перед новым запуском требуется проверить конфигурацию. Это отличается от проверенного управляемого завершения дочернего SMAPI. Срез не заявляет устойчивость к произвольной конкурентной перезаписи private test root другим процессом.

Новые runtime-прогоны подтверждают подготовку среды и существующие сценарии. Они не являются новым PASS физического ввода, всей Flow failure matrix или полного MVP. Следующий незавершённый шаг Q01 — физический Backspace при активном окне с completed-frame evidence. Product UI/Flowline roadmap остаётся у соответствующих задач; GQ владеет общим harness и сквозной приёмкой.
