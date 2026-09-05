import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
OVERLAY_SESSION = (
    ROOT
    / "Hatifect UI"
    / "Hatifect.UI.Stardew"
    / "Semantic"
    / "UiSemanticStardewOverlaySession.cs"
)
SURFACE_SERVICE = (
    ROOT
    / "Hatifect UI"
    / "Hatifect.UI.Stardew"
    / "Hosting"
    / "UiSemanticSurfaceService.cs"
)
STARDEW_RUNTIME = (
    ROOT
    / "Hatifect UI"
    / "Hatifect.UI.Stardew"
    / "Semantic"
    / "UiSemanticStardewRuntime.cs"
)


def _method_body(source: str, signature: str) -> str:
    signature_start = source.index(signature)
    body_start = source.index("{", signature_start)
    depth = 0
    for index in range(body_start, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[body_start + 1 : index]
    raise AssertionError(f"Method body is incomplete: {signature}")


class UiSemanticSurfaceLifecycleTests(unittest.TestCase):
    def test_overlay_routes_secondary_buttons_through_runtime_modality(self) -> None:
        source = OVERLAY_SESSION.read_text(encoding="utf-8")
        pressed = _method_body(source, "private void OnButtonPressed(")
        released = _method_body(source, "private void OnButtonReleased(")

        for body in (pressed, released):
            self.assertIn("if (!CanRouteInput()) return;", body)
            self.assertIn("_input.UnhandledInput()", body)
            self.assertRegex(body, r"if \(dispatch\.Consumed\)\s*_helper\.Input\.Suppress\(e\.Button\);")
            self.assertLess(body.index("CanRouteInput()"), body.index("_input.UnhandledInput()"))
        self.assertRegex(pressed, r"else\s*\{\s*dispatch = _input\.UnhandledInput\(\);\s*\}")
        self.assertRegex(released, r"e\.Button == SButton\.MouseLeft\s*\? _input\.PointerUp\([^;]+: _input\.UnhandledInput\(\)")

    def test_unmapped_platform_input_does_not_bypass_runtime_modality(self) -> None:
        source = (OVERLAY_SESSION.parent / "UiSemanticStardewInputAdapter.cs").read_text(encoding="utf-8")
        keyboard = source[source.index("public UiPortalDispatch KeyDown(Keys key, bool shift, bool control)") :]
        gamepad = source[source.index("public UiPortalDispatch GamePad(") :]

        self.assertIn("public UiPortalDispatch UnhandledInput()\n        => _host.UnhandledInput();", source)
        self.assertNotIn("new UiPortalDispatch(false, null, null)", source)
        self.assertIn("if (key == Keys.Space) return _host.UnhandledInput();", keyboard)
        for body in (keyboard, gamepad):
            self.assertIn("_ => _host.UnhandledInput()", body)
        self.assertIn("Keys.Escape => _host.Cancel()", keyboard)
        self.assertIn("Buttons.B => _host.Cancel()", gamepad)

    def test_title_lifecycle_verifies_after_all_event_handlers(self) -> None:
        source = (ROOT / "Hatifect UI/Hatifect.UI.Stardew/Diagnostics/UiAutomatedAcceptanceController.cs").read_text(encoding="utf-8")
        on_title = _method_body(source, "internal void OnReturnedToTitle()")
        update = _method_body(source, "private void OnUpdateTickedCore()")
        verify = _method_body(source, "private void VerifyReturnedToTitleContribution()")
        execute = _method_body(source, "private bool ExecuteNamedScenario(")

        self.assertIn("_verifyReturnToTitleContribution = true;", on_title)
        self.assertNotIn("AfterReturnedToTitle!(context)", on_title)
        self.assertIn("if (_verifyReturnToTitleContribution)", update)
        self.assertIn("VerifyReturnedToTitleContribution();", update)
        self.assertIn("if (Context.IsWorldReady)", verify)
        self.assertIn("contribution.AfterReturnedToTitle!(context)", verify)
        self.assertIn("context.Complete();", verify)
        self.assertLess(execute.index("context.Complete();"), execute.index("RequestReturnToTitle();"))

    def test_lost_active_menu_identity_retires_surface_without_mutating_native_menu(
        self,
    ) -> None:
        source = OVERLAY_SESSION.read_text(encoding="utf-8")
        route = _method_body(source, "private bool CanRouteInput()")
        retire = _method_body(source, "private void RetireLostMenuContext()")
        hide = _method_body(source, "private void HideCore(bool notifyClosed)")

        self.assertRegex(
            route,
            re.compile(
                r"if \(OwnsCurrentMenuContext\(\)\) return true;\s*"
                r"RetireLostMenuContext\(\);\s*return false;"
            ),
        )
        self.assertRegex(
            retire,
            re.compile(
                r"_renderLayer == UiSemanticStardewOverlayRenderLayer\.ActiveMenu"
                r"\)\s*\{\s*HideCore\(notifyClosed: true\);\s*return;\s*\}"
            ),
        )
        self.assertNotIn("Game1.activeClickableMenu =", retire)
        self.assertIn("if (!Visible) return;", hide)
        self.assertLess(hide.index("Visible = false;"), hide.index("NotifyClosed(failures)"))

    def test_consumer_refresh_and_synchronize_retire_lost_menu_identity(self) -> None:
        overlay_source = OVERLAY_SESSION.read_text(encoding="utf-8")
        service_source = SURFACE_SERVICE.read_text(encoding="utf-8")
        context = _method_body(overlay_source, "internal bool SynchronizeMenuContext()")
        refresh = _method_body(service_source, "public void Refresh()")
        synchronize = _method_body(service_source, "public void Synchronize()")

        self.assertIn("return CanRouteInput();", context)
        for body in (refresh, synchronize):
            self.assertIn(
                "if (_shown && !overlay.SynchronizeMenuContext()) return;",
                body,
            )
            self.assertLess(
                body.index("SynchronizeMenuContext()"),
                body.index("SynchronizeState()"),
            )

    def test_hidden_surface_can_recompose_before_first_show(self) -> None:
        source = SURFACE_SERVICE.read_text(encoding="utf-8")
        refresh = _method_body(source, "public void Refresh()")
        synchronize = _method_body(source, "public void Synchronize()")

        for body in (refresh, synchronize):
            self.assertIn("_shown && !overlay.SynchronizeMenuContext()", body)
            self.assertLess(body.index("_shown &&"), body.index("SynchronizeState()"))

    def test_automated_cancel_honors_consumed_surface_dismissal(self) -> None:
        source = OVERLAY_SESSION.read_text(encoding="utf-8")
        cancel = _method_body(
            source,
            "internal void CancelForAutomatedAcceptance(",
        )

        self.assertRegex(
            cancel,
            re.compile(
                r"surfaceDismissRequested = dispatch\.Portal == null\s*"
                r"&& dispatch\.Interaction\?\.DismissRequested == true;"
            ),
        )
        self.assertIn(
            "(surfaceDismissRequested || !dispatch.Consumed) && "
            "!TryCloseFromUnhandledCancel()",
            cancel,
        )

    def test_partial_teardown_keeps_failed_cleanup_retryable(self) -> None:
        source = OVERLAY_SESSION.read_text(encoding="utf-8")
        public_dispose = _method_body(source, "public void Dispose()")
        retire = _method_body(source, "internal void Retire()")
        dispose = _method_body(source, "private void DisposeCore()")
        hide = _method_body(source, "private void HideCore(bool notifyClosed)")
        unsubscribe = _method_body(source, "private void UnsubscribeEvents()")

        for body in (public_dispose, retire):
            self.assertLess(body.index("_retireRequested = true;"), body.index("DisposeCore();"))
        self.assertLess(
            public_dispose.index("DisposeCore();"),
            public_dispose.index("runtime?.Retire(this);"),
        )
        self.assertLess(
            dispose.index("HideCore(notifyClosed: false)"),
            dispose.index("_disposed = true"),
        )
        self.assertLess(
            hide.index('throw new AggregateException("Semantic Stardew overlay hide failed."'),
            hide.index("Visible = false;"),
        )
        self.assertLess(
            unsubscribe.index(
                'throw new AggregateException("Semantic Stardew overlay event teardown failed."'
            ),
            unsubscribe.index("_subscribed = false;"),
        )

    def test_closed_callback_cannot_reentrantly_show_same_session(self) -> None:
        overlay_source = OVERLAY_SESSION.read_text(encoding="utf-8")
        service_source = SURFACE_SERVICE.read_text(encoding="utf-8")
        overlay_show = _method_body(overlay_source, "public void Show()")
        notify = _method_body(overlay_source, "private void NotifyClosed(")
        surface_show = _method_body(service_source, "public void Show()")
        surface_closed = _method_body(service_source, "private void OnOverlayClosed()")

        self.assertLess(
            overlay_show.index("if (_closedNotified)"),
            overlay_show.index("if (Visible) return;"),
        )
        self.assertLess(
            notify.index("_closedNotified = true;"),
            notify.index("_onClosed?.Invoke();"),
        )
        self.assertLess(
            surface_show.index("ThrowIfUnavailable();"),
            surface_show.index("_overlay!.Show();"),
        )
        throw_if_unavailable = _method_body(service_source, "private void ThrowIfUnavailable()")
        self.assertIn("if (_closedRaised)", throw_if_unavailable)
        self.assertLess(
            surface_closed.index("_closedRaised = true;"),
            surface_closed.index("Closed?.Invoke();"),
        )

    def test_public_surface_close_is_terminal_even_before_first_show(self) -> None:
        source = SURFACE_SERVICE.read_text(encoding="utf-8")
        hide = _method_body(source, "public void Hide()")
        throw_if_unavailable = _method_body(source, "private void ThrowIfUnavailable()")

        self.assertIn("if (overlay?.Visible == true)", hide)
        self.assertIn("else\n            OnOverlayClosed();", hide)
        self.assertIn("if (_closedRaised)", throw_if_unavailable)
        for signature in (
            "public void Show()",
            "public void Configure(",
            "public void Refresh()",
            "public void Synchronize()",
            "internal void CancelForAutomatedAcceptance(",
        ):
            self.assertIn("ThrowIfUnavailable();", _method_body(source, signature))

    def test_surface_retains_runtime_until_overlay_cleanup_succeeds(self) -> None:
        source = SURFACE_SERVICE.read_text(encoding="utf-8")
        dispose = _method_body(source, "public void Dispose()")
        owned = _method_body(source, "private void DisposeOwnedResources(")

        self.assertIn("_disposeRequested = true;", dispose)
        self.assertIn("if (_overlay == null && runtime != null)", owned)
        self.assertLess(owned.index("overlay.Dispose();"), owned.index("runtime.Dispose();"))
        self.assertIn("if (_overlay == null && _runtime == null)", dispose)

    def test_automation_cancel_requires_the_creating_service_instance(self) -> None:
        source = SURFACE_SERVICE.read_text(encoding="utf-8")
        create = _method_body(source, "public IUiSemanticSurfaceSession CreateActiveMenuOverlay(")
        cancel = _method_body(source, "public void Cancel(")
        ownership = _method_body(source, "internal bool IsOwnedBy(")

        self.assertIn("new UiActiveMenuSemanticSurfaceSession(this,", create)
        self.assertIn("!owned.IsOwnedBy(this)", cancel)
        self.assertIn("ReferenceEquals(_owner, owner)", ownership)

    def test_runtime_dispose_retains_failed_owners_and_bridge_for_retry(self) -> None:
        source = STARDEW_RUNTIME.read_text(encoding="utf-8")
        dispose = _method_body(source, "public void Dispose()")
        throw_if_disposed = _method_body(source, "private void ThrowIfDisposed()")

        self.assertIn("if (_disposed || _disposing) return;", dispose)
        self.assertIn("_disposeRequested = true;", dispose)
        self.assertNotIn("_overlays.Clear();", dispose)
        self.assertNotIn("_hosts.Clear();", dispose)
        self.assertRegex(
            dispose,
            re.compile(
                r"try\s*\{\s*overlay\.Retire\(\);\s*"
                r"_overlays\.Remove\(overlay\);\s*\}\s*catch"
            ),
        )
        self.assertRegex(
            dispose,
            re.compile(
                r"try\s*\{\s*host\.Retire\(\);\s*"
                r"_hosts\.Remove\(host\);\s*\}\s*catch"
            ),
        )
        self.assertRegex(
            dispose,
            re.compile(
                r"if \(_overlays\.Count == 0\)\s*\{\s*"
                r"foreach \(UiSemanticStardewHost host"
            ),
        )
        self.assertIn(
            "if (_overlays.Count == 0 && _hosts.Count == 0 && !_bridgeDisposed)",
            dispose,
        )
        self.assertLess(
            dispose.index("_bridge.ReleaseGeneratedResources();"),
            dispose.index("_bridge.Dispose();"),
        )
        self.assertLess(dispose.index("_bridge.Dispose();"), dispose.index("_bridgeDisposed = true;"))
        self.assertLess(dispose.index("_bridgeDisposed = true;"), dispose.index("_disposed = true;"))
        self.assertIn("if (_disposeRequested)", throw_if_disposed)

    def test_host_retirement_keeps_failed_owner_registered_for_retry(self) -> None:
        source = STARDEW_RUNTIME.read_text(encoding="utf-8")
        host_source = source[source.index("internal sealed class UiSemanticStardewHost") :]
        dispose = _method_body(host_source, "public void Dispose()")
        retire = _method_body(host_source, "internal void Retire()")
        dispose_owner = _method_body(host_source, "private void DisposeOwner()")

        self.assertLess(dispose.index("DisposeOwner();"), dispose.index("_runtime = null;"))
        self.assertLess(retire.index("DisposeOwner();"), retire.index("_runtime = null;"))
        self.assertLess(dispose_owner.index("owner.Dispose();"), dispose_owner.index("_owner = null;"))
        self.assertLess(dispose_owner.index("owner.Dispose();"), dispose_owner.index("_terminal = null;"))


if __name__ == "__main__":
    unittest.main()
