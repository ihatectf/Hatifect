using System;
using System.Collections.Generic;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Planning;

public enum UiPlanDecisionCode
{
    ExplicitPattern,
    GeneratedPattern,
    ExplicitPlacement,
    CapabilityPlacement,
    ExplicitPresentation,
    ProfileAdaptation,
    CapabilityPresentation,
    EnvironmentProfile,
    EnvironmentFacet,
    PresentationRejected,
    CoverageFallback
}

public sealed record UiPlanDecision(
    UiPlanDecisionCode Code,
    UiSymbolId? Element,
    string Message,
    UiSourceProvenance? Source)
{
    public UiSymbolId? Candidate { get; init; }
}

/// <summary>No partial plan is activated when an element's required semantics cannot be covered.</summary>
public sealed class UiPlanningException : InvalidOperationException
{
    internal UiPlanningException(UiSymbolId element, UiPlanDecision[] decisions)
        : base($"No permitted presentation covers all required capabilities of element '{element}'.")
    {
        Element = element;
        Decisions = Array.AsReadOnly(decisions);
    }

    public UiSymbolId Element { get; }
    public IReadOnlyList<UiPlanDecision> Decisions { get; }
}

public sealed record UiPlannedElement(
    UiSymbolId Element,
    UiSymbolId Region,
    UiSymbolId Presentation,
    IReadOnlyList<UiPlanDecision> Decisions);

internal sealed record UiPlannedCollectionRecipe(UiSymbolId ItemSizing, string Density);

public sealed class UiPresentationPlan
{
    private readonly IReadOnlyDictionary<UiSymbolId, UiPlannedCollectionRecipe> _collectionRecipes;

    internal UiPresentationPlan(
        UiSymbolId experience,
        UiSymbolId pattern,
        UiHostContext host,
        UiPlannedElement[] elements,
        UiPlanDecision[] decisions,
        IDictionary<UiSymbolId, UiPlannedCollectionRecipe>? collectionRecipes = null)
    {
        Experience = experience;
        Pattern = pattern;
        Host = host;
        Elements = Array.AsReadOnly(elements);
        Decisions = Array.AsReadOnly(decisions);
        _collectionRecipes = new System.Collections.ObjectModel.ReadOnlyDictionary<UiSymbolId, UiPlannedCollectionRecipe>(
            new Dictionary<UiSymbolId, UiPlannedCollectionRecipe>(
                collectionRecipes ?? new Dictionary<UiSymbolId, UiPlannedCollectionRecipe>()));
    }

    public UiSymbolId Experience { get; }
    public UiSymbolId Pattern { get; }
    public UiHostContext Host { get; }
    public IReadOnlyList<UiPlannedElement> Elements { get; }
    public IReadOnlyList<UiPlanDecision> Decisions { get; }

    internal UiPlannedCollectionRecipe CollectionRecipeFor(UiSymbolId element)
        => _collectionRecipes.TryGetValue(element, out UiPlannedCollectionRecipe? recipe)
            ? recipe
            : throw new InvalidOperationException($"Plan element '{element}' has no collection recipe.");
}
