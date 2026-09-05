# Разработка и проверка

UI authoring: [подключение language server, экспорт метаданных, операции редактора и ограничения](UI_AUTHORING.md).

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
| `./tools/hatifect-test flow` | Core/Persistence Flowline tests |
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

## CI и ветки

GitHub Actions запускает текущую статическую проверку, build и автоматически полученную из solution матрицу .NET тестов без игры. Финальный обязательный check — `Hatifect CI / CI Gate`; любой неуспешный или пропущенный prerequisite блокирует его. После публикации новой истории настрой branch protection на этот check и удали старые required checks, которых больше нет в workflow.

Игровые assemblies не скачиваются из личной установки в GitHub Actions. Проверка `--platform`, изоляция CA и runtime acceptance выполняются в среде с установленной игрой и прикладываются как отдельные результаты. Зелёный host-free CI сам по себе не доказывает работоспособность игрового адаптера.

Работай небольшими сквозными PR от `develop`: контракт, owning implementation, affected consumer и проверки входят вместе. До обновления task branch сохрани собственные изменения обычным commit. Worktree других задач не изменяй. Новая история этой базы независима от старой; старые ветки нельзя слепо вливать merge-коммитом. Незавершённые изменения переносятся осмысленным diff поверх новой базы с повторной проверкой.

## Среда Codex и агент Hatifect

Проектные правила поставляются вместе с исходниками. Корневой `AGENTS.md` задаёт роль Principal Software Architect / Framework & Platform Lead, порядок работы, общие границы и проверку результата. Локальные AGENTS в UI, Flowline, Integrations и tools уточняют владельцев поведения. При задаче из корня агент явно читает нужные локальные инструкции: Codex не добавляет все вложенные AGENTS в контекст автоматически.

`.codex/config.toml` ограничивает размер автоматически подгружаемых project instructions и число параллельных subagents. Модель, reasoning, sandbox, approval policy и MCP наследуются из личной/управляемой конфигурации. Основной агент ведёт задачу целиком; `.codex/agents/hatifect-explorer.toml` и `hatifect-reviewer.toml` задают ограниченные роли исследования и независимого review. Их не требуется запускать для каждой правки. Настройка `read-only` не отменяет фактическую policy текущей сессии; reviewer также обязан соблюдать запрет на исправления по инструкциям роли.

`.agents/skills/hatifect-development/SKILL.md` — собственный переносимый skill проекта. Его `routing.json` задаёт 12 маршрутов: architecture, csharp, ui, flowline, adapters, test-authoring, test-execution, review, build, ci, performance, codex. Маршрут рекомендует инструкции и специальные навыки по типу работы. Агент выбирает применимые части, а не загружает весь каталог. Например, изменение Flowline с новым UI experience затрагивает flowline + ui + csharp; новые тесты дополнительно требуют test-authoring. Проверка MSBuild log не нужна для исправной сборки только потому, что она указана в build-маршруте.

Три разных факта следует сообщать отдельно: skill установлен, обнаружен Codex, прочитан и применён в конкретной задаче. `hatifect-agent-check --host` проверяет второй факт; отчёт агента описывает третий. Без `--route` отсутствующий рекомендованный личный skill отображается как недоступный маршрут и не ломает переносимую базу. `--route <id>` требует доступности всех перечисленных в этом маршруте skills. Ошибки загрузки собственного skill или project config дают BLOCKED.

Для новой рабочей копии открой её как проект Codex и установи доверие этому точному Git-корню. Доверие родительскому каталогу может быть недостаточным. Затем запусти `./tools/hatifect-agent-check --host`. Команда запускает краткоживущий локальный app-server только для `initialize`, `config/read` и `skills/list`: не создаёт задачу и не вызывает модель. Codex может потребовать запись своей служебной SQLite-базы. В GitHub CI выполняется только переносимая часть без Codex, личных skills и credentials.

Новые project config/AGENTS/roles применяются при следующей загрузке контекста Codex; существующая задача не считается автоматически перенастроенной. При обновлении CLI сверяй схему с установленной версией. Проверено по официальным [project configuration](https://learn.chatgpt.com/docs/config-file/config-basic.md), [AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md.md), [subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents.md) и [app-server](https://learn.chatgpt.com/docs/app-server.md). В текущем CLI 0.150.0-alpha.13 строгий разбор личного `tools.view_image` расходится с текущей документацией; это не повод стирать настройки другого клиента. Обычную реальную загрузку проверяет host audit.

## Добавление будущей подсистемы

Сначала определи владельца состояния и публичный capability/application contract. Затем добавь проекты и test project в `Hatifect.slnx`, зафиксируй разрешённые зависимости в architecture checker и напиши короткий README с текущей готовностью и командой проверки. Добавляй локальный AGENTS только для новых специфичных правил. Если новая область требует отдельного выбора инструкций/skills, добавь маршрут в `routing.json`; переносимая проверка отклонит битые пути, дубликаты и превышение бюджета маршрута. Она проверяет файлы и размеры, но не доказывает качество текста или соблюдение инструкции агентом.

UI consumer читает контракт своего subsystem; изменения application state доходят через явно реализованный boundary. Git/CI обеспечивают согласованное изменение кода и обратную связь о несовместимости. Автоматическое редактирование кода соседней системы после каждого изменения не является механизмом runtime-синхронизации. Для Flowline текущий следующий шаг — snapshots, команды и revision-уведомления, описанные в ARCHITECTURE.

## Переход на имя Hatifect

Имена проектов, assemblies/namespaces, package IDs, SMAPI UniqueID, команды и harness environment переведены на Hatifect. Это согласованное переименование всей поставки: старый compiled consumer нужно пересобрать с новыми packages; старые и новые модули не устанавливаются одновременно. Публичные типы/методы семантической границы сохраняют форму, source baseline обновлён после смены namespace. Форматы, имена файлов и версии persistence Flowline сохраняются; доменная логика не меняется от переименования.

Имя автора `ihatectf`, фактический GitHub URL `ihatectf/HATIFECT` и обнаружение уже установленного личного SDK в `.dotnet/hatifect-x64-8` описывают существующие внешние объекты. Они не являются именем продукта. Бинарные signatures `HATFLOWC`, `HATFLOWD`, `HATFLOWP` остаются частью формата сохранений: их смена без отдельной миграции сделала бы существующие checkpoint/provider images нечитаемыми. Перенос/переименование GitHub и замена shared `develop` — отдельный шаг миграции подготовленной базы. Новые переменные конфигурации — `HATIFECT_*`; локальный файл создаётся из `tools/hatifect.env.example` как `tools/hatifect.env` и не коммитится.

## Runtime harness

Runtime transport и JSON schemas сохранены для дальнейшей acceptance-проверки. Каталог сценариев — `tools/live-harness/scenarios.json`; результаты имеют статусы PASS/FAIL/BLOCKED/NOT_APPLICABLE. Сценарии `flow.route.basic` и `flow.save.isolation` проверяют диагностический fake-provider маршрут и lifecycle Flowline. UI aggregate `all` проверяет текущие семантические поверхности и совместимый CA Overlay; отрицательные конфигурации CA запускаются отдельно.

Для runtime сначала явно настрой `tools/hatifect.env` по примеру и изолированную `.smapi-test` среду. `./tools/hatifect-runtime-executor serve` запускается пользователем, когда нужен harness. Используй `./tools/hatifect-smoke <scenario>` и `./tools/hatifect-ui-test <scenario>` только при runtime-задаче. Они могут запускать игру в изолированной среде. Не подменяй отсутствующий runtime-отчёт историческим результатом другой сборки.
