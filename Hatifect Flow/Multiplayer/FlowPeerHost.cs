using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Sessions;

namespace Hatifect.Flow.Multiplayer;

// Receivers only admit bounded intentions. All game reads and writes run in Pump on the owner screen.
internal sealed class FlowPeerHost : IDisposable
{
    private readonly ulong _saveId;
    private readonly FlowGameSession _session;
    private readonly FlowPeerQueue<FlowPeerIntent, FlowPeerReply> _queue;
    private readonly Dictionary<long, Peer> _peers = new();
    private readonly Func<long, FlowPeerIntent, FlowCapturedTarget?> _captureTarget;
    private readonly Action<long, FlowPeerReply> _send;
    private readonly Action<Exception> _report;
    private FlowPeerProjection _projection;
    private bool _dirty, _outbox, _pumping, _closed;

    internal FlowPeerHost(ulong saveId, FlowGameSession session,
        Func<long, FlowPeerIntent, FlowCapturedTarget?> captureTarget,
        Action<long, FlowPeerReply> send, Action<Exception> report)
    {
        _saveId = saveId;
        _session = session;
        _captureTarget = captureTarget;
        _send = send;
        _report = report;
        _projection = FlowPeerProtocol.Capture(session);
        _queue = new FlowPeerQueue<FlowPeerIntent, FlowPeerReply>(_projection.SessionId);
        session.RevisionChanged += Changed;
    }

    internal bool Connect(long peer, FlowPeerHello hello)
    {
        if (_closed || hello is null || hello.Version != FlowPeerProtocol.Version || hello.SaveId != _saveId
            || !_queue.Connect(peer, hello.ClientId)) return false;
        if (!_peers.TryGetValue(peer, out Peer? state)) _peers.Add(peer, state = new Peer(hello.ClientId));
        state.Hello = true;
        _outbox = true;
        return true;
    }

    internal void Disconnect(long peer)
    {
        _queue.Disconnect(peer);
        _peers.Remove(peer);
    }

    internal FlowPeerAdmission Receive(long peer, FlowPeerRequest request)
    {
        if (_closed || !FlowPeerProtocol.Valid(request) || request.SaveId != _saveId)
            return FlowPeerAdmission.Rejected;
        FlowPeerAdmission admission = _queue.Enqueue(peer, request.ClientId, request.SessionId,
            request.Sequence, request.Intent, out FlowPeerReply? replay);
        if (admission == FlowPeerAdmission.Replayed)
        {
            _peers[peer].Replay = replay! with { Sequence = request.Sequence };
            _outbox = true;
        }
        return admission;
    }

    internal void Pump()
    {
        if (_closed || (!_dirty && !_outbox && _queue.PendingCount == 0)) return;
        if (_pumping) throw new InvalidOperationException("Peer host cannot reenter its owner pump.");
        _pumping = true;
        try
        {
            // Snapshot peer identities only on work, since transport can synchronously self-deliver.
            var peers = _peers.ToArray();
            _outbox = false;
            foreach (var pair in peers)
            {
                if (!IsCurrent(pair.Key, pair.Value)) continue;
                Peer peer = pair.Value;
                if (peer.Hello)
                {
                    peer.Hello = false;
                    Send(pair.Key, Reply(peer, 0));
                }
                if (peer.Replay is { } replay)
                {
                    peer.Replay = null;
                    Send(pair.Key, replay);
                }
            }
            // A save barrier retains pending requests without changing their sequence or outcome.
            if (!_session.IsSaving)
                _queue.Drain(Execute, (id, _, sequence, reply) => Send(id, reply with { Sequence = sequence }));
            if (!_dirty) return;
            _dirty = false;
            Capture();
            foreach (var pair in peers)
                if (IsCurrent(pair.Key, pair.Value)) Send(pair.Key, Reply(pair.Value, 0));
        }
        finally { _pumping = false; }
    }

    private FlowPeerReply Execute(long id, Guid connection, FlowPeerIntent intent)
    {
        Peer peer = _peers[id];
        FlowCommandResult? result = null;
        FlowInventorySlot[]? inventory = null;
        FlowRoutePreview? route = null;
        try
        {
            (result, inventory, route) = ExecuteIntent(id, peer, intent);
            Capture();
        }
        catch (Exception error)
        {
            // Even an unexpected failure after mutation becomes a retained, non-replayed outcome.
            Fence(error);
            result = new FlowCommandResult(FlowCommandStatus.Faulted, _projection.Revision);
        }
        return Reply(peer, 0) with
        {
            Result = result, InventoryStation = inventory is null ? Guid.Empty : intent.Station, Inventory = inventory,
            RouteSource = route is null ? Guid.Empty : intent.Station,
            RouteDestination = route is null ? Guid.Empty : intent.Destination, Route = route
        };
    }

    private (FlowCommandResult? Result, FlowInventorySlot[]? Inventory, FlowRoutePreview? Route) ExecuteIntent(
        long id, Peer peer, FlowPeerIntent intent)
    {
        FlowCommandResult? result = null;
        FlowInventorySlot[]? inventory = null;
        FlowRoutePreview? route = null;
        switch (intent.Operation)
        {
            case FlowPeerOperation.Parcel: result = _session.Execute(intent.Parcel!); break;
            case FlowPeerOperation.Network: result = _session.Execute(intent.Network!, peer.Target); break;
            case FlowPeerOperation.Send: result = _session.Execute(intent.Send!); break;
            case FlowPeerOperation.Recovery: result = _session.Execute(intent.Recovery!); break;
            case FlowPeerOperation.Inventory: inventory = _session.ReadInventory(intent.Station).ToArray(); break;
            case FlowPeerOperation.Route: route = _session.PreviewRoute(intent.Station, intent.Destination); break;
            case FlowPeerOperation.Target:
                FlowSnapshot snapshot = _session.ReadSnapshot();
                FlowCommandResult? unavailable = _session.Application.ValidateExternalCommand(snapshot.SessionId, snapshot.Revision);
                if (unavailable is not null)
                {
                    result = unavailable;
                    break;
                }
                peer.Target = _captureTarget(id, intent);
                result = new FlowCommandResult(peer.Target is null ? FlowCommandStatus.Rejected : FlowCommandStatus.Applied,
                    _session.ReadSnapshot().Revision);
                break;
            case FlowPeerOperation.Refresh: break;
            default: result = new FlowCommandResult(FlowCommandStatus.InvalidCommand, _session.ReadSnapshot().Revision); break;
        }
        return (result, inventory, route);
    }

    private FlowPeerReply Reply(Peer peer, long sequence)
        => new(FlowPeerProtocol.Version, _saveId, peer.ClientId, sequence,
            _projection with { Target = peer.Target?.Token ?? Guid.Empty, TargetDescription = peer.Target?.Description ?? "" });

    private void Capture()
    {
        try { _projection = FlowPeerProtocol.Capture(_session); }
        catch (Exception error) { Fence(error); }
    }

    private void Fence(Exception error)
    {
        try { _session.FencePeerFailure(error); }
        catch (Exception reportingFailure) { Report(reportingFailure); }
        // A resolver can itself be the failing dependency. Retain the last detached view and close actions.
        _projection = _projection with { State = FlowApplicationState.RecoveryRequired,
            Parcels = _projection.Parcels.Select(p => p with { Actions = FlowParcelActions.None, Availability = FlowActionAvailabilitySet.Disabled(FlowRejectionCode.RecoveryRequired) }).ToArray() };
    }

    private bool IsCurrent(long id, Peer peer) => _peers.TryGetValue(id, out Peer? current) && ReferenceEquals(peer, current);
    private void Changed(long revision) => _dirty = true;
    private void Send(long id, FlowPeerReply reply)
    {
        try { _send(id, reply); }
        catch (Exception error) { Report(error); }
    }
    private void Report(Exception error)
    {
        try { _report(error); }
        catch { /* Logging failure cannot discard an already committed command outcome. */ }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _session.RevisionChanged -= Changed;
        _projection = _projection with { State = FlowApplicationState.Closed, Stations = Array.Empty<FlowStationDetails>(),
            Links = Array.Empty<FlowLinkSnapshot>(), Parcels = Array.Empty<FlowParcelSnapshot>(), Recovery = Array.Empty<FlowRecoveryIssue>() };
        foreach (var pair in _peers.ToArray())
        {
            pair.Value.Target = null;
            Send(pair.Key, Reply(pair.Value, 0));
            _queue.Disconnect(pair.Key);
        }
        _peers.Clear();
    }

    private sealed class Peer
    {
        internal readonly Guid ClientId;
        internal FlowCapturedTarget? Target;
        internal bool Hello;
        internal FlowPeerReply? Replay;
        internal Peer(Guid clientId) => ClientId = clientId;
    }
}
