using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using Hatifect.UI;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Terminal;
using Hatifect.UI.Semantics;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>
/// Provisional semantic IClickableMenu presenter. It owns one semantic host and normalizes Stardew
/// lifecycle/input only; Runtime retains route, placement, layout, focus, portal, and editing policy.
/// </summary>
internal sealed partial class UiSemanticStardewMenu : IClickableMenu, IDisposable
{
    private readonly UiSemanticStardewInputAdapter _input;
    private readonly UiSemanticKeyboardSubscriberLease _keyboard;
    private readonly UiSemanticMenuSlotLease _slot;
    private readonly Action<RuntimeRect> _recompose;
    private readonly Action? _onClosed;
    private UiSemanticStardewHost? _host;
    private bool _cleaned;
    private bool _cleaning;
    private bool _cleanupComplete;
    private bool _closedNotified;
    private bool _closeRequested;
    private RuntimeRect _viewport;

    internal UiSemanticStardewMenu(
        UiSemanticStardewHost host,
        RuntimeRect viewport,
        Action<RuntimeRect> recompose,
        Action? onClosed = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _input = host.Input;
        _recompose = recompose ?? throw new ArgumentNullException(nameof(recompose));
        _onClosed = onClosed;
        int owner = Context.ScreenId;
        _slot = new UiSemanticMenuSlotLease(this, () => Context.ScreenId == owner,
            static () => Game1.activeClickableMenu, static () => Game1.activeClickableMenu = null);
        _keyboard = new UiSemanticKeyboardSubscriberLease(ReceiveTextInput, ReceiveSpecialInput);
        ApplyViewport(viewport);
        _input.PointerMove(Game1.getMouseX(), Game1.getMouseY());
        UiStardewAcceptanceRecorder.Shared.RegisterSemanticSurface(this, "semantic-terminal-menu");
    }

    public bool CloseOnCancel { get; set; } = true;
    public Func<bool>? CloseRequestHandler { get; set; }
    internal event Action? Rendered;
    internal UiSurfaceObservationState? Observation { get; set; }
    internal UiSymbolId CurrentSection => (_host ?? throw new ObjectDisposedException(nameof(UiSemanticStardewMenu)))
        .CurrentInvocation.Experience.Id;

    internal static RuntimeRect CaptureViewport()
        => new(0, 0, Math.Max(1, Game1.uiViewport.Width), Math.Max(1, Game1.uiViewport.Height));

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        // The base menu scales its position relative to the unused window margins. A full-viewport
        // menu has zero margins, so that calculation can produce NaN/int.MinValue even on a no-op
        // resize. This presenter derives its bounds from the UI viewport in Update and Draw.
    }

    public override void update(GameTime time)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        _host?.Session.PumpActions();
        _slot.PollRetirement();
        if (_slot.CanDispatch)
        {
            _host?.Session.AdvanceInteractions(time.ElapsedGameTime);
            base.update(time);
            SynchronizeViewport();
            CompleteInput();
        }
        sample.Complete();
    }

    public override void performHoverAction(int x, int y)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        base.performHoverAction(x, y);
        _input.PointerMove(x, y);
        CompleteInput();
        sample.Complete();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        _input.PointerDown(x, y);
        CompleteInput();
        sample.Complete();
    }

    public override void releaseLeftClick(int x, int y)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        _input.PointerUp(x, y);
        CompleteInput();
        sample.Complete();
    }

    public override void leftClickHeld(int x, int y)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        _input.PointerMove(x, y);
        sample.Complete();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        _input.Wheel(Game1.getMouseX(), Game1.getMouseY(), direction);
        sample.Complete();
    }

    public override void receiveKeyPress(Keys key)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        if (_keyboard.OwnsSubscriber) return;
        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch = _input.KeyDown(key);
        if (RequestsRootCancel(dispatch) && key == Keys.Escape)
            TryCloseFromUnhandledCancel();
        else if (!dispatch.Consumed)
            base.receiveKeyPress(key);
        CompleteInput();
        sample.Complete();
    }

    public override void receiveGamePadButton(Buttons button)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch = _input.GamePad(button);
        if (RequestsRootCancel(dispatch) && button == Buttons.B)
            TryCloseFromUnhandledCancel();
        else if (!dispatch.Consumed)
            base.receiveGamePadButton(button);
        CompleteInput();
        sample.Complete();
    }

    public override void draw(SpriteBatch batch)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        // Stardew can establish the UI viewport after Update, including the first menu frame.
        SynchronizeViewport();
        UiSemanticStardewHost host = _host
            ?? throw new ObjectDisposedException(nameof(UiSemanticStardewMenu));
        host.Render(batch);
        drawMouse(batch);
        sample.Complete(completesFrame: true, layoutBuilds: host.Performance.LayoutBuilds);
        Observation?.CompleteRender(host.Session.Root.LastCompletedRender);
        Rendered?.Invoke();
    }

    protected override void cleanupBeforeExit()
    {
        base.cleanupBeforeExit();
        CleanupSession();
    }

    public override void emergencyShutDown()
    {
        CleanupSession();
        base.emergencyShutDown();
    }

    public void Dispose() => CleanupSession();

    internal (UiInvocationResult Invocation, UiHostRuntimeSession Runtime) CaptureInspectionContext()
    {
        UiSemanticStardewHost host = _host
            ?? throw new ObjectDisposedException(nameof(UiSemanticStardewMenu));
        return (host.CurrentInvocation, host.Session.Root);
    }

    internal void InsertAutomationText(string text)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        _input.TextInput(text);
        CompleteInput();
        sample.Complete();
    }

    internal void RequestClose()
    {
        if (!_cleaned) _closeRequested = true;
    }

    internal UiHostUpdate OpenTerminalSection(UiSymbolId section)
    {
        UiSemanticStardewHost host = _host
            ?? throw new ObjectDisposedException(nameof(UiSemanticStardewMenu));
        return host.OpenTerminalSection(section);
    }

    internal void SetTerminalTheme(Hatifect.UI.Runtime.Visual.Theming.UiTheme theme) => _host!.SetTerminalTheme(theme);

    internal UiHostUpdate RecomposeTerminal(Hatifect.UI.Planning.UiPresentationProfile profile,
        RuntimeRect viewport, string locale, UiEnvironment? environment = null, Action? acceptOwnerState = null,
        Hatifect.UI.Runtime.Visual.Theming.UiTheme? theme = null, Action? validateOwner = null)
    {
        ValidateTerminalOwner();
        return _host!.RecomposeTerminal(profile, new UiHostPlacementContext(viewport), locale,
            environment, acceptOwnerState, ValidateOwner, theme);

        void ValidateOwner()
        {
            validateOwner?.Invoke();
            ValidateTerminalOwner();
        }
    }

    internal UiHostUpdate ReloadTerminal(UiTerminalSectionAssets assets,
        Hatifect.UI.Planning.UiPresentationProfile profile, RuntimeRect viewport, string locale, Action acceptAssets,
        UiEnvironment? environment = null, Hatifect.UI.Runtime.Visual.Theming.UiTheme? theme = null,
        Action? validateOwner = null)
    {
        ValidateTerminalOwner();
        return _host!.ReloadTerminal(assets, profile, new UiHostPlacementContext(viewport), locale,
            acceptAssets, ValidateOwner, environment, theme);

        void ValidateOwner()
        {
            validateOwner?.Invoke();
            ValidateTerminalOwner();
        }
    }

    private void ValidateTerminalOwner()
    {
        _slot.PollRetirement();
        if (_host == null || !_slot.CanDispatch)
            throw new InvalidOperationException("The Terminal lost its native menu owner during preparation.");
    }

    internal bool EvictTerminalSection(UiSymbolId section)
    {
        UiSemanticStardewHost host = _host
            ?? throw new ObjectDisposedException(nameof(UiSemanticStardewMenu));
        return host.EvictTerminalSection(section);
    }

    private void CleanupSession()
    {
        _host?.Session.Root.RequireOwner();
        // Foreign-screen disposal must disable drawing/input before releasing the host.
        _cleaned = true;
        _slot.Retire();
        if (_cleanupComplete || _cleaning) return;
        _cleaning = true;
        var failures = new List<Exception>();
        try
        {
            ReleaseSessionResources(failures);
            NotifyClosed(failures);
            _cleanupComplete = failures.Count == 0;
        }
        finally { _cleaning = false; }
        if (failures.Count > 0)
            throw new AggregateException("Semantic Stardew menu cleanup failed.", failures);
    }

    private void ReleaseSessionResources(List<Exception> failures)
    {
        try { _host?.Session.Deactivate(); }
        catch (Exception error) { failures.Add(error); }
        try { _keyboard.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        try { UiStardewAcceptanceRecorder.Shared.UnregisterSemanticSurface(this); }
        catch (Exception error) { failures.Add(error); }
        try { _host?.Dispose(); _host = null; }
        catch (Exception error) { failures.Add(error); }
    }

    private void NotifyClosed(List<Exception> failures)
    {
        if (_closedNotified) return;
        _closedNotified = true;
        try { _onClosed?.Invoke(); }
        catch (Exception error) { failures.Add(error); }
    }

    private void SynchronizeViewport()
    {
        RuntimeRect viewport = CaptureViewport();
        if (viewport == _viewport) return;
        _recompose(viewport);
        ApplyViewport(viewport);
    }

    private void ApplyViewport(RuntimeRect viewport)
    {
        _viewport = viewport;
        xPositionOnScreen = (int)viewport.X;
        yPositionOnScreen = (int)viewport.Y;
        width = Math.Max(1, (int)viewport.Width);
        height = Math.Max(1, (int)viewport.Height);
    }

    private void TryCloseFromUnhandledCancel()
    {
        if (!CloseOnCancel || CloseRequestHandler?.Invoke() == false) return;
        exitThisMenu();
    }

    private void SyncTextInputOwnership()
    {
        bool needsText = ReferenceEquals(Game1.activeClickableMenu, this) && _input.IsTextEditing;
        if (needsText) _keyboard.Acquire(); else _keyboard.Release();
    }

    private void CompleteInput()
    {
        if (_cleaned) return;
        SyncTextInputOwnership();
        if (!_closeRequested || _cleaned) return;
        _closeRequested = false;
        exitThisMenu();
    }

    internal event Action? NativeTextReceived;
    internal bool HasActiveInputPortal => _host?.Session.ActivePortalCount > 0;

    private void ReceiveTextInput(string text)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        _input.TextInput(text);
        NativeTextReceived?.Invoke();
        CompleteInput();
        sample.Complete();
    }

    private void ReceiveSpecialInput(Keys key)
    {
        _slot.PollRetirement();
        if (!_slot.CanDispatch) return;
        var sample = BeginAcceptanceSample();
        UiPortalDispatch dispatch = _input.KeyDown(key);
        if (RequestsRootCancel(dispatch) && key == Keys.Escape)
            TryCloseFromUnhandledCancel();
        CompleteInput();
        sample.Complete();
    }

    private static bool RequestsRootCancel(UiPortalDispatch dispatch)
        => dispatch.RequestsRootDismissal;

    private UiStardewAcceptanceRecorder.SemanticOperationSample BeginAcceptanceSample()
    {
        UiStardewAcceptanceRecorder recorder = UiStardewAcceptanceRecorder.Shared;
        UiSemanticStardewHost? host = _host;
        return !recorder.IsCapturing || host is null
            ? default
            : recorder.BeginSemanticOperation(this, host.Performance.LayoutBuilds);
    }
}
