using System;
using System.Collections.Generic;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Network;
using Hatifect.Flow.Domain.Policies;

namespace Hatifect.Flow.Application.Planning;

internal sealed class RoutePlanner
{
    private readonly NetworkGraph _network;
    private readonly FlowLimits _limits;
    private readonly Dictionary<(StationId, StationId), RoutePlan> _cache = new();
    private readonly Queue<(StationId, StationId)> _cacheOrder = new();

    internal RoutePlanner(NetworkGraph network, FlowLimits limits)
    {
        _network = network;
        _limits = limits;
    }

    internal long SearchCount { get; private set; }
    internal int CacheCount => _cache.Count;

    internal RoutePlan Plan(StationId origin, StationId destination)
    {
        if (!_network.Contains(origin) || !_network.Contains(destination) || origin == destination)
        {
            throw new ArgumentException("A route requires two different existing Stations.");
        }
        var key = (origin, destination);
        if (_cache.TryGetValue(key, out RoutePlan? cached) && cached.IsCurrent(_network))
        {
            return cached;
        }
        RoutePlan plan = Search(origin, destination);
        if (!_cache.ContainsKey(key))
        {
            if (_cache.Count == _limits.MaxRoutePlans)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }
            _cacheOrder.Enqueue(key);
        }
        _cache[key] = plan;
        return plan;
    }

    private RoutePlan Search(StationId origin, StationId destination)
    {
        SearchCount++;
        var dependencies = new Dictionary<StationId, long>();
        var parents = new Dictionary<StationId, Link>();
        var discovered = new HashSet<StationId> { origin };
        var pending = new Queue<StationId>();
        pending.Enqueue(origin);
        while (pending.Count > 0)
        {
            StationId station = pending.Dequeue();
            dependencies.Add(station, _network.Revision(station));
            foreach (Link link in _network.Outgoing(station))
            {
                if (discovered.Contains(link.Destination))
                {
                    continue;
                }
                if (discovered.Count >= _limits.MaxRouteVisits)
                {
                    return new RoutePlan(_network, RouteStatus.SearchLimitExceeded, Array.Empty<Link>(), dependencies);
                }
                discovered.Add(link.Destination);
                parents.Add(link.Destination, link);
                if (link.Destination == destination)
                {
                    return BuildPlan(origin, destination, parents, dependencies);
                }
                pending.Enqueue(link.Destination);
            }
        }
        return new RoutePlan(_network, RouteStatus.NoRoute, Array.Empty<Link>(), dependencies);
    }

    private RoutePlan BuildPlan(StationId origin, StationId destination,
        Dictionary<StationId, Link> parents, Dictionary<StationId, long> dependencies)
    {
        var links = new List<Link>();
        StationId station = destination;
        while (station != origin)
        {
            Link link = parents[station];
            links.Add(link);
            station = link.Origin;
        }
        links.Reverse();
        return new RoutePlan(_network, RouteStatus.Found, links.ToArray(), dependencies);
    }
}
