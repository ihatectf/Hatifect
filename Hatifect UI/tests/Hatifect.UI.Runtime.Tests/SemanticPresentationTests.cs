using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
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

public sealed class SemanticPresentationTests
{
    [Theory]
    [InlineData(.75f)]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    public void ScalarTitleLabelAndValueHaveSeparateVisibleRegions(float scale)
    {
        UiScene scene = Scene("Parcel", "Status", "Accepted");
        var platform = new Platform(scale);
        var runtime = new UiHostRuntimeSession(scene, new UiRect(0, 0, 700, 500), platform);
        runtime.Render();
        UiTextPrimitive title = Assert.Single(platform.Texts, text => text.Text == "Parcel");
        UiTextPrimitive label = Assert.Single(platform.Texts, text => text.Text == "Status");
        UiTextPrimitive value = Assert.Single(platform.Texts, text => text.Text == "Accepted");
        Assert.Equal(scene.Root.Id, title.Node);
        Assert.Equal(label.Node, value.Node);
        Assert.NotEqual(title.Node, label.Node);
        Assert.True(title.Bounds.Bottom <= label.Bounds.Y);
        Assert.True(label.Bounds.Bottom <= value.Bounds.Y);
        foreach (var run in platform.Texts)
        {
            Assert.True(run.Bounds.Width > 0 && run.Bounds.Height > 0);
            AssertContained(run.Bounds, run.Clip);
            Assert.True(run.Bounds.Height >= run.Typography.Size * run.Typography.LineHeight * scale);
        }
        Assert.Equal(UiTextOverflow.Wrap, value.Overflow);
    }

    [Fact]
    public void NarrowRussianHeadingsHaveExplicitSingleLineEllipsisAndEmptyValueHasNoRun()
    {
        string titleText = new string('Ж', 120);
        string labelText = new string('Я', 100);
        UiScene scene = Scene(titleText, labelText, string.Empty);
        var platform = new Platform(1.5f);
        var runtime = new UiHostRuntimeSession(scene, new UiRect(0, 0, 220, 250), platform);
        runtime.Render();
        Assert.Equal(2, platform.Texts.Count);
        foreach (var run in platform.Texts)
        {
            Assert.Equal(UiTextOverflow.Ellipsis, run.Overflow);
            AssertContained(run.Bounds, run.Clip);
            Assert.Equal(run.Typography.Size * run.Typography.LineHeight * 1.5f, run.Bounds.Height, 3);
        }
        Assert.Equal(new[] { titleText, labelText }, platform.Texts.Select(run => run.Text));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TitleOrLabelOnlyChangeInvalidatesAcceptedPresentation(bool changeTitle)
    {
        UiScene initial = Scene("Parcel", "Status", "Accepted");
        UiScene next = Scene(changeTitle ? "Посылка" : "Parcel", changeTitle ? "Status" : "Состояние", "Accepted");
        var platform = new Platform(1);
        var viewport = new UiRect(0, 0, 700, 500);
        var runtime = new UiHostRuntimeSession(initial, viewport, platform);
        Assert.False(runtime.Update(Scene("Parcel", "Status", "Accepted"), viewport).LayoutChanged);
        UiHostUpdate update = runtime.Update(next, viewport);
        Assert.True(update.LayoutChanged);
        Assert.True(update.FrameChanged);
        Assert.False(update.Diff.RequiresRecompose);
        Assert.Contains(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), run => run.Text == (changeTitle ? "Посылка" : "Состояние"));
        Assert.DoesNotContain(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), run => run.Text == (changeTitle ? "Parcel" : "Status"));
        Assert.False(runtime.Update(next, viewport).LayoutChanged);
    }

    [Fact]
    public void TerminalRetainsSevenSlotsBelowItsOwnRenderedShellTitle()
    {
        UiScene scene = Compose(terminal: true);
        Assert.Equal(7, scene.Root.Children.Count);
        Assert.All(scene.Root.Children, node => Assert.IsType<UiSlotSceneNode>(node));
        var runtime = new UiHostRuntimeSession(scene, new UiRect(0, 0, 1000, 700), new Platform(1));
        var title = Assert.Single(runtime.Frame.Primitives.OfType<UiTextPrimitive>(), run => run.Node == scene.Root.Id);
        Assert.Equal("Hatifect Terminal", title.Text);
        foreach (var slot in scene.Root.Children)
        {
            Assert.True(runtime.Layout.TryGetEntry(slot.Id, out var entry));
            Assert.True(entry!.Bounds.Y >= title.Bounds.Bottom);
        }
    }

    private static void AssertContained(UiRect bounds, UiRect clip)
    {
        const float epsilon = .001f;
        Assert.True(bounds.X >= clip.X - epsilon);
        Assert.True(bounds.Y >= clip.Y - epsilon);
        Assert.True(bounds.Right <= clip.Right + epsilon);
        Assert.True(bounds.Bottom <= clip.Bottom + epsilon);
    }

    private static UiScene Scene(string title, string label, string value)
    {
        UiScene recipe = Compose();
        UiSourceSceneNode original = Assert.Single(recipe.Root.Children.SelectMany(slot => slot.Children).OfType<UiSourceSceneNode>());
        var source = new UiSourceSceneNode(original.Id, original.Kind, original.Role, original.Visual, label,
            new UiConstantSource<string>(value)) { SemanticId = original.SemanticId };
        var root = new UiHostSceneNode(recipe.Root.Id, recipe.Root.Role, recipe.Root.Visual,
            recipe.Root.Policy, new[] { source }) { SemanticId = recipe.Root.SemanticId };
        return new UiScene(recipe.Experience, title, root, recipe.MeasurementContext);
    }

    private static UiScene Compose(bool terminal = false)
    {
        UiSymbolId id = RegistryTests.Id("semantic-presentation");
        var experience = new UiExperienceBuilder(id, "Parcel")
            .Monitor("Status", new UiConstantSource<string>("Accepted")).Build();
        var builder = new UiRegistryBuilder();
        if (terminal) builder.TerminalSection(id, experience.DisplayName, () => experience);
        else builder.Window(id, experience.DisplayName, () => experience);
        var registry = builder.Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
    }

    private sealed class Platform : IUiPlatformBridge
    {
        private readonly float _scale;
        internal readonly List<UiTextPrimitive> Texts = new();
        internal Platform(float scale) => _scale = scale;
        public UiSize Measure(string text, UiTypography typography, float width, UiTextOverflow overflow)
        {
            float natural = text.Length * typography.Size * .6f * _scale;
            float lines = overflow == UiTextOverflow.Wrap ? Math.Max(1, MathF.Ceiling(natural / Math.Max(1, width))) : 1;
            return new(Math.Min(width, natural), typography.Size * typography.LineHeight * _scale * lines);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) => Texts.Add(text);
    }
}
