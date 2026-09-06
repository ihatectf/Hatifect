# F12 readiness and remaining acceptance

Assessed on 2026-09-06 after Flow F20 implementation `a0520d20ea89422c06a929a6f3fca20557eb1bfe` and evidence `db7acd697b204a8b1d80d73f1060e4ad965ec63f`. The independent source review compared this Flow candidate with published develop `54e14e74a3f8bc9d40d71e7e7521c4ddf74e770d`. This is a source and acceptance assessment, not a new runtime result or an API specification for U04.

F12 depends on F11, U02 and U04 in [ROADMAP.md](ROADMAP.md). F11 and U02 are complete with separately recorded evidence. U04 is now **IN_PROGRESS**: checkpoint `b89c792` implements environment facets, complete capability coverage, bounded trace and Runtime Invocation over the tested alpha.43 base `f2f5a03`. The Flow task owns this bounded framework slice by coordination with the UI owner; UI retains existing host/Terminal/reload integration. GQ owns the combined alpha.45 package version authority; the owner results here remain historical alpha.43 evidence. C1489+365/G1731+365/P90 are attributed in [ROADMAP-STATUS.md](ROADMAP-STATUS.md#u04-a-environment-planning-and-invocation-foundation). The combined alpha.45 source `92f4890` now has its own C1499+366/G1772+366/P90, ordinary-GC Planning118 and fresh native compatibility/PERF acceptance; exact identities and limits are in [U04_ALPHA45_INTEGRATION.md](U04_ALPHA45_INTEGRATION.md). Native environment propagation and coherent consumer locale projection are still required; no dependency-ready F12 implementation stage is claimed here. The original alpha.35/alpha.36 comparison below remains historical evidence for its named sources.

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
| [UiHostContext](../Hatifect%20UI/Hatifect.UI.Planning/UiHostContext.cs) | Named `InEnvironment` captures validated logical viewport, scale, input, locale, theme and accessibility preferences; original profile constructors remain. | Deliver actual platform values with their origins through live hosts. |
| [UiPresentationPlanner](../Hatifect%20UI/Hatifect.UI.Planning/UiPresentationPlanner.cs) and [UiBinder](../Hatifect%20UI/Hatifect.UI.Semantics/Binding/UiBinder.cs) | `LUI2009` and direct-IR planning now require complete capability coverage; generated alternatives are deterministic and identity mismatches reject. | Verify this policy with the actual host/consumer environment matrix. |
| [UiPresentationPlan](../Hatifect%20UI/Hatifect.UI.Planning/UiPresentationPlan.cs) | Six facet explanations and a profile rule per plan; bounded candidate rejections/fallback, addressable `UiPlanningException` without a partial plan. | Connect diagnostics to the actual host pipeline; planner-only PERF does not establish native frame cost. |
| [UiSemanticSurfaceService](../Hatifect%20UI/Hatifect.UI.Stardew/Hosting/UiSemanticSurfaceService.cs) and [UiHostedSemanticSurfaceSession](../Hatifect%20UI/Hatifect.UI.Stardew/Hosting/UiHostedSemanticSurfaceSession.cs) | Controller/width select profiles; compositor locale is synchronized separately. | Deliver environment changes through the same supported planning/hosting pipeline. |

## Acceptance after U04 is ready

Use the existing F12 acceptance and global UI matrix; this assessment adds no numerical budgets or product scope. Keep domain behavior in Flow Application and presentation policy in UI.

1. Open the diagnostic read-only Flow screen through the selected supported host on a fixed isolated candidate. Publish a domain change and observe one coherent snapshot update with stable identity.
2. Display meaningful temporary unavailable, empty and faulted states. Distinguish a current session's missing data/error from an actually closed or replaced session, whose handles must become inert.
3. Exercise representative EN/RU, input and scale environments through the completed U04 contract. Preserve required data; verify explicit diagnostic/fallback behavior when presentation cannot cover it. Change locale while the surface is open and verify labels and values agree.
4. Close/reopen, then run A → B → A with the Flow surface present. Retained callbacks, sources and actions from the previous session must not publish stale data or mutate the new session. Verify subscription/resource cleanup, including supported retry paths.
5. Verify Flow Semantic inclusion, exact UI dependency and one runtime DLL owner. Run applicable C/F/U/G/P and isolated RUNTIME gates on the combined source/package candidate, retaining commit, fingerprint, nonzero counts and actual surface observations.

Existing `flow.route.basic`, `flow.save.isolation` and chest lifecycle drivers prove their documented transport/session behavior. They do not cover this UI lifecycle. Read-only source review is complete; new runtime and visual evidence are NOT_APPLICABLE to this document and remain required for F12 implementation. U04 availability is the current implementation dependency; the roadmap as a whole remains unfinished.
