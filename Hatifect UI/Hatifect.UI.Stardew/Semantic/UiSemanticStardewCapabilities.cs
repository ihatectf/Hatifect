using System;
using System.Collections.Generic;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;

namespace Hatifect.UI.Stardew.Semantic;

internal sealed class UiSemanticStardewCapabilityException : InvalidOperationException
{
    public const string RotationDiagnosticId = "LUI4001";

    public UiSemanticStardewCapabilityException(string message)
        : base($"{RotationDiagnosticId}: {message}") { }

    public string DiagnosticId => RotationDiagnosticId;
}

/// <summary>Fail-fast validation for semantic values unsupported by the current Stardew backend.</summary>
internal static class UiSemanticStardewCapabilities
{
    private const float RotationTolerance = 0.01f;

    public static void Validate(UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Validate(theme.Resolve(UiThemeTokens.TransformNone), UiThemeTokens.TransformNone.Id.ToString());
        Validate(theme.Resolve(UiThemeTokens.TransformRaised), UiThemeTokens.TransformRaised.Id.ToString());
        Validate(theme.Resolve(UiThemeTokens.TransformPressed), UiThemeTokens.TransformPressed.Id.ToString());
    }

    public static void Validate(UiScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var pending = new Stack<UiSceneNode>();
        pending.Push(scene.Root);
        while (pending.Count > 0)
        {
            UiSceneNode node = pending.Pop();
            Validate(node.Visual, node.Id.ToString());
            if (node is UiCollectionSceneNode collection)
            {
                Validate(collection.SelectedItemVisual, $"{node.Id}/selected-item");
                foreach ((UiSymbolId item, UiVisualResolution visual) in collection.ActiveItemVisuals)
                    Validate(visual, item.ToString());
            }
            foreach (UiSceneNode child in node.Children) pending.Push(child);
        }
    }

    public static Func<UiInteractionSnapshot, UiScene>? Guard(
        Func<UiInteractionSnapshot, UiScene>? composeInteraction)
    {
        if (composeInteraction == null) return null;
        return interaction =>
        {
            UiScene scene = composeInteraction(interaction)
                ?? throw new InvalidOperationException("The interaction scene composer returned null.");
            Validate(scene);
            return scene;
        };
    }

    private static void Validate(UiVisualResolution visual, string context)
    {
        foreach (UiResolvedVisualProperty property in visual.Properties)
        {
            switch (property.Value)
            {
                case UiTransform transform:
                    Validate(transform, context);
                    break;
                case UiResolvedTransition transition:
                    if (transition.From is UiTransform from) Validate(from, context);
                    if (transition.To is UiTransform to) Validate(to, context);
                    break;
            }
        }
    }

    private static void Validate(UiTransform transform, string context)
    {
        if (Math.Abs(transform.RotationDegrees) <= RotationTolerance) return;
        throw new UiSemanticStardewCapabilityException(
            $"Rotated semantic visuals are unsupported by the Stardew bridge; '{context}' requested " +
            $"{transform.RotationDegrees} degrees.");
    }
}
