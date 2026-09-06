using System;
using System.Collections.Generic;

namespace Hatifect.Flow.Multiplayer;

internal enum FlowPeerAdmission { Queued, DuplicatePending, Replayed, Rejected }

// One in-flight request per connected peer. Completed replies are cached only for the latest
// sequence; older/out-of-order requests never regain mutation authority. Reconnect requires a
// new connection nonce and explicit peer removal. No SMAPI event callback executes domain work.
internal sealed class FlowPeerQueue<TIntent, TReply> where TIntent : notnull where TReply : class
{
    internal const int MaxPeers = 16;
    private readonly Dictionary<long, Peer> _peers = new();
    private readonly Queue<(long Id, Peer Peer, long Sequence, TIntent Intent)> _queue = new();
    private readonly Guid _session;
    private bool _draining;
    internal FlowPeerQueue(Guid session) => _session = session;
    internal int PendingCount => _queue.Count;

    internal bool Connect(long peer, Guid connection)
    {
        if (peer == 0 || connection == Guid.Empty) return false;
        if (_peers.TryGetValue(peer, out Peer? existing)) return existing.Connection == connection;
        if (_peers.Count >= MaxPeers) return false;
        _peers.Add(peer, new Peer(connection));
        return true;
    }

    internal void Disconnect(long peer) => _peers.Remove(peer);

    internal FlowPeerAdmission Enqueue(long peer, Guid connection, Guid session, long sequence, TIntent intent, out TReply? replay)
    {
        replay = null;
        if (session != _session || sequence <= 0 || !_peers.TryGetValue(peer, out Peer? state) || state.Connection != connection)
            return FlowPeerAdmission.Rejected;
        if (state.Completed == sequence)
        {
            if (!EqualityComparer<TIntent>.Default.Equals(intent, state.LastIntent!)) return FlowPeerAdmission.Rejected;
            replay = state.LastReply;
            return FlowPeerAdmission.Replayed;
        }
        if (state.Pending is long pending)
            return pending == sequence && EqualityComparer<TIntent>.Default.Equals(intent, state.PendingIntent!)
                ? FlowPeerAdmission.DuplicatePending : FlowPeerAdmission.Rejected;
        if (state.Completed == long.MaxValue || sequence != state.Completed + 1 || _queue.Count >= MaxPeers)
            return FlowPeerAdmission.Rejected;
        state.Pending = sequence;
        state.PendingIntent = intent;
        _queue.Enqueue((peer, state, sequence, intent));
        return FlowPeerAdmission.Queued;
    }

    internal int Drain(Func<long, Guid, TIntent, TReply> execute, Action<long, Guid, long, TReply> reply, int budget = 8)
    {
        if (_draining) throw new InvalidOperationException("Peer commands cannot reenter the owner drain.");
        if (budget is < 1 or > MaxPeers) throw new ArgumentOutOfRangeException(nameof(budget));
        _draining = true;
        try
        {
            int ready = Math.Min(budget, _queue.Count), processed = 0;
            for (int index = 0; index < ready; index++)
            {
                var next = _queue.Dequeue();
                if (!_peers.TryGetValue(next.Id, out Peer? state) || !ReferenceEquals(state, next.Peer)) continue;
                // The executor returns a typed fault/rejection on failure; it must not throw after
                // physical mutation. The endpoint fences its session on unexpected exceptions.
                TReply result = execute(next.Id, state.Connection, next.Intent);
                state.Completed = next.Sequence;
                state.LastIntent = next.Intent;
                state.LastReply = result;
                state.Pending = null;
                state.PendingIntent = default;
                processed++;
                // Cache before sending: a lost/reentrant reply cannot execute the intent again.
                reply(next.Id, state.Connection, next.Sequence, result);
            }
            return processed;
        }
        finally { _draining = false; }
    }

    private sealed class Peer
    {
        internal readonly Guid Connection;
        internal long Completed;
        internal long? Pending;
        internal TIntent? PendingIntent, LastIntent;
        internal TReply? LastReply;
        internal Peer(Guid connection) => Connection = connection;
    }
}
