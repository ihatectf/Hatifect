# U05 — инкрементальный runtime

Полный U05 — **IN_PROGRESS**. Зависимости U02/U04 сверены с их implementation и общей приёмкой; U05-a **DONE в owner-ветке**: повторное использование измерений adaptive collection подтверждено; общая интеграция выполняется отдельно.

## U05-a: локальное изменение коллекции

Owning layer — UI Runtime. Раньше любая source revision очищала все measurement entries: изменение одного невидимого элемента повторно измеряло неизменившиеся видимые строки. Теперь owner replacement очищает cache, а revision сохраняет измерения. Индекс высот по-прежнему инвалидируется на revision, поэтому переиспользование не скрывает изменение геометрии.

Ключ содержит stable item ID и geometry context (icon presence, width, profile, locale, theme/typography, sizing/density). Cached value содержит version и exact label/supporting text: hit подтверждается ordinal equality, включая внешний source с отсутствующей или ненадёжной ContentVersion. Текст не хешируется в dictionary key; для обычного immutable item сравнение завершается по той же ссылке. Изменившееся содержимое заменяет value того же item/geometry slot.

LRU сохраняет прежнюю ёмкость 1024, exact rows 512; owner replacement сбрасывает cache, inactive collection удаляет owning state. Geometry variants остаются раздельными. Public API, persistence и consumer policy не меняются.

Implementation commits: [`8e0da27`](https://github.com/ihatectf/Hatifect/commit/8e0da2747844d9027e063f9b38b7cacad43ff444) — исходный срез; [`f0f68cf`](https://github.com/ihatectf/Hatifect/commit/f0f68cfcdcfaf7110f1b1841b25c5016829e4ecd) — исправление independent review. Между ними отдельный policy commit 6fa1c47 (=79e65e8).

## Проверки

Канонические команды: `./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'`, `./tools/hatifect-check`, `./tools/hatifect-check --platform`, `./tools/hatifect-isolated-ui-ca --keep`, `./tools/hatifect-ui-test semantic.performance`. Build/test используют проверенный x64 SDK8, указанный через HATIFECT_DOTNET/HATIFECT_TEST_DOTNET; для .NET test процессов установлен DOTNET_gcConcurrent=0. Этот override не передаётся native game процессу.

- Исходный RED run-j70lfcsv:529/531, два ожидаемых провала точного числа повторных измерений.
- Первоначальный GREEN run-vn5uk7h_:531/531; C run-31_8g7o8:1626.NET+381Python; G run-7m_cafra:1955.NET+381Python; P wu5nln33:101. Эти gates относятся только к 8e0da27 и не заменяют новые.
- Sol review обнаружил P2: text hashing и historical text/version entries. Исправлено f0f68cf; повторный source review finding closed.
- Fresh Runtime run-jh3xjyw5:532/532, включая cached offscreen mutation -> scroll-back, актуальную height/text и anchorY.
- C `run-35b4kxap`: **PASS 1627 .NET + 386 Python**, семь TRX и individual results сверены.
- G `run-3e9_t_ty`: **PASS 1956 .NET + 386 Python**, десять TRX и individual results сверены.
- P `emkky442`: **PASS 101**, 46 projection files, восемь exact packages, две CA DLL; UI source отсутствует. Все 1444 файла retained projection/evidence скопированы с проверкой SHA256.
- Native `semantic.performance`, request `18887925-3d97-43f2-b095-274373f02db9`: **PASS 2**, 620 frames; p95 **0.063374 ms**, p99 **0.447958 ms**, **5337.870967741936 B/frame**; measure/arrange miss ratio **0.0016129032258064516**. Стандартные budgets соблюдены; это не сравнительный benchmark collection deltas.
- Native request содержит source `f0f68cfcdcfaf7110f1b1841b25c5016829e4ecd`; runtime fingerprint **`00d0d842ff9ab733a0b697cfeff85bda7d057916ab7aa1cb3e6e13066ee97609`** пересчитан по deployed и retained runtime. Все восемь package/producer/game DLL совпали; retained producer DLL и полный game UI module сохранены. Process 76454 завершился с exit 0, teardown/errors/exceptions пусты; options Restored, оба временно созданных файла options отсутствуют. Сам isolatedRoot сохранён для следующих сценариев. SavePath=null, title-only workload; очистка request-owned save здесь NOT_APPLICABLE.
- Тихое окно подтверждено GQ и FLOWLINE. Десять process observations содержат собственные prepare dotnet до launch, затем только game 76454 во время сценария и пустой набор после exit. Executor штатно остановлен.

Постоянные raw/audits в owner worktree: `artifacts/validation/run-35b4kxap`, `artifacts/validation/run-3e9_t_ty`, `artifacts/runtime/18887925-3d97-43f2-b095-274373f02db9`, `artifacts/u05-incremental-runtime/{c-f0f68cf-audit.json,g-f0f68cf-audit.json,isolated-p-f0f68cf-audit.json,isolated-p-f0f68cf,producer-game-f0f68cf,native-f0f68cf-audit.json,perf-18887925-process-observations.json}`. Эти evidence не подменяют результаты будущего общего candidate.

Authoring failures сохранены: run-xpjjgm2c CS7036 при изменении общего MeasuredItem; исправлено отдельным cached wrapper. run-d49exhoh CS1674 — fixture использовал using для session без IDisposable. run-yq6k8_0j:531PASS/1FAIL — fixture передавал absolute0 в delta-based ScrollCollection; исправлено -currentOffset. Эти провалы не обозначены как production regressions.

Политика Luna/Sol применена отдельно: portable agent-check PASS; --host BLOCKED «Codex rejected config/read», личные настройки не менялись.

## Ограничения и следующий шаг

Срез не доказывает полную инкрементальность scene reconciliation, отсутствие source scans во всех hosts или lifetime всей generation. Native semantic.performance — существующий Terminal diagnostic workload, не динамический collection-delta benchmark. Full U05 требует отдельной исходной acceptance matrix. Продолжение U05-b и U05-c описано ниже; полная acceptance U05 остаётся открытой.


## U05-b: bounded height index — implementation, acceptance pending

Source `e992b6a` заменяет count-sized Fenwick storage для коллекций больше8192 строк sparse buckets. Exact-row LRU остаётся512; число buckets ограничено512×31. Малые коллекции сохраняют dense representation. Contributors учитываются отдельно от суммы: последнее удаление снимает bucket без floating residue, взаимно компенсирующие live deltas не теряются. Public geometry/API и scroll/anchor contract сохранены.

Scoped `run-ngxyspnu` — **PASS536**; C `run-tnhxwxdy` — **PASS1631 .NET +386 Python**; G `run-wke3k4po` — **PASS1960 .NET +386 Python**. P `0zi58k54` выполнил101 тест, но source identity — **FAIL**: все восемь nuspec лишены repository commit в SDK8 packed-refs worktree. Этот результат не считается принятым P. Исправление общей metadata policy принадлежит GQ; исходный worktree заморожен. Native на `e992b6a` не выполнялся. U05-c включает эту реализацию и проверен отдельно на своём source; его результаты не переписывают исторический FAIL U05-b.

## U05-c: active-menu source notifications

**DONE в owner-ветке** на source [`62be88a`](https://github.com/ihatectf/Hatifect/commit/62be88a5915f747f08073d6ff6f54093c509f8d0); общая интеграция выполняется отдельно. Owning layer — UI Stardew hosting. Активная menu surface раньше пропускала обычные source Changed при неизменном окружении в SynchronizeState. Теперь private binding подписывается на distinct source objects при первом Show и помечает pending epoch. Callback не читает модель и не входит в UI. Следующий owner-screen SynchronizeState захватывает актуальное состояние и подтверждает только epoch, захваченный до подготовки; reentrant publication остаётся pending. Серия событий до synchronization объединяется в одно accepted update.

Первый Show перечитывает состояние, изменившееся после Create. Неудачный Show откатывает подписки и сохраняет возможность повторного Show. Закрытие/Dispose сначала закрывают callback gate, затем независимо снимают все обработчики; неудачное снятие сохраняется для повторной cleanup. Учтены callbacks и retirement внутри add/remove accessors. Idle probe не читает источники и не выделяет память; это утверждение относится к helper probe, не ко всему кадру. Refresh остаётся явной принудительной синхронизацией.

Consumer и Flow persistence не меняются. Public API shape прежний; XML SynchronizeState уточняет контракт pending notifications, canonical API baseline и проверяющий pinned hash обновлены согласованно. В native reload/environment fixtures усилены прежние checks без удаления acceptance: четыре host вида, source burst/idle/retirement и retry после замены native owner во время первого Show.

### Проверки exact source

Команды выполнялись через `rtk proxy`; C/G/scoped/P использовали `HATIFECT_DOTNET` и `HATIFECT_TEST_DOTNET` = `${HOME}/.dotnet/hatifect-x64-8/dotnet`, `DOTNET_gcConcurrent=0`. Native использует собственную canonical process policy, без этого GC override.

| Команда | Run | Результат |
| --- | --- | --- |
| `./tools/hatifect-test ui --platform --project 'Hatifect UI/tests/Hatifect.UI.Stardew.Tests/Hatifect.UI.Stardew.Tests.csproj'` | `run-ljckn9zo` | **PASS79**, включая11 новых binding cases |
| `./tools/hatifect-check` | `run-s4tmypr1` | **PASS1631 .NET +386 Python**,7 actual TRX |
| `./tools/hatifect-check --platform` | `run-e90qjc7_` | **PASS1971 .NET +386 Python**,10 actual TRX |
| `./tools/hatifect-isolated-ui-ca --keep` | `mludvgmf` | **PASS101**,46 projection files,8 exact alpha.47 packages,2 CA DLL; UI source отсутствует |
| `./tools/hatifect-smoke save.bootstrap` | `eb8527f9-f252-4c0f-aaa6-cd5eff454166` | **PASS1**, synthetic isolated golden |
| `./tools/hatifect-ui-test semantic.actions.reload` | `38cea71f-a5c1-4623-b8cd-e93f640efe73` | **PASS18** |
| `./tools/hatifect-ui-test semantic.environment` | `44a8ff42-f036-4f5c-b3de-2c26f7abd631` | **PASS25**, failed-first-Show retry подтверждён |
| `./tools/hatifect-ui-test semantic.chests-anywhere-overlay` | `db68d342-ec35-4cf5-9454-15518c3cc17f` | **PASS5**, behavioral acceptance |
| `./tools/hatifect-ui-test semantic.performance` | `9b26287a-1ac4-4df0-9a36-387cd5e474d4` | **PASS2**,620 frames |

Все request repositoryHead = `62be88a5915f747f08073d6ff6f54093c509f8d0`; runtime fingerprint четырёх UI сценариев **`7547da6f80f4311497de30f61acbb8c775e224e52fcc078f11b74954dc763d55`** совпадает с пересчитанным deployed/retained runtime. Все8 nuspec содержат exact source commit; все8 package/producer/game DLL совпадают. Сохранены1303 файла isolated P и полный game UI module с assets/manifest, producer DLL и SHA256 manifests.

PERF: p95 **0.065168ms**, p99 **0.647959ms**, **5339.083870967742B/frame**, measure/arrange miss ratio **0.0016129032258064516**. Это existing title-only Terminal diagnostic workload, SavePath=null; не comparative collection-delta benchmark. GQ/FLOWLINE подтвердили тихое окно. Из16 ограниченных process observations четыре попали внутрь game interval `10:14:18.961672Z–10:14:39.192495Z`; во всех только собственный SMAPI PID2874. Sampling не является непрерывным профилированием процессов.

Все пять native processes завершились exit0, без teardown errors/result exceptions, runtime options Restored. Request-owned рабочие сейвы reload/environment/CA отсутствуют после cleanup; оба временных options файла отсутствуют после PERF. Synthetic golden и isolated root `/private/tmp/hatifect-smapi-test.u05c.ywtkibvs` сохранены. Собственный executor штатно остановлен. CA screenshot проверен: финальный fade после закрытия overlay; он **не является visual acceptance**. Для этого среза visual stage **NOT_APPLICABLE**, behavioral native gates приведены отдельно.

### Requirement | Evidence

| Requirement | Evidence |
| --- | --- |
| First Show, burst coalescing и отсутствие чтений в callback | `FirstActivationAndBurstNotificationsCoalesceWithoutReadingValues`; native reload `firstShowCurrent/deferredSource/burstVisible=true` для4 hosts |
| Publication во время prepare не теряется | `PublicationDuringPreparationRemainsPendingAfterAcceptingCapturedVersion`; native reload `publicationVisible=true` |
| Retry после rejected Show | `RejectedFirstShowCanReleaseSubscriptionsAndRetryWithFreshState`; native `semantic.environment.active-menu.show-owner`, retained/retried=true |
| Cleanup продолжает все removals и повторяет failed detach | `FailedActivationReleasesEveryAttemptAndRetainsOnlyFailedDetach` (2cases); `RetirementDisablesCapturedCallbacksAndRetriesEachOutstandingRemoval` |
| Reentrant accessors и late callbacks | `RetirementInsideAddAccessorCannotLeaveAnAttachedCallback` (2cases); `DisposalReentryDoesNotRecursivelyRemoveTheSameSource`; native reload `sourceReleased/retiredObservers=true` |
| Empty source set и idle | `SourceFreeSurfaceCanAcceptItsFirstShowAndRetire`; `IdleSynchronizationProbeDoesNotReadSourcesOrAllocate`; native reload256 unchanged synchronizations, `idleSource=true` |

Independent source review обнаружил и закрыл два дефекта до frozen source: несогласованный XML baseline и terminal Dispose после первого failed Show. Последний заменён retryable Deactivate; native environment подтвердил старый retry contract. Independent C/G/P evidence review — **PASS**, counters/individual results,1303 retained hashes и8 packages сверены. Финальный independent native evidence review — **PASS, без evidence-integrity findings**: пять raw directories (18/21/18/18/17 files),18 retained producer/game files, exact request/source identities, hashes, restoration и четыре quiet-window samples сверены независимо.

Исторические C failures сохранены: `run-zqqqihrb` — baseline hash; `run-71_dgowg` — pinned Python hash. Focused semantic contracts после исправления — PASS19. `run-9vr6ba55` PASS1631+386 относится к состоянию до Show-retry fix и **superseded** финальным `run-s4tmypr1`. Scoped77/78 также superseded финальными79; тесты не удалялись и не ослаблялись.

Raw evidence находится в owner worktree `${HOME}/Developer/Hatifect/artifacts/worktrees/ui-source-notifications`: `artifacts/validation/{run-ljckn9zo,run-s4tmypr1,run-e90qjc7_}`, перечисленные `artifacts/runtime/<request>`, `artifacts/u05-source-notifications/*-62be88a-audit.json`, `isolated-p-62be88a`, `producer-game-62be88a`, `perf-process-observations.json`. Host agent-check остаётся **BLOCKED** несовместимым `config/read` type в установленном CLI; portable stage PASS. Личные settings не менялись.

Полный U05 остаётся **IN_PROGRESS**: необходимы закрытие U05-b source identity на reviewed общей metadata policy, последующий аудит полной scene/delta/lifecycle acceptance и общий integration candidate. U05-c не обещает отсутствие scans во всех hosts и не меняет контракт произвольных action CanExecute callbacks.
