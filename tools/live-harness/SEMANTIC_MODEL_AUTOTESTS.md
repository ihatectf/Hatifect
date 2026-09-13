# Semantic-model autotest architecture

This document defines the architectural contract for Hatifect's generic semantic-model-driven live UI autotester. Operational spec syntax lives in [`SEMANTIC_TESTS.md`](SEMANTIC_TESTS.md).

## Goal

The autotester is a reusable external E2E control engine, not a Flow-specific automation script. It drives the same visible UI a player uses while choosing targets from the semantic/accessibility model rather than from recorded coordinates.

The required control loop is:

```text
semantic/accessibility observation
    -> deterministic control resolution
    -> native Quartz input
    -> fresh semantic observation
    -> observed postcondition
```

A step succeeds because the requested observable state appeared, not because an input event was emitted.

## Trust and authority boundaries

The driver is outside Stardew, SMAPI and product assemblies. It receives only checked-in scenario instructions and request-owned detached diagnostics.

Allowed authority:

- read immutable/request-bound evidence;
- resolve UI controls from semantic/accessibility state;
- activate the game application when declared by the spec;
- issue real macOS Quartz pointer, wheel, key and Unicode-text input;
- wait for read-only observations.

Forbidden authority:

- direct Flow commands;
- Hatifect action-automation APIs or equivalent product backdoors;
- mutable product/runtime references;
- arbitrary code or shell execution from scenario JSON;
- direct mutation of semantic/accessibility state;
- synthesizing or weakening product PASS evidence.

The product/harness owns acceptance. The driver owns only navigation and native user input.

## Layers

### Protocol and evidence layer

`semantic_test_driver.py` owns:

- checked-in spec identity/version/path validation;
- evidence path confinement below `diagnostics/`;
- request/scenario identity checks on every read;
- bounded variables, transforms and predicates;
- low-level compatibility operations;
- status/evidence emission.

### UI composition layer

`semantic_driver_ui.py` composes the protocol with the generic semantic interaction engine. It adds the higher-level UI operations without mutating the base protocol module's operation registry/controller at import time.

It is also responsible for ensuring raw native `text` is only possible when exactly one enabled semantic `TextField` reports focus.

### Semantic interaction layer

`semantic_interactions.py` owns:

- selector validation;
- deterministic resolution by operation intent;
- frame freshness/actionability checks;
- reveal/root-scroll/collection-scroll behavior;
- focus/fill/click/select feedback loops;
- interactive-control discovery;
- fail-closed ambiguity and stale-target handling.

### Runtime observation layer

The UI runtime publishes a read-only projection built from accessibility, interaction, scene and layout state. The live driver consumes that projection; it does not reconstruct product relationships from naming conventions.

## Observation contract

A materialized semantic element provides concrete identity and state including:

- `nodeId`: concrete observed node identity;
- `semanticId`: product semantic origin;
- `actionId`: product action identity when applicable;
- `role`: accessibility role (`TextField`, `Button`, `List`, `ListItem`, etc.);
- `name`, `value`;
- `enabled`, `selected`, `focused`;
- `bounds`, `clip`, visibility/hit-test/interactivity state;
- `itemIdentity` for list items;
- `collectionId` for owning collection;
- collection viewport/clip/current offset/maximum offset;
- set position/size when available.

Root-scroll viewport/clip/current offset/maximum offset is published at frame level.

### Stable collection identity

For virtualized collections, the runtime's item node identity is the stable item identity. The projection maps each item node to the owning `UiCollectionLayoutWindow.Collection` using runtime scene/layout relationships and publishes that collection's semantic ID as `collectionId`.

This mapping is architectural: the driver must not infer ownership from semantic-ID string prefixes, list indices, screen position or labels.

## Deterministic resolution

Selectors can constrain:

- exact `semantic` ID;
- `semanticPrefix`;
- `action` identity;
- accessible `name`;
- accessibility `role`;
- owning `collection`;
- concrete `node` identity.

Unknown selector fields are invalid.

Resolution additionally applies operation intent:

- `focus` / `fill` -> `TextField`;
- `activate` -> `Button`;
- `select` -> `ListItem`;
- collection traversal -> `List`;
- generic value inspection -> an observable value node;
- generic click -> an interactive actionable node.

There is no first-match fallback. If semantic filtering and intent leave more than one valid concrete target, the operation fails as ambiguous. Duplicate concrete node IDs invalidate the observation instead of providing an arbitrary tie-breaker.

This permits several accessibility nodes to share one semantic origin while still resolving the actual editable field or button by role/action identity.

## Frame actionability and stale-state prevention

A native action may use geometry only from an actionable rendered frame. An accepted bridge/frame acknowledgment without a newly completed/rendered semantic frame is insufficient.

After any operation that can change layout, scroll position, focus, enabled state or surface identity, the engine rereads evidence before using coordinates.

Pointer actions follow this pattern:

1. resolve a concrete target;
2. reveal it if needed;
3. move/calibrate the pointer using current geometry;
4. reread a new semantic frame;
5. verify the same concrete `nodeId`, current geometry and required state;
6. emit the native click;
7. wait for the operation-specific observed postcondition.

If the surface is replaced, the target disappears, geometry becomes invalid, or the control becomes disabled during calibration, no stale click is sent.

## Generic interaction primitives

### Focus

`focus` resolves and reveals one enabled `TextField`, performs a revalidated native click, then requires `focused=true` in a fresh observation.

A sent mouse-down/up pair is not focus proof.

### Fill

`fill` is feedback-driven, not keystroke playback:

1. resolve the exact `TextField`;
2. reveal/revalidate it;
3. establish observed focus;
4. read the observed current value;
5. send native `End`;
6. send native `Backspace` for the observed value length, requiring observed value progress;
7. require the observed value to become empty;
8. verify focus/ownership still belongs to the same enabled field;
9. send native Unicode text;
10. require the observed value to equal the requested value exactly.

Lost focus, lost native input, disabled state or missing value progress cannot report success.

### Activate / click

`activate` resolves `Button` intent, preferably by `actionId`. Generic `click` remains a compatibility primitive but uses the same semantic interaction layer.

The engine reveals and revalidates the enabled target before the native click. It does not treat the click itself as proof that a domain command completed and does not replay the click to force a desired result.

### Select

`select` targets `ListItem` and uses stable item/concrete identity. If `selected=true` is already observed, the operation is idempotently complete. Otherwise it emits one revalidated native click and waits for `selected=true`.

For an unmaterialized virtualized item, the operation requires an explicit/observed owning collection. It scans only that collection through native wheel input, using current offset/maximum-offset observations to bound traversal. Absence across the bounded collection is a real failure.

### Reveal

`reveal` never clicks a target merely to bring it onscreen. It chooses the scroll owner from the observation:

- owning nested collection when one exists;
- otherwise root scroll.

It sends native wheel input at a point owned by the intended scroll surface, rereads the frame after each wheel event, and requires measurable bounded progress. Root-scroll points avoid nested collection wheel owners.

### Discover

`discover` returns only semantic interactive controls with valid concrete identity/geometry. Decoration and static-label nodes are deliberately excluded.

### Fill form

`fillForm` preflights all requested exact semantic fields before editing any of them, then applies the same `fill` primitive field by field. It is a reusable form operation, not a scenario-specific shortcut.

## Fail-closed invariants

The engine must fail or remain pending rather than guess when any of these conditions occur:

- two or more valid interactive targets remain after resolution;
- concrete node identities are duplicated;
- a selector contains an unknown field;
- required geometry is missing or non-finite;
- the surface/target changed after pointer calibration;
- a target became disabled before input;
- a text field is not observed focused before text input;
- native text/key input produces no required semantic value change;
- a selection click is not acknowledged by `selected=true`;
- a virtualized item is absent and has no collection owner;
- collection/root scrolling makes no progress or exceeds its bound;
- only an accepted/non-rendered bridge frame is available after an input;
- a requested postcondition never appears.

These are correctness properties, not retry heuristics.

## Product acceptance

Scenario input and product acceptance are separate concerns.

The semantic driver may navigate to a control and emit exactly the intended native action. The owning scenario/runtime must still publish the domain evidence proving the product result. For example, a successful `activate` on a Send button is not shipment proof; the scenario must observe the shipment/result state it owns.

The reference `flow.ui.player.input` workflow deliberately exercises:

- production `K` entry through Quartz -> game -> SMAPI -> Flow;
- real TextField focus and native text/Backspace/Tab;
- busy-entry rejection;
- station registration;
- source/destination list selection;
- link creation;
- whole and partial transfer controls;
- save/reload/transport lifecycle;
- delivered-history selection and product-owned delivery evidence.

No step calls a Flow command or Hatifect action-automation backdoor.

## Regression obligations

Changes to the generic interaction engine should retain regression coverage for at least:

- TextField selection over labels/containers sharing semantic origin;
- ambiguity when two editable controls remain;
- duplicate concrete node IDs;
- Button action-identity resolution;
- stable ListItem identity and explicit collection ownership;
- lost pointer/native text input;
- disabled controls;
- geometry reflow and replaced surfaces;
- disabled-during-calibration behavior;
- accepted-but-not-rendered frames;
- no action replay to force product results;
- observed `selected=true` acknowledgment;
- bounded collection-only virtualization scans;
- rejection of unmaterialized items without collection ownership;
- native-wheel reveal and no-progress bounds;
- root-scroll points that avoid nested wheel owners;
- discovery filtering of decorations;
- non-finite geometry rejection;
- unknown selector-key rejection.

Run semantic regressions with:

```bash
python3 -m unittest discover -s tools/tests -p 'test_semantic*.py'
```

Then run the repository tooling/CI gates. Host-free CI does not replace the final macOS Stardew/SMAPI live acceptance.
