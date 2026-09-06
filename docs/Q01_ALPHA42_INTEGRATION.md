# Q01: общая runtime-проверка alpha.42

Проверенный UI candidate — [`9b8f5e6`](https://github.com/ihatectf/Hatifect/commit/9b8f5e6beea9691c8d8644230a4075c8b57c4e24), включающий production action binding `790288b`, FLOWLINE admission repair `ae8ef7d`/`60c82ce` и Q01 `ba0db13`. Последующая опубликованная интеграция [`db9332c`](https://github.com/ihatectf/Hatifect/commit/db9332c3deede5458e42c58498d644ea094aca88) добавляет проверку каталогов runtime evidence `74f8010` и документацию; UI, Flow, CA и package inputs остаются прежними. Этот отчёт фиксирует общую регрессионную проверку. Полные **U03 и Q01 остаются IN_PROGRESS**.

GQ отвечает за общую интеграцию, harness и сквозную приёмку. Следующие продуктовые срезы UI и Flowline остаются у профильных задач. Новых API, package versions, persistence contracts или изменений product source в этом отчёте нет.

## Исходники и статические проверки

Read-only merge preflight исходных `ba0db13` и `8014a7c` выявил только конфликт журнала roadmap. Итоговая интеграция `9b8f5e6` отличается от trial tree только разрешённым журналом. Сохранены 16 Q01 postimages и 36 incoming non-Markdown postimages; compositor объединяет независимые изменения пустого Search и action-state rendering. Проверены 49 non-Markdown inputs объединённого candidate по manifest владельца. Это ограниченный интеграционный проход GQ; независимый source review владельца описан отдельно в [журнале U03-b](ROADMAP-STATUS.md#u03-b-production-action-binding-and-captured-presentation).

| Gate | Проверенный источник | Результат и evidence |
|---|---|---|
| C: `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | `9b8f5e6` | PASS 1 431 .NET +362 Python; owner `run-vokns0vr`, 7 фактических TRX |
| G: `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | `9b8f5e6` | PASS 1 672 .NET +362 Python; owner `run-darycblx`, 10 фактических TRX |
| C после evidence-directory guard | `9c2435d`, опубликован с документацией как `db9332c` | PASS 1 431 .NET +363 Python; owner `run-c_kg9d9v`, 7 фактических TRX |
| GQ P: `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | UI/package source `9b8f5e6` | PASS 90; `hatifect-ui-ca-isolated.gk0wtdmj` |
| CI опубликованной интеграции | exact `db9332c` | [SUCCESS, все 10 jobs включая CI Gate](https://github.com/ihatectf/Hatifect/actions/runs/34039712393) |

GQ прочитал individual outcomes и counters всех перечисленных TRX: количества согласованы, failures/skips отсутствуют. Последние два Python postimages совпадают с проверенной интеграцией владельца. C/G повторно не запускались здесь при наличии проверенных результатов для тех же исходников. Owner validation находится в `/private/tmp/hatifect-ui-next-ten-slices/artifacts/validation/`; его команды используют явный x64 SDK. `DOTNET_gcConcurrent=0` ограничен процессами сборки/тестов и не передаётся игре.

P проверил 44 точных projected files, восемь alpha.42 packages, 90 фактических CA tests, три assets files из изолированного cache и две точные deployed CA DLL. UI source отсутствует в consumer projection. SHA-256 всех восьми packaged UI DLL совпадают одновременно с producer outputs этого worktree и UI DLL в игровой изолированной поставке. Отдельный owner P имеет другую build identity; его DLL не приписываются игровым прогонам GQ. Первый owner P с несовпавшими informational versions сохранён как не прошедший дополнительный byte-аудит; исправленный повтор описан в журнале владельца.

## Игровые прогоны

Рабочая копия: `${HOME}/Developer/Worktrees/Codex/345f/Hatifect`. Каноническая подготовка `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-live-prepare` прошла как `run-ak_dyn90` на `9b8f5e6`. Уже живой executor PID29126 переиспользован после проверки `status`/Ready. Изолированная поставка находится только в `.smapi-test/isolated`.

Каждый сценарий вызван через `rtk proxy ./tools/hatifect-live-runner <kind> <scenario> <result.json> <artifact-directory>`; точный argv сохранён в `artifacts/q01-rendered-matrix/alpha42-integrated-*-request.json`. Artifact directory — `artifacts/runtime/<requestId>`, result — его `result.json`. Helper `run-request.py` только назначает request ID и передаёт эти аргументы каноническому runner.

| Scenario / kind | Request ID | Checkout при запросе | Результат |
|---|---|---|---|
| `all` / `ui` | `6c0fecbc-cc1f-4b40-b6ed-22fd0cdde244` | `9b8f5e6` | PASS 28, 28.508 s, PID1365 exit0 |
| `flow.route.basic` / `smoke` | `2d183d83-71f7-4863-a3b2-a473d1ee3606` | `9b8f5e6` | PASS 6, 10.421 s, PID1613 exit0 |
| `flow.save.isolation` / `smoke` | `00f184be-dc0b-4722-845b-4d34f1ab26d3` | `9b8f5e6` | PASS 7, 10.114 s, PID1887 exit0 |
| `semantic.performance` / `ui` | `25070893-f4a6-484b-b9b5-75f8c9873133` | `db9332c`, неизменные UI DLL | PASS 2, 17.613 s, PID4285 exit0 |

UI fingerprint всех UI-прогонов — `c562ff6eca522c1c6f66c520a94d849e3a7b720e6fe012ce171875f8e08db221`, algorithm `sha256-runtime-v2`, UI alpha.42. Flow fingerprint обоих fake-сценариев — `806ead954db3ff7e2f41b436095eaf88a53dc0d968e889b835538b4afa94b126`. Runtime reports сохраняют собственный checkout и fingerprint; последующий commit документации их не меняет.

Все четыре owned game processes завершились с exit0 и пустыми teardownErrors. Options lease имеет state Restored и пустые errors; исходные bytes восстановлены. Проверенные SHA-256: startup_preferences `966cacbaac45a181f19efe2ccdcf1581f64147c0ef82187676bd4de704133ebb`, default_options `1d638f881dc5102d9f07363fe56d21286893aa03478b5e623f380c85899a0b16`. В последнем запросе supervisor автоматически перезапустил worker для нового harness digest; второй executor не создавался. Переиспользованный supervisor оставлен работающим.

## Performance и визуальные границы

Aggregate захватил 620 кадров: p95/p99 0.059709/0.342167 ms, 4569.123 B/frame, measure/arrange cache miss ratios 0. Перед запуском активная сборка не наблюдалась, однако её последующее пересечение с owner P не удалось исключить. Поэтому этот результат не используется как доказательство отсутствия конкурентной сборки.

Отдельный `semantic.performance` на тех же восьми UI DLL захватил 620 кадров: **p95/p99 0.048083/0.366583 ms**, **4605.484 B/frame**, оба cache miss ratios **0**. Проверены действующие пределы scenario catalog: минимум600 кадров, p95≤2ms, p99≤4ms, allocations≤16384B/frame, miss ratio≤0.2. Surface — semantic-terminal-menu, Dark, 1280×720, scale1. Одиннадцать наблюдений процессов с интервалом2s не обнаружили dotnet/MSBuild. GQ не выполнял build, CUA или визуальный аудит во время выборки. Это измерение диагностического Terminal; оно не является сравнительным A/B benchmark, полной нагрузкой Flow или доказательством отсутствия любой фоновой активности ОС.

Первый клиент попытки `af1c421f-0968-4885-9742-5567874df6e0` завершился до принятия запроса executor: sandbox запретил `ps` в observation wrapper. Specific PID4097 затем отсутствовал, executor оставался Ready, runtime directory пуст. Эта попытка сохранена как BLOCKED до запуска; PASS ей не присвоен. Разрешённый повтор чтения процессов и канонического сценария дал указанный выше результат.

Aggregate сохранил восемь completed-frame matrix states и восстановил настройки matrix; terminalError отсутствует. После первоначального прохода двух кадров завершён просмотр **всех восьми composed PNG**: EN/RU ×75/100/125/150% ×Dark, 1280×720. Diagnostic Terminal, две отдельные кнопки и focus outline находятся внутри верхней левой области, полный English/Russian probe — внутри нижней границы; обрезка текста и пустой кадр не наблюдаются. Game/scene locale, desired/base/pixel scale и completed frames3–17 согласованы с исходным runtime.json. Кнопки diagnostic fixture сохраняют английские названия и в RU. Дополнительно просмотрен completed-frame `semantic-performance.png`. `visual-matrix-audit.json` фиксирует SHA-256 и наблюдение для каждого из восьми кадров. Просмотр выполнен после runtime/PERF и не изменяет изображения; он не сертифицирует физический ввод, другие темы, borderless macOS или локализацию всех product screens.

## Evidence и следующий шаг

Локальный индекс `artifacts/alpha42-integration-preflight/` содержит `preflight.json`, `owner-validation.json`, `final-integration-audit.json`, `package-audit.json`, `gq-package-audit.json`, `gq-runtime-package-comparison.json`, `runtime-audit.json`, `performance-audit.json`, `performance-process-observations.json`, `visual-matrix-audit.json` и исходные command logs. Runtime request directories содержат исходные reports, process/transport evidence, SMAPI logs и screenshots.

Ограниченная общая регрессия alpha.42 подтверждена. Для **Q01** по-прежнему требуется завершённое наблюдение физического Backspace в активном окне с completed-frame evidence; предыдущий native FAIL сохраняется в [Q01_NATIVE_INPUT.md](Q01_NATIVE_INPUT.md). Новый native-запуск без доступного участника не выполнялся.

Для **U03** владелец UI продолжает owning Stardew Update/root/portal pump, lifecycle/generation retirement, конкретный typed consumer и обязательную игровую приёмку этого поведения. Выполненные fake Flow basic/isolation не заменяют полную реальную Flow MVP acceptance. Следующий общий шаг — приёмка готового product candidate либо завершение физического Q01 при доступности пользователя. Host Codex audit — NOT_APPLICABLE: configuration/skills не менялись.
