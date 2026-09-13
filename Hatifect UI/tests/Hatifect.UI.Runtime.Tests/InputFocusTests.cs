using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Hosting;
using Hatifect.UI.Runtime.Input;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class InputFocusTests
{
    [Fact]
    public void PointerUsesArrangedClipAndInvokesOnlyOnMatchingRelease()
    {
        int invoked = 0;
        (UiScene scene, UiLayoutSnapshot layout) = Build(
            UiHostKind.Window,
            new UiActionDefinition(RegistryTests.Id("input/action"), "Take", () => invoked++));
        var session = new UiInteractionSession(scene, layout);
        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
        Assert.True(layout.TryGetEntry(button.Id, out UiLayoutEntry? entry));
        Assert.NotNull(entry);
        var inside = new UiPoint(entry.Bounds.X + 1, entry.Bounds.Y + 1);

        Assert.True(session.PressPointer(inside).Consumed);
        UiInteractionUpdate released = session.ReleasePointer(inside);

        Assert.True(released.ActionInvoked);
        Assert.Equal(1, invoked);
        Assert.Equal(button.Id, session.Snapshot.Focused);
        Assert.Null(session.Snapshot.Pressed);
    }

    [Fact]
    public void ControllerNavigationIsDerivedFromGeometryAndTrappedScopeWraps()
    {
        (UiScene scene, UiLayoutSnapshot layout) = Build(
            UiHostKind.Popup,
            new UiActionDefinition(RegistryTests.Id("input/first"), "First", () => { }),
            new UiActionDefinition(RegistryTests.Id("input/second"), "Second", () => { }));
        var session = new UiInteractionSession(scene, layout);
        UiButtonSceneNode[] buttons = Nodes(scene.Root).OfType<UiButtonSceneNode>().ToArray();

        Assert.True(session.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(buttons[0].Id, session.Snapshot.Focused);
        Assert.True(session.MoveFocus(UiNavigationDirection.Right).Consumed);
        Assert.Equal(buttons[1].Id, session.Snapshot.Focused);
        Assert.True(session.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(buttons[0].Id, session.Snapshot.Focused);
    }

    [Fact]
    public void PopupOutsidePressAndEscapeFollowHostDismissPolicy()
    {
        (UiScene scene, UiLayoutSnapshot layout) = Build(
            UiHostKind.Popup,
            new UiActionDefinition(RegistryTests.Id("input/action"), "Take", () => { }),
            popupPolicy: UiHostPolicies.Popup);
        var session = new UiInteractionSession(scene, layout);

        Assert.True(session.PressPointer(new UiPoint(-10, -10)).DismissRequested);
        Assert.True(session.Cancel().DismissRequested);
    }

    [Fact]
    public void DisabledActionIsNeitherFocusableNorInvoked()
    {
        int invoked = 0;
        (UiScene scene, UiLayoutSnapshot layout) = Build(
            UiHostKind.Window,
            new UiActionDefinition(RegistryTests.Id("input/disabled"), "Disabled", () => invoked++, () => false));
        var session = new UiInteractionSession(scene, layout);

        Assert.False(session.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.False(session.Submit().Consumed);
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void InteractionSnapshotFlowsThroughTypedVisualStateResolution()
    {
        UiSymbolId id = RegistryTests.Id("input/visual-state");
        var action = new UiActionDefinition(RegistryTests.Id("input/focus-action"), "Focus", () => { });
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Input")
            .Actions("Actions", action)
            .VisualRole("Action.Primary")
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Input", () => experience)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiCompilationResult compilation = new UiCompiler(catalog).Compile(@"visual Input

Action.Primary@Focused
    surface = Surface.Hover
", experience.CreateBindingContext(), "Input#visual");
        Assert.True(compilation.IsValid, string.Join(Environment.NewLine, compilation.Diagnostics.Select(item => item.Message)));
        UiVisualDefinition visual = Assert.IsType<UiVisualDefinition>(compilation.Definition);
        UiSymbolId buttonId = action.Id.Child("scene/button");

        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry, catalog).Compose(
            invocation,
            visual,
            new UiInteractionSnapshot(Focused: buttonId));
        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
        Assert.True(catalog.TryGetProperty(UiDefinitionKind.Visual, "surface", out UiPropertySymbol? surface));
        Assert.True(button.Visual.TryGet(surface!, out UiResolvedVisualProperty? resolved));

        Assert.Equal(UiVisualResolutionLayer.InteractionState, resolved!.Layer);
        Assert.Equal(UiThemePresets.Dark().Resolve(UiThemeTokens.SurfaceHover), resolved.Value);
    }

    private static (UiScene Scene, UiLayoutSnapshot Layout) Build(
        UiHostKind kind,
        UiActionDefinition first,
        UiActionDefinition? second = null,
        UiHostPolicy? popupPolicy = null)
    {
        UiSymbolId id = RegistryTests.Id($"input/{kind}");
        UiActionDefinition[] actions = second == null ? new[] { first } : new[] { first, second! };
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Input")
            .Actions("Actions", actions)
            .VisualRole("Action.Primary")
            .Build();
        var builder = new UiRegistryBuilder();
        if (kind == UiHostKind.Popup)
            builder.Popup(id, "Input", () => experience, host: popupPolicy ?? UiHostPolicies.Modal);
        else
            builder.Window(id, "Input", () => experience);
        UiRegistrySnapshot registry = builder.Freeze();
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics())
            .Build(scene, new UiRect(0, 0, 420, 180));
        return (scene, layout);
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    private sealed class TestTextMetrics : IUiTextMetrics
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);
    }
}
