using System;
using StardewValley;

namespace Hatifect.Flow.Diagnostics;

// Diagnostic value captured only after the owning game draw. No resource is retained or allocated here.
internal readonly record struct FlowUiNativeFrame(
    float BaseScale, float DesiredScale, float AppliedScale,
    int WindowWidth, int WindowHeight, int UiWidth, int UiHeight,
    int UiTargetWidth, int UiTargetHeight, int ScreenWidth, int ScreenHeight,
    int BackBufferWidth, int BackBufferHeight, int DeviceWidth, int DeviceHeight, int BoundTargets)
{
    internal bool IsConsistent => float.IsFinite(AppliedScale) && AppliedScale > 0
        && BaseScale == DesiredScale && WindowWidth > 0 && WindowHeight > 0
        && UiWidth > 0 && UiHeight > 0 && UiTargetWidth == UiWidth && UiTargetHeight == UiHeight
        && BackBufferWidth > 0 && BackBufferHeight > 0 && BoundTargets == 0;

    internal static FlowUiNativeFrame Capture()
    {
        var game = Game1.game1;
        var graphics = Game1.graphics.GraphicsDevice;
        var window = game.Window.ClientBounds;
        var ui = game.uiScreen;
        var screen = game.screen;
        return new(Game1.options.baseUIScale, Game1.options.desiredUIScale, Game1.options.uiScale,
            window.Width, window.Height, Game1.uiViewport.Width, Game1.uiViewport.Height,
            ui is { IsDisposed: false } ? ui.Width : 0, ui is { IsDisposed: false } ? ui.Height : 0,
            screen is { IsDisposed: false } ? screen.Width : 0, screen is { IsDisposed: false } ? screen.Height : 0,
            graphics.PresentationParameters.BackBufferWidth, graphics.PresentationParameters.BackBufferHeight,
            graphics.Viewport.Width, graphics.Viewport.Height, graphics.RenderTargetCount);
    }
}

internal sealed class FlowUiNativeFrameSettling
{
    private FlowUiNativeFrame? _previous;
    private bool _profilePending;
    internal bool ProfileReady => !_profilePending;
    internal FlowUiNativeFrame? Settled { get; private set; }

    internal void ObserveCompletedDraw(FlowUiNativeFrame frame)
    {
        Settled = frame.IsConsistent && _previous == frame ? frame : null;
        _previous = frame.IsConsistent ? frame : null;
    }

    internal bool CompleteProfile(Action apply)
    {
        if (Settled is null) return false;
        if (!_profilePending) return true;
        apply();
        _profilePending = false;
        _previous = null; Settled = null;
        return false;
    }

    internal void Reset() { _previous = null; Settled = null; _profilePending = true; }
}
