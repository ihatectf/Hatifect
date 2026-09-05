using System;
using Hatifect.Flow.Application;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class SessionOwnerTests
{
    [Fact]
    public void FarmhandLoadTickAndExitDoNotCreateAdvanceOrCloseHostSession()
    {
        var world = new GameSessionWorld();
        using var owner = new FlowSessionOwner();
        FlowGameSession host = Assert.IsType<FlowGameSession>(owner.Open(0, true, () => world.Open()));
        world.Configure(host);
        Guid sessionId = host.ReadSnapshot().SessionId;
        Assert.Null(owner.Open(1, false, () => throw new Exception("Farmhand must never load game save data.")));
        owner.ForScreen(0)!.Tick(true);
        owner.ForScreen(1)?.Tick(true);
        Assert.Equal(1, host.Now);
        owner.Close(1);
        Assert.Same(host, owner.ForScreen(0));
        Assert.Equal(sessionId, host.ReadSnapshot().SessionId);
        Assert.Equal(FlowApplicationState.Active, host.ReadSnapshot().State);
        Assert.NotNull(host.BeginSave());
        host.EndSave();
        owner.Close(0);
        Assert.Equal(FlowApplicationState.Closed, host.ReadSnapshot().State);
        Assert.Null(owner.Current);
    }

    [Fact]
    public void HostReloadRetiresOldEpochAndAnotherScreenCannotReplaceIt()
    {
        var world = new GameSessionWorld();
        using var owner = new FlowSessionOwner();
        FlowGameSession old = Assert.IsType<FlowGameSession>(owner.Open(3, true, () => world.Open()));
        world.Configure(old);
        FlowGameSave saved = GameSessionWorld.Clone(old.BeginSave());
        Assert.Throws<InvalidOperationException>(() => owner.Open(4, true, () => throw new Exception("Wrong screen cannot run the factory.")));
        Assert.Same(old, owner.ForScreen(3));
        var reloadedWorld = world.Clone();
        FlowGameSession current = Assert.IsType<FlowGameSession>(owner.Open(3, true, () => reloadedWorld.Open(saved)));
        Assert.Equal(FlowApplicationState.Closed, old.ReadSnapshot().State);
        Assert.NotEqual(old.ReadSnapshot().SessionId, current.ReadSnapshot().SessionId);
        Assert.Equal(2, current.ReadSnapshot().Stations.Count);
        owner.Close(0);
        Assert.Same(current, owner.Current);
    }
}
