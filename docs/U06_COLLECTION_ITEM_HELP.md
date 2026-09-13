# U06-i semantic collection item help

Source: `35dc94660415e2f31c6d6110b0339c4b17856e78` on local branch
`feature/u06-collection-help`. This slice is local only and has not been pushed.

## Contract

`UiSemanticCollectionItem` carries optional immutable `UiLocalizedText` help. Additive final-selector
overloads expose it from static, mutable, selectable and published collection sources while retaining
all existing constructor signatures. Published snapshots and deltas capture the value; tooltip-only
content changes advance collection and item revisions, while equivalent localized content retains the
existing snapshot.

Scene composition captures the locale and theme-owned tooltip visual. Layout resolves and measures
help only for materialized rows, stores measurements in a per-active-collection bounded LRU with a
capacity of 1,024, and publishes a constant-time stable-ID index for the visible window. Rendering
uses the retained tooltip layout and never calls text metrics. Tooltip placement, viewport clamping,
theme tokens and hover-over-focus arbitration reuse the U06-e overlay.

Selectable rows expose item help through pointer hover or keyboard/controller focus. Read-only
`Browse` rows accept hover for help without accepting press or submit. Accessibility retains the row
label as Name and publishes localized help separately as Description.

## Evidence

| Requirement | Evidence |
|---|---|
| Immutable projection, equivalent-content reuse and tooltip-only revisions | `CollectionSnapshotTests.ItemHelpIsCapturedAndAdvancesContentRevisionOnlyWhenItsTextChanges` |
| Published delta snapshot immutability | `PublishedCollectionTests.PublishedCollectionCarriesItemHelpThroughImmutableDeltaSnapshots` |
| Bounded 10,000-item materialization, RU locale, cached measurement, O(1) lookup, hover-over-focus, accessibility and render without text metrics | `TooltipTests.MaterializedCollectionItemHelpIsLocalizedIndexedAccessibleAndPremeasured` |
| Read-only hover help without activation | `TooltipTests.ReadOnlyCollectionItemHelpCanBeHoveredWithoutBecomingActionable` |
| Planning RED→GREEN | `run-u49_7phl`: compile RED with 8 missing contract members; `run-b8x4m2o0`: 147/147 PASS |
| Runtime RED→GREEN | `run-ub8bgxll`: compile RED with 7 missing runtime/layout members; `run-mzkztigw`: 699/699 PASS |
| Full UI regression | `run-7344afe2`: 10 DevTools + 147 Planning + 699 Runtime + 67 Semantics + 16 Tooling.Server + 192 Tooling = 1,131 PASS |
| Canonical source gate | `run-xj7bplfh`: 1,934 .NET + 503 Python PASS; architecture, packages, restore/build PASS |
| Isolated package consumer | `hatifect-ui-ca-isolated.dhhxog2i`: 8 UI packages, 47 projected files, 102 CA tests, 2 CA DLLs; UI source absent |
| Public contract | `verify_public_api.py`: API v1 / 7 semantic consumer types PASS; `test_semantic_contracts`: 21 PASS |

## Remaining scope

This slice does not define a tooltip display delay or validation-error help and does not add
consumer-specific collection help. Flow/CA adoption, native screenshots, physical controller input
and the full EN/RU/scale/theme matrix remain open. Those items and the remaining theme/component
families keep U06 `IN_PROGRESS`.
