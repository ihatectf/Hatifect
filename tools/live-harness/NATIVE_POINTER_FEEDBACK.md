# Native pointer delivery acknowledgement

The semantic engine's native mouse input uses a bounded button lease rather than
an unobserved fixed-duration down/up pulse. This extends the internal driver
contract, not the scenario DSL or public Hatifect API.

## Observed failure

In the reported `flow.ui.player.input` run `5c41bf48-b189-4024-8978-ecd933a9cdcb`,
the Ready probe completed and the agent resolved the editable name node. The
pointer reached `(735, 356)` inside the field's bounds and clip. Both the UI
observer and Flow input counters remained at zero mouse presses/releases; the
UI continued to completed frame 1224 / render sequence 1199 with no focus.

These artifacts establish missing observed mouse-button delivery, not a stalled
renderer or ambiguous selector. They do not establish whether the original
100-ms pulse was missed during an input-pump delay or filtered below SMAPI.
The old implementation released after elapsed time even without press evidence.
The new mechanism removes that timing assumption and provides a discriminating
failure when a held down is still not observed. Native macOS acceptance remains
required; host-free tests do not establish the OS-level cause or live success.

## Protocol and ownership

`SemanticInteractions` requires a current request-validated rendered frame with
`observation.pointerPressed`, `observation.pointerReleased`, and the game-local
`pointer`. Counters are nonnegative integers, not booleans. Before a new click,
press/release counters must balance; another agent/hardware transition is not
silently attributed to the owned attempt.

The backend provides `hold_left_button(x, y)` as a context manager. It allocates
both Quartz events before posting, posts one down on entry, and attempts the
matching up on exit. It performs no pointer movement, replay or product action.
The existing Accessibility authorization and Quartz event transport are retained.
No fallback to the old fixed pulse is allowed for semantic clicks.

While holding, the engine waits at most three seconds (also capped by the
owning step/run deadline) for exactly one new observed press and no release.
Polling uses one nonblocking semantic-frame read per attempt, not a nested
20-second frame wait. A fresh same-surface rendered frame, the same concrete
control, its current enabled/visible geometry, stable screen transform and a
pointer inside its bounds/clip are required. Missing telemetry, changed identity,
extra transitions or a timeout cannot produce successful click evidence.

`native_button_lease.py` attempts release on normal exit, Python exceptions,
KeyboardInterrupt and SIGTERM, and restores the previous SIGTERM handler.
It rejects nested leases and non-owner threads. SIGKILL, process crashes and OS
refusal cannot be made safe by Python finally blocks; those remain limitations.

After up is posted, `focus` and `select` also require an observed release, then
their original focused/selected postcondition. An action may legitimately close
its surface on release, so generic `click`/`activate` do not require the old
surface to survive afterward. They still do not prove a domain command result:
the owning product/scenario's evidence remains authoritative.

## Diagnostics and regression

`lastTarget.pointerDelivery` retains the attempt phase, baseline/last counters,
local/screen point and last observed frame stamp. Events distinguish a sent down,
an observed down and the completed emission of the click pair. A missing down
reports `native mouse-down acknowledgement`; a missing up in focus/select reports
`native mouse-up acknowledgement`; absent focus after delivery reports a
TextField focus timeout. The agent never retries a click to force success.

Run:

```bash
python3 -m unittest discover -s tools/tests -p 'test_native_pointer_feedback.py' -v
python3 -m unittest discover -s tools/tests -p 'test_semantic*.py' -v
./tools/hatifect-test tools
./tools/hatifect-check
```

Regression tests cover delayed input pumping beyond the old tap interval, lost
down/up, observed delivery without focus, stale/unrendered frames, changed
target/surface/geometry, invalid counters, no replay, action-driven closure,
allocation/post failures and release on interruption. The existing semantic
interaction assertions are unchanged; their fake backend now models the lease
and observer counters rather than an instantaneous native click.

## Delayed pointer motion must not train coordinate offsets

The subsequent native run `955548d4-3fd9-4072-bf01-48fb459cd613` reached
`phase=Complete`, retained Ready/Pointer/Text/Backspace/Tab captures and filled
`src_955548`. It then stopped at `registerSource-activate`. The agent reported
local target `(494, 65)` but posted screen point `(253, -228)`; the last observed
pointer was also `(253, -228)` and press/release counts stayed `(2, 2)`.

The inherited `Controller.move_local` immediately treated any newer completed
frame as a response to its latest move. If that frame still reported the previous
position `(735, 358)`, it learned offset `(494-735, 65-358) = (-241, -293)`.
Its next move was consequently `(253, -228)`. A delayed observation of the first
move could then falsely acknowledge the second one. A deterministic regression
against the original implementation reproduces both emitted coordinates exactly.
This identifies the calibration race; the supplied excerpt does not expose the
precise scheduling of every intermediate game input pump.

The shared motion implementation now posts one movement using the existing
window/client/viewport transform and fixed compatibility offset, then waits for
the requested game-local position in two distinct newer snapshots. Visible
semantic surfaces also require matching accepted/rendered versions and advancing
render sequences. Pre-window motion uses the observer's completed frame and
geometry without demanding a semantic window that has not been opened yet.
The existing three-unit pointer tolerance is unchanged.

A delayed or unchanged pointer sample is pending, never a new calibration offset.
Missing position confirmation times out after at most four seconds, further
capped by the step/run deadline. Changed surface or geometry, regressing frames,
non-finite coordinates and out-of-viewport local targets fail closed. Negative
global screen coordinates remain valid when produced by declared window geometry.
An actual platform-transform mismatch now fails instead of being guessed away;
adding automatic calibration would require independent provenance for the samples.
No C# observer, scenario steps, focus/selection assertions or product acceptance
criteria are changed. Mouse-button lease handling is unchanged.

`move-pointer-requested.motion` retains the requested local/screen point, last
observed pointer/frame, confirmation count and failure phase. Successful
`move-pointer` events include the actual observed pointer and confirming frame.

Run the new motion regressions with:

```bash
python3 -m unittest discover -s tools/tests -p 'test_native_pointer_motion.py' -v
```

The initial six regressions fail against the previous implementation; all 24
motion tests pass after the change, including repeated/stale frames, delayed
motion, genuine transform offsets, negative monitor origin, unrendered frames,
pre-window movement, invalid geometry and the outer deadline. Five unchanged
native-backend tests also pass in the targeted source-subset run. Full repository
CI and native macOS acceptance must be tracked separately; this change does not
claim the complete Flow workflow has passed.
