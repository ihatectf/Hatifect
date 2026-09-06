using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class SceneCompositionTests
{
    [Fact]
    public void FoundationComponentCatalogHasAFixedClosedSize()
    {
        Assert.Equal(12, UiSceneComposer.FoundationComponentCount);

        _ = new UiSceneComposer(UiThemePresets.Dark());
        _ = new UiSceneComposer(UiThemePresets.Dark());

        Assert.Equal(12, UiSceneComposer.FoundationComponentCount);
    }

    [Fact]
    public void PlanMaterializesTextInputInspectorAndWorkingButtonWithoutCss()
    {
        UiSymbolId id = RegistryTests.Id("storage-window");
        var search = new UiState<string>(string.Empty);
        int invoked = 0;
        var action = new UiActionDefinition(RegistryTests.Id("action/take"), "Take", () => invoked++);
        UiExperienceDefinition experience = Experience(id, search, action);
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Storage", () => experience)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);

        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);
        UiTextInputSceneNode input = Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>());
        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());

        Assert.True(input.TrySetText("ore"));
        Assert.Equal("ore", search.Value);
        Assert.Equal("Take", button.Label);
        Assert.True(button.Invoke());
        Assert.Equal(1, invoked);
        Assert.Contains(Nodes(scene.Root), node => node.Kind == UiSceneNodeKind.Collection);
        Assert.Contains(Nodes(scene.Root), node => node.Kind == UiSceneNodeKind.Inspector);
    }

    [Fact]
    public void PublishedForeignWindowActionBecomesButtonInOwnerSlot()
    {
        UiSymbolId id = RegistryTests.Id("storage-window");
        UiSymbolId point = RegistryTests.Id("storage-window/actions");
        UiSymbolId contributionId = RegistryTests.Id("contribution/sort");
        var contributionAction = new UiActionDefinition(RegistryTests.Id("action/sort"), "Sort", () => { });
        UiExperienceDefinition experience = Experience(
            id,
            new UiState<string>(string.Empty),
            new UiActionDefinition(RegistryTests.Id("action/take"), "Take", () => { }));
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Storage", () => experience)
            .Publish(new UiContributionPointDescriptor(
                point,
                id,
                new UiSymbolId("Hatifect.UI", "region/Actions"),
                UiContributionKind.Action))
            .Contribute(new UiActionContributionDescriptor(contributionId, point, contributionAction))
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);

        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation);

        Assert.Contains(
            Nodes(scene.Root).OfType<UiButtonSceneNode>(),
            button => button.Action.Id == contributionAction.Id && button.Label == "Sort");
    }

    [Fact]
    public void EveryComponentLowersThroughOneRenderFrameAndCompositor()
    {
        UiSymbolId id = RegistryTests.Id("storage-window");
        UiExperienceDefinition experience = Experience(
            id,
            new UiState<string>("wood"),
            new UiActionDefinition(RegistryTests.Id("action/take"), "Take", () => { }));
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Storage", () => experience)
            .Freeze();
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
        var bounds = Nodes(scene.Root).ToDictionary(
            node => node.Id,
            node =>
            {
                var rect = new UiRect(0, 0, 120, 32);
                UiTextOverflow overflow = node.Kind is UiSceneNodeKind.Button or UiSceneNodeKind.RouteButton
                    ? UiTextOverflow.Ellipsis
                    : UiTextOverflow.Clip;
                return new UiLayoutEntry(rect, rect, rect, new UiSize(120, 32), overflow);
            });
        var placement = new UiHostPlacementResult(
            bounds[scene.Root.Id].Bounds,
            UiHostPlacementKind.Fill,
            UsedFallback: false,
            WasClamped: false);
        UiRenderFrame frame = new UiSceneRenderPlanner().Build(scene, new UiLayoutSnapshot(bounds, placement));
        var backend = new RecordingBackend();

        new UiCompositor().Render(frame, backend);

        Assert.Contains(frame.Primitives, primitive => primitive is UiSurfacePrimitive);
        Assert.Contains(frame.Primitives, primitive => primitive is UiTextPrimitive text && text.Text == "Take");
        Assert.Equal(frame.Primitives.Count, backend.Drawn.Count);
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("Состояние")]
    public void TypedGraphUsesAliasForRoleAndLabelForPresentationWithoutRenderingAuxiliarySources(string label)
    {
        UiSymbolId owner = RegistryTests.Id("localized-window");
        UiSymbolId role = owner.Child("visual/status");
        var auxiliary = new UiState<bool>(true);
        var experience = new UiExperienceBuilder(owner, "View")
            .Element(owner.Child("stable/status"), "Status", label, new UiState<string>("Ready"), UiSourceTypes.String, UiCapabilities.Monitor)
            .Source(owner.Child("source/internal"), "Internal", "Internal", auxiliary, UiSourceTypes.Boolean, UiCapabilities.Filter)
            .VisualRole(role, "Status")
            .Build();
        var registry = new UiRegistryBuilder().Window(owner, "View", () => experience).Freeze();
        var scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(owner, UiPresentationProfiles.Wide));

        UiSourceSceneNode visible = Assert.Single(Nodes(scene.Root).OfType<UiSourceSceneNode>());
        Assert.Equal(role, visible.Role);
        Assert.Equal(label, visible.SemanticName);
        Assert.Equal("Ready", visible.DisplayText);
        Assert.DoesNotContain(Nodes(scene.Root).OfType<UiSourceSceneNode>(), node => ReferenceEquals(node.Source, auxiliary));
    }

    private static UiExperienceDefinition Experience(
        UiSymbolId id,
        UiState<string> search,
        UiActionDefinition action)
        => new UiExperienceBuilder(id, "Storage")
            .Browse("Items", new UiCollectionSource<string>(
                new[] { "Wood", "Stone" }, item => id.Child($"item/{item}")))
            .Search("Search", search)
            .Inspect("Inspector", new UiState<string?>("Wood"))
            .Actions("Actions", action)
            .VisualRole("Action.Primary")
            .Build();

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    private sealed class RecordingBackend : IUiRenderBackend
    {
        public List<UiRenderPrimitive> Drawn { get; } = new();
        public void DrawSurface(UiSurfacePrimitive surface) => Drawn.Add(surface);
        public void DrawText(UiTextPrimitive text) => Drawn.Add(text);
    }
}
