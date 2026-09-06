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
    [Theory]
    [InlineData("update")]
    [InlineData("refresh")]
    [InlineData("focus")]
    [InlineData("text")]
    [InlineData("submit")]
    [InlineData("reconcile")]
    public void CandidateCallbacksSeeAcceptedStateAndCannotReenterMutation(string mutation)
    {
        Action? observe = null;
        int executions = 0;
        var text = new UiState<string>("Accepted draft");
        SceneFixture fixture = Fixture(text, canExecute: () => { observe?.Invoke(); return true; },
            execute: () => executions++);
        var viewport = new UiRect(0, 0, 420, 180);
        UiScene initial = fixture.Compose();
        UiSymbolId focused = mutation == "submit"
            ? Assert.Single(Nodes(initial.Root).OfType<UiButtonSceneNode>()).Id
            : Assert.Single(Nodes(initial.Root).OfType<UiTextInputSceneNode>()).Id;
        var runtime = new UiHostRuntimeSession(initial, viewport, new RecordingPlatform(),
            new UiInteractionSnapshot(Focused: focused));
        Assert.Equal(focused, runtime.Interactions.Snapshot.Focused);
        runtime.RefreshTextEditingVisuals();
        UiScene accepted = runtime.Scene;
        UiLayoutSnapshot layout = runtime.Layout;
        UiRenderFrame frame = runtime.Frame;
        var accessibility = runtime.Accessibility;
        UiInteractionSnapshot interaction = runtime.Interactions.Snapshot;
        text.Value = "Candidate draft";
        UiScene candidate = fixture.Compose(interaction);
        int observations = 0;
        observe = () =>
        {
            observations++;
            Assert.Same(accepted, runtime.Scene);
            Assert.Same(layout, runtime.Layout);
            Assert.Same(frame, runtime.Frame);
            Assert.Same(accessibility, runtime.Accessibility);
            Assert.Same(interaction, runtime.Interactions.Snapshot);
            var failure = Assert.Throws<InvalidOperationException>(() =>
            {
                switch (mutation)
                {
                    case "update": runtime.Update(accepted, viewport); break;
                    case "refresh": runtime.RefreshInteractionVisuals(); break;
                    case "focus": runtime.Interactions.MoveFocus(UiNavigationDirection.Next); break;
                    case "text": runtime.Interactions.ReplaceText("Reentrant draft"); break;
                    case "submit": runtime.Interactions.Submit(); break;
                    case "reconcile": runtime.Interactions.Reconcile(accepted, layout); break;
                    default: throw new Xunit.Sdk.XunitException("Unknown mutation case.");
                }
            });
            Assert.Equal("The host cannot be mutated while a scene update is being prepared.", failure.Message);
        };

        runtime.Update(candidate, viewport);

        Assert.True(observations > 0);
        Assert.Same(candidate, runtime.Scene);
        Assert.Equal("Candidate draft", text.Value);
        Assert.Equal(0, executions);
        Assert.Equal(interaction.Focused, runtime.Interactions.Snapshot.Focused);
        observe = null;
        if (mutation == "submit")
        {
            Assert.True(runtime.Interactions.Submit().ActionInvoked);
            Assert.Equal(1, executions);
        }
        else
        {
            Assert.True(runtime.Interactions.ReplaceText("Accepted later edit").Consumed);
            Assert.Equal("Accepted later edit", text.Value);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedCandidateRetainsTheAcceptedSceneAndInteraction(int failOnRead)
    {
        bool armed = false;
        int reads = 0;
        var error = new InvalidOperationException("Candidate availability failed.");
        var text = new UiState<string>("Accepted draft");
        SceneFixture fixture = Fixture(text, canExecute: () =>
        {
            if (armed && ++reads == failOnRead) throw error;
            return true;
        });
        var viewport = new UiRect(0, 0, 420, 180);
        var runtime = new UiHostRuntimeSession(fixture.Compose(), viewport, new RecordingPlatform());
        Assert.True(runtime.Interactions.MoveFocus(UiNavigationDirection.Next).Consumed);
        runtime.RefreshTextEditingVisuals();
        UiScene scene = runtime.Scene;
        UiLayoutSnapshot layout = runtime.Layout;
        UiRenderFrame frame = runtime.Frame;
        var accessibility = runtime.Accessibility;
        UiInteractionSession interactions = runtime.Interactions;
        UiInteractionSnapshot snapshot = interactions.Snapshot;
        UiHostUpdate? update = runtime.LastUpdate;
        text.Value = "Unaccepted candidate draft";
        UiScene candidate = fixture.Compose(snapshot);
        armed = true;

        var failure = Assert.Throws<InvalidOperationException>(() => runtime.Update(candidate, viewport));

        Assert.Same(error, failure);
        Assert.Equal(failOnRead, reads);
        Assert.Same(scene, runtime.Scene);
        Assert.Same(layout, runtime.Layout);
        Assert.Same(frame, runtime.Frame);
        Assert.Same(accessibility, runtime.Accessibility);
        Assert.Same(interactions, runtime.Interactions);
        Assert.Same(snapshot, runtime.Interactions.Snapshot);
        Assert.Same(update, runtime.LastUpdate);
        armed = false;
        Assert.True(runtime.Update(candidate, viewport).LayoutChanged);
        Assert.Same(candidate, runtime.Scene);
        Assert.Equal(snapshot.Focused, runtime.Interactions.Snapshot.Focused);
        Assert.Contains(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), primitive => primitive.Text == text.Value);
    }

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

    private static SceneFixture Fixture(UiState<string> text, int actionCount = 1, Func<bool>? canExecute = null, Action? execute = null)
    {
        UiSymbolId id = RegistryTests.Id("reconcile/window");
        UiActionDefinition[] actions = Enumerable.Range(0, actionCount)
            .Select(index => new UiActionDefinition(RegistryTests.Id($"reconcile/action/{index}"), $"Action {index}", execute ?? (() => { }), canExecute))
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
