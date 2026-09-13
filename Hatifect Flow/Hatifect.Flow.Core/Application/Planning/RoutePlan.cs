using System;
using System.Collections.Generic;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Network;

namespace Hatifect.Flow.Application.Planning;

internal sealed class RoutePlan
{
    private readonly NetworkGraph _network;
    private readonly Dictionary<StationId, long> _dependencies;
    private long _validatedRevision;
    private bool _invalidated;

    internal RoutePlan(NetworkGraph network, RouteStatus status, Link[] links,
        Dictionary<StationId, long> dependencies)
    {
        _network = network;
        Status = status;
        Links = Array.AsReadOnly(links);
        _dependencies = dependencies;
        _validatedRevision = network.TopologyRevision;
    }

    public RouteStatus Status { get; }
    public IReadOnlyList<Link> Links { get; }
    public NetworkId NetworkId => _network.Id;
    public long ValidationPassCount { get; private set; }
    internal IEnumerable<KeyValuePair<StationId, long>> Dependencies => _dependencies;

    internal static RoutePlan Restore(NetworkGraph network, Link[] links,
        Dictionary<StationId, long> dependencies)
    {
        var plan = new RoutePlan(network, RouteStatus.Found, links, dependencies);
        // Never mark persisted dependencies as current without checking them.
        plan._validatedRevision = -1;
        return plan;
    }

    internal bool IsCurrent(NetworkGraph network)
    {
        if (!ReferenceEquals(network, _network) || _invalidated)
        {
            return false;
        }
        if (_validatedRevision == network.TopologyRevision)
        {
            return true;
        }
        ValidationPassCount++;
        foreach (KeyValuePair<StationId, long> dependency in _dependencies)
        {
            if (network.Revision(dependency.Key) != dependency.Value)
            {
                _invalidated = true;
                return false;
            }
        }
        _validatedRevision = network.TopologyRevision;
        return true;
    }
}
