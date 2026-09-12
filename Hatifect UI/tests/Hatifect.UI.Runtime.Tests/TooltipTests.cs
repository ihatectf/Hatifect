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
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class TooltipTests
{
    private static readonly UiRect Viewport = new(0, 0, 900, 600);

    [Theory]
    [InlineData(UiInputMode.Keyboard, UiSemanticTheme.Dark)]
    [InlineData(UiInputMode.Keyboard, UiSemanticTheme.Light)]
    [InlineData(UiInputMode.Keyboard, UiSemanticTheme.HighContrast)]
    [InlineData(UiInputMode.Controller, UiSemanticTheme.Dark)]
    [InlineData(UiInputMode.Controller, UiSemanticTheme.Light)]
    [InlineData(UiInputMode.Controller, UiSemanticTheme.HighContrast)]
    public void FocusShowsOneLocalizedThemeOwnedTooltipWithoutRelayout(
        UiInputMode mode,
        UiSemanticTheme semanticTheme)
    {
        UiSymbolId id = new("Hatifect.Tests", $"tooltip/focus/{mode}/{semanticTheme}");
        UiSymbolId actionId = id.Child("send");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Shipment")
            .Search("Query", new UiState<string>(string.Empty))
            .Actions("Actions", new UiActionDefinition(actionId, "Send", () => { }))
            .Tooltip(actionId, Localized("Send selected cargo", "Отправить выбранный груз"))
            .Build();
        UiTheme theme = UiSemanticThemes.Resolve(semanticTheme);
        UiScene scene = Scene(experience, theme, mode, "ru-RU");
        var platform = new Platform();
        var runtime = new UiHostRuntimeSession(scene, Viewport, platform);
        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
        long layouts = runtime.Performance.LayoutBuilds;

        for (int step = 0; step < 4 && runtime.Interactions.Snapshot.Focused != button.Id; step++)
            Assert.True(runtime.Interactions.MoveFocus(UiNavigationDirection.Next).Consumed);
        runtime.RefreshInteractionVisuals();

        Assert.NotNull(button.Tooltip);
        UiTooltipPresentation tooltip = button.Tooltip!;
        UiTextPrimitive text = Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            primitive => primitive.Node == tooltip.Id);
        UiSurfacePrimitive surface = Assert.Single(runtime.Frame.Primitives.OfType<UiSurfacePrimitive>(),
            primitive => primitive.Node == tooltip.Id);
        Assert.True(runtime.Layout.TryGetEntry(button.Id, out UiLayoutEntry? entry));
        Assert.NotNull(entry?.Tooltip);
        Assert.Equal("Отправить выбранный груз", text.Text);
        Assert.Equal(theme.Resolve(UiThemeTokens.TextPrimary), text.Foreground);
        Assert.Equal(theme.Resolve(UiThemeTokens.TypographyBody), text.Typography);
        Assert.Equal(theme.Resolve(UiThemeTokens.SurfacePopup), surface.Surface);
        Assert.Equal(theme.Resolve(UiThemeTokens.RadiusS), surface.Radius);
        Assert.Equal(theme.Resolve(UiThemeTokens.ElevationHigh), surface.Elevation);
        Assert.Equal(layouts, runtime.Performance.LayoutBuilds);
        Assert.True(text.Bounds.Width > 0 && text.Bounds.Height > 0);
        Assert.Equal(new UiThickness(theme.Resolve(UiThemeTokens.SpaceS).Value), entry!.Tooltip!.Inset);
        Assert.Equal(surface.Bounds.Inset(entry.Tooltip.Inset), text.Bounds);
        Assert.Equal(text.Bounds.X, text.Clip.X, 3);
        Assert.Equal(text.Bounds.Y, text.Clip.Y, 3);
        Assert.Equal(text.Bounds.Width, text.Clip.Width, 3);
        Assert.Equal(text.Bounds.Height, text.Clip.Height, 3);
        Assert.True(surface.Bounds.X >= Viewport.X && surface.Bounds.Right <= Viewport.Right);
        Assert.True(surface.Bounds.Y >= Viewport.Y && surface.Bounds.Bottom <= Viewport.Bottom);

        UiAccessibilityNodeSnapshot accessible = Assert.Single(
            Accessibility(runtime.Accessibility.Root), node => node.Id == button.Id);
        Assert.Equal("Отправить выбранный груз", accessible.Description);
    }

    [Fact]
    public void HoverTakesPrecedenceAndClearingItReturnsToFocusedTooltip()
    {
        UiSymbolId id = new("Hatifect.Tests", "tooltip/hover");
        UiSymbolId firstId = id.Child("first"), secondId = id.Child("second");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Commands")
            .Actions("Actions",
                new UiActionDefinition(firstId, "First", () => { }),
                new UiActionDefinition(secondId, "Second", () => { }))
            .Tooltip(firstId, Localized("First help", "Первая подсказка"))
            .Tooltip(secondId, Localized("Second help", "Вторая подсказка"))
            .Build();
        UiTheme theme = UiThemePresets.Dark();
        UiScene scene = Scene(experience, theme, UiInputMode.MouseKeyboard, "en");
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiButtonSceneNode[] buttons = Nodes(scene.Root).OfType<UiButtonSceneNode>().ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.DoesNotContain(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Node == buttons[0].Tooltip!.Id || text.Node == buttons[1].Tooltip!.Id);
        Assert.True(runtime.Layout.TryGetEntry(buttons[1].Id, out UiLayoutEntry? second));
        Assert.NotNull(second);

        runtime.Interactions.MoveFocus(UiNavigationDirection.Next);
        runtime.Interactions.MovePointer(Center(second.Bounds));
        runtime.RefreshInteractionVisuals();

        Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), text => text.Text == "Second help");
        Assert.DoesNotContain(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), text => text.Text == "First help");

        runtime.Interactions.MovePointer(new UiPoint(Viewport.Right + 1, Viewport.Bottom + 1));
        runtime.RefreshInteractionVisuals();

        Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), text => text.Text == "First help");
        Assert.DoesNotContain(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), text => text.Text == "Second help");
    }

    [Fact]
    public void PlacementUsesAboveFallbackAndClampsOversizedTooltipToViewport()
    {
        var viewport = new UiRect(10, 20, 120, 70);
        var anchor = new UiRect(100, 75, 30, 15);

        UiRect above = UiTooltipPlacement.Place(anchor, new UiSize(100, 40), viewport, gap: 8);
        UiRect clamped = UiTooltipPlacement.Place(anchor, new UiSize(180, 90), viewport, gap: 8);

        Assert.Equal(30, above.X);
        Assert.Equal(27, above.Y);
        Assert.Equal(100, above.Width);
        Assert.Equal(40, above.Height);
        Assert.Equal(viewport, clamped);
    }

    [Fact]
    public void AcceptedTooltipLayoutBuildsFrameWithoutRenderTimeTextMeasurement()
    {
        UiSymbolId id = new("Hatifect.Tests", "tooltip/premeasured");
        UiSymbolId actionId = id.Child("send");
        UiExperienceDefinition experience = Experience(id, actionId, "Send selected cargo");
        UiTheme theme = UiThemePresets.Dark();
        UiScene scene = Scene(experience, theme, UiInputMode.Controller, "en");
        UiButtonSceneNode button = Assert.Single(Nodes(scene.Root).OfType<UiButtonSceneNode>());
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new Platform()).Build(scene, Viewport);

        UiRenderFrame frame = new UiSceneRenderPlanner().Build(
            scene,
            layout,
            new UiInteractionSnapshot(Focused: button.Id),
            new RejectingTextMetrics());

        Assert.Single(frame.Primitives.OfType<UiTextPrimitive>(), text => text.Node == button.Tooltip!.Id);
    }

    [Fact]
    public void TooltipContractChangeInvalidatesOverlayLayoutWithoutStructuralRecompose()
    {
        UiSymbolId id = new("Hatifect.Tests", "tooltip/reconcile");
        UiSymbolId actionId = id.Child("send");
        UiTheme theme = UiThemePresets.Dark();
        UiScene before = Scene(Experience(id, actionId, tooltip: null), theme, UiInputMode.Keyboard, "en");
        UiScene after = Scene(Experience(id, actionId, "Send selected cargo"), theme, UiInputMode.Keyboard, "en");

        UiSceneDiff diff = new UiSceneReconciler().Compare(before, after);

        Assert.False(diff.RequiresRecompose);
        Assert.True(diff.RequiresLayout);
        Assert.Contains(Assert.Single(Nodes(after.Root).OfType<UiButtonSceneNode>()).Id, diff.ChangedNodes);
    }

    [Fact]
    public void FormFieldHelpUsesFocusedInputAndAccessibilityDescription()
    {
        UiSymbolId id = new("Hatifect.Tests", "tooltip/form/text");
        UiSymbolId fieldId = id.Child("field/name");
        using var form = new UiFormState(UiFormFields.Text(fieldId, "Name", string.Empty));
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Station")
            .Configure("Details", form)
            .Tooltip(fieldId, Localized("Enter a station name", "Введите название станции"))
            .Build();
        UiTheme theme = UiThemePresets.Dark();
        UiScene scene = Scene(experience, theme, UiInputMode.Keyboard, "ru-RU");
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiTextInputSceneNode input = Assert.Single(Nodes(scene.Root).OfType<UiTextInputSceneNode>());
        long layouts = runtime.Performance.LayoutBuilds;

        Assert.True(runtime.Interactions.MoveFocus(UiNavigationDirection.Next).Consumed);
        runtime.RefreshInteractionVisuals();

        Assert.Equal(input.Id, runtime.Interactions.Snapshot.Focused);
        Assert.Equal(fieldId, input.SemanticId);
        Assert.NotNull(input.Tooltip);
        Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Node == input.Tooltip!.Id && text.Text == "Введите название станции");
        UiAccessibilityNodeSnapshot accessible = Assert.Single(
            Accessibility(runtime.Accessibility.Root), node => node.Id == input.Id);
        Assert.Equal("Name", accessible.Name);
        Assert.Equal("Введите название станции", accessible.Description);
        Assert.Equal(layouts, runtime.Performance.LayoutBuilds);
    }

    [Fact]
    public void ChoiceFieldHelpProjectsToEveryFocusableOptionAndShowsOnlyTheFocusedOne()
    {
        UiSymbolId id = new("Hatifect.Tests", "tooltip/form/choice");
        UiSymbolId fieldId = id.Child("field/enabled");
        using var form = new UiFormState(UiFormFields.Toggle(fieldId, "Enabled", true));
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Station")
            .Configure("Details", form)
            .Tooltip(fieldId, Localized("Choose whether the station is enabled", "Выберите состояние станции"))
            .Build();
        UiTheme theme = UiThemePresets.Dark();
        UiScene scene = Scene(experience, theme, UiInputMode.Controller, "en");
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiButtonSceneNode[] options = Nodes(scene.Root).OfType<UiButtonSceneNode>().ToArray();

        Assert.Equal(2, options.Length);
        Assert.All(options, option =>
        {
            Assert.Equal(fieldId, option.SemanticId);
            Assert.Equal("Choose whether the station is enabled", option.Tooltip?.Text);
        });
        Assert.True(runtime.Interactions.MoveFocus(UiNavigationDirection.Next).Consumed);
        runtime.RefreshInteractionVisuals();

        UiButtonSceneNode focused = Assert.Single(options, option => option.Id == runtime.Interactions.Snapshot.Focused);
        Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Node == focused.Tooltip!.Id && text.Text == "Choose whether the station is enabled");
        Assert.DoesNotContain(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            text => options.Any(option => option.Id != focused.Id && text.Node == option.Tooltip!.Id));
    }

    [Fact]
    public void ContributedActionAndRouteResolveLocalizedHelpIntoExistingOverlayPolicy()
    {
        UiSymbolId owner = new("Hatifect.Tests", "tooltip/contribution/owner");
        UiSymbolId destination = new("Hatifect.Tests", "tooltip/contribution/destination");
        UiSymbolId point = owner.Child("point/actions");
        var ownerExperience = new UiExperienceBuilder(owner, "Owner")
            .Browse("Items", new UiCollectionSource<string>(Array.Empty<string>(), value => owner.Child(value)))
            .Build();
        var destinationExperience = new UiExperienceBuilder(destination, "Destination")
            .Browse("Items", new UiCollectionSource<string>(Array.Empty<string>(), value => destination.Child(value)))
            .Build();
        var action = new UiActionDefinition(owner.Child("action/run"), "Run", () => { });
        var actionHelp = Localized("Run the contributed command", "Выполнить добавленную команду");
        var routeHelp = Localized("Open the contributed route", "Открыть добавленный раздел");
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(owner, ownerExperience.DisplayName, () => ownerExperience)
            .Window(destination, destinationExperience.DisplayName, () => destinationExperience)
            .Publish(new UiContributionPointDescriptor(
                point,
                owner,
                new UiSymbolId("Hatifect.UI", "region/Actions"),
                UiContributionKind.Action,
                UiContributionKind.Route))
            .Contribute(new UiActionContributionDescriptor(
                owner.Child("contribution/run"), point, action, tooltip: actionHelp))
            .Contribute(new UiRouteContributionDescriptor(
                owner.Child("contribution/open"), point, "Open", destination, tooltip: routeHelp))
            .Freeze();
        UiTheme theme = UiThemePresets.Dark();
        var environment = new UiEnvironment(
            new UiEnvironmentViewport(Viewport.Width, Viewport.Height),
            1,
            UiInputMode.Controller,
            "ru-RU",
            theme.Id);
        UiScene scene = new UiSceneComposer(theme, registry).Compose(
            new UiInvocationService(registry).InvokeInEnvironment(owner, environment));
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiButtonSceneNode contributedAction = Assert.Single(
            Nodes(scene.Root).OfType<UiButtonSceneNode>(), node => node.Action.Id == action.Id);
        UiRouteButtonSceneNode contributedRoute = Assert.Single(
            Nodes(scene.Root).OfType<UiRouteButtonSceneNode>(), node => node.Route == destination);

        Assert.Equal("Выполнить добавленную команду", contributedAction.Tooltip?.Text);
        Assert.Equal("Открыть добавленный раздел", contributedRoute.Tooltip?.Text);
        foreach (UiSceneNode target in new UiSceneNode[] { contributedAction, contributedRoute })
        {
            UiRenderFrame frame = new UiSceneRenderPlanner().Build(
                scene,
                runtime.Layout,
                new UiInteractionSnapshot(Focused: target.Id),
                new Platform());
            Assert.Single(frame.Primitives.OfType<UiTextPrimitive>(),
                text => text.Node == target.Tooltip!.Id && text.Text == target.Tooltip.Text);
        }
    }

    [Fact]
    public void MaterializedCollectionItemHelpIsLocalizedIndexedAccessibleAndPremeasured()
    {
        UiSymbolId id = new("Hatifect.Tests", "tooltip/collection/items");
        var source = new UiSelectableCollectionState<int>(
            Enumerable.Range(0, 10_000).ToArray(),
            value => id.Child("item/" + value),
            label: value => "Item " + value,
            selectedItemId: null,
            supportingText: null,
            icon: null,
            tooltip: value => Localized("Help " + value, "Подсказка " + value));
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Items")
            .Select("Items", source)
            .Build();
        UiTheme theme = UiThemePresets.Dark();
        UiScene scene = Scene(experience, theme, UiInputMode.Controller, "ru-RU");
        var metrics = new Platform();
        var engine = new UiSceneLayoutEngine(metrics);
        UiLayoutSnapshot layout = engine.Build(scene, Viewport);
        Assert.True(layout.TryGetCollection(
            Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>()).Id,
            out UiCollectionLayoutWindow? window));
        Assert.NotNull(window);
        Assert.InRange(window!.Items.Count, 2, 100);
        UiVirtualizedItemLayout focused = window.Items[0];
        UiVirtualizedItemLayout hovered = window.Items[1];

        Assert.True(layout.TryGetCollectionItem(focused.Node, out UiVirtualizedItemLayout indexed));
        Assert.Equal(focused, indexed);
        Assert.Equal("Подсказка 0", focused.Tooltip?.Presentation.Text);
        Assert.Equal("Подсказка 1", hovered.Tooltip?.Presentation.Text);
        Assert.InRange(metrics.HelpMeasures, 2, 100);
        int helpMeasures = metrics.HelpMeasures;

        UiLayoutSnapshot repeated = engine.Build(scene, Viewport);
        Assert.Equal(helpMeasures, metrics.HelpMeasures);
        Assert.True(repeated.TryGetCollectionItem(hovered.Node, out UiVirtualizedItemLayout repeatedHovered));
        UiRenderFrame frame = new UiSceneRenderPlanner().Build(
            scene,
            repeated,
            new UiInteractionSnapshot(Focused: focused.Node, Hovered: hovered.Node),
            new RejectingTextMetrics());

        Assert.Single(frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Node == repeatedHovered.Tooltip!.Presentation.Id && text.Text == "Подсказка 1");
        Assert.DoesNotContain(frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Node == focused.Tooltip!.Presentation.Id);
        UiAccessibilityNodeSnapshot accessible = Assert.Single(
            Accessibility(new UiAccessibilitySnapshotBuilder()
                .Build(scene, repeated, new UiInteractionSnapshot(Focused: focused.Node)).Root),
            node => node.Id == focused.Node);
        Assert.Equal("Item 0", accessible.Name);
        Assert.Equal("Подсказка 0", accessible.Description);
    }

    [Fact]
    public void ReadOnlyCollectionItemHelpCanBeHoveredWithoutBecomingActionable()
    {
        UiSymbolId id = new("Hatifect.Tests", "tooltip/collection/read-only");
        var source = new UiCollectionSource<int>(
            new[] { 1 },
            value => id.Child("item/" + value),
            label: value => "Item " + value,
            supportingText: null,
            icon: null,
            tooltip: value => Localized("Help " + value, "Подсказка " + value));
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Items")
            .Browse("Items", source)
            .Build();
        UiScene scene = Scene(experience, UiThemePresets.Dark(), UiInputMode.MouseKeyboard, "ru-RU");
        var runtime = new UiHostRuntimeSession(scene, Viewport, new Platform());
        UiCollectionLayoutWindow window = Assert.Single(runtime.Layout.CollectionWindows);
        UiVirtualizedItemLayout item = Assert.Single(window.Items);
        UiPoint point = Center(item.Bounds);

        UiInteractionUpdate hover = runtime.Interactions.MovePointer(point);
        runtime.RefreshInteractionVisuals();

        Assert.True(hover.Consumed);
        Assert.Equal(item.Node, runtime.Interactions.Snapshot.Hovered);
        Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(),
            text => text.Node == item.Tooltip!.Presentation.Id && text.Text == "Подсказка 1");

        UiInteractionUpdate press = runtime.Interactions.PressPointer(point);

        Assert.False(press.Consumed);
        Assert.Null(runtime.Interactions.Snapshot.Focused);
        Assert.Null(runtime.Interactions.Snapshot.Pressed);
    }

    private static UiExperienceDefinition Experience(UiSymbolId id, UiSymbolId actionId, string? tooltip)
    {
        var builder = new UiExperienceBuilder(id, "Shipment")
            .Actions("Actions", new UiActionDefinition(actionId, "Send", () => { }));
        if (tooltip is not null)
            builder.Tooltip(actionId, new UiLocalizedText(
                tooltip,
                Array.Empty<KeyValuePair<string, string>>()));
        return builder.Build();
    }

    private static UiScene Scene(
        UiExperienceDefinition experience,
        UiTheme theme,
        UiInputMode inputMode,
        string locale)
    {
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(experience.Id, experience.DisplayName, () => experience)
            .Freeze();
        var environment = new UiEnvironment(
            new UiEnvironmentViewport(Viewport.Width, Viewport.Height),
            1,
            inputMode,
            locale,
            theme.Id);
        return new UiSceneComposer(theme, registry).Compose(
            new UiInvocationService(registry).InvokeInEnvironment(experience.Id, environment));
    }

    private static UiLocalizedText Localized(string fallback, string russian)
        => new(fallback, new[] { new KeyValuePair<string, string>("ru-RU", russian) });

    private static UiPoint Center(UiRect bounds)
        => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

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
        public int HelpMeasures { get; private set; }

        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            if (text.StartsWith("Подсказка ", StringComparison.Ordinal)) HelpMeasures++;
            float naturalWidth = text.Length * typography.Size * .6f;
            float width = Math.Min(naturalWidth, availableWidth);
            int lines = overflow == UiTextOverflow.Wrap
                ? Math.Max(1, (int)MathF.Ceiling(naturalWidth / Math.Max(1, availableWidth)))
                : 1;
            return new UiSize(width, lines * typography.Size * typography.LineHeight);
        }

        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }

    private sealed class RejectingTextMetrics : IUiTextMetrics
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => throw new InvalidOperationException("Frame construction must use accepted tooltip geometry.");
    }
}
