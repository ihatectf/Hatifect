using System;
using System.Collections.Generic;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Policies;

namespace Hatifect.Flow.Domain.Network;

internal sealed class NetworkGraph
{
    private readonly Dictionary<StationId, SortedDictionary<Guid, Link>> _outgoing = new();
    private readonly Dictionary<StationId, long> _revisions = new();
    private readonly Dictionary<LinkId, Link> _links = new();
    private readonly Dictionary<LinkId, Link> _history = new();
    private readonly FlowLimits _limits;

    internal NetworkGraph(NetworkId id, FlowLimits limits)
    {
        IdentityValue.Require(id.Value);
        Id = id;
        _limits = limits;
    }

    internal NetworkId Id { get; }
    internal long TopologyRevision { get; private set; }
    internal bool Contains(StationId station) => _outgoing.ContainsKey(station);
    internal long Revision(StationId station) => _revisions[station];
    internal IEnumerable<Link> Outgoing(StationId station) => _outgoing[station].Values;
    internal IEnumerable<Link> History => _history.Values;
    internal int ActiveLinkCount => _links.Count;
    internal int LifetimeLinkCount => _history.Count;
    internal bool IsActive(LinkId id) => _links.ContainsKey(id);
    internal Link HistoricalLink(LinkId id) => _history[id];

    internal void AddStation(StationId id)
    {
        IdentityValue.Require(id.Value);
        if (_outgoing.ContainsKey(id) || _outgoing.Count >= _limits.MaxStations)
        {
            throw new InvalidOperationException("Station identity already exists or the station limit is reached.");
        }
        long topologyRevision = checked(TopologyRevision + 1);
        _outgoing.Add(id, new SortedDictionary<Guid, Link>());
        _revisions.Add(id, 0);
        TopologyRevision = topologyRevision;
    }

    internal void AddLink(Link link)
    {
        IdentityValue.Require(link.Id.Value);
        if (!Contains(link.Origin) || !Contains(link.Destination) || link.Origin == link.Destination)
        {
            throw new ArgumentException("Link endpoints must be different existing Stations.", nameof(link));
        }
        if (link.Capacity <= 0 || link.TransitTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(link), "Link capacity and transit ticks must be positive.");
        }
        if (_history.ContainsKey(link.Id) || _history.Count >= _limits.MaxLinks)
        {
            throw new InvalidOperationException("Link identity already used or the lifetime Link limit is reached.");
        }
        long revision = checked(_revisions[link.Origin] + 1);
        long topologyRevision = checked(TopologyRevision + 1);
        _history.Add(link.Id, link);
        _links.Add(link.Id, link);
        _outgoing[link.Origin].Add(link.Id.Value, link);
        _revisions[link.Origin] = revision;
        TopologyRevision = topologyRevision;
    }

    internal bool RemoveLink(LinkId id)
    {
        if (!_links.TryGetValue(id, out Link? link))
        {
            return false;
        }
        long revision = checked(_revisions[link.Origin] + 1);
        long topologyRevision = checked(TopologyRevision + 1);
        _links.Remove(id);
        _outgoing[link.Origin].Remove(id.Value);
        _revisions[link.Origin] = revision;
        TopologyRevision = topologyRevision;
        return true;
    }
}
