# U04: итоговая приёмка environment planner

На 2026-09-08 исходные acceptance criteria U04 выполнены на проверенном owner-кандидате [`04def9e302c89eb948219db99206b8733ce59f16`](https://github.com/ihatectf/Hatifect/commit/04def9e302c89eb948219db99206b8733ce59f16). Статус U04 — **DONE**. Общая интеграционная приёмка и публикация alpha47 остаются отдельной работой GQ; этот отчёт не объявляет её завершённой. U03 остаётся IN_PROGRESS.

## Соответствие исходным требованиям

| Требование ROADMAP U04 | Реализация и доказательство |
|---|---|
| Viewport, scale, input, locale, theme, accessibility с provenance | `UiEnvironment`, `UiSemanticStardewEnvironmentCapture`, `EnvironmentCaptureTests` и `EnvironmentPlanningTests`; свежий native PASS25 проверяет все четыре host. |
| Детерминированный допустимый plan с полной обязательной семантикой | `UiPresentationPlanner` проверяет каждый элемент до возврата plan; `EnvironmentPlanningTests` проверяют deterministic fallback, identity и rejection поддельных IR/provenance. В consumer matrix все пять полей и пять действий присутствуют в каждом из 19 completed frames. |
| Объяснение правил, alternatives/rejections и origins без неограниченной истории | Двенадцать фиксированных alternatives; независимые immutable decision collections и шесть facet explanations. Поведенческие тесты проверяют повторяемость и неизменность; PERF измеряет normal и coverage-fallback path. |
| Одна Experience в нескольких environments | `InvocationEnvironmentTests`, `TerminalEnvironmentTests`, native Window/Terminal/HUD/active-menu: модель, focus, draft и pending root/portal work сохраняются. Автоматические transitions проходят без прямого вызова Synchronize из этой фазы driver. |
| Невозможная комбинация даёт explicit diagnostic/fallback | `UiPlanningException` с offending identity и двенадцатью причинами; успешный fallback не пропускает элементы. Native rejected/reentrant preparation сохраняет принятое состояние и успешно повторяется. |
| Native и видимое представление | Свежая Flow matrix EN/RU, scales75/100/125/150 и controller profile; реальные submitted title/labels/values/actions, readable ASCII direction. Исправления Runtime headings и Stardew glyph fallback совпадают с UI source побайтно. |
| C/U/P/PERF и применимый G | Фактические результаты ниже, независимые audits PASS. Бюджеты и acceptance assertions не изменены. |

## Свежие проверки в постоянной рабочей копии

Рабочая копия: `${HOME}/Developer/Worktrees/Codex/ui-recovery`, source `04def9e`. Все команды запущены с разрешённым `require_escalated`; C/G/P используют x64 SDK8.0.424 и .NET6 target. Для них задан `DOTNET_gcConcurrent=0`; отдельный Planning replay выполнен без этого override. Canonical native child получает минимальное окружение и GC override не наследует.

| Команда | Evidence | Результат |
|---|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 HATIFECT_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet HATIFECT_TEST_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet ./tools/hatifect-check` | `artifacts/validation/run-j6tn4cyk` | PASS1548 .NET +373 Python,7 TRX |
| Та же команда с `./tools/hatifect-check --platform` | `artifacts/validation/run-kp08d24i` | PASS1830 .NET +373 Python,10 TRX; Stardew59 включает8 glyph cases |
| `rtk proxy env HATIFECT_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet HATIFECT_TEST_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet ./tools/hatifect-test ui --project 'Hatifect UI/tests/Hatifect.UI.Planning.Tests/Hatifect.UI.Planning.Tests.csproj'` | `artifacts/validation/run-16u94l88` | PASS118; обычная GC |
| C/G environment prefix с `./tools/hatifect-isolated-ui-ca --keep` | `artifacts/u04-recovery/isolated-p` — постоянная копия исходного `hatifect-ui-ca-isolated.8fag3ing` | PASS90;44 projection files,8 exact producer/package DLL,4 resolved UI packages в изолированном cache,3 project.assets.json,2 CA DLL; UI source и UI deployment copies отсутствуют |
| `rtk proxy ./tools/hatifect-smoke save.bootstrap` | request `a08c475d-2dbd-434c-9cc3-38a73e2b290a` | PASS1; новый isolated fixture |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-ui-test semantic.environment` | request `a9fedcad-0a84-4fb6-a0c4-a8a8998dfa44` | PASS25;4 host,8 callbacks,по256 idle Synchronize с0 reads/allocations |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-ui-test semantic.performance` | request `3a9cd048-3567-4b75-8dc4-6e80c90d8041` | PASS2;620 frames |

C/G: все индивидуальные TRX outcomes — Passed, failures/skips отсутствуют. Независимый reviewer проверил source hashes, все17 TRX, Python counts и отдельные Planning118. P отдельно проверен по44 projection hashes,8 package/producer DLL и resolved cache; совпадение сохранилось после native prepare.

Оба UI native reports имеют independently recomputed fingerprint `e12ca923780b4a0560d2c9947169ca5050266d0c3b7fa44c8d419e1cb679f5a1`. Восемь game DLL равны producer. Процессы exit0, exceptions/teardown пусты, terminalError отсутствует. Options восстановлены, environment working copy удалена; PERF запускается без сейва. Golden checksum `a4e5fd591712acc839b55152ce2e9b18433537656e34e1c3af8c492467bbf345` сохранён. Собственный executor штатно остановлен.

## Численные бюджеты

| Измерение | p95/p99, ms | Allocations | Область |
|---|---:|---:|---|
| Planning normal | 0.015208 / 0.020917 | 8717.41 B/plan | 600 alternating Wide/Controller plans; Browse100/Search/Inspector/Actions |
| Planning coverage fallback | 0.023791 / 0.027792 | 11521.41 B/plan | тот же representative fixture с rejected alternatives |
| Native Terminal | 0.083625 / 0.495585 | 5337.75 B/frame | 620 frames,Dark,1470×956,scale1; normalized controller/wheel input |

Неизменённые limits: p95≤2ms, p99≤4ms, allocations≤16384B. Native measure/arrange miss ratios — оба0.0016129 при limit0.2. Девять process samples во время game PID27561 показывают отсутствие dotnet/MSBuild; это выборочная проверка, не непрерывная запись нагрузки машины. GQ и FLOWLINE приостановили тяжёлые процессы до завершения запроса.

Эти измерения подтверждают representative planner и именованный Terminal steady-frame budget. Они не измеряют суммарный Flow/game frame, произвольный размер модели или будущие U05/Q02 throughput-критерии. 256 idle Synchronize отдельно доказывают стоимость этого вызова, не заменяя native PERF.

## Actual Flow consumer и визуальная проверка

Свежий request `7a9a735e-8216-40c9-a884-d471aaa5b173` в `${HOME}/Developer/Hatifect/artifacts/worktrees/flow-recovery` — PASS15,19captures,6retired surfaces. Source `522bef4` содержит только последующие docs относительно `5a404242e61d98853b7ce1f98b94fba5bca626ed`; восемь затронутых UI source/test postimages совпадают с `04def9e`. Flow runtime fingerprint: `84300a00093f2fc673375a6ce81a7232b252f427dc4d82e89260d9af9ebf4de8`.

UI audit проверил каждую из пяти semantic field identities (три StaticText, два Inspector), пять actions, соответствующие title/labels/values в submitted Texts, отсутствие unmapped/truncated content и completed rendered frame во всех19 observations. UI owner просмотрел оригинальные composed cargo-EN и controller-RU PNG: заголовок, подписи, значения и `->` видимы без обнаруженного clipping. Flow owner просмотрел все19 composed originals для EN/RU, четырёх scales, controller profile и A7→B13→A7. Независимый Flow reviewer отдельно просмотрел все19 original UI-layer PNG и подтвердил38 PNG hashes,59 source postimages,14 DLL pairs,15 assertions/lifecycle/cleanup без findings; итоговый Flow doc commit `ba983c6`. Поле Result пусто во всех19 captures: отображение непустого action result относится к следующему U03/F13 срезу, а не к этой read-only matrix. Физический controller input и полный Flow frame budget не заявлены.

Audits находятся в `artifacts/u04-recovery/{c-audit,g-audit,p-audit,planning-ordinary-gc,environment-audit,performance-audit,flow-native-audit}.json`; native raw reports — в `artifacts/runtime/<request-id>`. Flow original reports/PNGs и audits находятся в его постоянном worktree. Исходные snapshots не подменяются Markdown-описанием.

## Восстановление evidence и следующий шаг

2026-09-08 прежние `/private/tmp` worktree отсутствовали; старый executor handle82221 больше не существовал. Git source `04def9e` восстановлен в постоянный worktree. Уцелевшие GQ independent audit JSON сохранены отдельно в `artifacts/u04-recovery/historical-audits`; прежние raw TRX/PNG считаются недоступными, а не восстановленными из hashes. Все финальные gates этого отчёта выполнены заново и имеют raw evidence. Исторические RED и ранее проверенные checkpoints остаются описанными в соответствующих журналах.

Следующий UI-owned срез — U03: immutable локализуемые сообщения actions, captured presentation для Running/Rejected/Failed/Cancelled и конкретный typed CA consumer. Flow migration координируется с F13. Отдельная доменная failure не требует искусственного Exception; existing `Failure(Exception)` и owner/generation fences сохраняются. U05 становится готов по зависимости U04, но текущая последовательность сначала завершает U03.

## Принятие документации в общей задаче

GQ принял owner documentation handoff `4806ce73c2bb3c43990201fb8eaf7e377343668a` после независимой проверки исходных критериев U04, source overlap, всех17 C/G TRX, Python373/373, Planning118, P90 и обоих UI native requests. Пересчитаны35 UI native raw hashes,54 Flow owner hashes и hashes19 общих Flow composed PNG. UI runtime fingerprint пересчитан по фактической игровой поставке; восстановление options и golden/copy cleanup подтверждено исходными файлами. Полный UI tree двух owner-веток не объявляется идентичным: проверены соответствующие environment/planning/runtime и hosting paths.

Единственная найденная P3 была неточностью формулировки P и исправлена в таблице: восемь относится к package/producer DLL, четыре — к реально resolved UI packages, три — к `project.assets.json`. Raw результаты не менялись. В общей задаче сохранены семь исходных audit JSON, оригинальный owner report и98 raw файлов C/G/Planning/двух UI native requests; inventory — `artifacts/alpha47-glyph-integration-preflight/u04-owner-closure/review-and-retention.json`. Полный P остаётся в постоянном `ui-recovery`, Flow owner raw — в постоянном `flow-recovery`; новый общий Flow request имеет собственные raw в GQ worktree.
