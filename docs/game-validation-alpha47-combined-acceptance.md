# Совместная проверка в игре: alpha.47

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Проверенный source: `925345c90808697dc9881cf70c6c34afdb528ef8`. Это общая интеграция завершённого «Действия и их завершение», «Обновление интерфейса без лишних пересчётов» (подэтап a)/b/c, исправления SDK source identity и bounded role-label cache. Полные «Обновление интерфейса без лишних пересчётов», «Проверка текущей сборки в игре» и «Управление диагностической перевозкой» остаются IN_PROGRESS. Ветка feature/hatifect-roadmap опубликована; публикация develop ещё предстоит.

## Состав и границы

Metadata recovery `ff87f52` восстанавливает отсутствующую SDK Git metadata с проверкой корня и revision; исходный package identity FAIL сохранён в BUILD_SOURCE_IDENTITY.md. «Обновление интерфейса без лишних пересчётов» интегрирован merge `de8e1ed`; role cache исправлен в `a7490cf`, literal StaticText/Button assertions добавлены в `925345c`. Публичные API, persistence и версии пакетов этим checkpoint не меняются. Изменения «Управление диагностической перевозкой» (подэтап a)/b и последующий root-overflow fix в эту проверенную сборку не входят.

## Общие проверки

| Команда | Результат | Evidence |
|---|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | PASS1632.NET +389Python +13metadata | run-4vwvei48,7TRX |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | PASS1972.NET +389Python +13metadata | run-3ibsb0pj,10TRX |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | PASS101;8packages,47projection files | hatifect-ui-ca-isolated.vnrl7rqw |

Actual TRX counters/results и source manifest проверены отдельно. P сохраняет1445 файлов; package repository commits равны source925345c, восемь package DLL совпадают с producer и игровой поставкой. Release manifest задаёт14 runtime DLL, все проверены. CI task branch: [run34218450749](https://github.com/ihatectf/Hatifect/actions/runs/34218450749), SUCCESS всех10jobs на exact925345c; это ещё не CI опубликованного develop.

## Изолированный runtime

UI сценарии выполнены каноническим `hatifect-ui-test <scenario>` с подготовкой, Flow — `hatifect-smoke <scenario>`. Все ниже завершились shell exit0; raw reports, fingerprints, original/retained hashes, восстановление options и очистка owned save copies проверены.

| Сценарий | Request | Результат |
|---|---|---|
| semantic.observation | `8819f0a1-b1b6-42a0-9a67-5f2c4c9ed99e` | PASS 10 |
| semantic.actions.reload | `2f6427a2-6e46-4f3b-845e-2ffdae2403e7` | PASS 18 |
| semantic.environment | `034b7778-85b9-4596-bbfa-fd9fac48b6e2` | PASS 25 |
| semantic.actions.save-switch | `b7a86771-4924-4e67-bd04-78a35df4eea0` | PASS 18 |
| semantic.actions.pump | `3c4165af-1de3-46b9-9c0f-495ba182740b` | PASS 4 |
| semantic.chests-anywhere-overlay | `6914aac3-74c7-4877-9d86-4590f0bdc653` | PASS 5 |
| flow.ui.names | `14e2a785-71c7-4e43-82c0-8f64e9f99f7c` | PASS 7 |
| flow.route.basic | `16db11ec-055f-452b-ba6e-e39ebba1e6f0` | PASS 6 |
| flow.save.isolation | `8347ede6-564b-449a-a1b4-c51faa1feb30` | PASS 7 |
| flow.ui.isolation | `e76ea759-547c-484a-b105-8dfb116a0a91` | PASS 15 |
| flow.chest.isolation | `5645630e-76b0-4e99-8d9f-af2b974bdc53` | PASS 12 |
| all | `5f6d7a62-eb9b-483a-ac30-389ba73dbb3a` | PASS 28 |
| semantic.performance | `9e27eb63-f4ec-4575-bcd1-791756b4aba7` | PASS 2 |

UI fingerprint: `6699b3c59f6c8a5947aad40cd5ee664b23a8a1b4d9142cde122418385578aa28`; Flow fingerprint: `048af19adf51c0a3dbf46766fee604b86c93995b2666f19f5f3a5df8a15d0906`. Environment: Stardew1.6.15 build24356, SMAPI4.5.2, UIalpha.47. DOTNET_gcConcurrent=0 относится только к build command, не к игре.

Отдельные structured audits проверяют четыре host в reload/environment/save-switch, отсутствие поздних callbacks, failed-cleanup retry и публикацию новых owners. Environment сохраняет256 unchanged-sync samples на каждый host без allocation/source reads; это не общий frame budget. CA проверяет все восемь typed actions через normalized Tab/Enter; это не физическая клавиатура.

## Visual и PERF

Фактически просмотрены8 Pump PNG,19 Flow composed PNG и один Scale75 UI-layer,8 aggregate EN/RU×75/100/125/150%×Dark PNG. В этих fixtures сообщения, заголовки, focus и status находятся в кадре. Английские диагностические labels не означают полной продуктовой локализации. Узкий Parcel/Terminal не исключает известное обрезание более широкого «Управление диагностической перевозкой» Network наScale75.

Standalone PERF:620frames,p95 0.065084ms,p99 1.499583ms,5189.987B/frame,measure/arrange misses0. Действующие бюджеты2/4ms,16384B/frame выполнены. Quiet window согласован с UI/FLOWLINE;30 process samples,9 во время игры,3 пустых после выхода,max gap2.056605s. Это дискретные наблюдения, не непрерывная трассировка. Первый observer aggregate ошибочно завершился по стартовому process.json; gap сохранён, чистое окно подтверждает отдельный повтор. Код product harness и бюджеты не ослаблены.

## Открытые условия

Native physical input request `b91f5a8b-cb26-4955-a46f-64bf3bc31999` завершился FAIL четырёх required checks через314.373s: последняя фаза Pointer, все SMAPI input counters0. GQ выбрал окно через CUA, но не выполнял pointer/key/text actions; пользовательский ввод не подтверждён. Shell exit1, owned game exit0 без teardown errors; исходные options bytes восстановлены, owned save удалён,23 raw files сохранены с hash-проверкой. Это незавершённая физическая проверка, не доказательство нового Backspace defect. Исторический Backspace FAIL не переименовывается в PASS. Полный «Обновление интерфейса без лишних пересчётов» scene/delta/lifecycle acceptance и «Управление диагностической перевозкой» native action matrix остаются у соответствующих owners; root-overflow и Scale75 не включены в этот source. Следующий интеграционный шаг — принять проверенные «Управление диагностической перевозкой» (подэтап a)/b и owning UI corrections, сохранив точные identities нового candidate.

Raw evidence и audits: `artifacts/alpha47-u03-final-integration/`; исходный manifest `u05-final-combined-source-925345c.json` содержит644 files. Исторические failed runs сохраняют собственные статусы и source identities. Этот отчёт не закрывает полный roadmap.
