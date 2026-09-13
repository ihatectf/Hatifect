"""Delivery tests: a delayed polled input pump must see down before up is posted."""
from contextlib import contextmanager
import copy
import importlib.util
from pathlib import Path
import signal
from types import SimpleNamespace
import unittest
from unittest import mock

# Reuse the same semantic fixture as the existing interaction suite; assertions
# below exercise the real interaction methods, not a second implementation.
import test_semantic_interactions as fixtures

UI = fixtures.UI
ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("native_button_lease_under_test", ROOT / "live-harness/native_button_lease.py")
LEASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LEASE)
SELECTOR = {"semantic": "Example/field/name"}


class DelayedBackend(fixtures.Backend):
    def __init__(self, host):
        super().__init__(host)
        self.held = False
        self.polls = 0
        self.ack_at = 5
        self.drop_release = False
        self.drop_focus = False
        self.on_poll = None

    @contextmanager
    def hold_left_button(self, x, y):
        self.events.append(("mouse-down", x, y))
        self.held = True
        try:
            yield
        finally:
            self.events.append(("mouse-up", x, y))
            self.held = False
            observation = self.host.data["observation"]
            if observation["pointerPressed"] > observation["pointerReleased"]:
                if not self.drop_release:
                    observation["pointerReleased"] += 1
                if not self.drop_focus:
                    self.click(x, y)

    def pump(self):
        if not self.held:
            return
        self.polls += 1
        if self.on_poll:
            self.on_poll()
        if not self.drop_click and self.polls == self.ack_at:
            self.host.data["observation"]["pointerPressed"] += 1


class DelayedHost(fixtures.Host):
    def __init__(self):
        super().__init__([fixtures.element()])
        self.backend = DelayedBackend(self)
        self.virtual_seconds = 0.0
        self.wait_budgets = []
        self.freeze_frames = False

    def latest(self, **kwargs):
        self.backend.pump()
        if self.freeze_frames and self.backend.held:
            return copy.deepcopy(self.data)
        return super().latest(**kwargs)

    def _wait(self, description, predicate, **kwargs):
        self.wait_budgets.append((description, kwargs.get("seconds")))
        for _ in range(8):
            self.virtual_seconds += 0.05
            result = predicate()
            if result:
                return result
        raise UI.InteractionError("Timed out: " + description)


class PointerFeedbackTests(unittest.TestCase):
    def assert_one_balanced_attempt(self, host):
        events = [e[0] for e in host.backend.events if e[0] in {"mouse-down", "mouse-up"}]
        self.assertEqual(events, ["mouse-down", "mouse-up"])
        self.assertFalse(host.backend.held)

    def test_delayed_pump_longer_than_old_tap_gets_one_press_before_release(self):
        host = DelayedHost()
        ui = UI.SemanticInteractions(host)
        ui.focus(SELECTOR)
        self.assertEqual(host.backend.polls, 5)
        self.assertGreater(host.backend.polls * 0.05, 0.1)
        self.assertTrue(host.data["elements"][0]["focused"])
        self.assertEqual(UI.pointer_counts(host.data), (1, 1))
        self.assertEqual(ui.last_target["pointerDelivery"]["phase"], "mouse-up-observed")
        self.assert_one_balanced_attempt(host)

    def test_missing_press_is_bounded_released_diagnosed_and_never_replayed(self):
        host = DelayedHost()
        host.backend.drop_click = True
        ui = UI.SemanticInteractions(host)
        with self.assertRaisesRegex(UI.InteractionError, "mouse-down acknowledgement"):
            ui.fill(SELECTOR, "must not be typed")
        self.assert_one_balanced_attempt(host)
        self.assertEqual(ui.last_target["pointerDelivery"]["lastCounts"], (0, 0))
        self.assertFalse(any(e[0] in {"key", "text"} for e in host.backend.events))
        self.assertIn(("native mouse-down acknowledgement", UI.POINTER_ACK_SECONDS), host.wait_budgets)
        down_index = next(i for i, e in enumerate(host.records) if e[0] == "semantic-mouse-down-sent")
        self.assertFalse(any(e[0] == "semantic-click" for e in host.records[down_index:]))

    def test_missing_release_does_not_allow_text_even_if_focus_changed(self):
        host = DelayedHost()
        host.backend.drop_release = True
        with self.assertRaisesRegex(UI.InteractionError, "mouse-up acknowledgement"):
            UI.SemanticInteractions(host).fill(SELECTOR, "must not be typed")
        self.assert_one_balanced_attempt(host)
        self.assertFalse(any(e[0] in {"key", "text"} for e in host.backend.events))

    def test_mouse_delivery_without_focus_does_not_claim_focus(self):
        host = DelayedHost()
        host.backend.drop_focus = True
        with self.assertRaisesRegex(UI.InteractionError, "TextField focus"):
            UI.SemanticInteractions(host).focus(SELECTOR)
        self.assert_one_balanced_attempt(host)

    def test_press_counter_in_stale_frame_is_not_acknowledgement(self):
        host = DelayedHost()
        host.freeze_frames = True
        with self.assertRaisesRegex(UI.InteractionError, "mouse-down acknowledgement"):
            UI.SemanticInteractions(host).focus(SELECTOR)
        self.assert_one_balanced_attempt(host)

    def test_unrendered_frames_do_not_extend_the_button_hold_via_nested_wait(self):
        host = DelayedHost()
        host.backend.on_poll = lambda: host.data.update(renderedFrameVersion=0)
        with self.assertRaisesRegex(UI.InteractionError, "mouse-down acknowledgement"):
            UI.SemanticInteractions(host).focus(SELECTOR)
        self.assert_one_balanced_attempt(host)
        # The inner hold polling uses _frame(), not frame()'s default 20s wait.
        self.assertEqual(host.wait_budgets[-1], ("native mouse-down acknowledgement", UI.POINTER_ACK_SECONDS))

    def test_interruption_releases_without_replay_or_typing(self):
        host = DelayedHost()
        def interrupt():
            raise KeyboardInterrupt()
        host.backend.on_poll = interrupt
        with self.assertRaises(KeyboardInterrupt):
            UI.SemanticInteractions(host).fill(SELECTOR, "not allowed")
        self.assert_one_balanced_attempt(host)
        self.assertFalse(any(e[0] in {"key", "text"} for e in host.backend.events))

    def test_changed_surface_target_state_geometry_or_pointer_aborts_owned_press(self):
        mutations = [
            lambda h: h.data.update(surfaceEpoch=2),
            lambda h: h.data["elements"][0].update(nodeId="replacement"),
            lambda h: h.data["elements"][0].update(enabled=False),
            lambda h: h.data["elements"][0]["bounds"].update(x=400),
            lambda h: h.data["pointer"].update(x=799),
            lambda h: h.data["observation"].update(pointerPressed=2),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                host = DelayedHost()
                host.backend.on_poll = lambda: mutate(host)
                with self.assertRaises(UI.InteractionError):
                    UI.SemanticInteractions(host).focus(SELECTOR)
                self.assert_one_balanced_attempt(host)

    def test_missing_invalid_or_already_held_counters_never_press(self):
        for observation in (None, {}, {"pointerPressed": True, "pointerReleased": 0},
                            {"pointerPressed": -1, "pointerReleased": 0},
                            {"pointerPressed": 0, "pointerReleased": 1},
                            {"pointerPressed": 1, "pointerReleased": 0}):
            with self.subTest(observation=observation):
                host = DelayedHost()
                host.data["observation"] = observation
                with self.assertRaises(UI.InteractionError):
                    UI.SemanticInteractions(host).click(SELECTOR)
                self.assertEqual(host.backend.events, [])

    def test_no_fallback_to_unacknowledged_backend_click(self):
        host = DelayedHost()
        host.backend.hold_left_button = None
        with self.assertRaisesRegex(UI.InteractionError, "pointer leases"):
            UI.SemanticInteractions(host).click(SELECTOR)
        self.assertEqual(host.backend.events, [])

    def test_release_action_can_close_surface_without_replaying_activation(self):
        host = DelayedHost()
        host.data["elements"][0].update(role="Button")
        original = host.backend.click
        def close(x, y):
            original(x, y)
            host.data.update(visible=False)
        host.backend.click = close
        UI.SemanticInteractions(host).click(SELECTOR, "activate")
        self.assertFalse(host.data["visible"])
        self.assert_one_balanced_attempt(host)


class NativeLeaseTests(unittest.TestCase):
    def make_backend(self, returned=(101, 102)):
        return SimpleNamespace(
            _app=SimpleNamespace(CGEventCreateMouseEvent=mock.Mock(side_effect=returned)),
            _cf=SimpleNamespace(CFRelease=mock.Mock()), _post=mock.Mock())

    @staticmethod
    def point(x, y):
        return (x, y)

    def test_down_and_up_are_separated_by_the_observation_body(self):
        backend = self.make_backend()
        with LEASE.hold_left_button(backend, self.point, 5, 6):
            self.assertEqual(backend._post.call_args_list, [mock.call(101)])
        self.assertEqual(backend._post.call_args_list, [mock.call(101), mock.call(102)])
        backend._cf.CFRelease.assert_not_called()
        self.assertFalse(backend._left_button_lease_active)

    def test_failed_allocation_never_posts_and_releases_unposted_events(self):
        for returned, released in (((101, None), 101), ((None, 102), 102)):
            backend = self.make_backend(returned)
            with self.subTest(returned=returned), self.assertRaises(LEASE.PointerLeaseError):
                with LEASE.hold_left_button(backend, self.point, 5, 6):
                    self.fail("unreachable")
            backend._post.assert_not_called()
            backend._cf.CFRelease.assert_called_once_with(released)
            self.assertFalse(backend._left_button_lease_active)

    def test_body_failure_and_keyboard_interrupt_release_once(self):
        for error in (RuntimeError("timeout"), KeyboardInterrupt()):
            backend = self.make_backend()
            previous = signal.getsignal(signal.SIGTERM)
            with self.subTest(error=error), self.assertRaises(type(error)):
                with LEASE.hold_left_button(backend, self.point, 5, 6):
                    raise error
            self.assertEqual(backend._post.call_args_list, [mock.call(101), mock.call(102)])
            self.assertIs(signal.getsignal(signal.SIGTERM), previous)

    def test_sigterm_releases_and_restores_original_handler(self):
        backend = self.make_backend()
        previous = signal.getsignal(signal.SIGTERM)
        with self.assertRaisesRegex(LEASE.PointerLeaseError, "SIGTERM"):
            with LEASE.hold_left_button(backend, self.point, 5, 6):
                signal.getsignal(signal.SIGTERM)(signal.SIGTERM, None)
        self.assertEqual(backend._post.call_args_list, [mock.call(101), mock.call(102)])
        self.assertIs(signal.getsignal(signal.SIGTERM), previous)

    def test_post_failure_still_attempts_up_without_double_release(self):
        backend = self.make_backend()
        backend._post.side_effect = [RuntimeError("post"), None]
        with self.assertRaisesRegex(RuntimeError, "post"):
            with LEASE.hold_left_button(backend, self.point, 5, 6):
                self.fail("unreachable")
        self.assertEqual(backend._post.call_args_list, [mock.call(101), mock.call(102)])
        backend._cf.CFRelease.assert_not_called()

    def test_second_allocation_exception_releases_first_without_posting(self):
        backend = self.make_backend((101, RuntimeError("allocation")))
        with self.assertRaisesRegex(RuntimeError, "allocation"):
            with LEASE.hold_left_button(backend, self.point, 5, 6):
                self.fail("unreachable")
        backend._post.assert_not_called()
        backend._cf.CFRelease.assert_called_once_with(101)
        self.assertFalse(backend._left_button_lease_active)

    def test_handler_install_failure_cleans_both_events_before_any_input(self):
        backend = self.make_backend()
        with mock.patch.object(LEASE.signal, "signal", side_effect=ValueError("handler")):
            with self.assertRaisesRegex(ValueError, "handler"):
                with LEASE.hold_left_button(backend, self.point, 5, 6):
                    self.fail("unreachable")
        backend._post.assert_not_called()
        self.assertEqual(backend._cf.CFRelease.call_args_list, [mock.call(101), mock.call(102)])
        self.assertFalse(backend._left_button_lease_active)

    def test_worker_thread_rejected_before_allocating_or_posting(self):
        backend = self.make_backend()
        with mock.patch.object(LEASE.threading, "current_thread", return_value=object()):
            with self.assertRaisesRegex(LEASE.PointerLeaseError, "main thread"):
                with LEASE.hold_left_button(backend, self.point, 5, 6):
                    self.fail("unreachable")
        backend._app.CGEventCreateMouseEvent.assert_not_called()
        backend._post.assert_not_called()

    def test_nested_lease_and_invalid_coordinates_emit_nothing_extra(self):
        backend = self.make_backend()
        with LEASE.hold_left_button(backend, self.point, 5, 6):
            with self.assertRaisesRegex(LEASE.PointerLeaseError, "already active"):
                with LEASE.hold_left_button(backend, self.point, 5, 6):
                    self.fail("unreachable")
        self.assertEqual(backend._post.call_args_list, [mock.call(101), mock.call(102)])
        for x in (True, "5", float("nan"), float("inf")):
            backend = self.make_backend()
            with self.subTest(x=x), self.assertRaises(LEASE.PointerLeaseError):
                with LEASE.hold_left_button(backend, self.point, x, 6):
                    self.fail("unreachable")
            backend._post.assert_not_called()


if __name__ == "__main__":
    unittest.main()
