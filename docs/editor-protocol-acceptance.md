# Приёмка обмена данными с редактором

Статусы и следующие шаги в этом техническом отчёте относятся к указанным сборкам. Текущая очередь — в [плане разработки](ROADMAP.md).

## «Обмен данными с редактором» (подэтап a) — initialization requirements

Implementation: [`f677f69`](https://github.com/ihatectf/Hatifect/commit/f677f69b4335df07828216cca9746c5f07442bd9), published. Owner: UI Tooling protocol session. Base: `08ecdb94102fb7311c1fe21eabc9843f7e4888ef`; branch `feature/tooling-protocol-negotiation`. «Типы и связи данных интерфейса» metadata v2 is present in this base; «Адаптация интерфейса к окружению» owner acceptance is recorded in `4806ce73c2bb3c43990201fb8eaf7e377343668a` on `feature/ui-u04-recovery`. This checkpoint does not substitute for combined release acceptance.

Previously, initialization advertised binding schema versions but ignored client protocol requirements. The session now validates optional `protocolVersion` and `requiredCapabilities` before importing bindings or changing state. It advertises provisional protocol version 0 independently from binding schemas 1/2 and runtime/package versions. Existing clients may omit both fields. Unsupported or malformed requirements return `Invalid params`; a corrected initialize can succeed in the same session. An over-budget initialization response also leaves the session uninitialized.

The contract is additive and internal to tooling. No runtime public API, package version, persistence format, compiler semantics, game adapter or CA boundary changes. Validation occurs only during initialization and is bounded to 32 names of 128 UTF-16 units. No new dependency or per-frame work is introduced.

### Verification plan and evidence

Focused scope: one protocol session class, using existing xUnit 2/VSTest conventions. First run the session regressions, then canonical C, then inspect the final source/assertion/documentation diff.

| Requirement | Evidence |
|---|---|
| Independent protocol version and legacy/explicit compatible initialization | `CompatibleProtocolRequirementsAdvertiseIndependentVersionAndActivate` |
| Unsupported version/capability, wrong types, duplicates and exact case fail before activation; corrected retry succeeds | `IncompatibleProtocolRequirementsRejectBeforeActivationAndPermitRetry` |
| Bounded capability request | `ProtocolRequirementBudgetsRejectBeforeInspectingEntries` |
| Oversized initialization response is not partial success and can be retried | `NegotiationResponseOverBudgetDoesNotActivateAndCanRetry` |

Focused command: `rtk proxy env DOTNET_gcConcurrent=0 ${HOME}/.dotnet/hatifect-x64-8/dotnet test 'Hatifect UI/tests/Hatifect.UI.Tooling.Tests/Hatifect.UI.Tooling.Tests.csproj' --configuration Release --filter FullyQualifiedName~ToolingProtocolSessionTests --logger trx --results-directory artifacts/t01/negotiation`. PASS: 24 passed, 0 failed, 0 skipped. The subsequent canonical C run includes the final assertion refinement.

Canonical C: **PASS**, `artifacts/validation/run-t64clr8f`: 1,622 .NET tests and 381 Python tests, zero failures/skips; architecture, public API baseline, package feed and build PASS. Command: `rtk proxy env HATIFECT_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet HATIFECT_TEST_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet DOTNET_gcConcurrent=0 ./tools/hatifect-check`. All seven TRX files were checked against individual passed results and counters. Final source/assertion/documentation diff review found no blocking issue; `git diff --check` PASS. Runtime/visual/PERF: `NOT_APPLICABLE` for this tooling-only checkpoint. Package boundary and game adapters are unchanged.

## «Обмен данными с редактором» (подэтап b) — complete compilation snapshot

Implementation: [`5e1d096`](https://github.com/ihatectf/Hatifect/commit/5e1d09663b6926968a9d5d0c91cde26cf85a49a3), published.

The new `hatifect/compilation` request reads the existing immutable compiler snapshot and returns explicit `valid`/`invalid` status with the complete diagnostics array. It requires URI, text version, binding revision and the diagnostic result ID. Text edits, binding refreshes and close/reopen invalidate old identities even when a text version is reused. Invalid source is a complete result; invalid/stale requests and oversized responses return an error without a partial result. The additive `compilation` capability advertises this operation independently of the protocol version.

This is a single-document compilation contract, not multi-asset build or planning acceptance. No second compiler pipeline, runtime activation, package dependency or API version change is introduced. The request is serialized with document/binding mutations by the existing session gate; it uses the existing bounded response serializer and retains no new state.

| Requirement | Evidence |
|---|---|
| Valid, semantic-invalid and syntax-invalid fixtures agree with compiler; diagnostic ranges retained | `CompleteResultAgreesWithCompilerAndDiagnosticSpans` |
| Wrong version/binding revision/result ID returns no result and preserves snapshot | `MismatchedSnapshotRejectsWithoutResultOrMutation` |
| Edit, binding refresh, close and reopen invalidate prior identity | `TextBindingAndReopenTransitionsInvalidateOldCompilationIdentity` |
| Oversized response returns only an error and permits a complete retry | `OversizedCompilationReturnsOnlyErrorAndRetainsSnapshotForRetry` |
| Active request and all snapshot fields are mandatory | `CompilationRequiresActiveRequestAndAllSnapshotFields` |

Tooling suite: PASS 156, zero failures/skips, before the final lifecycle/required-fields case. Command: `rtk proxy env DOTNET_gcConcurrent=0 ${HOME}/.dotnet/hatifect-x64-8/dotnet test 'Hatifect UI/tests/Hatifect.UI.Tooling.Tests/Hatifect.UI.Tooling.Tests.csproj' --configuration Release --logger trx --results-directory artifacts/t01/compilation`. Canonical C with the final case: **PASS 1633 .NET +381 Python**, no failures/skips, `artifacts/validation/run-nos_6yed`, using the same C command as «Обмен данными с редактором» (подэтап a). Seven TRX files were audited against all individual results and counters. Final Tooling suite contains 157 passing cases. Source/contract/assertion second pass and `git diff --check` PASS. Runtime/visual/PERF are NOT_APPLICABLE for this tooling-only change.

## «Обмен данными с редактором» (подэтап c) — detached planning input

Implementation: [`b3ec3fa`](https://github.com/ihatectf/Hatifect/commit/b3ec3fa140c6be24b150d44a1e1c8befcaff3df0), published.

Planning now owns `UiPlanningInput` and `UiPlanningElement`: immutable owner, presented order, capabilities and explicit collection source-kind. `Capture(experience)` exports actual planner facts without source reads/subscriptions or callback retention. Public `PlanInput` and the existing `Plan(experience)` share one `PlanCore`. The original entrypoint uses value-type views, not per-call detached arrays; its signature is unchanged. Detached inputs reject empty sets, null/duplicate/foreign/self element identities and invalid capability IDs.

| Requirement | Evidence |
|---|---|
| Live/detached generated and explicit plans preserve full trace, order and collection recipe | `DetachedAndExperiencePlansPreserveOrderRecipesAndFullTrace` |
| Legacy collection source-kind survives missing DataType | `LegacySelectCollectionKeepsListAndRecipeWithoutGuessingFromDataType` |
| Actual rejected candidates agree with production planner | `DetachedRejectionPreservesRealCandidatesAndDoesNotReturnPartialPlan` |
| Caller collections are copied; invalid owner/element/capability identities rejected | `DetachedInputCopiesCallerCollectionsAndRejectsInvalidIdentities` |
| No source reads or subscriptions | `CaptureAndDetachedPlanningNeverReadOrSubscribeToSource` |
| Auxiliary sources stay outside the presented input | `CaptureExcludesAuxiliarySourcesFromPlanning` |

Planning suite PASS 140 with zero failures/skips, `artifacts/t01/detached-planning-final`. Command: `rtk proxy env DOTNET_gcConcurrent=0 ${HOME}/.dotnet/hatifect-x64-8/dotnet test 'Hatifect UI/tests/Hatifect.UI.Planning.Tests/Hatifect.UI.Planning.Tests.csproj' --configuration Release --logger trx --results-directory artifacts/t01/detached-planning-final`. Earlier attempts found two test compilation mistakes (friend access and typed fixture interface) and an incorrect trace-count assertion; these were corrected without changing planner policy or weakening existing tests.

Independent read-only review found missing foreign-owner and empty-input validation; both were fixed with regressions and re-reviewed. The reviewer independently checked all 140 TRX results and closed the finding. The performance scan in `artifacts/t01/planning-performance-scan.json` records 4/4 sealed classes, no hot-path LINQ, blocking async, string slicing, regex or serializer patterns. The planner retains its existing four List and four Dictionary allocation sites; cold Capture has one LINQ projection. Existing performance budgets remain unchanged. Canonical C **PASS 1640 .NET +381 Python**, seven TRX audited, `artifacts/validation/run-e9qmbc58` (same C command as «Обмен данными с редактором» (подэтап a)). Isolated consumer **PASS90**, 44 projected files, 8 producer/package DLL pairs verified, UI source absent and 2 CA deployment DLLs; retained workspace `hatifect-ui-ca-isolated.kl__xw54`, audit `artifacts/t01/isolated-ca-audit.json`, archive `artifacts/t01/isolated-ca-kl__xw54.tar.gz`. Command: `./tools/hatifect-isolated-ui-ca --keep` with the same SDK/runtime overrides and the installed game references. Ordinary-GC performance verification **PASS2**, `artifacts/t01/planning-default-gc`: 600 samples per case; normal p95/p99 0.015459/0.272125 ms and 8653.41 B/plan, fallback 0.016291/0.021292 ms and 11457.41 B/plan; unchanged limits 2/4 ms and 16384 B. Command: `rtk proxy env -u DOTNET_gcConcurrent ${HOME}/.dotnet/hatifect-x64-8/dotnet test` for the Planning test project with `--configuration Release --no-build --filter FullyQualifiedName~EnvironmentPlanningPerformanceTests --logger trx --results-directory artifacts/t01/planning-default-gc`. No blocking source/diff review findings remain.

No project dependency or package version changed. Planning.Tests receives friend access to assert actual collection recipes. Runtime/native visual acceptance is not claimed; this checkpoint does not expose a plannerTrace protocol operation yet.

## «Обмен данными с редактором» (подэтап d) — actual server planner trace

Implementation: [`3ffc627`](https://github.com/ihatectf/Hatifect/commit/3ffc627eb8d76393187a69522865e915de3ce482).

`hatifect/plannerTrace` uses the same required `{textDocument:{uri,version},bindingRevision,resultId}` identity as compilation. It additionally requires `hostKind`, an explicit `environment`, and `planningMetadata`. The optional internal provider keeps Tooling Language/Semantics-only; Server composes Planning/Experience/Semantics. Only a session with a provider advertises `plannerTrace` and accepts that required capability.

Planning metadata schema 1 contains `ownerId` and ordered `elements` with `id` and boolean `isCollection`. Every presented binding must occur exactly once; v2 graph order is authoritative. Capabilities come from the immutable compiler snapshot. `UiPlanningMetadataJson.Export(experience)` in DevTools captures real order/source-kind without reading values or subscribing. Metadata is request-scoped and never changes workspace bindings.

Environment fields are `width`, `height` (logical units), `scale` (all finite positive), exact named `inputMode`, nonempty `locale` (maximum 128 characters), semantic `theme`, and boolean `reducedMotion`/`highContrast`. `hostKind` must be a named supported host. Origins are `Tooling request`; profile is computed by the actual host policy, so scale does not divide logical width again.

Results are complete `planned`, `planning-rejected` or `compilation-invalid` outcomes. All contain snapshot identity, diagnostics, nullable plan and decisions. A planned result includes experience, pattern, host kind/profile, ordered element regions/presentations and their decisions. Decisions preserve code, element, message, candidate and nullable source with sourceName/start/length/line/column. Rejection preserves the actual planner's decisions and returns no partial plan. Invalid compilation never calls the provider. Stale/malformed requests are protocol errors; output budget overflow returns only an error and retains the snapshot for retry.

The framed Server transcript obtains an opaque resultId from the running server and compares actual trace fields with direct Planning on explicit/generated/rejected/invalid fixtures. It checks legacy collection source-kind and nonalphabetical order, Compact at logical width 719 and scale 2, compiler diagnostics and stale/invalid metadata retry. Protocol boundary tests cover missing/foreign/duplicate elements, graph order, schema, environment, host and snapshot validation, compiler-only unsupported capability and bounded responses. DevTools tests cover export order/source-kind without source access.

Canonical C **PASS 1663 .NET +381 Python**, no failures/skips, `artifacts/validation/run-1ck95mlm`; seven TRX individually audited in `artifacts/t01/planner-canonical-audit.json`. Final Server suite **PASS14**, Tooling **PASS175**, DevTools **PASS10**. C command uses the same SDK/GC overrides as «Обмен данными с редактором» (подэтап a). An earlier Server run found a new test assertion looking for provenance at the root instead of element decisions; the assertion was corrected without changing planner behavior. Source/contract/diff second pass is recorded in `artifacts/t01/planner-source-review.json`. Isolated consumer P **PASS90**, 44 projected files, 8 UI packages, 2 CA DLLs and UI source absent; retained `hatifect-ui-ca-isolated.77ztlbx3`. All 8 producer/package DLL hashes and all 90 test results were audited in `artifacts/t01/planner-isolated-audit.json`. Command: `./tools/hatifect-isolated-ui-ca --keep` with the same SDK/GC/game-reference overrides as «Обмен данными с редактором» (подэтап c). Native runtime/visual and runtime PERF are NOT_APPLICABLE to this authoring-only operation; existing runtime planning code is unchanged.

«Обмен данными с редактором» remains **IN_PROGRESS**. Editor client unsupported/partial-response acceptance remains the next step after this provider checkpoint.

## «Обмен данными с редактором» (подэтап e) — client acceptance

Implementation: [`ed53cca`](https://github.com/ihatectf/Hatifect/commit/ed53ccac9f586d36d8e49c866254a4c234fcc0da).

Public `UiToolingClient` uses the same bounded framing over borrowed streams. It verifies returned protocol/capabilities before `initialized`, synchronizes bounded versioned documents, pulls the live opaque diagnostic identity and requests complete compilation. Response validation requires JSON-RPC identity and exactly one result/error, all snapshot fields, valid/invalid status and complete diagnostics with ordered nonnegative ranges. Status and error severity must agree. Failed exchanges poison the session; no partial response can be retried as a different request. Process lifetime remains the caller's responsibility; binding refresh uses a new client session.

`ToolingClientTests` proves missing/legacy/unsupported version and capability stop initialization, eleven incomplete/error/mismatched response cases never yield compilation, and EOF prevents reuse while borrowed streams remain open. `AuthoringClientServerTests` uses the public client against actual Server framing, compares direct compiler diagnostics through valid/invalid/edit/reopen transitions, checks binding revision and fresh result IDs, and shuts down cleanly.

Focused Tooling **PASS191**, Server **PASS15**. Canonical C **PASS1680 .NET +381 Python**, no failures/skips, `artifacts/validation/run-xugrzumh`; seven TRX audited in `artifacts/t01/client-canonical-audit.json`. Source/contract second pass is recorded in `artifacts/t01/client-source-review.json`. The C command uses the same SDK/GC overrides as «Обмен данными с редактором» (подэтап a). Isolated consumer **PASS90**, 44 projected files, 8 packages and 2 CA DLLs; UI source absent. Retained workspace `hatifect-ui-ca-isolated.a74tmff7`; all producer/package DLL pairs and TRX outcomes audited in `artifacts/t01/client-isolated-audit.json`. Command: `./tools/hatifect-isolated-ui-ca --keep` with the same SDK/GC/game-reference overrides as «Обмен данными с редактором» (подэтап d). Native runtime/visual and runtime PERF are NOT_APPLICABLE: the client is authoring-only and changes no runtime paths. This .NET protocol client satisfies the client-side «Обмен данными с редактором» boundary; installing a VS Code/JetBrains extension, preview and broader editor workflow remain «Предпросмотр интерфейса»/«Работа автора в редакторе» scope.

### Original «Обмен данными с редактором» criterion audit

| Required behavior | Current implementation and behavioral evidence |
| --- | --- |
| Common symbols/types/capabilities metadata | `UiBindingContextJson`; public export/import test verifies identities, capabilities, detached ownership and direct compilation. SemanticGraphMetadataTests verifies independent aliases, typed inputs, presented/auxiliary membership, relations and source provenance. |
| Definitions/references | EditorDefinitionTests verifies exact consumer source coordinates and no invented coordinates; EditorReferenceTests verifies cross-document identity, declaration flags, token-sized ranges and owner isolation. |
| Diagnostics and source spans; compiler agreement on valid/invalid fixtures | CompilationProtocolTests directly compares compiler diagnostics/ranges and snapshot identity. Public client/actual Server integration independently compares valid, invalid, edited and reopened documents. |
| Actual planner trace | PlannerTraceServerTests compares real planner decisions, candidates, spans, ordered elements and host profile through framed Server, including generated, explicit, rejected and compiler-invalid cases. |
| Protocol version separate from runtime API | «Обмен данными с редактором» (подэтап a) negotiation advertises integer0 and capabilities independently; frozen runtime API baseline check passes. |
| Client detects unsupported capability | Public UiToolingClient checks returned advertisement before initialized. Missing/legacy/version/capability fixture tests verify no initialized/compilation request escapes. |
| Client never treats partial response as successful compilation | Public client rejects incomplete snapshot/status/diagnostics, server errors, contradictory status, wrong response/snapshot identity and malformed ranges. EOF prevents reuse; actual valid/invalid outcomes are verified against the compiler. |

All listed behavioral suites are included in the current canonical C run. Isolated package verification is also PASS. «Типы и связи данных интерфейса» is present in the base graph; «Адаптация интерфейса к окружению» owner closure on `04def9e` was checked against the common `ui-environment-acceptance.md` and its exact scope. «Обмен данными с редактором» (подэтап e) is published in the owner task branch. **«Обмен данными с редактором» owner acceptance: DONE** for the original criteria above. Whole-goal completion and develop integration are not inferred from this audit.

## «Обмен данными с редактором» common integration and negotiation error review

The owner handoff `5a2e47500794bcae28f95ff3e2554e87eb07a63a` was merged into common «Действия и их завершение» candidate `82c8ef42b774f3900022cbaf1653b387d763a91c` by `cefe16b76b46e9f32806ad9e3e1ad2d892ea6f64`. Project model policy `79e65e80d3d44d681d8a502a994949963e515e54` was applied as `f4247431b55107add14e5a9945c63e43a343c7ba`. No merge conflicts, version changes or changes to the incoming production postimages were needed. The integration audit checks 76 exact source/config/test postimages; the three combined architecture/development/roadmap documents were reviewed separately. The task branch is `feature/tooling-common-acceptance`.

Independent review found one P2 in the authoring documentation: a conforming server can reject an unknown required capability before returning an advertisement. Such a JSON-RPC error is preserved as `UiToolingRequestException`, whereas `NotSupportedException` describes insufficient support in a successful response. The documentation now distinguishes these outcomes; it does not classify every `Invalid params` error as incompatibility because malformed metadata uses the same code. The protocol and public implementation are unchanged. The same independent reviewer inspected the final documentation/test diff and actual Server16 TRX, closed the P2 and reported no additional findings. Final source/diff review and `git diff --check` pass.

| Requirement | Evidence |
| --- | --- |
| Client understands an actual server rejection of unsupported capability and does not activate/reuse the failed session | `PublicClientPreservesServerRejectionOfUnsupportedCapabilityAndCannotBeReused`: real framed Server, code -32602, diagnostic reason, inactive capability query, rejected retry, canceled server exit and empty stderr |
| Focused real Server regression | `rtk proxy env ./tools/hatifect-test --project 'Hatifect UI/tests/Hatifect.UI.Tooling.Server.Tests/Hatifect.UI.Tooling.Server.Tests.csproj'`: PASS16, no failures/skips, `artifacts/validation/run-6kid15mv`; individual TRX outcomes audited |
| Final canonical C including the new regression | `rtk proxy env ./tools/hatifect-check`: PASS1700 .NET +388 Python, no failures/skips, `artifacts/validation/run-mlv_9goq`; all seven TRX audited in `artifacts/integration/c-final-audit.json` |
| Full graph on frozen integration production source f424743 | `rtk proxy env ./tools/hatifect-check --platform`: PASS2028 .NET +388 Python, no failures/skips, `artifacts/validation/run-4rpx4jnq`; all ten TRX audited |
| Isolated consumer on f424743 | `rtk proxy env ./tools/hatifect-isolated-ui-ca --keep`: retained output audit PASS101, eight exact package/producer DLL pairs, 46 projected files, two CA deployment DLLs, manifest present, UI source absent and no absolute path leaks |
| Project tooling/configuration | Explicit `rtk proxy env ./tools/hatifect-test tools`: PASS388 (`run-ukcwjxmh`); portable portable tooling audit: PASS |

The isolated workspace is `hatifect-ui-ca-isolated.ddvoau8a` under the host temporary directory. Its post-run audit is `artifacts/integration/p-audit.json`; the final command stdout was unavailable after context truncation, so the audit independently rechecked retained TRX outcomes, package bytes, deployment and isolation conditions. G/P precede the documentation/test-only correction; the final C includes that correction. No new production source or package boundary was introduced after G/P, and their counts are not relabeled as final-C counts. Runtime/native visual and runtime PERF are NOT_APPLICABLE to «Обмен данными с редактором» authoring changes; common «Действия и их завершение»/alpha.47 runtime acceptance remains separately owned.


Next: publish the reviewed task candidate with this explicit host-check limitation; complete shared «Действия и их завершение» runtime acceptance before «Управление диагностической перевозкой», and retain «Общие компоненты, темы и подсказки»/«Подготовка обновления интерфейса» dependencies for «Предпросмотр интерфейса».
