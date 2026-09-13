# Разработка и проверка

UI authoring: [подключение language server, экспорт метаданных, операции редактора и ограничения](UI_AUTHORING.md).

Runtime-диагностика: [failure envelope](FAILURE_ENVELOPE.md), [semantic events](SEMANTIC_EVENTS.md).

Последовательность развития и acceptance criteria — в [утверждённой roadmap](ROADMAP.md). После каждого среза обновляются его статус, commit/evidence и следующий готовый шаг; переходы в рамках утверждённой задачи не требуют повторного согласования.

## Инструменты

`global.json` задаёт SDK .NET 8; проекты сохраняют `net6.0`. Для выполнения тестов нужен .NET 6 runtime. Предупреждение SDK `NETSDK1138` о завершении поддержки net6 остаётся видимым: целевой runtime сохранён для совместимости с игрой. Каноническая сборка не повышает только это предупреждение до ошибки; compiler/analyzer warnings и ошибки аудита NuGet по-прежнему блокируют её. Python tooling использует стандартную библиотеку Python 3.11+ (`tomllib` для конфигурации Codex). На Apple Silicon для тестов игрового графа нужен совместимый x64 .NET SDK/runtime. Явные пути задаются через `HATIFECT_DOTNET`, `HATIFECT_TEST_DOTNET` и, при необходимости, `HATIFECT_GAME_PATH`.

Не добавляй вторую вручную поддерживаемую solution: `Hatifect.slnx` — единственный реестр проектов. Скрипты создают временный `.sln` для SDK 8. Новый проект должен сразу входить в solution и соответствовать архитектурным ограничениям.

## Команды

| Команда | Результат |
|---|---|
| `./tools/hatifect-check` | Статика, Python tests, build и все .NET suites без игры |
| `./tools/hatifect-check --platform` | Те же проверки плюс игровые проекты и CA tests |
| `./tools/hatifect-test ui` | Текущие семантические UI suites |
| `./tools/hatifect-test flow` | Core/Persistence/application/semantic Flowline tests |
| `./tools/hatifect-test flow --platform` | Дополнительно реальные Item/Chest и игровая session save/reload модель, без запуска игры |
| `./tools/hatifect-test ca --platform` | CA consumer tests с игровыми references |
| `./tools/hatifect-test tools` | Python tooling tests |
| `./tools/hatifect-agent-check` | Переносимая проверка config, roles, skill routing и instruction budget |
| `./tools/hatifect-agent-check --host` | Реальная загрузка проекта и доступность skills в локальном Codex |
| `./tools/hatifect-agent-check --route flowline` | Та же проверка с обязательной доступностью skills выбранного маршрута |
| `./tools/hatifect-pack-ui` | Точные локальные UI packages из текущих исходников |
| `./tools/hatifect-isolated-ui-ca` | Сборка consumer из UI packages без исходников framework |
| `./release.sh` | Локальная сборка и проверка runtime-архива без установки и публикации |

`--no-build` у `hatifect-test` подходит только для уже собранного текущего Release. Результаты новых запусков сохраняются отдельно в `artifacts/validation/`: команды, логи, TRX и `summary.json`. Для каждой выбранной .NET suite требуются ненулевые total/executed/passed и успешные индивидуальные результаты. Пропуск или ошибка теста не превращаются в PASS.

Проверка изоляции CA использует только подготовленные локальные feeds и собственный временный package cache; её offline restore не выполняет аудит NuGet. Аудит выполняется обычной канонической сборкой. Внутри временного каталога также проверяется копирование двух CA DLL в тестовую структуру установки.

При изменении UI producer сначала пересобирается локальный feed. Версию UI меняют совместно в package authority и manifests по принятому release-процессу; потребители не используют плавающие версии. `artifacts/`, `bin/`, `obj/` и локальный feed не коммитятся.

После упаковки всего выбранного графа `hatifect-pack-ui` сверяет DLL каждого пакета с итоговым `bin/Release/net6.0` owning project. Отсутствующий output или несовпадение байтов означает FAIL; для расхождения выводятся обе SHA-256. Это же условие действует при отдельном `ui_packages.py verify-feed`: сохранённый feed проверяется относительно текущих producer outputs. Проверка не пересобирает и не заменяет артефакты при отказе. Подробный разбор исходного смешанного feed — в [отчёте проверки идентичности пакетов](Q01_PACKAGE_IDENTITY.md).

Общий `Directory.Build.targets` восстанавливает revision/source-root metadata, если SDK обнаружил Git repository, но потерял metadata в linked worktree с packed refs. Recovery использует только read-only Git queries и завершает сборку ошибкой при их отказе; обычные source archives и CA projection без Git не требуют этих запросов. Канонический build запускает `tools/build_metadata_tests.py` после выбора SDK; статическая стадия остаётся Python-only. [Контракт, регрессии и статус фактической приёмки](BUILD_SOURCE_IDENTITY.md).

## CI и ветки

GitHub Actions запускает текущую статическую проверку, build и автоматически полученную из solution матрицу .NET тестов без игры. Финальный обязательный check — `Hatifect CI / CI Gate`; любой неуспешный или пропущенный prerequisite блокирует его. После публикации новой истории настрой branch protection на этот check и удали старые required checks, которых больше нет в workflow.

Игровые assemblies не скачиваются из личной установки в GitHub Actions. Проверка `--platform`, изоляция CA и runtime acceptance выполняются в среде с установленной игрой и прикладываются как отдельные результаты. Зелёный host-free CI сам по себе не доказывает работоспособность игрового адаптера.

Работай небольшими сквозными PR от `develop`: контракт, owning implementation, affected consumer и проверки входят вместе. До обновления task branch сохрани собственные изменения обычным commit. Worktree других задач не изменяй. Новая история этой базы независима от старой; старые ветки нельзя слепо вливать merge-коммитом. Незавершённые изменения переносятся осмысленным diff поверх новой базы с повторной проверкой.

Для выбора следующей работы используй [roadmap](ROADMAP.md): она содержит ID срезов, зависимости, decision gates и рекомендуемые уровни reasoning. Завершённый срез обновляет свой статус, ссылки на commit/evidence и следующий готовый шаг. Шаблоны задания и handoff находятся там же; roadmap описывает будущую работу и не заменяет текущую архитектуру или acceptance-файлы.

## Среда Codex

Корневой и локальные `AGENTS.md` задают обязательные правила и владельцев областей. Переносимый
[project skill](../.agents/skills/hatifect-development/SKILL.md) и его
[`routing.json`](../.agents/skills/hatifect-development/routing.json) помогают выбрать только
относящиеся к задаче инструкции и skills. Модели, reasoning и порядок делегации определяет
корневой `AGENTS.md`; личные model, permissions, sandbox и MCP не переопределяются проектом.
Отдельные project skills покрывают semantic UI, Flowline, adapters, test authoring, bounded
diagnostics, runtime acceptance и выбор тестов; они лежат в `.agents/skills/` и загружаются только
для соответствующей задачи.

`./tools/hatifect-agent-check` проверяет project config, роли, маршруты и instruction budget;
`--host` — их фактическую загрузку Codex, не создавая задачу и не вызывая модель. Для нового
worktree требуется доверие точному Git-корню. После изменения Codex-конфигурации, AGENTS, ролей
или project skill начни новую сессию и выполни `./tools/hatifect-agent-check --host`.

## Добавление подсистемы

Сначала определи владельца состояния и публичный capability/application contract. Добавь owning и
test projects в `Hatifect.slnx`, разрешённые зависимости — в architecture checker, а специфичные
правила — в локальный AGENTS или маршрут только при необходимости. UI consumer получает изменения
через явный application boundary; Flow Core/Persistence не зависит от UI или игровых API.

## Переименование Hatifect

Текущие идентификаторы, сохранённые wire/persistence значения и порядок обновления установки
описаны в [руководстве миграции](HATIFECT_MIGRATION.md). Старые и новые модули одновременно не
устанавливаются; compiled consumers пересобираются с пакетами Hatifect.

## Runtime harness

Сценарии и transport schemas находятся в `tools/live-harness/`; канонические статусы —
PASS/FAIL/BLOCKED/NOT_APPLICABLE. `result.json` protocol v1 остаётся авторитетным, raw evidence —
отдельным. Non-PASS диагностика описана в [failure envelope](FAILURE_ENVELOPE.md) и
[semantic events](SEMANTIC_EVENTS.md). Подготовка, singleton/Ready, владение executor и безопасное
завершение — в [руководстве runtime-тестов](RUNTIME.md).
