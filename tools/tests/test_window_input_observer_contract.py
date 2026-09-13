import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
OBSERVER = ROOT / "Hatifect UI" / "Hatifect.UI.Stardew" / "Diagnostics" / "UiWindowInputObserver.cs"
RUNTIME_BOUNDARY = ROOT / "Hatifect UI" / "Hatifect.UI.Stardew" / "Semantic" / "UiSemanticStardewMenu.RuntimeObservation.cs"
TERMINAL_MENU = ROOT / "Hatifect UI" / "Hatifect.UI.Stardew" / "Semantic" / "UiSemanticStardewMenu.cs"


class WindowInputObserverContractTests(unittest.TestCase):
    def test_ordinary_observer_never_enters_terminal_invocation_boundary(self) -> None:
        observer = OBSERVER.read_text(encoding="utf-8")
        boundary = RUNTIME_BOUNDARY.read_text(encoding="utf-8")
        terminal = TERMINAL_MENU.read_text(encoding="utf-8")

        self.assertGreaterEqual(observer.count("menu.CaptureRuntimeContext()"), 2)
        self.assertNotIn("menu.CurrentSection", observer)
        self.assertNotIn("CaptureInspectionContext().Runtime", observer)
        self.assertIn(".Session.Root", boundary)
        self.assertNotIn(".CurrentInvocation", boundary)
        self.assertIn("return (host.CurrentInvocation, host.Session.Root);", terminal)


if __name__ == "__main__":
    unittest.main()
