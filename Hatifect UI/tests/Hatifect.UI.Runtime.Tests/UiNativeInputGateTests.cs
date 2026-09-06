using Hatifect.UI.Stardew;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class UiNativeInputGateTests
{
    private long _frame;
    private const string Probe = "native-42";
    private static UiNativeInputObservation Ready => new(true, false, "diagnostics", null, 0, 0, 0);
    private static UiNativeInputObservation Inspector => new(true, true, "inspector", "", 1, 1, 0);
    private static UiNativeInputObservation Keyboard => Inspector with { FocusedId = "search", TabPressed = 1, TextFieldFocused = true };
    private static UiNativeInputObservation Text => Keyboard with { PointerPressed = 2, PointerReleased = 2, TextValue = Probe, TextFieldFocused = true };
    private static UiNativeInputObservation Backspace => Text with { TextValue = "native-4", BackspacePressed = 1 };

    [Fact]
    public void DirectMenuStateChangeCannotReplaceSmapiPointerEvents()
    {
        var gate = Start();
        UiNativeInputObservation direct = Inspector with { PointerPressed = 0, PointerReleased = 0 };

        Assert.Null(gate.Observe(++_frame, direct));
        Assert.Null(gate.Observe(++_frame, direct));
        Assert.Equal(UiNativeInputPhase.Pointer, gate.Phase);

        Accept(gate, UiNativeInputPhase.Pointer, Inspector);
    }

    [Fact]
    public void PointerRequiresReleaseAndTwoStableRenderedFrames()
    {
        var gate = Start();
        UiNativeInputObservation held = Inspector with { PointerReleased = 0 };

        Assert.Null(gate.Observe(++_frame, held));
        Assert.Null(gate.Observe(++_frame, held));
        Assert.Equal(UiNativeInputPhase.Pointer, gate.Phase);
        Accept(gate, UiNativeInputPhase.Pointer, Inspector);
        Assert.Equal(UiNativeInputPhase.Keyboard, gate.Phase);
    }

    [Fact]
    public void KeyboardRequiresNewTabEventAndChangedVisibleFocus()
    {
        var gate = Start();
        Accept(gate, UiNativeInputPhase.Pointer, Inspector with { TabPressed = 1 });

        Assert.Null(gate.Observe(++_frame, Keyboard));
        Assert.Null(gate.Observe(++_frame, Keyboard));
        UiNativeInputObservation unchanged = Inspector with { TabPressed = 2, TextFieldFocused = true };
        Assert.Null(gate.Observe(++_frame, unchanged));
        Assert.Null(gate.Observe(++_frame, unchanged));
        UiNativeInputObservation hidden = Keyboard with { TabPressed = 2, FocusedId = null };
        Assert.Null(gate.Observe(++_frame, hidden));
        Assert.Null(gate.Observe(++_frame, hidden));
        UiNativeInputObservation wrongTarget = Keyboard with { TabPressed = 2, TextFieldFocused = false };
        Assert.Null(gate.Observe(++_frame, wrongTarget));
        Assert.Null(gate.Observe(++_frame, wrongTarget));

        Accept(gate, UiNativeInputPhase.Keyboard, Keyboard with { TabPressed = 2 });
    }

    [Fact]
    public void TextRequiresFreshFieldGestureAndExactSessionProbe()
    {
        var gate = Start();
        Accept(gate, UiNativeInputPhase.Pointer, Inspector);
        Accept(gate, UiNativeInputPhase.Keyboard, Keyboard);
        UiNativeInputObservation direct = Keyboard with { TextValue = Probe };

        Assert.Null(gate.Observe(++_frame, direct));
        Assert.Null(gate.Observe(++_frame, direct));
        UiNativeInputObservation stale = Text with { TextValue = "native-another-session" };
        Assert.Null(gate.Observe(++_frame, stale));
        Assert.Null(gate.Observe(++_frame, stale));
        UiNativeInputObservation unfocused = Text with { TextFieldFocused = false };
        Assert.Null(gate.Observe(++_frame, unfocused));
        Assert.Null(gate.Observe(++_frame, unfocused));
        Assert.Equal(UiNativeInputPhase.Text, gate.Phase);

        Accept(gate, UiNativeInputPhase.Text, Text);
        Assert.Equal(UiNativeInputPhase.Backspace, gate.Phase);
    }

    [Fact]
    public void BackspaceRequiresANewEventAndExactlyOneRenderedDeletion()
    {
        var gate = Start();
        Accept(gate, UiNativeInputPhase.Pointer, Inspector);
        Accept(gate, UiNativeInputPhase.Keyboard, Keyboard);
        Accept(gate, UiNativeInputPhase.Text, Text);
        foreach (UiNativeInputObservation invalid in new[]
        {
            Backspace with { BackspacePressed = 0 },
            Backspace with { TextValue = Probe },
            Backspace with { TextValue = "native-" },
            Backspace with { TextFieldFocused = false }
        })
        {
            Assert.Null(gate.Observe(++_frame, invalid));
            Assert.Null(gate.Observe(++_frame, invalid));
            Assert.Equal(UiNativeInputPhase.Backspace, gate.Phase);
        }
        Accept(gate, UiNativeInputPhase.Backspace, Backspace);
        Assert.Equal(UiNativeInputPhase.Complete, gate.Phase);
    }

    [Fact]
    public void InvalidGeometryInterruptsRenderedConfirmation()
    {
        var gate = Start();
        Assert.Null(gate.Observe(++_frame, Inspector));
        Assert.Null(gate.Observe(++_frame, Inspector with { ValidGeometry = false }));

        Accept(gate, UiNativeInputPhase.Pointer, Inspector);
    }

    [Fact]
    public void DuplicateOrOlderFramesCannotAdvanceOrRepeatCompletedScenario()
    {
        var gate = Start();
        Assert.Null(gate.Observe(_frame, Inspector));
        Assert.Null(gate.Observe(++_frame, Inspector));
        Assert.Null(gate.Observe(_frame, Inspector));
        Assert.Null(gate.Observe(_frame - 1, Inspector));
        Assert.Equal(UiNativeInputPhase.Pointer, gate.Observe(++_frame, Inspector));
        Accept(gate, UiNativeInputPhase.Keyboard, Keyboard);
        Accept(gate, UiNativeInputPhase.Text, Text);
        Accept(gate, UiNativeInputPhase.Backspace, Backspace);

        Assert.Null(gate.Observe(++_frame, Text));
        Assert.Null(gate.Observe(++_frame, Ready));
        Assert.Equal(UiNativeInputPhase.Complete, gate.Phase);
    }

    private UiNativeInputGate Start()
    {
        var gate = new UiNativeInputGate(Probe);
        Accept(gate, UiNativeInputPhase.Ready, Ready);
        return gate;
    }

    private void Accept(UiNativeInputGate gate, UiNativeInputPhase phase, UiNativeInputObservation observation)
    {
        Assert.Null(gate.Observe(++_frame, observation));
        Assert.Equal(phase, gate.Observe(++_frame, observation));
    }
}
