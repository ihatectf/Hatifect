using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class ShipmentAuthoringTests
{
    [Fact]
    public void ItemChangedWhileAcquiringMutexRejectsOldFingerprintUnderTheLease()
    {
        var world = new GameSessionWorld();
        var mutex = new AdmissionMutex();
        using FlowGameSession session = world.Open(locks: new FlowChestLocks(() => true, world.Errors.Add, _ => mutex));
        world.Configure(session);
        FlowSnapshot before = session.ReadSnapshot();
        FlowLinkSnapshot link = Assert.Single(before.Links);
        FlowInventorySlot selected = Assert.Single(session.ReadInventory(link.Origin));
        mutex.Acquiring = () => world.Source.Items[0].Stack = 7;
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(new FlowSendCommand(before.SessionId, before.Revision,
            link.Origin, link.Destination, selected.Index, selected.Fingerprint)).Status);
        Assert.False(mutex.IsHeld);
        Assert.Empty(session.ReadSnapshot().Parcels);
        Assert.False(world.Source.Items[0].modData.ContainsKey(ChestInventoryAccess.CargoKey));
        Assert.Equal(7, world.Source.Items[0].Stack);
        Assert.Empty(session.BeginSave().Payloads);
    }

    private sealed class AdmissionMutex : IFlowInventoryMutex
    {
        internal Action? Acquiring;
        public bool IsLocked => IsHeld;
        public bool IsHeld { get; private set; }
        public void Request(Action acquired, Action failed) { Acquiring?.Invoke(); IsHeld = true; acquired(); }
        public void Release() => IsHeld = false;
    }

    [Fact]
    public void InventoryObserverCannotReenterCommandsOrLeaveAnOrphanPayload()
    {
        var world = new GameSessionWorld();
        world.Source.Items.Add(new StardewValley.Object { ItemId = "388", Stack = 2 });
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid parcel = session.Send("source", "destination", 0);
        FlowSnapshot snapshot = session.ReadSnapshot();
        FlowLinkSnapshot link = Assert.Single(snapshot.Links);
        FlowInventorySlot wood = Assert.Single(session.ReadInventory(link.Origin), value => value.Index == 1);
        FlowCommandStatus? nested = null;
        world.Source.Items.OnSlotChanged += (_, _, _, item) =>
        {
            if (item is not null) return;
            nested = session.Execute(new FlowSendCommand(snapshot.SessionId, snapshot.Revision, link.Origin, link.Destination, wood.Index, wood.Fingerprint)).Status;
            Assert.Throws<InvalidOperationException>(() => session.RenameStation("source", "unexpected"));
            Assert.Equal(FlowCommandStatus.Rejected, session.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, parcel, FlowParcelAction.Cancel)).Status);
        };
        session.Tick(true);
        Assert.Equal(FlowCommandStatus.Rejected, nested);
        Assert.False(session.IsFaulted);
        Assert.Equal("source", session.StationName(link.Origin));
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        Assert.Single(saved.Payloads);
        var nextWorld = world.Clone();
        using FlowGameSession restored = nextWorld.Open(saved);
        for (int i = 0; i < 5; i++) restored.Tick(true);
        Assert.Equal(2, nextWorld.Source.Items[1].Stack);
        Assert.Equal(8, Assert.Single(nextWorld.Destination.Items).Stack);
        Assert.Single(restored.ReadSnapshot().Parcels);
        Assert.Empty(nextWorld.Errors);
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("metadata")]
    [InlineData("replacement")]
    public void ChangedSlotRejectsStaleUiSendBeforeTaggingOrAdmittingCargo(string change)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowLinkSnapshot link = Assert.Single(session.ReadSnapshot().Links);
        FlowInventorySlot slot = Assert.Single(session.ReadInventory(link.Origin));
        FlowSnapshot before = session.ReadSnapshot();
        if (change == "quantity") world.Source.Items[0].Stack--;
        else if (change == "metadata") world.Source.Items[0].modData["test/new"] = "changed";
        else world.Source.Items[0] = new StardewValley.Object { ItemId = "388", Stack = 8 };
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(new FlowSendCommand(before.SessionId, before.Revision,
            link.Origin, link.Destination, slot.Index, slot.Fingerprint)).Status);
        Assert.Empty(session.ReadSnapshot().Parcels);
        Assert.Empty(world.Destination.Items);
        Assert.False(world.Source.Items[0].modData.ContainsKey(ChestInventoryAccess.CargoKey));
        FlowInventorySlot refreshed = Assert.Single(session.ReadInventory(link.Origin));
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowSendCommand(before.SessionId, before.Revision,
            link.Origin, link.Destination, refreshed.Index, refreshed.Fingerprint)).Status);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Null(world.Source.Items[0]);
        Assert.Equal(refreshed.Quantity, Assert.Single(world.Destination.Items).Stack);
        Assert.Equal(refreshed.ItemKey, world.Destination.Items[0].QualifiedItemId);
    }

    [Fact]
    public void InventoryProjectionIsDetachedAndExcludesUnsupportedPayloads()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid source = Assert.Single(session.ReadSnapshot().Links).Origin;
        var snapshot = session.ReadInventory(source);
        world.Source.Items[0].Stack = 2;
        world.Source.Items.Add(new StardewValley.Object { ItemId = "388", Stack = 1000 });
        Assert.Equal(8, Assert.Single(snapshot).Quantity);
        Assert.Equal(2, Assert.Single(session.ReadInventory(source)).Quantity);
        Assert.Empty(session.ReadInventory(Guid.NewGuid()));
        session.BeginSave();
        Assert.Empty(session.ReadInventory(source));
    }
}
