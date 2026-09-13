import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
DRIVER_PATH = ROOT / "tools" / "live-harness" / "macos_native_input_driver.py"
SPEC = importlib.util.spec_from_file_location("hatifect_macos_native_input_driver_startup", DRIVER_PATH)
assert SPEC is not None and SPEC.loader is not None
DRIVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DRIVER)


class NativeInputDriverStartupRaceTests(unittest.TestCase):
    def test_null_input_telemetry_latest_is_a_transient_inactive_sample(self) -> None:
        controller = object.__new__(DRIVER.Controller)
        controller.flow = lambda: {"inputTelemetry": {"latest": None}}
        self.assertFalse(controller._game_active())
        self.assertEqual(DRIVER._field(None, "gameActive", False), False)

    def test_cli_backstop_records_unexpected_controller_exceptions(self) -> None:
        request_id = "11111111-1111-4111-8111-111111111111"
        with tempfile.TemporaryDirectory(prefix="hatifect-native-input-race.") as temporary:
            artifact = Path(temporary) / request_id
            artifact.mkdir()
            with mock.patch.object(DRIVER, "QuartzInput", return_value=object()), \
                 mock.patch.object(DRIVER.Controller, "run", side_effect=TypeError("startup race")), \
                 mock.patch.object(DRIVER, "build_parser") as parser:
                parser.return_value.parse_args.return_value = type("Args", (), {
                    "artifact_directory": str(artifact),
                    "request_id": request_id,
                    "timeout_seconds": 30.0,
                })()
                self.assertEqual(DRIVER.main(), 2)
            status = DRIVER._read_json(artifact / "diagnostics" / "native-input-driver.json")
            self.assertEqual(status["state"], "Failed")
            self.assertEqual(status["action"], "native-player-input-failed")
            self.assertIn("startup race", status["failure"])


if __name__ == "__main__":
    unittest.main()
