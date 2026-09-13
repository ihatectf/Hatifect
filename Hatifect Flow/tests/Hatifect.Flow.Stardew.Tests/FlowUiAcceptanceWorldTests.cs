using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Shipments;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowUiAcceptanceWorldTests
{
    [Fact]
    public void NativeUiFixturePublishesRealDiagnosticCargoAndKeepsPriorSnapshots()
    {
        using var world = new FlowUiAcceptanceWorld(false);
        FlowSnapshot empty = world.ReadSnapshot();
        var revisions = new List<long>();
        Action<long> observer = revisions.Add;
        world.RevisionChanged += observer;
        try
        {
            world.Seed();
            FlowSnapshot seeded = world.ReadSnapshot();
            FlowParcelSnapshot parcel = Assert.Single(seeded.Parcels);

            Assert.Equal(FlowProviderMode.DiagnosticFake, seeded.ProviderMode);
            Assert.Equal(empty.SessionId, seeded.SessionId);
            Assert.Equal(empty.Revision + 1, seeded.Revision);
            Assert.Equal(new[] { seeded.Revision }, revisions);
            Assert.Equal(2, seeded.Stations.Count);
            Assert.Single(seeded.Links);
            Assert.Equal(world.Parcel, parcel.Id);
            Assert.Equal("(O)378", parcel.ItemKey);
            Assert.Equal(7, parcel.Quantity);
            Assert.Equal(ParcelState.Created, parcel.State);
            Assert.Equal(FlowParcelActions.Reserve | FlowParcelActions.Cancel, parcel.Actions);
            Assert.All(new[] { FlowParcelAction.RetryDelivery, FlowParcelAction.ReconcileTransfer, FlowParcelAction.ReturnToSource },
                action => Assert.Equal(FlowRejectionCode.InvalidState, parcel.Availability[action].Code));
            Assert.Equal(new[] { "A source", "A destination" }, seeded.Stations.Select(station => world.StationName(station.Id)));
            Assert.Empty(empty.Parcels);
            Assert.Equal(0, world.EffectAttempts);
            Assert.Throws<InvalidOperationException>(world.Seed);
            Assert.Same(seeded, world.ReadSnapshot());
        }
        finally { world.RevisionChanged -= observer; }
        Assert.Equal(0, world.Subscribers);
    }

    [Fact]
    public void NativeUiWorldsRejectPriorSessionCommandsBeforeAnyNewWorldEffect()
    {
        using var first = new FlowUiAcceptanceWorld(false);
        using var second = new FlowUiAcceptanceWorld(true);
        using var reloaded = new FlowUiAcceptanceWorld(false);
        first.Seed(); second.Seed(); reloaded.Seed();
        FlowSnapshot old = first.ReadSnapshot();
        Assert.Equal(first.Parcel, reloaded.Parcel);
        Assert.NotEqual(first.Parcel, second.Parcel);
        Assert.Equal(13, Assert.Single(second.ReadSnapshot().Parcels).Quantity);
        foreach (var current in new[] { second, reloaded })
        {
            FlowSnapshot before = current.ReadSnapshot();
            Assert.NotEqual(old.SessionId, before.SessionId);

            FlowCommandResult result = current.Execute(new FlowParcelCommand(old.SessionId, before.Revision,
                first.Parcel, FlowParcelAction.Reserve));

            Assert.Equal(FlowCommandStatus.Conflict, result.Status);
            Assert.Equal(FlowRejectionCode.StaleSession, result.Code);
            Assert.Same(before, current.ReadSnapshot());
            Assert.Equal(0, current.EffectAttempts);
        }
        var active = reloaded.ReadSnapshot();
        Assert.Equal(FlowCommandStatus.Applied, reloaded.Execute(new FlowParcelCommand(active.SessionId,
            active.Revision, reloaded.Parcel, FlowParcelAction.Reserve)).Status);
        Assert.Equal(ParcelState.Reserved, Assert.Single(reloaded.ReadSnapshot().Parcels).State);
        var reserved = Assert.Single(reloaded.ReadSnapshot().Parcels);
        Assert.Equal(FlowParcelActions.Cancel, reserved.Actions);
        Assert.All(new[] { FlowParcelAction.Reserve, FlowParcelAction.RetryDelivery, FlowParcelAction.ReconcileTransfer, FlowParcelAction.ReturnToSource },
            action => Assert.Equal(FlowRejectionCode.InvalidState, reserved.Availability[action].Code));
        Assert.Equal(ParcelState.Created, Assert.Single(old.Parcels).State);
        Assert.Equal(1, reloaded.EffectAttempts);
    }

    [Fact]
    public void ControlledFailureUsesRealFaultTransitionOnceAndOwnerCloseRemainsTerminal()
    {
        using var world = new FlowUiAcceptanceWorld(false);
        world.Seed();
        var before = world.ReadSnapshot();
        world.FailNextOperation = true;

        var result = world.Execute(new FlowParcelCommand(before.SessionId, before.Revision, world.Parcel, FlowParcelAction.Reserve));

        Assert.Equal(FlowCommandStatus.Faulted, result.Status);
        var fault = world.ReadSnapshot();
        Assert.Equal(FlowApplicationState.Faulted, fault.State);
        Assert.Empty(fault.Parcels);
        Assert.Equal(1, world.EffectAttempts);
        Assert.Same(world.InjectedFailure, Assert.Single(world.Errors));
        Assert.Equal(FlowCommandStatus.SessionClosed, world.Execute(new FlowParcelCommand(fault.SessionId,
            fault.Revision, world.Parcel, FlowParcelAction.Reserve)).Status);
        Assert.Same(fault, world.ReadSnapshot());
        world.CloseApplication();
        Assert.Equal(FlowApplicationState.Closed, world.ReadSnapshot().State);
        Assert.Equal(fault.Revision + 1, world.ReadSnapshot().Revision);
        Assert.Equal(FlowApplicationState.Faulted, fault.State);
        Assert.Equal(1, world.EffectAttempts);
        Assert.Single(world.Errors);
    }

    [Fact]
    public void FailedUnsubscribeKeepsTheActualObserverUntilRetryAndCannotClaimCleanup()
    {
        using var world = new FlowUiAcceptanceWorld(false);
        int notifications = 0;
        Action<long> observer = _ => notifications++;
        world.RevisionChanged += observer;
        try
        {
            world.FailNextUnsubscribe = true;
            Assert.Same(world.UnsubscribeFailure,
                Assert.Throws<InvalidOperationException>(() => world.RevisionChanged -= observer));
            Assert.Equal(1, world.Subscribers);
            Assert.Throws<InvalidOperationException>(world.Dispose);

            world.SetAvailability(FlowApplicationState.Paused);

            Assert.Equal(1, notifications);
            Assert.Equal(FlowApplicationState.Paused, world.ReadSnapshot().State);
        }
        finally { world.RevisionChanged -= observer; }
        Assert.Equal(0, world.Subscribers);
        world.SetAvailability(FlowApplicationState.Active);
        Assert.Equal(1, notifications);
        world.Dispose();
        Assert.Equal(FlowApplicationState.Closed, world.ReadSnapshot().State);
        Assert.Throws<InvalidOperationException>(() => world.RevisionChanged += observer);
        Assert.Throws<InvalidOperationException>(world.Seed);
    }
}
