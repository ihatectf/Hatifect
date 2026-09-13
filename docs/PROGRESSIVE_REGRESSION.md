# Progressive regression selection

Status: Phase 6 implemented. Этот tooling-слой сокращает обычную локальную fix iteration, но не
меняет обязательные CI/release gates, runtime-протоколы, семантику сценариев или правила
приёмки. Финальная проверка по-прежнему выполняется только канонической командой
`./tools/hatifect-check`.

## Владение и запуск

`tools/progressive_regression.py` владеет выбором, валидацией и последовательным исполнением
scope. `tools/regression-selection.json` — проверяемое детерминированное отображение путей в
компоненты, direct tests и integration areas. Точка входа:

```text
./tools/hatifect-regression plan [--changed PATH] [--test TEST_ID] [--scenario ID]
./tools/hatifect-regression run  [--changed PATH] [--test TEST_ID] [--scenario ID]
./tools/hatifect-regression run --final
```

`plan` только печатает план. `run` сохраняет новый приватный
`artifacts/validation/regression-*/regression-report.json`. Публичная команда всегда добавляет
staged, unstaged и untracked файлы текущего Git candidate к переданным `--changed`; поэтому
неполный ручной список не может сузить проверку. Неизвестный, неоднозначный, межобластной,
repository/CI/release change и явный `--final` консервативно выбирают Level 5.

Level 1 принимает только один точный идентификатор:

```text
python:tools.tests.test_module.TestClass.test_method
dotnet:path/to/Registered.Tests.csproj::Namespace.TestClass.TestMethod
```

.NET filter аддитивен к существующему `hatifect-test --project`: сборка, TRX validation,
ненулевые counters и запрет skipped/failed tests сохраняются.

## Уровни

| Level | Scope | Канонический источник |
|---:|---|---|
| 1 | один падающий test/assertion | точный unittest ID или зарегистрированный `.csproj` + exact FullyQualifiedName |
| 2 | один падающий сценарий | реестр `tools/live-harness/scenarios.json` и существующий smoke/UI runner |
| 3 | direct tests owning subsystem | component mapping и owning test assemblies из `Hatifect.slnx` |
| 4 | релевантные integration tests | downstream dependency graph и allowlisted `hatifect-test` area commands |
| 5 | полный host-free regression | только `./tools/hatifect-check` |

Тестовые сборки owning subsystem остаются на Level 3. Транзитивные consumers из других
подсистем не маскируются под direct tests: они поднимают выбор до Level 4. Несколько явно
изменённых integration areas требуют Level 5. Platform tests выбираются только явным owning
scope/area и никогда не считаются покрытыми host-free full gate.

## Детерминированный отчёт

План и отчёт ограничены 256 KiB и сериализуются стабильным JSON. `plan` выводится в stdout;
только run report публикуется атомарно с mode `0600`. Selection fingerprint включает точный HEAD,
полный candidate path set, причины, выбранные и исключённые scopes. Перед первым процессом
исполнение повторно сверяет worktree path set, отвергает устаревший или изменённый план и
допускает только зарегистрированные сценарии, test assemblies и фиксированные команды.

Отчёт различает:

- `selectionReason` и `plannedEscalation` — почему был выбран план;
- `escalationTrigger` — уровни, до которых исполнение действительно дошло;
- `testsExecuted` — команда, статус, duration, ненулевой test count и ограниченный output tail;
- `testsOmittedByScope` — inventory-derived Python modules/.NET assemblies, оставшиеся вне scope;
- `plannedScopesNotExecuted` и `executionStop` — что не запустилось после первого FAIL/BLOCKED;
- `plannedRegressionLevel` и `finalRegressionLevel` — запланированный и фактически достигнутый
  уровни;
- `authoritativeFullGateSelected`/`authoritativeFullGatePassed` — был ли выбран и успешно пройден
  финальный gate.

Для unit/integration scope exit code 0 без машинно распознаваемого ненулевого числа тестов — FAIL,
а не PASS. Полный stdout не удерживается в памяти: executor сохраняет только bounded 64 KiB tail,
а в JSON остаются максимум 12 строк по 512 символов. Каждый stage ограничен 1,200 секундами;
при timeout executor завершает всю process group, фиксирует `HATIFECT_REGRESSION_TIMEOUT` и
возвращает `BLOCKED`, не переходя к следующим уровням. Без POSIX process-group semantics
исполнение fail closed как `BLOCKED`, не запуская stage.

## Совместимость и авторитетность

Изменение аддитивно. `.github/workflows/*`, `release.sh`, `result.json` v1, `failure.json` v3,
`preflight.json` v1, semantic events, direct/user-session transports, scenario IDs, raw logs и
acceptance schemas не менялись. Progressive run предназначен для быстрой локальной обратной
связи; зелёный Level 1–4 не заменяет Level 5, CI или platform/runtime acceptance.

## Финальные измерения Phase 6

Baseline взят из зафиксированного pre-Phase-1 состояния и таблицы в
`docs/FAILURE_ENVELOPE.md`. After измерен 2026-09-13 на `runtime.boot` run
`896995cc-2b5b-4066-b5bf-8c550360064e` и на Level 3 изменении
`tools/progressive_regression.py`. Недоступные значения не оценивались.

| Показатель | Before | After |
|---|---:|---:|
| Диагностический текст до actionable root | 478 bytes, 18 lines | 387 bytes, первые 5 строк `failure-summary.txt` |
| Diagnostic packet | отсутствовал | NOT_AVAILABLE: у измеренного preflight run нет `request.json`; error sidecar 230 bytes, hard budget пакета 131,072 bytes |
| Raw log | 0 bytes | 0 bytes |
| Downstream failures | BLOCKED, baseline не содержал надёжного count | 0 cascade records; 2 дополнительные независимые preflight failures |
| Targeted reproduction time | механизма не было | NOT_APPLICABLE: исходный run имеет статус BLOCKED, а безопасный repro разрешён только для валидного FAIL evidence |
| Source files для first-pass diagnosis | 0 для наблюдаемого environment blocker | 0 для того же класса blocker |
| Тесты обычной fix iteration | 505 Python tests в полном tooling scope | 29 тестов owning module, 94.3% меньше (примерно 17.4×) |
| Время до первого actionable signal | 0 ms в canonical result; требовалось прочитать один JSON | 0 ms в canonical result; root находится в первых 5 строках summary |

Level 3 measurement: PASS, 29 tests, 2,763 ms внутри regression stage; JSON report — 12,228 bytes.
Полное runtime/product acceptance и сравнение успешного/падающего игрового сценария остаются
BLOCKED теми же prerequisites: executable SMAPI, prepared isolated deployment, Ready
user-session executor и, для UI/input, разрешения macOS. Эти значения нельзя заменять
синтетическими оценками.
