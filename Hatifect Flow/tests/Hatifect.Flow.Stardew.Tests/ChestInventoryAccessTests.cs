using System;
using System.Linq;
using System.Xml;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using StardewValley;
using StardewValley.Objects;
using Xunit;
using SObject = StardewValley.Object;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class ChestInventoryAccessTests
{
    [Fact]
    public void WholeStackTransfer_PreservesSerializedPropertiesAndDoesNotMergeOtherStacks()
    {
        var fixture = new InventoryFixture();
        Item old = fixture.Source.Items[0];
        fixture.Destination.Items.Add(new SObject { ItemId = "388", Stack = 990, Quality = 2 });
        string payload = FlowItemCodec.Encode(old);

        Assert.Equal(PortResult.Applied, fixture.Access.Apply(fixture.Transfer(PortTransferKind.Extract), payload));
        Assert.Null(fixture.Source.Items[0]);
        Assert.Equal(PortResult.Applied, fixture.Access.Apply(fixture.Transfer(PortTransferKind.Deposit), payload));

        Assert.Equal(990, fixture.Destination.Items[0].Stack);
        Item delivered = fixture.Destination.Items[1];
        Assert.Equal("(O)388", delivered.QualifiedItemId);
        Assert.Equal(12, delivered.Stack);
        Assert.Equal(2, delivered.Quality);
        Assert.Equal("data & <>", delivered.modData["test/metadata"]);
        Assert.Equal(payload, FlowItemCodec.Encode(delivered));
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("metadata")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public void ChangedSource_IsRejectedWithoutRemovingItems(string change)
    {
        var fixture = new InventoryFixture();
        Item original = fixture.Source.Items[0];
        string payload = FlowItemCodec.Encode(original);
        switch (change)
        {
            case "quantity": original.Stack--; break;
            case "metadata": original.modData["test/metadata"] = "changed"; break;
            case "missing": fixture.Source.Items[0] = null; break;
            case "duplicate": fixture.Source.Items.Add(FlowItemCodec.Decode(payload)); break;
        }
        int units = fixture.Source.Items.Where(item => item is not null).Sum(item => item.Stack);
        Assert.Equal(PortResult.Rejected, fixture.Access.Apply(fixture.Transfer(PortTransferKind.Extract), payload));
        Assert.Equal(units, fixture.Source.Items.Where(item => item is not null).Sum(item => item.Stack));
        Assert.Empty(fixture.Destination.Items);
    }

    [Fact]
    public void FullDestination_RejectsWithoutPartialMergeThenUsesFreedSlot()
    {
        var fixture = new InventoryFixture();
        string payload = FlowItemCodec.Encode(fixture.Source.Items[0]);
        for (int slot = 0; slot < fixture.Destination.GetActualCapacity(); slot++)
            fixture.Destination.Items.Add(new SObject { ItemId = "388", Stack = 1 });
        Assert.Equal(PortResult.Rejected, fixture.Access.Apply(fixture.Transfer(PortTransferKind.Deposit), payload));
        Assert.All(fixture.Destination.Items, item => Assert.Equal(1, item.Stack));
        fixture.Destination.Items[5] = null;
        Assert.Equal(PortResult.Applied, fixture.Access.Apply(fixture.Transfer(PortTransferKind.Deposit), payload));
        Assert.Equal(12, fixture.Destination.Items[5].Stack);
        Assert.Equal(47, fixture.Destination.Items.Sum(item => item.Stack));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrNonAuthoritativeWorld_RejectsPhysicalMutation(bool absent)
    {
        var fixture = new InventoryFixture();
        var access = new ChestInventoryAccess(_ => absent ? null : fixture.Source, () => absent, 1);
        string payload = FlowItemCodec.Encode(fixture.Source.Items[0]);
        Assert.Equal(PortResult.Rejected, access.Apply(fixture.Transfer(PortTransferKind.Extract), payload));
        Assert.Equal(12, fixture.Source.Items[0].Stack);
    }

    [Fact]
    public void FridgeInventory_IsRejectedWithoutExtractingItsContents()
    {
        var fixture = new InventoryFixture();
        string payload = FlowItemCodec.Encode(fixture.Source.Items[0]);
        fixture.Source.fridge.Value = true;
        Assert.Equal(PortResult.Rejected, fixture.Access.Apply(fixture.Transfer(PortTransferKind.Extract), payload));
        Assert.Equal(payload, FlowItemCodec.Encode(fixture.Source.Items[0]));
        Assert.Empty(fixture.Destination.Items);
    }

    [Fact]
    public void Codec_RejectsUnsupportedCargoAndExternalEntities()
    {
        Assert.Throws<InvalidOperationException>(() => FlowItemCodec.Encode(new Chest()));
        var machine = new SObject { ItemId = "388", Stack = 1 };
        machine.bigCraftable.Value = true;
        Assert.Throws<InvalidOperationException>(() => FlowItemCodec.Encode(machine));
        // Internal entities would deserialize successfully if DTD processing were accidentally enabled.
        string xml = FlowItemCodec.Encode(new SObject { ItemId = "388", Stack = 1 });
        Exception error = Assert.Throws<InvalidOperationException>(() => FlowItemCodec.Decode("<!DOCTYPE Item [<!ENTITY cargo '388'>]>" + xml.Replace("<itemId>388</itemId>", "<itemId>&cargo;</itemId>")));
        Assert.IsType<XmlException>(error.InnerException);
        Assert.Contains("DTD", error.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PortTransferKind.Extract)]
    [InlineData(PortTransferKind.Deposit)]
    internal void ThrowingObserverAfterIndexedWrite_ReportsApplied(PortTransferKind kind)
    {
        var fixture = new InventoryFixture();
        string payload = FlowItemCodec.Encode(fixture.Source.Items[0]);
        fixture.Destination.Items.Add(null);
        Chest chest = kind == PortTransferKind.Extract ? fixture.Source : fixture.Destination;
        chest.Items.OnSlotChanged += (_, _, _, _) => throw new InvalidOperationException("observer failed");
        Assert.Equal(PortResult.Applied, fixture.Access.Apply(fixture.Transfer(kind), payload));
        if (kind == PortTransferKind.Extract) Assert.Null(chest.Items[0]);
        else Assert.Equal(payload, FlowItemCodec.Encode(chest.Items[0]));
    }
}

internal sealed class InventoryFixture
{
    internal static readonly Guid SourceId = new("00000000-0000-0000-0000-000000000001");
    internal static readonly Guid DestinationId = new("00000000-0000-0000-0000-000000000002");
    internal static readonly Guid Cargo = new("00000000-0000-0000-0000-000000000003");
    internal static readonly Guid Parcel = new("00000000-0000-0000-0000-000000000004");
    internal Chest Source { get; } = CreateChest();
    internal Chest Destination { get; } = CreateChest();
    internal ChestInventoryAccess Access { get; }

    internal InventoryFixture()
    {
        var item = new SObject { ItemId = "388", Stack = 12, Quality = 2 };
        item.modData[ChestInventoryAccess.CargoKey] = Cargo.ToString("D");
        item.modData["test/metadata"] = "data & <>";
        Source.Items.Add(item);
        Access = new ChestInventoryAccess(id => id == SourceId ? Source : id == DestinationId ? Destination : null, () => true, 1);
    }

    internal PortTransfer Transfer(PortTransferKind kind) => new(new PortTransferId(this, new ParcelId(Parcel), kind, 1),
        new CargoId(Cargo), new StationId(kind == PortTransferKind.Extract ? SourceId : DestinationId), new CargoManifest("(O)388", 12));

    internal static Chest CreateChest()
    {
        var chest = new Chest();
        chest.playerChest.Value = true;
        return chest;
    }
}
