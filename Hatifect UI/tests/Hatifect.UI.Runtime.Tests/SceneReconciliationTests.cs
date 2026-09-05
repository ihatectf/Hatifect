using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
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

public sealed class SceneReconciliationTests
{
    [Fact]
    public void TextStateChangeRequiresLayoutBecauseSceneSnapshotsContent()
    {
        var text = new UiState<string>("ore");
        SceneFixture fixture = Fixture(text);
        UiScene before = fixture.Compose();

        text.Value = "iridium ore";
        UiScene after = fixture.Compose();
        UiSceneDiff diff = new UiSceneReconciler().Compare(before, after);

        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Measure));
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Arrange));
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Render));
        Assert.False(diff.RequiresRecompose);
    }

    [Fact]
    public void RenderOnlyVisualStateReusesLayoutButRebuildsFrame()
    {
        var text = new UiState<string>("ore");
        SceneFixture fixture = Fixture(text);
        UiScene before = fixture.Compose();
        var platform = new RecordingPlatform();
        var viewport = new UiRect(0, 0, 420, 180);
        var runtime = new UiHostRuntimeSession(before, viewport, platform);
        UiButtonSceneNode button = Assert.Single(Nodes(before.Root).OfType<UiButtonSceneNode>());

        UiScene after = fixture.Compose(new UiInteractionSnapshot(Focused: button.Id));
        UiHostUpdate update = runtime.Update(after, viewport);
        runtime.Render();

        Assert.False(update.LayoutChanged);
        Assert.True(update.FrameChanged);
        Assert.Equal(UiPropertyEffects.Render, update.Diff.Effects);
        Assert.Equal(runtime.Frame.Primitives.Count, platform.DrawCount);
    }

    [Fact]
    public void StructuralChangeRequestsFullRecomposition()
    {
        SceneFixture one = Fixture(new UiState<string>("ore"), actionCount: 1);
        SceneFixture two = Fixture(new UiState<string>("ore"), actionCount: 2);

        UiSceneDiff diff = new UiSceneReconciler().Compare(one.Compose(), two.Compose());

        Assert.True(diff.RequiresRecompose);
        Assert.True(diff.RequiresLayout);
        Assert.True(diff.RequiresRender);
    }

    [Fact]
    public void InteractionComposerRefreshesTypedVisualsWithoutRelayout()
    {
        SceneFixture fixture = Fixture(new UiState<string>("ore"));
        UiScene scene = fixture.Compose();
        var runtime = new UiHostRuntimeSession(
            scene,
            new UiRect(0, 0, 420, 180),
            new RecordingPlatform(),
            composeInteraction: fixture.Compose);

        UiInteractionUpdate focus = runtime.Interactions.MoveFocus(UiNavigationDirection.Next);
        UiHostUpdate? update = runtime.RefreshInteractionVisuals();

        Assert.True(focus.StateChanged);
        UiHostUpdate applied = Assert.IsType<UiHostUpdate>(update);
        Assert.False(applied.LayoutChanged);
        Assert.True(applied.FrameChanged);
        Assert.Equal(UiPropertyEffects.Render, applied.Diff.Effects);
    }

    private static SceneFixture Fixture(UiState<string> text, int actionCount = 1)
    {
        UiSymbolId id = RegistryTests.Id("reconcile/window");
        UiActionDefinition[] actions = Enumerable.Range(0, actionCount)
            .Select(index => new UiActionDefinition(RegistryTests.Id($"reconcile/action/{index}"), $"Action {index}", () => { }))
            .ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Reconcile")
            .Search("Search", text)
            .Actions("Actions", actions)
            .VisualRole("Action.Primary")
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Reconcile", () => experience)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiCompilationResult compilation = new UiCompiler(catalog).Compile(@"visual Reconcile

Action.Primary@Focused
    surface = Surface.Hover
", experience.CreateBindingContext(), "Reconcile#visual");
        Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics.Select(item => item.Message)));
        return new SceneFixture(
            new UiSceneComposer(UiThemePresets.Dark(), registry, catalog),
            invocation,
            Assert.IsType<UiVisualDefinition>(compilation.Definition));
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    private sealed record SceneFixture(
        UiSceneComposer Composer,
        UiInvocationResult Invocation,
        UiVisualDefinition Visual)
    {
        public UiScene Compose(UiInteractionSnapshot? interaction = null)
            => Composer.Compose(Invocation, Visual, interaction);
    }

    private sealed class RecordingPlatform : IUiPlatformBridge
    {
        public int DrawCount { get; private set; }

        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);

        public void DrawSurface(UiSurfacePrimitive surface) => DrawCount++;
        public void DrawText(UiTextPrimitive text) => DrawCount++;
    }
}
