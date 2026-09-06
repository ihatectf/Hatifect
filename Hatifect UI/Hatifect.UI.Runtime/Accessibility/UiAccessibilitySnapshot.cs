using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Actions;

namespace Hatifect.UI.Runtime.Accessibility;

internal enum UiAccessibilityRole
{
    Window,
    Dialog,
    Menu,
    Group,
    List,
    ListItem,
    StaticText,
    Button,
    TextField,
    Inspector,
    Form,
    Toolbar
}

internal sealed class UiAccessibilityNodeSnapshot
{
    public UiAccessibilityNodeSnapshot(
        UiSymbolId id,
        UiAccessibilityRole role,
        string? name,
        string? value,
        bool enabled,
        bool focused,
        bool selected,
        UiRect bounds,
        UiRect clip,
        UiAccessibilityNodeSnapshot[] children,
        int? positionInSet = null,
        int? setSize = null)
    {
        Id = id;
        Role = role;
        Name = name;
        Value = value;
        Enabled = enabled;
        Focused = focused;
        Selected = selected;
        Bounds = bounds;
        Clip = clip;
        Children = Array.AsReadOnly(children ?? throw new ArgumentNullException(nameof(children)));
        PositionInSet = positionInSet;
        SetSize = setSize;
    }

    public UiSymbolId Id { get; }
    public UiAccessibilityRole Role { get; }
    public string? Name { get; }
    public string? Value { get; }
    public bool Enabled { get; }
    public bool Focused { get; }
    public bool Selected { get; }
    public UiRect Bounds { get; }
    public UiRect Clip { get; }
    public IReadOnlyList<UiAccessibilityNodeSnapshot> Children { get; }
    public int? PositionInSet { get; }
    public int? SetSize { get; }
}

internal sealed record UiAccessibilitySnapshot(
    UiSymbolId Experience,
    UiAccessibilityNodeSnapshot Root);

internal sealed record UiAccessibilityPortalSnapshot(
    UiSymbolId Id,
    UiSymbolId OwnerNode,
    UiSymbolId? OwnerPortal,
    bool Modal,
    UiAccessibilitySnapshot Tree);

internal sealed class UiAccessibilityHostSnapshot
{
    public UiAccessibilityHostSnapshot(
        UiAccessibilitySnapshot root,
        UiAccessibilityPortalSnapshot[] portals)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Portals = Array.AsReadOnly(portals ?? throw new ArgumentNullException(nameof(portals)));
    }

    public UiAccessibilitySnapshot Root { get; }
    public IReadOnlyList<UiAccessibilityPortalSnapshot> Portals { get; }
    public bool RootFocusScopeActive => Portals.Count == 0;
    public UiSymbolId? ActiveFocusPortal => Portals.Count == 0 ? null : Portals[^1].Id;
}

/// <summary>
/// Projects immutable Scene, Layout, and Interaction revisions into a host-free semantic tree.
/// Platform adapters may translate this snapshot, but native accessibility ownership stays outside Runtime.
/// </summary>
internal sealed class UiAccessibilitySnapshotBuilder
{
    public UiAccessibilitySnapshot Build(
        UiScene scene,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot interaction,
        IUiActionResolver? actions = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(interaction);
        return new UiAccessibilitySnapshot(
            scene.Experience,
            BuildNode(scene, scene.Root, layout, interaction, actions));
    }

    private static UiAccessibilityNodeSnapshot BuildNode(
        UiScene scene,
        UiSceneNode node,
        UiLayoutSnapshot layout,
        UiInteractionSnapshot interaction, IUiActionResolver? actions)
    {
        if (!layout.TryGetEntry(node.Id, out UiLayoutEntry? entry) || entry == null)
            throw new InvalidOperationException($"Accessibility node '{node.Id}' has no layout entry.");

        UiAccessibilityNodeSnapshot[] children = node.Children
            .Where(IsExposed)
            .Select(child => BuildNode(scene, child, layout, interaction, actions))
            .ToArray();
        if (node is UiCollectionSceneNode collection &&
            layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window) &&
            window != null)
        {
            children = window.Items
                .Where(item => item.Clip.Width > 0 && item.Clip.Height > 0)
                .Select(item => new UiAccessibilityNodeSnapshot(
                    item.Node,
                    UiAccessibilityRole.ListItem,
                    item.Item.Label,
                    value: null,
                    enabled: true,
                    focused: interaction.Focused == item.Node,
                    selected: collection.IsSelected(item.Item.Id),
                    item.Bounds,
                    item.Clip,
                    Array.Empty<UiAccessibilityNodeSnapshot>(),
                    item.Index + 1,
                    window.TotalCount))
                .ToArray();
        }

        return new UiAccessibilityNodeSnapshot(
            node.Id,
            Role(node),
            Name(scene, node),
            Value(node),
            Enabled(node, actions),
            interaction.Focused == node.Id,
            selected: node is UiRouteButtonSceneNode { IsCurrent: true },
            entry.Bounds,
            entry.Clip,
            children);
    }

    private static bool IsExposed(UiSceneNode node)
        => node is not UiSlotSceneNode slot || slot.Children.Count > 0;

    private static UiAccessibilityRole Role(UiSceneNode node)
        => node switch
        {
            UiHostSceneNode host when host.Policy.Kind == UiHostKind.Context => UiAccessibilityRole.Menu,
            UiHostSceneNode host when host.Policy.Modal == UiModalPolicy.Modal ||
                                      host.Policy.Kind is UiHostKind.Popup or UiHostKind.Sheet => UiAccessibilityRole.Dialog,
            UiHostSceneNode => UiAccessibilityRole.Window,
            UiCollectionSceneNode => UiAccessibilityRole.List,
            UiButtonSceneNode or UiRouteButtonSceneNode => UiAccessibilityRole.Button,
            UiTextInputSceneNode => UiAccessibilityRole.TextField,
            UiSourceSceneNode source when source.Kind == UiSceneNodeKind.Inspector => UiAccessibilityRole.Inspector,
            UiSourceSceneNode source when source.Kind == UiSceneNodeKind.Form => UiAccessibilityRole.Form,
            UiSourceSceneNode or UiTextSceneNode => UiAccessibilityRole.StaticText,
            UiContainerSceneNode container when container.Kind == UiSceneNodeKind.Form => UiAccessibilityRole.Form,
            UiContainerSceneNode container when container.Kind == UiSceneNodeKind.ActionBar => UiAccessibilityRole.Toolbar,
            _ => UiAccessibilityRole.Group
        };

    private static string? Name(UiScene scene, UiSceneNode node)
        => node switch
        {
            UiHostSceneNode => scene.DisplayName,
            UiContainerSceneNode container => container.SemanticName,
            UiSourceSceneNode source => source.SemanticName,
            UiCollectionSceneNode collection => collection.SemanticName,
            UiTextSceneNode text => text.Text,
            UiButtonSceneNode button => button.Label,
            UiRouteButtonSceneNode route => route.Label,
            UiTextInputSceneNode input => input.SemanticName,
            _ => null
        };

    private static string? Value(UiSceneNode node)
        => node switch
        {
            UiSourceSceneNode source => source.DisplayText,
            UiTextInputSceneNode input => input.CurrentText,
            _ => null
        };

    private static bool Enabled(UiSceneNode node, IUiActionResolver? actions)
        => node is not UiButtonSceneNode button || (actions?.CanInvoke(button.Action) ?? button.Action.CanExecute);
}
