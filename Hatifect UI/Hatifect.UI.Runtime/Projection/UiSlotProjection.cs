using System;
using System.Collections.Generic;
using Hatifect.UI.Planning;

namespace Hatifect.UI.Runtime.Projection;

public static class UiHostSlots
{
    public static readonly UiSymbolId Navigation = Slot("Navigation");
    public static readonly UiSymbolId Utility = Slot("Utility");
    public static readonly UiSymbolId Content = Slot("Content");
    public static readonly UiSymbolId Context = Slot("Context");
    public static readonly UiSymbolId Actions = Slot("Actions");
    public static readonly UiSymbolId Status = Slot("Status");
    public static readonly UiSymbolId Overlay = Slot("Overlay");

    internal static IReadOnlyList<UiSymbolId> TerminalOrder { get; } = Array.AsReadOnly(new[]
    {
        Navigation,
        Utility,
        Content,
        Context,
        Actions,
        Status,
        Overlay
    });

    private static UiSymbolId Slot(string name) => new("Hatifect.UI", $"host-slot/{name}");
}

public sealed record UiProjectedElement(
    UiSymbolId Element,
    UiSymbolId SourceRegion,
    UiSymbolId HostSlot,
    string Reason);

public sealed class UiSlotProjectionResult
{
    internal UiSlotProjectionResult(UiProjectedElement[] elements)
        => Elements = Array.AsReadOnly(elements);

    public IReadOnlyList<UiProjectedElement> Elements { get; }
}

/// <summary>Internal policy prevents child regions from materializing nested host chrome.</summary>
internal sealed class UiSlotProjector
{
    public UiSlotProjectionResult Project(UiPresentationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var projected = new List<UiProjectedElement>(plan.Elements.Count);
        foreach (UiPlannedElement element in plan.Elements)
        {
            UiSymbolId slot = ProjectRegion(plan.Host.HostKind, element.Region);
            projected.Add(new UiProjectedElement(
                element.Element,
                element.Region,
                slot,
                plan.Host.HostKind == UiHostKind.Terminal
                    ? "Child semantic region projected into Terminal shell slot."
                    : "Semantic region projected into host slot."));
        }
        return new UiSlotProjectionResult(projected.ToArray());
    }

    internal static UiSymbolId ProjectRegion(UiHostKind host, UiSymbolId region)
    {
        string name = region.LocalId[(region.LocalId.LastIndexOf('/') + 1)..];
        if (host == UiHostKind.Terminal)
        {
            return name switch
            {
                "Navigation" => UiHostSlots.Navigation,
                "Utility" => UiHostSlots.Utility,
                "Context" => UiHostSlots.Context,
                "Actions" => UiHostSlots.Actions,
                "Footer" => UiHostSlots.Status,
                "Overlay" => UiHostSlots.Overlay,
                _ => UiHostSlots.Content
            };
        }

        return name switch
        {
            "Context" => UiHostSlots.Context,
            "Utility" => UiHostSlots.Utility,
            "Actions" => UiHostSlots.Actions,
            "Overlay" => UiHostSlots.Overlay,
            _ => UiHostSlots.Content
        };
    }
}
