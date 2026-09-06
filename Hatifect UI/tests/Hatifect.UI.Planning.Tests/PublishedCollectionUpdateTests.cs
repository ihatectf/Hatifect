using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class PublishedCollectionUpdateTests
{
    private static readonly UiSymbolId Owner = new("PublishedUpdates.Tests", "screen");
    private static UiSymbolId Id(int index) => Owner.Child("item/" + index);
    private static readonly UiSourceType<Row> RowType = UiSourceTypes.Scalar<Row>(Owner.Child("type/row"), false);
    private sealed record Row(int Id, string Label, int Payload = 0, string? Supporting = null, UiSymbolId? Icon = null);

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void FirstUpdateProjectsOnlyTheChangedRowAndRetainsTheCapturedView(int count)
    {
        using var publication = new UiPublication(Owner);
        var original = Enumerable.Range(0, count).Select(index => new Row(index, "Item " + index)).ToArray();
        int identities = 0, labels = 0, supporting = 0, icons = 0;
        var rows = publication.SelectableCollection(Owner.Child("rows"), original, RowType,
            row => { identities++; return Id(row.Id); }, row => { labels++; return row.Label; }, Id(count - 1),
            row => { supporting++; return row.Supporting; }, row => { icons++; return row.Icon; });
        var before = rows.CaptureSnapshot();
        identities = labels = supporting = icons = 0;
        int changed = count / 2;
        var replacement = original[changed] with { Payload = 1, Icon = Owner.Child("icon/updated") };

        Assert.Equal(UiPublicationStatus.Committed, Apply(publication, rows,
            new[] { new UiCollectionUpdate<Row>(changed, Id(changed), replacement) }, Id(count - 1)).Status);

        Assert.Equal((1, 1, 1, 1), (identities, labels, supporting, icons));
        Assert.Equal(1, rows.Version);
        Assert.Equal(1, rows.Revision);
        Assert.Equal(0, before.Version);
        Assert.Same(original[changed], before.GetItem(changed).Value);
        Assert.Same(replacement, rows.Value[changed]);
        Assert.Equal(before.GetItem(changed).ContentVersion, rows.GetItem(changed).ContentVersion);
        Assert.Equal(1, rows.GetItem(changed).ItemRevision);
        Assert.Equal(replacement.Icon, rows.GetItem(changed).Icon);
        Assert.Equal(Id(count - 1), rows.SelectedItemId);
        for (int index = 0; index < count; index++)
        {
            Assert.True(rows.TryGetIndex(Id(index), out int found));
            Assert.Equal(index, found);
            if (index != changed) Assert.Same(before.GetItem(index), rows.GetItem(index));
        }
    }

    [Fact]
    public void MaximumBatchProjectsFinalValuesInIndexOrderAndCopiesOnlyItsOwnStorage()
    {
        using var publication = new UiPublication(Owner);
        var original = Enumerable.Range(0, 10000).Select(index => new Row(index, "old")).ToArray();
        var projected = new List<int>();
        var rows = publication.Collection(Owner.Child("rows"), original, RowType, row => Id(row.Id),
            row => { projected.Add(row.Id); return row.Label; });
        var before = rows.CaptureSnapshot();
        projected.Clear();
        int[] indices = { 9999, 256, 255, 128, 127, 0 };
        var operations = Enumerable.Range(0, UiCollectionChange<Row>.MaximumOperations)
            .Select(iteration => new UiCollectionUpdate<Row>(indices[iteration % indices.Length],
                Id(indices[iteration % indices.Length]), new Row(indices[iteration % indices.Length], "new", iteration))).ToArray();
        Assert.True(Apply(publication, rows, operations).Succeeded);
        Assert.Equal(indices.OrderBy(index => index), projected);
        foreach (int index in indices)
        {
            Assert.Equal(operations.Last(operation => operation.Index == index).Value, rows.Value[index]);
            Assert.Same(original[index], before.GetItem(index).Value);
            Assert.Equal(1, rows.GetItem(index).ItemRevision);
        }
        Assert.Same(before.GetItem(129), rows.GetItem(129));
        var captured = rows.CaptureSnapshot();
        Assert.True(Apply(publication, rows, new[] { new UiCollectionUpdate<Row>(127, Id(127), new Row(127, "later")) }).Succeeded);
        Assert.Equal("new", captured.GetItem(127).Label);
        Assert.Equal("later", rows.GetItem(127).Label);
        Assert.Same(captured.GetItem(128), rows.GetItem(128));
        Assert.Equal(128, rows.ReadChanges(0).Changes[0].Operations.Count);
    }

    [Fact]
    public void EquivalentUpdatesAdvanceVersionAndHistoryWhileKeepingOriginalStorage()
    {
        using var publication = new UiPublication(Owner);
        var original = new Row(0, "same");
        var rows = publication.Collection(Owner.Child("rows"), new[] { original }, RowType, row => Id(row.Id), row => row.Label);
        var before = rows.CaptureSnapshot();
        var change = new UiCollectionChange<Row>(0, 1, new UiCollectionOperation<Row>[]
        { new UiCollectionUpdate<Row>(0, Id(0), original with { Payload = 1 }), new UiCollectionUpdate<Row>(0, Id(0), original with { }) }, null);
        int events = 0;
        rows.Changed += () => events++;
        Assert.Equal(UiPublicationStatus.Committed, publication.BeginUpdate().Apply(rows, change).Commit().Status);
        Assert.Same(before.UntypedValue, rows.UntypedValue);
        Assert.Same(before.GetItem(0), rows.GetItem(0));
        Assert.Equal(0, rows.Revision);
        Assert.Equal(1, rows.Version);
        Assert.Equal(1, events);
        Assert.Same(change, Assert.Single(rows.ReadChanges(0).Changes));
        Assert.Equal(UiPublicationStatus.Unchanged, publication.BeginUpdate().Apply(rows, change).Commit().Status);
        Assert.Equal(1, events);
        Assert.True(Apply(publication, rows, new[] { new UiCollectionUpdate<Row>(0, Id(0), original with { Payload = 2 }) }).Succeeded);
        Assert.Equal(2, rows.Version);
        Assert.Equal(1, rows.Revision);
        Assert.Equal(2, rows.GetItem(0).ItemRevision);
    }

    [Fact]
    public void MixedEquivalentAndChangedUpdatesPreserveTypedValueAndItemReuseSemantics()
    {
        using var publication = new UiPublication(Owner);
        var original = new Row(0, "same");
        var equivalent = original with { };
        var rows = publication.Collection(Owner.Child("rows"), new[] { original, new Row(1, "old") }, RowType, row => Id(row.Id), row => row.Label);
        var before = rows.CaptureSnapshot();
        Assert.True(Apply(publication, rows, new[] { new UiCollectionUpdate<Row>(0, Id(0), equivalent),
            new UiCollectionUpdate<Row>(1, Id(1), new Row(1, "changed")) }).Succeeded);
        Assert.Same(equivalent, rows.Value[0]);
        Assert.Same(original, rows.GetItem(0).Value);
        Assert.Same(before.GetItem(0), rows.GetItem(0));
        Assert.Equal("changed", rows.GetItem(1).Label);
        Assert.NotEqual(before.GetItem(1).ContentVersion, rows.GetItem(1).ContentVersion);
    }

    [Fact]
    public void SupportingMetadataTracksLastSupportingRowAcrossUpdateSelectionAndReset()
    {
        using var publication = new UiPublication(Owner);
        var values = new[] { new Row(0, "zero", Supporting: "support"), new Row(1, "one") };
        var rows = publication.SelectableCollection(Owner.Child("rows"), values, RowType, row => Id(row.Id), row => row.Label,
            supportingText: row => row.Supporting);
        var before = rows.CaptureSnapshot();
        Assert.True(Apply(publication, rows, new[] { new UiCollectionUpdate<Row>(0, Id(0), values[0] with { Supporting = "  " }) }).Succeeded);
        Assert.False(rows.HasSupportingText);
        Assert.True(before.HasSupportingText);
        Assert.True(rows.TrySelect(Id(1)));
        Assert.False(rows.HasSupportingText);
        Assert.True(Apply(publication, rows, new[] { new UiCollectionUpdate<Row>(1, Id(1), values[1] with { Supporting = "new" }) }, Id(1)).Succeeded);
        Assert.True(rows.HasSupportingText);
        rows.Replace(new[] { values[0] with { Supporting = null } });
        Assert.False(rows.HasSupportingText);
        Assert.Null(rows.SelectedItemId);
        Assert.True(Apply(publication, rows, new[] { new UiCollectionUpdate<Row>(0, Id(0), values[0]) }).Succeeded);
        Assert.True(rows.HasSupportingText);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("identity")]
    [InlineData("address")]
    [InlineData("index")]
    [InlineData("icon")]
    [InlineData("projection")]
    [InlineData("selection")]
    public void InvalidUpdatePoisonsOtherSourcesEvenWhenALaterOperationWouldRepairIt(string failure)
    {
        using var publication = new UiPublication(Owner);
        var original = new Row(0, "original");
        int projected = 0;
        var rows = publication.SelectableCollection(Owner.Child("rows"), new[] { original }, RowType, row => Id(row.Id),
            row => { projected++; return row.Label == "throw" ? throw new ApplicationException("projection failed") : row.Label; },
            icon: row => row.Icon);
        var status = publication.State(Owner.Child("status"), "old", UiSourceTypes.String);
        var before = publication.Capture();
        projected = 0;
        var bad = failure switch
        {
            "null" => null!, "identity" => original with { Id = 1 },
            "icon" => original with { Icon = default(UiSymbolId) },
            "projection" => original with { Label = "throw" }, _ => original
        };
        var operations = new List<UiCollectionOperation<Row>>
        { new UiCollectionUpdate<Row>(failure == "index" ? 1 : 0, failure == "address" ? Id(1) : Id(0), bad) };
        // Projection applies to final values; address/identity/null validity applies to every operation.
        if (failure is "null" or "identity" or "address" or "index")
            operations.Add(new UiCollectionUpdate<Row>(0, Id(0), original));
        int events = 0;
        publication.Changed += () => events++;
        rows.Changed += () => events++;
        status.Changed += () => events++;
        var result = publication.BeginUpdate().Set(status, "new").Apply(rows,
            new(0, 1, operations, failure == "selection" ? Id(9) : null)).Commit();
        Assert.Equal(UiPublicationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UIP010" && diagnostic.Source == rows.SourceId);
        Assert.Same(before, publication.Capture());
        Assert.Equal("old", status.Value);
        Assert.Equal(0, events);
        Assert.Equal(0, rows.Version);
        if (failure is "null" or "identity" or "address" or "index") Assert.Equal(0, projected);
    }

    [Fact]
    public void NullableItemUpdateAndEmptyExplicitSelectionChangeKeepTheirDeclaredContracts()
    {
        using var publication = new UiPublication(Owner);
        var rows = publication.SelectableCollection(Owner.Child("rows"), new Row?[] { new(0, "old") },
            UiSourceTypes.Scalar<Row?>(Owner.Child("type/row"), true), row => Id(row?.Id ?? 0), row => row?.Label ?? "empty");
        Assert.True(publication.BeginUpdate().Apply(rows,
            new UiCollectionChange<Row?>(0, 1, new[] { new UiCollectionUpdate<Row?>(0, Id(0), null) }, Id(0))).Commit().Succeeded);
        Assert.Null(rows.Value[0]);
        Assert.Equal("empty", rows.GetItem(0).Label);
        var before = rows.CaptureSnapshot();
        Assert.True(publication.BeginUpdate().Apply(rows,
            new UiCollectionChange<Row?>(1, 2, Array.Empty<UiCollectionOperation<Row?>>(), null)).Commit().Succeeded);
        Assert.Same(before.UntypedValue, rows.UntypedValue);
        Assert.Same(before.GetItem(0), rows.GetItem(0));
        Assert.Equal(2, rows.Version);
        Assert.Equal(1, rows.Revision);
        Assert.Null(rows.SelectedItemId);
    }

    private static UiPublicationResult Apply(UiPublication publication, UiPublishedCollection<Row> rows,
        IEnumerable<UiCollectionOperation<Row>> operations, UiSymbolId? selection = null)
        => publication.BeginUpdate().Apply(rows, new(rows.Version, rows.Version + 1, operations, selection)).Commit();
}
