using System;
using System.Diagnostics;
using System.Linq;
using Hatifect.UI.Experience;
using Hatifect.UI.Planning;
using Hatifect.UI.Runtime.Invocation;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Registration;
using Hatifect.UI.Runtime.Rendering;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Theming;
using Hatifect.UI.Semantics;
using Xunit;
using Xunit.Abstractions;

namespace Hatifect.UI.Runtime.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SemanticRuntimePerformanceCollection
{
    public const string CollectionName = "Semantic runtime performance";
}

[Collection(SemanticRuntimePerformanceCollection.CollectionName)]
public sealed class SemanticRuntimePerformanceGateTests
{
    private const int MeasurementFrames = 600;
    private const double MaximumP95Milliseconds = 2;
    private const double MaximumP99Milliseconds = 4;
    private const double MaximumAllocatedBytesPerFrame = 16 * 1024;
    private readonly ITestOutputHelper _output;

    public SemanticRuntimePerformanceGateTests(ITestOutputHelper output)
        => _output = output;

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "performance")]
    public void CollectionCaptureReusesPreparedViewsWithinTheSteadyBudgets(int count)
    {
        UiSymbolId owner = RegistryTests.Id("performance/capture");
        var source = new UiSelectableCollectionState<int>(Enumerable.Range(0, count).ToArray(), value => owner.Child("item/" + value));
        IUiSemanticCollectionSnapshot first = source.CaptureSnapshot();
        var elapsed = new double[MeasurementFrames];
        int total = 0;
        // Warm the complete measured path, including the runtime timer initialization.
        for (int index = 0; index < 100; index++)
        {
            long started = Stopwatch.GetTimestamp();
            _ = source.CaptureSnapshot().Count;
            _ = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < MeasurementFrames; index++)
        {
            long started = Stopwatch.GetTimestamp();
            total += source.CaptureSnapshot().Count;
            elapsed[index] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(elapsed);
        double p95 = Percentile(elapsed, 0.95), p99 = Percentile(elapsed, 0.99);
        double bytes = allocated / (double)MeasurementFrames;
        _output.WriteLine($"collection-capture items={count} frames={MeasurementFrames} p95Ms={p95:F6} p99Ms={p99:F6} allocatedBytesPerCapture={bytes:F2}");
        Assert.Equal(count * MeasurementFrames, total);
        Assert.Same(first, source.CaptureSnapshot());
        Assert.Equal(0, allocated);
        Assert.True(p95 <= MaximumP95Milliseconds);
        Assert.True(p99 <= MaximumP99Milliseconds);
        Assert.True(bytes <= MaximumAllocatedBytesPerFrame);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "performance")]
    public void PublicationSelectionUsesBoundedDispatchWithoutReprojectingCollectionItems(int count)
    {
        UiSymbolId owner = RegistryTests.Id("performance/publication");
        using var publication = new UiPublication(owner);
        int projected = 0;
        var ids = Enumerable.Range(0, count).Select(value => owner.Child("item/" + value)).ToArray();
        var source = publication.SelectableCollection(owner.Child("rows"), Enumerable.Range(0, count).ToArray(),
            UiSourceTypes.Scalar<int>(owner.Child("type/int"), false), value => { projected++; return ids[value]; });
        for (int index = 0; index < 100; index++) source.TrySelect(ids[index % 2]);
        int projectedBefore = projected;
        var payload = source.Value;
        var elapsed = new double[MeasurementFrames];
        int succeeded = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < MeasurementFrames; index++)
        {
            long started = Stopwatch.GetTimestamp();
            if (source.TrySelect(ids[index % 2])) succeeded++;
            elapsed[index] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(elapsed);
        double p95 = Percentile(elapsed, .95), p99 = Percentile(elapsed, .99), bytes = allocated / (double)MeasurementFrames;
        _output.WriteLine($"publication-selection items={count} dispatches={MeasurementFrames} p95Ms={p95:F6} p99Ms={p99:F6} allocatedBytesPerDispatch={bytes:F2}");
        Assert.Equal(MeasurementFrames, succeeded);
        Assert.Equal(projectedBefore, projected);
        Assert.Same(payload, source.Value);
        Assert.Equal(700, source.Version);
        Assert.Equal(0, source.Revision);
        Assert.Equal(ids[1], source.SelectedItemId);
        Assert.True(p95 <= MaximumP95Milliseconds, $"p95 {p95:F3} ms exceeds budget.");
        Assert.True(p99 <= MaximumP99Milliseconds, $"p99 {p99:F3} ms exceeds budget.");
        Assert.True(bytes <= MaximumAllocatedBytesPerFrame, $"{bytes:F2} bytes per selection exceeds budget.");
    }

    [Fact]
    [Trait("Category", "performance")]
    public void TerminalLayoutReportsHostFreeLatencyAndAllocationEvidence()
    {
        UiScene scene = LayoutScene();
        var engine = new UiSceneLayoutEngine(new FixedMetrics());
        var viewport = new UiRect(0, 0, 960, 540);
        for (int index = 0; index < 100; index++) _ = engine.Build(scene, viewport);

        var elapsedMilliseconds = new double[MeasurementFrames];
        UiLayoutSnapshot layout = null!;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int frameIndex = 0; frameIndex < MeasurementFrames; frameIndex++)
        {
            long started = Stopwatch.GetTimestamp();
            layout = engine.Build(scene, viewport);
            _ = layout.CollectionWindows.Count;
            elapsedMilliseconds[frameIndex] =
                (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Array.Sort(elapsedMilliseconds);
        double p95 = Percentile(elapsedMilliseconds, 0.95);
        double p99 = Percentile(elapsedMilliseconds, 0.99);
        double bytesPerBuild = allocated / (double)MeasurementFrames;
        _output.WriteLine(
            $"terminal-layout frames={MeasurementFrames} p95Ms={p95:F6} " +
            $"p99Ms={p99:F6} allocatedBytesPerBuild={bytesPerBuild:F2}");

        Assert.True(double.IsFinite(p95));
        Assert.True(double.IsFinite(p99));
        Assert.True(bytesPerBuild >= 0);
        Assert.True(layout.TryGetEntry(scene.Root.Id, out UiLayoutEntry? rootEntry));
        Assert.NotNull(rootEntry);
        Assert.Equal(viewport, rootEntry.Bounds);
        Assert.Equal(viewport, rootEntry.Clip);
        foreach (UiSceneNode child in scene.Root.Children)
        {
            Assert.True(layout.TryGetEntry(child.Id, out UiLayoutEntry? childEntry));
            Assert.NotNull(childEntry);
            Assert.True(float.IsFinite(childEntry.Bounds.X));
            Assert.True(float.IsFinite(childEntry.Bounds.Y));
            Assert.True(float.IsFinite(childEntry.Bounds.Width));
            Assert.True(float.IsFinite(childEntry.Bounds.Height));
            Assert.True(childEntry.Bounds.Width >= 0);
            Assert.True(childEntry.Bounds.Height >= 0);
            Assert.True(childEntry.Clip.X >= rootEntry.Clip.X);
            Assert.True(childEntry.Clip.Y >= rootEntry.Clip.Y);
            Assert.True(childEntry.Clip.Right <= rootEntry.Clip.Right + 0.01f);
            Assert.True(childEntry.Clip.Bottom <= rootEntry.Clip.Bottom + 0.01f);
        }
    }

    [Fact]
    [Trait("Category", "performance")]
    public void SteadyCompositorMeetsTheHostFreeCpuAndAllocationBudgets()
    {
        UiRenderFrame frame = Frame(primitiveCount: 200);
        var backend = new CountingBackend();
        var compositor = new UiCompositor();
        for (int index = 0; index < 100; index++) compositor.Render(frame, backend);

        var elapsedMilliseconds = new double[MeasurementFrames];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int frameIndex = 0; frameIndex < MeasurementFrames; frameIndex++)
        {
            long started = Stopwatch.GetTimestamp();
            compositor.Render(frame, backend);
            elapsedMilliseconds[frameIndex] =
                (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Array.Sort(elapsedMilliseconds);
        double p95 = Percentile(elapsedMilliseconds, 0.95);
        double p99 = Percentile(elapsedMilliseconds, 0.99);
        double bytesPerFrame = allocated / (double)MeasurementFrames;

        Assert.True(p95 <= MaximumP95Milliseconds, $"p95 {p95:F3} ms exceeds {MaximumP95Milliseconds:F3} ms.");
        Assert.True(p99 <= MaximumP99Milliseconds, $"p99 {p99:F3} ms exceeds {MaximumP99Milliseconds:F3} ms.");
        Assert.True(
            bytesPerFrame <= MaximumAllocatedBytesPerFrame,
            $"Steady allocation {bytesPerFrame:F0} B/frame exceeds {MaximumAllocatedBytesPerFrame:F0} B/frame.");
        Assert.Equal((100 + MeasurementFrames) * frame.Primitives.Count, backend.DrawCount);
    }

    [Fact]
    [Trait("Category", "performance")]
    public void AdaptiveGridVirtualizationMeetsTheHostFreeCpuAllocationAndBoundsBudgets()
    {
        UiSymbolId id = RegistryTests.Id("performance/adaptive-grid");
        int[] values = Enumerable.Range(0, 50_000).ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Performance grid")
            .Browse("Items", new UiCollectionSource<int>(
                values,
                item => id.Child($"item/{item}"),
                item => $"Item {item}"))
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Performance grid", () => experience)
            .Freeze();
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
        UiCollectionSceneNode collection = Assert.Single(SceneNodes(scene.Root).OfType<UiCollectionSceneNode>());
        var viewport = new UiRect(0, 0, 640, 240);
        const float itemWidth = 120;
        const float itemExtent = 20;
        var virtualizer = new UiCollectionVirtualizer(new FixedMetrics());
        var typography = new UiTypography("Body", 16, 1.25f);
        for (int index = 0; index < 100; index++)
            _ = virtualizer.Materialize(
                collection,
                viewport,
                viewport,
                itemWidth,
                itemExtent,
                itemExtent,
                typography,
                scene.MeasurementContext,
                new UiCollectionViewportRequest(index * 137, null, 0));

        var elapsedMilliseconds = new double[MeasurementFrames];
        int maximumRealized = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int frameIndex = 0; frameIndex < MeasurementFrames; frameIndex++)
        {
            long started = Stopwatch.GetTimestamp();
            UiCollectionLayoutWindow window = virtualizer.Materialize(
                collection,
                viewport,
                viewport,
                itemWidth,
                itemExtent,
                itemExtent,
                typography,
                scene.MeasurementContext,
                new UiCollectionViewportRequest(frameIndex * 137, null, 0));
            elapsedMilliseconds[frameIndex] =
                (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            maximumRealized = Math.Max(maximumRealized, window.Items.Count);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Array.Sort(elapsedMilliseconds);
        double p95 = Percentile(elapsedMilliseconds, 0.95);
        double p99 = Percentile(elapsedMilliseconds, 0.99);
        double bytesPerFrame = allocated / (double)MeasurementFrames;
        int maximumRows = (int)MathF.Ceiling(viewport.Height / itemExtent) + 5;
        int columns = (int)(viewport.Width / itemWidth);

        Assert.True(p95 <= MaximumP95Milliseconds, $"p95 {p95:F3} ms exceeds {MaximumP95Milliseconds:F3} ms.");
        Assert.True(p99 <= MaximumP99Milliseconds, $"p99 {p99:F3} ms exceeds {MaximumP99Milliseconds:F3} ms.");
        Assert.True(
            bytesPerFrame <= MaximumAllocatedBytesPerFrame,
            $"Virtualization allocation {bytesPerFrame:F0} B/frame exceeds {MaximumAllocatedBytesPerFrame:F0} B/frame.");
        Assert.InRange(maximumRealized, 1, columns * maximumRows);
        Assert.True(maximumRealized < values.Length);
    }

    [Fact]
    [Trait("Category", "performance")]
    public void VariableHeightListMeetsTheHostFreeCpuAllocationAndBoundsBudgets()
    {
        UiSymbolId id = RegistryTests.Id("performance/variable-height-list");
        int[] values = Enumerable.Range(0, 50_000).ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Variable-height performance list")
            .Browse("Items", new UiCollectionSource<int>(
                values,
                item => id.Child($"item/{item}"),
                item => $"Item {item}",
                item => string.Join(" ", Enumerable.Repeat("Supporting content", item % 5 + 1))))
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .Window(id, "Variable-height performance list", () => experience)
            .Freeze();
        UiSemanticCatalog catalog = UiSemanticCatalog.CreateFoundation();
        UiCompilationResult compilation = new UiCompiler(catalog).Compile(@"presentation Performance

Items
    view = List
    itemSizing = Adaptive
", experience.CreateBindingContext(), "VariableHeightPerformance#presentation");
        Assert.True(compilation.IsValid);
        UiPresentationDefinition presentation = Assert.IsType<UiPresentationDefinition>(compilation.Definition);
        UiScene scene = new UiSceneComposer(UiThemePresets.Dark(), registry, catalog).Compose(
            new UiInvocationService(registry, planner: new UiPresentationPlanner(catalog))
                .Invoke(id, UiPresentationProfiles.Wide, presentation),
            locale: "en-US");
        UiCollectionSceneNode collection = Assert.Single(SceneNodes(scene.Root).OfType<UiCollectionSceneNode>());
        var viewport = new UiRect(0, 0, 640, 240);
        var virtualizer = new UiCollectionVirtualizer(new FixedMetrics());
        var typography = new UiTypography("Body", 16, 1.25f);
        const float estimate = 48;
        for (int index = 0; index < 100; index++)
            _ = virtualizer.Materialize(
                collection,
                viewport,
                viewport,
                viewport.Width,
                estimate,
                20,
                typography,
                scene.MeasurementContext,
                new UiCollectionViewportRequest(index * 113, null, 0));

        var elapsedMilliseconds = new double[MeasurementFrames];
        int maximumRealized = 0;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int frameIndex = 0; frameIndex < MeasurementFrames; frameIndex++)
        {
            long started = Stopwatch.GetTimestamp();
            UiCollectionLayoutWindow window = virtualizer.Materialize(
                collection,
                viewport,
                viewport,
                viewport.Width,
                estimate,
                20,
                typography,
                scene.MeasurementContext,
                new UiCollectionViewportRequest(frameIndex * 113, null, 0));
            elapsedMilliseconds[frameIndex] =
                (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            maximumRealized = Math.Max(maximumRealized, window.Items.Count);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Array.Sort(elapsedMilliseconds);
        double p95 = Percentile(elapsedMilliseconds, 0.95);
        double p99 = Percentile(elapsedMilliseconds, 0.99);
        double bytesPerFrame = allocated / (double)MeasurementFrames;

        Assert.True(p95 <= MaximumP95Milliseconds, $"p95 {p95:F3} ms exceeds {MaximumP95Milliseconds:F3} ms.");
        Assert.True(p99 <= MaximumP99Milliseconds, $"p99 {p99:F3} ms exceeds {MaximumP99Milliseconds:F3} ms.");
        Assert.True(
            bytesPerFrame <= MaximumAllocatedBytesPerFrame,
            $"Variable-height allocation {bytesPerFrame:F0} B/frame exceeds {MaximumAllocatedBytesPerFrame:F0} B/frame.");
        Assert.InRange(maximumRealized, 1, 32);
        Assert.True(maximumRealized < values.Length);
    }

    private static UiRenderFrame Frame(int primitiveCount)
    {
        UiSurface surface = UiSurface.Solid(UiColor.FromRgb(0x202530));
        UiRenderPrimitive[] primitives = Enumerable.Range(0, primitiveCount)
            .Select(index =>
            {
                var bounds = new UiRect(index % 20 * 16, index / 20 * 16, 14, 14);
                return (UiRenderPrimitive)new UiSurfacePrimitive(
                    RegistryTests.Id($"performance/node/{index}"),
                    bounds,
                    bounds,
                    surface,
                    new UiCornerRadius(2),
                    null,
                    null,
                    new UiOpacity(1),
                    new UiTransform(0, 0));
            })
            .ToArray();
        return new UiRenderFrame(primitives);
    }

    private static UiScene LayoutScene()
    {
        UiSymbolId id = RegistryTests.Id("performance/terminal-layout");
        int[] values = Enumerable.Range(0, 128).ToArray();
        UiExperienceDefinition experience = new UiExperienceBuilder(id, "Layout performance")
            .Search("Search", new UiState<string>(string.Empty))
            .Browse("Items", new UiCollectionSource<int>(
                values,
                item => id.Child($"item/{item}"),
                item => $"Item {item}"))
            .Actions(
                "Actions",
                new UiActionDefinition(id.Child("action/primary"), "Apply", () => { }))
            .VisualRole("Action.Primary")
            .Build();
        UiRegistrySnapshot registry = new UiRegistryBuilder()
            .TerminalSection(id, "Layout performance", () => experience)
            .Freeze();
        return new UiSceneComposer(UiThemePresets.Dark(), registry).Compose(
            new UiInvocationService(registry).Invoke(id, UiPresentationProfiles.Wide));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static System.Collections.Generic.IEnumerable<UiSceneNode> SceneNodes(UiSceneNode node)
    {
        yield return node;
        foreach (UiSceneNode child in node.Children)
        foreach (UiSceneNode descendant in SceneNodes(child))
            yield return descendant;
    }

    private sealed class CountingBackend : IUiRenderBackend
    {
        public int DrawCount { get; private set; }
        public void DrawSurface(UiSurfacePrimitive surface) => DrawCount++;
        public void DrawText(UiTextPrimitive text) => DrawCount++;
    }

    private sealed class FixedMetrics : IUiTextMetrics
    {
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            int lines = overflow == UiTextOverflow.Wrap
                ? Math.Max(1, (int)MathF.Ceiling(text.Length * 8 / Math.Max(1, availableWidth)))
                : 1;
            return new UiSize(
                Math.Min(text.Length * 8, availableWidth),
                typography.Size * typography.LineHeight * lines);
        }
    }
}
