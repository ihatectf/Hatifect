using System;
using System.Linq;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Network;
using Hatifect.Flow.Domain.Policies;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class RoutingTests
{
    [Fact]
    public void DuplicateStationAndLinkIds_RejectWithoutChangingTopology()
    {
        var graph = Graph(4);
        var planner = Planner(graph);
        var first = Edge(10, 1, 2);
        graph.AddLink(first);
        RoutePlan before = planner.Plan(Station(1), Station(2));

        Assert.Throws<InvalidOperationException>(() => graph.AddStation(Station(1)));
        Assert.Throws<InvalidOperationException>(() => graph.AddLink(Edge(10, 3, 4)));

        Assert.True(before.IsCurrent(graph));
        Assert.Equal(new[] { first }, planner.Plan(Station(1), Station(2)).Links);
        Assert.Equal(RouteStatus.NoRoute, planner.Plan(Station(3), Station(4)).Status);
        Assert.True(graph.RemoveLink(first.Id));
        Assert.Throws<InvalidOperationException>(() => graph.AddLink(first));
        Assert.Equal(RouteStatus.NoRoute, planner.Plan(Station(1), Station(2)).Status);
    }

    [Fact]
    public void EqualLengthRoutes_ChooseSameOrderedLinksRegardlessOfInsertionOrder()
    {
        var edges = new[] { Edge(20, 1, 2), Edge(10, 1, 3), Edge(40, 2, 4), Edge(30, 3, 4) };
        var forward = Graph(4);
        var reverse = Graph(4);
        foreach (Link edge in edges)
        {
            forward.AddLink(edge);
        }
        foreach (Link edge in edges.Reverse())
        {
            reverse.AddLink(edge);
        }

        RoutePlan first = Planner(forward).Plan(Station(1), Station(4));
        RoutePlan second = Planner(reverse).Plan(Station(1), Station(4));

        Assert.Equal(RouteStatus.Found, first.Status);
        Assert.Equal(new[] { LinkId(10), LinkId(30) }, first.Links.Select(link => link.Id));
        Assert.Equal(first.Links, second.Links);
        forward.AddLink(Edge(99, 1, 4));
        Assert.Equal(new[] { LinkId(99) }, Planner(forward).Plan(Station(1), Station(4)).Links.Select(link => link.Id));
    }

    [Fact]
    public void ExplicitLinks_DoNotConnectDisjointStationsOrCreateReverseRoute()
    {
        var graph = Graph(4);
        graph.AddLink(Edge(10, 1, 2));
        graph.AddLink(Edge(20, 3, 4));
        var planner = Planner(graph);

        RoutePlan disjoint = planner.Plan(Station(1), Station(4));
        RoutePlan reverse = planner.Plan(Station(2), Station(1));

        Assert.Equal(RouteStatus.NoRoute, disjoint.Status);
        Assert.Empty(disjoint.Links);
        Assert.Equal(RouteStatus.NoRoute, reverse.Status);
        Assert.Empty(reverse.Links);
    }

    [Fact]
    public void RelatedTopologyChange_InvalidatesPlanButUnrelatedChangeDoesNot()
    {
        var graph = Graph(5);
        graph.AddLink(Edge(10, 1, 2));
        graph.AddLink(Edge(20, 2, 3));
        var planner = Planner(graph);
        RoutePlan original = planner.Plan(Station(1), Station(3));
        Assert.Equal(0, original.ValidationPassCount);
        Assert.True(original.IsCurrent(graph));
        Assert.Equal(0, original.ValidationPassCount);

        graph.AddLink(Edge(30, 4, 5));

        Assert.True(original.IsCurrent(graph));
        Assert.Same(original, planner.Plan(Station(1), Station(3)));
        Assert.Equal(1, planner.SearchCount);
        Assert.Equal(1, original.ValidationPassCount);
        for (int check = 0; check < 10; check++)
        {
            Assert.True(original.IsCurrent(graph));
        }
        Assert.Equal(1, original.ValidationPassCount);

        graph.AddLink(Edge(40, 1, 3));

        Assert.False(original.IsCurrent(graph));
        Assert.Equal(2, original.ValidationPassCount);
        for (int check = 0; check < 10; check++)
        {
            Assert.False(original.IsCurrent(graph));
        }
        Assert.Equal(2, original.ValidationPassCount);
        Assert.Equal(new[] { LinkId(40) }, planner.Plan(Station(1), Station(3)).Links.Select(link => link.Id));
        Assert.Equal(2, planner.SearchCount);
    }

    [Fact]
    public void MissingRoute_NewReachableEdgeInvalidatesNegativeCache()
    {
        var graph = Graph(3);
        graph.AddLink(Edge(10, 1, 2));
        var planner = Planner(graph);
        RoutePlan missing = planner.Plan(Station(1), Station(3));
        Assert.Equal(RouteStatus.NoRoute, missing.Status);

        graph.AddLink(Edge(20, 2, 3));

        Assert.False(missing.IsCurrent(graph));
        Assert.Equal(new[] { LinkId(10), LinkId(20) }, planner.Plan(Station(1), Station(3)).Links.Select(link => link.Id));
        Assert.Equal(2, planner.SearchCount);
    }

    [Fact]
    public void RoutePlan_SameNetworkIdInAnotherRuntimeDoesNotShareAuthority()
    {
        var original = Graph(2);
        original.AddLink(Edge(10, 1, 2));
        var foreign = Graph(2);
        foreign.AddLink(Edge(10, 1, 2));
        RoutePlan plan = Planner(original).Plan(Station(1), Station(2));

        Assert.True(plan.IsCurrent(original));
        Assert.False(plan.IsCurrent(foreign));
    }

    [Fact]
    public void SearchVisitLimit_AtBoundaryFindsRouteAndOverflowIsExplicit()
    {
        var graph = Graph(3);
        graph.AddLink(Edge(10, 1, 2));
        graph.AddLink(Edge(20, 2, 3));
        var limited = Planner(graph, new FlowLimits(maxRouteVisits: 2));
        var exact = Planner(graph, new FlowLimits(maxRouteVisits: 3));

        RoutePlan rejected = limited.Plan(Station(1), Station(3));
        RoutePlan found = exact.Plan(Station(1), Station(3));

        Assert.Equal(RouteStatus.SearchLimitExceeded, rejected.Status);
        Assert.Empty(rejected.Links);
        Assert.Equal(RouteStatus.Found, found.Status);
        Assert.Equal(new[] { LinkId(10), LinkId(20) }, found.Links.Select(link => link.Id));
    }

    [Fact]
    public void RouteCache_AtLimitEvictsOldestWithoutChangingRouteMeaning()
    {
        var graph = Graph(4);
        graph.AddLink(Edge(10, 1, 2));
        graph.AddLink(Edge(20, 2, 3));
        graph.AddLink(Edge(30, 3, 4));
        var planner = Planner(graph, new FlowLimits(maxRoutePlans: 2));
        RoutePlan oldest = planner.Plan(Station(1), Station(2));
        RoutePlan retained = planner.Plan(Station(2), Station(3));
        Assert.Equal(2, planner.CacheCount);

        planner.Plan(Station(3), Station(4));

        Assert.Equal(2, planner.CacheCount);
        Assert.Same(retained, planner.Plan(Station(2), Station(3)));
        RoutePlan recomputed = planner.Plan(Station(1), Station(2));
        Assert.NotSame(oldest, recomputed);
        Assert.Equal(oldest.Links, recomputed.Links);
        Assert.Equal(4, planner.SearchCount);
        Assert.Equal(2, planner.CacheCount);
    }

    [Fact]
    public void InvalidLinkEndpointsAndDimensions_DoNotMutateGraph()
    {
        var graph = Graph(2);
        Assert.Throws<ArgumentException>(() => graph.AddLink(Edge(10, 1, 1)));
        Assert.Throws<ArgumentException>(() => graph.AddLink(Edge(10, 1, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.AddLink(Edge(10, 1, 2) with { Capacity = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.AddLink(Edge(10, 1, 2) with { TransitTicks = 0 }));
        Assert.Equal(RouteStatus.NoRoute, Planner(graph).Plan(Station(1), Station(2)).Status);
        graph.AddLink(Edge(10, 1, 2));
        Assert.Equal(RouteStatus.Found, Planner(graph).Plan(Station(1), Station(2)).Status);
    }

    private static NetworkGraph Graph(int stations)
    {
        var graph = new NetworkGraph(new NetworkId(Guid.Parse("f0000000-0000-0000-0000-000000000001")), new FlowLimits());
        for (int index = 1; index <= stations; index++)
        {
            graph.AddStation(Station(index));
        }
        return graph;
    }

    private static RoutePlanner Planner(NetworkGraph graph, FlowLimits? limits = null) => new(graph, limits ?? new FlowLimits());
    private static StationId Station(int value) => new(new Guid(value, 0, 0, new byte[8]));
    private static LinkId LinkId(int value) => new(new Guid(value, 0, 0, new byte[8]));
    private static Link Edge(int id, int origin, int destination) => new(LinkId(id), Station(origin), Station(destination), 10, 3);
}
