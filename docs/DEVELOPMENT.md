# Разработка и проверка

UI authoring: [подключение language server, экспорт метаданных, операции редактора и ограничения](UI_AUTHORING.md).

Последовательность развития и acceptance criteria — в [утверждённой roadmap](ROADMAP.md). После каждого среза обновляются его статус, commit/evidence и следующий готовый шаг; переходы в рамках утверждённой задачи не требуют повторного согласования.

## Инструменты

`global.json` задаёт SDK .NET 8; проекты сохраняют `net6.0`. Для выполнения тестов нужен .NET 6 runtime. Предупреждение SDK `NETSDK1138` о завершении поддержки net6 остаётся видимым: целевой runtime сохранён для совместимости с игрой. Каноническая сборка не повышает только это предупреждение до ошибки; compiler/analyzer warnings и ошибки аудита NuGet по-прежнему блокируют её. Python tooling использует только стандартную библиотеку Python 3.11+. На Apple Silicon для тестов игрового графа нужен совместимый x64 .NET SDK/runtime. Явные пути задаются через `HATIFECT_DOTNET`, `HATIFECT_TEST_DOTNET` и, при необходимости, `HATIFECT_GAME_PATH`.

Не добавляй вторую вручную поддерживаемую solution: `Hatifect.slnx` — единственный реестр проектов. Скрипты создают временный `.sln` для SDK 8. Новый проект должен сразу входить в solution и соответствовать архитектурным ограничениям.

## Команды

| Команда | Результат |
|---|---|
| `./tools/hatifect-test` | Progressive regression по текущим Git changes; без изменений — полный host-free gate |
| `./tools/hatifect-test all` | Прямой запуск всех host-free .NET suites, без progressive selection |
| `./tools/hatifect-check` | Статика, Python tests, build и все .NET suites без игры |
| `./tools/hatifect-check --platform` | Те же проверки плюс игровые проекты и CA tests |
| `./tools/hatifect-test ui` | Текущие семантические UI suites |
| `./tools/hatifect-test flow` | Core/Persistence/application/semantic Flowline tests |
| `./tools/hatifect-test flow --platform` | Дополнительно реальные Item/Chest и игровая session save/reload модель, без запуска игры |
| `./tools/hatifect-test ca --platform` | CA consumer tests с игровыми references |
| `./tools/hatifect-test tools` | Python tooling tests |
| `./tools/hatifect-pack-ui` | Точные локальные UI packages из текущих исходников |
| `./tools/hatifect-isolated-ui-ca` | Сборка consumer из UI packages без исходников framework |
| `./release.sh` | Локальная сборка и проверка runtime-архива без установки и публикации |

`--no-build` у `hatifect-test` подходит только для уже собранного текущего Release. Результаты новых запусков сохраняются отдельно в `artifacts/validation/`: команды, логи, TRX и `summary.json`. Для каждой выбранной .NET suite требуются ненулевые total/executed/passed и успешные индивидуальные результаты. Пропуск или ошибка теста не превращаются в PASS.

Проверка изоляции CA использует только подготовленные локальные feeds и собственный временный package cache; её offline restore не выполняет аудит NuGet. Аудит выполняется обычной канонической сборкой. Внутри временного каталога также проверяется копирование двух CA DLL в тестовую структуру установки.

При изменении UI producer сначала пересобирается локальный feed. Версию UI меняют совместно в package authority и manifests по принятому release-процессу; потребители не используют плавающие версии. `artifacts/`, `bin/`, `obj/` и локальный feed не коммитятся.

После упаковки всего выбранного графа `hatifect-pack-ui` сверяет DLL каждого пакета с итоговым `bin/Release/net6.0` owning project. Отсутствующий output или несовпадение байтов означает FAIL; для расхождения выводятся обе SHA-256. Это же условие действует при отдельном `ui_packages.py verify-feed`: сохранённый feed проверяется относительно текущих producer outputs. Проверка не пересобирает и не заменяет артефакты при отказе. Подробный разбор исходного смешанного feed — в [отчёте проверки идентичности пакетов](Q01_PACKAGE_IDENTITY.md).

Общий `Directory.Build.targets` восстанавливает revision/source-root metadata, если SDK обнаружил Git repository, но потерял metadata в linked worktree с packed refs. Recovery использует только read-only Git queries и завершает сборку ошибкой при их отказе; обычные source archives и CA projection без Git не требуют этих запросов. Канонический build запускает `tools/build_metadata_tests.py` после выбора SDK; статическая стадия остаётся Python-only. [Контракт, регрессии и статус фактической приёмки](BUILD_SOURCE_IDENTITY.md).

Выбор progressive по умолчанию реализован в `tools/validation.py`, а правила scope — в
`tools/progressive_regression.py` и `tools/regression-selection.json`. Явные scope,
`--project`, `--platform`, `--host-free` и `--no-build` сохраняют прямой запуск без
progressive; `--results-directory` сам по себе не отключает progressive.
`hatifect-check` остаётся независимым полным gate.
Подробнее: [progressive regression](PROGRESSIVE_REGRESSION.md).

## CI и ветки

GitHub Actions запускает текущую статическую проверку, build и автоматически полученную из solution матрицу .NET тестов без игры. Финальный обязательный check — `Hatifect CI / CI Gate`; любой неуспешный или пропущенный prerequisite блокирует его. После публикации новой истории настрой branch protection на этот check и удали старые required checks, которых больше нет в workflow.

Основной workflow остаётся быстрым PR-gate на Ubuntu. Отдельный `Compatibility` workflow
еженедельно и вручную выполняет полный `./tools/hatifect-check` на актуальных macOS и Windows;
он расширяет проверяемую матрицу, не задерживая каждое изменение. Все сторонние Actions
закреплены полными commit SHA. Обновления GitHub Actions выполняются вручную отдельным PR с
проверкой совместимости и повторным закреплением полного commit SHA.

Игровые assemblies не скачиваются из личной установки в GitHub Actions. Проверка `--platform`, изоляция CA и runtime acceptance выполняются в среде с установленной игрой и прикладываются как отдельные результаты. Зелёный host-free CI сам по себе не доказывает работоспособность игрового адаптера.

Работай небольшими сквозными PR от `develop`: контракт, owning implementation, affected consumer и проверки входят вместе. До обновления task branch сохрани собственные изменения обычным commit. Worktree других задач не изменяй. Новая история этой базы независима от старой; старые ветки нельзя слепо вливать merge-коммитом. Незавершённые изменения переносятся осмысленным diff поверх новой базы с повторной проверкой.

Для выбора следующей работы используй [roadmap](ROADMAP.md): она содержит ID срезов, зависимости и decision gates. Завершённый срез обновляет свой статус, ссылки на commit/evidence и следующий готовый шаг. Шаблоны задания и handoff находятся там же; roadmap описывает будущую работу и не заменяет текущую архитектуру или acceptance-файлы.

## Добавление будущей подсистемы

Сначала определи владельца состояния и публичный capability/application contract. Затем добавь проекты и test project в `Hatifect.slnx`, зафиксируй разрешённые зависимости в architecture checker и напиши короткий README с текущей готовностью и командой проверки.

UI consumer читает контракт своего subsystem; изменения application state доходят через явно реализованный boundary. Git/CI обеспечивают согласованное изменение кода и обратную связь о несовместимости. Автоматическое редактирование кода соседней системы после каждого изменения не является механизмом runtime-синхронизации. Flowline использует IFlowApplication: cached snapshots, команды с session/revision и уведомления после commit. IFlowNetworkApplication добавляет управление сетью, fingerprint отправки, возврат, recovery и холодные inventory queries. Semantic DLL входит в Flow runtime; UI assemblies остаются только в UI module.

## Идентичность Hatifect

Проекты, assemblies, namespaces, package IDs, SMAPI UniqueID, команды и harness environment
используют только идентичность Hatifect. Автор — `ihatectf`, официальный GitHub URL —
`ihatectf/Hatifect`, переменные конфигурации — `HATIFECT_*`. Локальный файл создаётся из
`tools/hatifect.env.example` как `tools/hatifect.env` и не коммитится. Альтернативные
брендовые идентификаторы и compatibility aliases не поддерживаются.

## Runtime harness

Runtime transport и JSON schemas сохранены для дальнейшей acceptance-проверки. Каталог сценариев — `tools/live-harness/scenarios.json`; результаты имеют статусы PASS/FAIL/BLOCKED/NOT_APPLICABLE. Сценарии `flow.route.basic` и `flow.save.isolation` проверяют диагностический fake-provider маршрут и lifecycle Flowline. Отдельный `flow.chest.roundtrip` проверяет production-сессию и настоящую запись/загрузку request-owned сейва. `flow.chest.crash-after-save` завершает принадлежащий executor процесс через SIGKILL после подтверждённого Saved и запускает новый SMAPI с той же копией; это одна фиксированная crash/reload граница, а не вся failure matrix. Оба chest-сценария запускаются отдельно от aggregate. UI aggregate `all` проверяет текущие семантические поверхности и совместимый CA Overlay; отрицательные конфигурации CA запускаются отдельно.

Когда задача или выбранный срез roadmap требует runtime-проверки, разработчик запускает канонический executor в терминале текущего worktree. Порядок подготовки, проверки singleton/Ready, владения терминалом и завершения процесса описан в [руководстве runtime-тестов](RUNTIME.md).
