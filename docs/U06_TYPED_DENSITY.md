# U06-d: typed collection density

Проверено 2026-09-12 только в локальном Git. Exact source/test:
`470da48e929b46e6b1deab49e257e7522a3c42c9`; implementation commit
`1d4a97e2d760911209bb13566a79677ea587fdb7`.

## Contract and ownership

- Semantics владеет закрытым catalog доменом `density`: `Default`, `Compact`, `Comfortable`.
  Произвольное или quoted значение отклоняется `LUI2018`; отсутствие assignment даёт `Default`.
- Planning хранит выбранное значение как `UiSymbolId`, поэтому editor metadata, hover и references
  используют одну identity, а detached planning не превращает значение обратно в строку.
- Scene composition проверяет foundation IDs и один раз переводит их во внутренний
  `UiCollectionDensity`. Consumer не передаёт числовые размеры или profile multiplier.
- Runtime `UiDensityPolicy` является единственным владельцем коэффициентов 0.75/1/1.25 и
  responsive multiplier 0.9 для точных Compact/Controller profile IDs. Layout estimate и adaptive
  virtualization вызывают одну policy; measurement/height cache keys содержат enum без string
  comparison или новых исторических slots.

## Behavioral evidence

- UI scope `run-j6h6d81k`: **PASS 1,102/1,102**, zero failed/skipped — DevTools10,
  Planning141, Runtime678, Semantics66, Tooling.Server16, Tooling191.
- Exact canonical C `run-8lewjf7s`: **PASS 1,908 .NET + 503 Python**, zero failed/skipped — Flow803;
  DevTools10, Planning142, Runtime678, Semantics67, Tooling.Server16, Tooling192; tooling setup,
  architecture, public API, metadata, eight UI packages, restore and build also PASS.
- Isolated UI→CA boundary `hatifect-ui-ca-isolated.1c3pd0fs`: **PASS** — eight UI packages,
  47 projected files, 102 CA tests, two CA DLLs; UI source is absent from the consumer projection.
- Semantic tests подтверждают typed catalog identity и compile-time отказ `Spacious`.
- Runtime test на одном layout engine/cache подтверждает Compact < Default < Comfortable и
  однократную композицию host multiplier: разность Comfortable−Compact для Compact profile равна
  0.9 той же разности Wide profile.
- Tooling export/completion path публикует три enum values; hover и references связывают одинаковый
  `Compact` в двух документах общей identity.
- Public API verifier: **PASS**, API v1 и 7 semantic consumer types без изменения baseline.
- `tools.tests.test_semantic_contracts`: **PASS 21/21**.
- Focused exact-source follow-up: Semantics **PASS67**, Tooling **PASS192** after adding quoted-value
  rejection and density completion coverage; Planning **PASS142** after adding forged-IR rejection.

Первый UI run `run-mtmh5li_` остановился на compile-time test fixture, которая напрямую создавала
внутренний recipe со старой строкой `Normal`. Fixture переведена на `UiCollectionDensity.Default`;
полный неизменный scope после исправления прошёл в `run-j6h6d81k`.

## Performance scan

Hot-path additions не создают коллекций, LINQ, строк или делегатов. `Factor` — один enum switch и
два точных сравнения `UiSymbolId`; результат вычисляется по месту без кэша и allocation. Проверка
изменённых Runtime files дала 0 IndexOf/Substring/culture string comparisons, 0 chained Replace,
0 params и 0 LINQ-on-char; все 10 class declarations в просмотренных файлах уже sealed, новая policy
static. Найденные 3 List, 14 Dictionary и 7 LINQ call sites находятся в существующей композиции и
не затронуты этим diff.

## Remaining scope

Остановленный `run-ahjl6e9z` с exit130 завершился до build/.NET tests, когда GQ вернул себе общий
runtime slot, и не считается evidence; неизменный кандидат позже прошёл exact C/P выше. Production
consumer assets сейчас не задают explicit density, поэтому native visual/EN/RU/scale/controller для
этого source не заявляется. Tooltip, collection-row prompts и оставшиеся theme families сохраняют
полный U06 в статусе IN_PROGRESS.
