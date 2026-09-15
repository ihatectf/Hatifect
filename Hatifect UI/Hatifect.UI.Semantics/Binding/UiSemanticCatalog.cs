using System;
using System.Collections.Generic;

namespace Hatifect.UI.Semantics;

public sealed class UiSemanticCatalog
{
    private const string BuiltInScope = "Hatifect.UI";
    private readonly Dictionary<(UiDefinitionKind Kind, string Name), UiPropertySymbol> _properties = new();
    private readonly Dictionary<string, UiTokenSymbol> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiPresentationSymbol> _presentations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiSymbolId> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiSymbolId> _regions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiSymbolId> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiSymbolId> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<(UiSymbolId Property, string Name), UiEnumValueSymbol> _propertyValues = new();
    private readonly Dictionary<UiSymbolId, UiEnumValueSymbol> _enumValues = new();

    public static UiSemanticCatalog CreateFoundation()
    {
        var catalog = new UiSemanticCatalog();
        catalog.RegisterFoundationPresentations();
        catalog.RegisterFoundationNamedSymbols();
        catalog.RegisterFoundationPresentationProperties();
        catalog.RegisterFoundationVisualProperties();
        catalog.RegisterFoundationTokens();
        return catalog;
    }

    private void RegisterFoundationPresentations()
    {
        AddPresentation("Gallery", "Browse", "Select");
        AddPresentation("List", "Browse", "Select");
        AddPresentation("TextField", "Search");
        AddPresentation("FilterBar", "Filter");
        AddPresentation("Value", "Select");
        AddPresentation("Side", "Inspect");
        AddPresentation("Sheet", "Inspect");
        AddPresentation("Route", "Inspect", "Navigate");
        AddPresentation("Form", "Configure");
        AddPresentation("Status", "Monitor");
        AddPresentation("NavigationList", "Navigate");
        AddPresentation("ActionBar", "Actions");
    }

    private void RegisterFoundationNamedSymbols()
    {
        foreach (string pattern in new[] { "Catalog", "MasterDetail", "Prompt", "Workspace" })
            _patterns.Add(pattern, BuiltIn($"pattern/{pattern}"));
        foreach (string region in new[] { "Navigation", "Utility", "Primary", "Secondary", "Context", "Actions", "Footer", "Overlay" })
            _regions.Add(region, BuiltIn($"region/{region}"));
        foreach (string profile in new[] { "Wide", "Medium", "Compact", "Controller" })
            _profiles.Add(profile, BuiltIn($"profile/{profile}"));
        foreach (string state in new[] { "Hover", "Pressed", "Focused", "Selected", "Checked", "Disabled", "Enter", "Exit", "Congested", "Offline", "Empty", "Loading", "Success", "Error" })
            _states.Add(state, BuiltIn($"state/{state}"));
    }

    private void RegisterFoundationPresentationProperties()
    {
        AddProperty(UiDefinitionKind.Presentation, "use", UiSemanticType.PresentationPattern, UiPropertyEffects.Recompose);
        AddProperty(UiDefinitionKind.Presentation, "view", UiSemanticType.Presentation, UiPropertyEffects.Recompose);
        UiPropertySymbol density = AddProperty(
            UiDefinitionKind.Presentation,
            "density",
            UiSemanticType.EnumValue,
            UiPropertyEffects.Recompose);
        AddEnumValue(density, "Default");
        AddEnumValue(density, "Compact");
        AddEnumValue(density, "Comfortable");
        AddProperty(UiDefinitionKind.Presentation, "prefer", UiSemanticType.Presentation, UiPropertyEffects.Recompose);
        AddProperty(UiDefinitionKind.Presentation, "fallback", UiSemanticType.Presentation, UiPropertyEffects.Recompose);
        AddProperty(UiDefinitionKind.Presentation, "width", UiSemanticType.Length, UiPropertyEffects.Measure | UiPropertyEffects.Arrange);
        UiPropertySymbol itemSizing = AddProperty(
            UiDefinitionKind.Presentation,
            "itemSizing",
            UiSemanticType.EnumValue,
            UiPropertyEffects.Measure | UiPropertyEffects.Arrange);
        AddEnumValue(itemSizing, "Uniform");
        AddEnumValue(itemSizing, "Adaptive");
    }

    private void RegisterFoundationVisualProperties()
    {
        AddProperty(UiDefinitionKind.Visual, "surface", UiSemanticType.SurfaceToken, UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "foreground", UiSemanticType.ColorToken, UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "radius", UiSemanticType.RadiusToken, UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "padding", UiSemanticType.SpaceToken, UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "offset.y", UiSemanticType.Length, UiPropertyEffects.Arrange | UiPropertyEffects.Render, animatable: true);
        AddProperty(UiDefinitionKind.Visual, "motion", UiSemanticType.MotionToken, UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "border", UiSemanticType.Border, UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "typography", UiSemanticType.TypographyToken, UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "elevation", UiSemanticType.ElevationToken, UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "transform", UiSemanticType.TransformToken, UiPropertyEffects.Arrange | UiPropertyEffects.Render, animatable: true);
        AddProperty(UiDefinitionKind.Visual, "opacity", UiSemanticType.Opacity, UiPropertyEffects.Render, animatable: true);
        AddProperty(UiDefinitionKind.Visual, "prompt.foreground", UiSemanticType.ColorToken, UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "prompt.typography", UiSemanticType.TypographyToken, UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render);
        AddProperty(UiDefinitionKind.Visual, "prompt.spacing", UiSemanticType.SpaceToken, UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Render);
    }

    private void RegisterFoundationTokens()
    {
        foreach (string name in new[] { "Canvas", "Raised", "Secondary", "Hover", "Pressed", "Disabled", "Popup", "Modal" })
            AddToken($"Surface.{name}", UiSemanticType.SurfaceToken);
        foreach (string name in new[] { "Primary", "Secondary", "Muted", "Accent", "Danger", "Success", "InputPrompt" })
            AddToken($"Text.{name}", UiSemanticType.ColorToken);
        AddToken("Accent", UiSemanticType.ColorToken);
        foreach (string name in new[] { "XS", "S", "M", "L", "XL" })
            AddToken($"Space.{name}", UiSemanticType.SpaceToken);
        foreach (string name in new[] { "S", "M", "L", "XL" })
            AddToken($"Radius.{name}", UiSemanticType.RadiusToken);
        foreach (string name in new[] { "None", "Fast", "Normal", "Slow" })
            AddToken($"Motion.{name}", UiSemanticType.MotionToken);
        foreach (string name in new[] { "Body", "Label", "Title", "InputPrompt" })
            AddToken($"Typography.{name}", UiSemanticType.TypographyToken);
        foreach (string name in new[] { "Subtle", "Strong", "Focus" })
            AddToken($"Border.{name}", UiSemanticType.Border);
        foreach (string name in new[] { "None", "Low", "High" })
            AddToken($"Elevation.{name}", UiSemanticType.ElevationToken);
        foreach (string name in new[] { "None", "Raised", "Pressed" })
            AddToken($"Transform.{name}", UiSemanticType.TransformToken);
        foreach (string name in new[] { "Hidden", "Disabled", "Visible" })
            AddToken($"Opacity.{name}", UiSemanticType.Opacity);
    }

    public bool TryGetProperty(UiDefinitionKind kind, string name, out UiPropertySymbol? property)
        => _properties.TryGetValue((kind, name), out property);
    public bool TryGetVisualProperty(string name, out UiPropertySymbol? property)
        => TryGetProperty(UiDefinitionKind.Visual, name, out property);
    public bool TryGetPresentationProperty(string name, out UiPropertySymbol? property)
        => TryGetProperty(UiDefinitionKind.Presentation, name, out property);
    public bool TryGetToken(string name, out UiTokenSymbol? token) => _tokens.TryGetValue(name, out token);
    public bool TryGetPresentation(string name, out UiPresentationSymbol? presentation) => _presentations.TryGetValue(name, out presentation);
    public bool TryGetPattern(string name, out UiSymbolId pattern) => _patterns.TryGetValue(name, out pattern);
    public bool TryGetRegion(string name, out UiSymbolId region) => _regions.TryGetValue(name, out region);
    public bool TryGetProfile(string name, out UiSymbolId profile) => _profiles.TryGetValue(name, out profile);
    public bool TryGetState(string name, out UiSymbolId state) => _states.TryGetValue(name, out state);
    public bool TryGetPropertyValue(UiPropertySymbol property, string name, out UiEnumValueSymbol? value)
    {
        ArgumentNullException.ThrowIfNull(property);
        return _propertyValues.TryGetValue((property.Id, name), out value);
    }
    public bool HasPropertyValues(UiPropertySymbol property)
    {
        ArgumentNullException.ThrowIfNull(property);
        foreach ((UiSymbolId Property, string Name) key in _propertyValues.Keys)
            if (key.Property == property.Id) return true;
        return false;
    }
    public bool TryGetEnumValue(UiSymbolId id, out UiEnumValueSymbol? value)
        => _enumValues.TryGetValue(id, out value);

    internal IEnumerable<UiPropertySymbol> Properties => _properties.Values;
    internal IEnumerable<UiTokenSymbol> Tokens => _tokens.Values;
    internal IEnumerable<UiPresentationSymbol> Presentations => _presentations.Values;
    internal IEnumerable<KeyValuePair<string, UiSymbolId>> Patterns => _patterns;
    internal IEnumerable<KeyValuePair<string, UiSymbolId>> Regions => _regions;
    internal IEnumerable<KeyValuePair<string, UiSymbolId>> Profiles => _profiles;
    internal IEnumerable<KeyValuePair<string, UiSymbolId>> States => _states;
    internal IEnumerable<UiEnumValueSymbol> EnumValues => _enumValues.Values;

    public UiSymbolId Capability(string name) => BuiltIn($"capability/{name}");

    private void AddPresentation(string name, params string[] capabilities)
    {
        var supported = new HashSet<UiSymbolId>();
        foreach (string capability in capabilities) supported.Add(Capability(capability));
        _presentations.Add(name, new UiPresentationSymbol(BuiltIn($"presentation/{name}"), name, supported));
    }

    private UiPropertySymbol AddProperty(
        UiDefinitionKind kind,
        string name,
        UiSemanticType type,
        UiPropertyEffects effects,
        bool animatable = false)
    {
        var property = new UiPropertySymbol(
            BuiltIn($"property/{kind}/{name}"), name, kind, type, effects, animatable);
        _properties.Add((kind, name), property);
        return property;
    }

    private void AddEnumValue(UiPropertySymbol property, string name)
    {
        var value = new UiEnumValueSymbol(
            BuiltIn($"enum/{property.DefinitionKind}/{property.Name}/{name}"),
            name,
            property.Id);
        _propertyValues.Add((property.Id, name), value);
        _enumValues.Add(value.Id, value);
    }

    private void AddToken(string name, UiSemanticType type)
        => _tokens.Add(name, new UiTokenSymbol(BuiltIn($"token/{name}"), name, type));

    private static UiSymbolId BuiltIn(string local) => new(BuiltInScope, local);
}
