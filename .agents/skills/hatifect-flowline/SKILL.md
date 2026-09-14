---
name: hatifect-flowline
description: Change or review Hatifect Flowline Core, persistence, application and semantic projections, Stardew inventory adaptation, save lifecycle, recovery, or resource boundaries. Not for generic UI rendering or third-party adapters.
---

# Hatifect Flowline

Read root `AGENTS.md`, `ARCHITECTURE.md`, `docs/DEVELOPMENT.md`,
`Hatifect Flow/AGENTS.md` and `Hatifect Flow/README.md`. Treat current source and
tests as authoritative over historical roadmap evidence.

Choose the owner before editing:

- Core owns identity, operation state, queues, routing, dispatch and public
  application contracts.
- Persistence owns checkpoints/envelopes, save binding, writer fencing,
  receipts, recovery and format compatibility.
- `Hatifect.Flow.UI.Semantic` projects immutable application snapshots and typed
  commands; it does not own domain state, layout or rendering.
- The Stardew host owns game lifecycle, inventory leases, physical items and the
  atomic game-save boundary.

Core/Persistence must remain independent of UI, Stardew, SMAPI and specific
mods. Preserve session/revision authority, deterministic state transitions,
save isolation and the existing ten-increment model. A known Applied receipt may
reconcile without repeating a physical effect; missing or ambiguous outcome
retains the recovery fence and forbids automatic replay. Do not materialize
historical custody, lose or duplicate cargo across retry/reload, or make a UI
projection the source of truth. Retained cargo, attempts, receipts, routing and
per-update work remain bounded by the published resource contract.

Trace changes through the owning project, Core/Persistence tests, semantic
application consumer and game host. For each changed transition cover repeat,
stale session/revision, provider refusal, interruption/recovery, commit boundary
and save isolation as applicable. A public or persisted change requires explicit
compatibility scope and all affected consumers in the same change.

Use host-free Flow tests for Core/Persistence/application/semantic work; add the
platform scope for `Chest`, `Item`, inventory, SMAPI or game-session behavior.
Use `hatifect-test-selection` for the exact command. Fresh runtime evidence is
required only for live chest/save/restart/input or release claims. Diagnostic
fake-provider scenarios do not prove production inventory readiness, and no
runtime workflow may write real user saves.
