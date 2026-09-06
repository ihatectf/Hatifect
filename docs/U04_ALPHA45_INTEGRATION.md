# U04: общая интеграция alpha.45

Статус — **IN_PROGRESS**. GQ принимает только зафиксированный checkpoint `f4b7386436166fae7f66f5a45447a0335a441e0e`: foundation `b89c792` и Stardew capture `29373a2` с их документацией. Основа интеграции — опубликованная alpha.44 `bea00ad`, включая explicit Terminal generations, native fixtures и общую background policy `fe51271`. Все 17 package/version/consumer authorities переводятся с alpha.44 на alpha.45 одним владельцем — GQ.

## Контракт и область

Immutable `UiEnvironment` принадлежит Semantics и не удерживает игровые объекты. Planning принимает logical viewport, scale, input, locale, theme, accessibility preferences и происхождение значений; Runtime Invocation предоставляет именованный environment-вызов с сохранением legacy profile API. Compiler и planner требуют покрытия всех обязательных capabilities. Ранее допускавшиеся explicit presentations с частичным покрытием теперь отклоняются вместо потери семантики; это намеренное изменение, требующее проверки существующих consumers.

Stardew adapter захватывает игровые значения и переиспользует неизменный снимок. Custom locale identity и английский пустой suffix обработаны отдельным исправлением владельца. Подключение capture к native hosts/Terminal, согласованное принятие environment и scene, active reload и live consumer locale относятся к следующему UI checkpoint alpha.46. Текущая интеграция не объявляет эти механизмы реализованными. Flow Core/Persistence и frozen surface API v1 не меняются.

## Источники и проверка владельца

GQ ранее прочитал все 11 source/test files и связанные contracts, затем повторно сверил каждый immutable Git blob с `artifacts/alpha45-integration-preflight/owner-final-audit.json`. Открытых замечаний в этом ограниченном source review нет. Owner C1490 .NET +365 Python, G1763 +365, scoped capture50 и retained P90 проверены по фактическим TRX, package/cache/deployment hashes. Это результаты owner alpha.43 producer, а не общей alpha.45. Подробные исторические проверки сохранены в [ROADMAP-STATUS.md](ROADMAP-STATUS.md#u04-a-environment-planning-and-invocation-foundation).

Исходники объединились без конфликтов. Единственный append conflict в журнале roadmap разрешён сохранением полного alpha.44 evidence и обоих U04 разделов. Background helper, запуск с явными flags и временная граница перед Popen сохраняются; версия UI.Stardew меняется без удаления linked helper. Незакоммиченная работа соседних задач не импортируется.

## Общая приёмка

Собственные проверки alpha.45 ещё не выполнены. Следующие обязательные действия: C и G на объединённом source, изолированный P с точными producer/consumer DLL, scoped Planning PERF с обычным GC и свежая проверка совместимости через canonical UI/CA aggregate, Flow и isolation. Отдельный native PERF и фактический просмотр EN/RU×scale кадров относятся к новой поставке. Эти сценарии проверяют совместимость интеграции; native environment matrix остаётся acceptance последующего host wiring.

Ожидаемый состав после объединения: Planning118, Runtime449 и Stardew50; результаты владельца не подменяют фактически выполненные root tests. Артефакты интеграции сохраняются под `artifacts/alpha45-integration-preflight/` с отдельной source identity и версиями. DONE и публикация появятся после завершённой проверки. Полные U04/F12, typed consumer, reload/save-switch и физический Backspace остаются незавершёнными.
