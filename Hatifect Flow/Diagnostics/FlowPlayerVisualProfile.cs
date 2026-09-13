using System;
using StardewValley;

namespace Hatifect.Flow.Diagnostics;

// Exact harness identities only. These profiles prove prepared visual behavior, not physical input.
internal sealed record FlowPlayerVisualProfile(string Scenario, bool Russian, float Scale)
{
    internal string Locale => Russian ? "ru-RU" : "en";
    internal LocalizedContentManager.LanguageCode Language => Russian
        ? LocalizedContentManager.LanguageCode.ru : LocalizedContentManager.LanguageCode.en;
    internal string CommandResult => Russian ? "Команда выполнена" : "Command completed";
    internal string ShipmentResult => Russian ? "Отправление создано" : "Shipment created";
    internal string DeliveredHistory(Guid parcel) => parcel + (Russian
        ? " · Доставлено · попыток: 1" : " · Delivered · attempts: 1");

    internal static bool IsScenario(string? scenario) => scenario is
        "flow.ui.player.en-075" or "flow.ui.player.ru-075" or
        "flow.ui.player.en-100" or "flow.ui.player.ru-100" or
        "flow.ui.player.en-125" or "flow.ui.player.ru-125" or
        "flow.ui.player.en-150" or "flow.ui.player.ru-150";

    internal static FlowPlayerVisualProfile Resolve(string scenario) => scenario switch
    {
        "flow.ui.player.en-075" => new(scenario, false, .75f),
        "flow.ui.player.ru-075" => new(scenario, true, .75f),
        "flow.ui.player.en-100" => new(scenario, false, 1f),
        "flow.ui.player.ru-100" => new(scenario, true, 1f),
        "flow.ui.player.en-125" => new(scenario, false, 1.25f),
        "flow.ui.player.ru-125" => new(scenario, true, 1.25f),
        "flow.ui.player.en-150" => new(scenario, false, 1.5f),
        "flow.ui.player.ru-150" => new(scenario, true, 1.5f),
        _ => throw new ArgumentException("Unknown fixed player visual profile.", nameof(scenario))
    };

    internal void RequireActual(LocalizedContentManager.LanguageCode language, float desired, float applied,
        Options.GamepadModes mode, bool controls, FlowUiNativeFrame? frame)
    {
        FlowUiActionsAcceptance.RequireEnglishInput(mode, controls);
        FlowHostAcceptance.Require(language == Language && desired == Scale && applied == Scale
            && frame is { } actual && actual.IsConsistent && actual.DesiredScale == Scale
            && actual.BaseScale == Scale && actual.AppliedScale == Scale,
            "The fixed player visual profile did not match its completed native frame.");
    }
}

// Acquisition never changes game settings. A new Options owner must first produce fresh stable draws.
internal sealed class FlowPlayerProfileAcquisition
{
    private object? _owner;

    internal bool ObserveOwner(object owner)
    {
        if (ReferenceEquals(_owner, owner)) return false;
        _owner = owner;
        return true;
    }

    internal bool CanAcquire(object owner, float baseScale, float desiredScale, float appliedScale,
        FlowUiNativeFrame? completed)
        => ReferenceEquals(_owner, owner) && completed is { IsConsistent: true } frame
            && frame.BaseScale == baseScale && frame.DesiredScale == desiredScale
            && frame.AppliedScale == appliedScale;

    internal void Release() => _owner = null;
}
