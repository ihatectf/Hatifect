# F12 readiness and remaining acceptance

Assessed on 2026-09-06 after Flow F20 implementation `a0520d20ea89422c06a929a6f3fca20557eb1bfe` and evidence `db7acd697b204a8b1d80d73f1060e4ad965ec63f`. The independent source review compared this Flow candidate with published develop `54e14e74a3f8bc9d40d71e7e7521c4ddf74e770d`. This is a source and acceptance assessment, not a new runtime result or an API specification for U04.

F12 depends on F11, U02 and U04 in [ROADMAP.md](ROADMAP.md). F11 is complete. U02 implementation `356e52e` / alpha.36 is published in that develop candidate. U04 remains PLANNED. The Flow task branch still has its separately verified alpha.35 package baseline; U02's published completion does not certify a new combined build. The UI owner integrates the published Flow work and owns the U04 contract. No dependency-ready F12 implementation stage is claimed here.

## Existing consumer and evidence

| Requirement | Existing source and behavior | What remains |
| --- | --- | --- |
| Read application snapshots | [ParcelExperience](../Hatifect%20Flow/Hatifect.Flow.UI.Semantic/ParcelExperience.cs) uses `IFlowApplication.ReadSnapshot`, `RevisionChanged`, `Pump()` and one `UiPublication`. It does not observe a mutable domain Parcel directly. | Retain this ownership while integrating the completed environment contract. |
| Open in a supported host | [ParcelSurface](../Hatifect%20Flow/Hatifect.Flow.UI.Semantic/ParcelSurface.cs) calls `CreateActiveMenuOverlay`. `hatifect_flow show` is the existing diagnostic entry and requires an open native menu. Public standalone `CreateSurface` and `CreateTerminal` already exist. | Choose the supported opening path for F12 and prove the real Flow surface in the game. Existing host capabilities do not themselves require a new public opening API. |
| Show empty and faulted states | `ParcelExperience.IsActive` is false for a faulted session or a missing parcel. `Project()` uses the same closed-session text for these cases. `ParcelSurface.Pump()` disposes before refreshing an inactive experience. `show` rejects a missing parcel before creating the experience. | Distinguish a visible empty/faulted projection from a retired session. Do not treat closing the surface as proof that these states are displayed. |
| Show unavailable reasons | Owner-derived availability and reason keys already project into EN/RU; fake provider mode is explicitly diagnostic. | Exercise temporary unavailability on the actual open surface without inventing a domain state or admitting an unavailable command. |
| Locale and environment | Labels and status strings choose EN/RU through a constructor-time `_russian` value. The host updates compositor locale separately. | Adopt U04's published environment pipeline so an environment change updates the same open experience coherently. Preserve identities while changing displayed text. |
| Close and change saves | Game-session closure and Saving close the parcel surface. Subscription cleanup retains a retry handle after an unsubscribe failure. | Exercise real open → update → close and A → B → A with a Flow surface present, including retired source/action references and no retained subscription. Transport-only save isolation is insufficient. |
| Package inventory | The release inventory includes Flow Semantic and the UI dependency. Release tooling enforces one owner for UI runtime DLLs. | Verify the new combined exact package candidate after integration; retain its own build/package evidence. |

Existing behavioral tests are useful foundations, with the following limits:

- [ParcelExperienceTests](../Hatifect%20Flow/tests/Hatifect.Flow.Tests/ParcelExperienceTests.cs): `LiveProjection_ExecutesTypedActionAndUpdatesLocalizedState`, `Surface_CoalescesRevisionsAndClosesWithApplication` and `FailedShowAndCleanup_RetainHandleForRetryAndRetireActions` cover projection, fake-host refresh and teardown. They do not prove an actual game surface.
- [ParcelPublicationTests](../Hatifect%20Flow/tests/Hatifect.Flow.Tests/ParcelPublicationTests.cs): `CommandResultAndCompleteReadModelAppearInOnePublicationBeforeEveryObserver` and preparation/reentry cases cover coherent publication.
- [FlowProjectionSubscriptionTests](../Hatifect%20Flow/tests/Hatifect.Flow.Tests/FlowProjectionSubscriptionTests.cs) cover subscribe-before-read, notifications during reads, construction failure, removal failure and retry.
- [FlowAvailabilityTests](../Hatifect%20Flow/tests/Hatifect.Flow.Tests/FlowAvailabilityTests.cs) cover owner reasons, diagnostic mode and EN/RU projections.

These tests and the relevant owning implementations are unchanged between the assessed Flow candidate and the published U02 develop candidate. The U02 result does not fill the empty/faulted or live-locale gaps. The five existing parcel actions are a foundation for F13; their presence does not complete F13 or its async dependency U03.

## Owning U04 dependencies

The following gaps belong to the UI framework. Flow should consume the completed contract instead of encoding its own planner or visual policy.

| UI seam | Current behavior | Required U04 result |
| --- | --- | --- |
| [UiHostContext](../Hatifect%20UI/Hatifect.UI.Planning/UiHostContext.cs) | `HostKind` and `Profile` only. | The approved viewport, scale, input, locale, theme and necessary accessibility facets. Exact new types remain the UI owner's implementation decision. |
| [UiPresentationPlanner](../Hatifect%20UI/Hatifect.UI.Planning/UiPresentationPlanner.cs) and [UiBinder](../Hatifect%20UI/Hatifect.UI.Semantics/Binding/UiBinder.cs) | Explicit/preferred/default selection; `LUI2009` checks whether capabilities overlap at all. | Coverage of all required semantics for the selected environment. Any overlap alone does not establish complete coverage. |
| [UiPresentationPlan](../Hatifect%20UI/Hatifect.UI.Planning/UiPresentationPlan.cs) | Selected decision, message and provenance. | Bounded explanations of alternatives/rejections and an explicit diagnostic or fallback result for impossible combinations. |
| [UiSemanticSurfaceService](../Hatifect%20UI/Hatifect.UI.Stardew/Hosting/UiSemanticSurfaceService.cs) and [UiHostedSemanticSurfaceSession](../Hatifect%20UI/Hatifect.UI.Stardew/Hosting/UiHostedSemanticSurfaceSession.cs) | Controller/width select profiles; compositor locale is synchronized separately. | Deliver environment changes through the same supported planning/hosting pipeline. |

## Acceptance after U04 is ready

Use the existing F12 acceptance and global UI matrix; this assessment adds no numerical budgets or product scope. Keep domain behavior in Flow Application and presentation policy in UI.

1. Open the diagnostic read-only Flow screen through the selected supported host on a fixed isolated candidate. Publish a domain change and observe one coherent snapshot update with stable identity.
2. Display meaningful temporary unavailable, empty and faulted states. Distinguish a current session's missing data/error from an actually closed or replaced session, whose handles must become inert.
3. Exercise representative EN/RU, input and scale environments through the completed U04 contract. Preserve required data; verify explicit diagnostic/fallback behavior when presentation cannot cover it. Change locale while the surface is open and verify labels and values agree.
4. Close/reopen, then run A → B → A with the Flow surface present. Retained callbacks, sources and actions from the previous session must not publish stale data or mutate the new session. Verify subscription/resource cleanup, including supported retry paths.
5. Verify Flow Semantic inclusion, exact UI dependency and one runtime DLL owner. Run applicable C/F/U/G/P and isolated RUNTIME gates on the combined source/package candidate, retaining commit, fingerprint, nonzero counts and actual surface observations.

Existing `flow.route.basic`, `flow.save.isolation` and chest lifecycle drivers prove their documented transport/session behavior. They do not cover this UI lifecycle. Read-only source review is complete; new runtime and visual evidence are NOT_APPLICABLE to this document and remain required for F12 implementation. U04 availability is the current implementation dependency; the roadmap as a whole remains unfinished.
