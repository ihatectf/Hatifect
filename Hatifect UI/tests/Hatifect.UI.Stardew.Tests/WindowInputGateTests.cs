using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI;
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

    [Theory]
    [InlineData("Hatifect.Flow", "network", true)]
    [InlineData("Hatifect.Flow", "parcel", false)]
    [InlineData("Hatifect.UI", "terminal", false)]
    public void ObserverTargetsOnlyTheOrdinaryFlowNetworkWindow(string scope, string name, bool expected)
        => Assert.Equal(expected, UiWindowInputObserver.IsTargetExperience(new UiSymbolId(scope, name)));

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

    [Fact]
    public void BoundedHistoryRetainsProbeAndFiveActionResultsAfterOptionalFramesSaturateIt()
    {
        var history = new UiBoundedCaptureHistory<string>(12, requiredReservation: 10);
        string[] required =
        {
            "phase:Ready", "phase:Pointer", "phase:Text", "phase:Backspace", "phase:Tab",
            "action-result:1:register", "action-result:2:register", "action-result:3:link",
            "action-result:3:send", "action-result:3:send-quantity"
        };
        var evicted = new List<string>();

        foreach (string key in required.Take(5)) Assert.True(history.TryAdd(key, _ => key));
        for (int index = 0; index < 64; index++) history.TryAdd(null, _ => "optional:" + index);
        foreach (string key in required.Skip(5))
        {
            int before = history.Count;
            Assert.True(history.TryAdd(key, _ => key, evicted.Add));
            Assert.Equal(before + 1, history.Count);
        }
        for (int index = 64; index < 256; index++) history.TryAdd(null, _ => "optional:" + index);

        Assert.Equal(12, history.Count);
        foreach (string key in required) Assert.Contains(key, history.Values);
        Assert.Empty(evicted);
        Assert.Equal(2, history.Values.Count(value => value.StartsWith("optional:", StringComparison.Ordinal)));
    }

    [Fact]
    public void RequiredCaptureBeyondReservationEvictsOldestOptionalCapture()
    {
        var history = new UiBoundedCaptureHistory<string>(3, requiredReservation: 1);
        Assert.True(history.TryAdd("phase:Ready", _ => "ready"));
        Assert.True(history.TryAdd(null, _ => "optional:0"));
        Assert.True(history.TryAdd(null, _ => "optional:1"));
        string? evicted = null;

        Assert.True(history.TryAdd("action-result:1:register", _ => "result", value => evicted = value));

        Assert.Equal("optional:0", evicted);
        Assert.Equal(new[] { "ready", "optional:1", "result" }, history.Values);
    }

    [Fact]
    public void DuplicateRequiredCaptureDoesNotConsumeCapacityOrInvokeFactory()
    {
        var history = new UiBoundedCaptureHistory<string>(2, requiredReservation: 1);
        Assert.True(history.TryAdd("phase:Ready", _ => "ready"));

        Assert.False(history.TryAdd("phase:Ready", _ => throw new InvalidOperationException("duplicate factory invoked")));

        Assert.Equal(new[] { "ready" }, history.Values);
    }

    [Fact]
    public void RequiredCaptureOverflowFailsWhenNoOptionalCaptureCanBeEvicted()
    {
        var history = new UiBoundedCaptureHistory<string>(2, requiredReservation: 2);
        Assert.True(history.TryAdd("phase:Ready", _ => "ready"));
        Assert.True(history.TryAdd("phase:Pointer", _ => "pointer"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => history.TryAdd("phase:Text", _ => "text"));

        Assert.Equal("Required Window input capture budget exhausted.", error.Message);
        Assert.Equal(new[] { "ready", "pointer" }, history.Values);
    }

    [Fact]
    public void CaptureKeyRequiresACompletedProbeOrNewVisibleActionResult()
    {
        Assert.Equal("phase:Ready", UiWindowInputObserver.RequiredCaptureKey(
            UiWindowInputPhase.Ready, 7, false, null, false));
        Assert.Equal("action-result:7:Hatifect.Flow/network/action/register", UiWindowInputObserver.RequiredCaptureKey(
            null, 7, true, "Hatifect.Flow/network/action/register", true));
        Assert.Equal("action-result:8:Hatifect.Flow/network/action/register", UiWindowInputObserver.RequiredCaptureKey(
            null, 8, true, "Hatifect.Flow/network/action/register", true));
        Assert.Null(UiWindowInputObserver.RequiredCaptureKey(null, 7, false, "action", true));
        Assert.Null(UiWindowInputObserver.RequiredCaptureKey(null, 7, true, null, true));
        Assert.Null(UiWindowInputObserver.RequiredCaptureKey(null, 7, true, "action", false));
    }

    [Theory]
    [InlineData(false, 42L, 41L, "action", "done", true, false)]
    [InlineData(true, 42L, 42L, "action", "done", true, false)]
    [InlineData(true, 42L, 41L, null, "done", true, false)]
    [InlineData(true, 42L, 41L, "action", "", true, false)]
    [InlineData(true, 42L, 41L, "action", "done", false, false)]
    [InlineData(true, 42L, 41L, "action", "done", true, true)]
    public void RequiredActionResultMustFollowTheFocusedActionPublication(
        bool sceneChanged, long scene, long? actionScene, string? action, string? result, bool visible, bool expected)
    {
        Assert.Equal(expected, UiWindowInputObserver.IsRequiredActionResult(
            sceneChanged, scene, actionScene, action, result, visible));
    }

    private static void Advance(UiWindowInputGate gate, ref long frame, UiWindowInputObservation value, UiWindowInputPhase phase)
    {
        Assert.Null(gate.Observe(++frame, value));
        Assert.Null(gate.Observe(frame, value));
        Assert.Equal(phase, gate.Observe(++frame, value));
    }
}
