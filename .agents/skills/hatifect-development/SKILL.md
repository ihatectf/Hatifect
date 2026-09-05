---
name: hatifect-development
description: Work on the Hatifect monorepo across its semantic UI framework, Flowline, game and third-party adapters, development tooling or Codex configuration. Select ownership, scoped instructions, relevant specialist skills and verification for a coherent cross-system change. Use for Hatifect implementation, architecture and review tasks.
---

# Hatifect development

Read root `AGENTS.md`, `ARCHITECTURE.md` and `docs/DEVELOPMENT.md`. Resolve paths in the adjacent `routing.json` from the repository root.

1. Establish scope and working-tree ownership. Identify the contract owner, implementation layer, affected consumers and migration risk before editing. Preserve current Flowline and semantic UI behavior unless explicitly changed by the task.
2. Select applicable routes from `routing.json`. Read their scoped instructions even from a root-cwd task. Choose the smallest relevant specialist skill set; read each selected skill fully and announce it. Registry names are recommendations to locate in the current catalog, not files guaranteed on every machine. Do not load every route or equate discovery with use.
3. For a missing specialist, search the catalog for its equivalent. Continue within repository guidance where possible; report a blocked requirement if the specialist is indispensable. Do not install skills/packages/plugins merely to make a catalog complete.
4. Trace shared contracts and consumers in one task branch/PR. Keep Flowline Core/Persistence independent of presentation and game APIs. The custom UI is not Blazor/MAUI/CSS. Adapters own third-party interaction; framework owns visual policy. Future systems join the solution and ownership process rather than creating a separate permanent branch or toolchain.
5. Use canonical checks in DEVELOPMENT. Test authoring, test execution, static pairing, assertion review and runtime observation are different tasks; select skills accordingly. Test helpers with temporary state and concrete failure/success outcomes. Inspect hot paths reached by the change.
6. Separate implementation and independent review when delegation is warranted by root instructions. Verify current source, review the final diff and report exact status/evidence. Never turn an unavailable stage into PASS.

`./tools/hatifect-agent-check` validates portable configuration integrity. `--host` inspects actual Codex project/skill discovery; add `--route <id>` to require the skills for that route. These checks do not prove that an agent read/applied a skill; the task report records actual selection and use.
