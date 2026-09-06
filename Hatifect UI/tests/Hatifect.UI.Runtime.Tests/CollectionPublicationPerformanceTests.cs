using System.Diagnostics;
using Hatifect.UI.Experience;
using Xunit;
using Xunit.Abstractions;

namespace Hatifect.UI.Runtime.Tests;

[Collection(SemanticRuntimePerformanceCollection.CollectionName)]
public sealed class CollectionPublicationPerformanceTests
{
    private const int Samples = 600;
    private readonly ITestOutputHelper _output;
    public CollectionPublicationPerformanceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(100, false)]
    [InlineData(1000, false)]
    [InlineData(10000, false)]
    [InlineData(100, true)]
    [InlineData(1000, true)]
    [InlineData(10000, true)]
    [Trait("Category", "performance")]
    public void ColdResetAndDeltaPreparationReportTheirActualCostAndRetainBoundedHistory(int count, bool reset)
    {
        var owner = RegistryTests.Id("performance/preparation");
        using var publication = new UiPublication(owner);
        var values = Enumerable.Range(0, count)
            .Select(index => new Row(owner.Child("item/" + index), "Item " + index, 0)).ToArray();
        var reversed = values.Reverse().ToArray();
        var modified = values[count / 2] with { Payload = 1 };
        var source = publication.Collection(owner.Child("rows"), values,
            UiSourceTypes.Scalar<Row>(owner.Child("type/row"), false), value => value.Id, value => value.Label);
        var first = source.CaptureSnapshot();
        var unchanged = first.GetItem(0);
        for (int index = 0; index < 100; index++) Publish(index);

        var elapsed = new double[Samples];
        int committed = 0;
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Samples; index++)
        {
            long started = Stopwatch.GetTimestamp();
            if (Publish(index).Status == UiPublicationStatus.Committed) committed++;
            elapsed[index] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        Array.Sort(elapsed);
        _output.WriteLine($"publication-preparation operation={(reset ? "reset-reorder" : "delta-update")} items={count} samples={Samples} " +
            $"p95Ms={elapsed[(int)(Samples * .95) - 1]:F6} p99Ms={elapsed[(int)(Samples * .99) - 1]:F6} allocatedBytesPerPublication={bytes / (double)Samples:F2}");

        Assert.Equal(Samples, committed);
        Assert.Equal(700, source.Version);
        Assert.Equal(count, source.Count);
        Assert.Equal(count, first.Count);
        Assert.Equal(values[0], first.GetItem(0).Value);
        Assert.True(source.TryGetIndex(values[0].Id, out int currentIndex));
        Assert.Same(unchanged, source.GetItem(currentIndex));
        Assert.Equal(reset ? values[^1] : values[0], source.Value[0]);
        Assert.Equal(reset ? 0 : 1, source.Value[reset ? count - 1 - count / 2 : count / 2].Payload);
        var history = source.ReadChanges(source.Version - (reset ? 1 : 64));
        Assert.Equal(reset ? 1 : 64, history.Changes.Count);
        Assert.All(history.Changes, change => Assert.Single(change.Operations));
        Assert.True(Assert.Single(source.ReadChanges(0).Changes).IsReset);
        Assert.True(double.IsFinite(elapsed[^1]));
        Assert.True(bytes > 0);
        // These are cold preparation allocations, not steady-state frame allocations. The
        // unchanged 2/4 ms and 16 KiB frame budgets remain enforced by the steady runtime gates.

        UiPublicationResult Publish(int iteration)
        {
            long version = source.Version;
            UiCollectionOperation<Row> operation = reset
                ? new UiCollectionReset<Row>(iteration % 2 == 0 ? values : reversed)
                : new UiCollectionUpdate<Row>(count / 2, modified.Id, iteration % 2 == 0 ? values[count / 2] : modified);
            return publication.BeginUpdate().Apply(source,
                new UiCollectionChange<Row>(version, version + 1, new[] { operation }, null)).Commit();
        }
    }

    private sealed record Row(UiSymbolId Id, string Label, int Payload);
}
