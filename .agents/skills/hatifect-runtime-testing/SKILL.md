---
name: hatifect-runtime-testing
description: Run or diagnose Hatifect isolated runtime, SMAPI, semantic UI, or live acceptance scenarios. Use when real executor or game evidence is required; not for host-free static, build, or unit-test work.
---

# Hatifect runtime testing

Read root `AGENTS.md`, `tools/AGENTS.md` and `docs/RUNTIME.md`; resolve the target
only from `tools/live-harness/scenarios.json`. Follow the exact commands and
ownership rules in the runtime guide instead of reconstructing a launch flow.

- Check executor `status` first. Reuse a matching Ready instance; do not create
  a second owner or interrupt another worktree's run.
- Start the canonical executor from the current worktree only when runtime
  evidence is required. Wait for Ready before submitting a scenario.
- Use only the explicit isolated root and request-owned Mods, config, saves and
  artifacts. Never use normal user Mods, real saves, golden assets or another
  worktree's mutable state.
- Match the scenario to the requested acceptance boundary. Historical reports
  and evidence from another commit, build, request or scenario are not PASS.
- Preserve PASS, FAIL, BLOCKED and NOT_APPLICABLE exactly. A permission,
  platform, dependency or sandbox refusal is a reported condition, not a reason
  to bypass the harness.
- Stop only the executor started by the current task; leave a reused user-owned
  executor running.

On non-PASS, use `hatifect-diagnostics` and begin with the bounded failure
envelope and semantic tail. Do not rerun the scenario until those artifacts
identify a transient condition or a concrete change that needs verification.
