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
