using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Shipments;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

public sealed class FlowUiAcceptanceNetworkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void ShippingCreatesOneRealReservedParcelAndFencesRepeatedRequests(int? quantity)
    {
        using var world = Network();
        FlowSnapshot before = world.ReadSnapshot();
        FlowInventorySlot captured = Assert.Single(world.ReadInventory(world.SourceStation));
        FlowSendCommand command = Send(world) with { Quantity = quantity };
        FlowCommandResult result = world.Execute(command);
        Assert.Equal(FlowCommandStatus.Applied, result.Status);
        FlowParcelSnapshot parcel = Assert.Single(world.ReadSnapshot().Parcels);
        Assert.Equal(world.Parcel, parcel.Id);
        Assert.Equal(quantity ?? captured.Quantity, parcel.Quantity);
        Assert.Equal(ParcelState.Reserved, parcel.State);
        Assert.Equal(before.Revision + 1, result.Revision);
        Assert.Equal(1, world.CreateEffects);
        Assert.Empty(world.ReadInventory(world.SourceStation));
        Assert.Equal(7, captured.Quantity);
        Assert.Equal(20 - parcel.Quantity, world.PreviewRoute(world.SourceStation, world.DestinationStation).AvailableUnits);
        Assert.Equal(FlowRejectionCode.StaleRevision, world.Execute(command).Code);
        Assert.Equal(FlowRejectionCode.WorkLimit, world.Execute(command with { ExpectedRevision = result.Revision }).Code);
        Assert.Equal(1, world.CreateEffects);
        Assert.Single(world.ReadNetwork().Transport.Parcels);
    }

    [Theory]
    [InlineData("session", FlowCommandStatus.Conflict, FlowRejectionCode.StaleSession)]
    [InlineData("revision", FlowCommandStatus.Conflict, FlowRejectionCode.StaleRevision)]
    [InlineData("source", FlowCommandStatus.InvalidCommand, FlowRejectionCode.InvalidCommand)]
    [InlineData("destination", FlowCommandStatus.InvalidCommand, FlowRejectionCode.InvalidCommand)]
    [InlineData("slot", FlowCommandStatus.InvalidCommand, FlowRejectionCode.InvalidCommand)]
    [InlineData("fingerprint", FlowCommandStatus.Conflict, FlowRejectionCode.StateChanged)]
    [InlineData("zero", FlowCommandStatus.InvalidCommand, FlowRejectionCode.InvalidCommand)]
    [InlineData("excess", FlowCommandStatus.InvalidCommand, FlowRejectionCode.InvalidCommand)]
    public void InvalidCaptureHasNoCreateEffectAndDoesNotConsumeTheValidFixture(string field,
        FlowCommandStatus status, FlowRejectionCode code)
    {
        using var world = Network();
        FlowSnapshot before = world.ReadSnapshot();
        FlowSendCommand valid = Send(world);
        FlowSendCommand invalid = field switch
        {
            "session" => valid with { SessionId = Guid.NewGuid() },
            "revision" => valid with { ExpectedRevision = valid.ExpectedRevision - 1 },
            "source" => valid with { Source = Guid.NewGuid() },
            "destination" => valid with { Destination = valid.Source },
            "slot" => valid with { Slot = 1 },
            "fingerprint" => valid with { Fingerprint = new string('f', 64) },
            "zero" => valid with { Quantity = 0 },
            "excess" => valid with { Quantity = 8 },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        FlowCommandResult result = world.Execute(invalid);
        Assert.Equal(status, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Same(before, world.ReadSnapshot());
        Assert.Empty(world.ReadNetwork().Transport.Parcels);
        Assert.Single(world.ReadInventory(world.SourceStation));
        Assert.Equal(0, world.CreateEffects);
        Assert.Equal(FlowCommandStatus.Applied, world.Execute(valid).Status);
        Assert.Equal(1, world.CreateEffects);
    }

    [Fact]
    public void DisconnectedRouteRejectsCreateWithoutMutatingInventoryOrTransport()
    {
        using var world = Network(connected: false);
        FlowSnapshot before = world.ReadSnapshot();
        Assert.False(world.PreviewRoute(world.SourceStation, world.DestinationStation).Found);
        Assert.Equal(FlowRejectionCode.RouteUnavailable, world.Execute(Send(world)).Code);
        Assert.Same(before, world.ReadSnapshot());
        Assert.Empty(before.Parcels);
        Assert.Single(world.ReadInventory(world.SourceStation));
        Assert.Equal(0, world.CreateEffects);
    }

    [Fact]
    public void PublicationObserversCannotReenterCreateOrParcelCommands()
    {
        using var world = Network();
        FlowSendCommand command = Send(world);
        FlowCommandResult? nestedCreate = null, nestedParcel = null;
        FlowNetworkSnapshot? observed = null;
        void Observer(long revision)
        {
            observed = world.ReadNetwork();
            nestedCreate = world.Execute(command with { ExpectedRevision = revision });
            nestedParcel = world.Execute(new FlowParcelCommand(command.SessionId, revision, world.Parcel, FlowParcelAction.Cancel));
        }
        world.RevisionChanged += Observer;
        try
        {
            Assert.Equal(FlowCommandStatus.Applied, world.Execute(command).Status);
            Assert.Equal(FlowRejectionCode.OperationPending, nestedCreate!.Code);
            Assert.Equal(FlowRejectionCode.OperationPending, nestedParcel!.Code);
            Assert.Equal(ParcelState.Reserved, Assert.Single(observed!.Transport.Parcels).State);
            Assert.Equal(1, world.CreateEffects);
            Assert.Equal(0, world.Commands);
            Assert.Empty(world.Errors);
        }
        finally { world.RevisionChanged -= Observer; }
        Assert.Equal(0, world.Subscribers);
    }

    [Fact]
    public void DeliveryRejectionCanRetryTheExistingParcelWithoutAnotherCreate()
    {
        using var world = Network();
        world.AcceptNetworkDelivery(false);
        Assert.Equal(FlowCommandStatus.Applied, world.Execute(Send(world)).Status);
        Assert.Equal(1, world.AdvanceNetworkTo(1));
        Assert.Equal(ParcelState.InTransit, Assert.Single(world.ReadSnapshot().Parcels).State);
        Assert.Equal(2, world.AdvanceNetworkTo(9));
        FlowSnapshot rejected = world.ReadSnapshot();
        Assert.Equal(ParcelState.DeliveryRejected, Assert.Single(rejected.Parcels).State);
        Assert.False(rejected.Parcels[0].Availability.Cancel.Available);
        Assert.True(rejected.Parcels[0].Availability.RetryDelivery.Available);
        world.AcceptNetworkDelivery(true);
        var retry = new FlowParcelCommand(rejected.SessionId, rejected.Revision, world.Parcel, FlowParcelAction.RetryDelivery);
        Assert.Equal(FlowCommandStatus.Applied, world.Execute(retry).Status);
        Assert.Equal(1, world.AdvanceNetworkTo(10));
        Assert.Equal(ParcelState.Delivered, Assert.Single(world.ReadSnapshot().Parcels).State);
        Assert.Equal(1, world.CreateEffects);
        Assert.Equal(1, world.EffectAttempts);
    }

    [Fact]
    public void PausedAndClosedOwnersCannotCreateOrAdvanceTheirFakeTransport()
    {
        using var world = Network();
        FlowSendCommand command = Send(world);
        world.SetAvailability(FlowApplicationState.Paused);
        Assert.Equal(FlowRejectionCode.Paused, world.Execute(command).Code);
        Assert.Throws<InvalidOperationException>(() => world.AdvanceNetworkTo(8));
        Assert.Empty(world.ReadInventory(world.SourceStation));
        world.CloseApplication();
        Assert.Equal(FlowRejectionCode.SessionClosed, world.Execute(command).Code);
        Assert.Throws<InvalidOperationException>(() => world.AdvanceNetworkTo(8));
        Assert.Empty(world.ReadNetwork().Stations);
        Assert.Equal(0, world.CreateEffects);
    }

    [Theory]
    [InlineData(FlowApplicationState.Paused)]
    [InlineData(FlowApplicationState.RecoveryRequired)]
    [InlineData(FlowApplicationState.Closed)]
    public void InactiveOwnerRejectsFixturePreparationBeforeAddingAnyStation(FlowApplicationState state)
    {
        using var world = new FlowUiAcceptanceWorld(false);
        SetState(world, state);
        FlowSnapshot before = world.ReadSnapshot();
        Assert.Throws<InvalidOperationException>(() => world.PrepareNetwork());
        Assert.Same(before, world.ReadSnapshot());
        Assert.Empty(world.ReadNetwork().Stations);
        Assert.Equal(0, world.CreateEffects);
        if (state != FlowApplicationState.Closed)
        {
            world.SetAvailability(FlowApplicationState.Active);
            world.PrepareNetwork();
            Assert.Equal(2, world.ReadNetwork().Stations.Count);
        }
    }

    [Theory]
    [InlineData(FlowApplicationState.Paused)]
    [InlineData(FlowApplicationState.RecoveryRequired)]
    [InlineData(FlowApplicationState.Closed)]
    public void InactiveOwnerRejectsFakeProviderMutationAndClockAdvance(FlowApplicationState state)
    {
        using var world = Network();
        SetState(world, state);
        FlowSnapshot before = world.ReadSnapshot();
        Assert.Throws<InvalidOperationException>(() => world.AcceptNetworkDelivery(false));
        Assert.Throws<InvalidOperationException>(() => world.AdvanceNetworkTo(9));
        Assert.Same(before, world.ReadSnapshot());
        Assert.Equal(0, world.CreateEffects);
        if (state != FlowApplicationState.Closed)
        {
            world.SetAvailability(FlowApplicationState.Active);
            Assert.Equal(FlowCommandStatus.Applied, world.Execute(Send(world)).Status);
            Assert.Equal(3, world.AdvanceNetworkTo(9));
            Assert.Equal(ParcelState.Delivered, Assert.Single(world.ReadSnapshot().Parcels).State);
        }
    }

    private static void SetState(FlowUiAcceptanceWorld world, FlowApplicationState state)
    {
        if (state == FlowApplicationState.Closed) world.CloseApplication();
        else world.SetAvailability(state);
    }

    private static FlowUiAcceptanceWorld Network(bool connected = true)
    {
        var world = new FlowUiAcceptanceWorld(false);
        world.PrepareNetwork(connected);
        return world;
    }
    private static FlowSendCommand Send(FlowUiAcceptanceWorld world)
    {
        FlowSnapshot snapshot = world.ReadSnapshot();
        FlowInventorySlot slot = Assert.Single(world.ReadInventory(world.SourceStation));
        return new(snapshot.SessionId, snapshot.Revision, world.SourceStation, world.DestinationStation, slot.Index, slot.Fingerprint);
    }
}
