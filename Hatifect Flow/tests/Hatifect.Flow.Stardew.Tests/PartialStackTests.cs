using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using Hatifect.Flow.Sessions;
using StardewValley;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class PartialStackTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void QueuedInFlightAndDeliveredReloadConserveQuantityAndPermitIndependentRemainder(int ticks)
    {
        var world = new GameSessionWorld();
        string originalXml = FlowItemCodec.Encode(world.Source.Items[0]);
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid parcel = Send(session, 3);
        Assert.Equal(8, world.Source.Items[0].Stack); // Admission has not extracted any units.
        Assert.Equal(3, Assert.Single(session.ReadSnapshot().Parcels).Quantity);
        for (int i = 0; i < ticks; i++) session.Tick(true);
        Assert.Equal(ticks == 0 ? 8 : 5, world.Source.Items[0].Stack);
        FlowGameSave saved = JsonConvert.DeserializeObject<FlowGameSave>(JsonConvert.SerializeObject(session.BeginSave()))!;
        Assert.Equal(2, saved.Version);
        Assert.Equal(8, Assert.Single(saved.Payloads).SourceQuantity);
        var nextWorld = world.Clone();
        session.Dispose();
        using FlowGameSession next = nextWorld.Open(saved);
        for (int i = 0; i < 10; i++) next.Tick(true);
        Assert.Equal(ParcelState.Delivered, Assert.Single(next.ReadSnapshot().Parcels).State);
        AssertItem(originalXml, nextWorld.Source.Items[0], 5, tagged: false);
        AssertItem(originalXml, Assert.Single(nextWorld.Destination.Items), 3, tagged: true);
        Assert.Equal(FlowCommandStatus.Rejected, Action(next, parcel, FlowParcelAction.RetryDelivery).Status);
        Guid second = Send(next, 2);
        Assert.NotEqual(parcel, second);
        for (int i = 0; i < 5; i++) next.Tick(true);
        Assert.Equal(3, nextWorld.Source.Items[0].Stack);
        Assert.Equal(new[] { 2, 3 }, nextWorld.Destination.Items.Select(item => item.Stack).OrderBy(value => value));
        Assert.Equal(8, nextWorld.Source.Items[0].Stack + nextWorld.Destination.Items.Sum(item => item.Stack));
        Assert.Empty(nextWorld.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(1000)]
    public void InvalidQuantityDoesNotTagAdmitOrExtract(int quantity)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        string before = FlowItemCodec.Encode(world.Source.Items[0]);
        FlowCommandResult result = session.Execute(Command(session, quantity));
        Assert.Equal(FlowCommandStatus.InvalidCommand, result.Status);
        Assert.Equal(FlowRejectionCode.InvalidCommand, result.Code);
        Assert.Equal(before, FlowItemCodec.Encode(world.Source.Items[0]));
        Assert.Empty(session.ReadSnapshot().Parcels);
        Assert.Empty(session.BeginSave().Payloads);
    }

    [Fact]
    public void CancellationKeepsWholeSourceAndDuplicateAdmissionCannotClaimSameStack()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        FlowSendCommand command = Command(session, 3);
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(command).Status);
        Assert.Equal(FlowCommandStatus.Conflict, session.Execute(command).Status);
        Assert.Equal(FlowCommandStatus.Rejected, session.Execute(Command(session, 2)).Status);
        Guid parcel = Assert.Single(session.ReadSnapshot().Parcels).Id;
        Assert.Equal(FlowCommandStatus.Applied, Action(session, parcel, FlowParcelAction.Cancel).Status);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(8, world.Source.Items[0].Stack);
        Assert.Empty(world.Destination.Items);
        Send(session, 2);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(6, world.Source.Items[0].Stack);
        Assert.Equal(2, Assert.Single(world.Destination.Items).Stack);
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("metadata")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public void ChangedAdmittedSourceRejectsWithoutSpendingSelectedUnits(string change)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Send(session, 3);
        Item item = world.Source.Items[0];
        if (change == "quantity") item.Stack = 7; // Still enough for 3, but the original fingerprint changed.
        else if (change == "metadata") item.modData["test/value"] = "changed";
        else if (change == "duplicate") world.Source.Items.Add(FlowItemCodec.Decode(FlowItemCodec.Encode(item)));
        else item.modData.Remove(ChestInventoryAccess.CargoKey);
        string[] before = world.Source.Items.Select(FlowItemCodec.Encode).ToArray();
        session.Tick(true);
        Assert.Equal(ParcelState.Cancelled, Assert.Single(session.ReadSnapshot().Parcels).State);
        Assert.Equal(before, world.Source.Items.Select(FlowItemCodec.Encode));
        Assert.Empty(world.Destination.Items);
        Assert.False(session.IsFaulted);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("quantity")]
    [InlineData("metadata")]
    [InlineData("silent")]
    public void RemainderObserverRequiresExactPostWriteEvidenceAndUnknownOutcomeSurvivesReload(string observer)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Send(session, 3);
        world.Source.Items.OnSlotChanged += (_, _, _, remainder) =>
        {
            Assert.NotNull(remainder);
            if (observer is "quantity" or "silent") remainder.Stack--;
            if (observer == "metadata") remainder.modData["test/observer"] = "changed";
            if (observer != "silent") throw new InvalidOperationException("after replacement");
        };
        session.Tick(true);
        bool uncertain = observer != "throw";
        Assert.Equal(uncertain, session.IsFaulted);
        Assert.Equal(uncertain ? ParcelState.ExtractionUncertain : ParcelState.InTransit, Assert.Single(session.ReadSnapshot().Parcels).State);
        if (uncertain)
        {
            FlowRecoveryIssue issue = Assert.Single(session.ReadRecovery());
            Assert.Equal("Missing", issue.Receipt);
            Assert.False(issue.CanReconcile);
        }
        string remainderXml = FlowItemCodec.Encode(world.Source.Items[0]);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        var nextWorld = world.Clone();
        using FlowGameSession next = nextWorld.Open(saved);
        for (int i = 0; i < 10; i++) next.Tick(true);
        Assert.Equal(remainderXml, FlowItemCodec.Encode(nextWorld.Source.Items[0]));
        Assert.Equal(uncertain, next.IsFaulted);
        if (uncertain) Assert.Empty(nextWorld.Destination.Items);
        else Assert.Equal(3, Assert.Single(nextWorld.Destination.Items).Stack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionTagObserverFailureKeepsSaveableEvidenceAndCannotDispatch(bool throws)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Item source = world.Source.Items[0];
        source.modData.OnValueAdded += (key, _) =>
        {
            if (key != ChestInventoryAccess.CargoKey) return;
            if (throws) throw new InvalidOperationException("tag observer failed");
            source.Stack--;
        };
        Assert.Equal(FlowCommandStatus.Faulted, session.Execute(Command(session, 3)).Status);
        Assert.True(session.IsFaulted);
        Assert.Equal(throws ? 8 : 7, source.Stack);
        Assert.Single(session.ReadSnapshot().Parcels);
        Assert.Empty(session.ReadRecovery()); // No transfer intent: no settled result can authorize reconciliation.
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        Assert.True(saved.RequiresRecovery);
        Assert.Equal(8, Assert.Single(saved.Payloads).SourceQuantity);
        var nextWorld = world.Clone();
        using FlowGameSession next = nextWorld.Open(saved);
        for (int i = 0; i < 10; i++) next.Tick(true);
        Assert.True(next.IsFaulted);
        Assert.Equal(throws ? 8 : 7, nextWorld.Source.Items[0].Stack);
        Assert.Empty(nextWorld.Destination.Items);
        FlowSnapshot snapshot = next.ReadSnapshot();
        Assert.Equal(FlowRejectionCode.UnknownOutcome, next.Execute(new FlowRecoveryCommand(snapshot.SessionId, snapshot.Revision,
            Assert.Single(snapshot.Parcels).Id)).Code);
    }

    [Fact]
    public void FullDestinationAndFullReturnSourceRetainOnlySelectedCustodyAcrossRetryAndReload()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Guid parcel = Send(session, 3);
        while (world.Destination.Items.Count < world.Destination.GetActualCapacity())
            world.Destination.Items.Add(new StardewValley.Object { ItemId = "388", Stack = 1 });
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(ParcelState.DeliveryRejected, Assert.Single(session.ReadSnapshot().Parcels).State);
        Assert.Equal(5, world.Source.Items[0].Stack);
        Assert.All(world.Destination.Items, item => Assert.Equal(1, item.Stack));
        Assert.Equal(FlowCommandStatus.Applied, Action(session, parcel, FlowParcelAction.ReturnToSource).Status);
        while (world.Source.Items.Count < world.Source.GetActualCapacity())
            world.Source.Items.Add(new StardewValley.Object { ItemId = "388", Stack = 1 });
        session.Tick(true);
        Assert.Equal(ParcelState.ReturnRejected, Assert.Single(session.ReadSnapshot().Parcels).State);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        var nextWorld = world.Clone();
        nextWorld.Source.Items[1] = null;
        using FlowGameSession next = nextWorld.Open(saved);
        Assert.Equal(FlowCommandStatus.Applied, Action(next, parcel, FlowParcelAction.RetryDelivery).Status);
        for (int i = 0; i < 10; i++) next.Tick(true);
        Assert.Equal(ParcelState.Returned, Assert.Single(next.ReadSnapshot().Parcels).State);
        Assert.Equal(5, nextWorld.Source.Items[0].Stack);
        Assert.Equal(3, nextWorld.Source.Items[1].Stack);
        Assert.Equal(8, nextWorld.Source.Items.Where(item => item.QualifiedItemId == "(O)348").Sum(item => item.Stack));
        Assert.Equal(FlowCommandStatus.Rejected, Action(next, parcel, FlowParcelAction.RetryDelivery).Status);
        Assert.Empty(nextWorld.Errors);
    }

    [Fact]
    public void LegacyWholeStackAggregateMigratesExplicitlyAndWritesVersionTwo()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        var legacyJson = Newtonsoft.Json.Linq.JObject.Parse(JsonConvert.SerializeObject(saved));
        legacyJson["Version"] = 1;
        foreach (var payload in legacyJson["Payloads"]!.Children<Newtonsoft.Json.Linq.JObject>()) payload.Remove("SourceQuantity");
        saved = legacyJson.ToObject<FlowGameSave>()!;
        var nextWorld = world.Clone();
        using FlowGameSession next = nextWorld.Open(saved);
        for (int i = 0; i < 5; i++) next.Tick(true);
        Assert.Null(nextWorld.Source.Items[0]);
        Assert.Equal(8, Assert.Single(nextWorld.Destination.Items).Stack);
        FlowGameSave migrated = next.BeginSave();
        Assert.Equal(2, migrated.Version);
        Assert.Equal(8, Assert.Single(migrated.Payloads).SourceQuantity);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 2)]
    [InlineData(2, 1000)]
    [InlineData(1, 8)]
    public void InvalidSourceQuantityCannotBeMisreadAsLegacyOrTouchInventory(int version, int quantity)
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        Send(session, 3);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        saved = saved with { Version = version, Payloads = saved.Payloads.Select(value => value with { SourceQuantity = quantity }).ToArray() };
        Assert.Throws<InvalidDataException>(() => world.Open(saved));
        Assert.Equal(8, world.Source.Items[0].Stack);
        Assert.Empty(world.Destination.Items);
    }

    private static FlowSendCommand Command(FlowGameSession session, int quantity)
    {
        FlowSnapshot snapshot = session.ReadSnapshot();
        FlowLinkSnapshot link = Assert.Single(snapshot.Links);
        FlowInventorySlot slot = Assert.Single(session.ReadInventory(link.Origin), value => value.Index == 0);
        return new(snapshot.SessionId, snapshot.Revision, link.Origin, link.Destination, slot.Index, slot.Fingerprint) { Quantity = quantity };
    }

    private static Guid Send(FlowGameSession session, int quantity)
    {
        Guid[] previous = session.ReadSnapshot().Parcels.Select(parcel => parcel.Id).ToArray();
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(Command(session, quantity)).Status);
        return Assert.Single(session.ReadSnapshot().Parcels, parcel => !previous.Contains(parcel.Id)).Id;
    }

    private static FlowCommandResult Action(FlowGameSession session, Guid parcel, FlowParcelAction action)
    {
        FlowSnapshot snapshot = session.ReadSnapshot();
        return session.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision, parcel, action));
    }

    private static void AssertItem(string originalXml, Item actual, int quantity, bool tagged)
    {
        Assert.Equal(tagged, actual.modData.ContainsKey(ChestInventoryAccess.CargoKey));
        Item expected = FlowItemCodec.Decode(originalXml);
        expected.Stack = quantity;
        Item detached = FlowItemCodec.Decode(FlowItemCodec.Encode(actual));
        detached.modData.Remove(ChestInventoryAccess.CargoKey);
        Assert.Equal(FlowItemCodec.Encode(expected), FlowItemCodec.Encode(detached));
    }
}
