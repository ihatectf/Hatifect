using System;
using StardewValley;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Semantics;
using Hatifect.UI.Stardew.Semantic;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class EnvironmentCaptureTests
{
    [Theory]
    [InlineData(false, UiSemanticTheme.Dark, "en", "Wide", "theme/Dark", false)]
    [InlineData(true, UiSemanticTheme.Light, "ru", "Controller", "theme/Light", false)]
    [InlineData(false, UiSemanticTheme.HighContrast, "ru", "Wide", "theme/HighContrast", true)]
    public void CapturedPlatformValuesReachPlanningWithActualFacetOrigins(bool controller,
        UiSemanticTheme theme, string locale, string profile, string themePath, bool highContrast)
    {
        UiEnvironment captured = UiSemanticStardewEnvironmentCapture.FromSnapshot(1100, 700, 2, controller, locale, theme);

        Assert.Equal(1100, captured.Viewport.Width);
        Assert.Equal(700, captured.Viewport.Height);
        Assert.Equal(2, captured.Scale);
        Assert.Equal(controller ? UiInputMode.Controller : UiInputMode.MouseKeyboard, captured.InputMode);
        Assert.Equal(locale, captured.Locale);
        Assert.Equal(new UiSymbolId("Hatifect.UI", themePath), captured.Theme);
        Assert.Equal(highContrast, captured.Accessibility.HighContrast);
        Assert.False(captured.Accessibility.ReducedMotion);
        Assert.Equal(new UiSymbolId("Hatifect.UI", "profile/" + profile), UiPresentationProfiles.Resolve(captured).Id);
        Assert.Contains("Game1.uiViewport", captured.Origins.Viewport);
        Assert.Contains("options.uiScale (applied)", captured.Origins.Scale);
        Assert.Contains("gamepadControls", captured.Origins.InputMode);
        Assert.Contains("LanguageCodeString(current)", captured.Origins.Locale);
        Assert.Contains("UiSemanticTheme", captured.Origins.Theme);
        Assert.Contains("ReducedMotion framework default false", captured.Origins.Accessibility);
    }

    [Fact]
    public void UnchangedCaptureReusesTheSnapshotWithoutPerFrameAllocation()
    {
        UiEnvironment captured = Capture();
        for (int index = 0; index < 100; index++) _ = Capture(captured);
        UiEnvironment current = captured;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) current = Capture(current);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(captured, current);
        Assert.InRange(allocated, 0, 64);
    }

    [Theory]
    [InlineData(1101, 700, 2, false, "en", UiSemanticTheme.Dark)]
    [InlineData(1100, 701, 2, false, "en", UiSemanticTheme.Dark)]
    [InlineData(1100, 700, 1, false, "en", UiSemanticTheme.Dark)]
    [InlineData(1100, 700, 2, true, "en", UiSemanticTheme.Dark)]
    [InlineData(1100, 700, 2, false, "ru", UiSemanticTheme.Dark)]
    [InlineData(1100, 700, 2, false, "en", UiSemanticTheme.HighContrast)]
    public void ChangedFacetProducesANewSnapshotAndPreservesThePreviousCapture(float width, float height,
        float scale, bool controller, string locale, UiSemanticTheme theme)
    {
        UiEnvironment original = Capture();
        UiEnvironment changed = UiSemanticStardewEnvironmentCapture.FromSnapshot(width, height, scale, controller,
            locale, theme, original);

        Assert.NotSame(original, changed);
        Assert.NotEqual(original, changed);
        Assert.Equal(width, changed.Viewport.Width);
        Assert.Equal(height, changed.Viewport.Height);
        Assert.Equal(scale, changed.Scale);
        Assert.Equal(controller ? UiInputMode.Controller : UiInputMode.MouseKeyboard, changed.InputMode);
        Assert.Equal(locale, changed.Locale);
        Assert.Equal(theme == UiSemanticTheme.HighContrast, changed.Accessibility.HighContrast);
        Assert.Equal(Capture(), original);
        Assert.Same(changed, UiSemanticStardewEnvironmentCapture.FromSnapshot(width, height, scale, controller,
            locale, theme, changed));
    }

    [Fact]
    public void AForeignOriginOrPreferenceCannotBeReusedAsAPlatformCapture()
    {
        UiEnvironment actual = Capture();
        var foreign = new UiEnvironment(actual.Viewport, actual.Scale, actual.InputMode, actual.Locale, actual.Theme);
        var preference = new UiEnvironment(actual.Viewport, actual.Scale, actual.InputMode, actual.Locale, actual.Theme,
            new UiAccessibilityPreferences(ReducedMotion: true), actual.Origins);

        UiEnvironment fromForeign = Capture(foreign);
        UiEnvironment fromPreference = Capture(preference);

        Assert.NotSame(foreign, fromForeign);
        Assert.NotSame(preference, fromPreference);
        Assert.Equal(actual, fromForeign);
        Assert.Equal(actual, fromPreference);
        Assert.True(preference.Accessibility.ReducedMotion);
        Assert.Equal("Host", foreign.Origins.Viewport);
    }

    [Theory]
    [InlineData(0, 700, 2)]
    [InlineData(1100, 0, 2)]
    [InlineData(-1, 700, 2)]
    [InlineData(1100, -1, 2)]
    [InlineData(float.NaN, 700, 2)]
    [InlineData(1100, float.PositiveInfinity, 2)]
    [InlineData(1100, 700, 0)]
    [InlineData(1100, 700, -1)]
    [InlineData(1100, 700, float.NaN)]
    [InlineData(1100, 700, float.PositiveInfinity)]
    public void InvalidPlatformGeometryOrScaleRejectsInsteadOfClampingToAUsableEnvironment(
        float width, float height, float scale)
    {
        UiEnvironment original = Capture();
        Assert.Throws<ArgumentOutOfRangeException>(() => UiSemanticStardewEnvironmentCapture.FromSnapshot(
            width, height, scale, false, "en", UiSemanticTheme.Dark, original));
        Assert.Same(original, Capture(original));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingLocaleDoesNotSilentlyReusePreviousLanguage(string? locale)
        => Assert.Throws<ArgumentException>(() => UiSemanticStardewEnvironmentCapture.FromSnapshot(
            1100, 700, 2, false, locale!, UiSemanticTheme.Dark, Capture()));

    [Fact]
    public void UnknownThemeDoesNotReuseThePreviousValidTheme()
        => Assert.Throws<ArgumentOutOfRangeException>(() => UiSemanticStardewEnvironmentCapture.FromSnapshot(
            1100, 700, 2, false, "en", (UiSemanticTheme)99, Capture()));

    [Theory]
    [InlineData(LocalizedContentManager.LanguageCode.en, "en")]
    [InlineData(LocalizedContentManager.LanguageCode.ru, "ru-RU")]
    [InlineData(LocalizedContentManager.LanguageCode.pt, "pt-BR")]
    public void ActualGameLocaleMappingNormalizesEnglishAndPreservesRegionalCodes(
        LocalizedContentManager.LanguageCode language, string expected)
    {
        string assetCode = LocalizedContentManager.LanguageCodeString(language);
        string locale = UiSemanticStardewEnvironmentCapture.ResolveLocale(language);
        UiEnvironment captured = UiSemanticStardewEnvironmentCapture.FromSnapshot(1100, 700, 2,
            false, locale, UiSemanticTheme.Dark);

        Assert.Equal(expected, captured.Locale);
        if (language == LocalizedContentManager.LanguageCode.en) Assert.Equal(string.Empty, assetCode);
        else Assert.Equal(assetCode, captured.Locale);
    }

    [Fact]
    public void TwoCustomLocalesWithTheSameGameEnumCannotReuseThePreviousEnvironment()
    {
        const LocalizedContentManager.LanguageCode language = LocalizedContentManager.LanguageCode.mod;
        string firstLocale = UiSemanticStardewEnvironmentCapture.NormalizeLocale(language, "mod-a");
        string secondLocale = UiSemanticStardewEnvironmentCapture.NormalizeLocale(language, "mod-b");
        UiEnvironment first = UiSemanticStardewEnvironmentCapture.FromSnapshot(1100, 700, 2,
            false, firstLocale, UiSemanticTheme.Dark);
        UiEnvironment second = UiSemanticStardewEnvironmentCapture.FromSnapshot(1100, 700, 2,
            false, secondLocale, UiSemanticTheme.Dark, first);

        Assert.Equal("mod-a", first.Locale);
        Assert.Equal("mod-b", second.Locale);
        Assert.NotSame(first, second);
        Assert.Same(second, UiSemanticStardewEnvironmentCapture.FromSnapshot(1100, 700, 2,
            false, secondLocale, UiSemanticTheme.Dark, second));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingCustomLocaleCannotBecomeAnEnglishFallback(string? locale)
        => Assert.Throws<ArgumentException>(() => UiSemanticStardewEnvironmentCapture.NormalizeLocale(
            LocalizedContentManager.LanguageCode.mod, locale!));

    private static UiEnvironment Capture(UiEnvironment? previous = null)
        => UiSemanticStardewEnvironmentCapture.FromSnapshot(1100, 700, 2, false, "en", UiSemanticTheme.Dark, previous);
}
