# Установка первой альфы

Эта инструкция относится к локальному кандидату
`57c9ef43883d84a916e50ce38e729168f397b893`. Он собран и проверен в изолированном каталоге, но
«Приёмка первой альфы» ещё не имеет статуса DONE: физический ввод остаётся обязательной проверкой.

## Requirements

- Stardew Valley `1.6.15`.
- SMAPI `4.5.2` или новее в совместимой линии API.
- Одиночное сохранение.
- Chests Anywhere `1.30.1` или новее необязателен. Если он установлен, модуль overlay подключает
  интеграцию; основной Flowline от него не зависит.

Архив `Hatifect-clean-baseline.zip` содержит один корень `Hatifect` с тремя модулями:

| Каталог | Версия | Назначение | Required dependency |
| --- | --- | --- | --- |
| `Hatifect/Hatifect UI` | `1.0.0-alpha.47` | UI framework и Stardew host | — |
| `Hatifect/Hatifect Flow` | `3.0.0-rc.89` | Flowline domain, persistence, UI consumer и game adapter | Hatifect UI `>=1.0.0-alpha.47` |
| `Hatifect/Integrations/Chests Anywhere/Hatifect Chests Anywhere Overlay` | `1.0.0-alpha.1` | Необязательный CA overlay | Hatifect UI `>=1.0.0-alpha.47`; optional Chests Anywhere `>=1.30.1` |

## Installation

1. Закройте Stardew Valley и SMAPI.
2. Проверьте SHA-256 архива. Для этого кандидата ожидается
   `a4d1e01b56d2fcce97cf2b219fa52b5e6859dd4dc79991d9d2f542279fd639a4`.
3. Распакуйте каталог `Hatifect` целиком в `Stardew Valley/Mods`. Итоговый путь должен выглядеть
   как `Mods/Hatifect/Hatifect UI/manifest.json`, а не `Mods/Hatifect-clean-baseline/Hatifect/...`.
4. Не смешивайте файлы этого кандидата с прежней версией. Заменяйте каталог `Mods/Hatifect`
   целиком.
5. Запустите игру через SMAPI и откройте одиночное сохранение. В консоли не должно быть ошибок
   загрузки `Hatifect.UI`, `Hatifect.Flow` или `Hatifect.ChestsAnywhereOverlay`.

## First route

Поставьте два обычных player chest или BigChest и положите поддерживаемую стопку в первый.
Закройте другие меню, наведите курсор на первый сундук и нажмите **K**. На контроллере используется
сочетание левой и правой верхних кнопок; привязку можно изменить через `OpenNetwork` в конфигурации
Flowline.

Создайте для первого сундука станцию с уникальным именем из 1–32 букв, цифр, `_` или `-`.
Закройте окно, наведите курсор на второй сундук, снова откройте Flowline и создайте вторую станцию.
Выберите источник и назначение, задайте capacity `1–999` и travel time `1–36000` тиков, затем
создайте направленную связь. Выберите актуальный груз в источнике и отправьте весь стек либо
количество от 1 до размера стека. После отправки закройте меню, чтобы simulation ticks продолжили
доставку, затем снова откройте Flowline и проверьте Result и сундук назначения.

Подробное поведение ошибок, recovery, повторного выбора изменившегося стека и сохранения описано в
[FLOWLINE_PLAYER_GUIDE.md](FLOWLINE_PLAYER_GUIDE.md).

## Alpha limits

- Только single-player; multiplayer не входит в этот сценарий.
- Поддерживаются обычные player chests и BigChest. Холодильники, подарочные, общие и особые
  сундуки не поддерживаются.
- Поддерживаются сериализуемые обычные объекты и исходные стеки до 999.
- На одно сохранение: 32 станции, 128 связей за lifetime сети, 256 retained cargo records и
  16 попыток доставки на груз. Удаление связи или завершение груза не возвращает history budget.
- Удаление станции не предоставляется. Переименование сохраняет identity; rebind не переносит
  предметы и ограничен незавершённым грузом.
- Неизвестный результат физической операции не повторяется автоматически. Используйте только
  доступные в UI recovery actions; при отсутствии однозначной квитанции восстанавливайте полный
  игровой backup.
- Проверенная runtime matrix не заменяет ещё не выполненный physical-input acceptance. Controller
  profile и normalized controller actions прошли, но до physical PASS нельзя считать реальные
  K, pointer, Tab/Search/Backspace/Enter и controller events подтверждёнными на этом архиве.
