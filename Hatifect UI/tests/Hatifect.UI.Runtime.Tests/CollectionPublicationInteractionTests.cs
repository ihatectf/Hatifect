using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Input;
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
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Hatifect.UI.Runtime.Tests;

[Collection(SemanticRuntimePerformanceCollection.CollectionName)]
public sealed class CollectionPublicationInteractionTests
{
    private readonly ITestOutputHelper _output;
    public CollectionPublicationInteractionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LogicalAndVisibleFocusOrderUseTheSameActionEligibilityObservation()
    {
        var owner = RegistryTests.Id("focus-eligibility");
        int queries = 0;
        var action = new UiActionDefinition(owner.Child("action"), "Run", () => { }, () => ++queries == 1);
        var experience = new UiExperienceBuilder(owner, "Actions").Actions("Commands", action).Build();
        var registry = new UiRegistryBuilder().Window(owner, "Actions", () => experience).Freeze();
        var scene = new UiSceneComposer(UiThemePresets.Dark(), registry)
            .Compose(new UiInvocationService(registry).Invoke(owner, UiPresentationProfiles.Wide));
        var layout = new UiSceneLayoutEngine(new Platform()).Build(scene, new UiRect(0, 0, 360, 240));
        queries = 0;

        var interactions = new UiInteractionSession(scene, layout);

        var expected = Assert.Single(interactions.CaptureDiagnostics().FocusOrder);
        Assert.Equal(1, queries);
        Assert.True(interactions.MoveFocus(UiNavigationDirection.Next).Consumed);
        Assert.Equal(expected, interactions.Snapshot.Focused);
        queries = 0;
        interactions.Reconcile(scene, layout);
        Assert.Equal(1, queries);
        Assert.Equal(expected, interactions.Snapshot.Focused);
    }

    [Theory]
    [InlineData("Uniform", 100)]
    [InlineData("Uniform", 1000)]
    [InlineData("Uniform", 10000)]
    [InlineData("Adaptive", 100)]
    [InlineData("Adaptive", 1000)]
    [InlineData("Adaptive", 10000)]
    [Trait("Category", "performance")]
    public void ColdPredecessorSearchReportsTheWorstRemovalTransitionAndLeavesSteadyRenderingBounded(string sizing, int count)
    {
        using var fixture = new Fixture(sizing, count: count);
        int[] original = Enumerable.Range(0, count).ToArray();
        int[] replacement = new[] { 0 }.Concat(Enumerable.Range(count, count - 1)).ToArray();
        const int samples = 600;
        var elapsed = new double[samples];
        long allocated = 0;
        int maximumItems = 0;
        for (int sample = -10; sample < samples; sample++)
        {
            if (sample != -10) fixture.Replace(original);
            fixture.Runtime.ScrollCollection(fixture.Collection, 10_000_000);
            Assert.True(fixture.Window.AnchorIndex > count / 2);
            UiScene next = fixture.Prepare(replacement);
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            fixture.Update(next);
            double duration = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            long used = GC.GetAllocatedBytesForCurrentThread() - bytes;
            if (sample >= 0) { elapsed[sample] = duration; allocated += used; }
            Assert.Equal(fixture.Item(0), fixture.Window.Anchor!.Item);
            maximumItems = Math.Max(maximumItems, fixture.Window.Items.Count);
        }
        Array.Sort(elapsed);
        _output.WriteLine($"removed-anchor sizing={sizing} oldItems={count} samples={samples} " +
            $"p95Ms={elapsed[569]:F6} p99Ms={elapsed[593]:F6} allocatedBytesPerTransition={allocated / (double)samples:F2} maximumMaterialized={maximumItems}");
        Assert.InRange(maximumItems, 1, 30);
        long layoutBuilds = fixture.Runtime.Performance.LayoutBuilds;
        for (int sample = 0; sample < 100; sample++) fixture.Runtime.Render();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int sample = 0; sample < samples; sample++) fixture.Runtime.Render();
        long steadyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, steadyBytes);
        Assert.Equal(layoutBuilds, fixture.Runtime.Performance.LayoutBuilds);
        Assert.True(double.IsFinite(elapsed[^1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UniformUnindexedSourceRevealsTheKnownLogicalIndexWithoutScanning(bool sequential)
    {
        UiNavigationDirection direction = sequential ? UiNavigationDirection.Next : UiNavigationDirection.Down;
        using var fixture = new Fixture("Uniform", unindexed: true);
        fixture.Focus(3);
        fixture.Runtime.ScrollCollection(fixture.Collection, 1800);
        int reads = fixture.Unindexed!.ItemReads;

        Assert.True(fixture.Host.MoveFocus(direction).Consumed);
        Assert.Equal(fixture.Item(4), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Contains(fixture.Window.Items, item => item.Item.Id == fixture.Item(4) && item.Clip.Height > 0);
        Assert.InRange(fixture.Unindexed.ItemReads - reads, 1, 80);
        Assert.True(fixture.Host.MoveFocus(direction).Consumed);
        Assert.Equal(fixture.Item(5), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Null(fixture.Source.SelectedItemId);
    }

    [Fact]
    public void UniformUnindexedRemovalKeepsTheFallbackIndexAcrossRepeatedReconciliation()
    {
        using var fixture = new Fixture("Uniform", unindexed: true);
        fixture.Focus(99);
        fixture.Runtime.ScrollCollection(fixture.Collection, -100_000);
        fixture.Replace(Enumerable.Range(0, 99));

        Assert.Equal(fixture.Item(98), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.DoesNotContain(fixture.Window.Items, item => item.Item.Id == fixture.Item(98));
        fixture.Refresh();
        Assert.Equal(fixture.Item(98), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Previous).Consumed);
        Assert.Equal(fixture.Item(97), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Contains(fixture.Window.Items, item => item.Item.Id == fixture.Item(97) && item.Clip.Height > 0);
        Assert.Null(fixture.Source.SelectedItemId);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void GridAnchorSurvivesAColumnChangeAndTheFollowingRemoval(string sizing)
    {
        using var fixture = new Fixture(sizing, view: "Gallery");
        fixture.Runtime.ScrollCollection(fixture.Collection, 700);
        var before = fixture.Window;
        Assert.True(before.Columns > 1);
        int anchored = Assert.Single(before.Items, item => item.Item.Id == before.Anchor!.Item).Index;
        Assert.True(anchored > 0);
        var order = Enumerable.Range(0, 100).ToArray();
        (order[anchored], order[anchored + 1]) = (order[anchored + 1], order[anchored]);

        fixture.Replace(order);

        Assert.Equal(before.Anchor!.Item, fixture.Window.Anchor!.Item);
        Assert.Equal(anchored + 1, fixture.Window.AnchorIndex);
        fixture.Replace(order.Where(value => value != anchored));
        Assert.Equal(fixture.Item(anchored + 1), fixture.Window.Anchor!.Item);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void ReorderKeepsFocusAndAnchorEvenWhenFocusMovesOutsideTheViewport(string sizing)
    {
        using var fixture = new Fixture(sizing);
        fixture.Focus(4);
        fixture.Runtime.ScrollCollection(fixture.Collection, 400);
        var before = fixture.Window;
        var anchor = before.Anchor!;
        float beforeY = Assert.Single(before.Items, item => item.Item.Id == anchor.Item).Bounds.Y;

        fixture.Replace(Enumerable.Range(50, 50).Concat(Enumerable.Range(0, 50)));

        Assert.Equal(fixture.Item(4), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.DoesNotContain(fixture.Window.Items, item => item.Item.Id == fixture.Item(4));
        Assert.Equal(anchor.Item, fixture.Window.Anchor!.Item);
        float afterY = Assert.Single(fixture.Window.Items, item => item.Item.Id == anchor.Item).Bounds.Y;
        Assert.InRange(Math.Abs(beforeY - afterY), 0, .02f);
        Assert.Null(fixture.Source.SelectedItemId);
    }

    [Theory]
    [InlineData("Uniform", 3, 4)]
    [InlineData("Adaptive", 3, 4)]
    [InlineData("Uniform", 99, 98)]
    [InlineData("Adaptive", 99, 98)]
    public void RemovedFocusUsesItsOldIndexWithoutSelectingItsReplacement(string sizing, int removed, int expected)
    {
        using var fixture = new Fixture(sizing);
        fixture.Focus(removed);
        Assert.True(fixture.Source.TrySelect(fixture.Item(removed)));

        fixture.Replace(Enumerable.Range(0, 100).Where(value => value != removed));

        Assert.Equal(fixture.Item(expected), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Null(fixture.Source.SelectedItemId);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void EmptyCollectionHasPositiveFocusableContainerAndRefillRestoresFirstItem(string sizing)
    {
        using var fixture = new Fixture(sizing);
        fixture.Focus(4);
        fixture.Replace(Array.Empty<int>());

        Assert.Equal(fixture.Collection, fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.True(fixture.Runtime.Layout.TryGetEntry(fixture.Collection, out var entry));
        Assert.True(entry!.ContentBounds.Width > 0);
        Assert.True(entry.ContentBounds.Height > 0);
        Assert.Contains(fixture.Collection, fixture.Runtime.Interactions.CaptureDiagnostics().FocusOrder);
        Assert.False(fixture.Host.Submit().Interaction!.Consumed);
        Assert.Null(fixture.Source.SelectedItemId);
        Assert.Empty(fixture.Window.Items);

        fixture.Replace(new[] { 80, 70 });

        Assert.Equal(fixture.Item(80), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.DoesNotContain(fixture.Collection, fixture.Runtime.Interactions.CaptureDiagnostics().FocusOrder);
        Assert.Null(fixture.Source.SelectedItemId);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void RemovingPressedItemCancelsReleaseInsteadOfActivatingTheFocusFallback(string sizing)
    {
        using var fixture = new Fixture(sizing);
        var target = fixture.Window.Items[1];
        var point = new UiPoint(target.Bounds.X + 1, target.Bounds.Y + 1);
        Assert.True(fixture.Host.PressPointer(point).Consumed);

        fixture.Replace(Enumerable.Range(0, 100).Where(value => value != 1));

        Assert.Equal(fixture.Item(2), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Null(fixture.Runtime.Interactions.Snapshot.Pressed);
        Assert.Null(fixture.Runtime.Interactions.Snapshot.Hovered);
        fixture.Host.ReleasePointer(point);
        Assert.Null(fixture.Source.SelectedItemId);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void RemovedAnchorUsesAnOldSurvivingPredecessorInsteadOfANewInsertion(string sizing)
    {
        using var fixture = new Fixture(sizing);
        fixture.Runtime.ScrollCollection(fixture.Collection, 600);
        var before = fixture.Window;
        int removed = Assert.Single(before.Items, item => item.Item.Id == before.Anchor!.Item).Index;
        Assert.True(removed >= 3);
        int predecessor = removed - 3;
        // Two old predecessors disappear too. The insertion occupies the old anchor index.
        var next = Enumerable.Range(0, predecessor + 1).Concat(new[] { 101, 102, 103 })
            .Concat(Enumerable.Range(removed + 1, 99 - removed));

        fixture.Replace(next);

        Assert.Equal(fixture.Item(predecessor), fixture.Window.Anchor!.Item);
        Assert.InRange(Math.Abs(before.Anchor!.LocalOffset - fixture.Window.Anchor.LocalOffset), 0, .02f);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void ResetWithoutSurvivingPredecessorStartsAtTheFirstNewItem(string sizing)
    {
        using var fixture = new Fixture(sizing);
        fixture.Runtime.ScrollCollection(fixture.Collection, 600);
        fixture.Replace(Enumerable.Range(200, 100));
        Assert.Equal(fixture.Item(200), fixture.Window.Anchor!.Item);
        Assert.Equal(0, fixture.Window.ScrollOffset);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void PassiveWheelKeepsFocusOffscreenAndExplicitNavigationRevealsItsLogicalNeighbour(string sizing)
    {
        using var fixture = new Fixture(sizing);
        fixture.Focus(3);
        fixture.Runtime.ScrollCollection(fixture.Collection, 1800);
        float passiveOffset = fixture.Window.ScrollOffset;
        fixture.Refresh();
        Assert.Equal(fixture.Item(3), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Equal(passiveOffset, fixture.Window.ScrollOffset);
        Assert.DoesNotContain(fixture.Window.Items, item => item.Item.Id == fixture.Item(3));

        Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Down).Consumed);

        Assert.Equal(fixture.Item(4), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.True(fixture.Window.ScrollOffset < passiveOffset);
        var target = Assert.Single(fixture.Window.Items, item => item.Item.Id == fixture.Item(4));
        Assert.True(target.Clip.Height > 0);
        Assert.InRange(Math.Abs(target.Bounds.Height - target.Clip.Height), 0, .02f);
        Assert.Null(fixture.Source.SelectedItemId);
        Assert.True(fixture.Host.Submit().Consumed);
        Assert.Equal(fixture.Item(4), fixture.Source.SelectedItemId);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Adaptive")]
    public void SequentialNavigationCrossesVirtualWindowsAndReversesWithoutSelecting(string sizing)
    {
        using var fixture = new Fixture(sizing);
        for (int index = 0; index < 75; index++)
        {
            Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Next).Consumed);
            Assert.Equal(fixture.Item(index), fixture.Runtime.Interactions.Snapshot.Focused);
            Assert.Contains(fixture.Window.Items, item => item.Item.Id == fixture.Item(index) && item.Clip.Height > 0);
        }
        Assert.True(fixture.Window.ScrollOffset > 0);
        Assert.True(fixture.Host.MoveFocus(UiNavigationDirection.Previous).Consumed);
        Assert.Equal(fixture.Item(73), fixture.Runtime.Interactions.Snapshot.Focused);
        Assert.Null(fixture.Source.SelectedItemId);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly UiSymbolId Owner = RegistryTests.Id("publication-interaction");
        private static readonly UiRect Viewport = new(0, 0, 360, 240);
        private readonly UiPublication _publication = new(Owner);
        private readonly UiSceneComposer _composer;
        private readonly UiInvocationResult _invocation;

        public Fixture(string sizing, bool unindexed = false, string view = "List", int count = 100)
        {
            Source = _publication.SelectableCollection(Owner.Child("rows"), Enumerable.Range(0, count).ToArray(),
                UiSourceTypes.Scalar<int>(Owner.Child("type/int"), false), Item, value => "Item " + value,
                supportingText: value => string.Join(" ", Enumerable.Repeat("Supporting", value % 3 + 1)));
            Unindexed = unindexed ? new UnindexedSource(Source) : null;
            IUiSemanticSource<IReadOnlyList<int>> presented = Unindexed is null ? Source : Unindexed;
            var experience = new UiExperienceBuilder(Owner, "Collection").Select("Items", presented).Build();
            var registry = new UiRegistryBuilder().Window(Owner, "Collection", () => experience).Freeze();
            var catalog = UiSemanticCatalog.CreateFoundation();
            var compilation = new UiCompiler(catalog).Compile(
                "presentation List\n\nItems\n    view = " + view + "\n    itemSizing = " + sizing + "\n",
                experience.CreateBindingContext(), "Collection#presentation");
            Assert.True(compilation.IsValid, string.Join("\n", compilation.Diagnostics.Select(item => item.Message)));
            var presentation = Assert.IsType<UiPresentationDefinition>(compilation.Definition);
            _invocation = new UiInvocationService(registry, planner: new UiPresentationPlanner(catalog))
                .Invoke(Owner, UiPresentationProfiles.Wide, presentation);
            _composer = new UiSceneComposer(UiThemePresets.Dark(), registry, catalog);
            var scene = _composer.Compose(_invocation);
            Collection = Assert.Single(Nodes(scene.Root).OfType<UiCollectionSceneNode>()).Id;
            Host = new UiPortalHostSession(scene, new UiHostPlacementContext(Viewport), new Platform(),
                composeInteraction: snapshot => _composer.Compose(_invocation, interaction: snapshot));
        }

        public UiPublishedSelectableCollection<int> Source { get; }
        public UnindexedSource? Unindexed { get; }
        public UiPortalHostSession Host { get; }
        public UiHostRuntimeSession Runtime => Host.Root;
        public UiSymbolId Collection { get; }
        public UiSymbolId Item(int value) => Owner.Child("item/" + value);
        public UiCollectionLayoutWindow Window
        {
            get
            {
                Assert.True(Runtime.Layout.TryGetCollection(Collection, out var window));
                return window!;
            }
        }

        public void Focus(int index)
        {
            for (int current = 0; current <= index; current++)
                Assert.True(Host.MoveFocus(UiNavigationDirection.Next).Consumed);
            Assert.Equal(Item(index), Runtime.Interactions.Snapshot.Focused);
        }

        public void Replace(IEnumerable<int> values)
        {
            Update(Prepare(values));
        }

        public UiScene Prepare(IEnumerable<int> values)
        {
            Assert.Equal(UiPublicationStatus.Committed, _publication.BeginUpdate().Replace(Source, values.ToArray()).Commit().Status);
            return _composer.Compose(_invocation, interaction: Runtime.Interactions.Snapshot);
        }

        public void Update(UiScene scene) => Runtime.Update(scene, Viewport);

        public void Refresh() => Runtime.Update(_composer.Compose(_invocation, interaction: Runtime.Interactions.Snapshot), Viewport);
        public void Dispose() => _publication.Dispose();

        private static IEnumerable<UiSceneNode> Nodes(UiSceneNode node)
        {
            yield return node;
            foreach (var child in node.Children)
            foreach (var descendant in Nodes(child)) yield return descendant;
        }
    }

    private sealed class UnindexedSource : IUiSemanticSource<IReadOnlyList<int>>, IUiSelectableCollectionSource
    {
        private readonly UiPublishedSelectableCollection<int> _source;
        public UnindexedSource(UiPublishedSelectableCollection<int> source) => _source = source;
        public int ItemReads { get; private set; }
        public IReadOnlyList<int> Value => _source.Value;
        public object UntypedValue => Value;
        public Type ValueType => typeof(IReadOnlyList<int>);
        public int Count => _source.Count;
        public UiSymbolId? SelectedItemId => _source.SelectedItemId;
        public bool TrySelect(UiSymbolId item) => _source.TrySelect(item);
        public UiSemanticCollectionItem GetItem(int index) { ItemReads++; return _source.GetItem(index); }
        public event Action? Changed { add => _source.Changed += value; remove => _source.Changed -= value; }
    }

    private sealed class Platform : IUiPlatformBridge
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            float width = text.Length * typography.Size * .6f;
            int lines = overflow == UiTextOverflow.Wrap ? Math.Max(1, (int)MathF.Ceiling(width / Math.Max(1, availableWidth))) : 1;
            return new UiSize(Math.Min(width, availableWidth), typography.Size * typography.LineHeight * lines);
        }
        public void DrawSurface(UiSurfacePrimitive surface) { }
        public void DrawText(UiTextPrimitive text) { }
    }
}
