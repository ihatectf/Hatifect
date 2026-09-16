# Bounded polling of native Flow evidence

The `flow.ui.player.input` scenario and its macOS Quartz native-input driver have since been
removed from the live harness, so the verification command below no longer runs. This report is
retained as the historical record of the polling defect it found and fixed in
`FlowPlayerNativeSequence.Admit`, which remains in the shipped source.

## Observed request

Request `256dcac6-8947-4709-86d9-bc6953c64fde` ran source
`4f28f786318b6f145996c02df668f95a2d5ffacb`. Its supplied archive shows:

- all five native probe captures and five command journal entries with status 0,
  revision progression through 9, and no command exceptions;
- two registered stations, one link and two reserved parcels of 8 and 5 items;
- failure at step 90, `sendFive-result-reveal`, before physical transport/reload;
- a single motion request for local/screen `(439, 50)`; the same pointer was
  observed at completed frame 855, but no second fresh observation arrived;
- UI evidence ended at completed frame 855, render sequence 385, accepted/rendered
  scene 42 and frame 43. It retained 71 captures and occupied 5,203,489 bytes;
- Flow telemetry continued to update 2094 with the game active. The first four
  commands have matching visible result captures; the fifth result remains clipped
  below the viewport. No final acceptance PASS exists.

Thus the immediate failure is missing subsequent UI-frame evidence, not a new
coordinate offset or a missing mouse-down. The archive does not include profiler
samples or a stack dump: it does not establish the exclusive cause of the draw gap.

## Source defect and bounded correction

Once two parcels existed, `FlowPlayerNativeSequence.Admit` published
`close-window-for-transport` before checking the visible results. Every sixth
update it called `HasInitialProbe` and `HasCommandResults`; each reads/parses the
entire retained UI journal. On pending results it published
`await-native-visible-result-evidence`, and the next update switched back to
`close-window-for-transport`. Each publication captures a UI stamp through another
full journal parse. The stage changes defeat the existing publication throttle.

This is a synchronous update-thread amplification hazard, especially as the
retained capture journal grows. Under sustained pending final evidence with a
completed probe, a 600-update control-flow replay of the old branches performs
410 UI-journal reads and 210 stage publications. Those are source-derived operation
counts, not measured Mac timings or proof of the sole scheduling cause.

`FlowPlayerEvidencePoll` now grants at most one evidence-poll opportunity per
60 sequence updates. A poll that is not due cannot authorize acceptance. Grants
are not queued for catch-up, and no evidence document or successful result is
cached. Pending-stage publication happens only inside the granted poll. The
close-window stage is published only after all existing admission checks succeed.
The final delivered-history observer uses the same bounded cadence. Pending
publication is explicitly forced only within that budget, so a non-aligned first
poll still publishes a current heartbeat every polling interval.

All probe, exact-five-command, epoch/scene, clip/visibility, inventory, reserved
parcel, delivered-history and completed-observer predicates remain unchanged.
There is no shorter freshness requirement, larger native timeout, synthetic input,
extra command, persistence/public API change, or alteration to the UI observer.

## Validation boundary

Nine host-free xUnit tests compile the exact game-independent poll implementation
into the existing Flow test project. They cover the work budget, repeated ticks,
large jumps without read backlog, current-evidence checks rather than cached
success, exception propagation, sequence isolation and integer bounds. They do
not execute the game-dependent admission adapter.

The source integration and existing platform evidence tests require the owning
Mac's current game references:

```bash
./tools/hatifect-test flow --platform
./tools/hatifect-live-prepare
./tools/hatifect-smoke flow.ui.player.input
```

This change removes the identified polling amplification. Native acceptance must
still establish that frames continue through result reveal, transport, save/reload
and delivered-history selection. Keep the live-input PR open until that evidence
exists; host-free CI alone is not native acceptance.
