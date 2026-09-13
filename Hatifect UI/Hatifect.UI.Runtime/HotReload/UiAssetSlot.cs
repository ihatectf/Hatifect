using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.HotReload;

public sealed class UiAssetUpdateResult
{
    internal UiAssetUpdateResult(
        bool accepted,
        bool recompose,
        UiPropertyEffects effects,
        UiDiagnostic[] diagnostics)
    {
        Accepted = accepted;
        Recompose = recompose;
        Effects = effects;
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public bool Accepted { get; }
    public bool RetainedPrevious => !Accepted;
    public bool Recompose { get; }
    public UiPropertyEffects Effects { get; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }
}

/// <summary>Atomic Last Known Good slot for one lowered Presentation or Visual definition.</summary>
public sealed class UiAssetSlot<TDefinition> where TDefinition : UiBoundDefinition
{
    public UiAssetSlot(TDefinition initial)
        => Current = initial ?? throw new ArgumentNullException(nameof(initial));

    public TDefinition Current { get; private set; }
    public long Version { get; private set; }

    public UiAssetUpdateResult Apply(UiCompilationResult candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        UiDiagnostic[] diagnostics = candidate.Diagnostics.ToArray();
        if (!candidate.IsValid) return new UiAssetUpdateResult(
            accepted: false,
            recompose: false,
            UiPropertyEffects.None,
            diagnostics);
        if (candidate.Definition is not TDefinition next)
            throw new InvalidOperationException(
                $"Asset slot expects {typeof(TDefinition).Name}, got {candidate.Definition?.GetType().Name ?? "null"}.");

        bool recompose = next is UiPresentationDefinition;
        UiPropertyEffects effects = next is UiVisualDefinition newVisual && Current is UiVisualDefinition oldVisual
            ? ChangedEffects(oldVisual, newVisual)
            : UiPropertyEffects.None;
        Current = next;
        Version++;
        return new UiAssetUpdateResult(true, recompose, effects, diagnostics);
    }

    private static UiPropertyEffects ChangedEffects(UiVisualDefinition previous, UiVisualDefinition next)
    {
        UiPropertyEffects effects = UiPropertyEffects.None;
        UiSymbolId[] properties = previous.Recipes.Select(item => item.Property.Id)
            .Concat(next.Recipes.Select(item => item.Property.Id))
            .Distinct()
            .ToArray();
        foreach (UiSymbolId property in properties)
        {
            UiPropertyAssignmentIr[] before = Canonical(previous.Recipes, property);
            UiPropertyAssignmentIr[] after = Canonical(next.Recipes, property);
            if (Equivalent(before, after)) continue;
            UiPropertySymbol metadata = after.FirstOrDefault()?.Property ?? before[0].Property;
            effects |= metadata.Effects;
        }
        return effects;
    }

    private static UiPropertyAssignmentIr[] Canonical(
        IReadOnlyList<UiPropertyAssignmentIr> recipes,
        UiSymbolId property)
        => recipes.Where(item => item.Property.Id == property)
            .OrderBy(item => item.Target?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Profile?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.State?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToArray();

    private static bool Equivalent(UiPropertyAssignmentIr[] left, UiPropertyAssignmentIr[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i].Target != right[i].Target ||
                left[i].Profile != right[i].Profile ||
                left[i].State != right[i].State ||
                left[i].Value != right[i].Value)
                return false;
        }
        return true;
    }
}
