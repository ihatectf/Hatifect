using System;
using StardewValley;
using Hatifect.Flow.Diagnostics;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

public sealed class PlayerVisualProfileTests
{
    [Theory]
    [InlineData("flow.ui.player.en-075", false, .75f)]
    [InlineData("flow.ui.player.ru-075", true, .75f)]
    [InlineData("flow.ui.player.en-100", false, 1f)]
    [InlineData("flow.ui.player.ru-100", true, 1f)]
    [InlineData("flow.ui.player.en-125", false, 1.25f)]
    [InlineData("flow.ui.player.ru-125", true, 1.25f)]
    [InlineData("flow.ui.player.en-150", false, 1.5f)]
    [InlineData("flow.ui.player.ru-150", true, 1.5f)]
    public void ExactProfileRequiresItsNativeFrame(string scenario, bool russian, float scale)
    {
        Assert.True(FlowPlayerVisualProfile.IsScenario(scenario));
        var profile = FlowPlayerVisualProfile.Resolve(scenario);
        Assert.Equal(scenario, profile.Scenario);
        Assert.Equal(russian ? "ru-RU" : "en", profile.Locale);
        Assert.Equal(scale, profile.Scale);
        var language = russian ? LocalizedContentManager.LanguageCode.ru : LocalizedContentManager.LanguageCode.en;
        profile.RequireActual(language, scale, scale, Options.GamepadModes.ForceOff, false, Frame(scale));
        Assert.Throws<InvalidOperationException>(() => profile.RequireActual(language, scale, scale,
            Options.GamepadModes.ForceOff, false, Frame(scale) with { AppliedScale = scale + .25f }));
    }

    [Theory]
    [InlineData("flow.ui.player")]
    [InlineData("flow.ui.player.input")]
    [InlineData("flow.ui.player.en-075.extra")]
    [InlineData("flow.ui.player.EN-075")]
    [InlineData("flow.ui.player.en-75")]
    [InlineData("flow.ui.player.en-200")]
    [InlineData("flow.ui.player.fr-075")]
    [InlineData("")]
    public void UnknownOrLegacyScenarioCannotBecomeProfile(string scenario)
    {
        Assert.False(FlowPlayerVisualProfile.IsScenario(scenario));
        Assert.Throws<ArgumentException>(() => FlowPlayerVisualProfile.Resolve(scenario));
    }

    [Theory]
    [InlineData("language")]
    [InlineData("desired")]
    [InlineData("applied")]
    [InlineData("base-frame")]
    [InlineData("desired-frame")]
    [InlineData("missing-frame")]
    [InlineData("render-target")]
    [InlineData("controller")]
    [InlineData("mode")]
    public void RequestedProfileAloneCannotPassMismatchedObservation(string mismatch)
    {
        var profile = FlowPlayerVisualProfile.Resolve("flow.ui.player.ru-075");
        var language = mismatch == "language" ? LocalizedContentManager.LanguageCode.en : LocalizedContentManager.LanguageCode.ru;
        float desired = mismatch == "desired" ? 1 : .75f;
        float applied = mismatch == "applied" ? 1 : .75f;
        FlowUiNativeFrame? frame = mismatch switch
        {
            "base-frame" => Frame(.75f) with { BaseScale = 1 },
            "desired-frame" => Frame(.75f) with { DesiredScale = 1 },
            "missing-frame" => null,
            "render-target" => Frame(.75f) with { BoundTargets = 1 },
            _ => Frame(.75f)
        };
        var mode = mismatch == "mode" ? Options.GamepadModes.Auto : Options.GamepadModes.ForceOff;
        Assert.Throws<InvalidOperationException>(() => profile.RequireActual(language, desired, applied,
            mode, mismatch == "controller", frame));
    }

    [Fact]
    public void ResultsAndHistoryRetainIndependentLocalizedExpectations()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var en = FlowPlayerVisualProfile.Resolve("flow.ui.player.en-075");
        var ru = FlowPlayerVisualProfile.Resolve("flow.ui.player.ru-075");
        Assert.Equal("Command completed", en.CommandResult);
        Assert.Equal("Shipment created", en.ShipmentResult);
        Assert.Equal("Команда выполнена", ru.CommandResult);
        Assert.Equal("Отправление создано", ru.ShipmentResult);
        Assert.Equal("12345678-1234-1234-1234-123456789abc · Delivered · attempts: 1", en.DeliveredHistory(id));
        Assert.Equal("12345678-1234-1234-1234-123456789abc · Доставлено · попыток: 1", ru.DeliveredHistory(id));
    }

    [Fact]
    public void TransitioningOriginalScaleCannotBeAcquiredBeforeStableDraws()
    {
        var gate = new FlowPlayerProfileAcquisition();
        var owner = new object();
        var settling = new FlowUiNativeFrameSettling();
        Assert.True(gate.ObserveOwner(owner));
        settling.Reset();
        var transitional = Frame(1) with { DesiredScale = .75f };
        settling.ObserveCompletedDraw(transitional);
        settling.ObserveCompletedDraw(transitional);
        Assert.False(settling.CompleteProfile(static () => throw new InvalidOperationException("Must not apply yet.")));
        Assert.False(gate.CanAcquire(owner, 1, .75f, 1, settling.Settled));
        Assert.False(gate.CanAcquire(owner, 1, .75f, 1, Frame(.75f)));
        settling.ObserveCompletedDraw(Frame(.75f));
        Assert.Null(settling.Settled);
        settling.ObserveCompletedDraw(Frame(.75f));
        Assert.False(settling.CompleteProfile(static () => { }));
        Assert.False(gate.CanAcquire(owner, .75f, .75f, .75f, settling.Settled));
        settling.ObserveCompletedDraw(Frame(.75f));
        settling.ObserveCompletedDraw(Frame(.75f));
        Assert.True(settling.CompleteProfile(static () => { }));
        Assert.True(gate.CanAcquire(owner, .75f, .75f, .75f, settling.Settled));
    }

    [Fact]
    public void NewOptionsOwnerAndReopeningRequireFreshAcquisition()
    {
        var gate = new FlowPlayerProfileAcquisition();
        var first = new object();
        var second = new object();
        Assert.True(gate.ObserveOwner(first));
        Assert.False(gate.ObserveOwner(first));
        Assert.False(gate.CanAcquire(second, 1, 1, 1, Frame(1)));
        Assert.True(gate.ObserveOwner(second));
        Assert.False(gate.CanAcquire(first, 1, 1, 1, Frame(1)));
        Assert.True(gate.CanAcquire(second, 1, 1, 1, Frame(1)));
        gate.Release();
        Assert.False(gate.CanAcquire(second, 1, 1, 1, Frame(1)));
        Assert.True(gate.ObserveOwner(second));
    }

    private static FlowUiNativeFrame Frame(float scale)
        => new(scale, scale, scale, 1280, 720, 1280, 720, 1280, 720, 1280, 720, 1280, 720, 1280, 720, 0);
}
