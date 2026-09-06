using System;
using System.Collections.Generic;
using System.Threading;
using Hatifect.UI.Experience;
using Hatifect.UI.Semantics;
using Xunit;

namespace Hatifect.UI.Planning.Tests;

public sealed class PublicationTests
{
    private static readonly UiSymbolId Owner = new("Publication.Tests", "screen");

    [Fact]
    public void EveryObserverSeesTheCompleteCommittedBatchAndOldViewsRemainReadable()
    {
        using var publication = new UiPublication(Owner);
        var title = publication.State(Owner.Child("title"), "old", UiSourceTypes.String);
        var ready = publication.State(Owner.Child("ready"), false, UiSourceTypes.Boolean);
        var unchanged = publication.State(Owner.Child("count"), 3m, UiSourceTypes.Number);
        var before = publication.Capture();
        var observed = new List<(string, bool, long, long, long)>();
        void Observe() => observed.Add((title.Value, ready.Value, publication.Version, title.Version, ready.Version));
        title.Changed += Observe;
        ready.Changed += Observe;
        publication.Changed += Observe;
        int unrelated = 0;
        unchanged.Changed += () => unrelated++;

        var batch = publication.BeginUpdate().Set(title, "new").Set(ready, true);
        Assert.Equal("old", title.Value);
        Assert.False(ready.Value);
        Assert.Equal("new", batch.Read(title));
        UiPublicationResult result = batch.Commit();

        Assert.Equal(UiPublicationStatus.Committed, result.Status);
        Assert.Equal(publication.Id, result.PublicationId);
        Assert.Equal(1, result.Version);
        Assert.Equal(3, observed.Count);
        Assert.All(observed, value => Assert.Equal(("new", true, 1L, 1L, 1L), value));
        Assert.Equal(0, unrelated);
        Assert.Equal(0, unchanged.Version);
        Assert.Equal("old", title.Read(before));
        Assert.False(ready.Read(before));
        Assert.Equal(0, before.Version);
        Assert.Equal(0, before.SourceVersion(title));
        Assert.Equal(0, Assert.IsAssignableFrom<IUiVersionedSemanticSource>(before.Read(title)).Version);
        Assert.Equal(1, Assert.IsAssignableFrom<IUiVersionedSemanticSource>(publication.Capture().Read(title)).Version);
        Assert.Same(publication.Capture(), publication.Capture());
    }

    [Fact]
    public void InvalidCandidateRejectsAllChangesWithoutNotifications()
    {
        using var publication = new UiPublication(Owner);
        var title = publication.State(Owner.Child("title"), "old", UiSourceTypes.String);
        var ready = publication.State(Owner.Child("ready"), false, UiSourceTypes.Boolean);
        var before = publication.Capture();
        int events = 0;
        publication.Changed += () => events++;
        title.Changed += () => events++;
        var result = publication.BeginUpdate().Set(ready, true).Set(title, null!).Commit();

        Assert.Equal(UiPublicationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Code == "UIG022" && item.Source == title.SourceId);
        Assert.Same(before, publication.Capture());
        Assert.Equal("old", title.Value);
        Assert.False(ready.Value);
        Assert.Equal(0, events);
    }

    [Fact]
    public void ObserverFailuresAreReportedWithoutRollbackOrStarvingOtherObservers()
    {
        using var publication = new UiPublication(Owner);
        var value = publication.State(Owner.Child("value"), "old", UiSourceTypes.String);
        var failure = new InvalidOperationException("observer failure");
        var observed = new List<string>();
        value.Changed += () => throw failure;
        value.Changed += () => observed.Add(value.Value);
        publication.Changed += () => throw new ApplicationException("publication observer");
        publication.Changed += () => observed.Add(value.Value);

        var result = publication.BeginUpdate().Set(value, "new").Commit();
        Assert.Equal(UiPublicationStatus.Committed, result.Status);
        Assert.Equal(new[] { "new", "new" }, observed);
        Assert.Equal(2, result.ObserverErrors.Count);
        Assert.Same(failure, result.ObserverErrors[0]);
        Assert.Equal("new", value.Value);
        Assert.Equal(1, value.Version);
        Assert.False(publication.IsPublishing);
    }

    [Fact]
    public void ReentrantCommitIsAddressedlyRejectedAndDoesNotLoseOuterNotification()
    {
        using var publication = new UiPublication(Owner);
        var value = publication.State(Owner.Child("value"), "old", UiSourceTypes.String);
        UiPublicationResult? inner = null;
        int events = 0;
        value.Changed += () => inner = publication.BeginUpdate().Set(value, "recursive").Commit();
        publication.Changed += () => events++;
        var outer = publication.BeginUpdate().Set(value, "new").Commit();

        Assert.Equal(UiPublicationStatus.Reentrant, inner!.Status);
        Assert.Contains(inner.Diagnostics, item => item.Code == "UIP003" && item.Source == Owner);
        Assert.Equal(UiPublicationStatus.Committed, outer.Status);
        Assert.Same(outer, publication.LastResult);
        Assert.Empty(outer.ObserverErrors);
        Assert.Equal("new", value.Value);
        Assert.Equal(1, events);
        Assert.Equal(1, publication.Version);
    }

    [Fact]
    public void StaleAndCompletedBatchesCannotOverwriteNewerState()
    {
        using var publication = new UiPublication(Owner);
        var value = publication.State(Owner.Child("value"), "old", UiSourceTypes.String);
        var stale = publication.BeginUpdate().Set(value, "stale");
        var applied = publication.BeginUpdate().Set(value, "new");
        Assert.True(applied.Commit().Succeeded);
        Assert.Equal(UiPublicationStatus.Conflict, stale.Commit().Status);
        Assert.Equal(UiPublicationStatus.Conflict, applied.Commit().Status);
        Assert.Throws<InvalidOperationException>(() => applied.Set(value, "again"));
        Assert.Equal("new", value.Value);
        Assert.Equal(1, publication.Version);
    }

    [Fact]
    public void EquivalentBatchIncludingRevertedDraftDoesNotAdvanceAnyVersion()
    {
        using var publication = new UiPublication(Owner);
        var value = publication.State(Owner.Child("value"), "old", UiSourceTypes.String);
        var before = publication.Capture();
        int events = 0;
        value.Changed += () => events++;
        var result = publication.BeginUpdate().Set(value, "temporary").Set(value, "old").Commit();
        Assert.Equal(UiPublicationStatus.Unchanged, result.Status);
        Assert.Same(before, publication.Capture());
        Assert.Equal(0, value.Version);
        Assert.Equal(0, events);
    }

    [Fact]
    public void PublicationsWithTheSameStableOwnerStillHaveSeparateIdentityAndState()
    {
        using var first = new UiPublication(Owner);
        using var second = new UiPublication(Owner);
        var left = first.State(Owner.Child("value"), "first", UiSourceTypes.String);
        var right = second.State(Owner.Child("value"), "second", UiSourceTypes.String);
        var foreign = second.BeginUpdate().Set(right, "changed");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Throws<ArgumentException>(() => first.Capture().Read(right));
        Assert.Throws<ArgumentException>(() => first.BeginUpdate().Set(right, "crossed"));
        Assert.Equal(UiPublicationStatus.Invalid, first.Commit(foreign).Status);
        Assert.Equal("first", left.Value);
        Assert.Equal("second", right.Value);
        Assert.True(foreign.Commit().Succeeded);
    }

    [Fact]
    public void WrongThreadCommitIsRejectedAndCandidateCanStillCommitOnTheOwner()
    {
        using var publication = new UiPublication(Owner);
        var value = publication.State(Owner.Child("value"), "old", UiSourceTypes.String);
        var batch = publication.BeginUpdate().Set(value, "new");
        UiPublicationResult? result = null;
        Exception? captureError = null;
        var worker = new Thread(() =>
        {
            result = batch.Commit();
            try { publication.Capture(); } catch (Exception error) { captureError = error; }
        });
        worker.Start();
        worker.Join();
        Assert.Equal(UiPublicationStatus.WrongThread, result!.Status);
        Assert.IsType<InvalidOperationException>(captureError);
        Assert.Equal("old", value.Value);
        Assert.True(batch.Commit().Succeeded);
        Assert.Equal("new", value.Value);
    }

    [Fact]
    public void DisposeIsTerminalButAlreadyCapturedValuesRemainAvailable()
    {
        var publication = new UiPublication(Owner);
        var value = publication.State(Owner.Child("value"), "old", UiSourceTypes.String);
        var before = publication.Capture();
        var batch = publication.BeginUpdate().Set(value, "new");
        int events = 0;
        value.Changed += () => events++;
        publication.Dispose();
        publication.Dispose();
        Assert.Equal(UiPublicationStatus.Disposed, batch.Commit().Status);
        Assert.Throws<ObjectDisposedException>(() => value.Changed += () => { });
        Assert.Throws<ObjectDisposedException>(() => publication.Changed += () => { });
        Assert.Equal("old", value.Read(before));
        Assert.Equal("old", value.Value);
        Assert.Equal(0, events);
    }

    [Fact]
    public void DisposeDuringNotificationRetiresSubscriptionsWithoutUndoingTheCommittedView()
    {
        using var publication = new UiPublication(Owner);
        var first = publication.State(Owner.Child("first"), "old", UiSourceTypes.String);
        var second = publication.State(Owner.Child("second"), "old", UiSourceTypes.String);
        int retiredCallbacks = 0;
        first.Changed += publication.Dispose;
        first.Changed += () => retiredCallbacks++;
        second.Changed += () => retiredCallbacks++;
        publication.Changed += () => retiredCallbacks++;
        var result = publication.BeginUpdate().Set(first, "new").Set(second, "new").Commit();
        Assert.Equal(UiPublicationStatus.Committed, result.Status);
        Assert.Empty(result.ObserverErrors);
        Assert.True(publication.IsDisposed);
        Assert.False(publication.IsPublishing);
        Assert.Equal("new", first.Value);
        Assert.Equal("new", second.Value);
        Assert.Equal(0, retiredCallbacks);
        Assert.Equal(UiPublicationStatus.Disposed, publication.BeginUpdate().Set(first, "again").Commit().Status);
    }

    [Fact]
    public void SourceRegistrationRejectsInvalidTypesForeignAndDuplicateIdsAndLateDeclarations()
    {
        using var publication = new UiPublication(Owner);
        Assert.Throws<UiGraphValidationException>(() => publication.State(Owner.Child("value"), null!, UiSourceTypes.String));
        Assert.Throws<UiGraphValidationException>(() => publication.State(Owner.Child("wrong"), 7, new UiSourceType<int>(UiDataTypes.String)));
        Assert.Throws<ArgumentException>(() => publication.State(new UiSymbolId("Other", "value"), "x", UiSourceTypes.String));
        var valid = publication.State(Owner.Child("value"), "valid", UiSourceTypes.String);
        Assert.Throws<InvalidOperationException>(() => publication.State(valid.SourceId, "duplicate", UiSourceTypes.String));
        publication.Capture();
        Assert.Throws<InvalidOperationException>(() => publication.State(Owner.Child("late"), "late", UiSourceTypes.String));
        Assert.Equal("valid", valid.Value);
    }

    [Fact]
    public void NominalTypesAreSharedAcrossPublicationSourcesAndFailedRegistrationCanBeRetried()
    {
        using var publication = new UiPublication(Owner);
        var sharedType = Owner.Child("type/shared");
        var first = publication.State(Owner.Child("first"), 1, UiSourceTypes.Scalar<int>(sharedType, false));
        var error = Assert.Throws<UiGraphValidationException>(() => publication.State(Owner.Child("second"), "wrong",
            UiSourceTypes.Scalar<string>(sharedType, false)));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "UIG022");
        var second = publication.State(Owner.Child("second"), "valid",
            UiSourceTypes.Scalar<string>(Owner.Child("type/text"), false));
        var sameType = publication.State(Owner.Child("third"), 2, UiSourceTypes.Scalar<int>(sharedType, false));
        Assert.True(publication.BeginUpdate().Set(first, 3).Set(second, "new").Set(sameType, 4).Commit().Succeeded);
        Assert.Equal(3, first.Value);
        Assert.Equal("new", second.Value);
        Assert.Equal(4, sameType.Value);
    }
}
