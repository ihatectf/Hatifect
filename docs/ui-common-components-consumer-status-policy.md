# Общие правила отображения состояний

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

Status: **implementation and common/CA/package validation complete; runtime validation pending**.
Owner source commit: `a0e558b5424e75e5afadfd5b6d81d569e32773bc`; local integration commits:
`102b3e2` and CA test correction `1fb9f02`. Full «Общие компоненты, темы и подсказки» remains **IN_PROGRESS**.

## Contract

The transport-neutral graph now has one canonical required scalar descriptor for semantic status:
`UiDataTypes.Status` (`Hatifect.UI:data/status`). `UiSourceTypes.Status` binds that descriptor to
`UiStatus`, and source validation rejects another CLR type that attempts to reuse the reserved ID.
`UiExperienceBuilder.Status` always emits this descriptor. Its ID/alias/label overload lets a
localized consumer preserve stable graph identity without putting display text into its alias.
The semantic surface API remains v1 and existing `Monitor<T>` authoring is unchanged.

Flow Network keeps the existing `source/status` and `element/transport` identities, `Transport`
alias and localized messages. It maps application state to the shared policy as follows:

| Flow state | Status kind | Existing message |
| --- | --- | --- |
| Active | Success | Ready / Готово |
| Paused | Status | Paused / Приостановлено |
| RecoveryRequired | Error | The existing retained/unknown outcome explanation |
| Closed | Empty | Session closed / Сессия закрыта |
| Faulted | Error | Session closed / Сессия закрыта |

The six localized values are allocated once per Network Experience and reused by every projection.
An unchanged state therefore does not add a status allocation or notification.

The CA Navigator keeps its existing `element/Status` ID, `Status` alias and provider-owned text.
Ordinary provider status uses `Status`, a successful open handoff uses `Loading`, and a rejected open
uses `Error`. A blank provider status is rejected before publication because `UiStatus` requires a
meaningful message. Provider effects, atomic publication, action outcomes and handoff ownership are
otherwise unchanged.

Neither consumer contains status colors, typography, accessibility roles, input prompt text or
geometry. Runtime continues to own those policies.

## Validation

- Public API verifier: PASS; semantic surface API v1 retained.
- Semantic contract Python suite: PASS 21/21.
- Initial UI scope `run-yo8kuanr`: infrastructure abort after successful restore/build because the
  sandbox denied VSTest's loopback socket; no tests executed and the run is not counted.
- UI scope `run-oep1tson`: PASS 1,100/1,100 (DevTools 10, Planning 141, Runtime 677,
  Semantics 65, Tooling.Server 16, Tooling 191), zero failed/skipped.
- First Flow platform run `run-s56e3dq1`: RED after successful build, 788 passed and six failed.
  All failures came from the old exact-string test probe reading the migrated Transport source;
  observer exceptions also explained the empty observation list. Production publication behavior
  was unchanged.
- Corrected Flow platform run `run-r8wygf4_`: PASS Flow 794 + Stardew 249, zero failed/skipped.
- Host-free CA selection `run-7rqaqy1v`: expected selector failure before execution because CA is
  game-linked; zero tests are not counted.
- CA platform `run-6p6ioof0`: cancelled during UI packaging when the shared heavy slot changed
  owner; it did not reach restore or tests and is not counted.
- Exact integration review found one remaining nested test cast to the old string status. Commit
  `1fb9f02` changes that probe to publish `UiStatus`; CA platform `run-h8mbdhbt` then passed 102/102.
- Exact integration common G `run-d974rh23`: PASS 2,366 .NET + 503 Python, zero failed/skipped.
- Isolated UI→CA boundary `hatifect-ui-ca-isolated.07hb2g1r`: PASS 8 UI packages, 47 projected files,
  102 tests and 2 CA DLL; UI source is absent from the consumer projection.

Native EN/RU/scale/controller observation remains pending. Tooltip, typed density, collection-row
prompts and the remaining theme families stay outside this slice.
