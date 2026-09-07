# F12: exact admission для Flow UI lifecycle

Статус полного F12 — **IN_PROGRESS**. Shared harness допускает согласованный consumer-owned сценарий `flow.ui.isolation`. Actual Flow driver, read model и UI lifecycle находятся в отдельном FLOWLINE checkpoint; эта регистрация сама по себе не доказывает native acceptance.

## Владение и протокол

Сценарий использует smoke runner, timeout420 seconds, требует Hatifect.UI и Hatifect.Flow и не входит в aggregate. Catalog содержит15 обязательных checks: loaded,empty,missing,publication,locale,scale,controller-profile,unavailable,faulted,close,unsubscribe-retry,save-isolation,retired-handles,restored,read-only. UI built-in controller отклоняет своё участие по точному scenario ID; Flow владеет выполнением и host report. Near-match/suffix IDs не получают этих полномочий.

Companion UUID равен parent UUID XOR1. Canonical names — HatifectHarness<runhex>_4242424242 и HatifectHarness<secondaryhex>_4242424243; роли primary/secondary входят в validate/cleanup authority. Только uniqueID второго owned XML reseeded; primary, остальные bytes и golden сохраняются. При collision чужой путь не приобретается и не удаляется. Launch failure очищает обе уже приобретённые копии, normal completion фиксирует обе роли в save-provisioning evidence.

Существующие HATIFECT_SMAPI_TEST_SECONDARY_SAVE и HATIFECT_TEST_SECONDARY_RUN_ID вычисляются из accepted request; inherited foreign values не принимаются. Request schema и исходный request не меняются. Старый flow.save.isolation сохраняет legacy primary/primary role semantics; prepare_flow_secondary сохраняет exact original defaults. Все прежние scenario objects сохранены.

## Проверки

Первое выполнение7 выбранных cases до implementation завершилось failures5/errors3: scenario отсутствовал, companion не передавался/не очищался, report выбирался из UI. После owning changes эти7cases прошли. Канонический `./tools/hatifect-test tools`, `run-e6l9bkwu` — **PASS379**, без ошибок/пропусков. Независимый source/tooling review — **PASS**, все7 postimages,379 индивидуальных ok и36 прежних scenario objects проверены. Source checkpoint — [`eca000f`](https://github.com/ihatectf/Hatifect/commit/eca000f29b4afa46d2d476540941a19ae612d866). Канонические `./tools/hatifect-check` (`run-o0ebrv52`) и `./tools/hatifect-check --platform` (`run-egubib6c`) — **PASS1538/1812 .NET +379 Python**. Независимый audit прочитал все7/10 TRX и индивидуальные Python outcomes:0 ошибок/пропусков,7/7 source hashes совпадают с immutable manifest. P — NOT_APPLICABLE для этого diff: публичный API, packages и CA consumer не меняются. Actual native — PENDING в combined Flow consumer; не подменяется тестами temporary fixtures.

| Требование | Точное evidence |
|---|---|
| Два canonical owned worlds, неизменный primary/golden и role-bound cleanup | SaveProvisioningTests.test_flow_ui_isolation_derives_owned_world_and_preserves_primary_and_golden |
| Failure/reseed/collision не приобретают чужие файлы | SaveProvisioningTests.test_flow_ui_isolation_reseed_failure_and_collision_release_only_acquired_copy |
| Derived companion environment и неизменный request | DirectRuntimeTests.test_flow_ui_isolation_environment_derives_companion_without_mutating_request |
| Ошибка подготовки сохраняет foreign secondary | DirectRuntimeTests.test_flow_ui_isolation_preparation_collision_preserves_foreign_secondary |
| Очистка обеих ролей при launch failure и completion | DirectRuntimeTests.test_flow_ui_isolation_cleans_both_owned_roles_on_launch_failure_and_success |
| Exact report owner, near-match не получает Flow report | DirectRuntimeTests.test_flow_lifecycle_report_has_fixed_module_ownership |
| Полный набор15, оба модуля,420s; каждый false/missing check отвергается | LiveHarnessTests.test_flow_ui_isolation_requires_exact_lifecycle_checks_and_both_mods |

Static pairing выполнен один раз на tools:38 Python files,16 source/22 test,0 paired и0 parser errors. Это naming heuristic, не line/branch coverage; исходники/test conventions исследованы до добавления cases. Связанные старые UI tests сохранены с теми же assertions через общие private fixture helpers.

Следующий шаг — pin этого immutable admission source в FLOWLINE, canonical prepare и fresh flow.ui.isolation, затем независимый audit фактических19observations/3loads/2titles и GQ integration. Versions принадлежат GQ; UI наблюдение df09a93 остаётся отдельным prerequisite; integration review обнаружил O(N) portal snapshot при получении числа порталов, follow-up исправляется отдельно до окончательной общей приёмки. FLOWLINE уже подключил admission в eb7929f; первый native выявил несоответствие запрошенных native settings до проверки текстов. Это не PASS полного F12; consumer владеет диагностикой/исправлением, UI проверяет независимо найденный presentation gap заголовков и подписей.

GQ включил exact admission в локальный alpha.47 candidate `1725dc684be82d7ec80b3b05c505ab8bfe8b12a1`; общий `./tools/hatifect-test tools`, `run-39pndd1i` — **PASS380**, без ошибок/пропусков. Сохранены все прежние scenario objects и Flow names tests; observation API и actual Flow driver принимаются отдельно после owning corrections. На этом immutable source `./tools/hatifect-check`, `run-ln70m4xo` — **PASS1584 .NET +380 Python**, `./tools/hatifect-check --platform`, `run-bqfypci7` — **PASS1876 .NET +380 Python**. Оба процесса завершились с exit0; GQ проверил 94 current/Git source hashes, все 17 TRX и исходные Python logs. Evidence: `artifacts/alpha47-flow-admission-preflight/c-audit.json`, `g-audit.json`. Эти результаты подтверждают общий admission checkpoint; actual Flow UI driver и его свежий native остаются отдельным обязательным этапом.
