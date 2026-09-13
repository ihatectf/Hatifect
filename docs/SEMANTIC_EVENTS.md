# Semantic diagnostic event stream

Status: Phase 2 implemented. Поток является аддитивным диагностическим контрактом и не меняет
семантику сценариев, `result.json` protocol v1, direct transport protocol v2 или user-session
protocol v1.

## Назначение и владелец

`tools/live-harness/validate.py` владеет схемой, сериализацией, синхронизацией, retention и строгой
проверкой событий. Это тот же source-pinned файл, который уже формирует канонические `result.json`
и failure sidecars. Direct runtime вызывает проверенный validator, а semantic companion не
импортирует event implementation: его начало и exit фиксирует внешний `hatifect-live-runner`
через validator CLI после join.

Каждый запуск по умолчанию создаёт request-owned `semantic-events.jsonl`. Он содержит только
осмысленные переходы исполнения, необходимые для восстановления ближайшей причинной
последовательности. Полные сообщения, stack traces, пути, environment dumps, кадры UI и содержимое
логов сюда не копируются. `transport.log`, `harness.log`, `smapi.log`, acceptance report,
screenshots и прочие исходные доказательства остаются отдельными и авторитетными.

## Формат записи

Каждая строка — один compact JSON object с фиксированными полями:

```json
{"component":"Runtime","event":"Runtime.StateChanged","fields":{"state_after":"Running","state_before":"Launching"},"format_version":1,"run_id":"...","scenario":"flow.ui.player.input","seq":4,"time":"2026-09-13T00:00:00Z"}
```

- `format_version` равен `1`;
- `seq` строго возрастает внутри request-owned потока;
- `time` — timestamp с timezone;
- `scenario` и `run_id` повторяют идентичность запуска;
- пара `event` / `component` выбирается только из фиксированного vocabulary;
- `fields` содержит не более 12 полей с именами lower snake case и только scalar JSON values:
  `null`, boolean, конечное число, bounded integer или строку не длиннее 256 символов.

Сериализация использует UTF-8 с literal Unicode, сортировку ключей и separators без пробелов.
Одинаковые события с одинаковым timestamp дают одинаковые байты.

## Стабильный vocabulary Phase 2

| Event | Component | Источник факта |
|---|---|---|
| `Scenario.Started` | `Scenario` | автоматическое начало первого события запуска |
| `Preflight.Completed` | `Preflight` | direct request прошёл существующие проверки и принят |
| `Preflight.Failed` | `Preflight` | канонический non-PASS result с preflight root |
| `Runtime.StateChanged` | `Runtime` | существующий direct-runtime lifecycle |
| `GameProcess.Started` | `GameProcess` | запуск owned SMAPI process group, включая crash continuation |
| `GameProcess.Completed` | `GameProcess` | завершение supervised process attempt |
| `Validation.Started` | `Validation` | начало finalization acceptance report |
| `Validation.Completed` | `Validation` | согласованы finalizer exit code и canonical result status |
| `SemanticAgent.Started` | `SemanticAgent` | runner запустил checked-in semantic companion |
| `SemanticAgent.Completed` | `SemanticAgent` | runner дождался успешного exit companion |
| `SemanticAgent.Failed` | `SemanticAgent` | runner дождался неуспешного exit companion |
| `Assertion.Failed` | `Assertion` | первая каноническая non-PASS assertion |
| `Cleanup.Failed` | `Cleanup` | один из строгих harness-owned cleanup outcomes |
| `Scenario.Completed` | `Scenario` | внешний владелец завершил runtime и companion и выбрал итоговый result |
| `Result.Published` | `Result` | атомарно записан canonical result snapshot с его fingerprint |

`Runtime.StateChanged` использует уже существующие состояния `Accepted`, `Launching`, `Running`,
`Completed`, `Failed`, `TimedOut`, `Cancelled` и `Rejected`; новый lifecycle не вводится. Polling,
heartbeat, каждая строка лога и каждый отдельный semantic UI step намеренно не становятся
событиями: их существующие bounded artifacts сохраняются, а поток остаётся маленьким.

## Границы и конкурентная запись

- не более 128 сохранённых событий;
- не более 256 KiB на весь файл;
- не более 16 KiB на одну запись, включая повторённую идентичность запуска;
- старые записи детерминированно вытесняются при достижении count/byte budget;
- `failure.json` содержит не более 16 последних записей и не более 32 KiB их JSONL-представления;
- lock и stream обязаны быть current-user regular files с mode `0600` и link count 1;
- sequence назначается только под межпроцессным file lock;
- замена потока атомарна и синхронизируется через `fsync`.

Malformed JSON, незавершённая строка, неизвестное событие, nested/object payload, чужие
`scenario`/`run_id`, symlink, неправильные permissions или превышение бюджета отклоняются до
замены существующего потока.

Строгий API writer/reader используется тестами и валидатором контракта. Production producers
публикуют события best-effort: отказ диагностики не меняет canonical PASS/FAIL/BLOCKED и не
мешает записи обязательных failure sidecars. Такой отказ сохраняется отдельно в bounded
`diagnostics/semantic-events-error.json`; при повреждённом stream failure tail становится пустым,
но root failure и raw evidence остаются доступными.

## Failure envelope

Новые non-PASS запуски формируют `failure.json` format v3 с полем `semantic_event_tail`. Phase 1
format v2 продолжает строго приниматься для уже существующих артефактов. `result.json` остаётся
авторитетным, а `result_fingerprint` по-прежнему вычисляется только по нему, поэтому циклической
зависимости между result, event stream и failure envelope нет.

После terminal direct-runtime transition и, если применимо, после join semantic companion внешний
владелец публикует ровно одно финальное `Scenario.Completed` для текущего result fingerprint и
атомарно обновляет bounded tail. Поэтому обычный первичный разбор может прочитать root failure и
ближайшие runtime/validation/cleanup события из одного `failure.json`; raw logs нужны только для
углублённого расследования.
