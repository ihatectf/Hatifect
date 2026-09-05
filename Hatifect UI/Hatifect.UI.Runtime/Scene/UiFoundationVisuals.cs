using System;
using System.Collections.Generic;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;

namespace Hatifect.UI.Runtime.Scene;

internal sealed class UiFoundationVisuals
{
    private readonly UiSemanticCatalog _catalog;

    public UiFoundationVisuals(UiSemanticCatalog catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public IReadOnlyList<UiVisualOverride> For(
        UiSceneNodeKind kind,
        UiHostPolicy host,
        IReadOnlyList<UiVisualStateRef>? domainStates = null,
        IReadOnlyList<UiVisualStateRef>? interactionStates = null)
    {
        var values = new List<UiVisualOverride>();
        switch (kind)
        {
            case UiSceneNodeKind.Host:
                values.Add(Token("surface", HostSurface(host), UiSemanticType.SurfaceToken));
                values.Add(Token("foreground", UiThemeTokens.TextPrimary.Id, UiSemanticType.ColorToken));
                values.Add(Token("padding", UiThemeTokens.SpaceL.Id, UiSemanticType.SpaceToken));
                break;
            case UiSceneNodeKind.Button:
            case UiSceneNodeKind.RouteButton:
                values.Add(Token(
                    "surface",
                    kind == UiSceneNodeKind.RouteButton && Contains(domainStates, UiVisualStates.Selected)
                        ? UiThemeTokens.SurfacePressed.Id
                        : UiThemeTokens.SurfaceRaised.Id,
                    UiSemanticType.SurfaceToken));
                values.Add(Token("foreground", UiThemeTokens.TextPrimary.Id, UiSemanticType.ColorToken));
                values.Add(Token("radius", UiThemeTokens.RadiusM.Id, UiSemanticType.RadiusToken));
                values.Add(Token("padding", UiThemeTokens.SpaceM.Id, UiSemanticType.SpaceToken));
                values.Add(Token("typography", UiThemeTokens.TypographyLabel.Id, UiSemanticType.TypographyToken));
                values.Add(Token("motion", UiThemeTokens.MotionFast.Id, UiSemanticType.MotionToken));
                values.Add(Token("opacity", UiThemeTokens.OpacityVisible.Id, UiSemanticType.Opacity));
                break;
            case UiSceneNodeKind.Text:
            case UiSceneNodeKind.Collection:
            case UiSceneNodeKind.Inspector:
            case UiSceneNodeKind.Form:
                values.Add(Token("foreground", UiThemeTokens.TextPrimary.Id, UiSemanticType.ColorToken));
                values.Add(Token("typography", UiThemeTokens.TypographyBody.Id, UiSemanticType.TypographyToken));
                break;
            case UiSceneNodeKind.TextInput:
                values.Add(Token("surface", UiThemeTokens.SurfaceSecondary.Id, UiSemanticType.SurfaceToken));
                values.Add(Token("foreground", UiThemeTokens.TextPrimary.Id, UiSemanticType.ColorToken));
                values.Add(Token("radius", UiThemeTokens.RadiusM.Id, UiSemanticType.RadiusToken));
                values.Add(Token("padding", UiThemeTokens.SpaceM.Id, UiSemanticType.SpaceToken));
                values.Add(Token("typography", UiThemeTokens.TypographyBody.Id, UiSemanticType.TypographyToken));
                break;
        }
        bool supportsFocusVisual = kind is UiSceneNodeKind.Button or UiSceneNodeKind.RouteButton or
            UiSceneNodeKind.TextInput or UiSceneNodeKind.Collection;
        if (supportsFocusVisual && Contains(interactionStates, UiVisualStates.Focused))
            values.Add(Token("border", UiThemeTokens.BorderFocus.Id, UiSemanticType.Border));
        if (kind == UiSceneNodeKind.Collection)
        {
            bool selected = Contains(domainStates, UiVisualStates.Selected);
            bool hovered = Contains(interactionStates, UiVisualStates.Hover);
            bool pressed = Contains(interactionStates, UiVisualStates.Pressed);
            if (selected)
            {
                values.Add(Token("radius", UiThemeTokens.RadiusS.Id, UiSemanticType.RadiusToken));
            }
            if (selected || hovered || pressed)
                values.Add(Token(
                    "surface",
                    pressed || selected && !hovered
                        ? UiThemeTokens.SurfacePressed.Id
                        : UiThemeTokens.SurfaceHover.Id,
                    UiSemanticType.SurfaceToken));
        }
        return values;
    }

    private static bool Contains(IReadOnlyList<UiVisualStateRef>? states, UiVisualStateRef expected)
    {
        if (states == null) return false;
        foreach (UiVisualStateRef state in states)
            if (state.Id == expected.Id) return true;
        return false;
    }

    private UiVisualOverride Token(string propertyName, UiSymbolId token, UiSemanticType type)
    {
        if (!_catalog.TryGetVisualProperty(propertyName, out UiPropertySymbol? property) || property == null)
            throw new InvalidOperationException($"Foundation visual property '{propertyName}' is not registered.");
        return new UiVisualOverride(property, new UiSymbolValue(token, TokenName(token), type));
    }

    private static UiSymbolId HostSurface(UiHostPolicy host)
        => host.Modal == UiModalPolicy.Modal
            ? UiThemeTokens.SurfaceModal.Id
            : host.Kind is UiHostKind.Popup or UiHostKind.Context
                ? UiThemeTokens.SurfacePopup.Id
                : UiThemeTokens.SurfaceCanvas.Id;

    private static string TokenName(UiSymbolId id)
        => id.LocalId.StartsWith("token/", StringComparison.Ordinal) ? id.LocalId[6..] : id.LocalId;
}
