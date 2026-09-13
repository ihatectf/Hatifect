"""Delayed pointer observations must never train a screen-coordinate offset."""
import copy
import importlib.util
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
PATH = ROOT / "tools/live-harness/macos_native_input_driver.py"
SPEC = importlib.util.spec_from_file_location("native_pointer_motion_under_test", PATH)
DRIVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DRIVER)


def frame(number, pointer=(494, 65), **changes):
    result = dict(completedFrame=number, visible=True, surfaceEpoch=1,
                  experience="Example/window", acceptedSceneVersion=3,
                  renderedSceneVersion=3, frameVersion=2, renderedFrameVersion=2,
                  renderSequence=number, pointer=dict(zip(("x", "y"), pointer)),
                  viewport=dict(width=1470, height=956),
                  window=dict(position=dict(x=0, y=0),
                              clientBounds=dict(x=0, y=0, width=1470, height=956)))
    result.update(changes)
    return result


class ScriptedController(DRIVER.Controller):
    """Only evidence/platform I/O is faked; the real motion implementation is used."""
    def __init__(self, frames):
        self.frames = copy.deepcopy(frames)
        self.reads = 0
        self.moves = []
        self.records = []
        self.offset_x = self.offset_y = 0.0
        self.backend = SimpleNamespace(move=lambda x, y: self.moves.append((x, y)))

    def latest(self, **_kwargs):
        result = self.frames[min(self.reads, len(self.frames) - 1)]
        self.reads += 1
        return copy.deepcopy(result)

    def _record(self, action, **details):
        self.records.append((action, details))

    def _wait(self, description, predicate, **_kwargs):
        for _ in range(24):
            result = predicate()
            if result:
                return result
        raise DRIVER.DriverError("Timed out waiting for " + description)


class NativePointerMotionTests(unittest.TestCase):
    def test_delayed_previous_position_does_not_create_negative_screen_correction(self):
        # The exact transition from request 955548d4. The first newer draw still
        # describes the old pointer; a later snapshot acknowledges the posted move.
        driver = ScriptedController([
            frame(205, (735, 358)), frame(206, (735, 358)),
            frame(206, (735, 358)), frame(207), frame(208),
        ])
        driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])
        self.assertEqual((driver.offset_x, driver.offset_y), (0, 0))
        self.assertGreaterEqual(driver.reads, 5)

    def test_repeated_snapshot_cannot_confirm_arrival_twice(self):
        driver = ScriptedController([frame(10, (735, 358)), frame(11)])
        with self.assertRaisesRegex(DRIVER.DriverError, "Timed out"):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_persistent_mismatch_fails_without_posting_a_guessed_correction(self):
        driver = ScriptedController([frame(i, (735, 358)) for i in range(10, 40)])
        with self.assertRaises(DRIVER.DriverError):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])
        self.assertEqual((driver.offset_x, driver.offset_y), (0, 0))

    def test_transient_arrival_then_departure_does_not_confirm(self):
        driver = ScriptedController([
            frame(10, (735, 358)), frame(11),
            *[frame(i, (735, 358)) for i in range(12, 40)],
        ])
        with self.assertRaises(DRIVER.DriverError):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_surface_replacement_aborts_motion(self):
        driver = ScriptedController([frame(10), frame(11, surfaceEpoch=2), frame(12, surfaceEpoch=2)])
        with self.assertRaisesRegex(DRIVER.DriverError, "surface"):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_window_movement_aborts_without_recalibration(self):
        changed = frame(11)
        changed["window"]["position"]["x"] = 300
        driver = ScriptedController([frame(10), changed, {**changed, "completedFrame": 12}])
        with self.assertRaisesRegex(DRIVER.DriverError, "geometry"):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])


    def test_scaled_translated_projection_retains_explicit_fixed_offset(self):
        samples = [frame(i, (250, 125)) for i in range(1, 4)]
        for sample in samples:
            sample["viewport"] = dict(width=1000, height=500)
            sample["window"] = dict(position=dict(x=100, y=50),
                                    clientBounds=dict(x=0, y=0, width=2000, height=1000))
        driver = ScriptedController(samples)
        driver.offset_x, driver.offset_y = 7.0, -3.0
        driver.move_local(250, 125)
        self.assertEqual(driver.moves, [(607, 297)])
        self.assertEqual((driver.offset_x, driver.offset_y), (7, -3))

    def test_negative_global_coordinates_are_allowed_with_declared_window_origin(self):
        samples = [frame(i) for i in range(1, 4)]
        for sample in samples:
            sample["window"]["position"] = dict(x=-1000, y=-500)
            sample["window"]["clientBounds"].update(x=20, y=30)
        driver = ScriptedController(samples)
        driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(-486, -405)])

    def test_new_draws_at_old_position_remain_pending_until_motion_arrives(self):
        samples = [frame(i, (735, 358)) for i in range(1, 15)]
        driver = ScriptedController(samples + [frame(15), frame(16)])
        driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])
        self.assertEqual(driver.reads, 16)

    def test_matching_position_from_before_move_is_not_acknowledgement(self):
        driver = ScriptedController([frame(10)] * 24)
        with self.assertRaises(DRIVER.DriverError):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_repeated_render_sequence_cannot_confirm_two_distinct_renders(self):
        driver = ScriptedController([frame(10), *[frame(i, renderSequence=11) for i in range(11, 40)]])
        with self.assertRaises(DRIVER.DriverError):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_accepted_but_unrendered_pointer_does_not_confirm_arrival(self):
        driver = ScriptedController([frame(10), *[frame(i, renderedFrameVersion=1) for i in range(11, 40)]])
        with self.assertRaises(DRIVER.DriverError):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_temporary_unrendered_observation_resets_confirmation(self):
        driver = ScriptedController([
            frame(10), frame(11), frame(12, renderedFrameVersion=1), frame(13), frame(14),
        ])
        driver.move_local(494, 65)
        self.assertEqual(driver.reads, 5)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_initial_unrendered_frame_waits_before_posting(self):
        driver = ScriptedController([frame(10, renderedFrameVersion=1), frame(11), frame(12), frame(13)])
        posted_after = []
        driver.backend.move = lambda x, y: posted_after.append(driver.reads)
        driver.move_local(494, 65)
        self.assertEqual(posted_after, [2])

    def test_invisible_semantic_frame_is_not_misclassified_as_pre_window(self):
        driver = ScriptedController([frame(10, visible=False), frame(11), frame(12), frame(13)])
        posted_after = []
        driver.backend.move = lambda x, y: posted_after.append(driver.reads)
        driver.move_local(494, 65)
        self.assertEqual(posted_after, [2])

    def test_pre_window_motion_requires_no_nonexistent_semantic_render(self):
        samples = [frame(i, visible=False, surfaceEpoch=0, experience=None) for i in range(1, 4)]
        for sample in samples:
            for key in ("acceptedSceneVersion", "renderedSceneVersion", "frameVersion", "renderedFrameVersion", "renderSequence"):
                del sample[key]
        driver = ScriptedController(samples)
        driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_invalid_local_coordinates_never_post(self):
        for point in ((float("nan"), 65), (494, float("inf")), (True, 65), ("494", 65),
                      (-1, 65), (1470, 65), (494, -1), (494, 956)):
            with self.subTest(point=point):
                driver = ScriptedController([frame(10), frame(11), frame(12)])
                with self.assertRaises(DRIVER.DriverError):
                    driver.move_local(*point)
                self.assertEqual(driver.moves, [])

    def test_invalid_transform_never_posts(self):
        for value in (None, True, "100", float("nan"), float("inf"), 0, -5):
            with self.subTest(value=value):
                sample = frame(10)
                sample["window"]["clientBounds"]["width"] = value
                driver = ScriptedController([sample])
                with self.assertRaises(DRIVER.DriverError):
                    driver.move_local(494, 65)
                self.assertEqual(driver.moves, [])

    def test_nonfinite_offsets_never_post(self):
        for value in (float("nan"), float("inf"), True, "3"):
            with self.subTest(value=value):
                driver = ScriptedController([frame(10)])
                driver.offset_x = value
                with self.assertRaises(DRIVER.DriverError):
                    driver.move_local(494, 65)
                self.assertEqual(driver.moves, [])

    def test_missing_or_nonfinite_observed_pointer_aborts_without_correction(self):
        for pointer in (None, {}, {"x": float("nan"), "y": 65}, {"x": 494, "y": True}):
            with self.subTest(pointer=pointer):
                driver = ScriptedController([frame(10), frame(11, pointer=(494, 65))])
                driver.frames[1]["pointer"] = pointer
                with self.assertRaisesRegex(DRIVER.DriverError, "pointer observation"):
                    driver.move_local(494, 65)
                self.assertEqual(driver.moves, [(494, 65)])

    def test_frame_regression_is_not_treated_as_motion(self):
        driver = ScriptedController([frame(10), frame(11), frame(10)])
        with self.assertRaisesRegex(DRIVER.DriverError, "regressed"):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_foreign_evidence_failure_is_not_swallowed_as_pointer_lag(self):
        driver = ScriptedController([frame(10)])
        read = driver.latest
        def foreign(**kwargs):
            if driver.moves:
                raise DRIVER.DriverError("foreign request identity")
            return read(**kwargs)
        driver.latest = foreign
        with self.assertRaisesRegex(DRIVER.DriverError, "foreign request identity"):
            driver.move_local(494, 65)
        self.assertEqual(driver.moves, [(494, 65)])

    def test_failure_retains_requested_and_observed_coordinates_in_event(self):
        driver = ScriptedController([frame(i, (735, 358)) for i in range(10, 40)])
        with self.assertRaises(DRIVER.DriverError):
            driver.move_local(494, 65)
        motion = next(details["motion"] for action, details in driver.records if action == "move-pointer-requested")
        self.assertEqual(motion["phase"], "aborted")
        self.assertEqual(motion["localPoint"], (494, 65))
        self.assertEqual(motion["screenPoint"], (494, 65))
        self.assertEqual(motion["lastPointer"], (735, 358))
        self.assertFalse(any(action == "move-pointer" for action, _ in driver.records))

    def test_real_wait_respects_outer_deadline_without_move_replay(self):
        driver = ScriptedController([frame(10), frame(11, (735, 358))])
        driver._wait = DRIVER.Controller._wait.__get__(driver)
        driver.deadline = 0.12
        now = [0.0]
        def tick(seconds):
            now[0] += seconds
        with mock.patch.object(DRIVER.time, "monotonic", side_effect=lambda: now[0]), \
             mock.patch.object(DRIVER.time, "sleep", side_effect=tick):
            with self.assertRaisesRegex(DRIVER.DriverError, "Timed out"):
                driver.move_local(494, 65)
        self.assertLess(now[0], 0.2)
        self.assertEqual(driver.moves, [(494, 65)])

if __name__ == "__main__":
    unittest.main()
