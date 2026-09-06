using System;
using System.Linq;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class CollectionSnapshotTests
{
    private static UiSymbolId Id(int value) => new("Snapshot.Tests", "item/" + value);

    [Fact]
    public void CapturesRetainValuesOrderLookupAndSelectionAfterReplacement()
    {
        var values = new[] { 1, 2, 3 };
        var source = new UiSelectableCollectionState<int>(values, Id, selectedItemId: Id(2));
        IUiSemanticCollectionSnapshot first = source.CaptureSnapshot();
        int events = 0;
        first.Changed += () => events++;
        values[0] = 99;
        source.Replace(new[] { 3, 1 });
        IUiSemanticCollectionSnapshot second = source.CaptureSnapshot();

        Assert.Same(second, source.CaptureSnapshot());
        Assert.Equal(new[] { 1, 2, 3 }, Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<int>>(first.UntypedValue));
        Assert.True(first.TryGetIndex(Id(2), out int index));
        Assert.Equal(1, index);
        Assert.Equal(Id(2), first.GetItem(index).Id);
        Assert.Equal(Id(2), first.SelectedItemId);
        Assert.Null(second.SelectedItemId);
        Assert.False(second.TryGetIndex(Id(2), out _));
        Assert.Equal(0, first.Revision);
        Assert.Equal(1, second.Revision);
        Assert.Equal(0, events);
    }

    [Fact]
    public void SelectionChangesReusePayloadAndKeepContentRevision()
    {
        var source = new UiSelectableCollectionState<int>(new[] { 1, 2 }, Id);
        var first = source.CaptureSnapshot();
        source.TrySelect(Id(2));
        var selected = source.CaptureSnapshot();
        source.ClearSelection();
        var cleared = source.CaptureSnapshot();
        Assert.Null(first.SelectedItemId);
        Assert.Equal(Id(2), selected.SelectedItemId);
        Assert.Null(cleared.SelectedItemId);
        Assert.Same(first.UntypedValue, selected.UntypedValue);
        Assert.Same(first.GetItem(1), cleared.GetItem(1));
        Assert.Equal(first.Revision, selected.Revision);
        Assert.Equal(selected.Revision, cleared.Revision);
    }

    [Fact]
    public void FailedOrEquivalentReplacementDoesNotReplaceTheCapturedView()
    {
        var source = new UiCollectionState<int>(new[] { 1, 2 }, Id);
        var first = source.CaptureSnapshot();
        int changes = 0;
        source.Changed += () => changes++;
        Assert.Throws<InvalidOperationException>(() => source.Replace(new[] { 1, 1 }));
        source.Replace(new[] { 1, 2 });
        Assert.Same(first, source.CaptureSnapshot());
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ImmutableSourceCaptureRetainsItsExactPreparedItems()
    {
        var source = new UiCollectionSource<int>(Enumerable.Range(0, 100).ToArray(), Id);
        var snapshot = source.CaptureSnapshot();
        Assert.Same(snapshot, source.CaptureSnapshot());
        Assert.Same(source.Value, snapshot.UntypedValue);
        Assert.Same(source.GetItem(99), snapshot.GetItem(99));
        Assert.Null(snapshot.SelectedItemId);
    }

    [Fact]
    public void LegacySourcesExposeMonotonicVersionsWithoutChangingTheirNotificationContract()
    {
        var value = new UiState<string>("old");
        var versions = new System.Collections.Generic.List<long>();
        value.Changed += () => versions.Add(value.Version);
        value.Value = "new";
        value.Value = "new";
        value.Value = "old";
        Assert.Equal(new long[] { 1, 2 }, versions);
        Assert.Equal(0, new UiConstantSource<string>("constant").Version);
        Assert.Equal(0, new UiCollectionSource<int>(new[] { 1 }, Id).Version);

        var rows = new UiSelectableCollectionState<int>(new[] { 1, 2 }, Id);
        var first = rows.CaptureSnapshot();
        Assert.True(rows.TrySelect(Id(2)));
        var selected = rows.CaptureSnapshot();
        Assert.True(rows.TrySelect(Id(2)));
        Assert.False(rows.TrySelect(Id(9)));
        rows.Replace(new[] { 2, 1 });
        Assert.True(rows.ClearSelection());
        Assert.False(rows.ClearSelection());
        Assert.Equal(3, rows.Version);
        Assert.Equal(1, rows.CaptureSnapshot().Revision);
        Assert.Equal(0, Assert.IsAssignableFrom<IUiVersionedSemanticSource>(first).Version);
        Assert.Equal(1, Assert.IsAssignableFrom<IUiVersionedSemanticSource>(selected).Version);
        Assert.Equal(3, Assert.IsAssignableFrom<IUiVersionedSemanticSource>(rows.CaptureSnapshot()).Version);
    }

    [Fact]
    public void LegacyItemRevisionTracksPayloadChangesAndRetainsUnchangedItemsAcrossReorder()
    {
        var source = new UiCollectionState<(int Id, int Payload)>(new[] { (1, 1), (2, 2) },
            value => Id(value.Id), value => value.Id.ToString());
        var unchanged = source.GetItem(1);
        long measurement = source.GetItem(0).ContentVersion;
        source.Replace(new[] { (1, 10), (2, 2) });
        var updated = source.GetItem(0);
        Assert.Equal(1, updated.ItemRevision);
        Assert.Equal(measurement, updated.ContentVersion);
        Assert.Same(unchanged, source.GetItem(1));
        source.Replace(new[] { (2, 2), (1, 10) });
        Assert.Same(updated, source.GetItem(1));
        Assert.Equal(2, source.Version);
        Assert.Equal(2, source.CaptureSnapshot().Revision);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiSemanticCollectionItem(Id(1), "one", 1) { ItemRevision = -1 });
    }
}
