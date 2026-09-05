# Flowline D03/D04: game-save coordinated chest provider

Date: 2026-09-06. Implements the provider decision required by F14; implementation and acceptance progress are tracked in ROADMAP-STATUS.md. Core/Persistence remain independent of Stardew/SMAPI and game item types.

## Supported boundary

The current production subset is one authoritative single-player session, stationary ordinary player Chest/Big Chest containers, and exact ordinary Stardew Object items up to 999 units per admitted physical stack. XML serialization must roundtrip before admission and preserves quality, preserve type/parent and modData. Fridge, mini shipping bin, shared/special chests, held objects and third-party item subclasses are rejected by capability checks. No arbitrary modded-inventory or multiplayer write guarantee is claimed. Partial-stack transport remains required by F16 and is the next provider increment; it is not silently considered implemented by whole-stack tests.

Station binding combines an opaque GUID in chest modData with saved location/tile and the known binding. Container replacement or a conflicting identity fails access. Explicit rebind requires an available destination and rejects relocating an origin that still owns queued active cargo. Snapshot metadata contains detached descriptions/IDs; game objects and item XML do not enter the public application projection.

## Commit timeline and custody

1. Admission takes the source inventory lease, verifies the selected complete item fingerprint, assigns a new cargo identity, validates serialized materialization and admits the shipment. Before dispatch, the original physical source still contains the tagged item; the saved payload is not authority to create another physical copy.
2. The Core schedules a bounded operation and records its retained transfer intent. The save-bound port uses the transfer identity to retain Applied/Rejected results. Extraction transfers custody from the exact tagged source item to the parcel. Delivery transfers parcel custody to the exact destination physical stack.
3. Flow's payloads, station bindings, checkpoint, journal and recovery fence are written under SMAPI `flowline-v1` during Saving. Physical inventories are serialized by the same game save. BeginSave prevents new host mutations; Saved resumes. A new session token fences stale UI on reload/title.
4. A complete game-save rollback restores both the physical inventories and their Flow state. Reload never reconstructs a historical Delivered/Returned physical item from checkpoint payloads. A player taking delivery therefore cannot cause its recreation by reloading that saved state.
5. If the physical inventory callback makes the result ambiguous, the retained intent/payload remain and transport is fenced for recovery. Automatic replay is forbidden. Only a retained settled Applied/Rejected receipt can authorize reconciliation; Missing does not authorize a guess based on an empty slot.

The chosen boundary is the complete game save. It is not an independent durable commit spanning an arbitrary external database and game inventory. Replacing only one side of the saved aggregate, editing saves or arbitrary external observers that mutate/throw mid-effect are outside the automatic-recovery guarantee; ambiguity is preserved and stopped, not repaired by creating items. Restoring a known-good complete save is the user-visible fallback when no settled result exists.

## Failure matrix and actual evidence

| Boundary | Required behavior | Evidence currently available |
|---|---|---|
| Stale slot before/while lease acquisition | Conflict before cargo tagging/admission | ShipmentAuthoringTests: quantity/metadata/replacement and ItemChangedWhileAcquiringMutexRejectsOldFingerprintUnderTheLease |
| Queued operation waits for mutex | Keep reservation, intent and custody; do not consume delivery attempt | PortReadinessTests + InventoryLockTests; late grant never owns cargo authority |
| Source changes after admission | Reject extraction/cancel queued parcel, retain actual source quantity | FlowGameSessionTests.ChangedOrAlreadyAssignedSource_CannotLoseOrDuplicateThePhysicalStack |
| Destination disappears/replaced/full | Retain parcel custody and bounded retry; no write to replacement identity | FlowGameSessionTests and ChestInventoryAccessTests |
| Physical callback throws after mutation | Persist uncertainty and recovery fence; do not replay automatically | AmbiguousInventoryCallback_IsFencedAcrossSaveReload and RecoveryGameSessionTests |
| In-flight whole-game save/reload | Resume sole parcel custody and deliver once with exact item fidelity | Unit roundtrip plus actual SMAPI request c64b077c-986a-450e-87a8-8e57a7d0c9f6 on prior candidate |
| Delivery taken before save | Historical checkpoint never recreates taken item | TakenDelivery_IsNeverRecreatedFromHistoricalCheckpoint |
| Whole-save rollback before delivery | Restore source and queue together, then deliver once | RollingBackWholeGameSave_RestoresSourceAndQueuedJourneyTogether |
| Partial stack and split parcels | Conserve quantity and separate custody across reject/cancel/return/reload | Incomplete; next F16 increment |
| Process crash at game-save boundaries | No mismatch within the supported complete-game-save recovery model | Full crash/reload matrix remains unperformed; unit fault injection is not process-crash evidence |
| Multiplayer contention / arbitrary external inventory | No production writes under an unproven authority contract | Explicitly disabled in ModEntry; exploratory peer code is not an advertised capability |

Latest current source tests: `./tools/hatifect-test flow --platform`, run-nu3kvz6n, PASS 634 + 61 tests. Runtime fingerprints and refreshed candidate evidence must accompany final F16/F17 acceptance; earlier success does not certify a changed DLL.
