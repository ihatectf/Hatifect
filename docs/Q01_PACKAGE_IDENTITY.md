# Q01: идентичность DLL в локальном UI feed

Дата: 2026-09-06. Владелец: GQ, общие build/package tools. Исходная база — `945040a`; UI остаётся alpha.42. Это ограниченное исправление ложного PASS в пакетной проверке, найденного при [интеграции alpha.42](Q01_ALPHA42_INTEGRATION.md). Причина исторической смены metadata ещё не установлена; полные U03/Q01 остаются IN_PROGRESS.

Implementation: [`c36a0d1`](https://github.com/ihatectf/Hatifect/commit/c36a0d133d847f2a9a8bd25fe1478d13963e1c5a), только verifier и его regression tests. Ограниченное исправление verifier — **DONE**. C выполнен на точных postimages этих двух файлов поверх `945040a`; P выполнен после commit. SHA-256 исходников и семи C TRX сохранены в `c-audit.json`.

## Подтверждённый дефект

`hatifect-pack-ui` упаковывает проекты в топологическом порядке. Каждый `dotnet pack` может снова собрать его ProjectReference. Прежний `ui_packages.py verify-feed` проверял точные package IDs, версии, зависимости, состав и отсутствие пути репозитория, но не сравнивал DLL пакетов с итоговыми producer outputs. Уже упакованная зависимость могла измениться при последующей сборке, а завершённый feed всё равно получал PASS.

Так произошло в первом owner P на source `9b8f5e6`, сохранённом в `hatifect-ui-ca-isolated.8spb85hh`. Все90 CA tests прошли; отдельный audit выявил три несовпадения из восьми:

| DLL | Metadata в пакете | Metadata итогового producer |
|---|---|---|
| Language | `1.0.0-alpha.42+9b8f5e6beea9691c8d8644230a4075c8b57c4e24` | `1.0.0-alpha.42` |
| Semantics | та же версия с revision | `1.0.0-alpha.42` |
| Experience | та же версия с revision | `1.0.0-alpha.42` |

У остальных пяти пакетов байты совпали с итоговыми producer DLL. Повтор `hatifect-ui-ca-isolated.lndyn8g4` сохранил согласованные восемь DLL. Первый смешанный набор не был принят как итоговая поставка.

Время первого P — 14:21:42 UTC. Сохранённые C/G закончились до14:20:53; reflog UI worktree сохраняет `9b8f5e6` с14:14:52 до14:27:53. Пересечение с этими C/G и изменение HEAD в момент упаковки не подтверждаются. Первые три пакета содержат DLL с ZIP timestamps14:15:54–14:16:02; их итоговые producer timestamps14:21:51–14:21:56 соответствуют упаковке Planning. По этим данным нельзя установить, почему Git metadata изменилась, или исключить каждый посторонний процесс.

Новый диагностический pack Language → Planning в GQ worktree сохранил одинаковые packaged/producer bytes и revision `945040a`. Это отрицательный результат попытки воспроизведения смены metadata, а не доказательство устранения её причины.

## Исправление проверки

После упаковки всего выбранного графа verifier обязательно сравнивает DLL каждого пакета с `bin/Release/net6.0/<Id>.dll` owning project. Отсутствующий producer или любое различие байтов приводит к FAIL; сообщение о расхождении содержит обе SHA-256. Проверка ничего не заменяет и не пересобирает при отказе. Host-free mode по-прежнему проверяет только семь выбранных библиотек; полный режим требует все восемь.

Публичные C# API, package version, dependency graph, compiler profile, source-revision policy, persistence и runtime-код не менялись. Условия проверки содержимого пакетов сохранены. Сравнение выполняется однократно на пакет вне игровых update/draw/layout/dispatch.

## Проверки

| Требование | Evidence |
|---|---|
| Изменённая после упаковки DLL отклоняется; оба артефакта сохраняются | `test_feed_rejects_dll_changed_after_its_package_was_created`: одинаковый размер, разные байты; до fix FAIL, после PASS |
| Полный набор пакетов без итогового producer не получает PASS | `test_feed_rejects_missing_producer_even_with_complete_packages`: до fix FAIL, после PASS |
| Совпадающие DLL проходят в полном и host-free режимах, состав остаётся строгим | `test_partial_feed_requires_explicit_mode_and_rejects_extra_stardew`: PASS |
| Пакетные regression tests | `rtk proxy python3 -m unittest discover -s tools/tests -p test_ui_packages.py -v`: PASS7; до fix FAIL2/7 |
| Canonical tooling tests | `rtk proxy ./tools/hatifect-test tools`: PASS365, zero failures/skips, `run-wvh66k1h` |
| Canonical C | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` на хосте: PASS1 431 .NET +365 Python, `run-bx1yb1kh`; все7 TRX counters и individual Passed outcomes сверены, zero failures/skips |
| Fresh P | `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` на хосте: PASS90, workspace `hatifect-ui-ca-isolated.5mec2l0l`;44 byte-matched projected files,8 producer-matched DLLs,3 assets files с одним изолированным cache,2 точных CA DLL |

Сохранённые реальные пакеты также проверены прежним и новым verifier. Во временном каталоге producer DLL восстановлены из повторного feed; SHA-256 всех восьми совпали с записанными producer hashes первого audit. Прежний verifier принял первый смешанный feed, новый отклонил Experience (`ad2c2087…` против `4689be26…`), согласованный повтор прошёл. Исходные worktree/feeds при этом не менялись. Это replay сохранённых DLL, а не новый запуск исторической сборки.

Финальный P проверен по actual TRX90/90, всем восьми DLL и всем44 projection inputs. Первая версия дополнительного audit ошибочно требовала завершающий `/` у packageFolders; реальные assets содержат тот же абсолютный путь без `/`. Исправлен только этот ad hoc audit: он сравнивает resolved paths и требует ровно один изолированный cache. Canonical P и его условия не менялись.

Первый C `run-mxjkwm1n` сохранил FAIL: Runtime408/409, `LiveSemanticAssetsTests.FileWatchLoadsReplacementRetainsInvalidLastGoodAndStopsOnDispose` не получил событие после замены файла за bounded wait. Exact filtered `--no-build --no-restore` повтор внутри sandbox также FAIL1/1. Тот же тест без пересборки на хосте — PASS1/1. Исходники watcher, теста и его assertions не менялись. Эти наблюдения различают результаты двух режимов запуска, но не являются полной диагностикой доставки FSEvents.

Отдельные G, RUNTIME, VISUAL и PERF для этого Python verifier change — NOT_APPLICABLE: игровой адаптер, граф, UI поведение и runtime harness не изменялись. P проверяет фактическую полную восьмипакетную поставку и CA. Игровое evidence alpha.42 остаётся привязано к исходным fingerprints; новый игровой PASS не заявляется.

## Локальные артефакты и продолжение

Worktree: `${HOME}/Developer/Worktrees/hatifect/345f/Hatifect`. Артефакты в `artifacts/package-metadata-drift/`:

- `regression-before.log`, `regression-after.log`, `tools.log` — red/green и полный tooling gate;
- `historical-replay.json` — все восемь полных hashes и результаты двух verifiers;
- `diagnostic-pack-audit.json`, `language-pack.binlog`, `planning-pack.binlog` — короткий pack с сохранённой metadata;
- `check.log`, `file-watch-recheck.log`, `file-watch-host-recheck.log`, `check-host.log` — отдельные режимы C и file-watch observation;
- `c-audit.json`, `isolated-ui-ca.log`, `p-audit.json` — окончательные C/P, source identities и полный byte/TRX audit;
- `g-replay.log` — SDK replay сохранённого owner G binlog, где `SourceRevisionId` содержит `9b8f5e6`.

Исходные owner evidence находятся в `/private/tmp/hatifect-ui-next-ten-slices/artifacts/u03-production-binding/`: `package-mismatch.json`, `package-informational-versions.json`, `final-p.log`, `final-p-audit.json`. Сохранённые feeds `8spb85hh` и `lndyn8g4` расположены в системном временном каталоге и атрибутированы отдельно от нового GQ P.

Следующий общий шаг — приёмка проверенного U03/alpha.43 от задачи UI и завершение физического Backspace при доступности пользователя. При повторении смены metadata verifier остановит feed; дальнейшая диагностика должна сохранить pack-time MSBuild properties и исходные артефакты, без автоматической перепаковки ради PASS.
