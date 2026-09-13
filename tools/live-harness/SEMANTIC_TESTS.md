# Semantic live-test driver

`semantic-test-driver.py` is the deterministic external GUI driver for Hatifect semantic acceptance tests. A scenario opts in by adding one checked-in spec:

```text
tools/live-harness/semantic-tests/<scenario-id>.json
```

`tools/hatifect-live-runner` discovers that exact file after the scenario has passed the canonical manifest/deployment/save preflight. Without a spec, the runner keeps the normal one-process `user_session_runtime` handoff.

The shared implementation is split intentionally:

- `semantic_test_driver.py` owns the bounded spec/evidence protocol and low-level compatibility operations;
- `semantic_driver_ui.py` composes that protocol with the semantic UI interaction engine;
- `semantic_interactions.py` owns deterministic control resolution and feedback-driven native interactions;
- `semantic-test-driver.py` is the stable CLI entry point.

The design and fail-closed invariants are documented in [`SEMANTIC_MODEL_AUTOTESTS.md`](SEMANTIC_MODEL_AUTOTESTS.md).

## Authority boundary

The semantic driver may only:

- read request-owned detached JSON evidence under `artifacts/runtime/<request>/diagnostics/`;
- resolve current UI controls from the semantic/accessibility observation;
- reveal clipped or virtualized targets through ordinary native wheel input;
- move/click the real pointer through the macOS Quartz backend;
- send bounded keyboard/text input through Quartz;
- wait for observed semantic/domain postconditions.

A spec cannot run shell/Python, evaluate expressions, import Hatifect product code, construct Flow commands, call Hatifect action automation, synthesize PASS, or change the product acceptance report. Product/harness assertions remain authoritative.

The execution chain is:

```text
semantic/accessibility observation
    -> deterministic control resolution
    -> native Quartz input
    -> fresh semantic observation
    -> observed postcondition
```

There are no coordinate constants or sleep/playback steps in the spec. UI layout, scale, scrolling, focus and screen position are resolved from current evidence. An accepted bridge frame is not actionable until a new rendered/completed semantic frame is observed.

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

Evidence paths are relative and must stay below `diagnostics/`. Every read revalidates the request/scenario identity declared by the spec.

Built-in variables are `${requestId}`, `${requestHex}` and `${scenarioId}`. `capture` creates additional bounded variables from detached evidence. Exact `${var}` substitution preserves the value type; interpolation into a larger string converts the value to text.

Supported capture transforms are `count`, `string`, `uuidHex`, `uuidCanonical`, `lower`, and `upper`.

## Semantic selectors

Selectors may use one or more of these keys:

```json
{
  "semantic": "Hatifect.Flow/network/field/name",
  "semanticPrefix": "Hatifect.Flow/network/source/",
  "action": "Hatifect.Flow/network/action/register",
  "name": "${sourceName}",
  "role": "TextField",
  "collection": "Hatifect.Flow/network/history",
  "node": "stable-concrete-node-id"
}
```

Unknown selector keys are rejected. Resolution never falls back to the first match. After applying the selector and operation intent, zero materialized targets are pending/not-found and more than one valid concrete target is an ambiguity error. Duplicate concrete `nodeId` values are invalid evidence.

Operation intent supplies the accessibility role where appropriate:

- `focus` / `fill` -> `TextField`;
- `activate` -> `Button`;
- `select` -> `ListItem`;
- collection scrolling -> `List`.

This is why a semantic origin shared by a label, container and actual editor can still resolve deterministically to the `TextField`.

Prefer stable semantic/action IDs. `node` is the concrete observed-node identity and is useful when the scenario has already discovered a specific target. `name` is appropriate for dynamic accessible names, not localized static labels. `collection` constrains a list item to its explicitly observed owning collection.

## Preferred semantic operations

Use these higher-level operations for new UI workflows.

### `focus`

Resolves one `TextField`, reveals it if needed, calibrates the pointer, rereads a fresh semantic frame, verifies the same concrete node and current geometry/state, clicks it, then waits until the field reports `focused=true`.

```json
{
  "id": "focus-name",
  "op": "focus",
  "selector": {
    "semantic": "Hatifect.Flow/network/field/name"
  }
}
```

### `fill`

Requires an exact `semantic` field ID with optional `role`. It focuses the field, reads the observed current value, clears that value through native keyboard input with observed value changes, verifies the field is empty, types the requested Unicode text through Quartz, then waits until the semantic value exactly matches the request.

```json
{
  "id": "fill-name",
  "op": "fill",
  "selector": {
    "semantic": "Hatifect.Flow/network/field/name",
    "role": "TextField"
  },
  "value": "${sourceName}"
}
```

The engine does not type unless exactly one enabled semantic `TextField` owns focus.

### `activate`

Resolves a `Button`, normally by stable `action` identity, reveals and revalidates it, requires `enabled=true`, then performs one native click.

```json
{
  "id": "register",
  "op": "activate",
  "selector": {
    "action": "Hatifect.Flow/network/action/register"
  }
}
```

The click is only an input event. Product/domain acceptance must be established by a later evidence predicate; the driver never replays an action merely to manufacture a result.

### `select`

Resolves a `ListItem`, uses the item's stable `nodeId`/item identity, and waits for `selected=true` after the native click. Selection is idempotent when the target is already selected.

For virtualized lists, constrain the search to the declared owning collection:

```json
{
  "id": "select-delivery",
  "op": "select",
  "selector": {
    "name": "${deliveredName}",
    "role": "ListItem"
  },
  "collection": "Hatifect.Flow/network/history"
}
```

If the item is not materialized, the engine scrolls only that collection using its observed viewport, offset and maximum offset. An unmaterialized item without an owning collection is rejected instead of triggering a guessed global search.

### `reveal`

Makes a target actionable without clicking it. It uses current bounds/clip and native wheel input, distinguishes a nested owning collection from root scrolling, requires monotonic scroll progress, bounds the number of attempts, and rereads the semantic frame after every scroll.

```json
{
  "id": "reveal-history",
  "op": "reveal",
  "selector": {
    "semantic": "Hatifect.Flow/network/history",
    "role": "List"
  }
}
```

### `discover`

Captures the currently observed semantic interactive controls into a bounded variable. Decorations, labels and non-interactive nodes are excluded.

```json
{
  "id": "discover-controls",
  "op": "discover",
  "variable": "controls"
}
```

### `fillForm`

Preflights 1-32 exact semantic field IDs before the first edit, then fills them using the same `fill` primitive. This prevents a partial form edit when a requested field/value is invalid.

```json
{
  "id": "fill-form",
  "op": "fillForm",
  "fields": {
    "Hatifect.Example/field/name": "Name",
    "Hatifect.Example/field/note": "Note"
  }
}
```

## Compatibility and evidence operations

The v1 protocol also retains these bounded operations:

- `capture`: read an evidence value into a variable;
- `ensureGameActive`: verify/activate the configured game process;
- `wait`: wait until all detached-evidence predicates are true;
- `assert`: require all predicates immediately;
- `move`: move the real pointer to a game-local point published by evidence;
- `key`: send one allowed native key (`K`, `Tab`, `Backspace`, or `Escape`) through Quartz;
- `text`: send bounded Unicode text through Quartz; exactly one enabled semantic `TextField` must own focus;
- `click`: compatibility native semantic click, now resolved through the generic interaction engine;
- `replaceText`: compatibility alias for generic feedback-driven field fill;
- `waitElement`: wait for a semantic element plus predicates on that element;
- `waitCapture`: require a fresh retained UI capture containing a matching visible semantic element;
- `waitUi`: wait for visible/hidden UI and optionally an exact experience identity.

No operation accepts executable code.

## Semantic observer contract

The read-only UI observation exposes the data needed for deterministic input. Relevant element fields include:

- `nodeId`, `semanticId`, `actionId`, `role`, `name`, `value`;
- `enabled`, `selected`, `focused`;
- `bounds`, `clip`, `visible`, `hitTestable`, `interactive`;
- stable `itemIdentity` for `ListItem` nodes;
- `collectionId` for the explicitly observed owning collection;
- `collectionViewport`, `collectionClip`, `collectionScrollOffset`, `collectionMaximumOffset`;
- `positionInSet`, `setSize` when available.

The frame also exposes root-scroll viewport/clip/offset/maximum-offset metadata. Collection ownership is projected from runtime scene/layout relationships; the driver does not infer ownership by parsing semantic strings.

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

`waitElement` and `waitCapture` apply the same predicates directly to the selected element, for example `{"path":"enabled","equals":true}` or `{"path":"value","truthy":true}`.

## Freshness and fail-closed rules

Every operation that can change layout/input ownership follows these rules:

1. resolve the target from a current rendered semantic frame;
2. reveal/calibrate if necessary;
3. reread a fresh completed frame;
4. verify the same concrete target plus current geometry and relevant state;
5. send at most the intended native input;
6. wait for an observed semantic/domain postcondition.

The driver must fail or remain pending instead of guessing when:

- multiple valid targets remain;
- a concrete node identity is duplicated;
- geometry is missing/non-finite or becomes stale;
- the surface/target is replaced during calibration;
- a target becomes disabled;
- focus/input ownership is lost;
- scrolling makes no bounded progress;
- a virtualized item has no declared/observed collection owner;
- the native input is accepted but the requested semantic postcondition never appears.

## Adding a semantic test

1. Keep product scenario and authoritative PASS checks in `scenarios.json`/the owning runtime. The semantic spec must not define product acceptance that belongs elsewhere.
2. Publish only request-bound read-only evidence for facts the external driver must observe. Do not expose mutable runtime objects or direct command authority.
3. Expose stable semantic/action identity and the accessibility state required by the generic resolver.
4. Add `semantic-tests/<scenario-id>.json` using generic operations rather than scenario-specific Python.
5. Address UI by semantic/action/stable item identity, not coordinates or localized labels.
6. After every native mutation, wait for discriminating fresh evidence instead of elapsed-time playback.
7. Add tooling regressions for any new generic primitive or fail-closed invariant.
8. Run repository checks, then complete the host-required macOS live acceptance separately.

`flow.ui.player.input.json` is the reference workflow. It drives production `K` entry, the native pointer/text/Backspace/Tab probe, busy-entry rejection, two station registrations, directed link creation, whole/partial shipments, Window closure, transport/save/reload, final native reopen and delivered-history selection without direct Hatifect automation calls.

## Validation

Fast semantic-driver regressions:

```bash
python3 -m unittest discover -s tools/tests -p 'test_semantic*.py'
```

Repository tooling gate:

```bash
./tools/hatifect-test tools
./tools/hatifect-check
```

Host-required acceptance on macOS:

```bash
./tools/hatifect-live-prepare
./tools/hatifect-smoke flow.ui.player.input
```

A green host-free CI run proves the engine/tooling gates, not the Stardew/SMAPI product path. The live run remains the acceptance proof for native UI behavior.
