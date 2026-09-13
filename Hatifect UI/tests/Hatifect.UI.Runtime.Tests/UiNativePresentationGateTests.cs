using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class UiNativePresentationGateTests
{
    private const int Auto = 0, ForceOff = 2;
    private static UiNativePresentationFrame Frame => new(.75f, .75f, .75f,
        1280, 720, 1707, 960, 1707, 960, 1280, 720, 1280, 720, 1280, 720, 0, "en-US", false, Auto);
    private static UiNativePresentationGate Gate(int mode = ForceOff) => new(.75f, "en-US", false, mode);

    [Fact]
    public void NativeTargetsSettleBeforeOneProfileWriteAndTwoNewCompletedDraws()
    {
        var gate = Gate(); int writes = 0;
        gate.ObserveCompletedDraw(1, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        Assert.Equal(0, writes);
        gate.ObserveCompletedDraw(2, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        Assert.Equal(1, writes);
        Assert.Null(gate.Settled);
        var repaired = Frame with { GamepadMode = ForceOff };
        gate.ObserveCompletedDraw(3, repaired);
        Assert.False(gate.CompleteProfile(() => writes++));
        gate.ObserveCompletedDraw(4, repaired);
        Assert.True(gate.CompleteProfile(() => writes++));
        Assert.True(gate.CompleteProfile(() => writes++));
        Assert.Equal(1, writes);
        Assert.Equal(repaired, gate.Settled);
    }

    [Theory]
    [InlineData("stale-target")]
    [InlineData("missing-target")]
    [InlineData("base-scale")]
    [InlineData("desired-scale")]
    [InlineData("applied-scale")]
    [InlineData("nonfinite-scale")]
    [InlineData("bound-target")]
    [InlineData("locale")]
    public void InvalidNativeFrameCannotApplyProfileOrBridgeTwoValidDraws(string mismatch)
    {
        var gate = Gate(); int writes = 0;
        var invalid = mismatch switch
        {
            "stale-target" => Frame with { UiTargetWidth = 1280, UiTargetHeight = 720 },
            "missing-target" => Frame with { ScreenWidth = 0 },
            "base-scale" => Frame with { BaseScale = 1 },
            "desired-scale" => Frame with { BaseScale = 1, DesiredScale = 1 },
            "applied-scale" => Frame with { AppliedScale = 1 },
            "nonfinite-scale" => Frame with { AppliedScale = float.NaN },
            "bound-target" => Frame with { BoundTargets = 1 },
            "locale" => Frame with { Locale = "ru-RU" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        gate.ObserveCompletedDraw(1, invalid);
        gate.ObserveCompletedDraw(2, invalid);
        Assert.Null(gate.Settled);
        Assert.False(gate.CompleteProfile(() => writes++));
        gate.ObserveCompletedDraw(3, Frame);
        gate.ObserveCompletedDraw(4, invalid);
        Assert.Null(gate.Settled);
        gate.ObserveCompletedDraw(5, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        Assert.Equal(0, writes);
        gate.ObserveCompletedDraw(6, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void DuplicateAndOlderDrawsCannotCompleteNativeSettling()
    {
        var gate = Gate(); int writes = 0;
        gate.ObserveCompletedDraw(10, Frame);
        gate.ObserveCompletedDraw(10, Frame);
        gate.ObserveCompletedDraw(9, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        Assert.Equal(0, writes);
        gate.ObserveCompletedDraw(11, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void AutoWithFalseControlsAfterTheOneProfileWriteIsRejected()
    {
        var gate = Gate(); int writes = 0;
        gate.ObserveCompletedDraw(1, Frame); gate.ObserveCompletedDraw(2, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        gate.ObserveCompletedDraw(3, Frame); gate.ObserveCompletedDraw(4, Frame);
        Assert.Throws<InvalidOperationException>(() => gate.CompleteProfile(() => writes++));
        Assert.Equal(1, writes);
    }

    [Fact]
    public void RestorationCanAcceptTheExactOriginalAutoProfileAfterFreshDraws()
    {
        var gate = Gate(Auto); int writes = 0;
        gate.ObserveCompletedDraw(1, Frame); gate.ObserveCompletedDraw(2, Frame);
        Assert.False(gate.CompleteProfile(() => writes++));
        gate.ObserveCompletedDraw(3, Frame); gate.ObserveCompletedDraw(4, Frame);
        Assert.True(gate.CompleteProfile(() => writes++));
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void ExactMouseAndControllerProfilesNeedBothModeAndControls(bool controls, int mode)
    {
        var gate = new UiNativePresentationGate(.75f, "en-US", controls, mode);
        gate.ObserveCompletedDraw(1, Frame); gate.ObserveCompletedDraw(2, Frame);
        Assert.False(gate.CompleteProfile(() => { }));
        var wrong = Frame with { GamepadMode = mode, GamepadControls = !controls };
        gate.ObserveCompletedDraw(3, wrong); gate.ObserveCompletedDraw(4, wrong);
        Assert.Throws<InvalidOperationException>(() => gate.CompleteProfile(() => { }));
        var exact = wrong with { GamepadControls = controls };
        gate.ObserveCompletedDraw(5, exact);
        Assert.False(gate.CompleteProfile(() => { }));
        gate.ObserveCompletedDraw(6, exact);
        Assert.True(gate.CompleteProfile(() => { }));
        Assert.Equal(exact, gate.Settled);
    }

    [Fact]
    public void AResizeAfterAcceptanceNeedsTwoFreshMatchingTargetDraws()
    {
        var gate = Gate();
        gate.ObserveCompletedDraw(1, Frame); gate.ObserveCompletedDraw(2, Frame);
        Assert.False(gate.CompleteProfile(() => { }));
        var exact = Frame with { GamepadMode = ForceOff };
        gate.ObserveCompletedDraw(3, exact); gate.ObserveCompletedDraw(4, exact);
        Assert.True(gate.CompleteProfile(() => { }));
        var resized = exact with { UiWidth = 1920, UiHeight = 1080 };
        gate.ObserveCompletedDraw(5, resized);
        Assert.False(gate.CompleteProfile(() => throw new InvalidOperationException("Unexpected second profile write.")));
        resized = resized with { UiTargetWidth = 1920, UiTargetHeight = 1080 };
        gate.ObserveCompletedDraw(6, resized);
        Assert.False(gate.CompleteProfile(() => { }));
        gate.ObserveCompletedDraw(7, resized);
        Assert.True(gate.CompleteProfile(() => throw new InvalidOperationException("Unexpected second profile write.")));
    }

    [Fact]
    public void FailedProfileWriteCanRetryButCannotReuseItsPreWriteDraws()
    {
        var gate = Gate(); int writes = 0;
        gate.ObserveCompletedDraw(1, Frame); gate.ObserveCompletedDraw(2, Frame);
        Assert.Throws<InvalidOperationException>(() => gate.CompleteProfile(() => throw new InvalidOperationException("Write failed.")));
        Assert.False(gate.CompleteProfile(() => writes++));
        Assert.Null(gate.Settled);
        Assert.Equal(1, writes);
        gate.ObserveCompletedDraw(3, Frame with { GamepadMode = ForceOff });
        Assert.False(gate.CompleteProfile(() => writes++));
        gate.ObserveCompletedDraw(4, Frame with { GamepadMode = ForceOff });
        Assert.True(gate.CompleteProfile(() => writes++));
    }
}
