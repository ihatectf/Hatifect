import importlib.util
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
DRIVER_PATH = ROOT / "tools" / "live-harness" / "macos_native_input_driver.py"
RUNNER_PATH = ROOT / "tools" / "hatifect-live-runner"
UI_OBSERVER = ROOT / "Hatifect UI" / "Hatifect.UI.Stardew" / "Diagnostics" / "UiWindowInputObserver.cs"
FLOW_SEQUENCE = ROOT / "Hatifect Flow" / "Diagnostics" / "FlowPlayerNativeSequence.cs"

SPEC = importlib.util.spec_from_file_location("hatifect_macos_native_input_driver", DRIVER_PATH)
assert SPEC is not None and SPEC.loader is not None
DRIVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DRIVER)


class MacOsNativeInputDriverTests(unittest.TestCase):
    def test_controller_is_external_and_never_uses_hatifect_automation_backdoors(self) -> None:
        body = DRIVER_PATH.read_text(encoding="utf-8")
        self.assertIn("CGEventPost", body)
        self.assertIn("kCGHIDEventTap", body)
        self.assertNotIn("ActionAutomation", body)
        self.assertNotIn("InsertAutomationText", body)
        self.assertNotIn("FlowNetworkCommand(", body)
        self.assertNotIn("FlowSendCommand(", body)
        self.assertNotIn("OpenPlayerNetwork(", body)

    def test_runner_starts_companion_only_for_exact_player_input_scenario(self) -> None:
        body = RUNNER_PATH.read_text(encoding="utf-8")
        self.assertIn('[[ "$scenario" != "flow.ui.player.input" ]]', body)
        self.assertIn('exec python3 "$user_session_runtime"', body)
        self.assertIn('macos_native_input_driver.py', body)
        self.assertIn('.cancel-requested', body)
        self.assertIn('native_driver_exit', body)
        self.assertIn('runtime_exit == 0', body)
        self.assertIn('native input companion did not complete successfully', body)
        ordinary_exec = body.index('exec python3 "$user_session_runtime"')
        companion = body.index('say "Native input: state-driven macOS Quartz companion"')
        self.assertLess(ordinary_exec, companion)

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

    def test_quartz_backend_is_never_loaded_at_module_import_on_non_macos_ci(self) -> None:
        with mock.patch.object(DRIVER.platform, "system", return_value="Linux"):
            with self.assertRaisesRegex(DRIVER.DriverError, "requires macOS"):
                DRIVER.QuartzInput()


if __name__ == "__main__":
    unittest.main()
