# Приёмка экрана Flowline

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Исходный «Просмотр состояния Flowline» — **DONE** на общем source `08ecdb94102fb7311c1fe21eabc9843f7e4888ef`. Зависимости «Данные и команды Flowline»/«Согласованное обновление данных» выполнены ранее; [«Адаптация интерфейса к окружению» owner-приёмка](ui-environment-acceptance.md) подтверждена независимым review. Общая публикация alpha.47, «Действия и их завершение» и «Проверка текущей сборки в игре» остаются отдельной незавершённой работой. Проверки ниже относятся к source08, а не к последующим документационным коммитам.

## Соответствие исходному «Просмотр состояния Flowline»

| Требование | Реализация и выполненная проверка |
|---|---|
| Snapshot projection вместо прямого наблюдения mutable Parcel | `ParcelExperience` подписывается до initial read, читает `IFlowApplication.ReadSnapshot` и атомарно публикует backing snapshot с пятью значениями. Общие C/G включают passing publication, subscription, locale и retained-source cases. |
| Конкретная точка открытия и поддержанный UI host | Команда `hatifect_flow show` вызывает общий `ShowParcel`; `ParcelSurface` создаёт настоящий active-menu overlay поверх открытого native menu. Driver вызывает тот же путь и сохраняет opaque handles настоящего UI provider. |
| Обновление после доменного изменения | Native driver выполняет `Reserve` через `FlowApplication`; `updated-ru` показывает новое состояние. Publication увеличивается ровно на1, effect attempt единственный, identities sources/actions сохранены. |
| Empty, unavailable и faulted | Captures содержат empty EN/RU, missing, paused, recovery, resumed и faulted. Отказ executor проходит через application; cargo/route очищены, причины и availability всех пяти действий проверены. |
| Диагностическая маркировка fake source | Локализованный diagnostic title поступает из application snapshot и проверяется в accepted elements и submitted text; он виден на просмотренных изображениях. |
| Представительные окружения | Одна Experience проходит EN↔RU, scales75/100/125/150 и controller profile с фактически применёнными environment values. Эти переходы не перепубликуют доменную модель. Native names отдельно проверяет настоящие EN/RU имена Copper Ore. |
| Close/reopen и A → B → A | Три loads, два title transitions, четыре application owners и шесть surfaces. A показывает7 единиц, B —13, повторный A —7 с новой session identity. |
| Нет stale data и утечек подписок | Старые publications/values неизменны, old actions не выполняют reads/commands, rendering прекращается, UI snapshots очищены. У всех четырёх owners0 subscribers. Проверены unsubscribe failure/retry и ещё120 стабильных terminal frames. |
| Semantic DLL, UI dependency и один поставщик UI | Release inventory включает Flow Semantic и UI dependency alpha.47. В изолированной поставке ровно8 UI DLL, все внутри UI module, копий у Flow/CA нет. Все14 runtime DLL совпадают с producer; все8 UI package DLL совпадают с producer/game. |

## Общая проверка

Команды сборки используют настроенный x64 SDK8/.NET6 и command-local `DOTNET_gcConcurrent=0`; игровые процессы работают с минимальным окружением канонического harness. Нормальные Mods и реальные сейвы не затронуты.

| Gate / команда | Actual evidence | Результат |
|---|---|---|
| C/F/U: `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | `run-gi9nzm7d`,7 TRX | PASS1604 .NET +381 Python; Flow и UI suites действительно выполнены |
| G: та же команда с `--platform` | `run-qdvu9v4z`,10 TRX | PASS1913 .NET +381 Python |
| P: `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | retained `isolated-p` | PASS90,44 projection files,8 exact alpha.47 packages,3 project.assets.json,2 CA deployment DLL |
| `rtk proxy ./tools/hatifect-smoke flow.ui.isolation` | `1c8eadec-5693-453e-8462-4f5332165a59` | PASS15,19 completed observations |
| `rtk proxy ./tools/hatifect-smoke flow.ui.names` | `5c2037d8-30b9-4cc9-af0b-7fe7b35642ef` | PASS7 |
| `rtk proxy ./tools/hatifect-smoke flow.route.basic` | `3d51e021-1071-4b5b-aa97-fa644f34a8e7` | PASS6; дополнительная fake-route regression |
| VISUAL | Все19 composed PNG из общего Flow UI request просмотрены GQ | PASS: title, пять labels, значения и ASCII `->` видимы, clipping панели не обнаружен |

Независимый GQ reviewer повторно проверил индивидуальные outcomes/counters всех17 TRX, оба Python footer381/OK, P90 и raw reports трёх requests. Failed/skipped test outcomes отсутствуют. Совпали98 native artifact hashes:56 UI,18 names,24 basic. Review исходного «Просмотр состояния Flowline» в Git source08 подтвердил, что его dependency/gate scope — «Данные и команды Flowline»/«Согласованное обновление данных»/«Адаптация интерфейса к окружению» и C/F/U/G/P/RUNTIME; дополнительные требования «Действия и их завершение»/«Проверка текущей сборки в игре» в «Просмотр состояния Flowline» не переносились.

Все три native requests имеют fingerprint `c20a53e718454384d6b45051c7919862fb5da184a50d99a0d8e32fab4eab74e8` (`sha256-flow-runtime-v1`). Source/request/result/transport identity, временные границы и owned PID/PGID согласованы; процессы exit0, teardown errors пусты. Две исходно существовавшие options files восстановлены побайтно, request-owned saves удалены, golden checksum `4eb16675dcf00f46c02403a379bd95cd512c1bf3bc1dd92c1c00d9d83d5627d8` и manifest сохранены.

Raw находятся в `artifacts/runtime/<request-id>`. Audits и retention находятся в `artifacts/alpha47-glyph-integration-preflight/`: `c-audit.json`, `g-audit.json`, `p-audit.json`, `p-retention.json`, `prepare-audit.json`, `flow-ui-native-audit.json`, `flow-ui-visual-review.json`, `flow-smokes-audit.json` и `f12-independent-closure-review.json`. Сохранённые117 postimages проверены против исходного Git commit08; после него в root изменялась только документация. [Общая приёмка alpha.47](ui-flow-alpha47-integration.md) сохраняет прежние failures и полный оставшийся объём.

## Границы и следующий шаг

Это read-only diagnostic experience над fake source. Driver изменяет домен для проверки обновлений; доступные продуктовые transport-команды, непустой command Result, running/rejection messages и повторные пользовательские действия относятся к «Действия и их завершение»/«Управление диагностической перевозкой». Physical controller input и Backspace не проверены этой матрицей. Production chest/save-write acceptance и полный Flow frame PERF не заявлены.

Прежнее ожидание common integration в owner `FLOW_UI_LIFECYCLE.md` на `1b994b9` снято выполненной общей «Просмотр состояния Flowline»-приёмкой. Следующий зависимый consumer ID — «Управление диагностической перевозкой» после завершения «Действия и их завершение» messages/typed actions. Общая alpha.47 ещё требует оставшуюся runtime/PERF/input матрицу, окончательную интеграцию, публикацию и exact CI; закрытие «Просмотр состояния Flowline» их не подменяет.
