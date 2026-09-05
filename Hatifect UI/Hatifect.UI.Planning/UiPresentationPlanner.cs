using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Planning;

/// <summary>Pure deterministic Experience -> PresentationPlan materialization.</summary>
public sealed class UiPresentationPlanner
{
    private readonly UiSemanticCatalog _catalog;

    public UiPresentationPlanner(UiSemanticCatalog? catalog = null)
        => _catalog = catalog ?? UiSemanticCatalog.CreateFoundation();

    public UiPresentationPlan Plan(
        UiExperienceDefinition experience,
        UiHostContext host,
        UiPresentationDefinition? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(experience);
        ArgumentNullException.ThrowIfNull(host);

        PlanIndexes indexes = PlanIndexes.Create(experience, presentation);
        var globalDecisions = new List<UiPlanDecision>();
        UiSymbolId pattern = ResolvePattern(indexes, globalDecisions);
        var planned = new List<UiPlannedElement>(experience.Elements.Count);
        var collectionRecipes = new Dictionary<UiSymbolId, UiPlannedCollectionRecipe>();
        foreach (UiSemanticElementDefinition element in experience.Elements)
        {
            UiPlannedElement plannedElement = PlanElement(element, host, indexes);
            planned.Add(plannedElement);
            if (element.Source is IUiSemanticCollectionSource)
                collectionRecipes.Add(
                    element.Id,
                    ResolveCollectionRecipe(element, plannedElement.Presentation, host, indexes));
        }

        return new UiPresentationPlan(
            experience.Id,
            pattern,
            host,
            planned.ToArray(),
            globalDecisions.ToArray(),
            collectionRecipes);
    }

    private UiSymbolId ResolvePattern(
        PlanIndexes indexes,
        List<UiPlanDecision> decisions)
    {
        UiPropertyAssignmentIr? explicitPattern = indexes.FindAssignment(target: null, "use", profile: null);
        if (explicitPattern?.Value is UiSymbolValue patternValue)
        {
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.ExplicitPattern, null,
                $"Pattern '{patternValue.Name}' comes from the Presentation asset.", explicitPattern.Provenance));
            return patternValue.Symbol;
        }

        bool browses = indexes.HasAny(UiCapabilities.Browse);
        bool inspects = indexes.HasAny(UiCapabilities.Inspect);
        bool configures = indexes.HasAny(UiCapabilities.Configure);
        string generated = browses && inspects ? "MasterDetail" : browses ? "Catalog" : configures ? "Prompt" : "Workspace";
        if (!_catalog.TryGetPattern(generated, out UiSymbolId pattern))
            throw new InvalidOperationException($"Foundation pattern '{generated}' is not registered.");
        decisions.Add(new UiPlanDecision(UiPlanDecisionCode.GeneratedPattern, null,
            $"Pattern '{generated}' was selected from Experience capabilities.", null));
        return pattern;
    }

    private UiPlannedElement PlanElement(
        UiSemanticElementDefinition element,
        UiHostContext host,
        PlanIndexes indexes)
    {
        var decisions = new List<UiPlanDecision>();
        UiPlacementIr? placement = indexes.FindPlacement(element.Id);
        UiSymbolId region;
        if (placement != null)
        {
            region = placement.Region;
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.ExplicitPlacement, element.Id,
                "Region comes from the Presentation asset.", placement.Provenance));
        }
        else
        {
            string regionName = DefaultRegion(element, indexes);
            if (!_catalog.TryGetRegion(regionName, out region))
                throw new InvalidOperationException($"Foundation region '{regionName}' is not registered.");
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.CapabilityPlacement, element.Id,
                $"Region '{regionName}' was selected from element capabilities.", null));
        }

        UiSymbolId presentation = ResolvePresentation(element, host, indexes, decisions);
        return new UiPlannedElement(element.Id, region, presentation, decisions.ToArray());
    }

    private UiSymbolId ResolvePresentation(
        UiSemanticElementDefinition element,
        UiHostContext host,
        PlanIndexes indexes,
        List<UiPlanDecision> decisions)
    {
        UiPropertyAssignmentIr? explicitView = indexes.FindAssignment(element.Id, "view", host.Profile)
            ?? indexes.FindAssignment(element.Id, "view", profile: null);
        if (explicitView?.Value is UiSymbolValue explicitValue)
        {
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.ExplicitPresentation, element.Id,
                $"Presentation '{explicitValue.Name}' comes from the active profile matrix.", explicitView.Provenance));
            return explicitValue.Symbol;
        }

        bool compact = host.Profile == UiPresentationProfiles.Compact.Id || host.Profile == UiPresentationProfiles.Controller.Id;
        UiPropertyAssignmentIr? preferred = indexes.FindAssignment(element.Id, compact ? "fallback" : "prefer", host.Profile)
            ?? indexes.FindAssignment(element.Id, compact ? "fallback" : "prefer", profile: null);
        if (preferred?.Value is UiSymbolValue preferredValue)
        {
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.ProfileAdaptation, element.Id,
                $"Presentation '{preferredValue.Name}' was selected by {(compact ? "fallback" : "preference")} policy.", preferred.Provenance));
            return preferredValue.Symbol;
        }

        string name = DefaultPresentation(element, host.Profile, indexes);
        if (!_catalog.TryGetPresentation(name, out UiPresentationSymbol? presentation) || presentation == null)
            throw new InvalidOperationException($"Foundation presentation '{name}' is not registered.");
        decisions.Add(new UiPlanDecision(
            IsAdaptive(element, host.Profile, indexes) ? UiPlanDecisionCode.ProfileAdaptation : UiPlanDecisionCode.CapabilityPresentation,
            element.Id,
            $"Presentation '{name}' was selected from capabilities and profile.", null));
        return presentation.Id;
    }

    private UiPlannedCollectionRecipe ResolveCollectionRecipe(
        UiSemanticElementDefinition element,
        UiSymbolId presentation,
        UiHostContext host,
        PlanIndexes indexes)
    {
        if (!_catalog.TryGetPresentationProperty("itemSizing", out UiPropertySymbol? property) || property == null ||
            !_catalog.TryGetPropertyValue(property, "Uniform", out UiEnumValueSymbol? uniform) || uniform == null ||
            !_catalog.TryGetPropertyValue(property, "Adaptive", out UiEnumValueSymbol? adaptive) || adaptive == null)
            throw new InvalidOperationException("Foundation item-sizing catalog values are not registered.");

        UiPropertyAssignmentIr? explicitSizing = indexes.FindAssignment(element.Id, "itemSizing", host.Profile)
            ?? indexes.FindAssignment(element.Id, "itemSizing", profile: null);
        UiSymbolId sizing = explicitSizing?.Value is UiSymbolValue sizingValue
            ? sizingValue.Symbol
            : uniform.Id;
        if (sizing != uniform.Id && sizing != adaptive.Id)
            throw new InvalidOperationException(
                $"Collection element '{element.Id}' resolved an unknown item-sizing catalog value '{sizing}'.");

        if (_catalog.TryGetPresentation("NavigationList", out UiPresentationSymbol? navigation) &&
            navigation != null && presentation == navigation.Id && sizing == adaptive.Id)
            throw new InvalidOperationException(
                $"Collection element '{element.Id}' requests Adaptive sizing for Uniform-only NavigationList.");

        UiPropertyAssignmentIr? explicitDensity = indexes.FindAssignment(element.Id, "density", host.Profile)
            ?? indexes.FindAssignment(element.Id, "density", profile: null);
        string density = explicitDensity?.Value is UiStringValue densityValue
            ? densityValue.Value
            : "Default";
        return new UiPlannedCollectionRecipe(sizing, density);
    }

    private static string DefaultRegion(UiSemanticElementDefinition element, PlanIndexes indexes)
    {
        if (indexes.Has(element, UiCapabilities.Actions)) return "Actions";
        if (indexes.Has(element, UiCapabilities.Navigate)) return "Navigation";
        if (indexes.Has(element, UiCapabilities.Search) || indexes.Has(element, UiCapabilities.Filter)) return "Utility";
        if (indexes.Has(element, UiCapabilities.Inspect)) return "Context";
        if (indexes.Has(element, UiCapabilities.Monitor)) return "Footer";
        return "Primary";
    }

    private static string DefaultPresentation(
        UiSemanticElementDefinition element,
        UiSymbolId profile,
        PlanIndexes indexes)
    {
        bool compact = profile == UiPresentationProfiles.Compact.Id;
        bool controller = profile == UiPresentationProfiles.Controller.Id;
        if (indexes.Has(element, UiCapabilities.Browse)) return compact || controller ? "List" : "Gallery";
        if (indexes.Has(element, UiCapabilities.Inspect)) return controller ? "Route" : compact ? "Sheet" : "Side";
        if (indexes.Has(element, UiCapabilities.Configure)) return "Form";
        if (indexes.Has(element, UiCapabilities.Search)) return "TextField";
        if (indexes.Has(element, UiCapabilities.Filter)) return "FilterBar";
        if (indexes.Has(element, UiCapabilities.Monitor)) return "Status";
        if (indexes.Has(element, UiCapabilities.Navigate)) return "NavigationList";
        if (indexes.Has(element, UiCapabilities.Actions)) return "ActionBar";
        if (indexes.Has(element, UiCapabilities.Select) && element.Source is IUiSemanticCollectionSource) return "List";
        return "Value";
    }

    private static bool IsAdaptive(UiSemanticElementDefinition element, UiSymbolId profile, PlanIndexes indexes)
        => (indexes.Has(element, UiCapabilities.Browse) || indexes.Has(element, UiCapabilities.Inspect)) &&
           (profile == UiPresentationProfiles.Compact.Id || profile == UiPresentationProfiles.Controller.Id);

    private readonly record struct AssignmentKey(UiSymbolId? Target, string Property, UiSymbolId? Profile);

    private sealed class PlanIndexes
    {
        private readonly Dictionary<UiSymbolId, UiPlacementIr> _placements;
        private readonly Dictionary<AssignmentKey, UiPropertyAssignmentIr> _assignments;
        private readonly Dictionary<UiSymbolId, HashSet<UiSymbolId>> _elementCapabilities;
        private readonly HashSet<UiSymbolId> _capabilities;

        private PlanIndexes(
            Dictionary<UiSymbolId, UiPlacementIr> placements,
            Dictionary<AssignmentKey, UiPropertyAssignmentIr> assignments,
            Dictionary<UiSymbolId, HashSet<UiSymbolId>> elementCapabilities,
            HashSet<UiSymbolId> capabilities)
        {
            _placements = placements;
            _assignments = assignments;
            _elementCapabilities = elementCapabilities;
            _capabilities = capabilities;
        }

        internal static PlanIndexes Create(
            UiExperienceDefinition experience,
            UiPresentationDefinition? definition)
        {
            var placements = new Dictionary<UiSymbolId, UiPlacementIr>();
            var assignments = new Dictionary<AssignmentKey, UiPropertyAssignmentIr>();
            if (definition != null)
            {
                foreach (UiPlacementIr placement in definition.Placements)
                    placements.TryAdd(placement.Element, placement);

                foreach (UiPropertyAssignmentIr assignment in definition.Assignments)
                {
                    if (assignment.State == null)
                    {
                        assignments.TryAdd(
                            new AssignmentKey(assignment.Target, assignment.Property.Name, assignment.Profile),
                            assignment);
                    }
                }
            }

            var elementCapabilities = new Dictionary<UiSymbolId, HashSet<UiSymbolId>>(experience.Elements.Count);
            var capabilities = new HashSet<UiSymbolId>();
            foreach (UiSemanticElementDefinition element in experience.Elements)
            {
                var indexed = new HashSet<UiSymbolId>();
                foreach (UiCapability capability in element.Capabilities)
                {
                    indexed.Add(capability.Id);
                    capabilities.Add(capability.Id);
                }

                elementCapabilities.Add(element.Id, indexed);
            }

            return new PlanIndexes(placements, assignments, elementCapabilities, capabilities);
        }

        internal UiPlacementIr? FindPlacement(UiSymbolId element)
            => _placements.TryGetValue(element, out UiPlacementIr? placement) ? placement : null;

        internal UiPropertyAssignmentIr? FindAssignment(
            UiSymbolId? target,
            string property,
            UiSymbolId? profile)
            => _assignments.TryGetValue(new AssignmentKey(target, property, profile), out UiPropertyAssignmentIr? assignment)
                ? assignment
                : null;

        internal bool Has(UiSemanticElementDefinition element, UiCapability capability)
            => _elementCapabilities[element.Id].Contains(capability.Id);

        internal bool HasAny(UiCapability capability)
            => _capabilities.Contains(capability.Id);
    }
}
