# U04: общая интеграция alpha.45

Общая интеграционная приёмка — **PASS** на source [`92f4890`](https://github.com/ihatectf/Hatifect/commit/92f48904e766f1c3fac479d84c85a57502d41b75); публикация и её CI ещё предстоят. Полный U04 остаётся **IN_PROGRESS**. GQ принял только зафиксированный checkpoint `f4b7386436166fae7f66f5a45447a0335a441e0e`: foundation `b89c792` и Stardew capture `29373a2` с их документацией. Основа интеграции — опубликованная alpha.44 `bea00ad`, включая explicit Terminal generations, native fixtures и общую background policy `fe51271`. Все 17 package/version/consumer authorities переведены с alpha.44 на alpha.45 одним владельцем — GQ.

## Контракт и область

Immutable `UiEnvironment` принадлежит Semantics и не удерживает игровые объекты. Planning принимает logical viewport, scale, input, locale, theme, accessibility preferences и происхождение значений; Runtime Invocation предоставляет именованный environment-вызов с сохранением legacy profile API. Compiler и planner требуют покрытия всех обязательных capabilities. Ранее допускавшиеся explicit presentations с частичным покрытием теперь отклоняются вместо потери семантики; это намеренное изменение, требующее проверки существующих consumers.

Stardew adapter захватывает игровые значения и переиспользует неизменный снимок. Custom locale identity и английский пустой suffix обработаны отдельным исправлением владельца. Подключение capture к native hosts/Terminal, согласованное принятие environment и scene, active reload и live consumer locale относятся к следующему UI checkpoint alpha.46. Текущая интеграция не объявляет эти механизмы реализованными. Flow Core/Persistence и frozen surface API v1 не меняются.

## Источники и проверка владельца

GQ ранее прочитал все 11 source/test files и связанные contracts, затем повторно сверил каждый immutable Git blob с `artifacts/alpha45-integration-preflight/owner-final-audit.json`. Открытых замечаний в этом ограниченном source review нет. Owner C1490 .NET +365 Python, G1763 +365, scoped capture50 и retained P90 проверены по фактическим TRX, package/cache/deployment hashes. Это результаты owner alpha.43 producer, а не общей alpha.45. Подробные исторические проверки сохранены в [ROADMAP-STATUS.md](ROADMAP-STATUS.md#u04-a-environment-planning-and-invocation-foundation).

Исходники объединились без конфликтов. Единственный append conflict в журнале roadmap разрешён сохранением полного alpha.44 evidence и обоих U04 разделов. Background helper, запуск с явными flags и временная граница перед Popen сохраняются; версия UI.Stardew меняется без удаления linked helper. Незакоммиченная работа соседних задач не импортируется.

## Общая приёмка

Все следующие результаты относятся к объединённому source `92f48904e766f1c3fac479d84c85a57502d41b75`, а не к исторической alpha.43 владельца. Артефакты находятся в `artifacts/alpha45-integration-preflight/`; `source.json` содержит hashes всех 34 изменённых файлов. Итоговый `final-audit.json` сверил текущие bytes с Git blobs, фактические TRX, runtime identities, package/game fingerprints, PNG и неизменность golden.

| Проверка и команда | Фактический результат |
| --- | --- |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | **PASS1499 .NET +366 Python**, `run-g3mz915j`, все7 TRX; `c-audit.json` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | **PASS1772 .NET +366 Python**, `run-qia6ig5p`, все10 TRX; `g-audit.json` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | **PASS90**, retained `hatifect-ui-ca-isolated.61463sz9`:44 projected files,8 exact package DLL,3 isolated assets,2 deployed CA DLL; `p-audit.json` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-live-prepare` | **PASS**, `run-u3zuo5s9`; все8 UI package DLL равны игровой поставке; `delivery-audit.json` |
| `rtk proxy env -u DOTNET_gcConcurrent -u COMPlus_gcConcurrent ./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Planning.Tests/Hatifect.UI.Planning.Tests.csproj' --no-build` | **PASS118**, `run-2qizpggb`; обычный GC, два bounded PERF cases; `planning-perf-audit.json` |

Command-local GC workaround относится только к .NET сборкам/полным тестам под Rosetta и не передаётся игре. Отдельный Planning запуск использовал уже собранный test assembly, явно снял оба GC overrides; перед ним не было процессов dotnet/MSBuild/vstest/SMAPI. Для каждого четырёхэлементного fixture выполнены100 warmups и600 чередующихся Wide/Controller plans. Без fallback: p95/p99 **0.015000/0.015583ms**, **8716.71B/plan**, максимум2 element decisions; с fallback: **0.017959/0.022583ms**, **11520.71B/plan**, максимум9. Сохранены бюджеты2/4ms/16384B и bounded trace. Это измерение ограниченного planner fixture, без claims о capture, native frame или произвольном размере graph.

## Свежая runtime-поставка

Canonical executor текущего worktree выполнил четыре request; точные команды, HEAD, options snapshots и logs сохранены в `run-runtime.py`, `<label>-request.json` и соответствующих logs. `runtime-audit.json` сверяет actual reports, host assertions, process records и восстановление options bytes/modes.

| Сценарий | Request | Результат |
| --- | --- | --- |
| UI/CA `all` | `5adb1d67-66e4-4643-bd1d-f7dbadc543bc` | **PASS28** |
| `flow.route.basic` | `04cc2bce-f150-4987-9033-5ffecb4d5637` | **PASS6** |
| `flow.save.isolation` | `b7e08ac4-e807-4af3-ae7a-518f1a062ba3` | **PASS7** |
| `semantic.performance` | `55c6c0cd-a655-4c49-8246-e81a3e71aef8` | **PASS2**,620 measured frames |

UI fingerprint — `d3b7b11840b46f6c429010e693d22f97707481e6a038c0498526af24a2740c18`; Flow — `c48d7b504000342d4ddb8e539d0a22cb6b0758492f4c2d5ff74ec645b39b785b`. Native PERF: p95/p99 **0.049166/0.367626ms**, **4604.154838709677B/frame**, measure/arrange cache misses0;10 наблюдений не обнаружили dotnet/MSBuild/vstest. Этот сценарий по canonical contract не требует сейва: `requiresSave=false`, actual `savePath=null`.

Aggregate подтвердил worldReady, pauseWhenOutOfFocus=false, fade=-0.0304 и visualMatrixRestored=true; игра в этом прогоне была active. Все8 composed1280×720 PNG фактически просмотрены: EN/RU×75/100/125/150%×Dark, диагностические controls/focus border и locale probe видимы без clipping. SHA256 и точная область просмотра записаны в `visual-review.json`. Это compatibility fixtures, а не вся native U04 environment matrix или продуктовая локализация.

Все четыре процесса завершились с exit0 и без teardown errors; options bytes/modes восстановлены. Все четыре рабочие копии трёх save-based requests удалены, включая обе копии isolation; три golden hashes неизменны. Первый дополнительный итоговый аудит ошибочно требовал provisioning report также у no-save PERF и завершился FileNotFoundError до записи результата. Исправленный аудит сопоставляет каждый request с его фактическим canonical save contract; сценарии, assertions и thresholds не менялись.

## Ограничения и следующий шаг

Foundation/capture integration прошла собственную проверку. Полные U04/F12, concrete typed consumer, согласованная consumer locale projection, active reload/save-switch и физический Backspace не закрыты этим результатом. Приёмка crash-return alpha.44 сохраняется как evidence именно alpha.44, новый crash-прогон здесь не заявлен. Следующий готовый шаг после публикации/CI — независимая приёмка frozen alpha.46 native host wiring и reload от UI; live text/Flow Parcel projection остаётся отдельным согласованным продолжением.
