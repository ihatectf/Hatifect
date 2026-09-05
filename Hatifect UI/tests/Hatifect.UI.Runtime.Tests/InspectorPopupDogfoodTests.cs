using System;
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
using Hatifect.UI.Tooling.Validation;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class InspectorPopupDogfoodTests
{
    [Fact]
    [Trait("Category", "dogfood")]
    public void RussianInspectorPopupTraversesTheSemanticPipelineWithoutWidgetOrCssTypes()
    {
        UiSymbolId id = RegistryTests.Id("dogfood/storage-inspector");
        int closed = 0;
        var close = new UiActionDefinition(id.Child("action/close"), "Закрыть", () => closed++);
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Сведения о хранилище")
            .Inspect("Inspector", new UiState<string?>("Лесной склад · 36 ячеек · доступен"))
            .Actions("Actions", close)
            .VisualRole("Inspector")
            .VisualRole("Action.Primary")
            .Build();

        UiBuildValidationResult assets = new UiBuildValidator().Validate(new[]
        {
            new UiBuildAsset(
                "StorageInspector#presentation",
                PresentationSource,
                experience.CreateBindingContext(),
                UiDefinitionKind.Presentation),
            new UiBuildAsset(
                "StorageInspector#visual",
                VisualSource,
                experience.CreateBindingContext(),
                UiDefinitionKind.Visual)
        });
        Assert.True(assets.IsValid, string.Join(Environment.NewLine, assets.Diagnostics.Select(item => item.Message)));
        UiPresentationDefinition presentation = Assert.IsType<UiPresentationDefinition>(
            assets.Definitions.Single(item => item.Definition.Kind == UiDefinitionKind.Presentation).Definition);
        UiVisualDefinition visual = Assert.IsType<UiVisualDefinition>(
            assets.Definitions.Single(item => item.Definition.Kind == UiDefinitionKind.Visual).Definition);

        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Popup(id, experience.DisplayName, () => experience, UiHostPolicies.Modal)
            .Freeze();
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Compact, presentation);
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(invocation, visual);
        var platform = new DogfoodPlatform();
        var runtime = new UiHostRuntimeSession(
            scene,
            new UiHostPlacementContext(new UiRect(0, 0, 640, 360)),
            platform);

        Assert.Equal(UiAccessibilityRole.Dialog, runtime.Accessibility.Root.Role);
        UiAccessibilityNodeSnapshot inspector = Descendants(runtime.Accessibility.Root)
            .Single(node => node.Role == UiAccessibilityRole.Inspector);
        Assert.Equal("Inspector", inspector.Name);
        Assert.Equal("Лесной склад · 36 ячеек · доступен", inspector.Value);
        UiAccessibilityNodeSnapshot button = Descendants(runtime.Accessibility.Root)
            .Single(node => node.Role == UiAccessibilityRole.Button);
        Assert.Equal("Закрыть", button.Name);
        Assert.True(button.Bounds.Width > 0);
        Assert.True(button.Bounds.Height > 0);
        Assert.True(button.Clip.Width >= 0);
        Assert.True(button.Clip.Height >= 0);
        Assert.Contains(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), primitive => primitive.Text == "Закрыть");

        UiInteractionUpdate focused = runtime.Interactions.MoveFocus(UiNavigationDirection.Next);
        UiInteractionUpdate submitted = runtime.Interactions.Submit();
        Assert.True(focused.Consumed);
        Assert.True(submitted.ActionInvoked);
        Assert.Equal(1, closed);

        runtime.Render();
        Assert.Equal(runtime.Frame.Primitives.Count, platform.DrawCount);
    }

    private const string PresentationSource = @"presentation StorageInspector

use = Prompt
Inspector -> Primary
Actions -> Actions

Inspector.view
    default = Side
    Compact = Sheet
";

    private const string VisualSource = @"visual StorageInspector

Inspector
    surface = Surface.Raised
    foreground = Text.Primary
    padding = Space.M
    radius = Radius.M
    typography = Typography.Body

Action.Primary
    surface = Surface.Secondary
    foreground = Text.Primary
    padding = Space.M
    radius = Radius.M
    typography = Typography.Label

Action.Primary@Focused
    border = Border.Focus

Action.Primary@Pressed
    surface = Surface.Pressed
    transform = Transform.Pressed
";

    private static System.Collections.Generic.IEnumerable<UiAccessibilityNodeSnapshot> Descendants(
        UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (UiAccessibilityNodeSnapshot child in node.Children)
        foreach (UiAccessibilityNodeSnapshot descendant in Descendants(child))
            yield return descendant;
    }

    private sealed class DogfoodPlatform : IUiPlatformBridge
    {
        public int DrawCount { get; private set; }

        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(
                Math.Min(availableWidth, text.Length * typography.Size * 0.6f),
                typography.Size * typography.LineHeight);

        public void DrawSurface(UiSurfacePrimitive surface) => DrawCount++;
        public void DrawText(UiTextPrimitive text) => DrawCount++;
    }
}
