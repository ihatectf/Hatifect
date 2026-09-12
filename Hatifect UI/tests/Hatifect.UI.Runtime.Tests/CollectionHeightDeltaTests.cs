using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Layout;
using Hatifect.UI.Runtime.Scene;
using Hatifect.UI.Runtime.Visual;
using Hatifect.UI.Runtime.Visual.Resolution;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class CollectionHeightDeltaTests
{
    [Theory]
    [InlineData("update", 0)]
    [InlineData("insert", 32)]
    [InlineData("remove", -32)]
    [InlineData("move", 0)]
    public void OffscreenDeltaKeepsUnrelatedExactHeightsAndAnchor(string operation, float extentDelta)
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        int measurements = fixture.Metrics.Calls;
        UiCollectionOperation<Row> change = operation switch
        {
            "update" => new UiCollectionUpdate<Row>(99, fixture.Rows[99].Id, fixture.Rows[99] with { Supporting = new string('X', 900) }),
            "insert" => new UiCollectionInsert<Row>(100, new(fixture.Owner.Child("item/new"), "New", "Supporting")),
            "remove" => new UiCollectionRemove<Row>(99, fixture.Rows[99].Id),
            "move" => new UiCollectionMove<Row>(80, 81, fixture.Rows[80].Id),
            _ => throw new InvalidOperationException()
        };
        fixture.Apply(change);

        UiCollectionLayoutWindow after = fixture.Materialize(fixture.Scene(), before);

        Assert.Equal(before.TotalExtent + extentDelta, after.TotalExtent);
        Assert.Equal(before.ScrollOffset, after.ScrollOffset);
        Assert.Equal(before.Anchor, after.Anchor);
        Assert.Equal(before.Items.Select(item => (item.Item.Id, item.Bounds)),
            after.Items.Select(item => (item.Item.Id, item.Bounds)));
        Assert.Equal(measurements, fixture.Metrics.Calls);
    }

    [Fact]
    public void MaximumRetainedMoveHistoryPreservesUnrelatedMeasuredWindows()
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        int measurements = fixture.Metrics.Calls;
        for (int batch = 0; batch < 64; batch++)
        {
            Row[] candidate = fixture.Source.Value.ToArray();
            var operations = new List<UiCollectionOperation<Row>>(UiCollectionChange<Row>.MaximumOperations);
            for (int operation = 0; operation < UiCollectionChange<Row>.MaximumOperations - 1; operation++)
            {
                int from = operation % 2 == 0 ? 80 : 90;
                operations.Add(new UiCollectionMove<Row>(from, from + 1, candidate[from].Id));
                (candidate[from], candidate[from + 1]) = (candidate[from + 1], candidate[from]);
            }
            operations.Add(new UiCollectionUpdate<Row>(99, candidate[99].Id,
                candidate[99] with { Supporting = "Batch " + batch }));
            fixture.Apply(operations.ToArray());
        }

        UiCollectionLayoutWindow after = fixture.Materialize(fixture.Scene(), before);

        Assert.Equal(64, fixture.Source.Version);
        Assert.Equal(measurements, fixture.Metrics.Calls);
        Assert.Equal(before.TotalExtent, after.TotalExtent);
        Assert.Equal(before.ScrollOffset, after.ScrollOffset);
        Assert.Equal(before.Anchor, after.Anchor);
        Assert.Equal(before.Items.Select(item => (item.Item.Id, item.Bounds)),
            after.Items.Select(item => (item.Item.Id, item.Bounds)));
    }

    [Fact]
    public void DisjointMovesInvalidateMeasuredRowsWithoutChangingItemHeights()
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow warm = fixture.WarmSeparatedWindows();
        fixture.Apply(new UiCollectionUpdate<Row>(30, fixture.Rows[30].Id,
            fixture.Rows[30] with { Supporting = new string('X', 900) }));
        UiCollectionLayoutWindow before = fixture.Materialize(fixture.Scene(), warm);
        var largeBefore = Assert.Single(before.Items, item => item.Item.Id == fixture.Rows[30].Id);
        fixture.Apply(new UiCollectionMove<Row>(80, 81, fixture.Rows[80].Id),
            new UiCollectionMove<Row>(30, 31, fixture.Rows[30].Id));

        UiCollectionLayoutWindow after = fixture.Materialize(fixture.Scene(), before);

        var moved = Assert.Single(after.Items, item => item.Item.Id == fixture.Rows[30].Id);
        var preceding = Assert.Single(after.Items, item => item.Item.Id == fixture.Rows[31].Id);
        Assert.Equal(31, moved.Index);
        Assert.Equal(30, preceding.Index);
        Assert.Equal(largeBefore.Bounds.Height, moved.Bounds.Height);
        Assert.True(preceding.Bounds.Height < moved.Bounds.Height);
        Assert.Equal(before.Anchor, after.Anchor);
    }

    [Theory]
    [InlineData(true, 31)]
    [InlineData(false, 29)]
    public void PrefixInsertionOrRemovalDiscardsShiftedExactRows(bool insert, int expectedIndex)
    {
        using var fixture = new Fixture();
        fixture.Apply(new UiCollectionUpdate<Row>(30, fixture.Rows[30].Id,
            fixture.Rows[30] with { Supporting = new string('X', 900) }));
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        var largeBefore = Assert.Single(before.Items, item => item.Item.Id == fixture.Rows[30].Id);
        fixture.Apply(insert
            ? new UiCollectionInsert<Row>(0, new(fixture.Owner.Child("item/new"), "Inserted", "Short"))
            : new UiCollectionRemove<Row>(0, fixture.Rows[0].Id));
        UiCollectionSceneNode candidate = fixture.Scene();

        UiCollectionLayoutWindow after = fixture.Materialize(candidate, before);
        UiCollectionLayoutWindow fresh = fixture.Materialize(candidate, before,
            new UiCollectionVirtualizer(new Metrics()));

        var shifted = Assert.Single(after.Items, item => item.Item.Id == fixture.Rows[30].Id);
        Assert.Equal(expectedIndex, shifted.Index);
        Assert.Equal(largeBefore.Bounds.Height, shifted.Bounds.Height);
        Assert.Equal(before.Anchor, after.Anchor);
        Assert.Equal(fresh.TotalExtent, after.TotalExtent);
        Assert.Equal(fresh.ScrollOffset, after.ScrollOffset);
        Assert.Equal(fresh.Items.Select(item => (item.Item.Id, item.Bounds)),
            after.Items.Select(item => (item.Item.Id, item.Bounds)));
    }

    [Fact]
    public void ReplacingSourceOwnerCannotKeepPreviousOwnersExactHeights()
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        Row[] replacement = fixture.Rows.ToArray();
        replacement[30] = replacement[30] with { Supporting = new string('X', 900) };
        UiPublishedCollection<Row> other = fixture.Publication.Collection(fixture.Owner.Child("other-rows"), replacement,
            UiSourceTypes.Scalar<Row>(fixture.Owner.Child("type"), false), row => row.Id, row => row.Label,
            row => row.Supporting);
        UiCollectionSceneNode candidate = fixture.Scene(other);
        int measurements = fixture.Metrics.Calls;

        UiCollectionLayoutWindow after = fixture.Materialize(candidate, before);
        UiCollectionLayoutWindow fresh = fixture.Materialize(candidate, before,
            new UiCollectionVirtualizer(new Metrics()));

        Assert.True(fixture.Metrics.Calls > measurements);
        var oldRow = Assert.Single(before.Items, item => item.Item.Id == fixture.Rows[30].Id);
        var newRow = Assert.Single(after.Items, item => item.Item.Id == fixture.Rows[30].Id);
        Assert.True(newRow.Bounds.Height > oldRow.Bounds.Height);
        Assert.Equal(before.Collection, after.Collection);
        Assert.Equal(before.Anchor, after.Anchor);
        Assert.Equal(fresh.TotalExtent, after.TotalExtent);
        Assert.Equal(fresh.ScrollOffset, after.ScrollOffset);
        Assert.Equal(fresh.Items.Select(item => (item.Item.Id, item.Bounds)),
            after.Items.Select(item => (item.Item.Id, item.Bounds)));
    }

    [Fact]
    public void RetainedCandidateDoesNotConsumeLaterLiveRowUpdate()
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        fixture.Apply(new UiCollectionUpdate<Row>(99, fixture.Rows[99].Id,
            fixture.Rows[99] with { Supporting = "First offscreen change" }));
        UiCollectionSceneNode retained = fixture.Scene();
        fixture.Apply(new UiCollectionUpdate<Row>(30, fixture.Rows[30].Id,
            fixture.Rows[30] with { Supporting = new string('X', 900) }));

        int measurements = fixture.Metrics.Calls;
        UiCollectionLayoutWindow captured = fixture.Materialize(retained, before);

        Assert.Equal(measurements, fixture.Metrics.Calls);
        Assert.Equal(before.TotalExtent, captured.TotalExtent);
        Assert.Equal(before.ScrollOffset, captured.ScrollOffset);
        var capturedRow = Assert.Single(captured.Items, item => item.Item.Id == fixture.Rows[30].Id);
        Assert.Equal(fixture.Rows[30].Supporting, capturedRow.Item.SupportingText);
        UiCollectionLayoutWindow latest = fixture.Materialize(fixture.Scene(), captured);
        var updatedRow = Assert.Single(latest.Items, item => item.Item.Id == fixture.Rows[30].Id);
        Assert.True(updatedRow.Bounds.Height > capturedRow.Bounds.Height);
        Assert.Equal(new string('X', 900), updatedRow.Item.SupportingText);
        Assert.Equal(captured.Anchor, latest.Anchor);
    }

    [Fact]
    public void HistoryGapFallsBackToFreshHeightIndex()
    {
        using var fixture = new Fixture(historyCapacity: 1);
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        fixture.Apply(new UiCollectionUpdate<Row>(99, fixture.Rows[99].Id,
            fixture.Rows[99] with { Supporting = "First" }));
        fixture.Apply(new UiCollectionUpdate<Row>(99, fixture.Rows[99].Id,
            fixture.Rows[99] with { Supporting = "Second" }));
        UiCollectionSceneNode candidate = fixture.Scene();

        UiCollectionLayoutWindow after = fixture.Materialize(candidate, before);
        UiCollectionLayoutWindow fresh = fixture.Materialize(candidate, before,
            new UiCollectionVirtualizer(new Metrics()));

        Assert.Equal(fresh.TotalExtent, after.TotalExtent);
        Assert.Equal(fresh.ScrollOffset, after.ScrollOffset);
        Assert.True(after.TotalExtent < before.TotalExtent);
        Assert.Equal(before.Anchor, after.Anchor);
    }

    [Fact]
    public void ExplicitResetDiscardsUnrelatedExactHeights()
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        Row[] replacement = fixture.Rows.ToArray();
        replacement[99] = replacement[99] with { Supporting = "Reset offscreen content" };
        fixture.Apply(new UiCollectionReset<Row>(replacement));
        UiCollectionSceneNode candidate = fixture.Scene();

        UiCollectionLayoutWindow after = fixture.Materialize(candidate, before);
        UiCollectionLayoutWindow fresh = fixture.Materialize(candidate, before,
            new UiCollectionVirtualizer(new Metrics()));

        Assert.Equal(fresh.TotalExtent, after.TotalExtent);
        Assert.Equal(fresh.ScrollOffset, after.ScrollOffset);
        Assert.True(after.TotalExtent < before.TotalExtent);
        Assert.Equal(before.Anchor, after.Anchor);
    }

    [Theory]
    [InlineData("width")]
    [InlineData("locale")]
    [InlineData("theme")]
    public void MeasurementContextChangeCannotReuseOldExactRows(string changed)
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        fixture.Apply(new UiCollectionUpdate<Row>(99, fixture.Rows[99].Id,
            fixture.Rows[99] with { Supporting = "Offscreen" }));
        UiCollectionSceneNode candidate = fixture.Scene();
        int calls = fixture.Metrics.Calls;
        float width = changed == "width" ? 160 : 240;
        string locale = changed == "locale" ? "ru-RU" : "en";
        string theme = changed == "theme" ? "other-theme" : "theme";

        UiCollectionLayoutWindow after = fixture.Materialize(candidate, before,
            width: width, locale: locale, theme: theme);
        UiCollectionLayoutWindow fresh = fixture.Materialize(candidate, before,
            new UiCollectionVirtualizer(new Metrics()), width, locale, theme);

        Assert.Equal(fresh.TotalExtent, after.TotalExtent);
        Assert.Equal(fresh.ScrollOffset, after.ScrollOffset);
        Assert.Equal(fresh.Items.Select(item => (item.Item.Id, item.Bounds)),
            after.Items.Select(item => (item.Item.Id, item.Bounds)));
        Assert.True(fixture.Metrics.Calls > calls);
        Assert.Equal(before.Anchor, after.Anchor);
    }

    [Fact]
    public void FailedCandidateAllowsRetryWithoutKeepingItsFailedMeasurement()
    {
        using var fixture = new Fixture();
        UiCollectionLayoutWindow before = fixture.WarmSeparatedWindows();
        UiCollectionSceneNode accepted = fixture.Scene();
        fixture.Apply(new UiCollectionUpdate<Row>(30, fixture.Rows[30].Id,
            fixture.Rows[30] with { Supporting = new string('X', 900) }));
        UiCollectionSceneNode candidate = fixture.Scene();
        var failure = new InvalidOperationException("Measurement failed");
        fixture.Metrics.Failure = failure;

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => fixture.Materialize(candidate, before)));
        fixture.RestoreTransitions(accepted);
        fixture.Metrics.Failure = null;
        UiCollectionLayoutWindow retry = fixture.Materialize(candidate, before);
        UiCollectionLayoutWindow stable = fixture.Materialize(candidate, retry);

        var oldRow = Assert.Single(before.Items, item => item.Item.Id == fixture.Rows[30].Id);
        var newRow = Assert.Single(retry.Items, item => item.Item.Id == fixture.Rows[30].Id);
        Assert.Equal(new string('X', 900), newRow.Item.SupportingText);
        Assert.True(newRow.Bounds.Height > oldRow.Bounds.Height);
        Assert.Equal(before.Anchor, retry.Anchor);
        Assert.Equal(retry.TotalExtent, stable.TotalExtent);
        Assert.Equal(retry.ScrollOffset, stable.ScrollOffset);
        Assert.Equal(retry.Items.Select(item => (item.Item.Id, item.Bounds)),
            stable.Items.Select(item => (item.Item.Id, item.Bounds)));
    }

    private sealed record Row(UiSymbolId Id, string Label, string Supporting);
    private sealed class Fixture : IDisposable
    {
        internal readonly UiSymbolId Owner = new("test", "height-delta");
        internal readonly Row[] Rows;
        internal readonly UiPublication Publication;
        internal readonly UiPublishedCollection<Row> Source;
        internal readonly Metrics Metrics = new();
        private readonly UiCollectionVirtualizer _virtualizer;
        private readonly UiRect _viewport = new(0, 0, 240, 160);
        private readonly UiVisualResolution _visual = new(new Dictionary<UiSymbolId, UiResolvedVisualProperty>(),
            Array.Empty<UiVisualResolutionStep>());
        internal Fixture(int historyCapacity = 64)
        {
            Publication = new(Owner);
            Rows = Enumerable.Range(0, 100).Select(index => new Row(Owner.Child("item/" + index),
                "Item " + index, new string('W', 100))).ToArray();
            Source = Publication.Collection(Owner.Child("rows"), Rows,
                UiSourceTypes.Scalar<Row>(Owner.Child("type"), false), row => row.Id, row => row.Label,
                row => row.Supporting, historyCapacity: historyCapacity);
            _virtualizer = new(Metrics);
        }
        internal UiCollectionSceneNode Scene(UiPublishedCollection<Row>? source = null)
            => new(Owner.Child("collection"), Owner.Child("role"), _visual, _visual, "Rows", source ?? Source,
                new(UiCollectionLayoutKind.List, 3, 1, Owner.Child("adaptive"), true,
                    UiCollectionDensity.Default, false));
        internal UiCollectionLayoutWindow WarmSeparatedWindows()
        {
            UiCollectionSceneNode scene = Scene();
            UiCollectionLayoutWindow first = Materialize(scene, null);
            Assert.Contains(first.Items, item => item.Item.Id == Rows[0].Id);
            Assert.True(first.Items[0].Bounds.Height > 32);
            UiCollectionLayoutWindow middle = _virtualizer.Materialize(scene, _viewport, _viewport, 240, 32, 24,
                new("Body", 16, 1.5f), new(Owner.Child("profile"), "en", Owner.Child("theme")),
                new(0, new(Rows[30].Id, 0), 30));
            middle = Materialize(scene, middle);
            Assert.All(middle.Items, item => Assert.InRange(item.Index, 1, 98));
            Assert.Equal(Rows[30].Id, middle.Anchor!.Item);
            return middle;
        }
        internal UiCollectionLayoutWindow Materialize(UiCollectionSceneNode scene,
            UiCollectionLayoutWindow? previous, UiCollectionVirtualizer? virtualizer = null,
            float width = 240, string locale = "en", string theme = "theme")
            => (virtualizer ?? _virtualizer).Materialize(scene, new(0, 0, width, 160), new(0, 0, width, 160), width, 32, 24,
                new("Body", 16, 1.5f), new(Owner.Child("profile"), locale, Owner.Child(theme)),
                previous is null ? new(0, null, -1) : new(previous.ScrollOffset, previous.Anchor, previous.AnchorIndex));
        internal void RestoreTransitions(UiCollectionSceneNode accepted)
            => _virtualizer.RestoreTransitions(accepted);
        internal void Apply(params UiCollectionOperation<Row>[] operations)
        {
            long version = Source.Version;
            Assert.Equal(UiPublicationStatus.Committed, Publication.BeginUpdate().Apply(Source,
                new UiCollectionChange<Row>(version, version + 1, operations, null)).Commit().Status);
        }
        public void Dispose() => Publication.Dispose();
    }

    private sealed class Metrics : IUiTextMetrics
    {
        internal int Calls;
        internal Exception? Failure;
        public UiSize Measure(string text, UiTypography typography, float availableWidth, UiTextOverflow overflow)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            float rows = overflow == UiTextOverflow.Wrap
                ? Math.Max(1, MathF.Ceiling(text.Length * 8 / Math.Max(1, availableWidth))) : 1;
            return new(Math.Min(availableWidth, text.Length * 8), rows * 24);
        }
    }
}
