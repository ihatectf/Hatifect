using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class ReturnGameCargoTests
{
    [Fact]
    public void ReturnToFullSourceWaitsThenRestoresOriginalStackAcrossGameSaveReload()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open();
        world.Configure(session);
        session.Send("source", "destination", 0);
        world.Destination = InventoryFixture.CreateChest(); // binding unavailable, cargo retained
        for (int i = 0; i < 5; i++) session.Tick(true);
        FlowSnapshot snapshot = session.ReadSnapshot();
        Assert.Equal(FlowCommandStatus.Applied, session.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision,
            snapshot.Parcels[0].Id, FlowParcelAction.ReturnToSource)).Status);
        world.Source.Items[0] = new StardewValley.Object { ItemId = "388", Stack = 999 };
        while (world.Source.Items.Count < world.Source.GetActualCapacity()) world.Source.Items.Add(new StardewValley.Object { ItemId = "388", Stack = 999 });
        session.Tick(true);
        Assert.Equal(ParcelState.ReturnRejected, Assert.Single(session.ReadSnapshot().Parcels).State);
        FlowGameSave saved = GameSessionWorld.Clone(session.BeginSave());
        var nextWorld = world.Clone();
        using FlowGameSession restored = nextWorld.Open(saved);
        nextWorld.Source.Items[0] = null;
        snapshot = restored.ReadSnapshot();
        Assert.Equal(FlowCommandStatus.Applied, restored.Execute(new FlowParcelCommand(snapshot.SessionId, snapshot.Revision,
            snapshot.Parcels[0].Id, FlowParcelAction.RetryDelivery)).Status);
        restored.Tick(true);
        Assert.Equal(ParcelState.Returned, Assert.Single(restored.ReadSnapshot().Parcels).State);
        var item = Assert.IsType<StardewValley.Object>(nextWorld.Source.Items[0]);
        Assert.Equal(8, item.Stack);
        Assert.Equal(4, item.Quality);
        Assert.Equal("saved & <metadata>", item.modData["test/value"]);
        Assert.Empty(nextWorld.Destination.Items);
        saved = GameSessionWorld.Clone(restored.BeginSave());
        var finalWorld = nextWorld.Clone();
        using FlowGameSession final = finalWorld.Open(saved);
        for (int i = 0; i < 10; i++) final.Tick(true);
        Assert.Single(finalWorld.Source.Items, value => value.QualifiedItemId == "(O)348");
        Assert.Empty(finalWorld.Errors);
    }
}
