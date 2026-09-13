# Hatifect Chests Anywhere Overlay

Адаптер связывает Chests Anywhere с семантическим Hatifect UI. Интеграционное поведение, проверка возможностей стороннего API и восстановление меню принадлежат адаптеру; представление задаёт отдельный semantic experience.

UI подключается через exact-version packages и `IUiSemanticSurfaceApi` v1. Адаптер не ссылается на исходные UI-проекты и не поставляет копии UI runtime DLL. При отсутствующем или несовместимом CA функциональность должна корректно оставаться недоступной; ошибка захвата не должна терять lifecycle исходного меню.

Из корня репозитория: `./tools/hatifect-test ca --platform` и `./tools/hatifect-isolated-ui-ca`. Для этих проверок нужны игровые references. Отдельные runtime-сценарии совместимого, отсутствующего, несовместимого CA, capture exception и return-to-title перечислены в `tools/live-harness/scenarios.json`.

Обычная сборка не устанавливает моды. Состав поставки и версии определяются корневым `Hatifect.Release.json`.
