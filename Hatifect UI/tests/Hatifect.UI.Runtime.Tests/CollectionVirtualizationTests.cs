using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Accessibility;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Input;
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

public sealed class CollectionVirtualizationTests
{
    [Fact]
    public void LargeCollectionMaterializesOnlyBoundedVisibleWindow()
    {
        UiScene scene = Scene(Enumerable.Range(0, 10_000));
        var metrics = new CountingPlatform();
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(metrics)
            .Build(scene, new UiRect(0, 0, 640, 240));
        UiCollectionSceneNode collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());

        Assert.True(layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));
        Assert.NotNull(window);
        Assert.Equal(10_000, window.TotalCount);
        Assert.True(window.Columns > 1);
        int boundedRows = (int)MathF.Ceiling(240 / window.ItemExtent) + 5;
        Assert.InRange(window.Items.Count, 1, window.Columns * boundedRows);
        Assert.Equal(0, window.Items[0].Index);
        Assert.True(metrics.MeasureCalls < 20);

        UiRenderFrame frame = new UiSceneRenderPlanner().Build(scene, layout);
        string[] labels = frame.Primitives.OfType<UiTextPrimitive>().Select(item => item.Text).ToArray();
        Assert.Contains("Item 0", labels);
        Assert.DoesNotContain("Item 9999", labels);
    }

    [Fact]
    public void HostScrollClampsAtEndWithoutRecomposingScene()
    {
        UiScene scene = Scene(Enumerable.Range(0, 1_000));
        var platform = new CountingPlatform();
        var runtime = new UiHostRuntimeSession(scene, new UiRect(0, 0, 640, 240), platform);
        UiCollectionSceneNode collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());

        UiHostScrollUpdate update = runtime.ScrollCollection(collection.Id, 100_000);

        Assert.True(update.Consumed);
        Assert.True(update.LayoutChanged);
        Assert.True(update.FrameChanged);
        Assert.True(update.Offset > 0);
        Assert.True(runtime.Layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));
        Assert.NotNull(window);
        Assert.Equal(999, window.Items[^1].Index);
        Assert.InRange(window.Items.Count, 1, 20);
    }

    [Fact]
    public void CollectionReorderInvalidatesLayoutWithoutStructuralRecompose()
    {
        UiScene before = Scene(new[] { 1, 2, 3 });
        UiScene after = Scene(new[] { 3, 2, 1 });

        UiSceneDiff diff = new UiSceneReconciler().Compare(before, after);

        Assert.False(diff.RequiresRecompose);
        Assert.True(diff.RequiresLayout);
        Assert.True(diff.RequiresRender);
        Assert.Single(diff.ChangedNodes);
    }

    [Fact]
    public void MutableCollectionRevisionIsCapturedByEachScene()
    {
        UiSymbolId id = RegistryTests.Id("revisioned-collection");
        var state = new UiCollectionState<int>(
            new[] { 1, 2, 3 },
            item => id.Child($"item/{item}"));
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Revisioned collection")
            .Browse("Items", state)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Revisioned collection", () => experience)
            .Freeze();
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);
        UiScene before = composer.Compose(invocation);

        state.Replace(new[] { 3, 2, 1 });
        UiScene after = composer.Compose(invocation);
        UiSceneDiff diff = new UiSceneReconciler().Compare(before, after);

        Assert.False(diff.RequiresRecompose);
        Assert.True(diff.RequiresLayout);
        Assert.True(diff.RequiresRender);
    }

    [Fact]
    public void DuplicateSemanticItemIdentityFailsAtSourceBoundary()
    {
        UiSymbolId duplicate = RegistryTests.Id("item/duplicate");

        Assert.Throws<InvalidOperationException>(() => new UiCollectionSource<int>(
            new[] { 1, 2 },
            _ => duplicate,
            value => value.ToString()));
    }

    [Fact]
    public void CollectionStateSignalsOnlySemanticReplacement()
    {
        UiSymbolId owner = RegistryTests.Id("stateful-collection");
        var state = new UiCollectionState<int>(
            new[] { 1, 2 },
            value => owner.Child($"item/{value}"));
        int changed = 0;
        state.Changed += () => changed++;

        state.Replace(new[] { 1, 2 });
        state.Replace(new[] { 2, 1 });

        Assert.Equal(1, changed);
        Assert.Equal(2, Assert.IsType<int>(state.GetItem(0).Value));
    }

    [Fact]
    public void SelectableCollectionOwnsSelectionByStableItemIdentity()
    {
        UiSymbolId owner = RegistryTests.Id("selectable-state");
        UiSymbolId first = owner.Child("item/first");
        UiSymbolId second = owner.Child("item/second");
        var state = new UiSelectableCollectionState<int>(
            new[] { 1, 2 },
            value => value == 1 ? first : second,
            selectedItemId: first);
        int changed = 0;
        state.Changed += () => changed++;

        Assert.True(state.TrySelect(second));
        state.Replace(new[] { 2, 1 });
        state.Replace(new[] { 1 });

        Assert.Equal(first, state.GetItem(0).Id);
        Assert.Null(state.SelectedItemId);
        Assert.Equal(3, changed);
        Assert.False(state.TrySelect(second));
    }

    [Fact]
    public void SelectionItemsUseArrangedGeometryAndControllerFocus()
    {
        UiSymbolId id = RegistryTests.Id("selectable-items");
        var state = new UiSelectableCollectionState<int>(
            Enumerable.Range(0, 12).ToArray(),
            item => id.Child($"item/{item}"),
            item => $"Item {item}");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Selectable")
            .Select("Items", state)
            .VisualRole("Items")
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Selectable", () => experience)
            .Freeze();
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);
        UiScene scene = composer.Compose(invocation);
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(new CountingPlatform())
            .Build(scene, new UiRect(0, 0, 420, 180));
        UiCollectionSceneNode collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>());
        Assert.True(layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));
        Assert.NotNull(window);
        Assert.Equal(1, window.Columns);
        var interactions = new UiInteractionSession(scene, layout);
        UiVirtualizedItemLayout second = window.Items[1];
        var point = new UiPoint(second.Bounds.X + 1, second.Bounds.Y + 1);

        Assert.True(interactions.PressPointer(point).Consumed);
        Assert.True(interactions.ReleasePointer(point).Consumed);
        Assert.Equal(second.Item.Id, state.SelectedItemId);

        UiScene selectedScene = composer.Compose(invocation, interaction: interactions.Snapshot);
        UiLayoutSnapshot selectedLayout = new UiSceneLayoutEngine(new CountingPlatform())
            .Build(selectedScene, new UiRect(0, 0, 420, 180));
        Assert.True(selectedLayout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? selectedWindow));
        UiVirtualizedItemLayout selected = Assert.Single(selectedWindow!.Items, item => item.Item.Id == second.Item.Id);
        Assert.True(selected.Selected);
        UiAccessibilityNodeSnapshot selectedAccessibility = Assert.Single(
            AccessibilityNodes(new UiAccessibilitySnapshotBuilder()
                .Build(selectedScene, selectedLayout, interactions.Snapshot).Root),
            node => node.Id == selected.Node);
        Assert.True(selectedAccessibility.Selected);
        Assert.True(selectedAccessibility.Focused);
        Assert.Contains(
            new UiSceneRenderPlanner().Build(selectedScene, selectedLayout).Primitives.OfType<UiSurfacePrimitive>(),
            primitive => primitive.Node == selected.Node);

        var controller = new UiInteractionSession(scene, layout);
        Assert.True(controller.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(window.Items[0].Node, controller.Snapshot.Focused);
        Assert.True(controller.MoveFocus(UiNavigationDirection.Down).Consumed);
        Assert.Equal(window.Items[1].Node, controller.Snapshot.Focused);
        Assert.True(controller.Submit().Consumed);
        Assert.Equal(window.Items[1].Item.Id, state.SelectedItemId);
    }

    [Fact]
    public void WideGalleryAdaptsColumnsWhileCompactFallsBackToList()
    {
        UiSymbolId id = RegistryTests.Id("adaptive-gallery");
        int[] values = Enumerable.Range(0, 100).ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Adaptive gallery")
            .Browse("Items", new UiCollectionSource<int>(
                values,
                item => id.Child($"item/{item}"),
                item => $"Item {item}"))
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Adaptive gallery", () => experience)
            .Freeze();
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        var invocation = new UiInvocationService(registry);
        var engine = new UiSceneLayoutEngine(new CountingPlatform());
        UiScene wideScene = composer.Compose(invocation.Invoke(id, UiPresentationProfiles.Wide));
        UiScene compactScene = composer.Compose(invocation.Invoke(id, UiPresentationProfiles.Compact));
        UiCollectionSceneNode wideCollection = Assert.Single(Nodes(wideScene.Root).OfType<UiCollectionSceneNode>());
        UiCollectionSceneNode compactCollection = Assert.Single(Nodes(compactScene.Root).OfType<UiCollectionSceneNode>());
        UiLayoutSnapshot wide = engine.Build(wideScene, new UiRect(0, 0, 640, 240));
        UiLayoutSnapshot compact = engine.Build(compactScene, new UiRect(0, 0, 320, 240));

        Assert.True(wide.TryGetCollection(wideCollection.Id, out UiCollectionLayoutWindow? wideWindow));
        Assert.True(compact.TryGetCollection(compactCollection.Id, out UiCollectionLayoutWindow? compactWindow));
        Assert.True(wideWindow!.Columns > 1);
        Assert.Equal(1, compactWindow!.Columns);
        Assert.True(wideWindow.Items.Select(item => item.Bounds.X).Distinct().Count() > 1);
        Assert.Single(compactWindow.Items.Select(item => item.Bounds.X).Distinct());
        Assert.True(wideWindow.Items.Count < values.Length);
        Assert.True(compactWindow.Items.Count < values.Length);
    }

    [Fact]
    public void UniformListKeepsOneRecipeOwnedItemExtent()
    {
        CollectionFixture fixture = ListFixture(
            new UiCollectionSource<int>(
                Enumerable.Range(0, 40).ToArray(),
                item => RegistryTests.Id($"uniform/item/{item}"),
                item => $"Item {item}",
                item => item % 2 == 0 ? "Supporting text" : null),
            "Uniform");
        var platform = new CountingPlatform();
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(platform)
            .Build(fixture.Scene, new UiRect(0, 0, 360, 240));
        UiCollectionSceneNode collection = Assert.Single(Nodes(fixture.Scene.Root).OfType<UiCollectionSceneNode>());

        Assert.True(layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));
        Assert.NotNull(window);
        Assert.Equal(1, window.Columns);
        Assert.Single(window.Items.Select(item => item.Bounds.Height).Distinct());
        Assert.All(window.Items, item => Assert.Equal(window.ItemExtent, item.Bounds.Height));
        Assert.Equal(0, platform.WrapCalls);
    }

    [Fact]
    public void AdaptiveListWrapsSupportingTextAndUsesExactItemHeights()
    {
        string[] supporting =
        {
            "Short supporting text.",
            string.Join(" ", Enumerable.Repeat("Long localized supporting content", 18)),
            "Another short line."
        };
        CollectionFixture fixture = ListFixture(
            new UiCollectionSource<int>(
                Enumerable.Range(0, supporting.Length).ToArray(),
                item => RegistryTests.Id($"adaptive/item/{item}"),
                item => $"Item {item}",
                item => supporting[item]),
            "Adaptive");
        var platform = new CountingPlatform();
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(platform)
            .Build(fixture.Scene, new UiRect(0, 0, 320, 240));
        UiCollectionSceneNode collection = Assert.Single(Nodes(fixture.Scene.Root).OfType<UiCollectionSceneNode>());

        Assert.True(layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));
        Assert.NotNull(window);
        Assert.True(platform.WrapCalls > 0);
        Assert.True(window.Items.Select(item => item.Bounds.Height).Distinct().Count() > 1);
        Assert.All(window.Items, item => Assert.NotNull(item.SupportingBounds));
        Assert.Contains(
            new UiSceneRenderPlanner().Build(fixture.Scene, layout).Primitives.OfType<UiTextPrimitive>(),
            primitive => primitive.Overflow == UiTextOverflow.Wrap);
    }

    [Fact]
    public void SelectionStateDoesNotChangeAdaptiveMeasureGeometry()
    {
        UiSymbolId owner = RegistryTests.Id("adaptive-selection");
        var source = new UiSelectableCollectionState<int>(
            Enumerable.Range(0, 12).ToArray(),
            item => owner.Child($"item/{item}"),
            item => $"Item {item}",
            supportingText: item => string.Join(" ", Enumerable.Repeat($"Detail {item}", item % 3 + 1)));
        CollectionFixture fixture = ListFixture(source, "Adaptive", owner);
        var engine = new UiSceneLayoutEngine(new CountingPlatform());
        UiLayoutSnapshot before = engine.Build(fixture.Scene, new UiRect(0, 0, 360, 240));
        UiCollectionSceneNode beforeCollection = Assert.Single(Nodes(fixture.Scene.Root).OfType<UiCollectionSceneNode>());
        Assert.True(before.TryGetCollection(beforeCollection.Id, out UiCollectionLayoutWindow? beforeWindow));

        Assert.True(source.TrySelect(owner.Child("item/1")));
        UiScene selectedScene = fixture.Composer.Compose(fixture.Invocation, locale: "en-US");
        UiLayoutSnapshot after = engine.Build(selectedScene, new UiRect(0, 0, 360, 240));
        UiCollectionSceneNode afterCollection = Assert.Single(Nodes(selectedScene.Root).OfType<UiCollectionSceneNode>());
        Assert.True(after.TryGetCollection(afterCollection.Id, out UiCollectionLayoutWindow? afterWindow));

        Assert.Equal(
            beforeWindow!.Items.Select(item => (item.Item.Id, item.Bounds)).ToArray(),
            afterWindow!.Items.Select(item => (item.Item.Id, item.Bounds)).ToArray());
        Assert.Contains(afterWindow.Items, item => item.Selected);
    }

    [Fact]
    public void CollectionSelectionFocusAndCheckedRecipesCannotChangeGeometry()
    {
        UiSymbolId id = RegistryTests.Id("collection-state-geometry");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Collection state geometry")
            .Browse("Items", new UiCollectionSource<int>(
                new[] { 1, 2 },
                item => id.Child($"item/{item}")))
            .VisualRole("Items")
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Collection state geometry", () => experience)
            .Freeze();
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiCompilationResult compilation = new UiCompiler(catalog).Compile(@"visual Collection

Items@Checked
    padding = Space.M
", experience.CreateBindingContext(), "CollectionStateGeometry#visual");
        Assert.True(compilation.IsValid);
        UiVisualDefinition visual = Assert.IsType<UiVisualDefinition>(compilation.Definition);
        UiInvocationResult invocation = new UiInvocationService(
                registry,
                planner: new UiPresentationPlanner(catalog))
            .Invoke(id, UiPresentationProfiles.Wide);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new UiSceneComposer(UiThemePresets.Dark(), registry, catalog)
                .Compose(invocation, visual));
        Assert.Contains("must be render-only", error.Message);
    }

    [Fact]
    public void AdaptiveHeightRefinementPreservesStableScrollAnchor()
    {
        UiSymbolId owner = RegistryTests.Id("adaptive-anchor");
        CollectionFixture fixture = ListFixture(
            new UiCollectionSource<int>(
                Enumerable.Range(0, 1_000).ToArray(),
                item => owner.Child($"item/{item}"),
                item => $"Item {item}",
                item => string.Join(" ", Enumerable.Repeat($"Supporting {item}", item % 7 + 1))),
            "Adaptive",
            owner);
        var platform = new CountingPlatform();
        var runtime = new UiHostRuntimeSession(
            fixture.Scene,
            new UiRect(0, 0, 360, 240),
            platform);
        UiCollectionSceneNode collection = Assert.Single(Nodes(fixture.Scene.Root).OfType<UiCollectionSceneNode>());
        runtime.ScrollCollection(collection.Id, 4_000);
        Assert.True(runtime.Layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? before));
        Assert.NotNull(before?.Anchor);
        UiSymbolId anchored = before!.Anchor!.Item;
        float beforeY = Assert.Single(before.Items, item => item.Item.Id == anchored).Bounds.Y;

        UiScene localized = fixture.Composer.Compose(fixture.Invocation, locale: "ru-RU");
        UiHostUpdate update = runtime.Update(localized, new UiRect(0, 0, 360, 240));
        Assert.True(update.LayoutChanged);
        Assert.True(runtime.Layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? after));
        Assert.Equal(anchored, after!.Anchor!.Item);
        float afterY = Assert.Single(after.Items, item => item.Item.Id == anchored).Bounds.Y;
        Assert.InRange(Math.Abs(afterY - beforeY), 0, 0.02f);
    }

    [Fact]
    public void WidthLocaleAndThemeMetricsInvalidateOnlyMatchingMeasurements()
    {
        UiSymbolId owner = RegistryTests.Id("adaptive-measurement-key");
        var source = new UiCollectionSource<int>(
            Enumerable.Range(0, 80).ToArray(),
            item => owner.Child($"item/{item}"),
            item => $"Item {item} {new string('W', 80)}",
            item => string.Join(" ", Enumerable.Repeat("Supporting content", item % 5 + 1)));
        CollectionFixture fixture = ListFixture(source, "Adaptive", owner);
        var platform = new CountingPlatform();
        var engine = new UiSceneLayoutEngine(platform);

        engine.Build(fixture.Scene, new UiRect(0, 0, 360, 240));
        int initialWraps = platform.WrapCalls;
        engine.Build(fixture.Scene, new UiRect(0, 0, 360, 240));
        Assert.Equal(initialWraps, platform.WrapCalls);

        engine.Build(fixture.Scene, new UiRect(0, 0, 300, 240));
        int afterWidth = platform.WrapCalls;
        Assert.True(afterWidth > initialWraps);

        UiScene localized = fixture.Composer.Compose(fixture.Invocation, locale: "ru-RU");
        engine.Build(localized, new UiRect(0, 0, 300, 240));
        int afterLocale = platform.WrapCalls;
        Assert.True(afterLocale > afterWidth);

        UiTheme largeTypography = new UiThemeBuilder(UiThemePresets.Dark())
            .Set(UiThemeTokens.TypographyBody, new UiTypography("Body", 19, 1.3f))
            .Build(RegistryTests.Id("theme/large-metrics"));
        var themedComposer = new UiSceneComposer(largeTypography, fixture.Registry, fixture.Catalog);
        UiScene themed = themedComposer.Compose(fixture.Invocation, locale: "ru-RU");
        engine.Build(themed, new UiRect(0, 0, 300, 240));
        Assert.True(platform.WrapCalls > afterLocale);
    }

    [Fact]
    public void AdaptiveSteadyStateReusesExactRowsWithinTheBoundedHeightScope()
    {
        UiSymbolId owner = RegistryTests.Id("adaptive-exact-row-reuse");
        var source = new CountingCollectionSource(owner, 10_000);
        CollectionFixture fixture = ListFixture(source, "Adaptive", owner);
        var engine = new UiSceneLayoutEngine(new CountingPlatform());
        var viewport = new UiRect(0, 0, 360, 240);

        UiLayoutSnapshot initial = engine.Build(fixture.Scene, viewport);
        UiCollectionSceneNode collection = Assert.Single(Nodes(fixture.Scene.Root).OfType<UiCollectionSceneNode>());
        Assert.True(initial.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? initialWindow));
        int callsAfterInitialLayout = source.GetItemCalls;

        UiLayoutSnapshot steady = engine.Build(fixture.Scene, viewport);
        Assert.True(steady.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? steadyWindow));

        Assert.Equal(initialWindow!.Items.Count, steadyWindow!.Items.Count);
        int expectedNonRowReads = Math.Min(source.Count, 8) + 1;
        Assert.Equal(
            steadyWindow.Items.Count + expectedNonRowReads,
            source.GetItemCalls - callsAfterInitialLayout);
    }

    [Fact]
    public void TenThousandItemsAreNotFullyMaterializedOrMeasured()
    {
        UiSymbolId owner = RegistryTests.Id("lazy-ten-thousand");
        var source = new CountingCollectionSource(owner, 10_000);
        CollectionFixture fixture = ListFixture(source, "Adaptive", owner);

        Assert.Equal(0, source.GetItemCalls);
        var platform = new CountingPlatform();
        UiLayoutSnapshot layout = new UiSceneLayoutEngine(platform)
            .Build(fixture.Scene, new UiRect(0, 0, 360, 240));
        UiCollectionSceneNode collection = Assert.Single(Nodes(fixture.Scene.Root).OfType<UiCollectionSceneNode>());
        Assert.True(layout.TryGetCollection(collection.Id, out UiCollectionLayoutWindow? window));

        Assert.InRange(source.GetItemCalls, 1, 99);
        Assert.InRange(platform.MeasureCalls, 1, 99);
        Assert.InRange(window!.Items.Count, 1, 99);
        Assert.Equal(10_000, window.TotalCount);
    }

    [Fact]
    public void AdaptiveListFailsClosedWithoutStableIdLookupCapability()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ListFixture(
                new UnindexedCollectionSource(RegistryTests.Id("unindexed-adaptive")),
                "Adaptive",
                RegistryTests.Id("unindexed-adaptive")));

        Assert.Contains("stable-ID lookup capability", error.Message);
    }

    [Fact]
    public void SelectionChangeInvalidatesRenderWithoutRelayout()
    {
        UiSymbolId id = RegistryTests.Id("selection-reconciliation");
        var state = new UiSelectableCollectionState<int>(
            new[] { 1, 2 },
            item => id.Child($"item/{item}"));
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Selection reconciliation")
            .Select("Items", state)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Selection reconciliation", () => experience)
            .Freeze();
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry);
        UiInvocationResult invocation = new UiInvocationService(registry)
            .Invoke(id, UiPresentationProfiles.Wide);
        UiScene before = composer.Compose(invocation);

        Assert.True(state.TrySelect(id.Child("item/2")));
        UiScene after = composer.Compose(invocation);
        UiSceneDiff diff = new UiSceneReconciler().Compare(before, after);

        Assert.Equal(UiPropertyEffects.Render, diff.Effects);
        Assert.False(diff.RequiresLayout);
        Assert.True(diff.RequiresRender);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(1)]
    public void ExternalLookupCannotRedirectAnAnchorToAnotherOrMissingItem(int returnedIndex)
    {
        UiSymbolId owner = RegistryTests.Id("external-corrupt");
        var source = new CorruptLookupSource(owner, returnedIndex);
        var fixture = ListFixture(source, "Adaptive", owner);
        var collection = Assert.Single(Nodes(fixture.Scene.Root).OfType<UiCollectionSceneNode>());
        Assert.Throws<InvalidOperationException>(() => collection.TryGetIndex(owner.Child("0"), -1, out _));
    }

    private sealed class CorruptLookupSource : IUiSemanticCollectionSource,
        IUiSemanticSource<IReadOnlyList<int>>, IUiSemanticCollectionMetadata
    {
        private readonly UiSymbolId _owner;
        private readonly int _index;
        internal CorruptLookupSource(UiSymbolId owner, int index) { _owner = owner; _index = index; }
        public int Count => 2;
        public long Revision => 1;
        public bool HasSupportingText => true;
        public IReadOnlyList<int> Value => Array.Empty<int>();
        public Type ValueType => typeof(IReadOnlyList<int>);
        public object UntypedValue => Value;
        public event Action? Changed { add { } remove { } }
        public UiSemanticCollectionItem GetItem(int index) => new(_owner.Child(index.ToString()), "Item", index);
        public bool TryGetIndex(UiSymbolId item, out int index) { index = _index; return true; }
    }

    private static UiScene Scene(IEnumerable<int> values)
    {
        UiSymbolId id = RegistryTests.Id("virtualized-window");
        int[] snapshot = values.ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Virtualized")
            .Browse("Items", new UiCollectionSource<int>(
                snapshot,
                item => id.Child($"item/{item}"),
                item => $"Item {item}"))
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Virtualized", () => experience)
            .Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
    }

    private static CollectionFixture ListFixture(
        IUiSemanticSource<IReadOnlyList<int>> source,
        string itemSizing,
        UiSymbolId? requestedId = null)
    {
        UiSymbolId id = requestedId ?? RegistryTests.Id($"list-{itemSizing.ToLowerInvariant()}");
        UiExperienceDefinition experience = new UiExperienceBuilder(id, $"{itemSizing} list")
            .Browse("Items", source)
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, $"{itemSizing} list", () => experience)
            .Freeze();
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiCompilationResult compilation = new UiCompiler(catalog).Compile($@"presentation List

Items
    view = List
    itemSizing = {itemSizing}
", experience.CreateBindingContext(), $"{itemSizing}List#presentation");
        Assert.True(compilation.IsValid, string.Join("\n", compilation.Diagnostics.Select(item => item.Message)));
        UiPresentationDefinition presentation = Assert.IsType<UiPresentationDefinition>(compilation.Definition);
        UiInvocationResult invocation = new UiInvocationService(
                registry,
                planner: new UiPresentationPlanner(catalog))
            .Invoke(id, UiPresentationProfiles.Wide, presentation);
        var composer = new UiSceneComposer(UiThemePresets.Dark(), registry, catalog);
        UiScene scene = composer.Compose(invocation, locale: "en-US");
        return new CollectionFixture(scene, invocation, composer, registry, catalog);
    }

    private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in Nodes(child))
            yield return descendant;
    }

    private static IEnumerable<UiAccessibilityNodeSnapshot> AccessibilityNodes(UiAccessibilityNodeSnapshot node)
    {
        yield return node;
        foreach (UiAccessibilityNodeSnapshot child in node.Children)
        foreach (UiAccessibilityNodeSnapshot descendant in AccessibilityNodes(child))
            yield return descendant;
    }

    private sealed class CountingPlatform : IUiPlatformBridge
    {
        public int MeasureCalls { get; private set; }
        public int WrapCalls { get; private set; }

        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            MeasureCalls++;
            if (overflow == UiTextOverflow.Wrap) WrapCalls++;
            float characterWidth = typography.Size * 0.6f;
            int lines = overflow == UiTextOverflow.Wrap
                ? Math.Max(1, (int)MathF.Ceiling(text.Length * characterWidth / Math.Max(1, availableWidth)))
                : 1;
            return new UiSize(
                Math.Min(text.Length * characterWidth, availableWidth),
                typography.Size * typography.LineHeight * lines);
        }

        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }

    private sealed class CountingCollectionSource :
        IUiSemanticCollectionSource,
        IUiSemanticSource<IReadOnlyList<int>>,
        IUiSemanticCollectionMetadata
    {
        private readonly UiSymbolId _owner;

        public CountingCollectionSource(UiSymbolId owner, int count)
        {
            _owner = owner;
            Count = count;
        }

        public int Count { get; }
        public int GetItemCalls { get; private set; }
        public IReadOnlyList<int> Value => Array.Empty<int>();
        public Type ValueType => typeof(IReadOnlyList<int>);
        public object UntypedValue => Value;
        long IUiSemanticCollectionMetadata.Revision => 0;
        bool IUiSemanticCollectionMetadata.HasSupportingText => true;
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public UiSemanticCollectionItem GetItem(int index)
        {
            GetItemCalls++;
            return new UiSemanticCollectionItem(
                _owner.Child($"item/{index}"),
                $"Item {index}",
                index,
                string.Join(" ", Enumerable.Repeat("Supporting", index % 4 + 1)),
                index);
        }

        bool IUiSemanticCollectionMetadata.TryGetIndex(UiSymbolId item, out int index)
        {
            string prefix = _owner.LocalId + "/item/";
            if (string.Equals(item.Scope, _owner.Scope, StringComparison.Ordinal) &&
                item.LocalId.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(item.LocalId[prefix.Length..], out index) &&
                (uint)index < (uint)Count)
                return true;
            index = -1;
            return false;
        }
    }

    private sealed class UnindexedCollectionSource :
        IUiSemanticCollectionSource,
        IUiSemanticSource<IReadOnlyList<int>>
    {
        private readonly UiSymbolId _owner;

        public UnindexedCollectionSource(UiSymbolId owner) => _owner = owner;

        public int Count => 1;
        public IReadOnlyList<int> Value => new[] { 1 };
        public Type ValueType => typeof(IReadOnlyList<int>);
        public object UntypedValue => Value;
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public UiSemanticCollectionItem GetItem(int index)
            => index == 0
                ? new UiSemanticCollectionItem(_owner.Child("item/1"), "Item 1", 1)
                : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private sealed record CollectionFixture(
        UiScene Scene,
        UiInvocationResult Invocation,
        UiSceneComposer Composer,
        UiRegistrySnapshot Registry,
        UiSemanticCatalog Catalog);
}
