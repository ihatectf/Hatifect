using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hatifect.UI.Experience;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class PublishedCollectionTests
{
    private static readonly UiSymbolId Owner = new("PublishedCollection.Tests", "screen");
    private static UiSymbolId Id(int id) => Owner.Child("item/" + id);
    private static readonly UiSourceType<Row> RowType = UiSourceTypes.Scalar<Row>(Owner.Child("type/row"), false);
    private sealed record Row(int Id, string Text, int Payload = 0, UiSymbolId? Icon = null);

    [Fact]
    public void CollectionRemovalAndDetailsClearAreVisibleAsOneBatch()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one"), new Row(2, "two") }, Id(2));
        var details = publication.State(Owner.Child("details"), "two", UiSourceTypes.String);
        var old = publication.Capture();
        var before = rows.CaptureSnapshot();
        var observed = new List<(int, UiSymbolId?, string)>();
        void Observe() => observed.Add((rows.Count, rows.SelectedItemId, details.Value));
        rows.Changed += Observe;
        details.Changed += Observe;
        publication.Changed += Observe;

        var batch = publication.BeginUpdate().Replace(rows, new[] { new Row(1, "one") }).Set(details, "");
        Assert.Equal(2, rows.Count);
        Assert.Null(batch.Read(rows).SelectedItemId);
        var result = batch.Commit();
        Assert.Equal(UiPublicationStatus.Committed, result.Status);
        Assert.Equal(3, observed.Count);
        Assert.All(observed, state => Assert.Equal((1, (UiSymbolId?)null, ""), state));
        Assert.Same(before, rows.Read(old));
        Assert.Equal(2, before.Count);
        Assert.Equal(Id(2), before.SelectedItemId);
        Assert.Equal("two", details.Read(old));
        Assert.Equal(1, rows.Version);
        Assert.Equal(1, rows.Revision);
        var selectedSource = new UiSelectionSource(rows);
        Assert.Equal(0, Assert.IsAssignableFrom<IUiVersionedSemanticSource>(selectedSource.ReadSnapshot(old)).Version);
        Assert.Equal(1, Assert.IsAssignableFrom<IUiVersionedSemanticSource>(selectedSource.ReadSnapshot(publication.Capture())).Version);
    }

    [Fact]
    public void ReplacementAndExplicitFallbackNotifyOnlyOnceAndSelectionReusesContent()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one"), new Row(2, "two") }, Id(1));
        int events = 0;
        rows.Changed += () => events++;
        var batch = publication.BeginUpdate().Replace(rows, new[] { new Row(2, "two"), new Row(3, "three") }).Select(rows, Id(3));
        Assert.True(batch.Commit().Succeeded);
        var previous = rows.CaptureSnapshot();
        Assert.Equal(1, events);
        Assert.Equal(Id(3), rows.SelectedItemId);
        Assert.True(rows.TrySelect(Id(2)));
        var selected = rows.CaptureSnapshot();
        Assert.Equal(2, rows.Version);
        Assert.Equal(1, rows.Revision);
        Assert.Same(previous.UntypedValue, selected.UntypedValue);
        Assert.Same(previous.GetItem(0), selected.GetItem(0));
        Assert.Equal(Id(3), previous.SelectedItemId);
        Assert.Equal(Id(2), selected.SelectedItemId);
        Assert.Empty(Assert.Single(rows.ReadChanges(1).Changes).Operations);
        Assert.Equal(Id(2), Assert.Single(rows.ReadChanges(1).Changes).SelectedItemId);
    }

    [Fact]
    public void FailedReplacementOrSelectionRollsBackOtherStagedSources()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one") }, Id(1));
        var status = publication.State(Owner.Child("status"), "old", UiSourceTypes.String);
        var before = publication.Capture();
        var duplicate = publication.BeginUpdate().Set(status, "new").Replace(rows, new[] { new Row(2, "two"), new Row(2, "duplicate") }).Commit();
        var missingSelection = publication.BeginUpdate().Set(status, "new").Select(rows, Id(9)).Commit();
        Assert.Equal(UiPublicationStatus.Invalid, duplicate.Status);
        Assert.Equal(UiPublicationStatus.Invalid, missingSelection.Status);
        Assert.All(duplicate.Diagnostics, diagnostic => Assert.Equal(rows.SourceId, diagnostic.Source));
        Assert.Same(before, publication.Capture());
        Assert.Equal("old", status.Value);
        Assert.Equal(Id(1), rows.SelectedItemId);
    }

    [Fact]
    public void DeltaIndicesAddressTheSequentialCandidateAndPreserveStableSelection()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one"), new Row(2, "two"), new Row(3, "three") }, Id(2));
        var unchangedItem = rows.GetItem(1);
        var change = new UiCollectionChange<Row>(0, 1, new UiCollectionOperation<Row>[]
        {
            new UiCollectionInsert<Row>(1, new Row(4, "four")),
            new UiCollectionRemove<Row>(0, Id(1)),
            new UiCollectionMove<Row>(2, 0, Id(3)),
            new UiCollectionUpdate<Row>(1, Id(4), new Row(4, "FOUR", 4))
        }, Id(2));
        Assert.True(publication.BeginUpdate().Apply(rows, change).Commit().Succeeded);
        Assert.Equal(new[] { 3, 4, 2 }, rows.Value.Select(row => row.Id));
        Assert.Equal(new Row(4, "FOUR", 4), rows.Value[1]);
        Assert.Equal(Id(2), rows.SelectedItemId);
        Assert.True(rows.TryGetIndex(Id(2), out int selectedIndex));
        Assert.Equal(2, selectedIndex);
        Assert.Same(unchangedItem, rows.GetItem(2));
        Assert.Same(change, Assert.Single(rows.ReadChanges(0).Changes));
        Assert.Equal(1, rows.Version);
    }

    [Fact]
    public void IdenticalReplayIsNoopButConflictingStaleChangeAndGapAreRejected()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one") });
        var applied = Insert(0, 1, new Row(2, "two"));
        Assert.True(publication.BeginUpdate().Apply(rows, applied).Commit().Succeeded);
        Assert.True(publication.BeginUpdate().Apply(rows, Insert(1, 2, new Row(3, "three"))).Commit().Succeeded);
        var before = publication.Capture();
        int events = 0;
        rows.Changed += () => events++;
        var replay = publication.BeginUpdate().Apply(rows, Insert(0, 1, new Row(2, "two"))).Commit();
        var stale = publication.BeginUpdate().Apply(rows, Insert(0, 1, new Row(9, "conflict"))).Commit();
        var gap = publication.BeginUpdate().Apply(rows, Insert(4, 5, new Row(5, "gap"))).Commit();
        Assert.Equal(UiPublicationStatus.Unchanged, replay.Status);
        Assert.Equal(UiPublicationStatus.Invalid, stale.Status);
        Assert.Contains(stale.Diagnostics, diagnostic => diagnostic.Code == "UIP012");
        Assert.Equal(UiPublicationStatus.Invalid, gap.Status);
        Assert.Contains(gap.Diagnostics, diagnostic => diagnostic.Code == "UIP013");
        Assert.Same(before, publication.Capture());
        Assert.Equal(new[] { 1, 2, 3 }, rows.Value.Select(row => row.Id));
        Assert.Equal(0, events);
    }

    [Fact]
    public void FullResetRepairsVersionGapAndIdenticalResetReplayDoesNotDuplicateData()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one") });
        var resetValues = new[] { new Row(7, "seven"), new Row(8, "eight") };
        var reset = new UiCollectionChange<Row>(6, 7, new[] { new UiCollectionReset<Row>(resetValues) }, Id(8));
        resetValues[0] = new Row(99, "caller mutation");
        Assert.True(publication.BeginUpdate().Apply(rows, reset).Commit().Succeeded);
        Assert.Equal(7, rows.Version);
        Assert.Equal(1, publication.Version);
        Assert.Equal(Id(8), rows.SelectedItemId);
        Assert.Equal(new[] { 7, 8 }, rows.Value.Select(row => row.Id));
        var repeated = new UiCollectionChange<Row>(6, 7, new[] { new UiCollectionReset<Row>(new[] { new Row(7, "seven"), new Row(8, "eight") }) }, Id(8));
        Assert.Equal(UiPublicationStatus.Unchanged, publication.BeginUpdate().Apply(rows, repeated).Commit().Status);
        Assert.True(Assert.Single(rows.ReadChanges(0).Changes).IsReset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void InvalidDeltaNeverChangesThePublicationOrEmitsEvents(int variant)
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one"), new Row(2, "two") });
        UiCollectionOperation<Row>[] operations = variant switch
        {
            0 => new UiCollectionOperation<Row>[] { new UiCollectionInsert<Row>(1, new Row(2, "duplicate")) },
            1 => new UiCollectionOperation<Row>[] { new UiCollectionRemove<Row>(0, Id(2)) },
            2 => new UiCollectionOperation<Row>[] { new UiCollectionMove<Row>(0, 2, Id(1)) },
            3 => new UiCollectionOperation<Row>[] { new UiCollectionUpdate<Row>(0, Id(1), new Row(9, "changed identity")) },
            4 => new UiCollectionOperation<Row>[] { new UiCollectionInsert<Row>(9, new Row(9, "outside")) },
            _ => new UiCollectionOperation<Row>[] { new UiCollectionRemove<Row>(0, Id(1)), new UiCollectionReset<Row>(Array.Empty<Row>()) }
        };
        var before = publication.Capture();
        int events = 0;
        rows.Changed += () => events++;
        var result = publication.BeginUpdate().Apply(rows, new(0, 1, operations, null)).Commit();
        Assert.Equal(UiPublicationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UIP010" && diagnostic.Source == rows.SourceId);
        Assert.Same(before, publication.Capture());
        Assert.Equal(new[] { 1, 2 }, rows.Value.Select(row => row.Id));
        Assert.Equal(0, events);
    }

    [Fact]
    public void HistoryIsBoundedAndSlowReaderGetsOneImmutableReset()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one") });
        for (int version = 1; version <= 70; version++)
            Assert.True(publication.BeginUpdate().Apply(rows, new(version - 1, version,
                new[] { new UiCollectionUpdate<Row>(0, Id(1), new Row(1, "one", version)) }, null)).Commit().Succeeded);
        var retained = rows.ReadChanges(6);
        Assert.Equal(64, retained.Changes.Count);
        Assert.Equal(70, retained.Version);
        Assert.All(retained.Changes, change => Assert.False(change.IsReset));
        var slow = rows.ReadChanges(5);
        var reset = Assert.Single(slow.Changes);
        Assert.True(reset.IsReset);
        Assert.Equal(5, reset.BaseVersion);
        Assert.Equal(70, reset.Version);
        rows.Replace(new[] { new Row(2, "two") });
        Assert.Equal(new Row(1, "one", 70), Assert.Single(Assert.IsType<UiCollectionReset<Row>>(Assert.Single(reset.Operations)).Values));
        Assert.Empty(rows.ReadChanges(71).Changes);
        Assert.Throws<ArgumentOutOfRangeException>(() => rows.ReadChanges(72));
    }

    [Fact]
    public void PayloadIconAndTextAdvanceItemRevisionButReorderAndSelectionKeepIt()
    {
        using var publication = new UiPublication(Owner);
        var rows = Create(publication, new[] { new Row(1, "one"), new Row(2, "two") });
        long measurement = rows.GetItem(0).ContentVersion;
        rows.Replace(new[] { new Row(1, "one", 1), new Row(2, "two") });
        Assert.Equal(1, rows.GetItem(0).ItemRevision);
        Assert.Equal(measurement, rows.GetItem(0).ContentVersion);
        rows.Replace(new[] { new Row(1, "one", 1, Owner.Child("icon/one")), new Row(2, "two") });
        Assert.Equal(2, rows.GetItem(0).ItemRevision);
        Assert.Equal(measurement, rows.GetItem(0).ContentVersion);
        rows.Replace(new[] { new Row(1, "ONE", 1, Owner.Child("icon/one")), new Row(2, "two") });
        var revised = rows.GetItem(0);
        Assert.Equal(3, revised.ItemRevision);
        Assert.NotEqual(measurement, revised.ContentVersion);
        rows.Replace(new[] { new Row(2, "two"), new Row(1, "ONE", 1, Owner.Child("icon/one")) });
        Assert.True(rows.TrySelect(Id(1)));
        Assert.Same(revised, rows.GetItem(1));
        Assert.Equal(0, rows.GetItem(0).ItemRevision);
        Assert.Equal(5, rows.Version);
        Assert.Equal(4, rows.Revision);
    }

    [Fact]
    public void OperationLimitStopsEnumerationAndUnsupportedSelectionIsRejected()
    {
        int enumerated = 0;
        IEnumerable<UiCollectionOperation<Row>> Infinite()
        { while (true) { enumerated++; yield return new UiCollectionRemove<Row>(0, Id(1)); } }
        Assert.Throws<ArgumentException>(() => new UiCollectionChange<Row>(0, 1, Infinite(), null));
        Assert.Equal(129, enumerated);
        using var publication = new UiPublication(Owner);
        var rows = publication.Collection(Owner.Child("rows"), new[] { new Row(1, "one") }, RowType, row => Id(row.Id));
        Assert.False(rows is IUiSelectableCollectionSource);
        Assert.Equal(UiPublicationStatus.Invalid,
            publication.BeginUpdate().Apply(rows, new(0, 1, Array.Empty<UiCollectionOperation<Row>>(), Id(1))).Commit().Status);
        Assert.Equal(0, rows.Version);
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("disposed")]
    [InlineData("sealed")]
    [InlineData("duplicate")]
    public void RejectedRegistrationNeverExecutesCollectionProjectionCallbacks(string reason)
    {
        using var publication = new UiPublication(Owner);
        var id = Owner.Child("rows");
        if (reason == "disposed") publication.Dispose();
        if (reason == "sealed") publication.Capture();
        if (reason == "duplicate") Create(publication, new[] { new Row(1, "one") });
        int callbacks = 0;
        void Register() => publication.Collection(id, new[] { new Row(1, "one") }, RowType,
            row => { callbacks++; return Id(row.Id); }, row => { callbacks++; return row.Text; });
        Exception? failure = null;
        void Attempt() { try { Register(); } catch (Exception error) { failure = error; } }
        if (reason == "thread")
        {
            var worker = new Thread(Attempt);
            worker.Start();
            worker.Join();
        }
        else Attempt();
        Assert.NotNull(failure);
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnyPreparationFailurePoisonsTheWholeBatch(bool delta, bool projectionThrows)
    {
        using var publication = new UiPublication(Owner);
        var rows = publication.Collection(Owner.Child("rows"), new[] { new Row(1, "one") }, RowType,
            row => row.Payload == 99 ? throw new ApplicationException("projection failed") : Id(row.Id));
        var status = publication.State(Owner.Child("status"), "old", UiSourceTypes.String);
        var before = publication.Capture();
        var batch = publication.BeginUpdate().Set(status, "new");
        Row invalid = projectionThrows ? new Row(2, "bad", 99) : null!;
        int events = 0;
        status.Changed += () => events++;
        rows.Changed += () => events++;
        if (delta) batch.Apply(rows, new(0, 1, new[] { new UiCollectionInsert<Row>(1, invalid) }, null));
        else batch.Replace(rows, new[] { invalid });
        var result = batch.Commit();
        Assert.Equal(UiPublicationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UIP010" && diagnostic.Source == rows.SourceId);
        Assert.Same(before, publication.Capture());
        Assert.Equal("old", status.Value);
        Assert.Equal(new Row(1, "one"), Assert.Single(rows.Value));
        Assert.Equal(0, events);
    }

    [Fact]
    public void ReplayEqualityFailureAlsoRejectsUnrelatedStagedChanges()
    {
        using var publication = new UiPublication(Owner);
        var rows = publication.Collection(Owner.Child("rows"), Array.Empty<ThrowingEquality>(),
            UiSourceTypes.Scalar<ThrowingEquality>(Owner.Child("type/equality"), false), row => Id(row.Id));
        var status = publication.State(Owner.Child("status"), "old", UiSourceTypes.String);
        var applied = new UiCollectionChange<ThrowingEquality>(0, 1,
            new[] { new UiCollectionInsert<ThrowingEquality>(0, new ThrowingEquality(1)) }, null);
        Assert.True(publication.BeginUpdate().Apply(rows, applied).Commit().Succeeded);
        var before = publication.Capture();
        var replay = new UiCollectionChange<ThrowingEquality>(0, 1,
            new[] { new UiCollectionInsert<ThrowingEquality>(0, new ThrowingEquality(1)) }, null);
        var result = publication.BeginUpdate().Set(status, "new").Apply(rows, replay).Commit();
        Assert.Equal(UiPublicationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "UIP010");
        Assert.Same(before, publication.Capture());
        Assert.Equal("old", status.Value);
    }

    private sealed class ThrowingEquality
    {
        internal ThrowingEquality(int id) => Id = id;
        internal int Id { get; }
        public override bool Equals(object? value) => throw new ApplicationException("payload equality failed");
        public override int GetHashCode() => Id;
    }

    private static UiPublishedSelectableCollection<Row> Create(UiPublication publication, IReadOnlyList<Row> rows, UiSymbolId? selected = null)
        => publication.SelectableCollection(Owner.Child("rows"), rows, RowType, row => Id(row.Id), row => row.Text,
            selected, icon: row => row.Icon);

    private static UiCollectionChange<Row> Insert(long from, long to, Row value)
        => new(from, to, new[] { new UiCollectionInsert<Row>((int)from + 1, value) }, null);
}
