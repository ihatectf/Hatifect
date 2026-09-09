using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Diagnostics;
using Hatifect.UI.Runtime.Hosting;
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

public sealed class NativeInteractionProjectionTests
{
    [Fact]
    public void FieldIdentityDoesNotConfuseLabelInputAndValidation()
    {
        var id = RegistryTests.Id("native-projection/form");
        var value = new UiState<string>("Farm");
        var validation = new UiState<string?>("Already exists");
        using var form = new UiFormState(new UiSemanticFormField(id.Child("name"), "Имя", value, validation));
        var experience = new UiExperienceBuilder(id, "Native form")
            .Configure("Details", form)
            .Actions("Actions", new UiActionDefinition(id.Child("save"), "Save", () => throw new Exception("must not execute"), () => false))
            .Build();
        var host = Host(experience);
        var snapshot = UiNativeInteractionProjection.Capture(host.Root.Scene, host.Root.Layout, host.Root.Accessibility);
        var field = snapshot.Elements.Where(n => n.SemanticId == id.Child("name").ToString()).ToArray();
        Assert.Equal(3, field.Length);
        var input = Assert.Single(field, n => n.Role == "TextField");
        Assert.Equal("Farm", input.Value);
        Assert.NotNull(input.ParentNodeId);
        Assert.Equal(2, field.Count(n => n.Role == "StaticText"));
        Assert.False(Assert.Single(snapshot.Elements, n => n.ActionId == id.Child("save").ToString()).Enabled);
        Assert.Equal("Farm", value.Value);
        Assert.Equal(field.Length, field.Select(n => n.NodeId).Distinct().Count());
    }

    [Fact]
    public void VirtualRowsRetainStableItemAndCollectionOwnershipWithoutReadingAllItems()
    {
        var id = RegistryTests.Id("native-projection/list");
        var items = new UiCollectionSource<int>(Enumerable.Range(0, 10000).ToArray(), item => id.Child("item/" + item), item => "Item " + item);
        var experience = new UiExperienceBuilder(id, "Native list").Browse("Items", items).Build();
        var host = Host(experience);
        var snapshot = UiNativeInteractionProjection.Capture(host.Root.Scene, host.Root.Layout, host.Root.Accessibility);
        var collection = Assert.Single(snapshot.Collections);
        Assert.Equal(10000, collection.TotalCount);
        Assert.True(collection.MaximumOffset > 0);
        var rows = snapshot.Elements.Where(n => n.Role == "ListItem").ToArray();
        Assert.InRange(rows.Length, 1, 100);
        Assert.All(rows, row =>
        {
            Assert.Equal(row.NodeId, row.ItemId);
            Assert.Equal(collection.SemanticId, row.CollectionId);
            Assert.Equal(collection.NodeId, row.ParentNodeId);
            Assert.Equal(10000, row.SetSize);
            Assert.NotNull(row.PositionInSet);
            Assert.False(row.Enabled); // Browse-only rows are not selectable controls.
        });
    }

    [Fact]
    public void ProjectionKeepsCurrentGeometryAndDoesNotChangeFocus()
    {
        var id = RegistryTests.Id("native-projection/geometry");
        var experience = new UiExperienceBuilder(id, "Geometry").Search("Search", new UiState<string>("ore")).Build();
        var host = Host(experience);
        var before = host.Root.Interactions.Snapshot.Focused;
        var snapshot = UiNativeInteractionProjection.Capture(host.Root.Scene, host.Root.Layout, host.Root.Accessibility);
        Assert.Equal(before, host.Root.Interactions.Snapshot.Focused);
        foreach (var element in snapshot.Elements)
        {
            var original = All(host.Root.Accessibility.Root).Single(n => n.Id.ToString() == element.NodeId);
            Assert.Equal(original.Bounds, element.Bounds);
            Assert.Equal(original.Clip, element.Clip);
            Assert.Equal(original.Focused, element.Focused);
            Assert.Equal(original.Selected, element.Selected);
        }
    }

    private static UiPortalHostSession Host(UiExperienceDefinition experience)
    {
        var registry = new UiRegistryBuilder().Window(experience.Id, experience.DisplayName, () => experience, UiHostPolicies.Window).Freeze();
        var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(experience.Id, UiPresentationProfiles.Wide));
        return new(scene, new UiHostPlacementContext(new UiRect(0, 0, 640, 300)), new Platform());
    }

    private static IEnumerable<Hatifect.UI.Runtime.Accessibility.UiAccessibilityNodeSnapshot> All(Hatifect.UI.Runtime.Accessibility.UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (var child in node.Children)
        foreach (var item in All(child)) yield return item;
    }

    private sealed class Platform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
