using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Actions;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Hosting;
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

public sealed class InputPromptTests
{
    [Theory]
    [InlineData(UiInputMode.MouseKeyboard, null)]
    [InlineData(UiInputMode.Keyboard, "Enter")]
    [InlineData(UiInputMode.Controller, "A")]
    public void AcceptedInputModeProjectsOneFrameworkPromptAcrossButtonsAndRoutes(
        UiInputMode mode,
        string? expected)
    {
        UiSymbolId id = Id($"components/{mode}");
        using var form = new UiFormState(UiFormFields.Toggle(id.Child("enabled"), "Enabled", true));
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Prompts")
            .Configure("Settings", form)
            .Actions("Actions", new UiActionDefinition(id.Child("apply"), "Apply", () => { }))
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(id, experience.DisplayName, () => experience)
            .Freeze();
        UiTheme theme = UiThemePresets.Dark();
        UiEnvironment environment = Environment(mode, theme.Id);
        UiScene scene = new UiSceneComposer(theme, registry).Compose(
            new UiInvocationService(registry).InvokeInEnvironment(id, environment));
        UiSceneNode[] interactive = Nodes(scene.Root)
            .Where(node => node is UiButtonSceneNode or UiRouteButtonSceneNode)
            .ToArray();

        Assert.Equal(4, interactive.Length); // Terminal route, two toggle options, and Apply.
        Assert.All(interactive, node => Assert.Equal(expected, UiSceneLayoutEngine.InputPrompt(node)?.Label));
        var host = new UiPortalHostSession(
            scene,
            new UiHostPlacementContext(new UiRect(0, 0, 1000, 700)),
            new Platform());
        UiTextPrimitive[] prompts = host.Root.Frame.Primitives
            .OfType<UiTextPrimitive>()
            .Where(text => interactive.Any(node => node.Id == text.Node) && text.Text == expected)
            .ToArray();
        UiAccessibilityNodeSnapshot[] buttons = Accessibility(host.Accessibility.Root.Root)
            .Where(node => node.Role == UiAccessibilityRole.Button)
            .ToArray();

        Assert.Equal(expected is null ? 0 : interactive.Length, prompts.Length);
        Assert.Equal(interactive.Length, buttons.Length);
        Assert.All(buttons, button => Assert.Equal(expected, button.Shortcut));
        if (expected is not null)
        {
            Assert.All(prompts, prompt =>
            {
                Assert.Equal(theme.Resolve(UiThemeTokens.TextInputPrompt), prompt.Foreground);
                Assert.Equal(theme.Resolve(UiThemeTokens.TypographyInputPrompt), prompt.Typography);
                Assert.True(prompt.Bounds.Width > 0 && prompt.Bounds.Height > 0);
            });
        }
    }

    [Fact]
    public void PromptChangeWithinWideProfileInvalidatesLayoutWithoutReactivatingConsumer()
    {
        UiSymbolId id = Id("reconcile");
        int created = 0;
        var action = new UiActionDefinition(id.Child("run"), "Run", () => { });
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Prompts")
            .Actions("Actions", action)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => { created++; return experience; })
            .Freeze();
        UiTheme theme = UiThemePresets.Dark();
        var invoker = new UiInvocationService(registry);
        UiInvocationResult keyboard = invoker.InvokeInEnvironment(id, Environment(UiInputMode.Keyboard, theme.Id));
        UiInvocationResult mouse = invoker.ReplanKnownAvailable(
            keyboard.Descriptor,
            keyboard.Experience,
            UiPresentationProfiles.Wide,
            environment: Environment(UiInputMode.MouseKeyboard, theme.Id));
        var composer = new UiSceneComposer(theme, registry);
        UiScene before = composer.Compose(keyboard);
        UiScene after = composer.Compose(mouse);

        UiSceneDiff diff = new UiSceneReconciler().Compare(before, after);

        Assert.Equal(1, created);
        Assert.Equal(UiPresentationProfiles.Wide.Id, keyboard.Plan.Host.Profile);
        Assert.Equal(UiPresentationProfiles.Wide.Id, mouse.Plan.Host.Profile);
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Measure));
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Arrange));
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Render));
        Assert.False(diff.RequiresRecompose);
        Assert.Equal(
            Assert.Single(Nodes(after.Root).OfType<UiButtonSceneNode>()).Id,
            Assert.Single(diff.ChangedNodes));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "A")]
    public void EnvironmentFreeInvocationUsesItsPresentationProfileFallback(
        bool controller,
        string? expected)
    {
        UiSymbolId id = Id($"legacy/{controller}");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Legacy prompt")
            .Actions("Actions", new UiActionDefinition(id.Child("run"), "Run", () => { }))
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => experience)
            .Freeze();
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(
                id,
                controller ? UiPresentationProfiles.Controller : UiPresentationProfiles.Wide));

        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
        Assert.Equal(expected, button.InputPrompt?.Label);
    }

    [Fact]
    public void EverySemanticThemeRendersTheKeyboardPromptFromItsOwnTokens()
    {
        foreach (UiSemanticTheme semanticTheme in Enum.GetValues<UiSemanticTheme>())
        {
            UiSymbolId id = Id($"theme/{semanticTheme}");
            UiExperienceDefinition experience = new UiExperienceBuilder(id, "Prompt theme")
                .Actions("Actions", new UiActionDefinition(id.Child("run"), "Run", () => { }))
                .Build();
            UiRegistrySnapshot registry = new UiRegistryBuilder()
                .Window(id, experience.DisplayName, () => experience)
                .Freeze();
            UiTheme theme = UiSemanticThemes.Resolve(semanticTheme);
            UiScene scene = new UiSceneComposer(theme, registry).Compose(
                new UiInvocationService(registry).InvokeInEnvironment(
                    id,
                    Environment(UiInputMode.Keyboard, theme.Id)));
            var host = new UiPortalHostSession(
                scene,
                new UiHostPlacementContext(new UiRect(0, 0, 1000, 700)),
                new Platform());

            UiTextPrimitive prompt = Assert.Single(
                host.Root.Frame.Primitives.OfType<UiTextPrimitive>(),
                text => text.Text == "Enter");
            Assert.Equal(theme.Resolve(UiThemeTokens.TextInputPrompt), prompt.Foreground);
            Assert.Equal(theme.Resolve(UiThemeTokens.TypographyInputPrompt), prompt.Typography);
        }
    }

    [Fact]
    public void BoundActionUsesMeasuredPromptGeometryWithoutRenderTimeMetrics()
    {
        UiSymbolId id = Id("bound-action-height");
        var promptTypography = new UiTypography("Body", 28, 1.5f, UiFontWeight.Bold);
        UiTheme theme = new UiThemeBuilder(UiThemePresets.Dark())
            .Set(UiThemeTokens.TypographyInputPrompt, promptTypography)
            .Build(id.Child("theme"));
        UiActionDefinition action = new UiAction<int, int>(
            id.Child("apply"),
            "Apply",
            (request, _) => new(UiActionResult<int>.Success(request)),
            UiActionConcurrency.RejectWhileRunning)
            .Bind(() => 1);
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Prompt height")
            .Actions("Actions", action)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => experience)
            .Freeze();
        UiScene scene = new UiSceneComposer(theme, registry).Compose(
            new UiInvocationService(registry).InvokeInEnvironment(
                id,
                Environment(UiInputMode.Keyboard, theme.Id)));
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new Platform())
            .Build(scene, new UiHostPlacementContext(new UiRect(0, 0, 1000, 700)));
        UiRenderFrame frame = new UiSceneRenderPlanner().Build(scene, layout);

        UiTextPrimitive prompt = Assert.Single(
            frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Text == "Enter");
        UiTextPrimitive label = Assert.Single(
            frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Text == "Apply");

        Assert.Equal(promptTypography.Size * promptTypography.LineHeight, prompt.Bounds.Height, 3);
        Assert.Equal(prompt.Bounds.Height, prompt.Clip.Height, 3);
        Assert.Equal(prompt.Bounds.Height, label.Bounds.Height, 3);
    }

    [Theory]
    [InlineData(UiInputMode.MouseKeyboard, null)]
    [InlineData(UiInputMode.Keyboard, "Enter")]
    [InlineData(UiInputMode.Controller, "A")]
    public void SelectableCollectionRowsProjectMeasuredFrameworkPrompts(
        UiInputMode mode,
        string? expected)
    {
        UiSymbolId id = Id($"collection/{mode}");
        var source = new UiSelectableCollectionState<int>(
            new[] { 0, 1, 2 },
            value => id.Child("item/" + value),
            value => "Cargo " + value);
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Row prompts")
            .Select("Cargo", source)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => experience)
            .Freeze();
        UiTheme theme = UiThemePresets.Dark();
        UiScene scene = new UiSceneComposer(theme, registry).Compose(
            new UiInvocationService(registry).InvokeInEnvironment(id, Environment(mode, theme.Id)));
        var host = new UiPortalHostSession(
            scene,
            new UiHostPlacementContext(new UiRect(0, 0, 1000, 700)),
            new Platform());
        UiCollectionSceneNode collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());
        UiCollectionLayoutWindow window = Assert.Single(host.Root.Layout.CollectionWindows);
        UiTextPrimitive[] prompts = host.Root.Frame.Primitives
            .OfType<UiTextPrimitive>()
            .Where(text => expected != null && text.Text == expected &&
                           window.Items.Any(item => item.Node == text.Node))
            .ToArray();
        UiAccessibilityNodeSnapshot[] items = Accessibility(host.Accessibility.Root.Root)
            .Where(node => node.Role == UiAccessibilityRole.ListItem)
            .ToArray();

        Assert.Equal(expected, UiSceneLayoutEngine.InputPrompt(collection)?.Label);
        Assert.Equal(expected is null ? 0 : window.Items.Count, prompts.Length);
        Assert.Equal(window.Items.Count, items.Length);
        Assert.All(items, item => Assert.Equal(expected, item.Shortcut));
        if (expected is null) return;

        foreach (UiVirtualizedItemLayout item in window.Items)
        {
            UiTextPrimitive prompt = Assert.Single(prompts, value => value.Node == item.Node);
            UiTextPrimitive label = Assert.Single(
                host.Root.Frame.Primitives.OfType<UiTextPrimitive>(),
                value => value.Node == item.Node && value.Text == item.Item.Label);
            Assert.True(prompt.Bounds.X >= label.Bounds.Right);
            Assert.Equal(theme.Resolve(UiThemeTokens.TextInputPrompt), prompt.Foreground);
            Assert.Equal(theme.Resolve(UiThemeTokens.TypographyInputPrompt), prompt.Typography);
            Assert.True(prompt.Bounds.Width > 0 && prompt.Bounds.Height > 0);
        }
    }

    [Fact]
    public void ReadOnlyCollectionDoesNotClaimAnActivationPrompt()
    {
        UiSymbolId id = Id("collection/read-only");
        var source = new UiCollectionSource<int>(
            new[] { 0, 1 },
            value => id.Child("item/" + value),
            value => "Cargo " + value);
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Read-only rows")
            .Browse("Cargo", source)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => experience)
            .Freeze();
        UiTheme theme = UiThemePresets.Dark();
        UiScene scene = new UiSceneComposer(theme, registry).Compose(
            new UiInvocationService(registry).InvokeInEnvironment(
                id, Environment(UiInputMode.Controller, theme.Id)));
        var host = new UiPortalHostSession(
            scene,
            new UiHostPlacementContext(new UiRect(0, 0, 1000, 700)),
            new Platform());
        UiCollectionSceneNode collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());

        Assert.Null(UiSceneLayoutEngine.InputPrompt(collection));
        Assert.DoesNotContain(host.Root.Frame.Primitives.OfType<UiTextPrimitive>(), text => text.Text == "A");
    }

    [Fact]
    public void CollectionPromptChangeInvalidatesLayoutWithoutStructuralRecompose()
    {
        UiSymbolId id = Id("collection/reconcile");
        var source = new UiSelectableCollectionState<int>(
            new[] { 0, 1 },
            value => id.Child("item/" + value),
            value => "Cargo " + value);
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Row prompts")
            .Select("Cargo", source)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, experience.DisplayName, () => experience)
            .Freeze();
        UiTheme theme = UiThemePresets.Dark();
        var invoker = new UiInvocationService(registry);
        var composer = new UiSceneComposer(theme, registry);
        UiScene keyboard = composer.Compose(invoker.InvokeInEnvironment(
            id, Environment(UiInputMode.Keyboard, theme.Id)));
        UiScene controller = composer.Compose(invoker.InvokeInEnvironment(
            id, Environment(UiInputMode.Controller, theme.Id)));

        UiSceneDiff diff = new UiSceneReconciler().Compare(keyboard, controller);

        Assert.Equal("Enter", UiSceneLayoutEngine.InputPrompt(
            Assert.Single(Nodes(keyboard.Root).OfType<UiCollectionSceneNode>()))?.Label);
        Assert.Equal("A", UiSceneLayoutEngine.InputPrompt(
            Assert.Single(Nodes(controller.Root).OfType<UiCollectionSceneNode>()))?.Label);
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Measure));
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Arrange));
        Assert.True(diff.Effects.HasFlag(UiPropertyEffects.Render));
        Assert.False(diff.RequiresRecompose);
    }

    private static UiEnvironment Environment(UiInputMode mode, UiSymbolId theme)
        => new(new UiEnvironmentViewport(1200, 700), 1, mode, "en", theme);

    private static UiSymbolId Id(string path) => new("Hatifect.Tests", "input-prompt/" + path);

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    private static IEnumerable<UiAccessibilityNodeSnapshot> Accessibility(UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (UiAccessibilityNodeSnapshot child in node.Children)
        foreach (UiAccessibilityNodeSnapshot descendant in Accessibility(child))
            yield return descendant;
    }

    private sealed class Platform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * 0.6f, availableWidth), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
