using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
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
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class PublicationSceneTests
{
    [Fact]
    public void CompositionUsesOnePublicationViewEvenWhenFormattingPublishesNewValues()
    {
        UiSymbolId owner = RegistryTests.Id("publication/scene");
        using var publication = new UiPublication(owner);
        var query = publication.State(owner.Child("query"), "old query", UiSourceTypes.String);
        var rows = publication.SelectableCollection(owner.Child("rows"), new[] { "old" }, UiSourceTypes.String,
            text => owner.Child("item/" + text), selectedItemId: owner.Child("item/old"));
        var trigger = new FormattingValue(() => publication.BeginUpdate().Set(query, "new query")
            .Replace(rows, new[] { "new" }).Select(rows, owner.Child("item/new")).Commit());
        var experience = new UiExperienceBuilder(owner, "Publication")
            .Monitor("Trigger", new UiConstantSource<FormattingValue>(trigger))
            .Search("Query", query)
            .Element(owner.Child("element/items"), "Items", "Items", rows,
                UiSourceTypes.Collection(UiSourceTypes.String), UiCapabilities.Browse, UiCapabilities.Select)
            .Monitor("Selection", new UiSelectionSource(rows))
            .Build();
        var registry = new UiRegistryBuilder().Window(owner, "Publication", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).Invoke(owner, UiPresentationProfiles.Wide);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        trigger.Armed = true;
        var scene = composer.Compose(invocation);
        var input = Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>());
        var collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());
        var selection = Assert.Single(Nodes(scene.Root).OfType<UiSourceSceneNode>(), node => node.SemanticName == "Selection");
        Assert.Equal("new query", query.Value);
        Assert.Equal("old query", input.CurrentText);
        Assert.Equal("old", collection.ItemAt(0).Value);
        Assert.Equal(owner.Child("item/old"), collection.SelectedItemId);
        Assert.Equal(owner.Child("item/old").ToString(), selection.DisplayText);
        Assert.Same(rows, collection.SourceIdentity);
        var next = composer.Compose(invocation);
        Assert.Equal("new query", Assert.Single(Nodes(next.Root).OfType<UiTextInputSceneNode>()).CurrentText);
        Assert.Equal("new", Assert.Single(Nodes(next.Root).OfType<UiCollectionSceneNode>()).ItemAt(0).Value);
    }

    [Fact]
    public void NestedPublishedFormInputsAndValidationKeepTheirCapturedValues()
    {
        UiSymbolId owner = RegistryTests.Id("publication/form");
        using var publication = new UiPublication(owner);
        var input = publication.State(owner.Child("input"), "old field", UiSourceTypes.String);
        var validation = publication.State<string?>(owner.Child("validation"), "old error",
            UiSourceTypes.Scalar<string?>(UiDataTypes.StringId, true));
        using var form = new UiFormState(new UiSemanticFormField(owner.Child("field/name"), "Name", input, validation));
        var experience = new UiExperienceBuilder(owner, "Form").Configure("Settings", form).Build();
        var registry = new UiRegistryBuilder().Window(owner, "Form", () => experience).Freeze();
        var invocation = new UiInvocationService(registry).Invoke(owner, UiPresentationProfiles.Wide);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        var old = composer.Compose(invocation);
        publication.BeginUpdate().Set(input, "new field").Set(validation, "new error").Commit();
        Assert.Equal("old field", Assert.Single(Nodes(old.Root).OfType<UiTextInputSceneNode>()).CurrentText);
        Assert.Contains(Nodes(old.Root).OfType<UiTextSceneNode>(), node => node.Text == "old error");
        var next = composer.Compose(invocation);
        Assert.Equal("new field", Assert.Single(Nodes(next.Root).OfType<UiTextInputSceneNode>()).CurrentText);
        Assert.Contains(Nodes(next.Root).OfType<UiTextSceneNode>(), node => node.Text == "new error");
    }

    [Fact]
    public void TextEditingRecomposesThePublishedSceneWithoutRequiringAnExternalComposer()
    {
        UiSymbolId owner = RegistryTests.Id("publication/editing");
        using var publication = new UiPublication(owner);
        var query = publication.State(owner.Child("query"), "A", UiSourceTypes.String);
        var experience = new UiExperienceBuilder(owner, "Editing").Search("Query", query).Build();
        var registry = new UiRegistryBuilder().Window(owner, "Editing", () => experience).Freeze();
        var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(owner, UiPresentationProfiles.Wide));
        var oldInput = Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>());
        var host = new UiPortalHostSession(scene, new UiHostPlacementContext(new UiRect(0, 0, 960, 540)), new TestPlatform());
        Assert.True(host.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.True(host.InsertText("B").Interaction?.TextChanged);
        Assert.Equal("AB", query.Value);
        Assert.Equal("A", oldInput.CurrentText);
        Assert.Equal("AB", Assert.Single(Nodes(host.Root.Scene.Root).OfType<UiTextInputSceneNode>()).CurrentText);
        Assert.True(host.InsertText("C").Interaction?.TextChanged);
        Assert.Equal("ABC", query.Value);
        Assert.Equal(3, host.FocusedTextEditing?.Caret);
        Assert.Equal("ABC", Assert.Single(Nodes(host.Root.Scene.Root).OfType<UiTextInputSceneNode>()).CurrentText);
    }

    private sealed class FormattingValue
    {
        private readonly Action _onFormat;
        internal FormattingValue(Action onFormat) => _onFormat = onFormat;
        internal bool Armed { get; set; }
        public override string ToString()
        {
            if (Armed) { Armed = false; _onFormat(); }
            return "trigger";
        }
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child)) yield return descendant;
    }

    private sealed class TestPlatform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * .6f, availableWidth), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
