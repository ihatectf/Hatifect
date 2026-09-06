# Flowline

[Полная roadmap](../docs/ROADMAP.md) продолжает десять инкрементов работами F11–F20 и связывает их с модернизацией UI. [Статус Flow foundation](../docs/ROADMAP-STATUS.md) отделяет выполненные проверки от оставшейся acceptance.

Flowline — транспортная подсистема Hatifect. `Hatifect.Flow.Core` содержит модель и выполнение; `Hatifect.Flow.Persistence` сохраняет состояние и проверяет идентичность сейва. Игровой `Hatifect.Flow` host владеет lifecycle SMAPI. Домен не зависит от UI и конкретных сторонних модов.

Эта база сохраняет десять инкрементов: очереди и операции, deterministic fake-provider execution, pause/recovery, persistence envelopes и изоляцию сейвов. Поверх них реализованы application snapshots/commands/revisions, редактор сети и отправлений, адаптер обычных сундуков и игровая сессия одиночного сейва.

## Первый игровой маршрут

Нужны Hatifect Flow и Hatifect UI 1.0.0-alpha.31 или новее. В одиночном загруженном сейве доступны команды консоли SMAPI:

1. Наведи курсор на обычный сундук игрока: `hatifect_flow station source`. Так же зарегистрируй второй сундук как `destination`. Имена: до 32 букв, цифр, `_` или `-`.
2. `hatifect_flow link source destination` создаёт направленную связь с capacity 999 и временем 180 игровых update ticks. Необязательные два последних аргумента меняют capacity (1..999) и ticks (1..36000).
3. `hatifect_flow send source destination 1` отправляет всю стопку из первого слота. Если capacity занята, посылка остаётся готовой к отправлению: повтори `hatifect_flow reserve <parcel-id>` после освобождения маршрута.
4. `hatifect_flow list` показывает станции и ID отправлений. Открой игровое меню и выполни `hatifect_flow show` для последней посылки либо `hatifect_flow show <parcel-id>`. Поверхность показывает груз, маршрут, состояние и доступные действия; layout/rendering принадлежат Hatifect UI. Поддержаны RU/EN. При открытом меню перевозка учитывает обычную игровую паузу.
5. `hatifect_flow cancel|reserve|retry|reconcile|return <parcel-id>` вызывает те же типизированные команды. При полном или отсутствующем сундуке назначения груз остаётся у перевозчика; освободив слот, выполни `retry`.

## Управление сетью и отправлениями

Укажи на сундук и выполни `hatifect_flow target`, затем открой игровое меню и выполни `hatifect_flow network`. UI запоминает конкретный физический сундук: замена после выбора отклоняет команду. Можно создать станцию, переименовать выбранный источник или явно перепривязать его к выбранному сундуку; выбрать два конца связи, задать ёмкость/время, просмотреть путь и удалить связь. Прямые команды: `rename <имя> <новое-имя>` и `rebind <имя>` под курсором. Перепривязка сохраняет GUID и журнал; источник с ожидающим извлечения грузом сначала требует отмены таких отправлений.

Раздел груза показывает поддерживаемые стопки выбранного источника. Можно отправить весь стек или ввести количество от 1 до размера выбранного стека. Команда проверяет session/revision и SHA-256 полного сериализованного предмета в слоте. Если игрок изменил стек или metadata после открытия UI, обнови список и выбери груз снова. До извлечения количество источника не меняется; при частичной отправке остаток получает отдельное владение и может стать следующим отправлением. История содержит поиск по предмету, станциям и ID, фильтры всех/активных/проблемных отправлений и страницы по 12 строк; выбранная посылка сохраняется по ID при возвращении на страницу.

После отказа доставки можно явно вернуть груз в источник. Это возврат находящегося у перевозчика стека через журнал custody, без построения нового обратного маршрута. Он выполняется на следующем активном тике, требует свободного слота источника и расходует общий лимит попыток доставки/возврата. При отказе можно повторить возврат; после успешного возврата исходная посылка завершена. В пути, после успешной доставки и при неизвестном результате передачи такой возврат запрещён.

## Поддерживаемая граница

Первый адаптер работает с точным игровым типом Chest (обычным/большим player chest) и целой стопкой обычного `StardewValley.Object` до 999 единиц. Холодильники, Junimo/global inventory, машины, вложенные heldObject и сторонние подклассы исключены. XML сериализатор игры сохраняет качество, flavor/preserve-поля и modData; transient поля вне формата игрового сейва не входят в гарантию. Размер описания одного груза ограничен 65 536 символами ещё во время сериализации. В получателе нужен свободный слот: частичного слияния стопок нет.

Привязка станции включает GUID в modData сундука, location и tile. Перемещённый/заменённый сундук не получает груз автоматически; вторую станцию на занятой координате зарегистрировать нельзя. Лимиты одного сейва: 32 станции, 128 связей за всю историю, 256 сохранённых грузов/отправлений, 16 попыток доставки/возврата, 64 операции за update. История пока не очищается. Multiplayer, сторонние inventory adapters, деление одной заявки на несколько посылок и различимое поведение service policies не входят в этот этап.

## Сохранение и ошибки

`FlowGameSession` пишет aggregate версии 1 через SMAPI `WriteSaveData` в ключ `flowline-v1` на `Saving`; он содержит Core checkpoint, station bindings и item payloads. Эти данные сериализуются вместе с физическими инвентарями в одном игровом сейве. На время сохранения команды и update остановлены. Выход без сохранения откатывает мир и перевозки вместе. Старые fake-provider envelope/formats не менялись и не подключаются к реальным сундукам.

Восстановление journals не выполняет физические transfer и не создаёт исторические предметы, которые уже забрал игрок. Единственный источник материализации — груз в custody активной посылки. Исключение с неоднозначным результатом inventory callback останавливает сессию и сохраняет `RequiresRecovery`; после reload автоматический retry/reconcile остаётся запрещён. Раздел диагностики и команда `hatifect_flow recovery` показывают неоднозначные операции. `recover <parcel-id>` и действие UI завершают только передачу с сохранённой квитанцией Applied/Rejected, не вызывая physical Apply. Missing не доказывает отсутствие предмета: при такой ошибке блокировка сохраняется и нужен разбор либо восстановление целого игрового сейва из исправной резервной копии. Простое очищение сундука не разрешает повторное создание груза. Произвольные сторонние callbacks, перемещающие предметы в другие инвентари или повреждающие NetList во время записи, не входят в поддерживаемую границу.

## Проверка

`./tools/hatifect-test flow` проверяет Core/Persistence/application/semantic behavior без игры. `./tools/hatifect-test flow --platform` дополнительно запускает тесты на настоящих игровых Item/Chest: сериализация, доставка, отказ/retry, save/reload, откат целого сейва, старые UI-команды и блокировка неоднозначной записи. Они работают без Game1/content и не заменяют настоящий цикл SMAPI Saving/Saved или визуальную проверку.

Полный граф: `./tools/hatifect-check --platform`. Существующие runtime `flow.route.basic` и `flow.save.isolation` проверяют только fake-provider/lifecycle harness. `./tools/hatifect-smoke flow.chest.roundtrip` проверяет production-сессию, настоящие сундуки, сохранение в пути, две пары реальных SMAPI Saving/Saved, две повторные загрузки и отсутствие дубликата. Provisioner создаёт отдельное canonical имя `HatifectHarness<UUID N>_4242424242` и удаляет только копию этого сценария/запуска. Старые fake copies остаются неизменными; golden и реальные сейвы не редактируются.

`./tools/hatifect-smoke flow.chest.crash-after-save` добавляет отдельную границу: реальный Saved с целым и частичным грузом в пути → SIGKILL принадлежащей executor группе процессов → новый SMAPI с той же копией → доставка и повторное сохранение без дублей. Перед остановкой executor сверяет request, PID, owner сейва, hash сохранённых байтов и трёх Flow DLL; после неё требует фактический raw exit `-9`. Любой отчёт об ошибке первого процесса запрещает продолжение. Один запрос ограничен двумя фиксированными запусками и общим timeout; диагностика сохраняет marker, termination и оба process journal. Сценарий проверяет восстановление после завершённого сохранения; сбой внутри записи сейва и остальные границы матрицы требуют отдельных проверок.

`./tools/hatifect-smoke flow.chest.crash-after-delivery` использует тот же протокол после второй пары Saving/Saved: обе посылки уже Delivered, предметы находятся в сундуке назначения. Новый процесс загружает это состояние, проверяет точное количество и свойства предметов в течение 120 update ticks и отклоняет повторную доставку. Executor принимает только фазу `saved-delivered` с двумя подтверждёнными сохранениями; маркер первой границы для этого сценария недействителен.

Дальнейшая реализация следует утверждённой [общей roadmap](../docs/ROADMAP.md), включая полную failure matrix, обычный игровой вход и acceptance UI. Multiplayer остаётся отключённым. Автоматическое восстановление без доказанного результата физической операции не поддерживается. Наличие runtime-сценария само по себе не является PASS: фактический результат хранится в artifacts/runtime/<run-id>/result.json.

## Application availability (F11)

`FlowSnapshot` identifies `DiagnosticFake` versus `GameInventory`, lists supported operations, and exposes the session `Code`/`ReasonKey`. Every parcel carries a fixed immutable `FlowActionAvailabilitySet`; each action has `Available`, a stable `FlowRejectionCode`, and a localization key. The existing `Actions` bitmask remains a compatibility projection of that set. Consumers use owner availability and localize reasons; they do not infer allowed effects from `ParcelState`. Diagnostic experiences identify their source in the title.

Reservation availability shares the owning admission check with `TryReserve`: state, queue room, route/search limit and remaining link capacity. The projection is built only when the owner refreshes it; ordinary `ReadSnapshot` returns the same immutable object and performs no route search or provider I/O. Availability describes the published state; a command still validates current authority, session and revision before any effect. A stale session and a stale revision have distinct results.

`FlowCommandResult` preserves status/revision and adds `Code` plus `ReasonKey` for refusals. Successful results have `None` and an empty key. Domain reasons use `flow.reason.<Code>`; the semantic consumer falls back to the code when a translation key is unknown. Extended network/recovery commands retain the same rejection envelope. Persistence envelopes, cargo custody and game-save format do not change for these UI fields. The internal disabled peer projection carries the new values and rejects contradictory action masks/reasons on decode.

The five public action entries remain Reserve, Cancel, RetryDelivery, ReconcileTransfer and ReturnToSource. Provider capabilities can explicitly disable an operation. Pause/recovery blocks ordinary commands; known-outcome recovery remains a separate typed host command.

## Partial-stack custody (F16)

`FlowSendCommand.Quantity` is optional; omitted means the original whole-stack operation, preserving its six-argument constructor and deconstruction. The game adapter validates quantity and the complete source fingerprint under the existing lease. It builds selected cargo and the future remainder on detached XML objects; only one indexed assignment changes physical quantity during extraction. Delivery/return use the selected cargo quantity, and retained receipts suppress physical replay. A changed remainder after an inventory callback retains an unknown outcome and blocks automatic recovery.

The game-save aggregate writes version 2 with the full source quantity alongside selected cargo XML. Version 1 whole-stack saves migrate explicitly; malformed version 2 payloads are rejected before any physical effect. The Core checkpoint/receipt format remains unchanged. A failure while assigning an admitted source tag has no settled transfer outcome; the UI explains the complete-save recovery fallback. Full provider boundaries and the remaining process-crash matrix are in [FLOW_PROVIDER_CONSISTENCY.md](../docs/FLOW_PROVIDER_CONSISTENCY.md).
