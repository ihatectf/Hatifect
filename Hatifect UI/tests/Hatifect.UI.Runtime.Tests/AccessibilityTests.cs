using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Platform;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class AccessibilityTests
{
    private static readonly UiRect Viewport = new(0, 0, 480, 260);

    [Fact]
    public void SnapshotUsesSemanticNamesStateAndArrangedGeometry()
    {
        UiSymbolId id = RegistryTests.Id("accessibility/window");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Accessible storage")
            .Search("Search", new UiState<string>("ore"))
            .Monitor("Status", new UiConstantSource<string>("Ready"))
            .Actions(
                "Actions",
                new UiActionDefinition(id.Child("action/take"), "Take", () => { }),
                new UiActionDefinition(id.Child("action/disabled"), "Disabled", () => { }, () => false))
            .VisualRole("Action.Primary")
            .Build();
        UiScene scene = Scene(experience, UiHostPolicies.Window);
        var host = new UiPortalHostSession(scene, new UiHostPlacementContext(Viewport), new TestPlatform());
        UiSymbolId inputId = Assert.Single(SceneNodes(scene.Root).OfType<UiTextInputSceneNode>()).Id;

        UiPortalDispatch focus = default!;
        for (int step = 0; step < 4 && host.Root.Interactions.Snapshot.Focused != inputId; step++)
            focus = host.MoveFocus(UiNavigationDirection.Next);
        UiAccessibilitySnapshot snapshot = host.Accessibility.Root;
        UiAccessibilityNodeSnapshot[] nodes = Nodes(snapshot.Root).ToArray();
        UiAccessibilityNodeSnapshot input = Assert.Single(nodes, node => node.Role == UiAccessibilityRole.TextField);
        UiAccessibilityNodeSnapshot[] buttons = nodes.Where(node => node.Role == UiAccessibilityRole.Button).ToArray();

        Assert.True(focus.Consumed);
        Assert.Equal(UiAccessibilityRole.Window, snapshot.Root.Role);
        Assert.Equal("Accessible storage", snapshot.Root.Name);
        Assert.Equal("Search", input.Name);
        Assert.Equal("ore", input.Value);
        Assert.True(input.Focused);
        Assert.Contains(buttons, button => button.Name == "Take" && button.Enabled);
        Assert.Contains(buttons, button => button.Name == "Disabled" && !button.Enabled);
        foreach (UiAccessibilityNodeSnapshot node in nodes)
        {
            Assert.True(host.Root.Layout.TryGetEntry(node.Id, out UiLayoutEntry? entry));
            Assert.NotNull(entry);
            Assert.Equal(entry.Bounds, node.Bounds);
            Assert.Equal(entry.Clip, node.Clip);
        }
    }

    [Fact]
    public void ConfigureFormProjectsLabeledEditableFieldsThroughLayoutInputAndAccessibility()
    {
        UiSymbolId id = RegistryTests.Id("accessibility/configure-form");
        var channel = new UiState<string>("Default");
        var mode = new UiState<string>("Bidirectional");
        var form = new UiFormState(
            new UiSemanticFormField(id.Child("field/channel"), "Channel", channel),
            new UiSemanticFormField(id.Child("field/mode"), "Mode", mode));
        int sourceChanges = 0;
        form.Changed += () => sourceChanges++;
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Pipe configuration")
            .Configure("Settings", form)
            .Actions("Actions", new UiActionDefinition(id.Child("action/apply"), "Apply", () => { }))
            .VisualRole("Field.Label")
            .VisualRole("Field.Input")
            .VisualRole("Action.Primary")
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Popup(id, experience.DisplayName, () => experience, UiHostPolicies.Modal)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Compact);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        UiScene Compose(UiInteractionSnapshot? interaction = null)
            => composer.Compose(invocation, interaction: interaction);
        var host = new UiPortalHostSession(
            Compose(),
            new UiHostPlacementContext(Viewport),
            new TestPlatform(),
            composeInteraction: interaction => Compose(interaction));

        UiAccessibilityNodeSnapshot formNode = Assert.Single(
            Nodes(host.Accessibility.Root.Root),
            node => node.Role == UiAccessibilityRole.Form);
        UiAccessibilityNodeSnapshot[] fields = formNode.Children
            .Where(node => node.Role == UiAccessibilityRole.TextField)
            .ToArray();
        Assert.Equal(new[] { "Channel", "Mode" }, fields.Select(field => field.Name));
        Assert.Equal(new[] { "Default", "Bidirectional" }, fields.Select(field => field.Value));
        Assert.All(fields, field =>
        {
            Assert.True(field.Bounds.Width > 0);
            Assert.True(field.Bounds.Height > 0);
        });

        UiSymbolId channelInput = fields[0].Id;
        for (int step = 0; step < 4 && host.Root.Interactions.Snapshot.Focused != channelInput; step++)
            host.MoveFocus(UiNavigationDirection.Next);
        UiPortalDispatch edit = host.ReplaceText("Ore");

        Assert.True(edit.Consumed);
        Assert.True(edit.Interaction?.TextChanged);
        Assert.Equal("Ore", channel.Value);
        Assert.Equal(1, sourceChanges);
        Assert.Equal(
            "Ore",
            Assert.Single(
                Nodes(host.Accessibility.Root.Root),
                node => node.Id == channelInput).Value);
        Assert.True(host.Root.LastUpdate?.LayoutChanged);
        Assert.True(host.Root.LastUpdate?.FrameChanged);
    }

    [Fact]
    public void TextEditingOwnsUnicodeCaretSelectionAndRenderOnlyCaretInvalidation()
    {
        UiSymbolId id = RegistryTests.Id("accessibility/text-editing");
        var query = new UiState<string>("A😀B");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Text editing")
            .Search("Search", query)
            .VisualRole("Field.Input")
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => experience)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        UiScene Compose(UiInteractionSnapshot? interaction = null)
            => composer.Compose(invocation, interaction: interaction);
        var host = new UiPortalHostSession(
            Compose(),
            new UiHostPlacementContext(Viewport),
            new TestPlatform(),
            composeInteraction: interaction => Compose(interaction));
        UiTextInputSceneNode input = Assert.Single(SceneNodes(host.Root.Scene.Root).OfType<UiTextInputSceneNode>());

        Assert.True(host.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(4, host.FocusedTextEditing?.Caret);
        Assert.True(host.EditText(UiTextEditAction.Left).Consumed);
        Assert.Equal(3, host.FocusedTextEditing?.Caret);
        UiPortalDispatch deleted = host.EditText(UiTextEditAction.Backspace);

        Assert.True(deleted.Interaction?.TextChanged);
        Assert.Equal("AB", query.Value);
        Assert.Equal(1, host.FocusedTextEditing?.Caret);
        Assert.True(host.InsertText("😀").Interaction?.TextChanged);
        Assert.Equal("A😀B", query.Value);
        Assert.Equal(3, host.FocusedTextEditing?.Caret);

        Assert.True(host.EditText(UiTextEditAction.Home).Consumed);
        Assert.True(host.EditText(UiTextEditAction.Right, extendSelection: true).Consumed);
        Assert.Equal(0, host.FocusedTextEditing?.SelectionStart);
        Assert.Equal(1, host.FocusedTextEditing?.SelectionLength);
        Assert.True(host.InsertText("X").Interaction?.TextChanged);
        Assert.Equal("X😀B", query.Value);

        Assert.True(host.EditText(UiTextEditAction.SelectAll).Consumed);
        Assert.Contains(
            host.Root.Frame.Primitives.OfType<UiSurfacePrimitive>(),
            primitive => primitive.Node == input.Id && primitive.Surface.Tint.A == 70);
        Assert.True(host.InsertText("Ore").Interaction?.TextChanged);
        Assert.Equal("Ore", query.Value);

        UiPortalDispatch moved = host.EditText(UiTextEditAction.Left);

        Assert.True(moved.Interaction?.TextEditingChanged);
        Assert.False(moved.Interaction?.TextChanged);
        Assert.False(host.Root.LastUpdate?.LayoutChanged);
        Assert.True(host.Root.LastUpdate?.FrameChanged);
        Assert.Contains(
            host.Root.Frame.Primitives.OfType<UiSurfacePrimitive>(),
            primitive => primitive.Node == input.Id && primitive.Bounds.Width == 1);
        Assert.Equal(
            "Ore",
            Assert.Single(
                Nodes(host.Accessibility.Root.Root),
                node => node.Id == input.Id).Value);
    }

    [Fact]
    public void DirectHostTextEditingRefreshesLiveTextAndScrolledPointerGeometry()
    {
        UiSymbolId id = RegistryTests.Id("accessibility/direct-text-editing");
        var query = new UiState<string>("Initial");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Direct text editing")
            .Search("Search", query)
            .VisualRole("Field.Input")
            .Build();
        UiScene scene = Scene(experience, UiHostPolicies.Window);
        var host = new UiPortalHostSession(
            scene,
            new UiHostPlacementContext(new UiRect(0, 0, 240, 180)),
            new TestPlatform());
        UiTextInputSceneNode input = Assert.Single(SceneNodes(scene.Root).OfType<UiTextInputSceneNode>());
        for (int step = 0; step < 4 && host.Root.Interactions.Snapshot.Focused != input.Id; step++)
            Assert.True(host.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(input.Id, host.Root.Interactions.Snapshot.Focused);
        string value = new string('W', 80);

        UiPortalDispatch replaced = host.ReplaceText(value);

        Assert.True(replaced.Interaction?.TextChanged);
        Assert.True(host.Root.LastUpdate?.LayoutChanged);
        Assert.True(host.Root.LastUpdate?.FrameChanged);
        Assert.Equal(value, query.Value);
        Assert.Equal(
            value,
            Assert.Single(
                host.Root.Frame.Primitives.OfType<UiTextPrimitive>(),
                primitive => primitive.Node == input.Id).Text);
        Assert.Equal(
            value,
            Assert.Single(
                Nodes(host.Accessibility.Root.Root),
                node => node.Id == input.Id).Value);
        Assert.True(host.Root.Layout.TryGetEntry(input.Id, out UiLayoutEntry? entry));
        Assert.NotNull(entry);
        UiTextPrimitive rendered = Assert.Single(
            host.Root.Frame.Primitives.OfType<UiTextPrimitive>(),
            primitive => primitive.Node == input.Id);
        Assert.True(rendered.Bounds.X < entry.ContentBounds.X);

        UiPortalDispatch pressed = host.PressPointer(new UiPoint(
            entry.ContentBounds.X + 1,
            entry.ContentBounds.Y + entry.ContentBounds.Height / 2));

        Assert.True(pressed.Consumed);
        Assert.True(host.FocusedTextEditing?.Caret > 0);
        Assert.False(host.Root.LastUpdate?.LayoutChanged);
        Assert.True(host.Root.LastUpdate?.FrameChanged);
    }

    [Fact]
    public void CollectionAccessibilityIsBoundedToTheVirtualizedWindow()
    {
        UiSymbolId id = RegistryTests.Id("accessibility/collection");
        string[] values = Enumerable.Range(0, 1000).Select(index => $"Item {index}").ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Large collection")
            .Browse("Items", new UiCollectionSource<string>(
                values,
                value => id.Child($"item/{value.Replace(' ', '-') }"),
                value => value))
            .Build();
        UiScene scene = Scene(experience, UiHostPolicies.Window);
        var host = new UiPortalHostSession(
            scene,
            new UiHostPlacementContext(new UiRect(0, 0, 320, 140)),
            new TestPlatform());

        UiAccessibilityNodeSnapshot list = Assert.Single(
            Nodes(host.Accessibility.Root.Root),
            node => node.Role == UiAccessibilityRole.List);

        Assert.NotEmpty(list.Children);
        Assert.True(list.Children.Count < values.Length);
        Assert.All(list.Children, item => Assert.Equal(values.Length, item.SetSize));
        Assert.Equal(
            Enumerable.Range(list.Children[0].PositionInSet!.Value, list.Children.Count),
            list.Children.Select(item => item.PositionInSet!.Value));
        Assert.All(list.Children, item => Assert.True(item.Clip.Width >= 0 && item.Clip.Height >= 0));
    }

    [Fact]
    public void PortalSnapshotExposesOwnerAncestryAndRetiresSynchronously()
    {
        UiSymbolId rootId = RegistryTests.Id("accessibility/root");
        UiSymbolId popupId = RegistryTests.Id("accessibility/popup");
        UiExperienceDefinition rootExperience = new UiExperienceBuilder(rootId, "Root")
            .Actions("Actions", new UiActionDefinition(rootId.Child("action/open"), "Open", () => { }))
            .VisualRole("Action.Primary")
            .Build();
        UiExperienceDefinition popupExperience = new UiExperienceBuilder(popupId, "Inspector dialog")
            .Inspect("Inspector", new UiState<string?>("Machine"))
            .Build();
        UiScene root = Scene(rootExperience, UiHostPolicies.Window);
        UiScene popup = Scene(popupExperience, UiHostPolicies.Modal);
        var host = new UiPortalHostSession(root, new UiHostPlacementContext(Viewport), new TestPlatform());
        UiSymbolId owner = Assert.Single(SceneNodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        UiSymbolId portalId = RegistryTests.Id("accessibility/portal");

        using UiPortalHandle handle = host.Present(new UiPortalRequest(
            portalId,
            new UiPortalOwner(owner),
            popup,
            new UiHostPlacementContext(Viewport)));
        UiAccessibilityPortalSnapshot portal = Assert.Single(host.Accessibility.Portals);

        Assert.Equal(portalId, portal.Id);
        Assert.Equal(owner, portal.OwnerNode);
        Assert.Null(portal.OwnerPortal);
        Assert.True(portal.Modal);
        Assert.Equal(UiAccessibilityRole.Dialog, portal.Tree.Root.Role);
        Assert.Equal("Inspector dialog", portal.Tree.Root.Name);

        handle.Dispose();
        Assert.Empty(host.Accessibility.Portals);
    }

    [Fact]
    public void ActiveFocusScopeFollowsTopPortalWithoutDiscardingRestorableLocalFocus()
    {
        UiScene root = Scene(
            new UiExperienceBuilder(RegistryTests.Id("accessibility/focus-root"), "Root")
                .Actions(
                    "Actions",
                    new UiActionDefinition(
                        RegistryTests.Id("accessibility/focus-root/action"),
                        "Root action",
                        () => { }))
                .VisualRole("Action.Primary")
                .Build(),
            UiHostPolicies.Window);
        UiScene popup = Scene(
            new UiExperienceBuilder(RegistryTests.Id("accessibility/focus-popup"), "Popup")
                .Actions(
                    "Actions",
                    new UiActionDefinition(
                        RegistryTests.Id("accessibility/focus-popup/action"),
                        "Popup action",
                        () => { }))
                .VisualRole("Action.Primary")
                .Build(),
            UiHostPolicies.Popup);
        UiScene nested = Scene(
            new UiExperienceBuilder(RegistryTests.Id("accessibility/focus-nested"), "Nested")
                .Actions(
                    "Actions",
                    new UiActionDefinition(
                        RegistryTests.Id("accessibility/focus-nested/action"),
                        "Nested action",
                        () => { }))
                .VisualRole("Action.Primary")
                .Build(),
            UiHostPolicies.Modal);
        var host = new UiPortalHostSession(root, new UiHostPlacementContext(Viewport), new TestPlatform());
        Assert.True(host.MoveFocus(UiNavigationDirection.Next).Consumed);
        UiAccessibilityHostSnapshot rootOnly = host.Accessibility;

        Assert.True(rootOnly.RootFocusScopeActive);
        Assert.Null(rootOnly.ActiveFocusPortal);
        Assert.Single(Nodes(rootOnly.Root.Root), node => node.Focused);

        UiSymbolId popupId = RegistryTests.Id("accessibility/focus-portal");
        UiSymbolId rootOwner = Assert.Single(SceneNodes(root.Root).OfType<UiButtonSceneNode>()).Id;
        using UiPortalHandle popupHandle = host.Present(new UiPortalRequest(
            popupId,
            new UiPortalOwner(rootOwner),
            popup,
            new UiHostPlacementContext(Viewport)));
        UiAccessibilityHostSnapshot popupActive = host.Accessibility;

        Assert.False(popupActive.RootFocusScopeActive);
        Assert.Equal(popupId, popupActive.ActiveFocusPortal);
        Assert.Single(Nodes(popupActive.Root.Root), node => node.Focused);
        Assert.Single(Nodes(Assert.Single(popupActive.Portals).Tree.Root), node => node.Focused);

        UiSymbolId nestedId = RegistryTests.Id("accessibility/focus-nested-portal");
        UiSymbolId popupOwner = Assert.Single(SceneNodes(popup.Root).OfType<UiButtonSceneNode>()).Id;
        using UiPortalHandle nestedHandle = host.Present(new UiPortalRequest(
            nestedId,
            new UiPortalOwner(popupOwner, popupId),
            nested,
            new UiHostPlacementContext(Viewport)));

        Assert.Equal(nestedId, host.Accessibility.ActiveFocusPortal);

        nestedHandle.Dispose();
        Assert.Equal(popupId, host.Accessibility.ActiveFocusPortal);

        popupHandle.Dispose();
        UiAccessibilityHostSnapshot restored = host.Accessibility;
        Assert.True(restored.RootFocusScopeActive);
        Assert.Null(restored.ActiveFocusPortal);
        Assert.Single(Nodes(restored.Root.Root), node => node.Focused);
    }

    private static UiScene Scene(UiExperienceDefinition experience, UiHostPolicy policy)
    {
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(experience.Id, experience.DisplayName, () => experience, policy)
            .Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(experience.Id, UiPresentationProfiles.Wide));
    }

    private static IEnumerable<UiAccessibilityNodeSnapshot> Nodes(UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (UiAccessibilityNodeSnapshot child in node.Children)
        foreach (UiAccessibilityNodeSnapshot descendant in Nodes(child))
            yield return descendant;
    }

    private static IEnumerable<UiSceneNode> SceneNodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in SceneNodes(child))
            yield return descendant;
    }

    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);

        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
