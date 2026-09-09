import importlib.util
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
DRIVER_PATH = ROOT / "tools" / "live-harness" / "macos_native_input_driver.py"
SEMANTIC_AGENT_PATH = ROOT / "tools" / "live-harness" / "semantic-test-agent.py"
RUNNER_PATH = ROOT / "tools" / "hatifect-live-runner"
UI_OBSERVER = ROOT / "Hatifect UI" / "Hatifect.UI.Stardew" / "Diagnostics" / "UiWindowInputObserver.cs"
FLOW_SEQUENCE = ROOT / "Hatifect Flow" / "Diagnostics" / "FlowPlayerNativeSequence.cs"

SPEC = importlib.util.spec_from_file_location("hatifect_macos_native_input_driver", DRIVER_PATH)
assert SPEC is not None and SPEC.loader is not None
DRIVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DRIVER)


class MacOsNativeInputDriverTests(unittest.TestCase):
    def test_native_backend_is_external_and_never_uses_hatifect_automation_backdoors(self) -> None:
        body = DRIVER_PATH.read_text(encoding="utf-8")
        self.assertIn("CGEventPost", body)
        self.assertIn("kCGHIDEventTap", body)
        self.assertNotIn("ActionAutomation", body)
        self.assertNotIn("InsertAutomationText", body)
        self.assertNotIn("FlowNetworkCommand(", body)
        self.assertNotIn("FlowSendCommand(", body)
        self.assertNotIn("OpenPlayerNetwork(", body)

    def test_runner_uses_semantic_spec_discovery_and_keeps_ordinary_exec_path(self) -> None:
        body = RUNNER_PATH.read_text(encoding="utf-8")
        self.assertIn('semantic_test_agent="$TOOLS_DIR/live-harness/semantic-test-agent.py"', body)
        self.assertIn('semantic_spec="$semantic_tests_root/$scenario.json"', body)
        self.assertIn('if [[ ! -f "$semantic_spec" ]]; then', body)
        self.assertIn('exec python3 "$user_session_runtime"', body)
        self.assertIn('native_input_backend="$TOOLS_DIR/live-harness/macos_native_input_driver.py"', body)
        self.assertIn('.cancel-requested', body)
        self.assertIn('semantic_agent_exit', body)
        self.assertIn('semantic test companion did not complete successfully', body)
        self.assertNotIn('[[ "$scenario" != "flow.ui.player.input" ]]', body)
        self.assertTrue(SEMANTIC_AGENT_PATH.is_file())

    def test_observers_publish_only_read_only_geometry_needed_for_pointer_projection(self) -> None:
        ui = UI_OBSERVER.read_text(encoding="utf-8")
        flow = FLOW_SEQUENCE.read_text(encoding="utf-8")
        self.assertIn("Game1.game1.Window.Position", ui)
        self.assertIn("Game1.game1.Window.ClientBounds", ui)
        self.assertIn("pointer = Pointer()", ui)
        self.assertIn("sourceScreen = Screen(_source)", flow)
        self.assertIn("destinationScreen = Screen(_destination)", flow)
        self.assertIn("emptyScreen = Screen(_standing)", flow)
        self.assertIn("Game1.GlobalToLocal", flow)

    def test_screen_projection_uses_client_to_viewport_scale_and_calibration_offset(self) -> None:
        controller = object.__new__(DRIVER.Controller)
        controller.offset_x = 7.0
        controller.offset_y = -3.0
        latest = {
            "viewport": {"width": 1000, "height": 500},
            "window": {
                "position": {"x": 100, "y": 50},
                "clientBounds": {"x": 0, "y": 0, "width": 2000, "height": 1000},
            },
        }
        x, y, sx, sy = controller._local_to_screen(latest, 250, 125)
        self.assertEqual((sx, sy), (2.0, 2.0))
        self.assertEqual((x, y), (607.0, 297.0))

    def test_visible_element_contract_requires_bounds_inside_clip(self) -> None:
        visible = {
            "bounds": {"x": 10, "y": 20, "width": 40, "height": 30},
            "clip": {"x": 0, "y": 0, "width": 100, "height": 100},
        }
        clipped = {
            "bounds": {"x": 80, "y": 20, "width": 40, "height": 30},
            "clip": {"x": 0, "y": 0, "width": 100, "height": 100},
        }
        self.assertTrue(DRIVER.Controller._fully_visible(visible))
        self.assertFalse(DRIVER.Controller._fully_visible(clipped))

    def test_physical_key_stays_down_across_multiple_input_poll_intervals(self) -> None:
        calls: list[tuple[int, bool]] = []
        posted: list[int] = []

        class App:
            @staticmethod
            def CGEventCreateKeyboardEvent(_source, keycode, down):
                calls.append((keycode, down))
                return 11 if down else 12

            @staticmethod
            def CGEventSetFlags(_event, _flags):
                pass

        backend = object.__new__(DRIVER.QuartzInput)
        backend._app = App()
        backend._post = posted.append

        with mock.patch.object(DRIVER.time, "sleep") as sleep:
            backend.key(DRIVER.K_KEY)

        self.assertGreaterEqual(DRIVER.KEY_HOLD_SECONDS, 0.05)
        self.assertGreater(DRIVER.KEY_HOLD_SECONDS, DRIVER.EVENT_GAP_SECONDS)
        self.assertEqual(calls, [(DRIVER.K_KEY, True), (DRIVER.K_KEY, False)])
        self.assertEqual(posted, [11, 12])
        self.assertEqual(
            sleep.call_args_list,
            [mock.call(DRIVER.KEY_HOLD_SECONDS), mock.call(DRIVER.EVENT_GAP_SECONDS)],
        )

    def test_quartz_backend_is_never_loaded_at_module_import_on_non_macos_ci(self) -> None:
        with mock.patch.object(DRIVER.platform, "system", return_value="Linux"):
            with self.assertRaisesRegex(DRIVER.DriverError, "requires macOS"):
                DRIVER.QuartzInput()


if __name__ == "__main__":
    unittest.main()
