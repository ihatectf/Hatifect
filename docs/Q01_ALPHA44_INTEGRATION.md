# Общая интеграция alpha.44

Статус общей приёмки — **IN_PROGRESS**. Локальная интеграция [`e954c6d`](https://github.com/ihatectf/Hatifect/commit/e954c6db21e26e1a9546f3bd23fe77c1fa25f329) объединяет immutable UI checkpoint [`e3c9dbf`](https://github.com/ihatectf/Hatifect/commit/e3c9dbff40a455ea97d18a52801034e3d0b7b1dc), implementation `ed44663`, с опубликованной общей базой `f035f4c`. Полные U03/Q01 остаются IN_PROGRESS. Новая интеграция ещё не опубликована; нативная приёмка explicit Open/Follow обязательна до её завершения.

## Исходники и границы

Все 22 non-Markdown postimages совпадают с переданным checkpoint. Четыре production changes принадлежат Runtime: подготовка нового dispatcher generation, принятие scene/owner metadata перед cancellation и retirement порталов прежнего поколения. Девять новых behavioral cases проверяют same-Cached/other/route transitions, rejected candidate, обычную recomposition, закрытие из cancellation, suppression готовых siblings и остановку старой очереди. Все 17 version/package/consumer authorities согласованы на alpha.44. Публичные Experience/surface signatures и Flow persistence не меняются; U04 и active reload WIP в этот merge не входят.

Связанные Shell Commit / dispatcher / execution contracts проверены отдельно: metadata commit не вызывает пользовательский код, retirement сначала блокирует dispatch и очищает observer/request state, затем вызывает cancellation с учётом ошибок. Bounded source review GQ не оставил открытых замечаний в этом срезе. Единственный merge conflict был между добавлениями в `ROADMAP-STATUS.md`; оба раздела сохранены. Ошибочный selector `ui-runtime` в owner report заменён фактическим `ui --project 'Hatifect UI/tests/Hatifect.UI.Runtime.Tests/Hatifect.UI.Runtime.Tests.csproj'` после проверки run-tpgza_ok.

Ancestry включает f035f4c/3043c67/e3c9dbf/ed44663/32c936b/53e85ff/a7be299. Все четыре postimages исправления process timestamp `3043c67` сохранены. Его опубликованный exact CI `34049870307` на `f035f4c` завершён SUCCESS во всех 10 jobs; прежний failed run `34048617203` остаётся в [отчёте alpha.43](Q01_ALPHA43_INTEGRATION.md).

## Проверка переданного evidence

`artifacts/alpha44-integration-preflight/owner-final-audit.json` получен повторным чтением фактических файлов:

- 22 source hashes равны immutable Git blobs и обеим сохранённым source maps.
- Owner C `run-1s3sgwvd`: PASS1466 .NET +365 Python; G `run-1h63c5rf`: PASS1707 .NET +365 Python. Все 17 TRX имеют согласованные counters, каждый результат Passed, failures/skips отсутствуют.
- Retained P `hatifect-ui-ca-isolated.vukebou9`: 44 projected files равны Git checkpoint; восемь package archives/DLL равны сохранённым owner hashes; все 90 CA results Passed; три assets files используют только isolated cache; две deployed CA DLL равны projected producer outputs, лишних UI DLL и UI source нет.

Owner UI producer outputs уже изменены последующей работой над reload. Их прежнее равенство пакетам сохраняется как исторический owner audit; GQ не объявляет их текущими producer outputs alpha.44. Собственная пакетная проверка интеграции ниже заново сравнила каждый package DLL с текущим producer output.

## Собственные проверки интеграции

Команды выполняются из `${HOME}/Developer/Worktrees/Codex/345f/Hatifect` на хосте с x64 SDK8/.NET6 и command-local `DOTNET_gcConcurrent=0`. Runtime этот workaround не наследует. Артефакты находятся в `artifacts/alpha44-integration-preflight/`.

| Проверка | Фактический результат |
|---|---|
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check` | `run-m57db06u`, **PASS1466 .NET +366 Python**; семь actual TRX audited, Runtime444, failures/skips0; `c-audit.json` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-check --platform` | `run-ik_madoq`, **PASS1707 .NET +366 Python**; десять actual TRX audited, Runtime444, Stardew18, CA90; `g-audit.json` |
| `rtk proxy env DOTNET_gcConcurrent=0 ./tools/hatifect-isolated-ui-ca --keep` | **PASS90**, retained `hatifect-ui-ca-isolated.ooc7yrq9`; 44 exact projected files, восемь package DLL равны текущим producer outputs, три isolated assets files и две точные deployed CA DLL; `p-audit.json` |
| Native explicit Terminal Open/Follow | Ожидается отдельный test-only checkpoint UI поверх e3c9dbf и фактический игровой запуск |

`source.json` связывает merge parents, 22 source postimages и сохранённое исправление harness. `preliminary-review.json` содержит границы исходного и connected source review. C/G относятся к production baseline e954c6d; последующее добавление native fixture получает собственную source identity и необходимые проверки.

Сценарий alpha.43 `semantic.actions.pump` подтверждает owning Update, но сам по себе не доказывает explicit Terminal generation transitions. UI готовит отдельное нативное доказательство same-Cached Open, route Follow, pending root/portal retirement, suppression поздних результатов, работоспособности нового action и сохранения pending при отклонённой подготовке. До выполнения этих условий alpha.43 fingerprints и runtime reports не переименовываются в alpha.44.

Следующий шаг — принять отдельный native fixture и выполнить изолированную runtime-приёмку на точной поставке. Затем проверить итоговый diff, обновить roadmap и опубликовать интеграцию. U04/alpha.45, active reload/alpha.46, concrete typed consumer и незавершённая физическая Backspace-проверка остаются отдельным объёмом.
