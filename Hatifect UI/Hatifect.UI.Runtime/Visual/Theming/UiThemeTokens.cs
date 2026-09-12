using System;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Visual.Theming;

public static class UiThemeTokens
{
    public static readonly UiThemeToken<UiSurface> SurfaceCanvas = Token<UiSurface>("Surface.Canvas", UiSemanticType.SurfaceToken);
    public static readonly UiThemeToken<UiSurface> SurfaceRaised = Token<UiSurface>("Surface.Raised", UiSemanticType.SurfaceToken);
    public static readonly UiThemeToken<UiSurface> SurfaceSecondary = Token<UiSurface>("Surface.Secondary", UiSemanticType.SurfaceToken);
    public static readonly UiThemeToken<UiSurface> SurfaceHover = Token<UiSurface>("Surface.Hover", UiSemanticType.SurfaceToken);
    public static readonly UiThemeToken<UiSurface> SurfacePressed = Token<UiSurface>("Surface.Pressed", UiSemanticType.SurfaceToken);
    public static readonly UiThemeToken<UiSurface> SurfaceDisabled = Token<UiSurface>("Surface.Disabled", UiSemanticType.SurfaceToken);
    public static readonly UiThemeToken<UiSurface> SurfacePopup = Token<UiSurface>("Surface.Popup", UiSemanticType.SurfaceToken);
    public static readonly UiThemeToken<UiSurface> SurfaceModal = Token<UiSurface>("Surface.Modal", UiSemanticType.SurfaceToken);

    public static readonly UiThemeToken<UiColor> TextPrimary = Token<UiColor>("Text.Primary", UiSemanticType.ColorToken);
    public static readonly UiThemeToken<UiColor> TextSecondary = Token<UiColor>("Text.Secondary", UiSemanticType.ColorToken);
    public static readonly UiThemeToken<UiColor> TextMuted = Token<UiColor>("Text.Muted", UiSemanticType.ColorToken);
    public static readonly UiThemeToken<UiColor> TextAccent = Token<UiColor>("Text.Accent", UiSemanticType.ColorToken);
    public static readonly UiThemeToken<UiColor> TextDanger = Token<UiColor>("Text.Danger", UiSemanticType.ColorToken);
    public static readonly UiThemeToken<UiColor> TextSuccess = Token<UiColor>("Text.Success", UiSemanticType.ColorToken);
    public static readonly UiThemeToken<UiColor> TextInputPrompt = Token<UiColor>("Text.InputPrompt", UiSemanticType.ColorToken);
    public static readonly UiThemeToken<UiColor> Accent = Token<UiColor>("Accent", UiSemanticType.ColorToken);

    public static readonly UiThemeToken<UiSpacing> SpaceXS = Token<UiSpacing>("Space.XS", UiSemanticType.SpaceToken);
    public static readonly UiThemeToken<UiSpacing> SpaceS = Token<UiSpacing>("Space.S", UiSemanticType.SpaceToken);
    public static readonly UiThemeToken<UiSpacing> SpaceM = Token<UiSpacing>("Space.M", UiSemanticType.SpaceToken);
    public static readonly UiThemeToken<UiSpacing> SpaceL = Token<UiSpacing>("Space.L", UiSemanticType.SpaceToken);
    public static readonly UiThemeToken<UiSpacing> SpaceXL = Token<UiSpacing>("Space.XL", UiSemanticType.SpaceToken);

    public static readonly UiThemeToken<UiCornerRadius> RadiusS = Token<UiCornerRadius>("Radius.S", UiSemanticType.RadiusToken);
    public static readonly UiThemeToken<UiCornerRadius> RadiusM = Token<UiCornerRadius>("Radius.M", UiSemanticType.RadiusToken);
    public static readonly UiThemeToken<UiCornerRadius> RadiusL = Token<UiCornerRadius>("Radius.L", UiSemanticType.RadiusToken);
    public static readonly UiThemeToken<UiCornerRadius> RadiusXL = Token<UiCornerRadius>("Radius.XL", UiSemanticType.RadiusToken);

    public static readonly UiThemeToken<UiMotion> MotionNone = Token<UiMotion>("Motion.None", UiSemanticType.MotionToken);
    public static readonly UiThemeToken<UiMotion> MotionFast = Token<UiMotion>("Motion.Fast", UiSemanticType.MotionToken);
    public static readonly UiThemeToken<UiMotion> MotionNormal = Token<UiMotion>("Motion.Normal", UiSemanticType.MotionToken);
    public static readonly UiThemeToken<UiMotion> MotionSlow = Token<UiMotion>("Motion.Slow", UiSemanticType.MotionToken);

    public static readonly UiThemeToken<UiTypography> TypographyBody = Token<UiTypography>("Typography.Body", UiSemanticType.TypographyToken);
    public static readonly UiThemeToken<UiTypography> TypographyLabel = Token<UiTypography>("Typography.Label", UiSemanticType.TypographyToken);
    public static readonly UiThemeToken<UiTypography> TypographyTitle = Token<UiTypography>("Typography.Title", UiSemanticType.TypographyToken);
    public static readonly UiThemeToken<UiTypography> TypographyInputPrompt = Token<UiTypography>("Typography.InputPrompt", UiSemanticType.TypographyToken);

    public static readonly UiThemeToken<UiBorder> BorderSubtle = Token<UiBorder>("Border.Subtle", UiSemanticType.Border);
    public static readonly UiThemeToken<UiBorder> BorderStrong = Token<UiBorder>("Border.Strong", UiSemanticType.Border);
    public static readonly UiThemeToken<UiBorder> BorderFocus = Token<UiBorder>("Border.Focus", UiSemanticType.Border);

    public static readonly UiThemeToken<UiElevation> ElevationNone = Token<UiElevation>("Elevation.None", UiSemanticType.ElevationToken);
    public static readonly UiThemeToken<UiElevation> ElevationLow = Token<UiElevation>("Elevation.Low", UiSemanticType.ElevationToken);
    public static readonly UiThemeToken<UiElevation> ElevationHigh = Token<UiElevation>("Elevation.High", UiSemanticType.ElevationToken);

    public static readonly UiThemeToken<UiTransform> TransformNone = Token<UiTransform>("Transform.None", UiSemanticType.TransformToken);
    public static readonly UiThemeToken<UiTransform> TransformRaised = Token<UiTransform>("Transform.Raised", UiSemanticType.TransformToken);
    public static readonly UiThemeToken<UiTransform> TransformPressed = Token<UiTransform>("Transform.Pressed", UiSemanticType.TransformToken);

    public static readonly UiThemeToken<UiOpacity> OpacityHidden = Token<UiOpacity>("Opacity.Hidden", UiSemanticType.Opacity);
    public static readonly UiThemeToken<UiOpacity> OpacityDisabled = Token<UiOpacity>("Opacity.Disabled", UiSemanticType.Opacity);
    public static readonly UiThemeToken<UiOpacity> OpacityVisible = Token<UiOpacity>("Opacity.Visible", UiSemanticType.Opacity);

    private static UiThemeToken<T> Token<T>(string name, UiSemanticType semanticType) where T : notnull
        => new(new UiSymbolId("Hatifect.UI", $"token/{name}"), semanticType);
}

public static class UiThemePresets
{
    public static UiTheme Dark()
    {
        UiColor canvas = UiColor.FromRgb(0x17191D);
        UiColor raised = UiColor.FromRgb(0x242830);
        UiColor secondary = UiColor.FromRgb(0x1D2026);
        UiColor accent = UiColor.FromRgb(0xE9B44C);
        UiColor transparentShadow = new(0, 0, 0, 96);

        return new UiThemeBuilder()
            .Set(UiThemeTokens.SurfaceCanvas, UiSurface.Solid(canvas))
            .Set(UiThemeTokens.SurfaceRaised, UiSurface.Solid(raised))
            .Set(UiThemeTokens.SurfaceSecondary, UiSurface.Solid(secondary))
            .Set(UiThemeTokens.SurfaceHover, UiSurface.Solid(UiColor.FromRgb(0x303642)))
            .Set(UiThemeTokens.SurfacePressed, UiSurface.Solid(UiColor.FromRgb(0x3A414F)))
            .Set(UiThemeTokens.SurfaceDisabled, UiSurface.Solid(UiColor.FromRgb(0x202329)))
            .Set(UiThemeTokens.SurfacePopup, UiSurface.Solid(raised))
            .Set(UiThemeTokens.SurfaceModal, UiSurface.Solid(UiColor.FromRgb(0x101216)))
            .Set(UiThemeTokens.TextPrimary, UiColor.FromRgb(0xF4F1EA))
            .Set(UiThemeTokens.TextSecondary, UiColor.FromRgb(0xC4C0B8))
            .Set(UiThemeTokens.TextMuted, UiColor.FromRgb(0x858992))
            .Set(UiThemeTokens.TextAccent, accent)
            .Set(UiThemeTokens.TextDanger, UiColor.FromRgb(0xEF6A6A))
            .Set(UiThemeTokens.TextSuccess, UiColor.FromRgb(0x75C991))
            .Set(UiThemeTokens.TextInputPrompt, accent)
            .Set(UiThemeTokens.Accent, accent)
            .Set(UiThemeTokens.SpaceXS, new UiSpacing(2))
            .Set(UiThemeTokens.SpaceS, new UiSpacing(4))
            .Set(UiThemeTokens.SpaceM, new UiSpacing(8))
            .Set(UiThemeTokens.SpaceL, new UiSpacing(12))
            .Set(UiThemeTokens.SpaceXL, new UiSpacing(20))
            .Set(UiThemeTokens.RadiusS, new UiCornerRadius(2))
            .Set(UiThemeTokens.RadiusM, new UiCornerRadius(4))
            .Set(UiThemeTokens.RadiusL, new UiCornerRadius(8))
            .Set(UiThemeTokens.RadiusXL, new UiCornerRadius(12))
            .Set(UiThemeTokens.MotionNone, new UiMotion(TimeSpan.Zero, UiEasing.Linear))
            .Set(UiThemeTokens.MotionFast, new UiMotion(TimeSpan.FromMilliseconds(80)))
            .Set(UiThemeTokens.MotionNormal, new UiMotion(TimeSpan.FromMilliseconds(160)))
            .Set(UiThemeTokens.MotionSlow, new UiMotion(TimeSpan.FromMilliseconds(260), UiEasing.EaseInOut))
            .Set(UiThemeTokens.TypographyBody, new UiTypography("Body", 16, 1.25f))
            .Set(UiThemeTokens.TypographyLabel, new UiTypography("Body", 14, 1.2f, UiFontWeight.Medium))
            .Set(UiThemeTokens.TypographyTitle, new UiTypography("Display", 22, 1.15f, UiFontWeight.Bold))
            .Set(UiThemeTokens.TypographyInputPrompt, new UiTypography("Body", 12, 1.2f, UiFontWeight.Bold))
            .Set(UiThemeTokens.BorderSubtle, new UiBorder(UiColor.FromRgb(0x383D47), 1))
            .Set(UiThemeTokens.BorderStrong, new UiBorder(UiColor.FromRgb(0x5A616E), 1))
            .Set(UiThemeTokens.BorderFocus, new UiBorder(accent, 2))
            .Set(UiThemeTokens.ElevationNone, new UiElevation(0, 0, 0, transparentShadow))
            .Set(UiThemeTokens.ElevationLow, new UiElevation(0, 2, 4, transparentShadow))
            .Set(UiThemeTokens.ElevationHigh, new UiElevation(0, 6, 12, transparentShadow))
            .Set(UiThemeTokens.TransformNone, new UiTransform(0, 0))
            .Set(UiThemeTokens.TransformRaised, new UiTransform(0, -1))
            .Set(UiThemeTokens.TransformPressed, new UiTransform(0, 1, 0.99f))
            .Set(UiThemeTokens.OpacityHidden, new UiOpacity(0))
            .Set(UiThemeTokens.OpacityDisabled, new UiOpacity(0.45f))
            .Set(UiThemeTokens.OpacityVisible, new UiOpacity(1))
            .Build(new UiSymbolId("Hatifect.UI", "theme/Dark"));
    }
}
