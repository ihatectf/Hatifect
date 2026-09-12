using System;
using System.Collections.Generic;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Planning;

/// <summary>Pure deterministic Experience -> PresentationPlan materialization.</summary>
public sealed class UiPresentationPlanner
{
    // Bounded foundation alternatives, in stable policy order. A plan never retains prior traces.
    private static readonly string[] FoundationPresentations =
    {
        "Gallery", "List", "TextField", "FilterBar", "Value", "Side", "Sheet", "Route",
        "Form", "Status", "NavigationList", "ActionBar"
    };
    private readonly UiSemanticCatalog _catalog;

    public UiPresentationPlanner(UiSemanticCatalog? catalog = null)
        => _catalog = catalog ?? UiSemanticCatalog.CreateFoundation();

    public UiPresentationPlan Plan(
        UiExperienceDefinition experience,
        UiHostContext host,
        UiPresentationDefinition? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(experience);
        return PlanCore(new PlanningSource(experience), host, presentation);
    }

    /// <summary>Plans detached authoring facts using the same policy as a live Experience.</summary>
    public UiPresentationPlan PlanInput(
        UiPlanningInput input,
        UiHostContext host,
        UiPresentationDefinition? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PlanCore(new PlanningSource(input), host, presentation);
    }

    private UiPresentationPlan PlanCore(PlanningSource experience, UiHostContext host,
        UiPresentationDefinition? presentation)
    {
        ArgumentNullException.ThrowIfNull(host);
        PlanIndexes indexes = PlanIndexes.Create(experience, presentation);
        var globalDecisions = new List<UiPlanDecision>();
        if (host.Environment is { } environment)
        {
            if (host.Profile != UiPresentationProfiles.Resolve(environment).Id)
                throw new ArgumentException("The selected profile contradicts the captured environment.", nameof(host));
            ExplainEnvironment(environment, host.Profile, globalDecisions);
        }
        UiSymbolId pattern = ResolvePattern(indexes, globalDecisions);
        var planned = new List<UiPlannedElement>(experience.Count);
        var collectionRecipes = new Dictionary<UiSymbolId, UiPlannedCollectionRecipe>();
        for (int i = 0; i < experience.Count; i++)
        {
            PlanningElementView element = experience.ElementAt(i);
            UiPlannedElement plannedElement = PlanElement(element, host, indexes);
            planned.Add(plannedElement);
            if (element.IsCollection)
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
        PlanningElementView element,
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
        return new UiPlannedElement(element.Id, region, presentation, Array.AsReadOnly(decisions.ToArray()));
    }

    private UiSymbolId ResolvePresentation(
        PlanningElementView element,
        UiHostContext host,
        PlanIndexes indexes,
        List<UiPlanDecision> decisions)
    {
        UiPropertyAssignmentIr? explicitView = indexes.FindAssignment(element.Id, "view", host.Profile)
            ?? indexes.FindAssignment(element.Id, "view", profile: null);
        if (explicitView?.Value is UiSymbolValue explicitValue)
        {
            RequireCoverage(element, explicitValue, explicitView.Provenance, decisions);
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.ExplicitPresentation, element.Id,
                $"Presentation '{explicitValue.Name}' comes from the active profile matrix.", explicitView.Provenance));
            return explicitValue.Symbol;
        }

        bool compact = host.Profile == UiPresentationProfiles.Compact.Id || host.Profile == UiPresentationProfiles.Controller.Id;
        UiPropertyAssignmentIr? preferred = indexes.FindAssignment(element.Id, compact ? "fallback" : "prefer", host.Profile)
            ?? indexes.FindAssignment(element.Id, compact ? "fallback" : "prefer", profile: null);
        if (preferred?.Value is UiSymbolValue preferredValue)
        {
            RequireCoverage(element, preferredValue, preferred.Provenance, decisions);
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.ProfileAdaptation, element.Id,
                $"Presentation '{preferredValue.Name}' was selected by {(compact ? "fallback" : "preference")} policy.", preferred.Provenance));
            return preferredValue.Symbol;
        }

        string name = DefaultPresentation(element, host.Profile, indexes);
        UiPresentationSymbol? presentation = TryCover(element, name, decisions);
        if (presentation == null)
        {
            foreach (string alternative in FoundationPresentations)
            {
                if (alternative == name) continue;
                presentation = TryCover(element, alternative, decisions);
                if (presentation == null) continue;
                decisions.Add(new UiPlanDecision(UiPlanDecisionCode.CoverageFallback, element.Id,
                    $"Presentation '{alternative}' covers every required capability after '{name}' was rejected.", null)
                    { Candidate = presentation.Id });
                return presentation.Id;
            }
            throw new UiPlanningException(element.Id, decisions.ToArray());
        }
        decisions.Add(new UiPlanDecision(
            IsAdaptive(element, host.Profile, indexes) ? UiPlanDecisionCode.ProfileAdaptation : UiPlanDecisionCode.CapabilityPresentation,
            element.Id,
            $"Presentation '{name}' was selected from capabilities and profile.", null));
        return presentation.Id;
    }

    private void RequireCoverage(PlanningElementView element, UiSymbolValue value,
        UiSourceProvenance source, List<UiPlanDecision> decisions)
    {
        UiPresentationSymbol? candidate = TryCover(element, value.Name, decisions, source);
        if (candidate != null && candidate.Id == value.Symbol) return;
        if (candidate != null)
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.PresentationRejected, element.Id,
                $"Presentation '{value.Name}' does not match the catalog identity '{value.Symbol}'.", source)
                { Candidate = value.Symbol });
        throw new UiPlanningException(element.Id, decisions.ToArray());
    }

    private UiPresentationSymbol? TryCover(PlanningElementView element, string name,
        List<UiPlanDecision> decisions, UiSourceProvenance? source = null)
    {
        if (!_catalog.TryGetPresentation(name, out UiPresentationSymbol? candidate) || candidate == null)
        {
            decisions.Add(new UiPlanDecision(UiPlanDecisionCode.PresentationRejected, element.Id,
                $"Presentation '{name}' is not registered.", source));
            return null;
        }

        List<string>? missing = null;
        foreach (UiCapability capability in element.Capabilities)
        {
            if (candidate.SupportedCapabilities.Contains(capability.Id)) continue;
            (missing ??= new List<string>()).Add(capability.Id.ToString());
        }
        if (missing == null) return candidate;
        missing.Sort(StringComparer.Ordinal);
        decisions.Add(new UiPlanDecision(UiPlanDecisionCode.PresentationRejected, element.Id,
            $"Presentation '{name}' cannot preserve required capabilities: {string.Join(", ", missing)}.", source)
            { Candidate = candidate.Id });
        return null;
    }

    private static void ExplainEnvironment(UiEnvironment environment, UiSymbolId profile,
        List<UiPlanDecision> decisions)
    {
        var origins = environment.Origins;
        Add("viewport", FormattableString.Invariant($"{environment.Viewport.Width} x {environment.Viewport.Height} logical UI units"), origins.Viewport);
        Add("scale", FormattableString.Invariant($"{environment.Scale}"), origins.Scale);
        Add("input", environment.InputMode.ToString(), origins.InputMode);
        Add("locale", environment.Locale, origins.Locale);
        Add("theme", environment.Theme.ToString(), origins.Theme);
        Add("accessibility", $"ReducedMotion={environment.Accessibility.ReducedMotion}, HighContrast={environment.Accessibility.HighContrast}", origins.Accessibility);
        decisions.Add(new UiPlanDecision(UiPlanDecisionCode.EnvironmentProfile, null,
            $"Profile '{profile}' uses controller input first, otherwise logical viewport width (<720 Compact, <1100 Medium, otherwise Wide). Scale is not applied twice.", null));

        void Add(string facet, string value, string origin)
            => decisions.Add(new UiPlanDecision(UiPlanDecisionCode.EnvironmentFacet, null,
                $"{facet} = {value}; origin: {origin}.", null));
    }

    private UiPlannedCollectionRecipe ResolveCollectionRecipe(
        PlanningElementView element,
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

        if (!_catalog.TryGetPresentationProperty("density", out UiPropertySymbol? densityProperty) ||
            densityProperty == null ||
            !_catalog.TryGetPropertyValue(densityProperty, "Default", out UiEnumValueSymbol? defaultDensity) ||
            defaultDensity == null ||
            !_catalog.TryGetPropertyValue(densityProperty, "Compact", out UiEnumValueSymbol? compactDensity) ||
            compactDensity == null ||
            !_catalog.TryGetPropertyValue(densityProperty, "Comfortable", out UiEnumValueSymbol? comfortableDensity) ||
            comfortableDensity == null)
            throw new InvalidOperationException("Foundation density catalog values are not registered.");

        UiPropertyAssignmentIr? explicitDensity = indexes.FindAssignment(element.Id, "density", host.Profile)
            ?? indexes.FindAssignment(element.Id, "density", profile: null);
        UiSymbolId density = explicitDensity?.Value is UiSymbolValue densityValue
            ? densityValue.Symbol
            : defaultDensity.Id;
        if (density != defaultDensity.Id && density != compactDensity.Id && density != comfortableDensity.Id)
            throw new InvalidOperationException(
                $"Collection element '{element.Id}' resolved an unknown density catalog value '{density}'.");
        return new UiPlannedCollectionRecipe(sizing, density);
    }

    private static string DefaultRegion(PlanningElementView element, PlanIndexes indexes)
    {
        if (indexes.Has(element, UiCapabilities.Actions)) return "Actions";
        if (indexes.Has(element, UiCapabilities.Navigate)) return "Navigation";
        if (indexes.Has(element, UiCapabilities.Search) || indexes.Has(element, UiCapabilities.Filter)) return "Utility";
        if (indexes.Has(element, UiCapabilities.Inspect)) return "Context";
        if (indexes.Has(element, UiCapabilities.Monitor)) return "Footer";
        return "Primary";
    }

    private static string DefaultPresentation(
        PlanningElementView element,
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
        if (indexes.Has(element, UiCapabilities.Select) && element.IsCollection) return "List";
        return "Value";
    }

    private static bool IsAdaptive(PlanningElementView element, UiSymbolId profile, PlanIndexes indexes)
        => (indexes.Has(element, UiCapabilities.Browse) || indexes.Has(element, UiCapabilities.Inspect)) &&
           (profile == UiPresentationProfiles.Compact.Id || profile == UiPresentationProfiles.Controller.Id);

    private readonly record struct PlanningElementView(
        UiSymbolId Id, IReadOnlyList<UiCapability> Capabilities, bool IsCollection);

    // Value-type view of either input; the existing runtime path does not copy elements.
    private readonly struct PlanningSource
    {
        private readonly UiExperienceDefinition? _experience;
        private readonly UiPlanningInput? _input;

        internal PlanningSource(UiExperienceDefinition experience) { _experience = experience; _input = null; }
        internal PlanningSource(UiPlanningInput input) { _experience = null; _input = input; }
        internal UiSymbolId Id => _experience?.Id ?? _input!.Id;
        internal int Count => _experience?.Elements.Count ?? _input!.Elements.Count;

        internal PlanningElementView ElementAt(int index)
        {
            if (_experience is { } experience)
            {
                UiSemanticElementDefinition element = experience.Elements[index];
                return new PlanningElementView(element.Id, element.Capabilities, element.Source is IUiSemanticCollectionSource);
            }
            UiPlanningElement detached = _input!.Elements[index];
            return new PlanningElementView(detached.Id, detached.Capabilities, detached.IsCollection);
        }
    }

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
            PlanningSource experience,
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

            var elementCapabilities = new Dictionary<UiSymbolId, HashSet<UiSymbolId>>(experience.Count);
            var capabilities = new HashSet<UiSymbolId>();
            for (int i = 0; i < experience.Count; i++)
            {
                PlanningElementView element = experience.ElementAt(i);
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

        internal bool Has(PlanningElementView element, UiCapability capability)
            => _elementCapabilities[element.Id].Contains(capability.Id);

        internal bool HasAny(UiCapability capability)
            => _capabilities.Contains(capability.Id);
    }
}
