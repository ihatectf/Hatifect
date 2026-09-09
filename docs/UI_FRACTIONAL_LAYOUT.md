# Fractional layout and root reachability

Status: **diagnostic candidate; final affected tests PASS; native/common acceptance pending**. Source [`6fdb045`](https://github.com/ihatectf/Hatifect/commit/6fdb0454a8639a57772bb4d8c166e8b6a4a8b4a8), based on common d881782. Full U05/U06/U07/I01 and the general geometry acceptance remain open.

## Observed failure and owning change

Actual native `flow.ui.player.ru-075`, request `e5751f10-8886-4381-ae76-e5590ddb8320`, stopped at the initial empty Network Result. The UI trace reports `nested-clip`, scene2/frame4, after one consumed root scroll: Result bottom935.2001, clip bottom935.2, root viewport bottom936. Result height20 was reduced only by the intersection to19.999939. Thus the root could reach the element, but a nested represented boundary clipped it by one float step. This was not a locale/result mismatch or proof of a native draw failure; the Flow driver failed before arming its screenshot capture.

Runtime layout now accumulates child offsets in the same local space as measurement and adds the translated origin once per child. It preserves each allocated extent rather than trimming a minimum-height text leaf to make Reveal succeed. Root maximum offset is derived from represented viewport edges, rounded outward, and checked against the actual float subtraction/addition used by arrangement. At most four additional float increments are attempted; an unrepresentable final edge rejects layout instead of claiming reachability. Both initial root offset clamping and subsequent scrolling use this same maximum.

`UiSurfaceReveal`, `UiRect.Intersect`, public API and Flow/CA consumer geometry are unchanged. The existing strict subpixel clipping test remains active. No source history, model scans or new collections are added to production update/layout; maximum correction is bounded scalar work.

## Executed evidence

Canonical scoped command:

`rtk proxy env DOTNET_gcConcurrent=0 HATIFECT_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet HATIFECT_TEST_DOTNET=${HOME}/.dotnet/hatifect-x64-8/dotnet ./tools/hatifect-test ui --platform`

- Initial generic fixture `run-zhfb3yk3` passed1049; it did **not** reproduce the native condition and is not causal evidence.
- RU/collection/empty-result fixture `run-pouds3at`: Runtime615PASS/1FAIL, count35 at1280x720. Result bottom696.00006 remained beyond viewport696 at the old maximum offset; no further scroll progress was possible.
- An intermediate shared-edge clamp was rejected and removed: `run-gd9_npis` Runtime613PASS/3FAIL, including two existing minimum-text regressions. No test was weakened to admit it.
- Local-origin/initial-maximum candidate: targeted28PASS; exact baseline production with the same strengthened test source yielded27PASS/1FAIL in `targeted-red` (handle38487, exit1).
- `run-p73fh4nu` (handle20348, exit0): **1049PASS/7actualTRX**, Runtime616, on the production candidate **before the final maximum postcondition correction**. It is retained evidence, not a full-run claim for final6fdb045.
- Final source6fdb045: `post-review-targeted` (handle44490, exit0), **78executed/78PASS**, nofailed/skipped. The filter includes SurfaceRevealTests, SemanticPresentationTests, LayoutTests, RootOverflow and Window tests. It builds the Runtime test project with SDK8, Release, no restore, shared compilation disabled and deployment disabled. The command record and actual TRX are retained in owner artifacts. All three new executions passed.

| Requirement | Final evidence |
| --- | --- |
| Reach represented root edge after actual float operations | `RootScrollMaximumReachesTheEdgeAfterActualFloatTranslation`: viewportY.2/height103.4, extent360; previous float offset is still insufficient |
| Reveal empty Result with RU fractional collection geometry | `FractionalNestedResultGeometryRemainsRevealable`,1707x960 and1280x720, each40stack depths |
| Do not hide clipping by shrinking text | Result bounds20, heading layout20, actual `Host.Root.Render` backend heading20, exact full clip containment |
| Preserve genuine clipping rejection/progress | Existing `ASubpixelClippedEdgeRequiresInputInsteadOfAnOptimisticSuccess` and other reveal/layout/Window cases in the final78 |

Owner raw evidence: `artifacts/fractional-clip/post-review-candidate.json` contains final2sourceSHA, commit, actualTRXSHA/counters and new test outcomes; `evidence.json` retains earlier scoped/targeted attribution. All own processes were reaped before handing the heavy slot back to GQ.

## Residual review finding and next step

Independent source review is **not a full PASS**. Its initial maximum counterexample was corrected and covered by the final test. A general, pre-existing associativity gap remains: origin993.0704345703125, allocations861.4312133789062 and348.369140625 produce child bottom2202.870849609375 versus parent bottom2202.87060546875. Baseline and candidate have the same arithmetic result; local-origin accumulation reduces cumulative drift but is not a universal containment proof.

Flow Network and CA both reach generic scene arrangement through semantic slots, collections and status elements. Native line measurement uses `lineCount * typography.Size * typography.LineHeight`; RU collection sizing and constrained slack distribution also introduce fractional allocations. The exact remaining counterexample has not been derived from a real Flow/CA snapshot, so neither its production reachability nor its absence is proven. It remains relevant to complete U05/U06 geometry acceptance and must not be silently waived.

GQ authorized a bounded **diagnostic** candidate after affected tests for a fresh RU75 run, preserving the generic gap and all original acceptance criteria; final78-tested6fdb045 was handed off on that basis. Next: common exact producer/package checks, actual RU75 Result/scroll/focus/render evidence, then the remaining profile/input matrix and a concrete generic-geometry follow-up. NativeRU75, fullC/G/P and PERF for6fdb045 remain pending.
