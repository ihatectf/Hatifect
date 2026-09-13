using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class CollectionIndexChangeTests
{
    [Fact]
    public void CapturedChangesExcludeLaterLivePublication()
    {
        using var fixture = new Fixture();
        fixture.Update(1, "First");
        var captured = Assert.IsAssignableFrom<IUiCollectionIndexChangeSnapshot>(fixture.Source.CaptureSnapshot());
        fixture.Update(2, "Later");
        var changes = new List<UiCollectionIndexChange>();

        Assert.True(captured.TryVisitIndexChanges(0, changes.Add));

        Assert.Equal(1, captured.Version);
        Assert.Equal(2, fixture.Source.Version);
        Assert.Equal(new(UiCollectionIndexChangeKind.Update, 1), Assert.Single(changes));
    }

    [Fact]
    public void MixedBatchPreservesSequentialCandidateIndices()
    {
        using var fixture = new Fixture();
        UiCollectionOperation<Row>[] operations =
        {
            new UiCollectionUpdate<Row>(1, fixture.Rows[1].Id, fixture.Rows[1] with { Label = "Updated" }),
            new UiCollectionInsert<Row>(2, new(fixture.Owner.Child("item/new"), "Inserted")),
            new UiCollectionMove<Row>(0, 3, fixture.Rows[0].Id),
            new UiCollectionRemove<Row>(4, fixture.Rows[3].Id)
        };
        Assert.Equal(UiPublicationStatus.Committed, fixture.Publication.BeginUpdate().Apply(fixture.Source,
            new UiCollectionChange<Row>(0, 1, operations, null)).Commit().Status);
        var snapshot = Assert.IsAssignableFrom<IUiCollectionIndexChangeSnapshot>(fixture.Source.CaptureSnapshot());
        var changes = new List<UiCollectionIndexChange>();

        Assert.True(snapshot.TryVisitIndexChanges(0, changes.Add));

        Assert.Equal(new[]
        {
            new UiCollectionIndexChange(UiCollectionIndexChangeKind.Update, 1),
            new UiCollectionIndexChange(UiCollectionIndexChangeKind.Insert, 2),
            new UiCollectionIndexChange(UiCollectionIndexChangeKind.Move, 0, 3),
            new UiCollectionIndexChange(UiCollectionIndexChangeKind.Remove, 4)
        }, changes);
        Assert.Equal(new[] { fixture.Rows[1].Id, fixture.Owner.Child("item/new"), fixture.Rows[2].Id, fixture.Rows[0].Id },
            fixture.Source.Value.Select(row => row.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingHistoryOrResetEmitsNoPartialChanges(bool reset)
    {
        using var fixture = new Fixture(historyCapacity: 1);
        fixture.Update(0, "First");
        if (reset) fixture.Source.Replace(fixture.Rows.Reverse().ToArray());
        else fixture.Update(1, "Second");
        var snapshot = Assert.IsAssignableFrom<IUiCollectionIndexChangeSnapshot>(fixture.Source.CaptureSnapshot());
        int callbacks = 0;

        Assert.False(snapshot.TryVisitIndexChanges(reset ? 1 : 0, _ => callbacks++));

        Assert.Equal(0, callbacks);
        Assert.Equal(2, snapshot.Version);
    }

    [Fact]
    public void CurrentVersionHasNoChangesAndInvalidVersionsAreUnknown()
    {
        using var fixture = new Fixture();
        var snapshot = Assert.IsAssignableFrom<IUiCollectionIndexChangeSnapshot>(fixture.Source.CaptureSnapshot());
        int callbacks = 0;

        Assert.True(snapshot.TryVisitIndexChanges(0, _ => callbacks++));
        Assert.False(snapshot.TryVisitIndexChanges(-1, _ => callbacks++));
        Assert.False(snapshot.TryVisitIndexChanges(1, _ => callbacks++));

        Assert.Equal(0, callbacks);
    }

    private sealed record Row(UiSymbolId Id, string Label);
    private sealed class Fixture : IDisposable
    {
        internal readonly UiSymbolId Owner = new("test", "captured-delta");
        internal readonly UiPublication Publication;
        internal readonly UiPublishedCollection<Row> Source;
        internal readonly Row[] Rows;
        internal Fixture(int historyCapacity = 64)
        {
            Publication = new(Owner);
            Rows = Enumerable.Range(0, 4).Select(index => new Row(Owner.Child("item/" + index), "Item " + index)).ToArray();
            Source = Publication.Collection(Owner.Child("rows"), Rows,
                UiSourceTypes.Scalar<Row>(Owner.Child("type"), false), row => row.Id, row => row.Label,
                historyCapacity: historyCapacity);
        }
        internal void Update(int index, string label)
        {
            long version = Source.Version;
            Assert.Equal(UiPublicationStatus.Committed, Publication.BeginUpdate().Apply(Source,
                new UiCollectionChange<Row>(version, version + 1, new UiCollectionOperation<Row>[]
                { new UiCollectionUpdate<Row>(index, Rows[index].Id, Rows[index] with { Label = label }) }, null)).Commit().Status);
        }
        public void Dispose() => Publication.Dispose();
    }
}
