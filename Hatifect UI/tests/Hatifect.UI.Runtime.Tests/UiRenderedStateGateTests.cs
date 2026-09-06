using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class UiRenderedStateGateTests
{
    [Fact]
    public void DesiredScaleReadbackCannotAcceptFramesBeforeAppliedScaleAndViewportChange()
    {
        var gate = new UiRenderedStateGate("ru", 1.5f, "Dark");
        UiRenderedStateObservation stale = Frame with
        {
            BaseScale = 1, PixelScale = 1, ViewportWidth = 1280, ViewportHeight = 720,
            MenuWidth = 1280, MenuHeight = 720
        };

        Assert.False(gate.Observe(1, stale));
        Assert.False(gate.Observe(2, stale));
        Assert.False(gate.Observe(3, Frame));
        Assert.True(gate.Observe(4, Frame));
    }

    [Fact]
    public void DuplicateOrOlderCallbacksDoNotCountAsAnotherRenderedFrame()
    {
        var gate = new UiRenderedStateGate("ru", 1.5f, "Dark");
        Assert.False(gate.Observe(10, Frame));
        Assert.False(gate.Observe(10, Frame));
        Assert.False(gate.Observe(9, Frame));
        Assert.True(gate.Observe(11, Frame));
        Assert.False(gate.Observe(12, Frame));
    }

    [Fact]
    public void ResizeRequiresTwoNewConsistentCompletedFrames()
    {
        var gate = new UiRenderedStateGate("ru", 1.5f, "Dark");
        UiRenderedStateObservation resized = Frame with
        {
            BackBufferWidth = 1440, BackBufferHeight = 900,
            ViewportWidth = 960, ViewportHeight = 600, MenuWidth = 960, MenuHeight = 600
        };

        Assert.False(gate.Observe(1, Frame));
        Assert.False(gate.Observe(2, resized));
        Assert.True(gate.Observe(3, resized));
    }

    [Theory]
    [InlineData("game-locale")]
    [InlineData("scene-locale")]
    [InlineData("theme")]
    [InlineData("desired-scale")]
    [InlineData("viewport")]
    [InlineData("menu-origin")]
    [InlineData("menu-size")]
    [InlineData("tree")]
    [InlineData("probe")]
    [InlineData("fade")]
    [InlineData("nonfinite-scale")]
    public void InvalidIntermediateFrameBreaksConfirmation(string mismatch)
    {
        var gate = new UiRenderedStateGate("ru", 1.5f, "Dark");
        UiRenderedStateObservation invalid = mismatch switch
        {
            "game-locale" => Frame with { GameLocale = "en" },
            "scene-locale" => Frame with { SceneLocale = "en" },
            "theme" => Frame with { Theme = "Light" },
            "desired-scale" => Frame with { DesiredScale = 1 },
            "viewport" => Frame with { ViewportWidth = 853 },
            "menu-origin" => Frame with { MenuX = int.MinValue },
            "menu-size" => Frame with { MenuWidth = 1280 },
            "tree" => Frame with { HasValidTree = false },
            "probe" => Frame with { HasProbeText = false },
            "fade" => Frame with { LoadFadeFinished = false },
            "nonfinite-scale" => Frame with { PixelScale = float.NaN },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        Assert.False(gate.Observe(1, Frame));
        Assert.False(gate.Observe(2, invalid));
        Assert.False(gate.Observe(3, Frame));
        Assert.True(gate.Observe(4, Frame));
    }

    private static UiRenderedStateObservation Frame => new(
        "ru", "ru", "Dark", 1.5f, 1.5f, 1.5f,
        1280, 720, 854, 480, 0, 0, 854, 480, true, true, true);
}
