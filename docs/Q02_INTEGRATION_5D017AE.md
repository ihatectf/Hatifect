# M3/Q02: local integration checkpoint 5d017ae

Проверено 2026-09-12 только в локальном Git. Exact source:
`5d017ae814168f61e7d5a1f4fc8d191765aeb9aa`. Checkpoint объединяет F19 admission и
cargo-reselection feedback, U06 shared status/input-prompt framework, typed Flow/CA status consumers
и интеграционное исправление CA publication test. Он подтверждает общую совместимость исходников и
пакетной границы, но не завершает runtime-часть F13/F18/F19/Q02.

## Integrated contracts

- `UiStatus` имеет канонический `UiDataTypes.Status` / `UiSourceTypes.Status`; Flow Network и CA
  публикуют status kind вместе с локализованным сообщением, сохраняя стабильные semantic IDs.
- Runtime владеет layout, visual/accessibility policy и input prompts; consumer не задаёт геометрию
  или цвета статуса.
- Flow refresh сохраняет cargo selection только при совпадении slot и fingerprint. Изменившийся стек
  требует явного перевыбора; typed Send и inventory lease остаются окончательной проверкой.
- Exact CA integration обнаружила один оставшийся test-only cast старого string status. Локальный
  commit `1fb9f02` перевёл probe на `UiStatus`; production contract не менялся.

## Exact validation

- UI scope `run-lapv_8_j`: **PASS 1,100/1,100** — DevTools 10, Planning 141, Runtime 677,
  Semantics 65, Tooling.Server 16, Tooling 191; zero failed/skipped.
- Flow platform `run-nkn2dnv2`: **PASS 803 + 254** Core/Persistence и Stardew adapter tests;
  zero failed/skipped.
- CA platform `run-h8mbdhbt`: **PASS 102/102**; zero failed/skipped.
- Common G `run-d974rh23`: **PASS 2,366 .NET + 503 Python**; architecture, public API,
  metadata, восемь UI packages, restore и пять game-reference stages также PASS.
- Isolated P `hatifect-ui-ca-isolated.07hb2g1r`: **PASS** — восемь UI packages, 47 projected files,
  102 tests, две CA DLL; UI source отсутствует в consumer projection.

Первые два prepare до этого checkpoint (`run-2rre1_h8`, `run-h4ork_te`) относились к более раннему
`d11e8f0` и остановились на внешнем NuGet repository-signature `NU1301`; они не являются evidence
текущего состава. После фиксации отчёта docs-only candidate `046267e` сохранил тот же код. Его exact
prepare `run-r7i4ztb4` подтвердил release source contract, metadata и UI packages, но снова получил
`NU1301` для CA на restore до build; результат **BLOCKED**, runtime deployment не принят.

Этот checkpoint продолжен docs-only source `57c9ef4`: canonical prepare, normalized/original
runtime matrix и installable archive теперь проверены в
[Q02_RUNTIME_57C9EF4.md](Q02_RUNTIME_57C9EF4.md). Physical `flow.ui.player.input` по-прежнему
требует процесса с `CGPreflightPostEventAccess()=true`; существующий TCC-block не заменяется
автоматизированным вводом.
