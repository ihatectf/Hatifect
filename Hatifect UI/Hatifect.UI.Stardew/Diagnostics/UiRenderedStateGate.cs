using System;

namespace Hatifect.UI.Stardew;

/// <summary>Accepts one state only after two distinct, consistent completed game frames.</summary>
internal sealed class UiRenderedStateGate
{
    private readonly string _locale;
    private readonly float _scale;
    private readonly string _theme;
    private long _lastFrame = -1;
    private UiRenderedStateObservation? _previous;
    private bool _accepted;

    internal UiRenderedStateGate(string locale, float scale, string theme)
    {
        if (string.IsNullOrWhiteSpace(locale)) throw new ArgumentException("A locale is required.", nameof(locale));
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (string.IsNullOrWhiteSpace(theme)) throw new ArgumentException("A theme is required.", nameof(theme));
        _locale = locale;
        _scale = scale;
        _theme = theme;
    }

    internal bool Observe(long completedFrame, UiRenderedStateObservation observation)
    {
        if (_accepted || completedFrame <= _lastFrame) return false;
        _lastFrame = completedFrame;
        if (!MatchesTarget(observation))
        {
            _previous = null;
            return false;
        }
        bool stable = _previous == observation;
        _previous = observation;
        _accepted = stable;
        return stable;
    }

    private bool MatchesTarget(UiRenderedStateObservation value)
        => string.Equals(value.GameLocale, _locale, StringComparison.Ordinal)
           && string.Equals(value.SceneLocale, _locale, StringComparison.Ordinal)
           && string.Equals(value.Theme, _theme, StringComparison.Ordinal)
           && Near(value.DesiredScale, _scale)
           && Near(value.BaseScale, _scale)
           && float.IsFinite(value.PixelScale) && value.PixelScale > 0
           && value.BackBufferWidth > 0 && value.BackBufferHeight > 0
           && value.ViewportWidth == Math.Ceiling(value.BackBufferWidth / (double)value.PixelScale)
           && value.ViewportHeight == Math.Ceiling(value.BackBufferHeight / (double)value.PixelScale)
           && value.MenuX == 0 && value.MenuY == 0
           && value.MenuWidth == value.ViewportWidth && value.MenuHeight == value.ViewportHeight
           && value.HasValidTree && value.HasProbeText && value.LoadFadeFinished;

    private static bool Near(float left, float right)
        => float.IsFinite(left) && Math.Abs(left - right) < 0.0001f;
}

internal readonly record struct UiRenderedStateObservation(
    string GameLocale,
    string SceneLocale,
    string Theme,
    float DesiredScale,
    float BaseScale,
    float PixelScale,
    int BackBufferWidth,
    int BackBufferHeight,
    int ViewportWidth,
    int ViewportHeight,
    int MenuX,
    int MenuY,
    int MenuWidth,
    int MenuHeight,
    bool HasValidTree,
    bool HasProbeText,
    bool LoadFadeFinished);
