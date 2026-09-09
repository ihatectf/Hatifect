using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Stardew.Tests;

public sealed class WindowInputGateTests
{
    private static UiWindowInputObservation Ready => new(1, true, true, null, false, null, false, 0, 0, 0, 0);
    private static UiWindowInputObservation Pointer => Ready with
    {
        FocusedSemantic = "owner/name", FocusedTextField = true, Text = "", PointerInsideFocusedField = true,
        PointerPressed = 1, PointerReleased = 1
    };

    [Fact]
    public void RequiresFreshStableFramesAndOrdinaryEventsForEachStage()
    {
        var gate = new UiWindowInputGate("native-test");
        long frame = 0;
        Advance(gate, ref frame, Ready, UiWindowInputPhase.Ready);
        Advance(gate, ref frame, Pointer, UiWindowInputPhase.Pointer);
        Advance(gate, ref frame, Pointer with { Text = "native-test", TextReceived = 1 }, UiWindowInputPhase.Text);
        Advance(gate, ref frame, Pointer with { Text = "native-tes", BackspacePressed = 1, TextReceived = 1 }, UiWindowInputPhase.Backspace);
        Advance(gate, ref frame, Pointer with { FocusedSemantic = "owner/register", FocusedTextField = false, TabPressed = 1, BackspacePressed = 1, TextReceived = 1 }, UiWindowInputPhase.Tab);
        Assert.Equal(UiWindowInputPhase.Complete, gate.Phase);
        Assert.Null(gate.Observe(++frame, Ready));
    }

    [Fact]
    public void PointerCannotUseStaleEventsOrAReleaseWithoutPressOrWrongCoordinates()
    {
        var gate = new UiWindowInputGate("probe"); long frame = 0;
        Advance(gate, ref frame, Ready with { PointerPressed = 3, PointerReleased = 3 }, UiWindowInputPhase.Ready);
        foreach (var value in new[]
        {
            Pointer with { PointerPressed = 3, PointerReleased = 3 },
            Pointer with { PointerPressed = 3, PointerReleased = 4 },
            Pointer with { PointerPressed = 4, PointerReleased = 4, PointerInsideFocusedField = false }
        })
        {
            Assert.Null(gate.Observe(++frame, value)); Assert.Null(gate.Observe(++frame, value));
            Assert.Equal(UiWindowInputPhase.Pointer, gate.Phase);
        }
    }

    [Theory]
    [InlineData(false, "owner/name", "probe")]
    [InlineData(true, "owner/other", "probe")]
    [InlineData(true, "owner/name", "wrong")]
    public void TextRequiresVisibleSameFocusedFieldAndExactProbe(bool visible, string semantic, string text)
    {
        var gate = new UiWindowInputGate("probe"); long frame = 0;
        Advance(gate, ref frame, Ready, UiWindowInputPhase.Ready);
        Advance(gate, ref frame, Pointer, UiWindowInputPhase.Pointer);
        var value = Pointer with { Visible = visible, FocusedSemantic = semantic, Text = text, TextReceived = 1 };
        Assert.Null(gate.Observe(++frame, value)); Assert.Null(gate.Observe(++frame, value));
        Assert.Equal(UiWindowInputPhase.Text, gate.Phase);
    }

    [Fact]
    public void MatchingSourceTextWithoutNativeSubscriberEventCannotPass()
    {
        var gate = new UiWindowInputGate("probe"); long frame = 0;
        Advance(gate, ref frame, Ready, UiWindowInputPhase.Ready);
        Advance(gate, ref frame, Pointer, UiWindowInputPhase.Pointer);
        var seeded = Pointer with { Text = "probe" };
        Assert.Null(gate.Observe(++frame, seeded)); Assert.Null(gate.Observe(++frame, seeded));
        Assert.Equal(UiWindowInputPhase.Text, gate.Phase);
    }

    [Fact]
    public void BackspaceRequiresNewEventAndExactlyOneRemovedCharacter()
    {
        var gate = new UiWindowInputGate("probe"); long frame = 0;
        Advance(gate, ref frame, Ready, UiWindowInputPhase.Ready);
        Advance(gate, ref frame, Pointer, UiWindowInputPhase.Pointer);
        Advance(gate, ref frame, Pointer with { Text = "probe", TextReceived = 1 }, UiWindowInputPhase.Text);
        foreach (var value in new[] { Pointer with { Text = "prob", TextReceived = 1 }, Pointer with { Text = "pro", BackspacePressed = 1, TextReceived = 1 } })
        {
            Assert.Null(gate.Observe(++frame, value)); Assert.Null(gate.Observe(++frame, value));
        }
        Assert.Equal(UiWindowInputPhase.Backspace, gate.Phase);
    }

    [Fact]
    public void TabRequiresFreshEventAndDifferentVisibleFocus()
    {
        var gate = new UiWindowInputGate("probe"); long frame = 0;
        Advance(gate, ref frame, Ready, UiWindowInputPhase.Ready);
        Advance(gate, ref frame, Pointer, UiWindowInputPhase.Pointer);
        Advance(gate, ref frame, Pointer with { Text = "probe", TextReceived = 1 }, UiWindowInputPhase.Text);
        var back = Pointer with { Text = "prob", TextReceived = 1, BackspacePressed = 1 };
        Advance(gate, ref frame, back, UiWindowInputPhase.Backspace);
        foreach (var value in new[] { back with { TabPressed = 1 }, back with { FocusedSemantic = "other" },
            back with { TabPressed = 1, FocusedSemantic = null }, back with { TabPressed = 1, FocusedSemantic = "other", Visible = false } })
        {
            Assert.Null(gate.Observe(++frame, value)); Assert.Null(gate.Observe(++frame, value));
        }
        Assert.Equal(UiWindowInputPhase.Tab, gate.Phase);
    }

    [Fact]
    public void ReplacementCannotCompletePredecessorInputSequence()
    {
        var gate = new UiWindowInputGate("probe"); long frame = 0;
        Advance(gate, ref frame, Ready, UiWindowInputPhase.Ready);
        Advance(gate, ref frame, Pointer, UiWindowInputPhase.Pointer);
        var replacement = Pointer with { SurfaceEpoch = 2, Text = "probe" };
        Assert.Null(gate.Observe(++frame, replacement));
        Assert.Equal(UiWindowInputPhase.Ready, gate.Phase);
        Assert.Equal(UiWindowInputPhase.Ready, gate.Observe(++frame, replacement));
        Assert.Equal(UiWindowInputPhase.Pointer, gate.Phase);
        Assert.Null(gate.Observe(++frame, replacement));
    }

    [Fact]
    public void HiddenFrameBreaksStabilityAndDuplicateFrameNeverAdvances()
    {
        var gate = new UiWindowInputGate("probe");
        Assert.Null(gate.Observe(1, Ready));
        Assert.Null(gate.Observe(1, Ready));
        Assert.Null(gate.Observe(2, Ready with { Visible = false }));
        Assert.Null(gate.Observe(3, Ready));
        Assert.Equal(UiWindowInputPhase.Ready, gate.Observe(4, Ready));
    }

    [Fact]
    public void DifferentAcceptedVersionsDoNotCountAsStableFrames()
    {
        var gate = new UiWindowInputGate("probe");
        Assert.Null(gate.Observe(1, Ready));
        Assert.Null(gate.Observe(2, Ready with { SceneVersion = 1 }));
        Assert.Null(gate.Observe(3, Ready with { SceneVersion = 1, FrameVersion = 1 }));
        Assert.Equal(UiWindowInputPhase.Ready, gate.Observe(4, Ready with { SceneVersion = 1, FrameVersion = 1 }));
    }

    private static void Advance(UiWindowInputGate gate, ref long frame, UiWindowInputObservation value, UiWindowInputPhase phase)
    {
        Assert.Null(gate.Observe(++frame, value));
        Assert.Null(gate.Observe(frame, value));
        Assert.Equal(phase, gate.Observe(++frame, value));
    }
}
