# Общая интеграция alpha.47: текст, Parcel и смена сохранений

Статус — **IN_PROGRESS**. GQ объединяет готовые срезы U04-c, F12-a–d и U03 actual save-switch поверх опубликованной alpha.46 `eb193b7` с успешным [CI34062754345](https://github.com/ihatectf/Hatifect/actions/runs/34062754345). Публикация `fad455e` добавляет только запись этого результата. Все 17 version authorities переведены GQ на `1.0.0-alpha.47`; общая приёмка новой версии ещё не выполнена.

## Состав и владение

- Flow frozen handoff `485c815a97af2d00321faf46cef1c745b80a6923`, final source `3810b1014eb1cc7d8a2d26fc21b43441fbb85cef`: captured locale/publication text, retained Parcel facts/results, видимые empty/faulted states, Faulted → Closed при Dispose, opening без выбранного отправления и exact EN/RU native item names. Интеграция исходников — `e999f44`.
- UI save-switch source `889f9b1bc21fb1b0ec4f3e082e20bddfb072ebf9`, frozen documentation handoff `d044f22b7b58b698406660a5c783016edbbae353`: четыре host, реальные title/load transitions, retirement старых root/portal actions и доставка новых. Общий companion-save helper сохраняет прежний Flow protocol. Интеграция исходников — `c269a50`.
- UI Runtime владеет композициями и lifecycle; Flow задаёт доменные факты, тексты и команды. Новое authoring API добавляет immutable localized labels и typed read-only formatters; opaque surface API v1 и persistence formats не меняются. Test-only Flow → UI.Runtime.Tests reference не добавляет production UI → Flow dependency.

Новый observation/UI lifecycle WIP владельцев не импортирован. Уже принятый host accepted-scene guard и его четыре regression cases сохранены без дублирования; `flow.ui.names` report routing зарегистрирован один раз. Documentation append conflicts разрешены сохранением обеих историй. При принятии save-switch сохранены automatic environment diagnostics, а Flow cleanup test наблюдает общий `prepare_secondary` helper. Старые scenario objects и исходные U03/U04/F12 acceptance criteria сохранены.

## Проверенные входящие evidence

GQ прочитал все 42 изменённых файла Flow handoff и actual C/G TRX: 1550/1842 .NET +367 Python, без пропущенных тестов. Bounded native `flow.ui.names` — PASS7 на `8915aae8-f838-4f6c-8d59-91a07d25834c`: проверены 15 artifact hashes, 12 producer/deployed DLL, fingerprint, исходное отсутствие/restoration options и unchanged golden inventory. Retained P90/44/8/3/2 относится к отдельному `6e75c5c` с alpha.43 package identities и не сертифицирует новую общую поставку. Детали: `artifacts/alpha47-text-preflight/owner-handoff-review.json`, `owner-final-static-audit.json`, `owner-item-native-audit.json`, `owner-parcel-p-audit.json`.

UI handoff независимо проверен GQ: C/G1528/1801 +372 Python, P90/44/8/3/2 и actual immutable native `cb89e4a8-eb45-4c47-a718-91beb6441f7a` — PASS18. Четыре title transitions, пять загрузок двух owned worlds, восемь старых cancelled/read-once операций без callbacks и восемь новых owner-thread callbacks подтверждены actual reports. Восемь package/producer/game DLL совпадают; options восстановлены, обе копии удалены, golden неизменён. Evidence: `artifacts/alpha47-save-switch-preflight/` и [отчёт владельца](U03_SAVE_SWITCH_ACCEPTANCE.md).

## Общая проверка и следующий шаг

На общем source обязательны C, G, P и isolated prepare, затем native save-switch, reload/environment, item-name capture, UI/CA, Flow route/isolation и PERF; rendered matrix требует просмотра всех восьми EN/RU × scale PNG. Для shared companion-save изменения также нужна существующая production chest isolation. Общие gates новой alpha.47 пока **NOT_RUN**; первым запускается C. Runtime fingerprints, request IDs, реальные результаты, ограничения и публикация будут добавлены по факту выполнения.

Полные U03/U04/F12 остаются **IN_PROGRESS**. Actual Flow screen open/update/empty/unavailable/faulted/close/A → B → A, concrete typed actions/messages, оставшиеся environment/planner/representative performance и физический Backspace ещё требуют своей проверки. Native custom-language start не выполнен ни в принятой alpha.46 environment fixture, ни в Flow names-only scenario; сохранённая custom-locale ветка не получает runtime PASS по запуску с EN. Следующий зависимый consumer шаг готовит FLOWLINE вместе с UI-owned bounded accepted-surface observation.
