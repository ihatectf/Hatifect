using System;

namespace Hatifect.UI.Stardew;

internal readonly record struct UiNativePresentationFrame(
    float BaseScale, float DesiredScale, float AppliedScale,
    int WindowWidth, int WindowHeight, int UiWidth, int UiHeight,
    int UiTargetWidth, int UiTargetHeight, int ScreenWidth, int ScreenHeight,
    int BackBufferWidth, int BackBufferHeight, int DeviceWidth, int DeviceHeight, int BoundTargets,
    string Locale, bool GamepadControls, int GamepadMode);

/// <summary>Two completed target frames, one owned profile write, then two fresh exact-profile frames.</summary>
internal sealed class UiNativePresentationGate
{
    private readonly float _scale;
    private readonly string _locale;
    private readonly bool _controls;
    private readonly int _mode;
    private long _lastFrame = -1;
    private UiNativePresentationFrame? _previous;
    private bool _profileApplied;

    internal UiNativePresentationGate(float scale, string locale, bool controls, int mode)
    {
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (string.IsNullOrWhiteSpace(locale)) throw new ArgumentException("A locale is required.", nameof(locale));
        _scale = scale; _locale = locale; _controls = controls; _mode = mode;
    }

    internal UiNativePresentationFrame? Settled { get; private set; }

    internal void ObserveCompletedDraw(long frame, UiNativePresentationFrame value)
    {
        if (frame <= _lastFrame) return;
        _lastFrame = frame;
        bool valid = value.BaseScale == _scale && value.DesiredScale == _scale
            && value.AppliedScale == _scale
            && value.WindowWidth > 0 && value.WindowHeight > 0
            && value.UiWidth > 0 && value.UiHeight > 0
            && value.UiTargetWidth == value.UiWidth && value.UiTargetHeight == value.UiHeight
            && value.ScreenWidth > 0 && value.ScreenHeight > 0
            && value.BackBufferWidth > 0 && value.BackBufferHeight > 0
            && value.DeviceWidth > 0 && value.DeviceHeight > 0 && value.BoundTargets == 0
            && string.Equals(value.Locale, _locale, StringComparison.Ordinal);
        Settled = valid && _previous == value ? value : null;
        _previous = valid ? value : null;
    }

    internal bool CompleteProfile(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (Settled is not { } frame) return false;
        if (!_profileApplied)
        {
            apply();
            _profileApplied = true;
            _previous = null; Settled = null;
            return false;
        }
        if (frame.GamepadControls != _controls || frame.GamepadMode != _mode)
            throw new InvalidOperationException("The native input profile changed after its one owned application.");
        return true;
    }
}
