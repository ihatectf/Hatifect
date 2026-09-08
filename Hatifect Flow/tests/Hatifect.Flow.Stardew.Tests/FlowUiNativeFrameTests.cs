using System;
using StardewValley;
using Hatifect.Flow.Diagnostics;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowUiNativeFrameTests
{
    private static FlowUiNativeFrame Scale75 => new(.75f, .75f, .75f,
        1470, 956, 1960, 1275, 1960, 1275, 1470, 956, 1470, 956, 1470, 956, 0);

    [Fact]
    public void ActionProfileRejectsAutoRestoredAfterItsOneTimeApplication()
    {
        var probe = new FlowUiNativeFrameSettling();
        var mode = Options.GamepadModes.Auto;
        bool controls = false;
        void Apply() => mode = Options.GamepadModes.ForceOff;
        probe.Reset();
        probe.ObserveCompletedDraw(Scale75);
        probe.ObserveCompletedDraw(Scale75);
        Assert.False(probe.CompleteProfile(Apply));
        FlowUiActionsAcceptance.RequireEnglishInput(mode, controls);

        mode = Options.GamepadModes.Auto;
        probe.ObserveCompletedDraw(Scale75);
        probe.ObserveCompletedDraw(Scale75);
        Assert.True(probe.CompleteProfile(Apply));
        Assert.Throws<InvalidOperationException>(() => FlowUiActionsAcceptance.RequireEnglishInput(mode, controls));
        Assert.Throws<InvalidOperationException>(() => FlowUiActionsAcceptance.RequireEnglishInput(Options.GamepadModes.ForceOff, true));
    }

    [Fact]
    public void NativeResizeCompletesBeforeOneProfileReapplyAndFreshPresentation()
    {
        var probe = new FlowUiNativeFrameSettling();
        int applications = 0;
        probe.Reset();
        probe.ObserveCompletedDraw(Scale75);
        Assert.False(probe.CompleteProfile(() => applications++));
        Assert.Equal(0, applications);
        probe.ObserveCompletedDraw(Scale75);
        Assert.False(probe.CompleteProfile(() => applications++));
        Assert.Equal(1, applications);
        Assert.Null(probe.Settled);
        probe.ObserveCompletedDraw(Scale75);
        Assert.False(probe.CompleteProfile(() => applications++));
        probe.ObserveCompletedDraw(Scale75);
        Assert.True(probe.CompleteProfile(() => applications++));
        Assert.True(probe.CompleteProfile(() => applications++));
        Assert.Equal(1, applications);
    }

    [Fact]
    public void AppliedScaleWithoutRecreatedTargetCannotBecomeSettled()
    {
        var probe = new FlowUiNativeFrameSettling();
        FlowUiNativeFrame stale = Scale75 with { UiTargetWidth = 1470, UiTargetHeight = 956 };
        probe.ObserveCompletedDraw(stale);
        probe.ObserveCompletedDraw(stale);
        Assert.Null(probe.Settled);

        probe.ObserveCompletedDraw(Scale75);
        Assert.Null(probe.Settled);
        probe.ObserveCompletedDraw(Scale75);
        Assert.Equal(Scale75, probe.Settled);
    }

    [Fact]
    public void PendingRequestAndMissingTargetBreakConsecutiveDrawEvidence()
    {
        var probe = new FlowUiNativeFrameSettling();
        probe.ObserveCompletedDraw(Scale75);
        probe.ObserveCompletedDraw(Scale75 with { DesiredScale = 1 });
        Assert.Null(probe.Settled);
        probe.ObserveCompletedDraw(Scale75);
        Assert.Null(probe.Settled);
        probe.ObserveCompletedDraw(Scale75 with { UiTargetWidth = 0 });
        probe.ObserveCompletedDraw(Scale75);
        Assert.Null(probe.Settled);
        probe.ObserveCompletedDraw(Scale75);
        Assert.Equal(Scale75, probe.Settled);
    }

    [Fact]
    public void RestorationResetRequiresFreshDrawsEvenWhenDimensionsDoNotChange()
    {
        var probe = new FlowUiNativeFrameSettling();
        probe.ObserveCompletedDraw(Scale75);
        probe.ObserveCompletedDraw(Scale75);
        Assert.NotNull(probe.Settled);
        probe.Reset();
        Assert.Null(probe.Settled);
        probe.ObserveCompletedDraw(Scale75);
        Assert.Null(probe.Settled);
        probe.ObserveCompletedDraw(Scale75);
        Assert.Equal(Scale75, probe.Settled);
    }
}
