using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Scene;

internal sealed class UiButtonStateVisuals
{
    private readonly UiSymbolId _node;
    private readonly UiVisualResolution?[] _states = new UiVisualResolution?[16];
    private readonly UiVisualResolution?[] _renderStates = new UiVisualResolution?[16];
    private readonly Func<IReadOnlyList<UiVisualStateRef>, UiVisualResolution> _resolve;
    private readonly bool _renderOnly;
    private const UiPropertyEffects Geometry = UiPropertyEffects.Measure | UiPropertyEffects.Arrange | UiPropertyEffects.Recompose;

    internal UiButtonStateVisuals(UiSymbolId node, UiVisualResolution normal,
        Func<IReadOnlyList<UiVisualStateRef>, UiVisualResolution> resolve, bool renderOnly)
    { _node = node; _states[0] = normal; _resolve = resolve; _renderOnly = renderOnly; }

    internal UiVisualResolution Resolve(bool enabled, UiInteractionSnapshot? interaction)
    {
        int state = State(enabled, interaction);
        if (_states[state] is { } cached) return cached;
        var active = new List<UiVisualStateRef>(4);
        if ((state & 2) != 0) active.Add(UiVisualStates.Hover);
        if ((state & 4) != 0) active.Add(UiVisualStates.Focused);
        if ((state & 8) != 0) active.Add(UiVisualStates.Pressed);
        if ((state & 1) != 0) active.Add(UiVisualStates.Disabled);
        UiVisualResolution resolved = _resolve(active);
        if (_renderOnly && (resolved.InvalidationFrom(_states[0]!) & Geometry) != 0)
            throw new InvalidOperationException($"Button states for '{_node}' change layout geometry.");
        _states[state] = resolved;
        return resolved;
    }

    internal UiVisualResolution ForHost(bool enabled, UiInteractionSnapshot? interaction, UiVisualResolution geometry)
    {
        UiVisualResolution resolved = Resolve(enabled, interaction);
        if (_renderOnly) return resolved;
        int state = State(enabled, interaction);
        if (_renderStates[state] is { } cached) return cached;
        // Existing legacy geometry recipes still apply when the consumer composes a scene.
        // A captured host may update only render properties until that next composition;
        // never draw different typography/padding against stale measured geometry.
        if ((resolved.InvalidationFrom(geometry) & Geometry) == 0) return _renderStates[state] = resolved;
        var properties = resolved.Properties.Where(p => (p.Property.Effects & Geometry) == 0)
            .ToDictionary(p => p.Property.Id);
        foreach (var property in geometry.Properties)
            if ((property.Property.Effects & Geometry) != 0) properties[property.Property.Id] = property;
        var trace = resolved.Trace.Where(p => (p.Property.Effects & Geometry) == 0)
            .Concat(geometry.Trace.Where(p => (p.Property.Effects & Geometry) != 0)).ToArray();
        return _renderStates[state] = new(properties, trace);
    }

    private int State(bool enabled, UiInteractionSnapshot? interaction)
        => (enabled ? 0 : 1) | (interaction?.Hovered == _node ? 2 : 0) |
           (interaction?.Focused == _node ? 4 : 0) | (interaction?.Pressed == _node ? 8 : 0);
}
