# Semantic live-test agent

`semantic-test-agent.py` is the deterministic external GUI driver for future Hatifect semantic acceptance tests. A scenario opts in by adding one checked-in file:

```text
tools/live-harness/semantic-tests/<scenario-id>.json
```

`tools/hatifect-live-runner` discovers that exact file after the scenario has already passed the canonical manifest/deployment/save preflight. Without a spec, the runner keeps the normal one-process `user_session_runtime` handoff.

## Authority boundary

The semantic agent may only:

- read request-owned detached JSON evidence under `artifacts/runtime/<request>/diagnostics/`;
- locate current UI nodes by semantic ID, semantic prefix, action ID, role or dynamic accessible name;
- reveal clipped nodes through ordinary wheel input;
- move/click the real pointer through the macOS Quartz backend;
- send bounded keyboard/text input through Quartz;
- wait for or assert values in detached evidence.

A spec cannot run shell/Python, evaluate expressions, import Hatifect, construct Flow commands, call action automation, synthesize PASS, or change the product acceptance report. Product/harness assertions remain authoritative.

The execution loop is deterministic:

```text
checked-in spec
    -> validate identity/bounds/operations
    -> read fresh domain + UI evidence
    -> resolve semantic selector/current state
    -> perform one native input action if needed
    -> wait for new evidence
    -> assert invariant
    -> next step
```

There are no coordinate constants or sleep/playback operations in the spec. UI layout, scale, scrolling and screen position are resolved from the current semantic/accessibility frame.

## Spec v1

Required top-level fields:

```json
{
  "schemaVersion": 1,
  "id": "example.scenario",
  "platform": "macos-quartz",
  "evidence": {
    "domain": {
      "path": "diagnostics/example-domain.json",
      "identity": {
        "requestId": "${requestId}",
        "scenarioId": "${scenarioId}"
      }
    },
    "ui": {
      "path": "diagnostics/example-ui.json",
      "identity": {
        "runId": "${requestId}",
        "scenario": "${scenarioId}"
      }
    }
  },
  "gameActive": {
    "source": "domain",
    "path": "inputTelemetry.latest.gameActive",
    "activateAppContains": "Stardew Valley"
  },
  "steps": []
}
```

Evidence paths are relative and must remain below `diagnostics/`. Each read revalidates the request/scenario identity declared by the spec.

Built-in variables are `${requestId}`, `${requestHex}` and `${scenarioId}`. `capture` creates additional bounded variables from detached evidence. Exact `${var}` substitution preserves the value type; interpolation into a larger string converts the value to text.

Supported capture transforms are `count`, `string`, `uuidHex`, `uuidCanonical`, `lower`, and `upper`.

## Operations

The v1 operation set is intentionally closed:

- `capture`: read an evidence value into a variable;
- `ensureGameActive`: verify/activate the configured game process;
- `wait`: wait until all detached-evidence predicates are true;
- `assert`: require all predicates immediately;
- `move`: move the real pointer to a game-local point published by evidence;
- `key`: send `K`, `Tab`, `Backspace`, or `Escape` through Quartz;
- `text`: send bounded Unicode text through Quartz;
- `click`: locate a semantic element, reveal it if clipped, require it enabled by default, then perform a real click;
- `replaceText`: focus one exact semantic text field and replace its current value through native Backspace/text events;
- `waitElement`: wait for a semantic element and predicates on that element;
- `waitCapture`: require a fresh retained UI capture containing a matching visible semantic element;
- `waitUi`: wait for visible/hidden UI and optionally an exact experience identity.

No operation accepts executable code.

## Semantic selectors

Selectors use one or more of:

```json
{
  "semantic": "Hatifect.Flow/network/field/name",
  "semanticPrefix": "Hatifect.Flow/network/source/",
  "action": "Hatifect.Flow/network/action/register",
  "name": "${sourceName}",
  "role": "TextField"
}
```

Exactly one current element must match an action/click selector. A clipped element is scrolled into view from its current `bounds`/`clip`; screen coordinates are calibrated against the game-local pointer before the click.

Prefer stable semantic/action IDs. `name` is intended for dynamic collection entries whose stable semantic ID is not known until runtime. Do not use localized static labels as selectors.

## Evidence predicates

`wait` and `assert` conditions use `source` (`domain`, `ui`, or `latest`), a dotted path with optional list indices such as `openings[-1].target`, and exactly one predicate:

- `equals`, `notEquals`;
- `truthy`, `falsy`, `nonNull`;
- `startsWith`, `contains`, `containsAny`, `regex`;
- `countEquals`, `countGreaterThan`, `countAtLeast`;
- `uuidZero`;
- `containsAll` for retained sets such as native probe phases.

Example:

```json
{
  "id": "wait-opening",
  "op": "wait",
  "timeout": 20,
  "conditions": [
    {
      "source": "domain",
      "path": "openings",
      "countGreaterThan": "${openingCountBefore}"
    }
  ]
}
```

`waitElement` and `waitCapture` apply the same predicates directly to the selected element, e.g. `{"path":"enabled","equals":true}` or `{"path":"value","truthy":true}`.

## Adding a future semantic test

1. Keep the product scenario and authoritative checks in `scenarios.json`/the owning runtime. The semantic spec does not define PASS criteria that belong to the product.
2. Ensure the owning runtime publishes request-bound read-only evidence for facts the external agent must observe. Do not expose mutable runtime objects or direct command authority.
3. Ensure UI observation exposes stable semantic/action identity plus accessibility bounds/clip/value/enabled/focus for the target experience.
4. Add `semantic-tests/<scenario-id>.json` using the smallest operation set necessary.
5. Address UI by semantic/action IDs, not geometry or localized labels.
6. After every native mutation, wait for a discriminating new evidence fact rather than relying on elapsed time.
7. Add tooling tests for schema rejection, required operations/invariants and any new generic primitive. Do not add a scenario-specific Python driver unless the generic agent truly lacks a reusable primitive.
8. Run `./tools/hatifect-test tools`, then `./tools/hatifect-check`, and complete the real macOS live acceptance separately.

`flow.ui.player.input.json` is the first reference workflow. It drives the production K entry, native pointer/text/Backspace/Tab proof, busy-entry rejection, two station registrations, directed link, whole/partial shipments, Window closure, transport/save/reload, final native reopen and delivered-history selection without direct Hatifect automation calls.
