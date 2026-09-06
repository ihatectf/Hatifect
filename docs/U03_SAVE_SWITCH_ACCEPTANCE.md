# U03: действия при настоящей смене сейва

Статус полного U03 — **IN_PROGRESS**. Этот срез добавляет точный native-сценарий `semantic.actions.save-switch` и подготовку двух принадлежащих запросу копий. Public UI API, action contracts и persistence formats не меняются; проверяются действующие production owners Window, Terminal, HUD и overlay активного меню.

## Поведение и границы

Сценарий начинает pending typed actions в root и popup первого мира, вызывает настоящий возврат на титульный экран, а на следующем native Update проверяет завершённый teardown. Затем загружается другой мир, открывается новый owner с прежними Experience/action IDs и только после этого завершаются старые worker operations и новые действия. Драйвер не вызывает UI Pump или native Update вручную.

У каждого owner проверяются поздние success и fault; root и popup чередуются в роли faulted операции. Запрос сохраняет значение `7`, хотя после invocation исходное mutable значение меняется на `99`. Старые callbacks записали бы запрос в общий список эффектов новой сессии: проверка отсутствия старых записей обнаруживает нарушение generation fence. Pre-await эффекты диагностического действия сохраняются после закрытия; это не проверка rollback реальной доменной транзакции.

Каждая фаза ограничена 3 600 native Updates и общим transport timeout. После доставки выполняются ещё 30 Updates с повторной проверкой результатов. Native SMAPI subscriptions проходят через наблюдающий forwarding proxy к настоящим events; их количество и source subscriptions после retirement должны быть нулевыми. Ручной Dispose выполняется после проверки native teardown.

## Принадлежащие запросу копии

Общий helper `prepare_secondary` сохраняет существующие Flow правила: UUID второй копии получается через XOR 1; `flow.save.isolation` использует роли primary/primary, `flow.chest.isolation` и новый UI-сценарий — primary/secondary. Старые Flow helper names и default сохранены.

Child process получает `HATIFECT_SMAPI_TEST_SECONDARY_SAVE` и `HATIFECT_TEST_SECONDARY_RUN_ID`, вычисленные из принятого запроса. Новых request fields нет; ambient значения не наследуются. Admission ограничена тремя точными scenario ID. Будущий `flow.ui.isolation` пока не зарегистрирован.

Вторая копия меняет только исходный XML tag синтетического world ID `4242424242` на `4242424243`; остальные bytes, namespace/QName, первая копия и golden fixture сохраняются. Коллизия не передаёт право очистки чужого пути. Cleanup проверяет marker, имя, роль и runtime identity и удаляет только приобретённые копии, включая отказ подготовки и запуска.

## Проверки исходного checkpoint

Immutable source — [`889f9b1`](https://github.com/ihatectf/Hatifect/commit/889f9b1bc21fb1b0ec4f3e082e20bddfb072ebf9). Version authorities остаются alpha.46; новая общая версия принадлежит GQ. C и первоначальный native выполнены на `a93c993` плюс те же семь postimage hashes; G, P и повторный native выполнены после фиксации `889f9b1`. Это owner evidence для последующей общей интеграции, не заявление о публикации новой версии.

| Проверка | Результат и evidence |
|---|---|
| Новые Python случаи и существующие Flow случаи | 79 PASS; первоначальный RED сохранён в `artifacts/u03-save-switch/python-red.log` |
| Canonical tooling после регистрации | `run-80mypkkw`: 372 PASS, 0 failures, 0 skips |
| Stardew build/tests | `run-c3iingz4`: 50 PASS в фактическом TRX |
| C, неизменные source postimages до фиксации | `run-6a3955r8`: 1 528 .NET + 372 Python PASS, 7 actual TRX |
| G, immutable source | `run-rq2cxmzr`: 1 801 .NET + 372 Python PASS, 10 actual TRX |
| P, внешний CA consumer | `hatifect-ui-ca-isolated.so022zfa`: 90 tests, 44 projected files, 8 packages, 3 fresh assets/cache, 2 CA DLLs; UI source отсутствует |
| Native bootstrap | `54299d14-5307-4ac6-a51c-ae406dcd16bc`: PASS, process 0 |
| Native save-switch до фиксации | `dcb68135-dda2-4f62-91b3-93f66c5c2924`: 18/18 PASS, process 0, no teardown errors |
| Native save-switch immutable source | `cb89e4a8-eb45-4c47-a718-91beb6441f7a`: 18/18 PASS, process 0, no teardown errors |
| Независимый review исходного checkpoint | Kepler: source/tooling/native evidence без открытых замечаний; 18 artifact hashes и 7 source hashes совпали |
| Заключительный независимый review | Kepler: PASS, открытых findings нет; пересчитаны 17 C/G TRX, оба Python результата, P projection/packages/assets/deployment, 18 final native artifact hashes и 7 source hashes |

Native fingerprint: `837951b0fb73123da5ccaac96889917debe744fd744170ed0a2fa4414b9b112a`. Подтверждены четыре title transitions, пять загрузок, восемь старых cancelled/read-once операций без callbacks и четыре observed faults; восемь свежих callbacks на owner thread 1 с capture `7` и results `71/72`. У каждого owner закрытие доставлено один раз, оставшихся подписок нет. Обе рабочие копии фактически удалены, временные options удалены согласно исходному состоянию. Golden checksum `641fa65ce650533041c63c10586a5087b2ed24332b56da6caf9e4b1f75dd2644` не изменился. Подробности: `artifacts/u03-save-switch/native-audit.json`.

Повторный immutable native fingerprint: `7583ae98635ad43b746b32ebcae9b7fcbf66a3b2d75ef0419a299a2679f78783`. Те же 18 проверок и количественные результаты подтверждены новым процессом; golden checksum не изменился. Все восемь DLL из P packages побайтно совпадают с final Release producers и игровой поставкой. Audits: `artifacts/u03-save-switch/c-audit.json`, `g-audit.json`, `p-audit.json`, `immutable-source-manifest.json`, `immutable-native-audit.json`. Precommit evidence сохранено отдельно.

Команды из корня worktree: `rtk proxy ./tools/hatifect-check`, `rtk proxy ./tools/hatifect-check --platform`, `rtk proxy ./tools/hatifect-isolated-ui-ca --keep`, `rtk proxy ./tools/hatifect-ui-test semantic.actions.save-switch`. Для build/test использованы command-local `DOTNET_gcConcurrent=0`, `HATIFECT_DOTNET` и `HATIFECT_TEST_DOTNET` с локальным x64 SDK 8.0.424. Native executor запущен каноническим `rtk proxy ./tools/hatifect-runtime-executor serve`; его минимальный child environment не переносит GC workaround в игру.

Ограничения: физический ввод, GC retention и native-матрица всех concurrency policies не входят в этот fixture. VISUAL — NOT_APPLICABLE для функционального среза. Полные U03/U04 остаются открытыми: нужны concrete typed consumer, сообщения и состояния действий, оставшаяся environment/planner/consumer/PERF acceptance. Общая версия alpha.47 принадлежит GQ; этот owner checkpoint не занимает version authorities.
