# Проверки игры в фоновом режиме

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Ограниченное исправление файловой подготовки harness — **DONE**. Implementation: [`4c860dd53c1be9cfc53b670c6f3f81d4a65ff2c1`](https://github.com/ihatectf/Hatifect/commit/4c860dd53c1be9cfc53b670c6f3f81d4a65ff2c1), основание `0ea8bb1`, UI alpha.41. Alpha.44 выявила дополнительное восстановление runtime options из сейва; последующее исправление fe51271 описано в конце документа. Полный «Проверка текущей сборки в игре» остаётся **IN_PROGRESS**: этот срез не завершает [физическую проверку Backspace](game-validation-native-input.md).

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

G/P — **NOT_APPLICABLE** для этого изменения runner: игровые adapters, API и package boundary не менялись. Их предыдущие выполненные результаты сохраняют собственную идентичность в [отчёте alpha.41](game-validation-native-input.md). Для свежего runtime каноническая `hatifect-ui-test all` подготовила текущую изолированную поставку (`run-vqnen6qg`). Command-local `DOTNET_gcConcurrent=0` использовался при сборке; минимальное окружение игры его не наследует.

| Сценарий | Запрос | Результат |
|---|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-ui-test all` | `7ed3036b-0af5-49e5-966d-1e4c8777b98b` | **PASS28**,22.682s. Исходная пауза true, восемь matrix states, runtime settings restored, terminalError null. PID96403 exit0, teardown errors отсутствуют. |
| Канонический `hatifect-live-runner smoke runtime.boot` без обоих файлов настроек | `746aa695-01d3-4d50-8222-07204100bb27` | **PASS1**,7.447s. Игра приняла минимальные XML; временные файлы удалены. Проверочная обвязка вернула исходные тестовые настройки. PID96537 exit0. |
| Канонический `hatifect-live-runner smoke flow.chest.crash-after-save` | `7a08e72f-e342-4b87-879d-d71c39583992` | **PASS9**,27.198s. PID96626 получил ожидаемый raw exit -9; новый PID96652 завершился0 без teardown errors. Файлы настроек восстановлены после обеих частей. |

Оба UI-запроса относятся к UI fingerprint `912e7b7dc41eff9d5fa0271ce7d472ca011b008ea56ff5991c78883898d8b619` (`sha256-runtime-v2`); Flow — `807b5ddc8bbc651a3cfeb8be2a89eff5fc66486450a674a5ad0cdacab965e62d` (`sha256-flow-runtime-v1`). Среда: Stardew1.6.15 build24356, SMAPI4.5.2, UI alpha.41/Dark. Aggregate измерил620 кадров при1280×720scale1: p95/p99 0.042041/0.298417ms, steady allocation4544.4 B/frame, measure/arrange miss ratios0. Сборки, CUA и image audit не пересекались с измерением.

`artifacts/q01-background-options/runtime-audit.json` связывает результаты с source commit `4c860dd`, проверяет PASS каждого assertion, завершение процессов и восстановление настроек. Исходники runner/tests после проверок совпадают с сохранёнными hashes. Исходные hashes конфигурации до/после совпали: startup `966cacbaac45a181f19efe2ccdcf1581f64147c0ef82187676bd4de704133ebb`, defaults `1d638f881dc5102d9f07363fe56d21286893aa03478b5e623f380c85899a0b16`. Переиспользованный executor29126 вернулся в Ready; worker generation9 принял `4c860dd`.

## Ограничения и следующий шаг

Принудительный SIGKILL самого executor или выключение ОС не дают выполнить восстановление в finally; исходники остаются в артефактах, и перед новым запуском требуется проверить конфигурацию. Это отличается от проверенного управляемого завершения дочернего SMAPI. Срез не заявляет устойчивость к произвольной конкурентной перезаписи private test root другим процессом.

Новые runtime-прогоны подтверждают подготовку среды и существующие сценарии. Они не являются новым PASS физического ввода, всей Flow failure matrix или полного MVP. Следующий незавершённый шаг «Проверка текущей сборки в игре» — физический Backspace при активном окне с completed-frame evidence. Product UI/Flowline roadmap остаётся у соответствующих задач; GQ владеет общим harness и сквозной приёмкой.

## Проверка каталогов резервных копий

Follow-up [`74f8010`](https://github.com/ihatectf/Hatifect/commit/74f8010ad937fba376e30bca703c23478f92f097), base `ba0db13`, устраняет обнаруженный независимым review P2. Проверки linked config files не защищали каталог назначения: symlink в `diagnostics` либо `runtime-options-originals` позволял создать резервные копии за пределами request directory. В первом случае наружу также попадал `runtime-options.json`.

`_background_game_options` теперь проверяет оба компонента существующим `_ensure_private_directory` до первой backup-записи. Он отклоняет symlink до открытия каталога или изменения его прав, проверяет владельца и устанавливает private mode только для допустимого каталога. Исходный механизм восстановления байтов/прав сохранён. Изменены только runner и его существующая stdlib unittest suite; public API, package versions, game input и persistence не меняются.

| Requirement | Evidence |
|---|---|
| Резервные копии остаются внутри изолированного request evidence | `test_background_options_reject_linked_evidence_before_copying_originals`: два subtests для `diagnostics` и `runtime-options-originals` проверяют пустой внешний каталог, его неизменные права0750, исходные config bytes и отказ до входа в контекст запуска. |
| Сохраняются обычная подготовка и восстановление | Все прежние direct-runtime tests прошли, включая точные байты/права, отсутствующие файлы, частичную подготовку, ошибку восстановления и классификацию crash evidence. |
| Исправление закрывает фактически воспроизведённый дефект | `rtk proxy python3 -m unittest -v tools.tests.test_direct_runtime`: исходный `before.log` —37 tests и2 failed subtests с созданными внешними файлами; тот же тест после исправления, `after.log` —37 tests/OK. |

`rtk proxy ./tools/hatifect-test tools` — **PASS363**, `tools/run-uh0zxyot`. Финальный `rtk proxy env HATIFECT_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet HATIFECT_TEST_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet DOTNET_gcConcurrent=0 ./tools/hatifect-check` — **PASS1 396 .NET +363 Python**, `c/run-46f1cxu2`; все фактические TRX counters и individual outcomes согласованы, failures/skips отсутствуют. Источник, hashes и оригинальные логи сохранены в `/private/tmp/hatifect-flow-q01-options-evidence/artifacts/q01-options-evidence/`. Независимый reviewer подтвердил закрытие P2 по двухфайловому diff и actual before/after evidence; иных findings в рассмотренной «Проверка текущей сборки в игре»-области не осталось.

G/P/runtime/visual — **NOT_APPLICABLE** для этого изменения проверки пути до запуска: adapter и package boundary не меняются, отрицательные сценарии выполнены на временных файловых fixtures без игры. Предыдущие игровые отчёты выше сохраняют source `4c860dd` и свои fingerprints. Защита от произвольной конкурентной подмены private request directory и восстановления после SIGKILL executor этим исправлением не заявляется. Следующий шаг — интеграция проверенного commit и продолжение незавершённой физической приёмки «Проверка текущей сборки в игре»; полный «Проверка текущей сборки в игре» остаётся IN_PROGRESS.

## Фоновый прогресс после загрузки сохранения

В alpha.44 фактическая диагностика подтвердила: `SaveGame` заменяет `Game1.options` сохранёнными options, восстанавливая pauseWhenOutOfFocus=true после успешной подготовки файлов. При unfocused single-player игра возвращалась из Update до fade. Исходные aggregate/probe FAIL сохранены; предыдущие PASS выше остаются историческими результатами своих поставок.

[`fe51271`](https://github.com/ihatectf/Hatifect/commit/fe51271c86c40198a431703ed248da91028da765) добавляет общую process policy: canonical runner явно задаёт `HATIFECT_TEST_BACKGROUND_PROGRESS=1`, обязательный UI game adapter подключает helper только вместе с MODE/AUTOMATED flags. Helper перед Update отключает pauseWhenOutOfFocus у текущего Options, не удерживая старый экземпляр через load/title; при disposal отписывается. Это охватывает UI и Flow. Обычный запуск, Game1.paused, physical input и критерии готовой отрисовки не меняются; golden и подтверждённые marker-ом bytes сейва не переписываются подготовкой. Файловое восстановление по завершении процесса сохранено. Текущая эксплуатационная схема — в [RUNTIME.md](RUNTIME.md#фоновые-прогоны-и-настройки-игры).

Три усиленных launcher tests дали RED3→GREEN3, tools — PASS366, C — PASS1466 .NET +366 Python, G — PASS1707 .NET +366 Python, P — PASS90 с восемью exact package/producer/game DLL. Повторный aggregate `bcc44b11-0b67-49ea-bafe-e9d42fc4376b` — PASS28: фактические unfocused/pause=false/fade=-0.0304 и восемь просмотренных EN/RU×scale PNG. Crash-after-return `09170474-c9c7-466c-858b-34df70579f7e` — PASS15: два distinct процесса, сохранение cargo21 и request-owned cleanup; config bytes/modes восстановлены и golden hashes неизменны. Точные команды, source identities, итоговый статус общей приёмки и ограничения — в [отчёте alpha.44](game-validation-alpha44-integration.md#исправленная-поставка-fe51271). Independent bounded source review не сообщил открытых findings; runtime и performance принимаются отдельно по выполненным запросам.
