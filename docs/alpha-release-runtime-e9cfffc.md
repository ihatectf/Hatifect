# Проверка альфы в игре — e9cfffc

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Проверено 2026-09-12 только в локальном Git. Source: `e9cfffc36922791c93d26457d3be5719378ecb16`. Это первый runtime checkpoint после локальных «Отправка груза и работа с ошибками» admission-feedback commits `acb1279`, `ff4562d` и `ffe59ff`, а также Window capture-retention candidate `b23f54c`. Он подтверждает сборку и изолированную подготовку нового состава, но не переносит на него прежнюю «Приёмка первой альфы» runtime-матрицу и не завершает «Управление диагностической перевозкой»/«Настройка станций и маршрутов»/«Отправка груза и работа с ошибками»/«Приёмка первой альфы».

## Exact preparation

- Чистый worktree: `/private/tmp/hatifect-flowline-q02-exact`, branch `feature/flowline-q02-exact`, source `e9cfffc36922791c93d26457d3be5719378ecb16`.
- `hatifect-live-prepare` `run-w270gyru`: PASS до первого запуска игры.
- `save.bootstrap` `5a3e51fd-8107-4978-a41b-6f8ccfdacea2`: PASS 1/1; deterministic synthetic save создан, выполнены возврат к title и повторная загрузка. Оба isolated runtime-options файла восстановлены, `errors=[]`.
- Bootstrap изменил release-файлы в isolated Mods, поэтому промежуточный input request `5b4b1f8b-4c2e-45bd-b536-8b23b8089ba3` корректно остановился с `HARNESS-ENV-DEPLOYMENT`. Он не является input evidence.
- Повторный `hatifect-live-prepare` `run-58sg01xm`: PASS. Metadata, восемь UI packages, restore, пять game-reference проверок и Release build прошли; host-free tests на этой packaging-стадии намеренно SKIPPED. Runtime fingerprint `4fa3460c973059d7a8ba2a790c3b05dc3fe98f5a1734dd1cc90b2eae2a7ca580`, candidate fingerprint `2c44c985874bf101348987504b71b1056084f66f22f4cb1a2fbb4053330e4a3f`.

## Exact physical-input request

`flow.ui.player.input` request `b0e6af20-c6c4-4bdf-bf7f-58bd2c9f0092` завершён **BLOCKED** до первого semantic step:

- `repositoryHead` и executor `checkoutSha` совпали с `e9cfffc36922791c93d26457d3be5719378ecb16`;
- `semantic-test-driver.json`: `currentStep=null`, `events=[]`, `CGPreflightPostEventAccess()=false`;
- `flow-player-input-progress.json` не создан, поэтому `clickPoint`, key K, Window phases и action-result captures в этом request не выполнялись;
- outer result: `HARNESS-PROCESS-START`, process exit 130, status BLOCKED;
- working-copy cleanup PASS; оба isolated runtime-options файла восстановлены, `errors=[]`; runtime supervisor остановлен штатно.

Read-only вызов установленного Apple SDK `IOHIDCheckAccess(kIOHIDRequestTypePostEvent)` также вернул `kIOHIDAccessTypeDenied`. Сам SDK требует пользовательского разрешения для `IOHIDPostEvent` и помечает API deprecated; поэтому переход на него не является доступным обходом TCC и без отдельного доказательства доставки клавиатуры не выбран как реализация.

## Evidence boundary

Raw evidence находится локально в exact worktree:

- `artifacts/validation/run-58sg01xm/summary.json`;
- `artifacts/runtime/5a3e51fd-8107-4978-a41b-6f8ccfdacea2/`;
- `artifacts/runtime/b0e6af20-c6c4-4bdf-bf7f-58bd2c9f0092/`.

Следующий необходимый шаг — запустить неизменный exact candidate из процесса, для которого `CGPreflightPostEventAccess()` уже возвращает `true`, и проверить новую последовательность `clickPoint` → key K. Только такой прогон различит гипотезу key-window focus и ранее наблюдавшийся raw-K delivery gap. После его PASS всё ещё нужны текущая общая G/P проверка, EN/RU × 75/100/125/150, controller/physical-input, CA, save/restart/save-switch и исходная chest-матрица на одном финальном составе.
