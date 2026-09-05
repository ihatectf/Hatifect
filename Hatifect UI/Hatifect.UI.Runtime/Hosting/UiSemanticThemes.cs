using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;

namespace Hatifect.UI.Runtime.Hosting;

internal static class UiSemanticThemes
{
    private static readonly UiTheme Dark = UiThemePresets.Dark();
    private static readonly UiTheme Light = Build(false);
    private static readonly UiTheme Contrast = Build(true);
    internal static UiTheme Resolve(UiSemanticTheme theme) => theme switch
    {
        UiSemanticTheme.Dark => Dark,
        UiSemanticTheme.Light => Light,
        UiSemanticTheme.HighContrast => Contrast,
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };

    private static UiTheme Build(bool contrast)
    {
        UiColor foreground = UiColor.FromRgb(contrast ? 0xFFFFFFu : 0x17191Du);
        UiColor canvas = UiColor.FromRgb(contrast ? 0x000000u : 0xF6F3EBu);
        UiColor raised = UiColor.FromRgb(contrast ? 0x111111u : 0xFFFFFFu);
        UiColor accent = UiColor.FromRgb(contrast ? 0xFFFF00u : 0x704900u);
        return new UiThemeBuilder(Dark)
            .Set(UiThemeTokens.SurfaceCanvas, UiSurface.Solid(canvas))
            .Set(UiThemeTokens.SurfaceRaised, UiSurface.Solid(raised))
            .Set(UiThemeTokens.SurfaceSecondary, UiSurface.Solid(canvas))
            .Set(UiThemeTokens.SurfacePopup, UiSurface.Solid(raised))
            .Set(UiThemeTokens.SurfaceModal, UiSurface.Solid(canvas))
            .Set(UiThemeTokens.SurfaceHover, UiSurface.Solid(UiColor.FromRgb(contrast ? 0x333333u : 0xE5DDCEu)))
            .Set(UiThemeTokens.SurfacePressed, UiSurface.Solid(UiColor.FromRgb(contrast ? 0x444444u : 0xD6CBB8u)))
            .Set(UiThemeTokens.SurfaceDisabled, UiSurface.Solid(canvas))
            .Set(UiThemeTokens.TextPrimary, foreground)
            .Set(UiThemeTokens.TextSecondary, foreground)
            .Set(UiThemeTokens.TextMuted, UiColor.FromRgb(contrast ? 0xCCCCCCu : 0x59554Fu))
            .Set(UiThemeTokens.TextAccent, accent)
            .Set(UiThemeTokens.Accent, accent)
            .Set(UiThemeTokens.BorderFocus, new UiBorder(accent, 2))
            .Set(UiThemeTokens.BorderStrong, new UiBorder(foreground, 1))
            .Set(UiThemeTokens.BorderSubtle, new UiBorder(UiColor.FromRgb(contrast ? 0xFFFFFFu : 0x777168u), 1))
            .Build(new UiSymbolId("Hatifect.UI", contrast ? "theme/HighContrast" : "theme/Light"));
    }
}
