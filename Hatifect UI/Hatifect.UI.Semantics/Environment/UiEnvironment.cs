using System;

namespace Hatifect.UI.Semantics;

public enum UiInputMode
{
    MouseKeyboard,
    Keyboard,
    Controller
}

/// <summary>Available layout space in logical UI units, after platform scaling.</summary>
public sealed record UiEnvironmentViewport
{
    public UiEnvironmentViewport(float width, float height)
    {
        if (!float.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
    }

    public float Width { get; }
    public float Height { get; }
}

public sealed record UiAccessibilityPreferences(bool ReducedMotion = false, bool HighContrast = false);

/// <summary>Origins of the six captured facets; values describe their actual provider or policy.</summary>
public sealed record UiEnvironmentOrigins(
    string Viewport = "Host",
    string Scale = "Host",
    string InputMode = "Host",
    string Locale = "Host",
    string Theme = "Host",
    string Accessibility = "Host");

/// <summary>
/// Immutable platform-independent environment captured for one planning operation. Scale describes
/// the platform transform; Viewport is already logical and must not be divided by Scale again.
/// Theme is a stable semantic ID, not a renderer resource. No platform object is retained.
/// </summary>
public sealed record UiEnvironment
{
    public UiEnvironment(
        UiEnvironmentViewport viewport,
        float scale,
        UiInputMode inputMode,
        string locale,
        UiSymbolId theme,
        UiAccessibilityPreferences? accessibility = null,
        UiEnvironmentOrigins? origins = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        if (!Enum.IsDefined(typeof(UiInputMode), inputMode)) throw new ArgumentOutOfRangeException(nameof(inputMode));
        if (string.IsNullOrWhiteSpace(locale)) throw new ArgumentException("A locale is required.", nameof(locale));
        if (!theme.IsValid) throw new ArgumentException("A stable theme ID is required.", nameof(theme));
        origins ??= new UiEnvironmentOrigins();
        if (string.IsNullOrWhiteSpace(origins.Viewport) || string.IsNullOrWhiteSpace(origins.Scale)
            || string.IsNullOrWhiteSpace(origins.InputMode) || string.IsNullOrWhiteSpace(origins.Locale)
            || string.IsNullOrWhiteSpace(origins.Theme) || string.IsNullOrWhiteSpace(origins.Accessibility))
            throw new ArgumentException("Every environment facet requires an origin.", nameof(origins));

        Viewport = viewport;
        Scale = scale;
        InputMode = inputMode;
        Locale = locale;
        Theme = theme;
        Accessibility = accessibility ?? new UiAccessibilityPreferences();
        Origins = origins;
    }

    public UiEnvironmentViewport Viewport { get; }
    public float Scale { get; }
    public UiInputMode InputMode { get; }
    public string Locale { get; }
    public UiSymbolId Theme { get; }
    public UiAccessibilityPreferences Accessibility { get; }
    public UiEnvironmentOrigins Origins { get; }
}
