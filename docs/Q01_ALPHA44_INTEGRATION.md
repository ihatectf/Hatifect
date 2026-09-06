# Общая интеграция alpha.44

Статус общей приёмки — **IN_PROGRESS**. Локальная интеграция [`e954c6d`](https://github.com/ihatectf/Hatifect/commit/e954c6db21e26e1a9546f3bd23fe77c1fa25f329) объединяет immutable UI checkpoint [`e3c9dbf`](https://github.com/ihatectf/Hatifect/commit/e3c9dbff40a455ea97d18a52801034e3d0b7b1dc), implementation `ed44663`, с опубликованной общей базой `f035f4c`. Последующий `4f9b388` включает нативный test-only checkpoint `0e6f6358`: explicit Open/Follow прошёл PASS8. Общая визуальная приёмка обнаружила повторное включение фоновой паузы после загрузки сейва; причина подтверждена отдельным diagnostic-only `b381eae`, исправление ещё не выполнено. Интеграция не опубликована, полные U03/Q01 остаются IN_PROGRESS.

## Исходники и границы

Все 22 non-Markdown postimages совпадают с переданным checkpoint. Четыре production changes принадлежат Runtime: подготовка нового dispatcher generation, принятие scene/owner metadata перед cancellation и retirement порталов прежнего поколения. Девять новых behavioral cases проверяют same-Cached/other/route transitions, rejected candidate, обычную recomposition, закрытие из cancellation, suppression готовых siblings и остановку старой очереди. Все 17 version/package/consumer authorities согласованы на alpha.44. Публичные Experience/surface signatures и Flow persistence не меняются; U04 и active reload WIP в этот merge не входят.

Связанные Shell Commit / dispatcher / execution contracts проверены отдельно: metadata commit не вызывает пользовательский код, retirement сначала блокирует dispatch и очищает observer/request state, затем вызывает cancellation с учётом ошибок. Bounded source review GQ не оставил открытых замечаний в этом срезе. Единственный merge conflict был между добавлениями в `ROADMAP-STATUS.md`; оба раздела сохранены. Ошибочный selector `ui-runtime` в owner report заменён фактическим `ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'` после проверки run-tpgza_ok.

Ancestry включает f035f4c/3043c67/e3c9dbf/ed44663/32c936b/53e85ff/a7be299. Все четыре postimages исправления process timestamp `3043c67` сохранены. Его опубликованный exact CI `34049870307` на `f035f4c` завершён SUCCESS во всех 10 jobs; прежний failed run `34048617203` остаётся в [отчёте alpha.43](Q01_ALPHA43_INTEGRATION.md).

## Проверка переданного evidence

`artifacts/alpha44-integration-preflight/owner-final-audit.json` получен повторным чтением фактических файлов:

- 22 source hashes равны immutable Git blobs и обеим сохранённым source maps.
- Owner C `run-1s3sgwvd`: PASS1466 .NET +365 Python; G `run-1h63c5rf`: PASS1707 .NET +365 Python. Все 17 TRX имеют согласованные counters, каждый результат Passed, failures/skips отсутствуют.
- Retained P `hatifect-ui-ca-isolated.vukebou9`: 44 projected files равны Git checkpoint; восемь package archives/DLL равны сохранённым owner hashes; все 90 CA results Passed; три assets files используют только isolated cache; две deployed CA DLL равны projected producer outputs, лишних UI DLL и UI source нет.

Owner UI producer outputs уже изменены последующей работой над reload. Их прежнее равенство пакетам сохраняется как исторический owner audit; GQ не объявляет их текущими producer outputs alpha.44. Собственная пакетная проверка интеграции ниже заново сравнила каждый package DLL с соответствующим producer output на момент проверки.

## Собственные проверки интеграции

Команды выполняются из `${HOME}/Developer/Worktrees/Codex/345f/Hatifect` на хосте с x64 SDK8/.NET6 и command-local `DOTNET_gcConcurrent=0`. Runtime этот workaround не наследует. Артефакты находятся в `artifacts/alpha44-integration-preflight/`.

| Проверка | Фактический результат |
|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | `run-m57db06u`, **PASS1466 .NET +366 Python**; семь actual TRX audited, Runtime444, failures/skips0; `c-audit.json` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | `run-ik_madoq`, **PASS1707 .NET +366 Python**; десять actual TRX audited, Runtime444, Stardew18, CA90; `g-audit.json` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | **PASS90**, retained `hatifect-ui-ca-isolated.ooc7yrq9`; 44 exact projected files, восемь package DLL равны producer outputs e954c6d, три isolated assets files и две точные deployed CA DLL; `p-audit.json` |
| Native explicit Terminal Open/Follow | На последующем `4f9b388` — PASS8; точная поставка и дополнительные проверки ниже |

`source.json` связывает merge parents, 22 source postimages и сохранённое исправление harness. `preliminary-review.json` содержит границы исходного и connected source review. C/G относятся к production baseline e954c6d; последующее добавление native fixture получает собственную source identity и необходимые проверки.

## Native checkpoint и повторная проверка поставки

Merge [`4f9b388`](https://github.com/ihatectf/Hatifect/commit/4f9b388fd5602a2ece10fdda6ea6af7864c17fa8) принял ровно три source postimages `0e6f6358b7a7da95a8d66b3598f696219511ad7d`, parent `4f9b18c` поверх e3c9dbf. Полный source/staged diff проверен. Idle sibling доступен до перехода, затем отвергается до capture/execute и в cancellation, и после перехода; это отделяет retirement от busy policy. Производственный код и версии не менялись. Owner Kepler сообщил закрытие замечания по исходникам; фактический native запуск выполнил GQ.

На этом commit `hatifect-check --platform` — **PASS1707 .NET +366 Python**, `run-6x8z89bn`, десять actual TRX audited. Повторный `hatifect-isolated-ui-ca --keep` — **PASS90**, retained `hatifect-ui-ca-isolated.xtrbljni`: 44 exact projected files, восемь package DLL равны producer outputs этой сборки, три isolated assets и две точные deployed CA DLL. `hatifect-live-prepare`, `run-h62059vp`, подготовил те же восемь DLL для игры. Source, G, P и deployment связаны в `native-source.json`, `g-final-audit.json`, `p-final-audit.json`, `delivery-audit.json`.

Канонический `hatifect-live-runner` вызван через `artifacts/alpha44-integration-preflight/run-runtime.py` с отдельными request IDs; command-local GC workaround использовался только при сборке. Среда: Stardew1.6.15 build24356, SMAPI4.5.2, UI alpha.44, Flow rc.89, CA1.30.1.

| Сценарий | Request ID | Результат |
|---|---|---|
| `ui semantic.actions.terminal` | `e0781d1e-039c-46b6-a785-51f122f089f0` | **PASS8**,11.103s; 4 перехода и 12 операций: восемь callbacks на owning thread и четыре late faults GetResult1/callback0; четыре idle siblings с capture0 |
| `ui semantic.input.retired-overlay` | `e1aa2d19-77bf-4449-9b06-84a264be3122` | **PASS2**,10.852s; keyboard ownership и retry сохранены |
| `ui semantic.actions.pump` | `0efc8a75-f241-491b-ad02-9c4981624aea` | **PASS4**,9.952s; native owning Update и retirement |
| `smoke flow.route.basic` | `3eb9864a-fe08-4d78-901d-eb52e5c6f883` | **PASS6** |
| `smoke flow.save.isolation` | `0b44638f-a2f1-4978-8f46-baa59b27494d` | **PASS7** |
| `ui all` | `a3d96a64-b156-4657-92fc-8d8f4d1ce5c8` | **FAIL**,21.147s; 13 ранних host checks Passed,14 remaining checks Failed; первый кадр visual matrix не прошёл LoadFadeFinished |

Первые пять запросов проверены по полным raw reports, diagnostics, process records и options evidence в `runtime-baseline-audit.json`; это **PARTIAL_PASS**, а не завершённая общая приёмка. Все завершились exit0 без teardown errors и восстановили исходные config bytes/modes. UI fingerprint — `84e64ba451641b557af81b821ece575140071377987cda9209ef9dab7a32584e`, Flow — `533fbf3016c2bdfab79bf4de74bbc9fa1167955b79155f7982d1bcff1a45d515`. Эти fingerprints относятся к 4f9b388 и не переносятся на последующие DLL.

## FAIL: настройка фоновой паузы из сейва

Первый aggregate сохранил валидное дерево, probe text, согласованные EN/scale0.75 и геометрию1280×720, но `fadeToBlackAlpha=1.0204`, `LoadFadeFinished=false`, captures0. `aggregate-failure-audit.json` сохраняет исходный FAIL и hashes. Config lease действительно применил false и восстановил оба файла, owned working copy удалена, процесс38458 завершился0. `visualMatrixRestored=false` в runtime diagnostics не выдаётся за восстановление живого визуального состояния.

Diagnostic-only [`b381eae`](https://github.com/ihatectf/Hatifect/commit/b381eaed82be5a3b54fb327b024a6cdc07955e4d) добавляет шесть scalar observations в `WriteDiagnostics`, не меняя поведение, criteria или версии. Scoped `hatifect-test ui --platform --project 'Hatifect UI/tests/Hatifect.UI.Stardew.Tests/Hatifect.UI.Stardew.Tests.csproj'` — **PASS18**, `run-vs0z0pix`; первый selector без `--platform`, `run-1ucyqyll`, корректно завершился FAIL без runnable projects и не считается проверкой кода. Затем prepare `run-aupd9qx2` и probe `1d4f9506-c438-49fc-b4a4-6dc5d25cd445` — **FAIL14**,20.221s, UI fingerprint `d3daed3390702242ee5b7fd3a3859c1d2305ed9075116fd07f6bd55e76fb3f52`.

Probe подтвердил фактические значения из памяти игры: `gamePaused=false`, `gameIsActiveNoOverlay=false`, `pauseWhenOutOfFocus=true`, `gameMode=3`, `worldReady=true`, `multiplayerMode=0`, fade1.0204. Код текущего game assembly подтверждает цепочку: `SaveGame` заменяет `Game1.options` на `loaded.options` и пишет defaults; последующее чтение startup preferences восстанавливает только gamepad mode. В golden save сохранён pause=true. При такой комбинации игра возвращается из Update до обновления fade, хотя Draw продолжает выполняться. Первоначальная файловая policy недостаточна после загрузки. Данные, source locations и hashes неизменённого golden сохранены в `fade-probe-audit.json`.

Следующий шаг GQ — исправить общую подготовку фонового runtime после загрузки, сохранив golden, owned save semantics и действующие rendered-state критерии; затем выполнить финальные проверки изменённой поставки, отдельный PERF и просмотр восьми кадров. До этого публикация alpha.44 и DONE не выполняются. Alpha.45 согласован как frozen U04 foundation/capture f4b7386 без host wiring/reload/text projection; все17 version authorities обновляет только GQ. UI ведёт отдельный alpha.46. Полные U04, U03/Q01, concrete typed consumer и физический Backspace остаются незавершёнными.
