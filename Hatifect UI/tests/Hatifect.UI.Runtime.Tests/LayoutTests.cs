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
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class LayoutTests
{
    [Fact]
    public void MeasureArrangeProducesFinitePositiveInteractiveGeometryInsideClip()
    {
        UiScene scene = Scene("Take ore");
        var viewport = new UiRect(10, 20, 480, 320);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(scene, viewport);

        foreach (UiSceneNode node in Nodes(scene.Root))
        {
            Assert.True(layout.TryGetEntry(node.Id, out UiLayoutEntry? entry));
            Assert.NotNull(entry);
            Assert.True(float.IsFinite(entry.Bounds.X));
            Assert.True(float.IsFinite(entry.Bounds.Y));
            Assert.True(float.IsFinite(entry.Bounds.Width));
            Assert.True(float.IsFinite(entry.Bounds.Height));
            Assert.True(entry.ContentBounds.Width >= 0);
            Assert.True(entry.ContentBounds.Height >= 0);
            Assert.True(entry.Clip.X >= viewport.X);
            Assert.True(entry.Clip.Y >= viewport.Y);
            Assert.True(entry.Clip.Right <= viewport.Right + 0.01f);
            Assert.True(entry.Clip.Bottom <= viewport.Bottom + 0.01f);

            if (node.Kind is UiSceneNodeKind.Button or UiSceneNodeKind.RouteButton or UiSceneNodeKind.TextInput)
            {
                Assert.True(entry.Bounds.Width > 0);
                Assert.True(entry.Bounds.Height > 0);
            }
        }
    }

    [Fact]
    public void TextPrimitiveUsesMeasuredContentBoxAndExplicitOverflow()
    {
        UiScene scene = Scene("A deliberately long action label");
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics())
            .Build(scene, new UiRect(0, 0, 220, 180));

        UiRenderFrame frame = new UiSceneRenderPlanner().Build(scene, layout);
        UiTextPrimitive button = Assert.Single(frame.Primitives.OfType<UiTextPrimitive>(), item => item.Text.Contains("long"));

        Assert.Equal(UiTextOverflow.Ellipsis, button.Overflow);
        Assert.True(button.Bounds.Width >= 0);
        Assert.True(button.Clip.Width >= 0);
        Assert.True(button.Bounds.X >= button.Clip.X);
    }

    [Fact]
    public void ImpossibleViewportFailsInsteadOfCollapsingInteractiveControls()
    {
        UiScene scene = Scene("Take");

        UiLayoutException error = Assert.Throws<UiLayoutException>(() =>
            new UiSceneLayoutEngine(new TestTextMetrics()).Build(scene, new UiRect(0, 0, 4, 4)));

        Assert.Contains("smaller than required minimum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModalIsCenteredWithoutUsingAnchorGeometry()
    {
        UiScene scene = Scene("Confirm", UiHostPolicies.Modal);
        var context = new UiHostPlacementContext(
            new UiRect(0, 0, 1000, 600),
            anchor: new UiRect(20, 20, 40, 40));

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(scene, context);

        Assert.Equal(UiHostPlacementKind.Center, layout.HostPlacement.Kind);
        Assert.False(layout.HostPlacement.UsedFallback);
        Assert.Equal(500, layout.HostPlacement.Bounds.X + layout.HostPlacement.Bounds.Width / 2, 3);
        Assert.Equal(300, layout.HostPlacement.Bounds.Y + layout.HostPlacement.Bounds.Height / 2, 3);
    }

    [Fact]
    public void AnchoredPopupFlipsAboveAndStaysInsideSafeViewport()
    {
        UiScene scene = Scene("Inspect", UiHostPolicies.Popup);
        var viewport = new UiRect(0, 0, 400, 300);
        var anchor = new UiRect(80, 260, 40, 20);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(
            scene,
            new UiHostPlacementContext(viewport, anchor: anchor));

        Assert.Equal(UiHostPlacementKind.AnchorAbove, layout.HostPlacement.Kind);
        Assert.False(layout.HostPlacement.UsedFallback);
        Assert.True(layout.HostPlacement.Bounds.Bottom <= anchor.Y - 8 + 0.01f);
        Assert.True(layout.HostPlacement.Bounds.X >= 12);
        Assert.True(layout.HostPlacement.Bounds.Right <= viewport.Right - 12 + 0.01f);
    }

    [Fact]
    public void ContextPopupFlipsAwayFromBottomRightPointer()
    {
        UiScene scene = Scene("Open", UiHostPolicies.Context);
        var viewport = new UiRect(0, 0, 400, 300);
        var pointer = new UiPoint(390, 290);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(
            scene,
            new UiHostPlacementContext(viewport, pointer: pointer));

        Assert.Equal(UiHostPlacementKind.PointerUpLeft, layout.HostPlacement.Kind);
        Assert.False(layout.HostPlacement.UsedFallback);
        Assert.True(layout.HostPlacement.Bounds.Right < pointer.X);
        Assert.True(layout.HostPlacement.Bounds.Bottom < pointer.Y);
    }

    [Fact]
    public void MissingPopupAnchorUsesExplainableCenterFallback()
    {
        UiScene scene = Scene("Inspect", UiHostPolicies.Popup);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(
            scene,
            new UiHostPlacementContext(new UiRect(0, 0, 400, 300)));

        Assert.Equal(UiHostPlacementKind.Center, layout.HostPlacement.Kind);
        Assert.True(layout.HostPlacement.UsedFallback);
    }

    [Theory]
    [InlineData(UiHostKind.Terminal)]
    [InlineData(UiHostKind.Fullscreen)]
    [InlineData(UiHostKind.Overlay)]
    public void SurfaceHostsFillViewport(UiHostKind kind)
    {
        UiHostPolicy policy = kind switch
        {
            UiHostKind.Terminal => UiHostPolicies.Terminal,
            UiHostKind.Fullscreen => UiHostPolicies.Fullscreen,
            UiHostKind.Overlay => UiHostPolicies.Overlay,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        UiScene scene = Scene("Open", policy);
        var viewport = new UiRect(10, 20, 480, 320);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(scene, viewport);

        Assert.Equal(UiHostPlacementKind.Fill, layout.HostPlacement.Kind);
        Assert.Equal(viewport, layout.HostPlacement.Bounds);
    }

    [Fact]
    public void ProvisionalTopRightOverlayUsesMeasuredContentInsideSafeViewport()
    {
        UiScene scene = Scene("Open", UiProvisionalHostPolicies.OverlayTopRight);
        var viewport = new UiRect(10, 20, 480, 320);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(scene, viewport);

        Assert.Equal(UiHostPlacementKind.TopRight, layout.HostPlacement.Kind);
        Assert.False(layout.HostPlacement.UsedFallback);
        Assert.Equal(viewport.Right - 12, layout.HostPlacement.Bounds.Right, 3);
        Assert.Equal(viewport.Y + 12, layout.HostPlacement.Bounds.Y, 3);
        Assert.True(layout.HostPlacement.Bounds.Width < viewport.Width - 24);
        Assert.True(layout.HostPlacement.Bounds.Height < viewport.Height - 24);
    }

    [Theory]
    [InlineData(true, UiDismissPolicy.OutsideOrEscape)]
    [InlineData(false, UiDismissPolicy.Escape)]
    public void ProvisionalCenteredOverlayUsesMeasuredModalBoundsAndConfiguredDismissal(
        bool closeOnOutsidePointer,
        UiDismissPolicy expectedDismissal)
    {
        UiHostPolicy policy = UiProvisionalHostPolicies.OverlayCentered(closeOnOutsidePointer);
        UiScene scene = Scene("Open", policy);
        var viewport = new UiRect(10, 20, 480, 320);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(scene, viewport);

        Assert.Equal(UiHostPlacementKind.Center, layout.HostPlacement.Kind);
        Assert.False(layout.HostPlacement.UsedFallback);
        Assert.Equal(expectedDismissal, policy.Dismiss);
        Assert.Equal(UiModalPolicy.Modal, policy.Modal);
        Assert.Equal(UiFocusScopePolicy.Trapped, policy.Focus);
        Assert.True(layout.HostPlacement.Bounds.Width < viewport.Width - 24);
        Assert.True(layout.HostPlacement.Bounds.Height < viewport.Height - 24);
        Assert.Equal(viewport.X + viewport.Width / 2, layout.HostPlacement.Bounds.X + layout.HostPlacement.Bounds.Width / 2, 3);
        Assert.Equal(viewport.Y + viewport.Height / 2, layout.HostPlacement.Bounds.Y + layout.HostPlacement.Bounds.Height / 2, 3);
    }

    [Fact]
    public void SheetIsDockedToSafeViewportBottom()
    {
        UiScene scene = Scene("Edit", UiHostPolicies.Sheet);
        var viewport = new UiRect(0, 0, 600, 400);

        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new TestTextMetrics()).Build(scene, viewport);

        Assert.Equal(UiHostPlacementKind.SheetBottom, layout.HostPlacement.Kind);
        Assert.Equal(viewport.Bottom - 12, layout.HostPlacement.Bounds.Bottom, 3);
        Assert.Equal(viewport.Width - 24, layout.HostPlacement.Bounds.Width, 3);
        Assert.True(layout.HostPlacement.Bounds.Height <= (viewport.Height - 24) * 0.6f + 0.01f);
    }

    private static UiScene Scene(string actionTitle, UiHostPolicy? host = null)
    {
        UiSymbolId id = RegistryTests.Id("layout-window");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Layout")
            .Search("Search", new UiState<string>(string.Empty))
            .Browse("Items", new UiCollectionSource<string>(
                new[] { "Wood", "Stone" }, item => id.Child($"item/{item}")))
            .Actions("Actions", new UiActionDefinition(RegistryTests.Id("layout/action"), actionTitle, () => { }))
            .VisualRole("Action.Primary")
            .Build();
        var builder = new UiRegistryBuilder();
        if (host?.Kind == UiHostKind.Terminal)
            builder.TerminalSection(id, "Layout", () => experience);
        else
            builder.Window(id, "Layout", () => experience, host);
        UiRegistrySnapshot registry = builder.Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
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
        {
            float glyphWidth = typography.Size * 0.6f;
            float naturalWidth = text.Length * glyphWidth;
            float lineHeight = typography.Size * typography.LineHeight;
            if (overflow == UiTextOverflow.Wrap && availableWidth > 0 && naturalWidth > availableWidth)
            {
                float lines = MathF.Ceiling(naturalWidth / availableWidth);
                return new UiSize(availableWidth, lines * lineHeight);
            }
            return new UiSize(Math.Min(naturalWidth, availableWidth), lineHeight);
        }
    }
}
