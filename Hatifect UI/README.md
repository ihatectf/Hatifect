# Hatifect UI

Семантический UI framework: Language → Semantics → Experience, затем Planning и Runtime; Stardew host связывает framework с игрой. Tooling, Tooling.Server и DevTools предоставляют диагностику и инспекцию.

## Идентичность Hatifect UI

Project, assembly, namespace и package IDs используют `Hatifect.UI.*`; SMAPI module имеет `UniqueID` `Hatifect.UI`, а tooling и acceptance identifiers используют префикс `hatifect`. Flowline semantic IDs принадлежат Flowline и используют ту же идентичность Hatifect.

Consumer описывает смысл интерфейса и действия. Framework владеет layout, visual policy, вводом и rendering. Доступны standalone Window/Modal/Fullscreen/HUD, Terminal и active-menu overlay.

Публичная игровая граница — `IUiSemanticSurfaceApi` v1 из `Hatifect.UI.Experience`. `PUBLIC_API_BASELINE.json` фиксирует её исходный контракт. CA Overlay использует точно версионированные NuGet packages; корневой `tools/hatifect-pack-ui` собирает feed из текущих исходников. Runtime-модуль поставляет восемь UI DLL; Tooling.Server используется как инструмент разработки.

Аддитивный SDK: [hosts, typed forms, resources/themes, adaptive sources и live reload](../docs/UI_SEMANTIC_SDK.md). Новые возможности доступны через отдельные optional interfaces; исходный контракт v1 сохранён.

[Редактор и language server](../docs/UI_AUTHORING.md): incremental sync, completion, hover, outline/folding, semantic tokens, references, definition, проверяемые quick fixes и workspace diagnostics. `UiBindingContextJson.Export` передаёт реальные bindings из Experience, а `tools/hatifect-ui-language-server` запускает stdio-сервер.

`UiSemanticFormField` также принимает внешний `ValidationMessage`: непустое сообщение блокирует `UiFormState.Apply`, заменяет обычную help поля на его focusable controls и остаётся inline accessibility `Alert`. Ошибка использует theme `Text.Danger`; после очистки возвращается локализованная static help. Типизированные Text/Number/Toggle/Choice и `Apply/Reset` сохраняются. Владелец формы вызывает `Dispose`; при ошибке снятия подписки повторный `Dispose` завершает очистку, а закрытая форма уже не уведомляет observers. Источники остаются собственностью consumer.

Overloads `UiExperienceBuilder` с явным `UiSymbolId` сохраняют ID элемента независимо от локализованного имени, включая пробелы. `UiSemanticGraph` переносит отдельные IDs, aliases и fallback labels через binding и metadata; Runtime разрешает visual role по alias.

[Локализованный текст на сохранённом Experience](../docs/UI_TEXT_PROJECTION.md): `UiLocalizedText`, `LocalizeDisplayName/Element/Action` и typed `FormatText<T>` позволяют повторно составить подписи и read-only значения из одного captured locale/publication snapshot. Исходные labels остаются authoring fallback, action/source identities сохраняются. Игровое переключение языка и Flow consumer integration имеют отдельную приёмку «Адаптация интерфейса к окружению»/«Просмотр состояния Flowline».

Из корня репозитория: `./tools/hatifect-test ui`, затем `./tools/hatifect-check --platform` для изменений Stardew host. Бюджеты реальных кадров находятся в `PERFORMANCE_BUDGETS.json`, требования к runtime-отчёту — в `HOST_ACCEPTANCE_REQUIREMENTS.json`. Статические тесты не заменяют измерения в игре.

Общие границы: [архитектура](../ARCHITECTURE.md). Команды: [разработка](../docs/DEVELOPMENT.md).

[План разработки интерфейса и Flowline](../docs/ROADMAP.md) отделяет готовые возможности от оставшейся работы: простое создание экранов, точная геометрия, общие компоненты, безопасное обновление открытых окон, редактор и предпросмотр. Наличие задачи в плане не означает, что соответствующий API уже опубликован.
