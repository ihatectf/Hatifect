# U04/F12: наблюдение принятой active-menu поверхности

Актуальный статус на 2026-09-08: **U04 owner DONE** на source `04def9e`; [итоговая приёмка](U04_ACCEPTANCE.md) подтверждена независимым GQ review восстановленных raw evidence. Общая alpha47 acceptance и полный F12 учитываются отдельно. Ниже сохранены исторические результаты GQ и владельца, включая прежние FAIL и утраченные временные артефакты; их IN_PROGRESS/PENDING не задают текущий статус U04.

Исходный owner checkpoint прошёл перечисленные ниже проверки, но последующий integration review выявил P2 стоимости portal count, исправленный в `87fe76a` (проверки ниже); source [`df09a93`](https://github.com/ihatectf/Hatifect/commit/df09a93ef4f82e389622602005c7655032bd4e85). Статус полного U04/F12 — **IN_PROGRESS**. Этот prerequisite позволяет изолированному TestHarness проверить фактически принятое и отрисованное содержимое opaque active-menu handle. Он не заменяет native Flow lifecycle acceptance.

## Контракт и владельцы

`IUiSemanticSurfaceObservationApi` — optional additive interface в Experience. Consumer получает его отдельным `GetApi<IUiSemanticSurfaceObservationApi>("Hatifect.UI")` и создаёт наблюдаемые handles через тот же экземпляр API. Старые v1 surface/automation и form/builder contracts не изменены. Наблюдение доступно только exact TestHarness; production, foreign service/handle, thread/screen отклоняются. Видимый overlay, потерявший native menu owner, отклоняется сразу без lifecycle synchronization.

Runtime проецирует уже принятые scene/accessibility/frame: immutable semantic IDs, текст, состояние доступности действий, accepted environment, instance ID, scene/frame pair и последний успешно завершённый surface pass. Source getters, formatters и availability delegates при Capture не вызываются. Identity берётся из явного origin metadata, а не renderer suffix. Draw хранит захваченные до compositor версии; принятие новой сцены внутри public Rendered не превращает её в уже отрисованную. Action availability может менять frame без смены scene, поэтому обе версии обязательны.

Projection ограничена 1024 scene nodes, 1024 accessibility nodes, 4096 render primitives и 256 строками каждой коллекции. Name/Value/Text ограничены 4096 UTF-16 units без разрыва surrogate pair; IDs и accepted environment сохраняются без усечения. Truncated и HasUnmappedContent явно отмечают неполное наблюдение. Nested portals не проецируются, их число доступно как UnobservedPortalCount. Текст означает submitted string перед platform clipping/ellipsis, а CompletedRenderPass — проход данной поверхности, не номер game/backbuffer frame.

Observation state хранит только идентичность, числовой stamp и terminal DTO; scene/frame/source/history не удерживаются. Draw не создаёт новых heap objects для stamp. Retired handle возвращает immutable identity/lifecycle без контента, frame или environment; повторное открытие тех же semantic IDs создаёт другой InstanceId. Consumer отдельно связывает это с собственной publication identity.

## Проверки

Ниже сохранены выполненные **PASS** исходного checkpoint; они не закрывают обнаруженный позднее portal-count P2. Source, final diff и исходные evidence независимо проверены reviewer; после native run producer outputs сохранены без пересборки.

| Команда / стадия | Фактический результат |
|---|---|
| `./tools/hatifect-check` | `run-vg0t8p6d`: 1538 .NET +373 Python,7 actual TRX; pre-commit HEAD0fdfaf8 и16 тех же postimages |
| `./tools/hatifect-check --platform` | `run-bhntzz8u`: 1812 .NET +373 Python,10 actual TRX; exact df09a93 |
| `./tools/hatifect-isolated-ui-ca --keep` | retained `hatifect-ui-ca-isolated.oduh77ie`: 90/90 tests,44 source-identical projection files,8 packages,3 independent assets/cache,2 deployed CA DLL; UI source отсутствует |
| `./tools/hatifect-smoke save.bootstrap` | `ae9acf26-4f7c-4025-a105-1011ae27eca5`: свежий synthetic fixture создан и реально перезагружен, PASS1 |
| `./tools/hatifect-ui-test semantic.observation` | `5cb1b197-0150-4aa7-b991-8284b48c21cb`: PASS10 на exact df09a93; process exit0 |
| Независимый review | Source/C/G/P/final native — PASS; все18 artifact hashes,16 source hashes и8 package=producer=game DLL подтверждены |

Команды C/G/P и prepare использовали x64 SDK8.0.424 с `DOTNET_gcConcurrent=0`; canonical executor запущен отдельно как `rtk proxy ./tools/hatifect-runtime-executor serve`, native child использовал обычное минимальное окружение harness. .NET6 target и известный NETSDK1138 сохранены. Для source-only early handoff G/P/native честно оставались PENDING до этих прогонов; scoped Runtime `run-xr9r6z23` PASS488 и Stardew `run-j_e0xpk5` PASS51 предшествуют review fix. Итоговые C/G выполняют актуальные cases снова, не переатрибутируют старый результат.

Десять новых Runtime cases проверяют poison sources, accepted/completed version separation, failed render/preparation, availability-only frames, bounds, immutable snapshots, unmapped origins, retired handles и owner thread. Stardew case проверяет production rejection через реальный bridge; Python mutation case запрещает скрыть public API даже после пересчёта baseline. Исходные test-fixture FAIL сохраняются отдельно; production acceptance не ослаблялась.

Native semantic.observation содержит10 checks: API/same-service identity, initial render, inert capture, update, reentrant Rendered, automatic EN→RU/scale/controller profile, retirement, reopen, immediate native-owner loss и restoration. Фактический report и diagnostics сохраняют8 snapshots. В третьем completed pass callback принимает scene/frame2/2, но RenderedFrame остаётся1/1; четвёртый pass подтверждает2/2. Accepted environment меняется `en / scale1 / MouseKeyboard` → `ru-RU / scale1.25 / Controller`. Во всех активных snapshots подтверждены5 materialized fields и5 action IDs/availability. Retired identity сохраняется без содержимого; новый handle имеет другой InstanceId, оба owners закрыты.

Runtime fingerprint `sha256-runtime-v2`: `638fc96b9985d72997eb93f02e6938218ea982a18543b4819a7d513fb835d10f`. Игровой процесс86677 завершился с exit0; exceptions/teardown пусты, terminalError=null. Persisted options восстановлены, request-owned copy удалена. Fresh golden checksum `c91ce4ce1aabf19c672005b1424273b4a97a027bae96a18c1a1631299a107fe9` и inventory неизменны. Полные артефакты в `artifacts/runtime/5cb1b197-0150-4aa7-b991-8284b48c21cb/`, локальные повторяемые audits в `artifacts/u04-observation/`; они не коммитятся. Конечные PNG показывают состояние после cleanup и не объявляются visual matrix.

Reviewer обнаружил исходный P2: Capture после смены native menu до Update возвращал подтверждение старого владельца. Adapter теперь сразу отклоняет это наблюдение без закрытия и source/callback effects; новый native check воспроизводит именно этот промежуток. Screen guard действует и для retired handles. Текстовый лимит явно отделён от неизменяемых semantic identities. После исправления повторный source review и новый native check — PASS.

При работе применены hatifect-development/routing, architecture/dotnet-csharp, run-tests/xunit, code-testing-agent/extensions, find-untested-sources, analyzing-dotnet-performance, code-review/assertion-quality/test-gap-analysis. Static pairing завершился без parser errors:238 C# files,156 source/82 test,0 paired. Этот результат naming heuristic не является coverage; behavioral pairing и pseudo-mutations описаны отдельно в ignored `.testagent/status.md`. Host agent check — NOT_APPLICABLE: настройки Codex/skills не менялись.

## Ограничения и следующий шаг

Полные U04/F12 остаются открыты. Scope — root active-menu overlays; standalone Window/Terminal/HUD observation не предоставлена. Физический controller input, native foreign split-screen и запуск из custom locale не проверены. Flow Core/Persistence и consumers этим source commit не меняются; FLOWLINE подключает новый API в собственном lifecycle driver. Общие version authorities и публикация принадлежат GQ.

Следующий готовый UI-owned шаг — exact companion-save admission flow.ui.isolation с15 проверками и неизменным request protocol, затем совместная native consumer acceptance. Typed CA action migration и shared user-visible action messages остаются в U03.

## Follow-up: стоимость числа порталов

Integration reviewer обнаружил, что native Capture вызывал `Host.Session.Accessibility.Portals.Count`: только для числа создавались snapshot DTO всех открытых порталов. Это нарушало ограниченную стоимость root observation при неограниченном числе не наблюдаемых порталов. Internal Capture теперь принимает сам UiPortalHostSession и читает owner-thread guarded `ActivePortalCount` за O(1); native adapter и behavioral tests используют один путь. Публичный observation/v1 API не меняется, portal content по-прежнему не проецируется.

Регрессия `CapturingRootDoesNotAllocatePerUnobservedPortal` использует реальный host и128 Present: на старом подсчёте32 Capture выделяли79872 bytes без порталов против473856 bytes с порталами (`run-dy8vfix7`, **FAIL1/490**). После исправления тот же exact-allocation assertion и весь Runtime — **PASS490**, `run-uh7ynrc6`. Дополнительный case проверяет закрытие, retirement и foreign-thread rejection Capture и count accessor; прежние4095/4096/4097 boundaries теперь открывают два реальных портала вместо injected integer. Root elements/texts остаются прежними; poison callbacks не вызываются.

Follow-up source `87fe76a` прошёл independent source/scoped review и C1540/G1814 .NET +373 Python. Refreshed native `a8b3769c-b8c8-471a-b4dd-bc3435fe945c` — PASS10 на том же source. GQ проверил все17 TRX, четыре source postimages,18 artifact hashes,8 producer/game DLL,7 package DLL, runtime fingerprint, options/copy/golden. P2 закрыт; исходный df09 native этим не переатрибутируется. Package boundary/CA не меняются, P для follow-up — NOT_APPLICABLE. Следующий отдельный UI-owned срез — реальная отрисовка host title и source labels, выявленная integration review actual Flow acceptance; accessibility metadata не заменяет submitted text.

GQ интегрирует исправленный observation вместе с Flow lifecycle `ce8c996` в alpha.47 поверх `ce8df1c`; новые общие gates обязательны. [Общая приёмка и текущий native glyph defect](U03_U04_F12_ALPHA47_INTEGRATION.md).


## Follow-up: стабильные allocation после GC

Owning correction — **DONE в owner-ветке**: [`05aed65`](https://github.com/ihatectf/Hatifect/commit/05aed6534250b471c6460e39deb4895aa1a3e346) и test-only [`6148b33`](https://github.com/ihatectf/Hatifect/commit/6148b33ae0d6969bf274100d2664bfbb546a1933). Общая интеграция и её gates принадлежат GQ. Эта правка закрывает конкретный integration failure observation, полный U05 остаётся IN_PROGRESS.

В общем C `run-k270ugto` прежний exact portal test получил82160 bytes без порталов и80384 с128 порталами. Повторные PASS на тех же DLL не объясняли failure. Диагностика `run-ncm7va00` измерила512 отдельных captures в каждой фазе: без порталов509×2512 и3×3400 bytes, с128 порталами511×2512 и1×3400. Два выброса в одном32capture окне дают именно82160. Число порталов не влияет на размер выброса.

В `run-mk5zypjt` forced GC перед каждым capture и отдельный счётчик вокруг `Role.ToString()` локализовали причину:64/64 captures обеих фаз дают3400 bytes, из них984 в форматировании ролей и2416 в остальном capture. По сравнению с обычными2512 дополнительные888 находятся внутри `Role.ToString()`. В CoreCLR6.0.36 [Enum.GetEnumInfo](https://github.com/dotnet/runtime/blob/v6.0.36/src/coreclr/System.Private.CoreLib/src/System/Enum.CoreCLR.cs#L27) использует RuntimeType.GenericCache, а [RuntimeType cache](https://github.com/dotnet/runtime/blob/v6.0.36/src/coreclr/System.Private.CoreLib/src/System/RuntimeType.CoreCLR.cs#L2173) удерживается WeakTrackResurrection и пересоздаётся после утраты. Размер888 — измерение этого host, не переносимый budget.

Runtime теперь один раз сохраняет строки конечного набора12 accessibility roles в private static enum-key dictionary. Capture читает готовую строку; неизвестное числовое значение сохраняет прежний ToString fallback. Cache не хранит scene/source/frame/environment, не накапливает историю и не растёт по посещённым узлам. Public API, consumer, persistence и game adapter не меняются. Устранены также96 bytes обычного форматирования четырёх ролей в данном fixture; улучшение всего UI frame не заявляется.

Исходный `CapturingRootDoesNotAllocatePerUnobservedPortal`, exact equality и helper32warmup/32samples не менялись. Новый `CapturingRootKeepsAllocationStableAfterGarbageCollection` проверяет8GC cycles: каждый capture послеGC равен по allocation прогретой32capture серии (сравнение `warmBytes == bytes * 32`, без округления), сохраняет Elements/Texts и не читает poisoned source/availability. `CaptureUsesAcceptedTextAndAvailabilityWithoutReadingLiveSources` дополнительно закрепляет публичные Role strings `StaticText` и `Button`.

| Requirement | Evidence |
| --- | --- |
| Нет allocation, зависящих от числа не наблюдаемых порталов | Неизменённый `CapturingRootDoesNotAllocatePerUnobservedPortal`,128 actual portals |
| GC не вызывает повторного форматирования известных ролей в capture | `CapturingRootKeepsAllocationStableAfterGarbageCollection`,8 cycles |
| Строки ролей и accepted content сохраняются | `CaptureUsesAcceptedTextAndAvailabilityWithoutReadingLiveSources`: literal `StaticText`/`Button`, poison callbacks и source reads |
| Actual host lifecycle и environment | `semantic.observation`:10 native checks,8 captured snapshots |

Канонические команды через `rtk proxy`: `./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'`, `./tools/hatifect-check`, `./tools/hatifect-check --platform`, `./tools/hatifect-ui-test semantic.observation`. C/G/scoped используют `HATIFECT_DOTNET` и `HATIFECT_TEST_DOTNET` = `${HOME}/.dotnet/hatifect-x64-8/dotnet`, command-local `DOTNET_gcConcurrent=0`. Native запускается через canonical executor без этого GC override.

- RED `run-dzz4jgtn`:537 executed,536PASS/1FAIL у нового GC test, expected2512/actual3400. Временные диагностические instrumentation восстановлены до regression; их исходники и результаты сохранены отдельно.
- GREEN `run-8egkenr5`: **PASS537**. Test-only literal follow-up `run-rq5mw8j7`: **PASS537**.
- C `run-httgw_r0` на production source05aed65: **PASS1632 .NET +386 Python**,7 actual TRX. Это C до двух literal assertions; production в6148b33 не менялся, окончательные assertions повторно выполнены scoped и G.
- G `run-ahp7sk2b` на6148b33: **PASS1972 .NET +386 Python**,10 actual TRX, включая Runtime537/Stardew79/CA101.
- Fresh native `semantic.observation`, `023e2193-ed0e-43f1-add6-259e8bf259b4`, exact6148b33: **PASS10**,8 snapshots,2 закрытия. Active snapshots сохраняют `Dialog`, `Toolbar`, `Button`, `StaticText`; проверены inert capture, update/reentry, environment, retirement/reopen, native-owner loss и restoration.
- Runtime fingerprint **`1342ad2ab9c46eb07c393df456613138b5266e02b7d52b2cfb655512a3128547`** пересчитан по deployed и retained module; все8 producer/game DLL совпадают. Process15836 exit0, exceptions/teardown пусты. Options Restored, оба временных файла отсутствуют; request-owned save удалён, cleanup PASS. Synthetic golden и isolated root сохранены, собственный executor штатно остановлен.

Source/RED/GREEN independent Sol review — PASS. Low gap отсутствия literal Role assertions исправлен6148b33 и закрыт reviewer. Остальные10 enum labels и unknown numeric fallback не перебираются отдельным тестом; текущее сохранение их поведения подтверждено source construction/fallback. Final independent G/scoped/native evidence audit — **PASS, без находок**:10 actual G TRX, scoped537,18 raw и18 retained files,8 producer/game DLL, fingerprint,8 snapshots и физическая очистка options/save сверены. Reviewer не смог дополнительно опросить список процессов из-за sandbox; game completion/exit0 подтверждены raw process report, остановка собственного executor — его terminal output. P — **NOT_APPLICABLE** для этой внутренней правки (package boundary и CA consumer не меняются); предыдущий U05-c P на62be88a не переатрибутируется. Visual — **NOT_APPLICABLE**, native evidence здесь behavioral.

Retained owner root: `${HOME}/Developer/Hatifect/artifacts/worktrees/ui-source-notifications`. Raw runs — `artifacts/validation/<run>`, `artifacts/runtime/023e2193-ed0e-43f1-add6-259e8bf259b4`; audits/diagnostic samples/source copies/retained producer+game — `artifacts/observation-allocation-diagnostic/`. Original GQ C failure сохранён; новые accepted runs указывают собственный source.

Следующий UI-owned integration fix — подтверждённый Network root overflow при стандартном hosted viewport. Flow native `20e15c7a` и host-free `run-o5k9m59z` отклоняют min-height1128.9/1152.9 при доступной меньшей высоте; последний сохраняет768PASS и один адресный RED. Consumer задаёт семантику без geometry overrides. UI исследует clamped centered host, общий scroll и focus reveal; Flow fixture и strict impossible Window acceptance сохраняются. Полный U06 по-прежнему зависит от завершения U05.
