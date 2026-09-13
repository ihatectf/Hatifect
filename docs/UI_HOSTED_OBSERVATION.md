# Standalone Window acceptance

Status: **implementation/scoped/review PASS; common gates and native acceptance pending**. Source `9cc3881593fb546101117283b34130033c5f1748`; owner baseline follow-up `3680157` changes only the reviewed Observation documentation hash. This is a bounded prerequisite for F18/M3, not completion of U05, U06, U07 or physical input acceptance.

## Contract and ownership

The existing exact-harness Capture, Activate, Reveal and Cancel facets now accept standalone `CreateSurface(Window)` handles from their own UI service. Production automation remains disabled. Modal, Fullscreen, HUD and Terminal are not added to this acceptance path. Public signatures and API v1 are unchanged.

Host and optional Reveal APIs are sibling interfaces. Request one consumer composite and create and observe through that same proxy:

```csharp
public interface IFlowUiHostAcceptanceApi :
    IUiSemanticHostApi, IUiSemanticSurfaceRevealAutomationApi { }

IFlowUiHostAcceptanceApi api = helper.ModRegistry
    .GetApi<IFlowUiHostAcceptanceApi>("Hatifect.UI");
IUiSemanticSurfaceSession surface = api.CreateSurface(
    experience, UiSemanticHostKind.Window, new(experience.Id));
surface.Show();
UiSemanticSurfaceSnapshot snapshot = api.Observation.Capture(surface);
```

The concrete provider implements this shape. Actual SMAPI proxy mapping is a separate native requirement; compiling the composite does not prove it. No consumer reflection or native menu internals are required.

Capture reads accepted Runtime state without reading sources, synchronizing, laying out, scrolling, repairing the native menu slot or dispatching input. A Window must have been shown; an earlier rejected capture does not retire its future instance. Retired handles return the stable empty identity/lifecycle snapshot. Thread and screen checks precede live native access. Foreign service handles and replaced native owners are rejected.

The menu records completion after successful host and cursor drawing, before public Rendered callbacks. Recomposition inside a callback can accept a new scene without attributing it to the preceding draw. Existing bounded snapshot rules remain unchanged.

Activate finds one action within 4096 scene nodes, then uses at most 256 normal Tab/Shift+Tab inputs and one Enter submission. Contained Window focus searches backward after the forward boundary. Every step checks owner, active root, portals, node and action definition identity. Normal focus recomposition and successful self-closing actions are supported. Reveal reuses bounded passive root wheel input; Cancel uses normal Escape/controller-back routing, including portal dismissal.

This adds no production per-frame collection or traversal. Observation state exists only in the exact harness; ordinary drawing adds a null check. Explicit diagnostic action lookup/navigation is bounded and may allocate temporary lookup stacks.

## Managed evidence

```sh
rtk proxy env DOTNET_gcConcurrent=0 \
  HATIFECT_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet \
  HATIFECT_TEST_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet \
  ./tools/hatifect-test ui --platform
```

`run-w0pcu9qc` exited 0: **1025 passed, 0 failed, 0 skipped**, verified from seven actual TRX. Runtime executed 603, including 15 new cases in `SurfaceActionInputTests`. Other counts: DevTools 9, Planning 133, Stardew 82, Semantics 60, Tooling.Server 10, Tooling 128.

| Requirement | Evidence |
|---|---|
| Reach an action from either end; submit once; preserve completed-draw attribution | `ContainedWindowReachesAnActionFromEitherEndThroughOrdinaryInput` (two cases) |
| Missing action and rejected owner cause no input or frame change | `MissingActionAndRejectedOwnerDoNotDispatchOrChangeTheFrame` |
| Reject foreign thread, owner loss and retirement before submission | `ForeignThreadIsRejectedBeforeOwnerCallbackOrInput`, `OwnerLossAfterNavigationPreventsSubmission`, `RetirementAfterNavigationCannotActivateTheRetiredTarget` |
| Reject changed action identity while allowing ordinary recomposition | `ReplacingTheActionDuringNavigationRejectsTheNewDefinition`, `OrdinaryFocusRecompositionPreservesTheOriginalAction` |
| Stop on no progress and at 256 inputs | `NonProgressingInputTriesBothDirectionsThenStops`, `NavigationBudgetStopsEvenWhenTheInputKeepsMoving` |
| Reject portal redirection, an open portal, oversized scene and ambiguous actions | `PortalRedirectedNavigationIsRejectedBeforeSubmit`, `OpenPortalRejectsActionBeforeAnyRootOrPortalInput`, `OversizedSceneRejectsActionBeforeInput`, `DuplicateActionTargetsRejectAmbiguousInput` |
| Preserve successful self-closing action semantics | `SuccessfulActionMayRetireItsOwnHost` |

These are Runtime tests. Existing `SurfaceObservationAdmissionTests` verify production-disabled gates before hostile handle/helper access. They do not prove enabled Stardew same-service, foreign-screen, native replacement or retired-window wiring. Existing Observation/Reveal tests cover reused pure helpers; native Window coverage remains pending.

Historical results are retained. `run-1__86ft5` built successfully but VSTest could not open its local socket in the sandbox; zero completed tests is BLOCKED. The approved escalation ran the tests. `run-xsuwkmhq` executed Runtime 603 with 602 passed and one failed: the budget fixture alternated through Send. Only the fixture changed; it now proves both alternating positions are distinct text inputs before exercising 256 calls.

Common G initially failed the whole-file API baseline after reviewed XML documentation changes. Both files have identical comment-free tokens to their parent. The canonical `verify_public_api.py --write-baseline` refresh changes only hashes; the verifier and all 19 semantic contract tests pass. The common branch includes an additional ActionAutomation baseline entry: GQ updated both hashes in `3ba57ab`, while the older owner baseline updates only Observation in `3680157`. Never replace the common baseline with the owner's whole file.

Source hashes, patch, actual TRX hashes/names and historical results are retained in `artifacts/hosted-window-observation/`. Independent GQ source/test review found no remaining issue. Integration preserved ten exact postimages and merged the controller's one added `flow.ui.player` exclusion while preserving its existing `flow.ui.actions` exclusion.

## Remaining acceptance

Run combined gates and actual `flow.ui.player` on one frozen candidate: single composite SMAPI proxy, native Window, real Flow session/chest actions, fresh result frames, close/reopen and cleanup. Seeded form state is not typing evidence. Normalized diagnostic dispatch is not physical or OS-generated input.

Existing `UiNativeInputGate` observes Dogfood Inspector-to-Search: `_dogfood.AutomationMenu`, its first text field and no text field during Ready. It cannot directly validate an already open Flow Window. A subsequent UI-owned bounded observer must correlate the actual Window's semantic target, focus, bounds/clip, completed frames and SMAPI event counters. Flow owns typed results and inventory assertions. The external observer records physical versus OS-injected origin honestly.
