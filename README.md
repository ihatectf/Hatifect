# Hatifect

Общая база для семантического Hatifect UI, Flowline и интеграции Chests Anywhere Overlay. Это новая исходная точка разработки с отдельной историей Git.

В репозитории сохранены текущий семантический framework, ядро и persistence Flowline, игровой host и адаптер CA Overlay. Старые продуктовые Terminal/Storage/Bootstrap, pipe runtime, UiNode и CSS cascade удалены.

## Начало работы

Нужны Python 3.11+, .NET SDK 8 из `global.json` и runtime .NET 6 для тестов. Проверки без игровых зависимостей:

```sh
./tools/hatifect-check
./tools/hatifect-test flow
./tools/hatifect-test ui
```

При установленной Stardew Valley/SMAPI:

```sh
./tools/hatifect-check --platform
./tools/hatifect-isolated-ui-ca
```

Обычная сборка и тесты не запускают игру и не устанавливают моды. Сборка проверенного локального архива: `./release.sh`.

## Карта проекта

| Каталог | Ответственность |
|---|---|
| `Hatifect UI/` | Семантический язык, planning, runtime, инструменты и Stardew host |
| `Hatifect Flow/` | Транспортная модель Flowline, persistence, игровая сессия и live semantic experience |
| `Integrations/Chests Anywhere/` | CA Overlay, изоляция стороннего API и семантическое представление |
| `tools/` | Общая логика локальных проверок, CI, пакетов и изолированного runtime harness |
| `dev/Hatifect.TestHarness/schemas/` | Форматы запросов, сценариев и результатов runtime harness |

Flowline поддерживает первый маршрут между обычными сундуками одиночного игрока: целые стопки, сохранение перевозок вместе с миром и управление посылкой через Hatifect UI. Редактор сети, отправка целых стеков, возврат и поиск истории доступны через Hatifect UI; консоль SMAPI открывает поверхность и выбирает физический сундук. Поддерживаемые типы, лимиты и границы проверок описаны в [Flowline](Hatifect%20Flow/README.md), устройство системы — в [архитектуре](ARCHITECTURE.md).

[Полная roadmap](docs/ROADMAP.md) связывает модернизацию UI, развитие Flowline и CA: вехи, зависимости, владельцы и критерии готовности. Проверенная основа Flow не закрывает semantic-v2, полный authoring/tooling и итоговую runtime-приёмку; фактический статус и evidence обновляются после каждого среза.
