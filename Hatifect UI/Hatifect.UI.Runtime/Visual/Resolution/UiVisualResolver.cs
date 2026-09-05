using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Hatifect.UI.Runtime.Caching;
using Hatifect.UI.Runtime.Identity;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Visual.Resolution;

public sealed record UiVisualStateRef
{
    public UiVisualStateRef(UiSymbolId id, int priority)
    {
        if (!id.IsValid) throw new ArgumentException("A stable visual state ID is required.", nameof(id));
        Id = id;
        Priority = priority;
    }

    public UiSymbolId Id { get; }
    public int Priority { get; }
}

public static class UiVisualStates
{
    public static readonly UiVisualStateRef Hover = State("Hover", 100);
    public static readonly UiVisualStateRef Focused = State("Focused", 200);
    public static readonly UiVisualStateRef Selected = State("Selected", 250);
    public static readonly UiVisualStateRef Checked = State("Checked", 250);
    public static readonly UiVisualStateRef Pressed = State("Pressed", 300);
    public static readonly UiVisualStateRef Disabled = State("Disabled", 400);
    public static readonly UiVisualStateRef Enter = State("Enter", 100);
    public static readonly UiVisualStateRef Exit = State("Exit", 200);
    public static readonly UiVisualStateRef Congested = State("Congested", 100);
    public static readonly UiVisualStateRef Offline = State("Offline", 200);

    private static UiVisualStateRef State(string name, int priority)
        => new(new UiSymbolId("Hatifect.UI", $"state/{name}"), priority);
}

public sealed class UiVisualContext
{
    public UiVisualContext(
        UiSymbolId role,
        UiSymbolId profile,
        IEnumerable<UiVisualStateRef>? domainStates = null,
        IEnumerable<UiVisualStateRef>? interactionStates = null)
    {
        if (!role.IsValid) throw new ArgumentException("A stable visual role ID is required.", nameof(role));
        if (!profile.IsValid) throw new ArgumentException("A stable presentation profile ID is required.", nameof(profile));
        Role = role;
        Profile = profile;
        DomainStates = CopyStates(domainStates, nameof(domainStates));
        InteractionStates = CopyStates(interactionStates, nameof(interactionStates));
        if (DomainStates.Select(state => state.Id).Intersect(InteractionStates.Select(state => state.Id)).Any())
            throw new ArgumentException("A visual state cannot be active in both domain and interaction layers.");
    }

    public UiSymbolId Role { get; }
    public UiSymbolId Profile { get; }
    public IReadOnlyList<UiVisualStateRef> DomainStates { get; }
    public IReadOnlyList<UiVisualStateRef> InteractionStates { get; }

    private static IReadOnlyList<UiVisualStateRef> CopyStates(
        IEnumerable<UiVisualStateRef>? states,
        string parameter)
    {
        UiVisualStateRef[] copy = states?.ToArray() ?? Array.Empty<UiVisualStateRef>();
        if (copy.Any(state => state == null)) throw new ArgumentException("Visual states cannot contain null.", parameter);
        if (copy.Select(state => state.Id).Distinct().Count() != copy.Length)
            throw new ArgumentException("A visual state may be active only once per layer.", parameter);
        return Array.AsReadOnly(copy);
    }
}

public sealed record UiVisualOverride
{
    public UiVisualOverride(UiPropertySymbol property, UiBoundValue value, UiSourceProvenance? provenance = null)
    {
        Property = property ?? throw new ArgumentNullException(nameof(property));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Provenance = provenance;
    }

    public UiPropertySymbol Property { get; }
    public UiBoundValue Value { get; }
    public UiSourceProvenance? Provenance { get; }
}

public enum UiVisualResolutionLayer
{
    RoleDefault,
    FeatureRecipe,
    DomainState,
    InteractionState,
    LocalOverride
}

public sealed record UiResolvedTransition(object From, object To);

public sealed record UiResolvedVisualProperty(
    UiPropertySymbol Property,
    object Value,
    UiVisualResolutionLayer Layer,
    UiSymbolId? State,
    UiSymbolId? Token,
    UiSourceProvenance? Provenance);

public sealed record UiVisualResolutionStep(
    UiPropertySymbol Property,
    UiVisualResolutionLayer Layer,
    UiSymbolId? State,
    UiSymbolId? Token,
    UiSourceProvenance? Provenance);

public sealed class UiVisualResolution
{
    private readonly IReadOnlyDictionary<UiSymbolId, UiResolvedVisualProperty> _properties;

    internal UiVisualResolution(
        IDictionary<UiSymbolId, UiResolvedVisualProperty> properties,
        UiVisualResolutionStep[] trace)
    {
        _properties = new ReadOnlyDictionary<UiSymbolId, UiResolvedVisualProperty>(properties);
        Properties = Array.AsReadOnly(properties.Values
            .OrderBy(value => value.Property.Id, UiSymbolIdOrdinalComparer.Instance)
            .ToArray());
        Trace = Array.AsReadOnly(trace);
    }

    public IReadOnlyList<UiResolvedVisualProperty> Properties { get; }
    public IReadOnlyList<UiVisualResolutionStep> Trace { get; }

    public bool TryGet(UiPropertySymbol property, out UiResolvedVisualProperty? value)
    {
        ArgumentNullException.ThrowIfNull(property);
        return _properties.TryGetValue(property.Id, out value);
    }

    public UiPropertyEffects InvalidationFrom(UiVisualResolution previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        UiPropertyEffects effects = UiPropertyEffects.None;
        foreach (UiSymbolId id in _properties.Keys.Concat(previous._properties.Keys).Distinct())
        {
            _properties.TryGetValue(id, out UiResolvedVisualProperty? current);
            previous._properties.TryGetValue(id, out UiResolvedVisualProperty? old);
            if (current?.Value.Equals(old?.Value) == true && old != null) continue;
            effects |= (current ?? old)!.Property.Effects;
        }
        return effects;
    }
}

/// <summary>Deterministic semantic-role resolver. It contains no selectors or source-order cascade.</summary>
public sealed class UiVisualResolver
{
    internal const int MaximumCachedDefinitions = 16;
    private readonly UiBoundedCache<UiVisualDefinition, UiVisualRecipeIndex> _recipes =
        new(MaximumCachedDefinitions);

    internal int CachedDefinitionCount => _recipes.Count;

    public UiVisualResolution Resolve(
        UiVisualContext context,
        UiTheme theme,
        UiVisualDefinition? feature = null,
        IReadOnlyList<UiVisualOverride>? roleDefaults = null,
        IReadOnlyList<UiVisualOverride>? localOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(theme);

        var resolved = new Dictionary<UiSymbolId, UiResolvedVisualProperty>();
        var trace = new List<UiVisualResolutionStep>();
        ApplyOverrides(roleDefaults, UiVisualResolutionLayer.RoleDefault, theme, resolved, trace);

        if (feature != null)
        {
            IReadOnlyList<UiPropertyAssignmentIr> recipes = RecipesFor(feature, context.Role);
            ApplyAssignments(
                SelectBase(recipes, context),
                UiVisualResolutionLayer.FeatureRecipe,
                theme,
                resolved,
                trace);
            ApplyAssignments(
                SelectStates(recipes, context, context.DomainStates, UiVisualResolutionLayer.DomainState),
                UiVisualResolutionLayer.DomainState,
                theme,
                resolved,
                trace);
            ApplyAssignments(
                SelectStates(recipes, context, context.InteractionStates, UiVisualResolutionLayer.InteractionState),
                UiVisualResolutionLayer.InteractionState,
                theme,
                resolved,
                trace);
        }

        ApplyOverrides(localOverrides, UiVisualResolutionLayer.LocalOverride, theme, resolved, trace);
        return new UiVisualResolution(resolved, trace.ToArray());
    }

    internal void ClearRecipeCache() => _recipes.Clear();

    private IReadOnlyList<UiPropertyAssignmentIr> RecipesFor(UiVisualDefinition definition, UiSymbolId role)
    {
        if (!_recipes.TryGetValue(definition, out UiVisualRecipeIndex? index))
        {
            index = new UiVisualRecipeIndex(definition.Recipes);
            _recipes.Set(definition, index);
        }
        return index!.For(role);
    }

    private static IReadOnlyList<SelectedAssignment> SelectBase(
        IReadOnlyList<UiPropertyAssignmentIr> recipes,
        UiVisualContext context)
    {
        return recipes
            .Where(recipe => recipe.Target == context.Role && recipe.State == null &&
                             (recipe.Profile == null || recipe.Profile == context.Profile))
            .GroupBy(recipe => recipe.Property.Id)
            .Select(group => SelectProfile(group, context.Profile, state: null))
            .OrderBy(
                selected => selected.Assignment.Property.Id,
                UiSymbolIdOrdinalComparer.Instance)
            .ToArray();
    }

    private static IReadOnlyList<SelectedAssignment> SelectStates(
        IReadOnlyList<UiPropertyAssignmentIr> recipes,
        UiVisualContext context,
        IReadOnlyList<UiVisualStateRef> activeStates,
        UiVisualResolutionLayer layer)
    {
        var byId = activeStates.ToDictionary(state => state.Id);
        var selectedByState = recipes
            .Where(recipe => recipe.Target == context.Role && recipe.State is { } state && byId.ContainsKey(state) &&
                             (recipe.Profile == null || recipe.Profile == context.Profile))
            .GroupBy(recipe => (recipe.State!.Value, recipe.Property.Id))
            .Select(group => SelectProfile(group, context.Profile, group.Key.Item1))
            .ToArray();

        var result = new List<SelectedAssignment>();
        foreach (IGrouping<UiSymbolId, SelectedAssignment> property in selectedByState.GroupBy(item => item.Assignment.Property.Id))
        {
            int highest = property.Max(item => byId[item.State!.Value].Priority);
            SelectedAssignment[] winners = property.Where(item => byId[item.State!.Value].Priority == highest).ToArray();
            if (winners.Length != 1)
                throw new InvalidOperationException(
                    $"Visual property '{winners[0].Assignment.Property.Name}' has an equal-priority conflict in {layer}.");
            result.Add(winners[0]);
        }
        return result
            .OrderBy(
                item => item.Assignment.Property.Id,
                UiSymbolIdOrdinalComparer.Instance)
            .ToArray();
    }

    private static SelectedAssignment SelectProfile(
        IEnumerable<UiPropertyAssignmentIr> candidates,
        UiSymbolId profile,
        UiSymbolId? state)
    {
        UiPropertyAssignmentIr[] exact = candidates.Where(candidate => candidate.Profile == profile).ToArray();
        UiPropertyAssignmentIr[] defaults = candidates.Where(candidate => candidate.Profile == null).ToArray();
        UiPropertyAssignmentIr[] selected = exact.Length > 0 ? exact : defaults;
        if (selected.Length != 1)
            throw new InvalidOperationException(
                $"Visual property '{selected.FirstOrDefault()?.Property.Name ?? "unknown"}' has an ambiguous profile layer.");
        return new SelectedAssignment(selected[0], state);
    }

    private static void ApplyOverrides(
        IReadOnlyList<UiVisualOverride>? overrides,
        UiVisualResolutionLayer layer,
        UiTheme theme,
        IDictionary<UiSymbolId, UiResolvedVisualProperty> resolved,
        ICollection<UiVisualResolutionStep> trace)
    {
        if (overrides == null) return;
        foreach (IGrouping<UiSymbolId, UiVisualOverride> group in overrides
                     .GroupBy(item => item.Property.Id)
                     .OrderBy(item => item.Key, UiSymbolIdOrdinalComparer.Instance))
        {
            UiVisualOverride[] values = group.ToArray();
            if (values.Length != 1)
                throw new InvalidOperationException($"Visual property '{values[0].Property.Name}' is assigned more than once in {layer}.");
            UiVisualOverride item = values[0];
            Apply(item.Property, item.Value, layer, null, item.Provenance, theme, resolved, trace);
        }
    }

    private static void ApplyAssignments(
        IReadOnlyList<SelectedAssignment> assignments,
        UiVisualResolutionLayer layer,
        UiTheme theme,
        IDictionary<UiSymbolId, UiResolvedVisualProperty> resolved,
        ICollection<UiVisualResolutionStep> trace)
    {
        foreach (SelectedAssignment selected in assignments)
        {
            UiPropertyAssignmentIr item = selected.Assignment;
            Apply(item.Property, item.Value, layer, selected.State, item.Provenance, theme, resolved, trace);
        }
    }

    private static void Apply(
        UiPropertySymbol property,
        UiBoundValue value,
        UiVisualResolutionLayer layer,
        UiSymbolId? state,
        UiSourceProvenance? provenance,
        UiTheme theme,
        IDictionary<UiSymbolId, UiResolvedVisualProperty> resolved,
        ICollection<UiVisualResolutionStep> trace)
    {
        object computed = ResolveValue(value, theme);
        UiSymbolId? token = Token(value);
        resolved[property.Id] = new UiResolvedVisualProperty(property, computed, layer, state, token, provenance);
        trace.Add(new UiVisualResolutionStep(property, layer, state, token, provenance));
    }

    private static object ResolveValue(UiBoundValue value, UiTheme theme)
        => value switch
        {
            UiSymbolValue symbol when IsThemeValue(symbol.Type) => theme.Resolve(symbol),
            UiSymbolValue symbol => symbol.Symbol,
            UiBooleanValue boolean => boolean.Value,
            UiIntegerValue integer => integer.Value,
            UiFloatValue number => number.Value,
            UiLengthValue length => length.Value,
            UiOpacityValue opacity => new UiOpacity((float)opacity.Value),
            UiStringValue text => text.Value,
            UiBorderValue border => new UiBorder((UiColor)theme.Resolve(border.Color), border.Width),
            UiTransitionValue transition => new UiResolvedTransition(
                ResolveValue(transition.From, theme), ResolveValue(transition.To, theme)),
            _ => throw new InvalidOperationException($"Unsupported visual value type '{value.GetType().Name}'.")
        };

    private static bool IsThemeValue(UiSemanticType type)
        => type.Kind is UiSemanticTypeKind.SurfaceToken or UiSemanticTypeKind.ColorToken or
            UiSemanticTypeKind.SpaceToken or UiSemanticTypeKind.RadiusToken or UiSemanticTypeKind.MotionToken or
            UiSemanticTypeKind.TypographyToken or UiSemanticTypeKind.ElevationToken or UiSemanticTypeKind.TransformToken or
            UiSemanticTypeKind.Opacity or UiSemanticTypeKind.Border;

    private static UiSymbolId? Token(UiBoundValue value)
        => value switch
        {
            UiSymbolValue symbol when IsThemeValue(symbol.Type) => symbol.Symbol,
            UiBorderValue border => border.Color.Symbol,
            _ => null
        };

    private sealed record SelectedAssignment(UiPropertyAssignmentIr Assignment, UiSymbolId? State);

    private sealed class UiVisualRecipeIndex
    {
        private readonly IReadOnlyDictionary<UiSymbolId, UiPropertyAssignmentIr[]> _byRole;

        public UiVisualRecipeIndex(IReadOnlyList<UiPropertyAssignmentIr> recipes)
            => _byRole = recipes
                .Where(recipe => recipe.Target.HasValue)
                .GroupBy(recipe => recipe.Target!.Value)
                .ToDictionary(group => group.Key, group => group.ToArray());

        public IReadOnlyList<UiPropertyAssignmentIr> For(UiSymbolId role)
            => _byRole.TryGetValue(role, out UiPropertyAssignmentIr[]? recipes)
                ? recipes
                : Array.Empty<UiPropertyAssignmentIr>();
    }
}
