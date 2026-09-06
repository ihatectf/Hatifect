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

Эти результаты относятся к `a93c993` плюс сохранённые postimage hashes в `artifacts/u03-save-switch/source-manifest.json`, версия alpha.46. Они не являются evidence новой опубликованной версии. Финальные проверки immutable source и package identity будут добавлены после фиксации исходников.

| Проверка | Результат и evidence |
|---|---|
| Новые Python случаи и существующие Flow случаи | 79 PASS; первоначальный RED сохранён в `artifacts/u03-save-switch/python-red.log` |
| Canonical tooling после регистрации | `run-80mypkkw`: 372 PASS, 0 failures, 0 skips |
| Stardew build/tests | `run-c3iingz4`: 50 PASS в фактическом TRX |
| C | `run-6a3955r8`: 1 528 .NET + 372 Python PASS |
| Native bootstrap | `54299d14-5307-4ac6-a51c-ae406dcd16bc`: PASS, process 0 |
| Native save-switch | `dcb68135-dda2-4f62-91b3-93f66c5c2924`: 18/18 PASS, process 0, no teardown errors |
| Независимый review | Kepler: source/tooling/native evidence без открытых замечаний; 18 artifact hashes и 7 source hashes совпали |

Native fingerprint: `837951b0fb73123da5ccaac96889917debe744fd744170ed0a2fa4414b9b112a`. Подтверждены четыре title transitions, пять загрузок, восемь старых cancelled/read-once операций без callbacks и четыре observed faults; восемь свежих callbacks на owner thread 1 с capture `7` и results `71/72`. У каждого owner закрытие доставлено один раз, оставшихся подписок нет. Обе рабочие копии фактически удалены, временные options удалены согласно исходному состоянию. Golden checksum `641fa65ce650533041c63c10586a5087b2ed24332b56da6caf9e4b1f75dd2644` не изменился. Подробности: `artifacts/u03-save-switch/native-audit.json`.

Ограничения: физический ввод, GC retention и native-матрица всех concurrency policies не входят в этот fixture. VISUAL — NOT_APPLICABLE для функционального среза. Полные U03/U04 остаются открытыми: нужны concrete typed consumer, сообщения и состояния действий, оставшаяся environment/planner/consumer/PERF acceptance. Общая версия alpha.47 принадлежит GQ; этот owner checkpoint не занимает version authorities.
