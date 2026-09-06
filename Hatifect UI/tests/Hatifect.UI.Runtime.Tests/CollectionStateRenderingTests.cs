using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Language;
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

public sealed class CollectionStateRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlannerWithoutInteractionPreservesPreparedStatesAndExplicitEmptyInteractionClearsThem(bool hover)
    {
        using var fixture = new Fixture("Uniform");
        if (hover)
        {
            UiRect bounds = fixture.Window.Items[1].Bounds;
            fixture.Host.MovePointer(new UiPoint(bounds.X + 1, bounds.Y + 1));
        }
        else fixture.FocusSecond();
        UiScene scene = fixture.Runtime.Scene;
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(fixture.Platform).Build(scene, Fixture.Viewport);
        var planner = new UiSceneRenderPlanner();

        UiRenderFrame prepared = planner.Build(scene, layout);

        UiSurfacePrimitive surface = Assert.Single(prepared.Primitives.OfType<UiSurfacePrimitive>(), item => item.Node == fixture.Item(1));
        if (hover) Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.SurfaceHover), surface.Surface);
        else Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.BorderFocus), surface.Border);
        UiRenderFrame cleared = planner.Build(scene, layout, new UiInteractionSnapshot(), fixture.Platform);
        Assert.DoesNotContain(cleared.Primitives.OfType<UiSurfacePrimitive>(), item => item.Node == fixture.Item(1));
    }

    [Fact]
    public void SelectedTextRecipeUpdatesTheActualGlyphColorWithoutReplacingGeometry()
    {
        using var fixture = new Fixture("Uniform", initiallySelected: true, selectedTextRecipe: true);
        UiLayoutSnapshot layout = fixture.Runtime.Layout;
        fixture.Draw();
        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.TextDanger), fixture.Text(0).Foreground);

        fixture.FocusSecond();
        fixture.Host.Submit();
        fixture.Draw();

        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.TextPrimary), fixture.Text(0).Foreground);
        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.TextDanger), fixture.Text(1).Foreground);
        Assert.Same(layout, fixture.Runtime.Layout);
    }

    [Theory]
    [InlineData("Uniform", true)]
    [InlineData("Adaptive", true)]
    [InlineData("Uniform", false)]
    [InlineData("Adaptive", false)]
    public void SubmitUpdatesDrawnSelectionAndAccessibilityWhileKeepingItemGeometry(string sizing, bool published)
    {
        using var fixture = new Fixture(sizing, published, initiallySelected: true);
        UiLayoutSnapshot layout = fixture.Runtime.Layout;
        UiCollectionLayoutWindow window = fixture.Window;
        UiRenderFrame frame = fixture.Runtime.Frame;
        long layouts = fixture.Runtime.Performance.LayoutBuilds;
        fixture.Draw();
        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.SurfacePressed), fixture.Surface(0).Surface);
        Assert.True(fixture.Accessibility(0).Selected);

        fixture.FocusSecond();
        Assert.True(fixture.Host.Submit().Consumed);
        fixture.Draw();

        Assert.Same(layout, fixture.Runtime.Layout);
        Assert.Same(window, fixture.Window);
        Assert.Equal(layouts, fixture.Runtime.Performance.LayoutBuilds);
        Assert.NotSame(frame, fixture.Runtime.Frame);
        Assert.Equal(fixture.Item(1), fixture.Selection.SelectedItemId);
        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.SurfacePressed), fixture.Surface(1).Surface);
        Assert.DoesNotContain(fixture.Platform.Surfaces, item => item.Node == fixture.Item(0));
        Assert.False(fixture.Accessibility(0).Selected);
        Assert.True(fixture.Accessibility(1).Selected);
        Assert.True(fixture.Accessibility(1).Focused);
        Assert.Equal(window.Items[1].Bounds, fixture.Surface(1).Bounds);
        // The window really is the original geometry snapshot, including its old selection.
        Assert.True(window.Items[0].Selected);
        Assert.False(window.Items[1].Selected);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void KeyboardFocusDrawsABorderWithoutSelectionOrHoverAndWithoutLayoutWork(string sizing)
    {
        using var fixture = new Fixture(sizing);
        UiLayoutSnapshot layout = fixture.Runtime.Layout;
        Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Next).Consumed);
        fixture.Draw();
        fixture.AssertFocusedSurface(fixture.Item(0));

        Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Next).Consumed);
        fixture.Draw();

        fixture.AssertFocusedSurface(fixture.Item(1));
        Assert.DoesNotContain(fixture.Platform.Surfaces, item => item.Node == fixture.Item(0));
        Assert.Same(layout, fixture.Runtime.Layout);
        Assert.Null(fixture.Selection.SelectedItemId);
        Assert.Null(fixture.Runtime.Interactions.Snapshot.Hovered);
        Assert.False(fixture.Accessibility(0).Focused);
        Assert.True(fixture.Accessibility(1).Focused);

        fixture.Platform.Record = false;
        for (int index = 0; index < 100; index++) fixture.Host.Render();
        var performance = fixture.Runtime.Performance;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 600; index++) fixture.Host.Render();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(performance, fixture.Runtime.Performance);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void RemovedFocusIsDrawnOnItsSurvivorInTheFirstFrameAfterReconciliation(string sizing)
    {
        using var fixture = new Fixture(sizing, guardReads: true);
        fixture.FocusSecond();
        UiScene next = fixture.Prepare(new[] { 0, 2, 3, 4, 5 });
        int compositions = fixture.Compositions;
        int captures = fixture.Guard!.Captures;
        fixture.Guard.RejectReads = true;
        // The unprepared fallback state must use this scene's theme, even if its composer changes.
        fixture.Composer.SetTheme(new UiThemeBuilder(fixture.Theme)
            .Set(UiThemeTokens.BorderFocus, new UiBorder(new UiColor(255, 0, 0), 7))
            .Build(RegistryTests.Id("later-theme")));

        fixture.Runtime.Update(next, Fixture.Viewport);
        fixture.Draw();

        Assert.Equal(fixture.Item(2), fixture.Runtime.Interactions.Snapshot.Focused);
        fixture.AssertFocusedSurface(fixture.Item(2));
        Assert.DoesNotContain(fixture.Platform.Surfaces, item => item.Node == fixture.Item(1));
        Assert.True(fixture.Accessibility(2).Focused);
        Assert.False(fixture.Accessibility(2).Selected);
        Assert.Equal(compositions, fixture.Compositions);
        Assert.Equal(captures, fixture.Guard.Captures);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void EmptyContainerAndRefilledFirstItemReceiveTheReconciledFocusBorder(string sizing)
    {
        using var fixture = new Fixture(sizing);
        fixture.FocusSecond();

        fixture.Runtime.Update(fixture.Prepare(Array.Empty<int>()), Fixture.Viewport);
        fixture.Draw();

        fixture.AssertFocusedSurface(fixture.Collection);
        Assert.Equal(fixture.Collection, fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Empty(fixture.Window.Items);
        UiSurfacePrimitive empty = Assert.Single(fixture.Platform.Surfaces, item => item.Node == fixture.Collection);
        Assert.True(empty.Bounds.Width > 0 && empty.Bounds.Height > 0);

        fixture.Runtime.Update(fixture.Prepare(new[] { 20, 10 }), Fixture.Viewport);
        fixture.Draw();

        fixture.AssertFocusedSurface(fixture.Item(20));
        Assert.DoesNotContain(fixture.Platform.Surfaces, item => item.Node == fixture.Collection);
        Assert.True(fixture.Accessibility(20).Focused);
        Assert.Null(fixture.Selection.SelectedItemId);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void HoverPressAndCancelledReleaseDrawCurrentStatesOnTheSameLayout(string sizing)
    {
        using var fixture = new Fixture(sizing);
        UiLayoutSnapshot layout = fixture.Runtime.Layout;
        UiRect bounds = fixture.Window.Items[0].Bounds;
        var point = new UiPoint(bounds.X + 1, bounds.Y + 1);

        fixture.Host.MovePointer(point);
        fixture.Draw();
        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.SurfaceHover), fixture.Surface(0).Surface);
        Assert.Null(fixture.Surface(0).Border);

        fixture.Host.PressPointer(point);
        fixture.Draw();
        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.SurfacePressed), fixture.Surface(0).Surface);
        Assert.Equal(fixture.Theme.Resolve(UiThemeTokens.BorderFocus), fixture.Surface(0).Border);

        var outside = new UiPoint(-100, -100);
        fixture.Host.MovePointer(outside);
        fixture.Host.ReleasePointer(outside);
        fixture.Draw();

        fixture.AssertFocusedSurface(fixture.Item(0));
        Assert.Null(fixture.Runtime.Interactions.Snapshot.Pressed);
        Assert.Null(fixture.Runtime.Interactions.Snapshot.Hovered);
        Assert.Null(fixture.Selection.SelectedItemId);
        Assert.Same(layout, fixture.Runtime.Layout);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly UiSymbolId Owner = RegistryTests.Id("current-collection-rendering");
        internal static readonly UiRect Viewport = new(0, 0, 360, 300);
        private readonly UiPublication _publication = new(Owner);
        private readonly UiPublishedSelectableCollection<int>? _published;
        private readonly UiSelectableCollectionState<int>? _legacy;
        private readonly UiInvocationResult _invocation;
        private readonly UiVisualDefinition? _visual;

        internal Fixture(string sizing, bool published = true, bool initiallySelected = false, bool guardReads = false,
            bool selectedTextRecipe = false)
        {
            int[] values = Enumerable.Range(0, 6).ToArray();
            IUiSemanticSource<IReadOnlyList<int>> source;
            if (published)
                source = _published = _publication.SelectableCollection(Owner.Child("rows"), values,
                    UiSourceTypes.Scalar<int>(Owner.Child("type/int"), false), Item, value => "Item " + value);
            else
                source = _legacy = new UiSelectableCollectionState<int>(values, Item, value => "Item " + value);
            Selection = (IUiSelectableCollectionSource)source;
            if (initiallySelected) Assert.True(Selection.TrySelect(Item(0)));
            if (guardReads) source = Guard = new GuardedSource(source);
            var experience = new UiExperienceBuilder(Owner, "Collection rendering").Select("Items", source).VisualRole("Items").Build();
            var registry = new UiRegistryBuilder().Window(Owner, "Collection rendering", () => experience).Freeze();
            var catalog = UiSemanticCatalog.CreateFoundation();
            var compilation = new UiCompiler(catalog).Compile(
                "presentation List\n\nItems\n    view = List\n    itemSizing = " + sizing + "\n",
                experience.CreateBindingContext(), "CollectionRendering#presentation");
            Assert.True(compilation.IsValid, string.Join("\n", compilation.Diagnostics.Select(item => item.Message)));
            var presentation = Assert.IsType<UiPresentationDefinition>(compilation.Definition);
            if (selectedTextRecipe)
            {
                var visual = new UiCompiler(catalog).Compile("visual Collection\n\nItems@Selected\n    foreground = Text.Danger\n",
                    experience.CreateBindingContext(), "CollectionRendering#visual");
                Assert.True(visual.IsValid);
                _visual = Assert.IsType<UiVisualDefinition>(visual.Definition);
            }
            _invocation = new UiInvocationService(registry, planner: new UiPresentationPlanner(catalog))
                .Invoke(Owner, UiPresentationProfiles.Wide, presentation);
            Composer = new UiSceneComposer(Theme, registry, catalog);
            UiScene scene = Compose(new UiInteractionSnapshot());
            Collection = Assert.Single(scene.Root.Children.SelectMany(slot => slot.Children).OfType<UiCollectionSceneNode>()).Id;
            Host = new UiPortalHostSession(scene, new UiHostPlacementContext(Viewport), Platform, composeInteraction: Compose);
        }

        internal UiTheme Theme { get; } = UiThemePresets.Dark();
        internal RecordingPlatform Platform { get; } = new();
        internal UiSceneComposer Composer { get; }
        internal UiPortalHostSession Host { get; }
        internal UiHostRuntimeSession Runtime => Host.Root;
        internal IUiSelectableCollectionSource Selection { get; }
        internal GuardedSource? Guard { get; }
        internal UiSymbolId Collection { get; }
        internal int Compositions { get; private set; }
        internal UiSymbolId Item(int value) => Owner.Child("item/" + value);
        internal UiCollectionLayoutWindow Window
        {
            get { Assert.True(Runtime.Layout.TryGetCollection(Collection, out var window)); return window!; }
        }
        internal UiAccessibilityNodeSnapshot Accessibility(int value)
            => Assert.Single(Runtime.Accessibility.Root.Children.SelectMany(slot => slot.Children)
                .Where(node => node.Id == Collection).SelectMany(node => node.Children), node => node.Id == Item(value));
        internal UiSurfacePrimitive Surface(int value) => Assert.Single(Platform.Surfaces, item => item.Node == Item(value));
        internal UiTextPrimitive Text(int value) => Assert.Single(Platform.Texts, item => item.Node == Item(value));
        internal void Draw() { Platform.Surfaces.Clear(); Platform.Texts.Clear(); Host.Render(); }
        internal void AssertFocusedSurface(UiSymbolId node)
        {
            var surface = Assert.Single(Platform.Surfaces, item => item.Node == node);
            Assert.Equal(Theme.Resolve(UiThemeTokens.BorderFocus), surface.Border);
            Assert.Equal((byte)0, surface.Surface.Tint.A);
        }
        internal void FocusSecond()
        {
            Assert.True(Host.MoveFocus(UiNavigationDirection.Next).Consumed);
            Assert.True(Host.MoveFocus(UiNavigationDirection.Next).Consumed);
            Assert.Equal(Item(1), Runtime.Interactions.Snapshot.Focused);
        }
        internal UiScene Prepare(int[] values)
        {
            if (_published is not null)
                Assert.Equal(UiPublicationStatus.Committed, _publication.BeginUpdate().Replace(_published, values).Commit().Status);
            else _legacy!.Replace(values);
            return Compose(Runtime.Interactions.Snapshot);
        }
        private UiScene Compose(UiInteractionSnapshot interaction)
        {
            Compositions++;
            return Composer.Compose(_invocation, _visual, interaction);
        }
        public void Dispose() => _publication.Dispose();
    }

    private sealed class GuardedSource : IUiSemanticSource<IReadOnlyList<int>>, IUiSelectableCollectionSource,
        IUiSemanticCollectionSnapshotSource
    {
        private readonly IUiSemanticSource<IReadOnlyList<int>> _source;
        internal GuardedSource(IUiSemanticSource<IReadOnlyList<int>> source) => _source = source;
        internal bool RejectReads { get; set; }
        internal int Captures { get; private set; }
        public IReadOnlyList<int> Value { get { Check(); return _source.Value; } }
        public object UntypedValue => Value;
        public Type ValueType => _source.ValueType;
        public int Count { get { Check(); return ((IUiSemanticCollectionSource)_source).Count; } }
        public UiSymbolId? SelectedItemId { get { Check(); return ((IUiSelectableCollectionSource)_source).SelectedItemId; } }
        public bool TrySelect(UiSymbolId item) { Check(); return ((IUiSelectableCollectionSource)_source).TrySelect(item); }
        public UiSemanticCollectionItem GetItem(int index) { Check(); return ((IUiSemanticCollectionSource)_source).GetItem(index); }
        public IUiSemanticCollectionSnapshot CaptureSnapshot()
        {
            Check(); Captures++;
            return ((IUiSemanticCollectionSnapshotSource)_source).CaptureSnapshot();
        }
        public event Action? Changed { add => _source.Changed += value; remove => _source.Changed -= value; }
        private void Check() { if (RejectReads) throw new InvalidOperationException("Rendering reread the collection owner."); }
    }

    private sealed class RecordingPlatform : IUiPlatformBridge
    {
        internal List<UiSurfacePrimitive> Surfaces { get; } = new();
        internal List<UiTextPrimitive> Texts { get; } = new();
        internal bool Record { get; set; } = true;
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
            => new(Math.Min(text.Length * typography.Size * .6f, availableWidth), typography.Size * typography.LineHeight);
        public void DrawSurface(UiSurfacePrimitive surface) { if (Record) Surfaces.Add(surface); }
        public void DrawText(UiTextPrimitive text) { if (Record) Texts.Add(text); }
    }
}
