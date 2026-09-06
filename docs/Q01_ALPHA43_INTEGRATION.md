# Общая интеграция alpha.43

Ограниченная общая приёмка alpha.43 — **PASS**. P2 повторного захвата keyboard lease после неуспешного Hide подтверждён нативным RED→GREEN и исправлен. Итоговый source commit — [`6c30713`](https://github.com/ihatectf/Hatifect/commit/6c307137287a9a5634c4383d9773cc873bf1a068). Полные U03 и Q01 остаются **IN_PROGRESS**.

## Исходники и границы

Общая интеграция [`6444fe5`](https://github.com/ihatectf/Hatifect/commit/6444fe5) объединяет проверенный UI checkpoint [`f2f5a03`](https://github.com/ihatectf/Hatifect/commit/f2f5a032a36a56bd70ab9c3b9f74ac55025589c9) с GQ [`a85c8ce`](https://github.com/ihatectf/Hatifect/commit/a85c8ceb55ea985b6107d9c7ab27c09789f945e6). Сохранена исходная FLOWLINE regression ancestry `a7be299`. Все30 входящих non-Markdown postimages равны immutable checkpoint UI; единственный конфликт разрешён объединением обоих дополнений `ROADMAP-STATUS.md`. Документация package identity и verifier `c36a0d1` сохранены.

Runtime и Stardew host теперь доставляют готовые action results через owning game Update, включая root/portal и перекрытый HUD. Отзыв сначала блокирует всю удаляемую группу и удаляет membership, затем вызывает cancellation. Общий Runtime.Update распространяет эту границу на interaction recomposition. Неудачная подготовка action presentation сохраняет принятое состояние и возможность повторного Pump. Публичные surface signatures, Experience descriptors и Flow persistence не меняются;17 существующих version/package/consumer authorities согласованы на alpha.43.

## Проверка переданного checkpoint

GQ повторно прочитал реальные артефакты, а не только итоговое сообщение задачи UI. `artifacts/alpha43-integration-preflight/owner-final-audit.json` связывает:

- 30 исходных файлов с immutable Git checkpoint;10 ранее просмотренных production/test postimages не изменились. Финальная поправка диагностического GetResult публикует счётчик чтения после признака наблюдённого исключения; assertions сохранены.
- G `run-eg0veryf`: **PASS1698 .NET +365 Python**; все10 TRX имеют согласованные counters и individual Passed outcomes, без failures/skips.
- Native V2 `61e1e874-e390-430d-ab87-1e3c2aaca309`: **PASS4**, восемь probes,20 проверенных artifact hashes, PID14010 exit0 без teardown errors. Пять callbacks доставлены на owning thread с captured request7/result42; два поздних fault наблюдены по одному разу без callbacks. Native context replacement подавляет готовое завершение прежнего владельца.
- P `hatifect-ui-ca-isolated.m7giwmqf`: **PASS90**,44 точных projected files,8 пакетов,3 assets files только с isolated cache и2 точных deployed CA DLL. Все восемь packaged UI DLL равны DLL native V2 по сохранённым SHA-256.

Эти результаты относятся к owner candidate и fingerprint `0502d9a683a9fabb4d1e7f486a2deb78a6bc43e2a3084e5df802da892d1fd8ad`. Они не объявляются результатами новых DLL общей интеграции. Предыдущие V1 и неуспешные попытки сохранены в [журнале U03](ROADMAP-STATUS.md#u03-b-owning-update-pump-and-portal-retirement).

## Собственная приёмка GQ

Проверки общей интеграции выполняются в `${HOME}/Developer/Worktrees/Codex/345f/Hatifect`. Логи и отдельные byte/TRX audits сохраняются в `artifacts/alpha43-integration-preflight/`.

На исходной интеграции `6444fe5` завершены:

- `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check`: `run-nhoswj2z`, **PASS1457 .NET +365 Python**, семь TRX.
- `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform`: `run-8d69lqio`, **PASS1698 .NET +365 Python**, десять TRX, Runtime435.

Обе команды выполнялись на хосте с установленным x64 SDK8/runtime6. Все индивидуальные результаты Passed, counters совпадают, failures/skips отсутствуют; `c-audit.json` и `g-audit.json` содержат SHA-256 каждого TRX. Command-local GC workaround относится только к сборке/тестам, не к игре. Эти результаты сохраняются как база до correction.

На corrected source `6c30713` полный `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform`, `run-10wrvzrr`, завершён **PASS1698 .NET +365 Python**. `final-g-audit.json` связывает все10 actual TRX, включая Runtime435, без failures/skips. Host-free C# inputs исходного C не менялись; финальный G дополнительно проверил новый scenario catalog и те же365 Python tests.

`rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` — **PASS90** на retained `hatifect-ui-ca-isolated.x30nbzbc`. Дополнительный byte-аудит подтвердил44 exact projection inputs,8 alpha.43 package DLL против producer outputs,3 assets files с единственным isolated consumer cache и2 exact deployed CA DLL; UI source и лишних UI DLL у consumer нет. После финального `hatifect-live-prepare` (`run-k_2bm1ax`) все восемь package DLL совпали также с игровой поставкой. Индексы: `p-audit.json`, `package-game-hashes.json`.

## P2 после исходных C/G и нативное воспроизведение

FLOWLINE сообщил source finding, затем GQ независимо проследил его на `6444fe5`. Во время text input publication observer вызывает Hide. Host уже retired, keyboard Release проходит, но исключение при удалении event subscription оставляет `Visible=true`. Publication сохраняет ошибку observer и возвращает управление. Последующий `SyncTextInputOwnership` проверяет видимость, menu context и сохранённый focused snapshot без `_retireRequested`; поэтому keyboard lease может снова вызвать Acquire для уже retired host. Повторная проверка source — **FAIL/P2**. Исходные успешные C/G этого пути не покрывают.

UI подготовил отдельный native fixture [`53e85ff`](https://github.com/ihatectf/Hatifect/commit/53e85ffc4694a3b044e12e1123c50be6d7ca5ca2) и owning correction [`32c936b`](https://github.com/ihatectf/Hatifect/commit/32c936ba72ebeb9f472a261a95b255d4ceafc1ce), без alpha.44 WIP. Guard повторно проверяет current screen и retirement перед захватом keyboard lease. Сам input, publication, UI host, keyboard dispatcher и event binding остаются настоящими; diagnostic forwarding proxy внедряет только отказ `remove_UpdateTicked`. Сценарий `semantic.input.retired-overlay` исключён из aggregate и запускается отдельно.

GQ сначала интегрировал только fixture как `9485895`. `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-live-prepare` — PASS (`run-s9emx2qr`), затем canonical `hatifect-live-runner ui semantic.input.retired-overlay` создал request `c0e7c6a5-3afc-4ddc-b8b5-a4d1bd530710`. Итог — **ожидаемый FAIL** только `semantic.input.retired-overlay.keyboard`: `retainedForRetry=true`, `keyboardRestored=false`. Host report отдельно подтверждает retry PASS. Диагностика содержит completed/retainedForRetry/retryClean=true, restoredAfterInput=false, publicationVersion/observerErrors/removalFailures/closed=1, retainedHandlers=0, text=x. `terminalError=null`; PID20412 exit0 без teardown errors, исходные bytes/modes двух options files восстановлены. Длительность16.473s; UI fingerprint `eef4cfde7479a9babec834de347c42f402b7e9fb0c3b9329294745cb02f6c953`.

Correction интегрирована как [`6c30713`](https://github.com/ihatectf/Hatifect/commit/6c307137287a9a5634c4383d9773cc873bf1a068). Все31 non-Markdown input общей ветки равны corrected owner source; все необходимые ancestry сохранены. От исходного C изменились только три Stardew C# файла и scenario catalog; host-free C# inputs остались прежними. Native GREEN `e6a80234-2501-489b-95ae-35289b32180a` — **PASS2**,13.429s, PID22491 exit0. Все причинные preconditions прежние; restoredAfterInput меняется false→true, retryClean остаётся true, поздний ввод не меняет query. Трассировка, точные исходники, RED и provenance находятся в `retired-keyboard-source-finding.json`, `retired-input-handoff.json`, `retired-input-red-audit.json`, `runtime-audit.json` и `final-source.json`. Bounded source review fixture/correction у UI/FLOWLINE и повторный проход GQ не оставили открытых замечаний в этом исправлении.

## Свежая runtime-приёмка corrected source

Переиспользован живой executor PID29126 после проверки Ready. Подготовка и runtime используют только `.smapi-test/isolated`; SMAPI запускается с обычным GC. Каждый вызов проходит через `rtk proxy ./tools/hatifect-live-runner <kind> <scenario> <result.json> <artifact-directory>`. Helper `artifacts/alpha43-integration-preflight/run-runtime.py` задаёт UUID/argv, фиксирует source commit и bytes/modes options до/после. Результаты находятся в `artifacts/runtime/<requestId>`.

| Scenario | Request | Результат | Длительность / PID |
|---|---|---|---|
| `ui semantic.input.retired-overlay` | `e6a80234-2501-489b-95ae-35289b32180a` | PASS2 |13.429s /22491 |
| `ui semantic.actions.pump` | `cbfd73ff-cef1-455a-97d5-894fb4f9e758` | PASS4 |9.821s /22663 |
| `ui all` | `eacdc56e-c0b2-46df-8444-5d9850962ca7` | PASS28 |22.114s /22803 |
| `smoke flow.route.basic` | `8724915d-a883-4337-bfe9-c6409230d189` | PASS6 |10.820s /23167 |
| `smoke flow.save.isolation` | `c618298e-fb37-4739-a82c-d0f67eee8d2d` | PASS7 |10.491s /23414 |
| `ui semantic.performance` | `abec53ad-6e6d-47f1-93e0-15c790aaf97c` | PASS2 |17.709s /23900 |

Все шесть процессов exit0 без teardown errors, exceptions отсутствуют, isolated options восстановлены по bytes/modes. UI terminalError отсутствует. Action Pump диагностирует восемь настоящих probes, пять owning-thread callbacks с request7/result42 и два однократно наблюдённых late faults без callbacks. Aggregate восстановил matrix settings. Физический keyboard hardware acceptance остаётся отдельным условием Q01.

`runtime-audit.json` проверяет actual results, checks, process/options reports и вычисляет fingerprint по финальным DLL/assets: UI **`fdebf9cb24d282a4c3409b18daa59028e6bc1c7e3c5b308f636469a846c84c25`**, `sha256-runtime-v2`; Flow **`2f7a0c223f4941373ade08b25f33ec16ee0b3553081e488b53c4dfe55c58731f`**, `sha256-flow-runtime-v1`. Игра1.6.15 build24356, SMAPI4.5.2, UI1.0.0-alpha.43, Flow3.0.0-rc.89, CA1.30.1. Эти fingerprints относятся к source6c30713; owner V1/V2, RED и alpha.42 не переименовываются.

## PERF и визуальный проход

Отдельный PERF на финальных DLL:620frames, **p95/p99 0.048542/0.518625ms**, **4609.006B/frame**, measure/arrange cache misses0. Выполнены пределы catalog:≥600frames,p95≤2ms,p99≤4ms,allocations≤16384B/frame,cache miss ratios≤0.2. Surface — semantic-terminal-menu, Dark,1280×720,scale1. Одиннадцать наблюдений через2s не обнаружили dotnet/MSBuild/vstest. Перед запуском GQ дождался завершения обнаруженного dotnetPID23555; чужие процессы не останавливались. Во время отдельной выборки GQ не выполнял build/CUA/visual inspection. Это diagnostic Terminal measurement; оно не является A/B benchmark, полной нагрузкой Flow или доказательством отсутствия всей фоновой активности ОС.

После завершения PERF фактически просмотрены все восемь composed PNG aggregate: EN/RU ×75/100/125/150% ×Dark. Два diagnostic controls, focus border и полный locale probe находятся внутри1280×720 без наблюдаемой обрезки или пустого UI. Scene/game locale и desired/base/pixel scale согласованы с metadata каждого кадра. Названия diagnostic controls остаются English в RU. `visual-matrix-audit.json` сохраняет SHA-256 и наблюдение каждого изображения; исходные PNG не изменены. Полная продуктовая локализация, другие темы и macOS borderless этим не сертифицируются.

## Оставшийся объём

UI передал следующий alpha.44 checkpoint для явных Open/Follow generations; его общая приёмка выполняется отдельно. U03 сохраняет active reload, concrete typed consumer и полную reload/save-switch runtime acceptance. Общий Q01 также сохраняет незавершённую физическую Backspace-проверку. Предыдущие alpha.42 fingerprints и game evidence остаются историческими. GQ отвечает за общую приёмку и публикацию; продуктовые UI/Flowline срезы остаются у профильных задач.
