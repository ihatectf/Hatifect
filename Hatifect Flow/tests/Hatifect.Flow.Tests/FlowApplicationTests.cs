using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class FlowApplicationTests
{
    [Fact]
    public void Availability_PreservesVisibleCargoAndPublishesDisabledActionsUntilResumed()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, command => command(fixture.Runtime), _ => { });
        FlowSnapshot active = app.ReadSnapshot();
        app.SetAvailability(FlowApplicationState.Paused);
        FlowSnapshot paused = app.ReadSnapshot();
        Assert.Equal(FlowApplicationState.Paused, paused.State);
        Assert.Equal(FlowParcelActions.None, Assert.Single(paused.Parcels).Actions);
        Assert.Equal(7, paused.Parcels[0].Quantity);
        Assert.Equal(FlowCommandStatus.Rejected, app.Execute(Command(paused, FlowParcelAction.Cancel)).Status);
        app.SetAvailability(FlowApplicationState.Paused);
        Assert.Same(paused, app.ReadSnapshot());
        app.SetAvailability(FlowApplicationState.RecoveryRequired);
        Assert.Equal(FlowApplicationState.RecoveryRequired, app.ReadSnapshot().State);
        app.SetAvailability(FlowApplicationState.Active);
        Assert.Equal(FlowCommandStatus.Conflict, app.Execute(Command(active, FlowParcelAction.Cancel)).Status);
        Assert.Equal(FlowCommandStatus.Applied, app.Execute(Command(app.ReadSnapshot(), FlowParcelAction.Cancel)).Status);
        app.Dispose();
        app.SetAvailability(FlowApplicationState.Active);
        Assert.Equal(FlowApplicationState.Closed, app.ReadSnapshot().State);
    }

    [Fact]
    public void Snapshots_RemainImmutableAndCachedWhileCommandsPublishCommittedState()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        FlowSnapshot before = app.ReadSnapshot();
        Assert.Same(before, app.ReadSnapshot());
        Assert.Throws<NotSupportedException>(() => ((IList<FlowParcelSnapshot>)before.Parcels).Clear());
        var observed = new List<FlowSnapshot>();
        app.RevisionChanged += _ => observed.Add(app.ReadSnapshot());

        FlowCommandResult result = app.Execute(Command(before, FlowParcelAction.Reserve));

        Assert.Equal(new FlowCommandResult(FlowCommandStatus.Applied, 1), result);
        Assert.Equal(ParcelState.Created, Assert.Single(before.Parcels).State);
        Assert.Equal(ParcelState.Reserved, Assert.Single(Assert.Single(observed).Parcels).State);
        Assert.Equal(7, fixture.Runtime.ReservedUnits(CheckpointFixture.Link));
        FlowSnapshot reserved = app.ReadSnapshot();
        Assert.Equal(FlowCommandStatus.Rejected, app.Execute(Command(reserved, FlowParcelAction.Reserve)).Status);
        Assert.Same(reserved, app.ReadSnapshot());
        Assert.Single(observed);
    }

    [Theory]
    [InlineData("revision", FlowCommandStatus.Conflict)]
    [InlineData("session", FlowCommandStatus.Conflict)]
    [InlineData("parcel", FlowCommandStatus.InvalidCommand)]
    [InlineData("action", FlowCommandStatus.InvalidCommand)]
    public void Commands_RejectStaleForeignAndInvalidRequestsWithoutMutation(string invalid, FlowCommandStatus expected)
    {
        var fixture = new CheckpointFixture();
        int executions = 0;
        using var app = new FlowApplication(fixture.Runtime, action => { executions++; action(fixture.Runtime); }, _ => { });
        FlowSnapshot snapshot = app.ReadSnapshot();
        FlowParcelCommand command = Command(snapshot, FlowParcelAction.Reserve);
        command = invalid switch
        {
            "revision" => command with { ExpectedRevision = 1 },
            "session" => command with { SessionId = Guid.Empty },
            "parcel" => command with { ParcelId = Guid.Empty },
            _ => command with { Action = (FlowParcelAction)99 }
        };

        Assert.Equal(expected, app.Execute(command).Status);
        Assert.Equal(0, executions);
        Assert.Same(snapshot, app.ReadSnapshot());
        Assert.Equal(0, fixture.Runtime.PendingOperationCount);
    }

    [Fact]
    public void Notifications_IsolateObserversAndRejectReentrantMutation()
    {
        var fixture = new CheckpointFixture();
        var errors = new List<Exception>();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), errors.Add);
        int laterObserver = 0;
        app.RevisionChanged += _ => app.Execute(Command(app.ReadSnapshot(), FlowParcelAction.Cancel));
        app.RevisionChanged += _ => throw new FormatException("observer");
        app.RevisionChanged += _ => laterObserver++;

        Assert.Equal(FlowCommandStatus.Applied, app.Execute(Command(app.ReadSnapshot(), FlowParcelAction.Reserve)).Status);

        Assert.Equal(2, errors.Count);
        Assert.IsType<InvalidOperationException>(errors[0]);
        Assert.IsType<FormatException>(errors[1]);
        Assert.Equal(1, laterObserver);
        Assert.Equal(ParcelState.Reserved, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void Closure_RetiresOldCommandsAndNotifiesOnce()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        FlowParcelCommand old = Command(app.ReadSnapshot(), FlowParcelAction.Reserve);
        int notifications = 0;
        app.RevisionChanged += _ => notifications++;
        app.Dispose();
        app.Dispose();

        Assert.Equal(FlowCommandStatus.SessionClosed, app.Execute(old).Status);
        Assert.Equal(FlowApplicationState.Closed, app.ReadSnapshot().State);
        Assert.Empty(app.ReadSnapshot().Parcels);
        Assert.Equal(1, notifications);
        Assert.Equal(ParcelState.Created, fixture.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Fact]
    public void DurableReads_DoNotPublishAndDeliveryRefreshExposesNewCapabilities()
    {
        using var directory = new DurableTestDirectory();
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime);
        long revision = session.Revision;
        using var app = new FlowApplication(session.Runtime, session.Execute, _ => { });
        FlowSnapshot initial = app.ReadSnapshot();
        app.Refresh();
        Assert.Same(initial, app.ReadSnapshot());
        Assert.Equal(revision, session.Revision);
        Assert.Equal(FlowCommandStatus.Applied, app.Execute(Command(initial, FlowParcelAction.Reserve)).Status);
        session.Execute(runtime => runtime.AdvanceTo(4));
        app.Refresh();

        FlowParcelSnapshot delivered = Assert.Single(app.ReadSnapshot().Parcels);
        Assert.Equal(ParcelState.Delivered, delivered.State);
        Assert.Equal(FlowParcelActions.None, delivered.Actions);
        Assert.Equal(2, app.ReadSnapshot().Revision);
        Assert.Equal(FlowCommandStatus.Conflict, app.Execute(Command(initial, FlowParcelAction.Cancel)).Status);
    }

    [Fact]
    public void ProviderFailure_ClosesApplicationAndPreservesRecoverableOutcome()
    {
        using var directory = new DurableTestDirectory();
        bool armed = false;
        using var session = DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime,
            stage => { if (armed && stage == DurableCommitStage.AfterAcknowledgement) throw new FormatException("commit"); });
        var errors = new List<Exception>();
        using var app = new FlowApplication(session.Runtime, session.Execute, errors.Add);
        armed = true;

        Assert.Equal(FlowCommandStatus.Faulted, app.Execute(Command(app.ReadSnapshot(), FlowParcelAction.Reserve)).Status);
        Assert.Equal(FlowApplicationState.Faulted, app.ReadSnapshot().State);
        Assert.Single(errors);
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(ParcelState.Reserved, reopened.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Single(reopened.GetPortSnapshot(CheckpointFixture.Origin).Inventory);
    }

    [Theory]
    [InlineData(ParcelState.ExtractionUncertain, ParcelState.InTransit)]
    [InlineData(ParcelState.DeliveryUncertain, ParcelState.Delivered)]
    public void ReconcileCommand_SettlesRetainedReceiptWithoutRepeatingPhysicalTransfer(ParcelState uncertain, ParcelState expected)
    {
        var fixture = new CheckpointFixture();
        fixture.Reach(uncertain);
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        FlowSnapshot snapshot = app.ReadSnapshot();
        Assert.True((Assert.Single(snapshot.Parcels).Actions & FlowParcelActions.ReconcileTransfer) != 0);
        Assert.Equal(FlowCommandStatus.Applied, app.Execute(Command(snapshot, FlowParcelAction.ReconcileTransfer)).Status);
        Assert.Equal(expected, Assert.Single(app.ReadSnapshot().Parcels).State);
        Assert.Equal(1, app.ReadSnapshot().Revision);
        Assert.Empty(fixture.Source.Inner.CaptureCheckpoint().Inventory);
        Assert.Equal(expected == ParcelState.Delivered ? 1 : 0, fixture.DestinationPort.Inner.CaptureCheckpoint().Inventory.Length);
    }

    [Fact]
    public void WrongThread_CannotReadExecuteOrCloseApplication()
    {
        var fixture = new CheckpointFixture();
        using var app = new FlowApplication(fixture.Runtime, action => action(fixture.Runtime), _ => { });
        FlowParcelCommand command = Command(app.ReadSnapshot(), FlowParcelAction.Reserve);
        Exception? read = null, execute = null, close = null;
        var thread = new Thread(() =>
        {
            read = Record.Exception(() => app.ReadSnapshot());
            execute = Record.Exception(() => app.Execute(command));
            close = Record.Exception(app.Dispose);
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(read);
        Assert.IsType<InvalidOperationException>(execute);
        Assert.IsType<InvalidOperationException>(close);
        Assert.Equal(FlowApplicationState.Active, app.ReadSnapshot().State);
        Assert.Equal(0, fixture.Runtime.PendingOperationCount);
    }

    internal static FlowParcelCommand Command(FlowSnapshot snapshot, FlowParcelAction action)
        => new(snapshot.SessionId, snapshot.Revision, CheckpointFixture.Parcel.Value, action);
}
