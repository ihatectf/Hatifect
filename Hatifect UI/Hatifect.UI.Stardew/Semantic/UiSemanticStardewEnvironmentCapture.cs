using StardewValley;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Semantics;
using RuntimeRect = Hatifect.UI.Runtime.Layout.UiRect;

namespace Hatifect.UI.Stardew.Semantic;

/// <summary>
/// Captures values on the owning game thread. The host supplies its already logical viewport;
/// the applied UI scale is descriptive and must not transform those dimensions a second time.
/// No global history or platform object is retained. An unchanged capture reuses the prior snapshot.
/// </summary>
internal static class UiSemanticStardewEnvironmentCapture
{
    private static readonly UiAccessibilityPreferences DefaultAccessibility = new();
    private static readonly UiAccessibilityPreferences ContrastAccessibility = new(HighContrast: true);
    private static readonly UiEnvironmentOrigins Origins = new(
        Viewport: "Host logical viewport (Game1.uiViewport)",
        Scale: "Game1.options.uiScale (applied)",
        InputMode: "Game1.options.gamepadControls",
        Locale: "LocalizedContentManager.LanguageCodeString(current); empty English asset suffix maps to en",
        Theme: "UiSemanticTheme selected by host appearance policy",
        Accessibility: "HighContrast from UiSemanticTheme; ReducedMotion framework default false");

    internal static UiEnvironment Capture(RuntimeRect viewport, UiSemanticTheme theme,
        UiEnvironment? previous = null)
    {
        var options = Game1.options;
        string locale = ResolveLocale(LocalizedContentManager.CurrentLanguageCode);
        return FromSnapshot(viewport.Width, viewport.Height, options.uiScale, options.gamepadControls,
            locale, theme, previous);
    }

    internal static string ResolveLocale(LocalizedContentManager.LanguageCode language)
    {
        // Read the current mod's code directly through the game's mapping. CurrentLanguageString
        // caches an asset suffix, while a mod-to-mod switch can keep the same LanguageCode.mod enum.
        return NormalizeLocale(language, LocalizedContentManager.LanguageCodeString(language));
    }

    internal static string NormalizeLocale(LocalizedContentManager.LanguageCode language, string assetCode)
    {
        if (language == LocalizedContentManager.LanguageCode.en && assetCode == string.Empty) return "en";
        if (string.IsNullOrWhiteSpace(assetCode))
            throw new ArgumentException("A non-English game language requires its actual locale code.", nameof(assetCode));
        return assetCode;
    }

    // Pure mapping seam: tests need neither Game1 initialization nor a graphics device.
    internal static UiEnvironment FromSnapshot(float logicalWidth, float logicalHeight, float appliedScale,
        bool gamepadControls, string locale, UiSemanticTheme theme, UiEnvironment? previous = null)
    {
        UiSymbolId themeId = UiSemanticThemes.Resolve(theme).Id;
        UiInputMode input = gamepadControls ? UiInputMode.Controller : UiInputMode.MouseKeyboard;
        UiAccessibilityPreferences accessibility = theme == UiSemanticTheme.HighContrast
            ? ContrastAccessibility : DefaultAccessibility;

        if (previous != null && previous.Viewport.Width == logicalWidth && previous.Viewport.Height == logicalHeight
            && previous.Scale == appliedScale && previous.InputMode == input && previous.Locale == locale
            && previous.Theme == themeId && previous.Accessibility == accessibility && previous.Origins == Origins)
            return previous;

        return new UiEnvironment(new UiEnvironmentViewport(logicalWidth, logicalHeight), appliedScale,
            input, locale, themeId, accessibility, Origins);
    }
}
