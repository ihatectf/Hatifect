# Совместная проверка действий и окружения: alpha.46

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Локальная общая приёмка исправленного source [`61e8a94`](https://github.com/ihatectf/Hatifect/commit/61e8a941bbb2936e2b41f029e931b2ee8d30fe8e) — **PASS**. Ограниченный интеграционный срез — **DONE**: опубликован в `develop` как [eb193b7](https://github.com/ihatectf/Hatifect/commit/eb193b7b98b35e8a446d1fef683c032588ece30a), exact [CI34062754345](https://github.com/ihatectf/Hatifect/actions/runs/34062754345) — **SUCCESS**, все 10 jobs. Frozen handoff UI — [`b6f60b4`](https://github.com/ihatectf/Hatifect/commit/b6f60b49af5c7f94469bab4021ab12f2b61acd88), immutable source [`0cd356f`](https://github.com/ihatectf/Hatifect/commit/0cd356f0fd252a0744fe6a871e46ba631193f876), implementation `902d834`. Основа GQ — опубликованная alpha.45 `12e7aab` с успешным exact CI34058056333 во всех 10 jobs. Все 17 version authorities уже переведены на alpha.46 владельцем UI; повторного изменения версий при интеграции нет.

## Область и контракт

Успешный active reload принимает assets, invocation, environment и scene до cancellation callbacks старого поколения. Rejected preparation сохраняет принятую scene, assets и pending root/portal work. Неизменённые документы не создают новое поколение. Window, Terminal, HUD и active-menu hosts передают один immutable environment через Invocation, Planning и composition; environment-only recomposition сохраняет модель и pending actions.

Кандидат theme/composer остаётся закрытым до принятия. Native owner, configuration и accepted scene/theme versions проверяются после callback-bearing preparation; устаревший внешний кандидат не перезаписывает принятую вложенную операцию. Retirement фиксируется перед fallible cleanup, поэтому оставшиеся event/watch delegates не доставляют уведомления закрытому владельцу; cleanup можно повторить. Изменены внутренние Runtime/Stardew seams. Opaque surface API v1 и Flow Core/Persistence не меняются.

## Проверка входящего checkpoint

GQ прочитал delta всех 20 изменённых source/test files, включая четыре Runtime test files, native fixtures и lifecycle checks. Все 48 source/version postimages сверены с immutable Git и frozen owner worktree; отдельные 17 non-Markdown version changes содержат только alpha.45→alpha.46. Version commit также добавляет один документационный абзац; финальный handoff меняет только три Markdown-файла. Все прежние 32 scenario objects сохранены, добавлены ровно `semantic.actions.reload` и `semantic.environment`. Исходные «Действия и их завершение»/«Адаптация интерфейса к окружению» acceptance paragraphs остались без изменений.

Повторно вычисленные GQ owner audits совпали с retained results: C1528 .NET +366 Python, G1801 +366, все 17 actual TRX; P90, 44 projected files, 8 exact package DLL, 3 isolated assets/cache entries и 2 deployed CA DLL. Native reload `57ee8df6-0147-4417-9a1d-e5bd8376612a` — PASS18, environment `26d7b6c8-a88c-4df9-a60e-b2e9028e9b26` — PASS25. Fingerprint `a5eed91fe33c86c54c7cfda90d796f4aa40934c6da5edd1760ef606fae06bc06` пересчитан по игровой поставке; все 8 package/producer/game DLL совпадают. Options original bytes восстановлены, оба процесса завершились с exit0, обе рабочие копии удалены. Открытых замечаний в этом ограниченном review нет.

Артефакты GQ: `artifacts/alpha46-integration-preflight/owner-final-audit.json` и заново вычисленные audits в `owner/`. [Отчёт UI](ui-actions-environment-host-acceptance.md) сохраняет owner commands, RED→GREEN и отдельное заключение reviewer Kepler. Owner results не подменяют собственные проверки объединённого GQ worktree.

## Общие проверки и ограничения

Merge frozen handoff поверх alpha.45 прошёл без конфликтов, все 48 исходных postimages сохранены. На общем source `fc83019f2d80bfce999d911ff591733b9bf09ec9` выполнены собственные проверки:

| Проверка | Фактический результат |
| --- | --- |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | `run-r10dqoku`: PASS1528 .NET +366 Python, 7 actual TRX |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | `run-hz0nj6om`: PASS1801 .NET +366 Python, 10 actual TRX |
| `./tools/hatifect-isolated-ui-ca --keep` | `hatifect-ui-ca-isolated.op7b5g5z`: PASS90, 44 projected files, 8 exact package DLL, 3 isolated assets/cache entries, 2 CA DLL |
| `./tools/hatifect-live-prepare` | `run-3me1m0zl`: PASS; package, producer и game DLL совпадают |
| `ui semantic.actions.reload` | `b87ec3a0-0ac9-4de1-a7b0-f3def4b58232`: PASS18 |
| `ui all` | `717909f4-4749-49c4-a2ca-51d863e9a15d`: PASS28 |
| `smoke flow.route.basic` | `e05b53a1-9a9c-41a1-88f3-f00b12c683cf`: PASS6 |
| `smoke flow.save.isolation` | `5fb52a5f-25cd-4ee6-a79d-eca167e83036`: PASS7 |
| `ui semantic.performance` | `50bae9c3-d01b-49ed-9d28-46214ca13bc8`: PASS2, 620 frames, p95/p99 0.062207/0.560833 ms, 4569.084 B/frame, layout misses0 |
| `ui semantic.environment` | `2b34d83b-802e-4737-a8df-4957ed43311d`: FAIL; первые 5 Window checks прошли, automatic transition завершился по таймауту |

Команды сборки использовали command-local `DOTNET_gcConcurrent=0`; игровой процесс запускался с обычным GC. Для PERF сохранены 12 наблюдений без dotnet/MSBuild/vstest. Все 8 composed EN/RU × 75/100/125/150% PNG фактически просмотрены; на английском 150% native cursor частично закрывает конец нижней диагностической строки. Options bytes/modes восстановлены, все 6 рабочих копий сейва очищены, golden hashes неизменны. UI fingerprint этой исходной поставки — `fccb1d033cc68d26cd7e58bb4b679bf6e23414fa8fa720d6991e54f9a449fef6`. Её общий результат остаётся **FAIL** в `artifacts/alpha46-integration-preflight/baseline-outcome.json`.

## Причина environment FAIL и исправление сценария

Диагностический source `a456eeefb796f9f23a8ce59d358fb635551a6996` добавил ограниченные снимки requested/native/accepted environment. Scoped `run-cb5mtxjy` — PASS50; prepare `run-n2di4dep` — PASS. Native `9bd5be53-f786-4329-9ce9-699f8e32194c` сохранил **FAIL** на fingerprint `915afbf137d8daf9c6287d576f9df1a752bad5b66db65e3a547b16b831cec2f3`. На requested сценарий устанавливал Controller при `gamepadMode=Auto`; уже на tick1 игра возвращала MouseKeyboard. Native и accepted environment при этом совпадали: locale `ru-RU`, scale1.5, viewport854×480 и MouseKeyboard. До tick600 сценарий ожидал отменённый игрой input target. Это дефект тестового стимула; наблюдение не подтверждает дефект синхронизации host. Подробности и исходный FAIL сохранены в `artifacts/alpha46-environment-probe/diagnostic-outcome.json`.

Проверенное исправление владельца UI `2b4eda7a599b56b2de96220297c6ca41ca4a3cad` принято GQ как `7d68b6ab87997ff096c33ffbb42aa17b8a5bf30d`: семь добавленных строк в одном environment fixture сохраняют исходный GamepadModes, закрепляют requested input через ForceOn/ForceOff и восстанавливают исходный mode вместе с controls. Production hosts, публичный API, версии, все 25 checks, acceptance predicate и лимит 600 ticks не меняются.

На исправленном source команда `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-test ui --platform --project 'Hatifect UI/tests/Hatifect.UI.Stardew.Tests/Hatifect.UI.Stardew.Tests.csproj'` дала `run-px5ihswm` — **PASS50** по actual TRX. Prepare `run-9nykgrit` — **PASS**, 8 producer/game DLL сверены. Fresh `ui semantic.environment`, request `4a2bb5d3-c38e-4b52-8685-30ea4023f823`, — **PASS25** на fingerprint `c2f56563f4015f13614fcd5c31e30ab9a45c7119f03f5b87af0cbb8d98d4ba08`. Window/Terminal принимают automatic transition за 1 tick, HUD/active-menu за 2 ticks; 8 pending operations доставляются однократно на owner thread, каждый host сохраняет 256 idle synchronizations без source reads/allocations. Process exit0, teardown errors пусты, options bytes/modes восстановлены, request-owned save copy очищена, 3 golden hashes неизменны. Все 53 source/doc hashes связаны с Git; фактические проверки сохранены в `artifacts/alpha46-environment-fix/`.

Это закрывает выявленный environment fixture FAIL. Результаты исходного `fc83019` сохраняют свою source attribution и не объявляются проверками исправленной поставки. Промежуточный documentation source `72a853e` прошёл C `run-4gsnlpuw` — PASS1528 +366, G `run-rmlk6irj` — PASS1801 +366 и P `hatifect-ui-ca-isolated.nfo6k75j` — PASS90. Перед его native запуском выявлена дополнительная ошибка восстановления custom locale в fixture; окончательная проверка выполнена после её исправления.

## Восстановление custom locale в сценарии

Установленный `LocalizedContentManager.CurrentLanguageCode` очищает descriptor пользовательского языка при выходе из `mod`. Поэтому одного восстановления enum недостаточно. Исправление UI `728a7f9af828f4eb529dad7538d8be1c4906b762` принято GQ как `61e8a941bbb2936e2b41f029e931b2ee8d30fe8e`: fixture захватывает descriptor и uncached locale до изменений, восстанавливает custom language через публичный `SetModLanguage` и проверяет enum, ту же reference identity и строку locale. Вложенный `finally` освобождает pending worker sources даже при ошибке восстановления. Production host, публичный API, версии, 25 checks и лимит 600 ticks не меняются.

Owner scoped `run-q9_ky7j2` — **PASS50** по actual TRX; GQ сверил source postimage, тестовый результат и поведение установленного игрового setter. Evidence: `artifacts/alpha46-locale-correction/owner-review.json`. Отдельный native старт с custom locale — **NOT_RUN**; успешный запуск ниже начинается с EN и не доказывает исполнение custom-start ветки.

## Итоговая общая приёмка source 61e8a94

Все проверки ниже выполнены на неизменном `61e8a941bbb2936e2b41f029e931b2ee8d30fe8e`. Сверены 53 source/doc hashes с рабочей копией и Git, фактические строки 17 TRX, отчёты native запросов, process exit и восстановление options. Результат `artifacts/alpha46-release-preflight/final-audit.json` — **PASS**.

| Проверка | Фактический результат |
| --- | --- |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | `run-856rkloj`: PASS1528 .NET +366 Python, 7 TRX |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | `run-k8rpgi68`: PASS1801 .NET +366 Python, 10 TRX |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | `hatifect-ui-ca-isolated.345up_ep`: PASS90; 44 projected files, 8 packages, 3 isolated assets/cache entries, 2 CA DLL |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-live-prepare` | `run-u5owgesw`: PASS; все 8 package/producer/game DLL совпадают |
| `ui semantic.actions.reload` | `0c835525-5651-4fe9-a82d-5b65238b5321`: PASS18 |
| `ui semantic.environment` | `5f25aa9b-c155-4d36-b260-22dae7543304`: PASS25 |
| `ui all` | `19185451-a271-4405-82cb-694e440935bc`: PASS28 |
| `smoke flow.route.basic` | `a5bad11d-9c9f-41ad-9da7-6e0256ad3d02`: PASS6 |
| `smoke flow.save.isolation` | `70d3c662-676c-47a1-b1e9-44b432f21480`: PASS7 |
| `ui semantic.performance` | `55b85814-71bf-4a98-9cb4-fe7b06536f55`: PASS2; 620 frames, p95/p99 0.051792/0.417460 ms, 4451.265 B/frame, layout misses 0 |

Native команды выполнены через `rtk proxy ./tools/hatifect-live-runner <kind> <scenario> <result.json> <artifact-directory>` и существующий worktree executor. Игровой процесс использовал обычный GC. Для PERF сохранены 15 наблюдений без процессов dotnet/MSBuild/vstest. Это наблюдение фоновых сборок, без утверждения о полностью бездействующей ОС.

UI fingerprint — `76f1f5ded2337aec104f3c5493239ffec06c19fe2421d0502a7dd5d2c3340bce`; Flow fingerprint — `c6ab1fc273a3ade0155112790d02c7cd20ce6e0af9420c020928607a3a69ca71`. Все шесть процессов завершились с exit0 без teardown errors. Options bytes/modes восстановлены, шесть request-owned save copies очищены, три golden hashes неизменны; PERF не требует сейва. Все восемь composed EN/RU × 75/100/125/150% × Dark PNG просмотрены: диагностический текст и focus border масштабируются без замеченного clipping, fade или перекрытия probe курсором. Это диагностическая матрица, без утверждения о полной продуктовой локализации.

Повторная проверка исходников и итогового diff не обнаружила открытых замечаний в этом срезе. Следующий шаг GQ — интеграция проверенного UI save-switch `889f9b1` / handoff `d044f22` и завершение review FLOWLINE text/Parcel `485c815`. Их новая незакоммиченная работа не включена в alpha.46; общий alpha.47 version bump принадлежит GQ.

Automatic Window/Terminal environment может обновиться через native Update или существующую Draw-time viewport synchronization; Update-only подготовка здесь не доказана. Нулевые allocations относятся к 256 неизменным public Synchronize на владельца, а не к полному frame/Update/Pump. Полные «Действия и их завершение»/«Адаптация интерфейса к окружению»/«Просмотр состояния Flowline», actual action save-switch, concrete typed consumer/messages, consumer localization и representative PERF остаются открытыми. Следующие владельцы согласованы: UI — actual A→title→B с pending root/portal actions в отдельном worktree; FLOWLINE — live text/Parcel projection после принятия host checkpoint. Физический Backspace этим срезом не закрывается.
