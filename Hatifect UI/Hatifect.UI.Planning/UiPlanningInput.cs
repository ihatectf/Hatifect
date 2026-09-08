using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Planning;

/// <summary>Detached ordered planner input. It retains no sources, values or action callbacks.</summary>
public sealed class UiPlanningInput
{
    public UiPlanningInput(UiSymbolId id, IEnumerable<UiPlanningElement> elements)
    {
        if (!id.IsValid) throw new ArgumentException("A valid experience ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(elements);
        UiPlanningElement[] copied = elements.ToArray();
        if (copied.Length == 0) throw new ArgumentException("At least one presented element is required.", nameof(elements));
        var identities = new HashSet<UiSymbolId>();
        foreach (UiPlanningElement element in copied)
        {
            if (element is null || !identities.Add(element.Id))
                throw new ArgumentException("Presented elements must be non-null and have unique identities.", nameof(elements));
            if (!UiGraphBinder.IsChild(id, element.Id))
                throw new ArgumentException("Element identities must be children of the Experience owner.", nameof(elements));
        }
        Id = id;
        Elements = Array.AsReadOnly(copied);
    }

    public UiSymbolId Id { get; }
    public IReadOnlyList<UiPlanningElement> Elements { get; }

    /// <summary>Captures planner facts from the presented elements without reading their sources.</summary>
    public static UiPlanningInput Capture(UiExperienceDefinition experience)
    {
        ArgumentNullException.ThrowIfNull(experience);
        return new UiPlanningInput(experience.Id, experience.Elements.Select(element =>
            new UiPlanningElement(element.Id, element.Capabilities, element.Source is IUiSemanticCollectionSource)));
    }
}
