---
name: hatifect-adapters
description: Change or review third-party integrations under Hatifect's Integrations tree, especially Chests Anywhere capability detection, reflection, capture, ownership leases, semantic projection, package isolation, and lifecycle recovery. Not for the Flowline Stardew inventory host.
---

# Hatifect adapters

Read root `AGENTS.md`, `ARCHITECTURE.md`, `docs/DEVELOPMENT.md`,
`Integrations/AGENTS.md` and the affected adapter README. Establish the supported
external API version, capability boundary, lifecycle owner and unsupported
behavior before editing.

Keep third-party types, reflection, subscriptions and native objects inside the
adapter. Capture them into bounded immutable application values before semantic
projection. The semantic experience describes meaning; UI framework owns layout,
rendering, input and accessibility policy; domain Core/Persistence do not depend
on a concrete mod.

Follow the affected adapter's declared behavior for a missing or incompatible
optional dependency instead of inventing a fallback or failing the host.

For Chests Anywhere specifically, unsupported CA is an unavailable capability.
Capture, mutation, reopen, close, return-to-title and disposal failures preserve
the exact menu/session lease and allow bounded cleanup retry. Publish ownership
before the first CA mutation, restore the captured original value, and never
release or overwrite another owner. Repeated close/dispose remains safe.

For Chests Anywhere, preserve the exact-version UI package boundary. Do not add
UI source `ProjectReference`s to the isolated consumer or ship duplicate UI
runtime DLLs. A public UI change includes its CA consumer and package-isolation
proof; CA-specific behavior does not justify changing Flow Core or general UI
framework policy.

Use platform adapter tests and isolated UI-package validation for CA changes,
then select the final gate with `hatifect-test-selection`. Use fresh
`hatifect-runtime-testing` scenarios only when live compatibility, missing or
incompatible dependency, capture failure, native lifecycle or return-to-title is
part of the claim. Ordinary builds must not install mods or touch user state.
