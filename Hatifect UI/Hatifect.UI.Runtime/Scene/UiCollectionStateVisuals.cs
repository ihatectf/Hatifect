using System;
using System.Collections.Generic;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Scene;

/// <summary>Scene-owned visual policy, independent of source reads and cached item geometry.</summary>
internal sealed class UiCollectionStateVisuals
{
    private const int Selected = 1, Hovered = 2, Focused = 4, Pressed = 8;
    private static readonly UiVisualStateRef[] SelectedState = { UiVisualStates.Selected };
    private readonly UiSymbolId _collection;
    private readonly UiVisualResolution?[] _states = new UiVisualResolution?[16];
    private readonly Func<IReadOnlyList<UiVisualStateRef>?, IReadOnlyList<UiVisualStateRef>, UiVisualResolution> _resolve;

    internal UiCollectionStateVisuals(
        UiSymbolId collection,
        UiVisualResolution normal,
        UiVisualResolution selected,
        Func<IReadOnlyList<UiVisualStateRef>?, IReadOnlyList<UiVisualStateRef>, UiVisualResolution> resolve)
    {
        _collection = collection;
        _states[0] = normal;
        _states[Selected] = selected;
        _resolve = resolve;
    }

    internal UiVisualResolution Resolve(UiSymbolId node, bool selected, UiInteractionSnapshot interaction)
    {
        int state = (selected ? Selected : 0) |
                    (interaction.Hovered == node ? Hovered : 0) |
                    (interaction.Focused == node ? Focused : 0) |
                    (interaction.Pressed == node ? Pressed : 0);
        if (_states[state] is { } cached) return cached;

        var active = new List<UiVisualStateRef>(3);
        if ((state & Hovered) != 0) active.Add(UiVisualStates.Hover);
        if ((state & Focused) != 0) active.Add(UiVisualStates.Focused);
        if ((state & Pressed) != 0) active.Add(UiVisualStates.Pressed);
        UiVisualResolution resolved = _resolve(selected ? SelectedState : null, active);
        UiPropertyEffects forbidden = UiPropertyEffects.Recompose | UiPropertyEffects.Measure | UiPropertyEffects.Arrange;
        if ((resolved.InvalidationFrom(_states[0]!) & forbidden) != 0)
            throw new InvalidOperationException(
                $"Collection item states for '{_collection}' change layout geometry. State recipes may change render properties only.");
        _states[state] = resolved;
        return resolved;
    }
}
