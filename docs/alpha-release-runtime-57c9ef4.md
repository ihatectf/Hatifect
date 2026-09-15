# Проверка альфы в игре — 57c9ef4

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Проверено 2026-09-12 только в локальном Git. Exact source:
`57c9ef43883d84a916e50ce38e729168f397b893`. Между кодовым checkpoint
`5d017ae814168f61e7d5a1f4fc8d191765aeb9aa` и этим source изменялась только документация.

## Build identity and static evidence

- Common G `run-d974rh23`: **PASS 2 366 .NET + 503 Python**; architecture, public API,
  metadata, восемь UI packages, restore и game references также PASS.
- Isolated UI→CA P `hatifect-ui-ca-isolated.07hb2g1r`: **PASS** — восемь packages,
  47 projected files, 102 CA tests и две CA DLL.
- Canonical prepare с task-local `NUGET_HTTP_CACHE_PATH`:
  `run-f1aqx9e4`, `run-wfsn5hlx` и `run-upix3tvo` — **PASS**. Audit и package
  signature checks не отключались. Runtime fingerprint:
  `6712061574487a3fd6be5607c35ea82c883439556e5ab7ef7ed6942d794b654e`;
  three-module candidate fingerprint:
  `bd642229a8a075b1deb1249e54aeef10197d223c64348478de77df552a3b51fa`.
- Все 14 упакованных DLL содержат exact revision
  `57c9ef43883d84a916e50ce38e729168f397b893`.

Глобальный NuGet HTTP cache ранее возвращал `NU1301`. Новый отдельный cache подтвердил, что
причина находилась в локальном cache state, а не в source/package policy. Позднее во время
финального `release.sh` DNS перестал разрешать `api.nuget.org`: `run-6tt9wtlk` и
`run-2vsbdv4c` остановились на restore. Эти два запуска не являются PASS. Архив ниже собран
штатными deterministic `release.py` stages из DLL последнего успешного exact prepare; тестовый
PASS переиспользован только потому, что код и build inputs после `run-d974rh23` не менялись.

## Runtime matrix

Каждый независимый игровой сценарий запускался после нового exact prepare, потому что игровой
runtime изменяет изолированное deployment state. Скриптовый аудит `result.json`, `request.json`
и `runtime-options.json` подтвердил точный repository HEAD, пустые `exceptions`, только PASS
assertions и восстановление обоих временно изменяемых файлов настроек без ошибок.

| Scenario | Request | Checks |
| --- | --- | ---: |
| `save.bootstrap` | `20957d78-6d39-4609-8f8a-1fcf26b3be60` | 2 |
| `flow.ui.player` | `93e65d45-f2f5-4400-905a-13ba2b8a094f` | 11 |
| `flow.chest.roundtrip` | `758206cf-3ca4-4764-b928-676e2dc9f150` | 8 |
| `flow.chest.crash-after-save` | `29fdea41-b08f-4bf8-b53f-fcf63020a02c` | 9 |
| `flow.chest.crash-after-delivery` | `b0a622d4-a2f1-4b19-8ed0-3eeb0d26cf5f` | 9 |
| `flow.chest.crash-after-unsaved-extraction` | `b7644858-db32-483a-ad12-86baf6542966` | 10 |
| `flow.chest.crash-after-unsaved-delivery` | `7b7def7f-4fd1-4179-be9c-059465e9dbef` | 10 |
| `flow.chest.cancellation` | `62bb0e4b-bf09-41a6-8bfc-05fb59bb6086` | 10 |
| `flow.chest.return` | `32bcd5d2-341b-492e-a33c-5124e536c7f7` | 14 |
| `flow.chest.crash-after-return` | `82d9b562-fae3-44a9-ab0d-8e57236b5b2c` | 15 |
| `flow.chest.isolation` | `b5c684d9-0395-4995-82aa-7493eb881654` | 12 |
| `flow.chest.performance` | `593cf698-58ff-48be-863c-c6737a501f24` | 9 |
| `flow.chest.resources` | `70cd967a-2e91-4377-a99f-c157b31702d0` | 12 |
| `flow.ui.player.en-075` | `9487d5c4-1dad-4dd6-9f1d-0612f1dde08f` | 13 |
| `flow.ui.player.ru-075` | `56e60468-9eec-411f-b8d9-54e08688802f` | 13 |
| `flow.ui.player.en-100` | `ef910d69-e389-49fd-be88-0ca3554a123a` | 13 |
| `flow.ui.player.ru-100` | `faccbe95-84c1-474b-8e2a-a31c128f7a5d` | 13 |
| `flow.ui.player.en-125` | `fc623638-2ad8-4fd5-889e-8de4ef80e1ad` | 13 |
| `flow.ui.player.ru-125` | `ff8881eb-a2bd-4665-9014-c2789a4c8e82` | 13 |
| `flow.ui.player.en-150` | `9ef2365d-f398-43a2-940f-b726c41d07ff` | 13 |
| `flow.ui.player.ru-150` | `0bf3b836-4450-49d3-a6b9-665d3c09bf23` | 13 |
| `flow.ui.isolation` | `f93db863-0d54-43c2-8d4e-3df585aa30bd` | 15 |
| `semantic.chests-anywhere-overlay` | `47a714ff-7a1c-4772-8ed5-a24617b62c93` | 5 |

Итог: **23 PASS scenarios, 255/255 checks**. `flow.ui.player` подтверждает production Window,
две реальные станции на сундуках, направленный маршрут, целую и частичную отправку реальных
стеков, два сохранения/reload, видимые результаты, reopen и отсутствие дублирования. Исходная
chest matrix подтверждает crash/restart, несохранённые extraction/delivery, cancel/retry/return,
save switching, resource bounds и игровой PERF. `flow.ui.isolation` подтверждает exact controller
profile, а `semantic.chests-anywhere-overlay` — production overlay с настоящим Chests Anywhere:
open, views, handoff, controller/Escape restoration и lifecycle.

Фактические CA composed/UI-layer PNG также просмотрены: после закрытия Hatifect session восстановлен
native Chests Anywhere menu, а Hatifect overlay не остаётся владельцем активной поверхности.

В каждом из восьми EN/RU × 75/100/125/150 artifact directories просмотрен фактический
`delivered-reopened-ui-layer.png`; дополнительно просмотрены `whole-created`,
`source-registered`, `destination-registered` и `route-created` для крайних RU75 и RU150.
Текст и Result остаются внутри окна; на 150% используется прокрутка, выбранные строки и итог
видимы. Semantic observations сами по себе для этого вывода не использовались.

Обычный `hatifect-ui-test semantic.chests-anywhere-overlay` создал request `059b8ffd-858f-4d51-99fc-0029ef489b46`,
но его обязательный повторный сетевой prepare остановился на DNS; запрос **BLOCKED** и evidence не
содержит. Совместимость проверена каноническим `hatifect-live-runner ui` после повторных
`release.py deploy-isolated`, записи и проверки deployment identity/marker. Runner сохранил все
обычные exact-head, required-mod, save, fingerprint и result guards.

## Physical input result

`flow.ui.player.input`, request `76906d54-c4a3-4e22-a089-2f9d1dc25d85`, — **BLOCKED** до
первого semantic step. macOS вернул false для post-event Accessibility preflight Python runtime.
Игра была отменена, настройки восстановлены (`Restored`, errors `[]`). Этот запрос не доказывает
K, pointer, Tab/Search, Backspace, Enter или controller action. Требуется выдать Accessibility
процессу `/Library/Frameworks/Python.framework/Versions/3.14/bin/python3` и повторить сценарий на
неизменном кандидате после нового prepare.

## Installable archive

- Archive: `artifacts/package/artifacts/Hatifect-clean-baseline.zip`.
- SHA-256: `a4d1e01b56d2fcce97cf2b219fa52b5e6859dd4dc79991d9d2f542279fd639a4`.
- Inventory: **21 files / 14 managed DLLs**; archive names exactly match `Hatifect.Release.json`.
- Archive bytes exactly match assembled package bytes.
- Isolated install: `/private/tmp/hatifect-smapi-test.q02-alpha/Mods/Hatifect`.
- Installed bytes exactly match all 21 assembled files; `release.py verify-package` passes for
  both assembled and installed roots.
- Dependencies and first-run procedure: [Установка первой альфы](alpha-installation.md).

«Управление диагностической перевозкой»/«Настройка станций и маршрутов»/«Отправка груза и работа с ошибками» имеют точную normalized runtime, visual, controller и CA coexistence evidence этого
состава, но physical input остаётся незакрытым. Поэтому «Приёмка первой альфы» остаётся **IN_PROGRESS** и архив нельзя
объявлять принятой первой альфой до этой проверки. Когда NuGet DNS снова доступен, следует также
повторить единый `release.sh` gate; deterministic package/install stages уже подтверждены.
