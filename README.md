# Hatifect

Общая база для семантического Hatifect UI, Flowline и интеграции Chests Anywhere Overlay.

Исходный код доступен по source-available лицензии для личного некоммерческого
использования. Hatifect не является open-source проектом: приватные изменения
разрешены, но самостоятельная повторная публикация, распространение, включение
в другие проекты и коммерческое использование запрещены. Изменения можно
предлагать через GitHub fork, feature branch и pull request; решение о принятии
остаётся за `ihatectf`. Полные условия находятся в [LICENSE](LICENSE), правила
вкладов — в [CONTRIBUTING.md](CONTRIBUTING.md), сторонние лицензии — в
[THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES).

Репозиторий содержит семантический framework, ядро и persistence Flowline, игровой host и адаптер CA Overlay. Hatifect — первая и единственная идентичность всей системы.

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

[План разработки](docs/ROADMAP.md) показывает, что уже принято и что осталось: сначала завершаем рефакторинг читаемости, затем общие возможности UI, пользовательские сценарии Flowline и Chests Anywhere и приёмку альфы. У каждой открытой задачи есть понятное название, зависимости, ответственный слой и условия завершения. Готовая основа перевозок не означает, что весь интерфейс и выпуск уже приняты.

## Безопасность и обратная связь

Уязвимости следует отправлять через private vulnerability reporting во вкладке
**Security**, а не через публичный issue. Политика и 90-дневный срок
координированного раскрытия описаны в [SECURITY.md](SECURITY.md). Обычные issues
и внешние pull requests разрешены по правилам
[CONTRIBUTING.md](CONTRIBUTING.md); только `ihatectf` решает, принимать ли
предложенное изменение.
