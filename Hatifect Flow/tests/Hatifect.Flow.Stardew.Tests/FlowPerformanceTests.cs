using System;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowPerformanceTests
{
    [Fact]
    public void OwnerCounters_ObserveRealWorkAndResetOnPausedSavingAndClosedTicks()
    {
        var world = new GameSessionWorld();
        using FlowGameSession session = world.Open(); world.Configure(session);
        session.BeginSave(); session.EndSave(); session.Send("source", "destination", 0);
        FlowSnapshot reserved = session.ReadSnapshot();
        session.Tick(false);
        Assert.Same(reserved, session.ReadSnapshot());
        Assert.Equal(0, session.TickCounters.ProcessedOperations);
        Assert.Equal(1, session.TickCounters.PendingOperations);
        Assert.True(session.TickCounters.RouteSearches > 0);
        session.Tick(true);
        Assert.Equal(1, session.TickCounters.LastTickProcessedOperations);
        Assert.Equal(1, session.TickCounters.ProcessedOperations);
        Assert.Equal(1, session.TickCounters.PhysicalApplyCalls);
        Assert.Equal(1, session.TickCounters.RefreshRequests);
        session.Tick(false);
        Assert.Equal(0, session.TickCounters.LastTickProcessedOperations);
        for (int i = 0; i < 5; i++) session.Tick(true);
        Assert.Equal(3, session.TickCounters.ProcessedOperations); // departure, arrival, delivery
        Assert.Equal(2, session.TickCounters.PhysicalApplyCalls);
        Assert.Equal(2, session.TickCounters.RefreshRequests);
        Assert.Equal(0, session.TickCounters.PendingOperations);
        Assert.Equal(1, session.TickCounters.CheckpointCaptures);
        FlowTickCounters before = session.TickCounters;
        session.BeginSave(); session.Tick(true);
        Assert.Equal(before with { TickInvocations = before.TickInvocations + 1, CheckpointCaptures = 2 }, session.TickCounters);
        session.Dispose(); session.Tick(true);
        Assert.Equal(before with { TickInvocations = before.TickInvocations + 2, CheckpointCaptures = 2 }, session.TickCounters);
        Assert.Throws<InvalidOperationException>(() => session.BeginSave());
        Assert.Equal(2, session.TickCounters.CheckpointCaptures);
        Assert.Empty(world.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Window_ExcludesWarmupComputesNearestRankAndRetainsAllocationEvidence(bool timePasses)
    {
        var world = new GameSessionWorld(); using FlowGameSession session = world.Open();
        FlowSnapshot snapshot = session.ReadSnapshot();
        var window = new FlowPerformanceWindow(snapshot.SessionId, 1, timePasses, 1_000_000, 0);
        for (int i = 1; i <= 720; i++)
            window.Observe(snapshot.SessionId, 1, timePasses, i <= 120 ? 99_000_000 : i - 120,
                i == 700 ? 8 : 0, Counters(i, timePasses), snapshot);
        FlowPerformanceWindowReport report = window.Finish();
        Assert.True(window.Complete);
        Assert.Equal(600, report.Frames); Assert.Equal(120, report.WarmupFrames);
        Assert.Equal(snapshot.SessionId, report.SessionId); Assert.Equal(1, report.ThreadId);
        Assert.Equal(0.300, report.P50TickMs, 9); Assert.Equal(0.570, report.P95TickMs, 9);
        Assert.Equal(0.594, report.P99TickMs, 9); Assert.Equal(0.600, report.MaxTickMs, 9);
        Assert.Equal(8, report.AllocatedBytesTotal); Assert.Equal(8, report.MaximumAllocatedBytesPerTick);
        Assert.Equal(timePasses ? 600 : 0, report.End.Now - report.Start.Now);
        Assert.Throws<InvalidOperationException>(() => window.Observe(snapshot.SessionId, 1, timePasses, 1, 0, Counters(721, timePasses), snapshot));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("skipped")]
    [InlineData("session")]
    [InlineData("thread")]
    [InlineData("clock")]
    [InlineData("elapsed")]
    [InlineData("allocated")]
    [InlineData("work")]
    public void Window_RejectsWrongIdentityDuplicateOrSkippedTicksAndInvalidMeasurements(string change)
    {
        var world = new GameSessionWorld(); using FlowGameSession session = world.Open();
        FlowSnapshot snapshot = session.ReadSnapshot();
        var window = new FlowPerformanceWindow(snapshot.SessionId, 1, false, 1_000_000, 0);
        for (int i = 1; i <= 120; i++) window.Observe(snapshot.SessionId, 1, false, 1, 0, Counters(i, false), snapshot);
        FlowTickCounters counters = Counters(change == "duplicate" ? 120 : change == "skipped" ? 122 : 121, false);
        if (change == "work") counters = counters with { LastTickProcessedOperations = 1 };
        Assert.Throws<InvalidOperationException>(() => window.Observe(change == "session" ? Guid.NewGuid() : snapshot.SessionId,
            change == "thread" ? 2 : 1, change == "clock", change == "elapsed" ? -1 : 1, change == "allocated" ? -1 : 0, counters, snapshot));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("checkpoint")]
    [InlineData("effect")]
    [InlineData("search")]
    [InlineData("projection")]
    [InlineData("time")]
    public void Window_RejectsIncompleteOrHiddenSteadyWork(string change)
    {
        var world = new GameSessionWorld(); using FlowGameSession session = world.Open();
        FlowSnapshot snapshot = session.ReadSnapshot();
        var window = new FlowPerformanceWindow(snapshot.SessionId, 1, false, 1_000_000, 0);
        for (int i = 1; i <= (change == "incomplete" ? 719 : 720); i++)
        {
            FlowTickCounters counters = Counters(i, false);
            if (i > 120) counters = change switch
            {
                "checkpoint" => counters with { CheckpointCaptures = 2 },
                "effect" => counters with { PhysicalApplyCalls = 1 },
                "search" => counters with { RouteSearches = 4 },
                "projection" => counters with { RefreshRequests = 1 },
                "time" => counters with { Now = 1 }, _ => counters
            };
            window.Observe(snapshot.SessionId, 1, false, 1, 0, counters, snapshot);
        }
        Assert.Throws<InvalidOperationException>(() => window.Finish());
    }

    [Fact]
    public void Window_RejectsNewCachedProjectionEvenForTheSameSession()
    {
        var world = new GameSessionWorld(); using FlowGameSession session = world.Open();
        FlowSnapshot snapshot = session.ReadSnapshot();
        var window = new FlowPerformanceWindow(snapshot.SessionId, 1, false, 1_000_000, 0);
        for (int i = 1; i <= 120; i++) window.Observe(snapshot.SessionId, 1, false, 1, 0, Counters(i, false), snapshot);
        world.Configure(session);
        Assert.Throws<InvalidOperationException>(() => window.Observe(snapshot.SessionId, 1, false, 1, 0, Counters(121, false), session.ReadSnapshot()));
    }

    private static FlowTickCounters Counters(int tick, bool timePasses) => new(tick, timePasses ? tick : 0,
        timePasses ? 0 : 80, 3, 0, 0, 0, 1, 0);
}
