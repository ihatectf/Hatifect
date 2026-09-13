using System;
using Hatifect.Flow.Multiplayer;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class PeerQueueTests
{
    private static readonly Guid Session = Guid.NewGuid(), Connection = Guid.NewGuid();

    [Fact]
    public void LostReplyAndDuplicatePendingNeverRepeatThePhysicalIntent()
    {
        var queue = new FlowPeerQueue<string, string>(Session);
        Assert.True(queue.Connect(1, Connection));
        Assert.Equal(FlowPeerAdmission.Queued, queue.Enqueue(1, Connection, Session, 1, "send eight wine", out _));
        Assert.Equal(FlowPeerAdmission.DuplicatePending, queue.Enqueue(1, Connection, Session, 1, "send eight wine", out _));
        int transfers = 0;
        Assert.Throws<InvalidOperationException>(() => queue.Drain((_, _, _) => { transfers++; return "applied"; },
            (_, _, _, _) => throw new InvalidOperationException("Reply transport failed.")));
        Assert.Equal(1, transfers);
        Assert.Equal(FlowPeerAdmission.Replayed, queue.Enqueue(1, Connection, Session, 1, "send eight wine", out string? cached));
        Assert.Equal("applied", cached);
        Assert.Equal(0, queue.Drain((_, _, _) => throw new Exception("No second transfer."), (_, _, _, _) => { }));
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(1, Connection, Session, 1, "send different cargo", out _));
    }

    [Fact]
    public void ForeignEpochConnectionSequenceAndUnknownPeerCannotQueueWork()
    {
        var queue = new FlowPeerQueue<string, string>(Session);
        Assert.True(queue.Connect(1, Connection));
        Assert.False(queue.Connect(1, Guid.NewGuid()));
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(2, Connection, Session, 1, "send", out _));
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(1, Guid.NewGuid(), Session, 1, "send", out _));
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(1, Connection, Guid.NewGuid(), 1, "send", out _));
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(1, Connection, Session, 0, "send", out _));
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(1, Connection, Session, 2, "send", out _));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void DisconnectAndRejoinRevokeAlreadyQueuedOldConnection()
    {
        var queue = new FlowPeerQueue<string, string>(Session);
        queue.Connect(1, Connection);
        queue.Enqueue(1, Connection, Session, 1, "old", out _);
        queue.Disconnect(1);
        Guid next = Guid.NewGuid();
        Assert.True(queue.Connect(1, next));
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(1, Connection, Session, 1, "old", out _));
        Assert.Equal(FlowPeerAdmission.Queued, queue.Enqueue(1, next, Session, 1, "new", out _));
        Assert.Equal(1, queue.Drain((peer, connection, intent) =>
        { Assert.Equal(1, peer); Assert.Equal(next, connection); Assert.Equal("new", intent); return "ok"; }, (_, _, _, _) => { }));
    }

    [Fact]
    public void SelfDeliveryIsDeferredAndPeerAndQueueBoundsAreEnforced()
    {
        var queue = new FlowPeerQueue<string, string>(Session);
        for (int peer = 1; peer <= FlowPeerQueue<string, string>.MaxPeers; peer++)
        {
            Assert.True(queue.Connect(peer, Connection));
            Assert.Equal(FlowPeerAdmission.Queued, queue.Enqueue(peer, Connection, Session, 1, "first", out _));
        }
        Assert.False(queue.Connect(17, Connection));
        Assert.Equal(1, queue.Drain((_, _, _) => "ok", (peer, _, _, _) =>
        {
            Assert.Throws<InvalidOperationException>(() => queue.Drain((_, _, _) => "bad", (_, _, _, _) => { }));
            Assert.Equal(FlowPeerAdmission.Queued, queue.Enqueue(peer, Connection, Session, 2, "second", out _));
        }, budget: 1));
        Assert.Equal(16, queue.PendingCount);
        Assert.Equal(FlowPeerAdmission.Rejected, queue.Enqueue(1, Connection, Session, 3, "third", out _));
    }
}
