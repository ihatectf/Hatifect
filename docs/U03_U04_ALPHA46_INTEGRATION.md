# U03/U04: общая интеграция alpha.46

Статус общей приёмки — **IN_PROGRESS**. Frozen handoff UI — [`b6f60b4`](https://github.com/ihatectf/Hatifect/commit/b6f60b49af5c7f94469bab4021ab12f2b61acd88), immutable source [`0cd356f`](https://github.com/ihatectf/Hatifect/commit/0cd356f0fd252a0744fe6a871e46ba631193f876), implementation `902d834`. Основа GQ — опубликованная alpha.45 `12e7aab` с успешным exact CI34058056333 во всех 10 jobs. Все 17 version authorities уже переведены на alpha.46 владельцем UI; повторного изменения версий при интеграции нет.

## Область и контракт

Успешный active reload принимает assets, invocation, environment и scene до cancellation callbacks старого поколения. Rejected preparation сохраняет принятую scene, assets и pending root/portal work. Неизменённые документы не создают новое поколение. Window, Terminal, HUD и active-menu hosts передают один immutable environment через Invocation, Planning и composition; environment-only recomposition сохраняет модель и pending actions.

Кандидат theme/composer остаётся закрытым до принятия. Native owner, configuration и accepted scene/theme versions проверяются после callback-bearing preparation; устаревший внешний кандидат не перезаписывает принятую вложенную операцию. Retirement фиксируется перед fallible cleanup, поэтому оставшиеся event/watch delegates не доставляют уведомления закрытому владельцу; cleanup можно повторить. Изменены внутренние Runtime/Stardew seams. Opaque surface API v1 и Flow Core/Persistence не меняются.

## Проверка входящего checkpoint

GQ прочитал delta всех 20 изменённых source/test files, включая четыре Runtime test files, native fixtures и lifecycle checks. Все 48 source/version postimages сверены с immutable Git и frozen owner worktree; отдельные 17 non-Markdown version changes содержат только alpha.45→alpha.46. Version commit также добавляет один документационный абзац; финальный handoff меняет только три Markdown-файла. Все прежние 32 scenario objects сохранены, добавлены ровно `semantic.actions.reload` и `semantic.environment`. Исходные U03/U04 acceptance paragraphs остались без изменений.

Повторно вычисленные GQ owner audits совпали с retained results: C1528 .NET +366 Python, G1801 +366, все 17 actual TRX; P90, 44 projected files, 8 exact package DLL, 3 isolated assets/cache entries и 2 deployed CA DLL. Native reload `57ee8df6-0147-4417-9a1d-e5bd8376612a` — PASS18, environment `26d7b6c8-a88c-4df9-a60e-b2e9028e9b26` — PASS25. Fingerprint `a5eed91fe33c86c54c7cfda90d796f4aa40934c6da5edd1760ef606fae06bc06` пересчитан по игровой поставке; все 8 package/producer/game DLL совпадают. Options original bytes восстановлены, оба процесса завершились с exit0, обе рабочие копии удалены. Открытых замечаний в этом ограниченном review нет.

Артефакты GQ: `artifacts/alpha46-integration-preflight/owner-final-audit.json` и заново вычисленные audits в `owner/`. [Отчёт UI](U03_U04_HOST_ACCEPTANCE.md) сохраняет owner commands, RED→GREEN и отдельное заключение reviewer Kepler. Owner results не подменяют собственные проверки объединённого GQ worktree.

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

Это закрывает выявленный environment fixture FAIL. Общая публикация alpha.46 остаётся **IN_PROGRESS**: следующая задача GQ — завершить проверку исправленной поставки и её пакетной/runtime совместимости, затем публикацию и exact CI. Результаты исходного `fc83019` сохраняют свою source attribution и не объявляются проверками fingerprint исправленного `7d68b6a`.

Automatic Window/Terminal environment может обновиться через native Update или существующую Draw-time viewport synchronization; Update-only подготовка здесь не доказана. Нулевые allocations относятся к 256 неизменным public Synchronize на владельца, а не к полному frame/Update/Pump. Полные U03/U04/F12, actual action save-switch, concrete typed consumer/messages, consumer localization и representative PERF остаются открытыми. Следующие владельцы согласованы: UI — actual A→title→B с pending root/portal actions в отдельном worktree; FLOWLINE — live text/Parcel projection после принятия host checkpoint. Физический Backspace этим срезом не закрывается.
