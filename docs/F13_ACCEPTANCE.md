# F13: управление изолированной fake session

Статус: **IN_PROGRESS**. F13-a опубликован в [`7d62ab5`](https://github.com/ihatectf/Hatifect/commit/7d62ab509ac6987df76938c4f34612669b3de16f), база — `203788f71da542470450e1b6aebf69f4a17135fb`, ветка `codex/flow-f13-actions`. Эта запись описывает проверенный объём consumer implementation и оставшуюся приёмку; полный F13 ещё не завершён.

## Зависимости и границы

F12 принят на общем source `08ecdb9` ([приёмка](F12_ACCEPTANCE.md)). U03 typed action contract и его reload/save-switch retirement проверены на source `9b6028c0561c5e1a8225bcdec7f1bcce64d81ead`: request `02565027-d1ae-4657-9693-c1872318761c` (18 save-switch checks) и `9fb55869-dbe5-4a63-a56b-c95015a89558` (18 reload checks), fingerprint `5fc2c2111b8357967c3c2be5343b12feaa0cad4279033d5fe58a13eaa2471480`. Локальный аудит `artifacts/integration/u03-dependency-native-audit.json` сверяет request/fingerprint и 43 raw hashes. Это подтверждает входной контракт; полная общая alpha47 acceptance и публикация U03 не объявляются завершёнными.

Owning implementation F13-a — Flow.UI.Semantic. Core, persistence и публичные UI API не изменены. Reserve (UI Dispatch), Cancel и RetryDelivery используют существующий доменный контракт через typed host. ReconcileTransfer и ReturnToSource сохраняют прежнюю реализацию; новые операции и гарантии возврата груза не вводятся.

## Реализованный checkpoint

- Request захватывает publication ID/version и domain session/revision/parcel/action. Перед effect проверяется актуальность владельца и domain availability.
- Host отвергает повторный запрос во время выполнения. Consumer дополнительно закрывает reentry и запрос до публикации результата. Команда выполняется один раз.
- Исходный domain result хранится до успешного atomic Pump; ошибка projection не повторяет effect. Другие открытые projections получают revision владельца.
- Applied отображается как Success с исходным result; domain Faulted — Failure с безопасным сообщением; остальные domain статусы — Rejected с соответствующим code. Настоящее исключение сохраняется отдельно от пользовательского текста.
- EN/RU availability/messages берутся из двух ограниченных enum-sized caches. Новые строки и словари при каждом availability read не создаются; это source inspection, не PERF-замер.
- Existing Parcel lifecycle/publication/locale tests вызывают действия через настоящий owning host. Native F12 retired-action probe тоже использует action automation вместо недопустимого direct invocation typed action.

## Выполненные проверки

| Проверка | Evidence | Результат |
|---|---|---|
| `./tools/hatifect-test flow` | `artifacts/validation/run-44k3yqtv`, `artifacts/f13/scoped-audit.json` | PASS 768/768, в том числе 13 новых cases |
| `./tools/hatifect-check` | `artifacts/validation/run-_bw4rs5h`, `artifacts/f13/c-audit.json` | PASS 1713 .NET +388 Python; семь TRX, zero failures/skips |
| Независимый source/test review | агент `t01_client_review`, Sol/xhigh; scoped source и actual TRX | Доказанных дефектов нет; native/create/G не входили в review |
| `./tools/hatifect-check --platform` | `run-_qxi68gc`, `artifacts/f13/g-audit.json` | PASS 2042 .NET +388 Python; до последней коррекции ожидания disposed-handle в native probe |
| `./tools/hatifect-test flow --platform` | `run-ch9uafob`, `artifacts/f13/platform-final-audit.json` | PASS 928/928 после последней коррекции native probe |
| Native input/action/result matrix | текущий checkpoint | NOT_RUN |

Первый G `run-141445rr` собрал candidate, но VSTest был остановлен sandbox при открытии локального сокета (SocketException 13); повторный запуск с необходимым доступом завершился PASS. При втором проходе исправлено ожидание retired native handle: owning action API может бросить ObjectDisposedException, и harness сохраняет проверки отсутствия read/effect/publication. Другие исключения не скрываются.

Новые 13 cases проверяют typed metadata/owner requirement, точный command capture, шесть domain outcomes, две projections, reentry и повторный dispatch, retry существующей доставки, запрет cancel в InTransit/Arrived/Delivered, разделение exception и безопасного текста. Existing publication regressions подтверждают retry projection без повторения команды и retirement после committed effect. Исторический `run-077y5n98` завершился build FAIL из-за отсутствовавшего namespace import; исправление проверено последующими scoped/C runs.

## Оставшаяся приёмка

1. Свежий isolated native regression на immutable source; G и финальный platform Flow regression выполнены.
2. Native create использует существующие NetworkExperience shipping actions и FlowSendCommand поверх bounded diagnostic IFlowNetworkApplication façade. Прямое provisioning runtime не заменяет UI create. Production shipping создаёт parcel и пробует Reserve; отдельная Created fixture проверяет самостоятельный Dispatch.
3. В изолированной игре подтвердить create/dispatch/cancel/retry, сообщения отказа, повторный click, stale screen, running operation и обновление другой projection. Сохранить request/source/fingerprint, фактический ввод и rendered observations с EN/RU/input/scale coverage.
4. Выполнить второй проход по итоговому diff, записать commit/publish evidence и только после полной матрицы пересмотреть статус F13.

Synthetic golden fixture скопирована в отдельный `.smapi-test/fixtures` текущего worktree с проверкой hash; исходник не изменён (`artifacts/f13/fixture-copy-audit.json`). Реальные saves/Mods не используются. Параллельная общая package source-identity проблема исследуется integration owner; чужая историческая runtime acceptance не заменяет новую приёмку этого checkpoint.

## F13-b: подготовка fake create

Diagnostic `FlowUiAcceptanceWorld` предоставляет существующий `IFlowNetworkApplication` поверх того же FlowRuntime/FlowApplication. Fixture ограничена двумя станциями, одним synthetic source stack и одним lifetime create. Она проверяет session/revision, endpoints, slot, полный fingerprint, quantity, route и running-create fence до effect; создаёт реальный aggregate и вызывает TryReserve, как production shipping. Отдельные helper-ы меняют только fake destination acceptance и продвигают fake logical clock. Core/public inventory/persistence contracts не изменены. Оставшийся исходный стек не предлагается для второго create в этой ограниченной fixture; это не обещание поведения production inventory.

`run-83tshysz` — **PASS174** platform Flow.Stardew tests, включая14 новых cases; actual TRX и имена — `artifacts/f13/network-scoped-audit.json`. Проверены whole/selected quantity, точная reservation capacity, восемь invalid captures без мутации, disconnected route, reentry из revision observer, delivery rejection/retry и paused/closed owner. Timeline теста исправлен по существующему контракту: departure1, arrival9, retry10; исходные два FAIL сохранены, доменный алгоритм не менялся. G `run-okzctzow` — **PASS2056 .NET +388 Python** (10 actual TRX). После него найдены и исправлены inactive-owner guards перед PrepareNetwork/AcceptNetworkDelivery; `run-y5l7rw6h` — **PASS180**, в том числе все20 новых cases (шесть дополнительных Paused/RecoveryRequired/Closed regressions), actual evidence — `artifacts/f13/network-platform-final-audit.json`. Финальный C `run-819ojiny` — **PASS1713 .NET +388 Python**; семь actual TRX проверены (`artifacts/f13/network-c-final-audit.json`). Native create ещё не запущен и F13 остаётся IN_PROGRESS.

Следующая native интеграция — отдельный `flow.ui.actions`: exact manifest и Flow report routing, отдельный driver через ModEntry lifecycle, существующие NetworkExperience/ParcelSurface и ShowParcel, отдельные Network/Parcel observations. F12 fixed15 checks/19 observations сохраняются. ActionAutomation обеспечивает normalized Tab/Enter для root action buttons; сама по себе она не предоставляет selection/OS input. UI owner подтвердил отсутствие публичного normalized selection facet: source/destination/slot допустимы как явные synthetic fixture preconditions, а Send проверяется через owning Activate. Настройку synthetic selections нельзя выдавать за физический ввод. Actual create/dispatch/cancel/retry и их rendered results должны быть приняты свежим native run, а не существованием fixture или unit PASS.

## Общая приёмка F13 на 4a7d710

Source `4a7d710128223a62642a6cea3b88a6b7bf923dc0` объединяет final Flow action driver, completed-frame/profile barrier и consumer semantic Reveal поверх общего UI. Public production API и доменная модель сохранности не менялись. **F13 IN_PROGRESS**: успешные действия не закрывают оставшуюся scale/input/lifecycle матрицу.

| Проверка | Evidence в общей рабочей копии | Результат |
|---|---|---|
| Flow Stardew scoped | `run-bqhhulbs`, `f13-common-scoped-audit.json` | PASS187 |
| C | `run-7rn7s6co`, `combined-4a7d710-run-7rn7s6co-audit.json` | PASS1678 .NET +390 Python +13 metadata;7 actual TRX |
| G | `run-e3dyuog1`, `combined-4a7d710-run-e3dyuog1-audit.json` | PASS2048 .NET +390 Python +13 metadata;10 actual TRX |
| P с `--keep` | `hatifect-ui-ca-isolated.d_ylfwdr`, `p-combined-4a7d710-hatifect-ui-ca-isolated.d_ylfwdr-audit.json` | PASS101;8 exact-source packages;47 projection files;2 CA DLL;1429 файлов сохранены |
| Изолированная подготовка | `run-ej_oeck7`, `prepare-4a7d710-audit.json` | PASS14 runtime DLL;8 UI DLL совпадают с пакетами |
| `flow.ui.actions` | request `209bd59f-341d-4a35-8241-0ea04bee6619`, `flow.ui.actions-4a7d710-audit.json` | PASS11;game exit0, cleanup и восстановление settings подтверждены |
| Просмотр actions PNG | `flow-ui-actions-4a7d710-visual-review.json` | Все13 composed PNG просмотрены; Parcel states и оба Network Result видны |
| `flow.ui.isolation` | request `ed43e616-7fef-4150-a6ea-b73c80791627`, `flow-ui-isolation-4a7d710-failure.json` | FAIL на Scale75: current Flow view retired prematurely; кадр75 не получен |

Audit-файлы находятся в `artifacts/f13-root-common/`, исходные runtime reports — в `artifacts/runtime/<request>/`; полные копии двух запросов сохранены в `artifacts/f13-root-common/native-4a7d710/`. C/G/P сверены с665 frozen tracked files. Flow fingerprint: `6d864b752b3b966cc2d3424c61ef6d5b375302c434f52e83ce48168241f23768`; отдельная UI identity: `f6b7c127bfa10348bc86dd14170d414e40e87b3d68ca9fa14435a170a0201a60`. Flow fingerprint самостоятельно не идентифицирует UI DLL; их соответствие проверено отдельно.

В свежих `network-created` и `no-route-result` после Reveal полностью видны «Shipment created» и «No route connects these stations». Верхняя часть формы при этом прокручена за viewport. Это подтверждает видимость двух результатов, но не физическое управление всей формой. Девять Parcel captures показывают состояния, результаты команд и доступность действий. Driver использует synthetic selections и normalized action input; реальное перемещение предметов между сундуками, обычный игровой вход и физический ввод этим не проверены.

Isolation завершился после176 кадров и пяти captures до смены масштаба: empty-en/empty-ru/missing-ru/cargo-ru/cargo-en. На Scale75 Observe обнаружил retired view (`FlowUiAcceptance.cs`, Observe/Tick). Game exit0, teardown errors отсутствуют; lifecycle сообщает settingsRestored=true, но restorationFrames пуст. Успешное восстановление файлов настроек не доказывает завершённую native restoration matrix. Причина retirement передана владельцам Flow/UI; assertion не ослабляется. Следующий шаг — owning correction и новая полная isolation matrix, затем оставшаяся F13 acceptance.

История проверок сохранена: первый G `run-2m8uuqlt` остановлен sandbox при создании VSTest TCP listener, тесты CA не исполнялись; разрешённый повтор прошёл. Первый P `m62m13f8` завершился командным PASS101, но без `--keep` временные evidence были удалены; файловая приёмка основана на повторном retained run. `live-prepare` штатно пропускает собственный повтор тестов и не создаёт eligible release candidate: его временный архив не является готовой поставкой M3/Q02. Впоследствии source опубликован в истории `9341e57`; CI run `34235070095` прошёл10/10 checks.

## Общая приёмка isolation на 866b1d0

Source `866b1d0f482a3216f1cdd92a3ea35245f71eb3af` меняет только `FlowUiAcceptance.cs`: diagnostic cover сохраняет native owner при смене масштаба. Production retirement guards, публичные API и persistence не изменены. Исторический FAIL выше сохранён; **F13 IN_PROGRESS**.

| Проверка | Run / evidence | Результат |
|---|---|---|
| Flow Stardew scoped | `run-t2gzt8cc`, `scoped-866b1d0-audit.json` | PASS187 |
| G | `run-9jnck8fc`, `combined-866b1d0-run-9jnck8fc-audit.json` | PASS2048 .NET +390 Python +13 metadata;10 actual TRX,665 source files |
| P с сохранением файлов | `hatifect-ui-ca-isolated.z2176bmw`, `p-combined-866b1d0-hatifect-ui-ca-isolated.z2176bmw-audit.json` | PASS101;8 packages,47 projection files,4 cache DLL,2 CA DLL;1318 файлов сохранены |
| Runtime prepare | `run-sfc8qj2n`, `prepare-866b1d0-audit.json` | PASS;14 DLL сохранены отдельно |
| `flow.ui.isolation` | `bfa4bf7f-edf3-40aa-8914-a7be24254990`, `flow.ui.isolation-866b1d0-audit.json` | PASS15;exit0, cleanup и settings restoration подтверждены |
| Визуальная проверка | `flow-ui-isolation-866b1d0-visual-review.json` | Просмотрены все19 composed PNG; масштабы75/100/125/150, EN/RU, состояния и переходы миров |

Evidence находится в `artifacts/f13-root-common/`. На75 поверхность целиком в viewport; на крупных масштабах некоторые подписи кнопок сокращены многоточием, полные причины доступности видны ниже. Controller profile не доказывает физический controller input. Actions11 на `4a7d710` не объявляются новым прогоном на866; синхронная reentry-проверка также не доказывает длительно видимое состояние Running.

`package-runtime-identity-866b1d0.json` подтверждает: все14 игровых DLL совпадают с сохранённой prepare-копией, все8 UI package payloads совпадают с соответствующими runtime DLL. После G текущие producer outputs шести Flow/CA DLL отличаются от runtime-копии. Команды G и prepare использовали разные пути dotnet; обе версии DLL содержат revision866. Это не доказательство эквивалентности бинарников: текущие producer DLL нельзя подменять в проверенном комплекте без новой проверки идентичности и соответствующей приёмки.

Следующий шаг — физический ввод и оставшаяся F13 матрица, затем обычный игровой путь F18/F19. Устанавливаемый архив с полной Q02 acceptance ещё не подготовлен. Source866 на момент этого отчёта локальный; публикация и CI для него пока не подтверждены.
