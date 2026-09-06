using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using StardewValley;
using StardewValley.Objects;
using StardewValley.SaveSerialization;
using Xunit;
using SObject = StardewValley.Object;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowGameSessionTests
{
    [Fact]
    public void DuplicateLocationAndOversizedCargo_RejectBeforeCreatingUnsavableState()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        world.Destination = InventoryFixture.CreateChest();
        Assert.Throws<InvalidOperationException>(() => session.RegisterStation("replacement", "Farm", 1, 0, world.Destination));
        Item item = world.Source.Items[0];
        item.modData["test/large"] = new string('x', FlowItemCodec.MaxPayloadLength);
        Assert.Throws<InvalidOperationException>(() => session.Send("source", "destination", 0));
        Assert.False(item.modData.ContainsKey(ChestInventoryAccess.CargoKey));
        Assert.Empty(session.Application.ReadSnapshot().Parcels);
        Assert.Equal(8, item.Stack);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        Assert.Empty(saved.Payloads);
        session.Dispose();
        using FlowGameSession restored = world.Open(saved);
        Assert.Equal(2, restored.Application.ReadSnapshot().Stations.Count);
    }

    [Fact]
    public void RollingBackWholeGameSave_RestoresSourceAndQueuedJourneyTogether()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        GameSessionWorld savedWorld = world.Clone();
        session.EndSave();
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(8, Assert.Single(world.Destination.Items).Stack);
        session.Dispose(); // the player exits without saving the delivery
        using FlowGameSession rolledBack = savedWorld.Open(saved);
        Assert.Equal(8, savedWorld.Source.Items[0].Stack);
        Assert.Empty(savedWorld.Destination.Items);
        for (int i = 0; i < 5; i++) rolledBack.Tick(true);
        Assert.Null(savedWorld.Source.Items[0]);
        Assert.Equal(8, Assert.Single(savedWorld.Destination.Items).Stack);
        Assert.Equal(ParcelState.Delivered, Assert.Single(rolledBack.Application.ReadSnapshot().Parcels).State);
    }

    [Fact]
    public void InFlightSaveReload_DeliversOnceWithFullItemFidelityAndRetiresOldUiCommands()
    {
        var world = new GameSessionWorld();
        using FlowGameSession original = world.Open();
        world.Configure(original);
        Guid parcel = original.Send("source", "destination", 0);
        FlowSnapshot before = original.Application.ReadSnapshot();
        Assert.Equal(ParcelState.Reserved, Assert.Single(before.Parcels).State);
        Assert.Equal(8, world.Source.Items[0].Stack);
        original.Tick(true);
        Assert.Null(world.Source.Items[0]);
        Assert.Equal(ParcelState.InTransit, Assert.Single(original.Application.ReadSnapshot().Parcels).State);
        FlowGameSave saved = GameSessionWorld.Clone(original.BeginSave());
        GameSessionWorld reloadedWorld = world.Clone();
        original.Dispose();
        Assert.Equal(FlowCommandStatus.SessionClosed, original.Application.Execute(new FlowParcelCommand(before.SessionId, before.Revision, parcel, FlowParcelAction.Cancel)).Status);

        using FlowGameSession resumed = reloadedWorld.Open(saved);
        Assert.NotEqual(before.SessionId, resumed.Application.ReadSnapshot().SessionId);
        Assert.Null(reloadedWorld.Source.Items[0]);
        Assert.Empty(reloadedWorld.Destination.Items);
        for (int tick = 0; tick < 10; tick++) resumed.Tick(true);
        Assert.Equal(ParcelState.Delivered, Assert.Single(resumed.Application.ReadSnapshot().Parcels).State);
        var delivered = Assert.IsType<SObject>(Assert.Single(reloadedWorld.Destination.Items));
        Assert.Equal(8, delivered.Stack);
        Assert.Equal(4, delivered.Quality);
        Assert.Equal("(O)348", delivered.QualifiedItemId);
        Assert.Equal(SObject.PreserveType.Wine, delivered.preserve.Value);
        Assert.Equal("613", delivered.preservedParentSheetIndex.Value);
        Assert.Equal("saved & <metadata>", delivered.modData["test/value"]);
        Assert.Empty(reloadedWorld.Errors);
    }

    [Fact]
    public void TakenDelivery_IsNeverRecreatedFromHistoricalCheckpoint()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(8, world.Destination.Items[0].Stack);
        world.Destination.Items[0] = null; // player took the delivered item before the next save
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        var nextWorld = world.Clone();
        session.Dispose();
        using FlowGameSession next = nextWorld.Open(saved);
        for (int i = 0; i < 10; i++) next.Tick(true);
        Assert.All(nextWorld.Source.Items, item => Assert.Null(item));
        Assert.All(nextWorld.Destination.Items, item => Assert.Null(item));
        Assert.Equal(ParcelState.Delivered, Assert.Single(next.Application.ReadSnapshot().Parcels).State);
        Assert.Empty(nextWorld.Errors);
    }

    [Fact]
    public void SaveBarrierAndAuthorityLoss_RejectCommandsAndPauseTicksWithoutFaulting()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid parcel = session.Send("source", "destination", 0);
        FlowSnapshot snapshot = session.Application.ReadSnapshot();
        var cancel = new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, parcel, FlowParcelAction.Cancel);
        session.BeginSave();
        session.Tick(true);
        Assert.Equal(0, session.Now);
        Assert.Equal(FlowCommandStatus.Rejected, session.Application.Execute(cancel).Status);
        session.EndSave();
        world.Authority = false;
        session.Tick(true);
        Assert.Equal(FlowCommandStatus.Rejected, session.Application.Execute(cancel).Status);
        Assert.Equal(8, world.Source.Items[0].Stack);
        world.Authority = true;
        session.Tick(false);
        FlowSnapshot resumed = session.Application.ReadSnapshot();
        Assert.Equal(FlowCommandStatus.Conflict, session.Application.Execute(cancel).Status);
        Assert.Equal(FlowCommandStatus.Applied, session.Application.Execute(cancel with { ExpectedRevision = resumed.Revision }).Status);
        Assert.False(session.IsFaulted);
        Assert.Equal(ParcelState.Cancelled, Assert.Single(session.Application.ReadSnapshot().Parcels).State);
    }

    [Fact]
    public void ReplacedDestination_RejectsThenRetriesAgainstOriginalBinding()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid parcel = session.Send("source", "destination", 0);
        Chest original = world.Destination;
        world.Destination = InventoryFixture.CreateChest();
        for (int i = 0; i < 5; i++) session.Tick(true);
        FlowSnapshot rejected = session.Application.ReadSnapshot();
        Assert.Equal(ParcelState.DeliveryRejected, Assert.Single(rejected.Parcels).State);
        Assert.Empty(world.Destination.Items);
        world.Destination = original;
        Assert.Equal(FlowCommandStatus.Applied, session.Application.Execute(new FlowParcelCommand(rejected.SessionId, rejected.Revision, parcel, FlowParcelAction.RetryDelivery)).Status);
        session.Tick(true);
        Assert.Equal(8, Assert.Single(original.Items).Stack);
        Assert.Equal(ParcelState.Delivered, Assert.Single(session.Application.ReadSnapshot().Parcels).State);
    }

    [Fact]
    public void ChangedOrAlreadyAssignedSource_CannotLoseOrDuplicateThePhysicalStack()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        Assert.Throws<InvalidOperationException>(() => session.Send("source", "destination", 0));
        world.Source.Items[0].Stack = 7;
        session.Tick(true);
        Assert.Equal(ParcelState.Cancelled, Assert.Single(session.Application.ReadSnapshot().Parcels).State);
        Assert.Equal(7, world.Source.Items[0].Stack);
        Assert.Empty(world.Destination.Items);
        session.Send("source", "destination", 0);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(7, Assert.Single(world.Destination.Items).Stack);
        Assert.Null(world.Source.Items[0]);
        Assert.Equal(2, session.Application.ReadSnapshot().Parcels.Count);
    }

    [Fact]
    public void AmbiguousInventoryCallback_IsFencedAcrossSaveReload()
    {
        var world = new GameSessionWorld();
        world.Source.Items.Add(new SObject { ItemId = "388", Stack = 1 });
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        bool entered = false;
        world.Source.Items.OnSlotChanged += (inventory, _, _, after) =>
        {
            if (entered || after is not null) return;
            entered = true;
            inventory.RemoveEmptySlots();
            throw new InvalidOperationException("observer compacted inventory");
        };
        session.Tick(true);
        Assert.True(session.IsFaulted);
        Assert.Single(world.Errors);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        Assert.True(saved.RequiresRecovery);
        var nextWorld = world.Clone();
        session.Dispose();
        using FlowGameSession next = nextWorld.Open(saved);
        Assert.True(next.IsFaulted);
        FlowSnapshot snapshot = next.Application.ReadSnapshot();
        FlowParcelSnapshot parcel = Assert.Single(snapshot.Parcels);
        Assert.Equal(ParcelState.ExtractionUncertain, parcel.State);
        Assert.Equal(FlowCommandStatus.Rejected, next.Application.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, parcel.Id, FlowParcelAction.ReconcileTransfer)).Status);
        for (int i = 0; i < 10; i++) next.Tick(true);
        Assert.Empty(nextWorld.Destination.Items);
        Assert.Equal("(O)388", Assert.Single(nextWorld.Source.Items).QualifiedItemId);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("payload")]
    [InlineData("station")]
    public void InvalidSave_IsRejectedBeforeAnyPhysicalInventoryMutation(string change)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        saved = change switch
        {
            "identity" => saved with { SaveId = 2 },
            "version" => saved with { Version = 99 },
            "payload" => saved with { Payloads = Array.Empty<CargoPayload>() },
            _ => saved with { Stations = new[] { saved.Stations[0], saved.Stations[0] } }
        };
        Assert.Throws<InvalidDataException>(() => world.Open(saved));
        Assert.Equal(8, world.Source.Items[0].Stack);
        Assert.Empty(world.Destination.Items);
    }
}

internal sealed class GameSessionWorld
{
    internal Chest Source { get; private set; } = InventoryFixture.CreateChest();
    internal Chest Destination { get; set; } = InventoryFixture.CreateChest();
    internal bool Authority { get; set; } = true;
    internal List<Exception> Errors { get; } = new();

    internal GameSessionWorld()
    {
        var wine = new SObject { ItemId = "348", Stack = 8, Quality = 4 };
        wine.preserve.Value = SObject.PreserveType.Wine;
        wine.preservedParentSheetIndex.Value = "613";
        wine.modData["test/value"] = "saved & <metadata>";
        Source.Items.Add(wine);
    }

    internal FlowGameSession Open(FlowGameSave? saved = null, FlowChestLocks? locks = null) => new(1, 1, () => Authority,
        binding => binding.Location == "Farm" ? binding.X == 0 ? Source : binding.X == 1 ? Destination : null : null, Errors.Add, saved, locks);
    internal void Configure(FlowGameSession session)
    {
        session.RegisterStation("source", "Farm", 0, 0, Source);
        session.RegisterStation("destination", "Farm", 1, 0, Destination);
        session.Link("source", "destination", transitTicks: 3);
    }
    internal static FlowGameSave Clone(FlowGameSave value) => JsonSerializer.Deserialize<FlowGameSave>(JsonSerializer.Serialize(value))!;
    internal GameSessionWorld Clone() => new() { Source = CloneChest(Source), Destination = CloneChest(Destination) };
    private static Chest CloneChest(Chest value)
    {
        var xml = new StringBuilder();
        using (XmlWriter writer = XmlWriter.Create(xml)) SaveSerializer.Serialize<Item>(writer, value);
        using var text = new StringReader(xml.ToString());
        using XmlReader reader = XmlReader.Create(text);
        return (Chest)SaveSerializer.Deserialize<Item>(reader);
    }
}
