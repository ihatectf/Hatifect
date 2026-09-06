using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Semantics;
using RuntimeSymbolId = Hatifect.UI.UiSymbolId;
using RuntimePoint = Hatifect.UI.Runtime.Layout.UiPoint;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;

namespace Hatifect.UI.Stardew.Semantic;

internal enum UiSemanticStardewOverlayRenderLayer
{
    Hud = 0,
    ActiveMenu = 1
}

/// <summary>
/// Provisional SMAPI lifecycle owner for one semantic Overlay host. Runtime retains scene, layout,
/// input, focus, dismissal, and render policy; this owner only binds SMAPI events and resources.
/// </summary>
internal sealed class UiSemanticStardewOverlaySession : IDisposable
{
    private UiSemanticStardewRuntime? _runtime;
    private readonly IModHelper _helper;
    private readonly UiSemanticStardewInputAdapter _input;
    private readonly UiSemanticStardewOverlayRenderLayer _renderLayer;
    private readonly UiSemanticKeyboardSubscriberLease _keyboard;
    private readonly Action? _onClosed;
    private readonly Action<RuntimeRect>? _synchronizeEnvironment;
    private UiSemanticStardewHost? _host;
    private IClickableMenu? _boundActiveMenu;
    private readonly UiSemanticOverlayEventBinding _events;
    private bool _acceptanceRegistered;
    private bool _closedNotified;
    private bool _retireRequested;
    private bool _disposing;
    private bool _hiding;
    private bool _showing;
    private bool _disposed;
    private bool _pointerSynchronized;
    private int _pointerX;
    private int _pointerY;
    private RuntimeRect _viewport;

    internal UiSemanticStardewOverlaySession(
        UiSemanticStardewRuntime runtime,
        IModHelper helper,
        UiSemanticStardewHost host,
        RuntimeRect viewport,
        UiSemanticStardewOverlayRenderLayer renderLayer,
        Action? onClosed = null,
        Action<RuntimeRect>? synchronizeEnvironment = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _input = host.Input;
        _viewport = viewport;
        if (!Enum.IsDefined(typeof(UiSemanticStardewOverlayRenderLayer), renderLayer))
            throw new ArgumentOutOfRangeException(nameof(renderLayer));
        _renderLayer = renderLayer;
        _onClosed = onClosed;
        _synchronizeEnvironment = synchronizeEnvironment;
        _events = new UiSemanticOverlayEventBinding(
            helper.Events, Context.ScreenId, static () => Context.ScreenId, renderLayer,
            OnUpdateTicked, OnReturnedToTitle, OnButtonPressed, OnButtonReleased,
            OnMouseWheelScrolled, OnWindowResized, OnRenderedHud, OnRenderedActiveMenu);
        _keyboard = new UiSemanticKeyboardSubscriberLease(ReceiveTextInput, ReceiveSpecialInput);
        UiStardewAcceptanceRecorder.Shared.RegisterSemanticSurface(
            this,
            renderLayer == UiSemanticStardewOverlayRenderLayer.Hud
                ? "semantic-hud-overlay"
                : "semantic-menu-overlay");
        _acceptanceRegistered = true;
    }

    public bool Visible { get; private set; }
    internal bool IsRetired => _retireRequested || _closedNotified || _disposed;
    internal UiSurfaceObservationState? Observation { get; set; }

    internal UiSemanticSurfaceSnapshot CaptureObservation(UiSurfaceObservationState observation, UiEnvironment environment)
    {
        RequireCurrentScreen();
        // Observation must reject a lost native owner immediately, without running lifecycle
        // synchronization, closing the surface, or invoking consumer callbacks.
        if (Visible && !OwnsCurrentMenuContext())
            throw new InvalidOperationException("The observed overlay no longer owns its native menu context.");
        return observation.Capture(Host.Session.Root, environment, Visible, Host.Session.Accessibility.Portals.Count);
    }

    /// <summary>Optional platform backdrop drawn before the semantic host.</summary>
    public float BackgroundDimmingOpacity { get; set; }

    /// <summary>Raised only after the semantic host completed one owned render pass.</summary>
    public event Action? Rendered;

    /// <summary>Raised after Runtime dismisses a nested portal through normalized input.</summary>
    public event Action<RuntimeSymbolId>? PortalClosed;

    /// <summary>True only while the pointer is inside the host-resolved Overlay bounds.</summary>
    public bool IsPointerOver
        => _events.IsCurrentScreen && Visible && Host.Contains(new RuntimePoint(Game1.getMouseX(), Game1.getMouseY()));

    /// <summary>Called only for an unconsumed Escape/B request; true hides this overlay.</summary>
    public Func<bool>? CloseRequestHandler { get; set; }

    public void Show()
    {
        ThrowIfDisposed();
        RequireCurrentScreen();
        if (_closedNotified)
        {
            throw new InvalidOperationException(
                "A closed semantic Stardew overlay session cannot be shown again.");
        }
        if (Visible) return;
        if (_showing) throw new InvalidOperationException("The overlay is already preparing its first Show.");
        var sample = BeginAcceptanceSample();
        IClickableMenu? activeMenu = Game1.activeClickableMenu;
        if (_renderLayer == UiSemanticStardewOverlayRenderLayer.ActiveMenu && activeMenu == null)
            throw new InvalidOperationException("An active-menu semantic overlay requires an active menu to bind.");

        _showing = true;
        _boundActiveMenu = activeMenu;
        try
        {
            ReflowIfViewportChanged();
            ValidatePreparedOwner();
            Visible = true;
            try
            {
                _events.Activate();
                ValidatePreparedOwner();
                SynchronizePointer(force: true);
                SyncTextInputOwnership();
                sample.Complete();
            }
            catch
            {
                HideCore(notifyClosed: false);
                throw;
            }
        }
        finally
        {
            _showing = false;
            if (!Visible) _boundActiveMenu = null;
        }
    }

    public void Hide()
    {
        if (_disposed || !Visible) return;
        HideCore(notifyClosed: true);
    }

    public void Update(UiScene scene)
    {
        ThrowIfDisposed();
        RequireCurrentScreen();
        ArgumentNullException.ThrowIfNull(scene);
        var sample = BeginAcceptanceSample();
        Host.UpdateOverlay(scene, _viewport);
        SynchronizePointer(force: true);
        SyncTextInputOwnership();
        sample.Complete();
    }

    internal void Reload(Func<UiScene> prepareScene, Action acceptOwnerState)
        => UpdatePrepared(prepareScene, _viewport, acceptOwnerState, renewActionGeneration: true);

    internal void UpdatePrepared(Func<UiScene> prepareScene, RuntimeRect viewport, Action acceptOwnerState,
        Action? validateOwner = null, bool renewActionGeneration = false)
    {
        ValidateOwner();
        var sample = BeginAcceptanceSample();
        Host.UpdateOverlayPrepared(() =>
        {
            UiScene scene = prepareScene();
            ValidateOwner();
            return scene;
        }, viewport, () =>
        {
            _viewport = viewport;
            acceptOwnerState();
        }, ValidateOwner, renewActionGeneration);
        // Cancellation may close this owner. Do not re-enter its retired input adapter.
        if (!_disposed && !_retireRequested)
        {
            SynchronizePointer(force: true);
            SyncTextInputOwnership();
        }
        sample.Complete();

        void ValidateOwner()
        {
            validateOwner?.Invoke();
            ValidatePreparedOwner();
        }
    }

    private void ValidatePreparedOwner()
    {
        ThrowIfDisposed();
        RequireCurrentScreen();
        if ((Visible || _showing) && _renderLayer == UiSemanticStardewOverlayRenderLayer.ActiveMenu && !OwnsCurrentMenuContext())
        {
            // A not-yet-visible Show can retry against a new menu without publishing its
            // candidate or installing subscriptions. An existing visible owner is retired.
            if (Visible) RetireLostMenuContext();
            throw new InvalidOperationException("The overlay lost its native menu owner during preparation.");
        }
    }

    public UiPortalHandle Present(UiPortalRequest request)
    {
        ThrowIfDisposed();
        RequireCurrentScreen();
        return Host.Present(request);
    }

    public UiHostUpdate UpdatePortal(
        RuntimeSymbolId id,
        UiScene scene,
        UiHostPlacementContext placement)
    {
        ThrowIfDisposed();
        RequireCurrentScreen();
        return Host.UpdatePortal(id, scene, placement);
    }

    internal void DismissTopPortalForAutomatedAcceptance()
    {
        ThrowIfDisposed();
        if (!string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE"), "1", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED"), "1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Semantic portal acceptance dismissal requires the exact automated TestHarness environment.");
        if (!CanRouteInput())
            throw new InvalidOperationException("The semantic Overlay cannot route acceptance input in its current menu context.");

        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch = _input.KeyDown(Keys.Escape, shift: false, control: false);
        NotifyPortalClosed(dispatch);
        SyncTextInputOwnership();
        sample.Complete();
        if (!dispatch.Consumed || !dispatch.PortalClosed || dispatch.Portal == null)
            throw new InvalidOperationException(
                "Runtime did not dismiss the top semantic portal through its OutsideOrEscape policy.");
    }

    internal void CancelForAutomatedAcceptance(UiSemanticSurfaceCancelInput input)
    {
        ThrowIfDisposed();
        if (!string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_MODE"), "1", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("HATIFECT_TEST_AUTOMATED"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Semantic surface cancellation requires the exact automated TestHarness environment.");
        }
        if (!Enum.IsDefined(typeof(UiSemanticSurfaceCancelInput), input))
            throw new ArgumentOutOfRangeException(nameof(input));
        if (!CanRouteInput())
            throw new InvalidOperationException("The semantic surface cannot route acceptance input in its current menu context.");

        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch = input == UiSemanticSurfaceCancelInput.Escape
            ? _input.KeyDown(Keys.Escape)
            : _input.GamePad(Buttons.B);
        NotifyPortalClosed(dispatch);
        bool surfaceDismissRequested = dispatch.Portal == null
            && dispatch.Interaction?.DismissRequested == true;
        if ((surfaceDismissRequested || !dispatch.Consumed) && !TryCloseFromUnhandledCancel())
            throw new InvalidOperationException("Runtime did not accept the normalized semantic-surface cancellation request.");
        SyncTextInputOwnership();
        sample.Complete();
    }

    /// <summary>
    /// Retire an active-menu surface when its exact native menu owner changed. The public semantic
    /// session calls this before recomposition so a consumer-driven refresh cannot keep a stale
    /// overlay alive until a later SMAPI input, update, or render event.
    /// </summary>
    internal bool SynchronizeMenuContext()
    {
        ThrowIfDisposed();
        return CanRouteInput();
    }

    public void Dispose()
    {
        if (_disposed || _disposing) return;
        _host?.Session.Root.RequireOwner();
        _retireRequested = true;
        _disposing = true;
        try
        {
            UiSemanticStardewRuntime? runtime = _runtime;
            DisposeCore();
            if (_disposed)
            {
                runtime?.Retire(this);
                if (ReferenceEquals(_runtime, runtime)) _runtime = null;
            }
        }
        finally
        {
            _disposing = false;
        }
    }

    internal void Retire()
    {
        if (_disposed || _disposing) return;
        _retireRequested = true;
        _disposing = true;
        try
        {
            DisposeCore();
            if (_disposed) _runtime = null;
        }
        finally
        {
            _disposing = false;
        }
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        bool notifyClosed = Visible && !_closedNotified;
        var failures = new List<Exception>();
        try { HideCore(notifyClosed: false); }
        catch (Exception error) { failures.Add(error); }

        if (!Visible)
        {
            if (_acceptanceRegistered)
            {
                try
                {
                    UiStardewAcceptanceRecorder.Shared.UnregisterSemanticSurface(this);
                    _acceptanceRegistered = false;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            UiSemanticStardewHost? host = _host;
            if (host != null)
            {
                try
                {
                    host.Dispose();
                    if (ReferenceEquals(_host, host)) _host = null;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            if (notifyClosed) NotifyClosed(failures);
        }

        if (!Visible && !_acceptanceRegistered && _host == null)
            _disposed = true;
        if (failures.Count > 0)
            throw new AggregateException("Semantic Stardew overlay cleanup failed.", failures);
    }

    private UiSemanticStardewHost Host
        => _host ?? throw new ObjectDisposedException(nameof(UiSemanticStardewOverlaySession));

    private void HideCore(bool notifyClosed, bool restoreKeyboard = true)
    {
        if (!Visible) return;
        if (_hiding) return;
        Host.Session.Root.RequireOwner();
        _hiding = true;
        try
        {
            // A real close is terminal even if event/keyboard cleanup needs a retry. A failed
            // first Show uses notifyClosed:false and can retry its not-yet-visible candidate.
            if (notifyClosed || _retireRequested)
            {
                _retireRequested = true;
                Host.Session.Deactivate();
            }
            var failures = new List<Exception>();
            try { _keyboard.Release(restoreKeyboard && (!_events.IsCurrentScreen || OwnsCurrentMenuContext())); }
            catch (Exception error) { failures.Add(error); }
            try { _events.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0)
                throw new AggregateException("Semantic Stardew overlay hide failed.", failures);

            Visible = false;
            _pointerSynchronized = false;
            _boundActiveMenu = null;
            if (notifyClosed) NotifyClosed(failures);
            if (failures.Count > 0)
                throw new AggregateException("Semantic Stardew overlay hide failed.", failures);
        }
        finally { _hiding = false; }
    }

    private void NotifyClosed(ICollection<Exception> failures)
    {
        if (_closedNotified) return;
        _closedNotified = true;
        try { _onClosed?.Invoke(); }
        catch (Exception error) { failures.Add(error); }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!_events.IsCurrentScreen || _retireRequested || !Visible) return;
        if (_renderLayer == UiSemanticStardewOverlayRenderLayer.ActiveMenu && !OwnsCurrentMenuContext())
        {
            RetireLostMenuContext();
            return;
        }
        var sample = BeginAcceptanceSample();
        // HUD occlusion by a native menu pauses input/draw, not owning-thread completions.
        Host.Session.PumpActions();
        if (_retireRequested || !Visible) return;
        bool reflowed = ReflowIfViewportChanged();
        if (CanRouteInput())
        {
            SynchronizePointer(force: reflowed);
            SyncTextInputOwnership();
        }
        sample.Complete();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e) => Dispose();

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        if (_retireRequested || !Visible) return;
        var sample = BeginAcceptanceSample();
        if (ReflowIfViewportChanged()) SynchronizePointer(force: true);
        sample.Complete();
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e) => Draw(e.SpriteBatch);

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e) => Draw(e.SpriteBatch);

    private void Draw(SpriteBatch batch)
    {
        if (!CanRouteInput()) return;
        UiSemanticStardewHost host = Host;
        var sample = BeginAcceptanceSample();
        float dim = Math.Clamp(BackgroundDimmingOpacity, 0f, 1f);
        if (dim > 0f)
            batch.Draw(
                Game1.staminaRect,
                new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height),
                Color.Black * dim);
        host.Render(batch);
        if (_renderLayer == UiSemanticStardewOverlayRenderLayer.Hud && !Game1.options.hardwareCursor)
            SoftwareCursor.Draw(batch);
        sample.Complete(completesFrame: true, layoutBuilds: host.Performance.LayoutBuilds);
        Observation?.CompleteRender(host.Session.Root.LastCompletedRender);
        Rendered?.Invoke();
    }

    private void OnMouseWheelScrolled(object? sender, MouseWheelScrolledEventArgs e)
    {
        if (!CanRouteInput()) return;
        var sample = BeginAcceptanceSample();
        UiPortalScrollDispatch dispatch = _input.Wheel(Game1.getMouseX(), Game1.getMouseY(), e.Delta);
        if (dispatch.Consumed)
            _helper.Input.SuppressScrollWheel();
        sample.Complete();
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!CanRouteInput()) return;
        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch;
        if (e.Button == SButton.MouseLeft)
        {
            dispatch = _input.PointerDown(Game1.getMouseX(), Game1.getMouseY());
        }
        else if (e.Button.TryGetController(out Buttons gamepad))
        {
            dispatch = _input.GamePad(gamepad);
            if (!dispatch.Consumed && gamepad == Buttons.B && TryCloseFromUnhandledCancel())
            {
                _helper.Input.Suppress(e.Button);
                sample.Complete();
                return;
            }
        }
        else if (e.Button.TryGetKeyboard(out Keys key))
        {
            if (_keyboard.OwnsSubscriber) return;
            dispatch = _input.KeyDown(key);
            if (!dispatch.Consumed && key == Keys.Escape && TryCloseFromUnhandledCancel())
            {
                _helper.Input.Suppress(e.Button);
                sample.Complete();
                return;
            }
        }
        else
        {
            dispatch = _input.UnhandledInput();
        }

        if (dispatch.Portal == null && dispatch.Interaction?.DismissRequested == true
            && TryCloseFromUnhandledCancel())
        {
            _helper.Input.Suppress(e.Button);
            sample.Complete();
            return;
        }

        NotifyPortalClosed(dispatch);

        if (dispatch.Consumed)
            _helper.Input.Suppress(e.Button);
        SyncTextInputOwnership();
        sample.Complete();
    }

    private void OnButtonReleased(object? sender, ButtonReleasedEventArgs e)
    {
        if (!CanRouteInput()) return;
        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch = e.Button == SButton.MouseLeft
            ? _input.PointerUp(Game1.getMouseX(), Game1.getMouseY())
            : _input.UnhandledInput();
        NotifyPortalClosed(dispatch);
        if (dispatch.Consumed)
            _helper.Input.Suppress(e.Button);
        SyncTextInputOwnership();
        sample.Complete();
    }

    private bool ReflowIfViewportChanged()
    {
        RuntimeRect viewport = UiSemanticStardewMenu.CaptureViewport();
        if (_synchronizeEnvironment != null)
        {
            long version = Host.Session.Root.AcceptedVersion;
            _synchronizeEnvironment(viewport);
            return !_retireRequested && !_disposed && Host.Session.Root.AcceptedVersion != version;
        }
        if (viewport == _viewport) return false;
        Host.ReflowOverlay(viewport);
        _viewport = viewport;
        return true;
    }

    private void SynchronizePointer(bool force)
    {
        if (!Visible || !OwnsCurrentMenuContext())
        {
            _pointerSynchronized = false;
            return;
        }
        int x = Game1.getMouseX();
        int y = Game1.getMouseY();
        if (!force && _pointerSynchronized && x == _pointerX && y == _pointerY) return;
        _pointerX = x;
        _pointerY = y;
        _pointerSynchronized = true;
        _input.PointerMove(x, y);
    }

    private bool CanRouteInput()
    {
        if (!_events.IsCurrentScreen) return false;
        if (_retireRequested) return false;
        if (!Visible) return false;
        if (OwnsCurrentMenuContext()) return true;
        RetireLostMenuContext();
        return false;
    }

    private void RetireLostMenuContext()
    {
        if (_renderLayer == UiSemanticStardewOverlayRenderLayer.ActiveMenu)
        {
            HideCore(notifyClosed: true, restoreKeyboard: false);
            return;
        }

        _keyboard.Release(restorePrevious: false);
    }

    private bool OwnsCurrentMenuContext()
    {
        IClickableMenu? active = Game1.activeClickableMenu;
        return _renderLayer == UiSemanticStardewOverlayRenderLayer.Hud
            ? active == null
            : ReferenceEquals(active, _boundActiveMenu);
    }

    private bool TryCloseFromUnhandledCancel()
    {
        if (CloseRequestHandler?.Invoke() != true) return false;
        Hide();
        return true;
    }

    private void SyncTextInputOwnership()
    {
        // Input callbacks may retire the overlay or switch screens. Failed event cleanup can
        // leave Visible true, so eligibility must be checked again before acquiring a lease.
        bool needsText = _events.IsCurrentScreen && !_retireRequested && Visible &&
                         OwnsCurrentMenuContext() && _input.IsTextEditing;
        if (needsText) _keyboard.Acquire(); else _keyboard.Release();
    }

    private void ReceiveTextInput(string text)
    {
        if (!CanRouteInput()) return;
        var sample = BeginAcceptanceSample();
        _input.TextInput(text);
        SyncTextInputOwnership();
        sample.Complete();
    }

    private void ReceiveSpecialInput(Keys key)
    {
        if (!CanRouteInput()) return;
        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch = _input.KeyDown(key);
        NotifyPortalClosed(dispatch);
        if (dispatch.Portal == null && dispatch.Interaction?.DismissRequested == true)
            TryCloseFromUnhandledCancel();
        else if (!dispatch.Consumed && key == Keys.Escape)
            TryCloseFromUnhandledCancel();
        SyncTextInputOwnership();
        sample.Complete();
    }

    private void NotifyPortalClosed(UiPortalDispatch dispatch)
    {
        if (dispatch.PortalClosed && dispatch.Portal is { } portal)
            PortalClosed?.Invoke(portal);
    }

    private UiStardewAcceptanceRecorder.SemanticOperationSample BeginAcceptanceSample()
    {
        UiStardewAcceptanceRecorder recorder = UiStardewAcceptanceRecorder.Shared;
        UiSemanticStardewHost? host = _host;
        return !recorder.IsCapturing || host is null
            ? default
            : recorder.BeginSemanticOperation(this, host.Performance.LayoutBuilds);
    }

    private void RequireCurrentScreen()
    {
        if (!_events.IsCurrentScreen)
            throw new InvalidOperationException("A semantic overlay can only be updated or shown on its owning screen.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed || _retireRequested)
            throw new ObjectDisposedException(nameof(UiSemanticStardewOverlaySession));
    }

    private sealed class SoftwareCursorMenu : IClickableMenu
    {
        public void DrawCursor(SpriteBatch batch) => drawMouse(batch);
    }

    private static class SoftwareCursor
    {
        private static readonly SoftwareCursorMenu Menu = new();
        public static void Draw(SpriteBatch batch) => Menu.DrawCursor(batch);
    }
}
