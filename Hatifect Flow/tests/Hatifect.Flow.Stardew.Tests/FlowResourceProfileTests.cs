using System;
using Hatifect.Flow.Application;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Sessions;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class FlowResourceProfileTests
{
    [Theory]
    [InlineData(1, 12, 512)]
    [InlineData(2, 4, 256)]
    [InlineData(16, 4, 256)]
    public void WorkWindow_AccountsForFullInitialAndRetryWaves(int attempt, int ticks, int effects)
    {
        var world = new GameSessionWorld(); using FlowGameSession session = world.Open();
        FlowSnapshot snapshot = session.ReadSnapshot();
        var window = new FlowResourceWorkWindow(attempt, snapshot.SessionId, 1, 1000, Start);
        for (int tick = 1; tick <= ticks; tick++)
            window.Observe(snapshot.SessionId, 1, true, tick, tick == 2 ? 16 : 0,
                Step(tick, tick == ticks ? 0 : 256, tick == ticks ? effects : 0), snapshot);
        FlowResourceWorkSample report = window.Finish();
        Assert.Equal(ticks * 64, report.End.ProcessedOperations); Assert.Equal(effects, report.End.PhysicalApplyCalls);
        Assert.Equal(ticks, report.Frames); Assert.Equal(ticks, report.WorkTicks); Assert.Equal(64, report.MaximumOperationsPerTick);
        Assert.Equal(ticks, report.MaxTickMs); Assert.Equal((ticks * 95 + 99) / 100, report.P95TickMs);
        Assert.Equal(16, report.AllocatedBytesTotal); Assert.Equal(16, report.MaximumAllocatedBytesPerTick);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("skipped")]
    [InlineData("session")]
    [InlineData("thread")]
    [InlineData("clock")]
    [InlineData("time")]
    [InlineData("elapsed")]
    [InlineData("allocated")]
    [InlineData("operations")]
    public void WorkWindow_RejectsLostOwnershipClockAndTickBounds(string change)
    {
        var world = new GameSessionWorld(); using FlowGameSession session = world.Open();
        FlowSnapshot snapshot = session.ReadSnapshot();
        var window = new FlowResourceWorkWindow(2, snapshot.SessionId, 1, 1000, Start);
        FlowTickCounters next = Step(1, 256, 0);
        next = change switch
        {
            "duplicate" => next with { TickInvocations = 0 }, "skipped" => next with { TickInvocations = 2 },
            "time" => next with { Now = 0 }, "operations" => next with { LastTickProcessedOperations = 65 }, _ => next
        };
        Assert.Throws<InvalidOperationException>(() => window.Observe(change == "session" ? Guid.NewGuid() : snapshot.SessionId,
            change == "thread" ? 2 : 1, change != "clock", change == "elapsed" ? -1 : 1, change == "allocated" ? -1 : 0, next, snapshot));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("effect")]
    [InlineData("checkpoint")]
    [InlineData("search")]
    [InlineData("refresh")]
    [InlineData("processed")]
    public void WorkWindow_RejectsPartialOrHiddenWork(string change)
    {
        var world = new GameSessionWorld(); using FlowGameSession session = world.Open();
        FlowSnapshot snapshot = session.ReadSnapshot();
        var window = new FlowResourceWorkWindow(2, snapshot.SessionId, 1, 1000, Start);
        for (int tick = 1; tick <= (change == "incomplete" ? 3 : 4); tick++)
        {
            FlowTickCounters next = Step(tick, tick == 4 ? 0 : 256, tick == 4 ? 256 : 0);
            if (tick == 4) next = change switch
            {
                "effect" => next with { PhysicalApplyCalls = 255 }, "checkpoint" => next with { CheckpointCaptures = 1 },
                "search" => next with { RouteSearches = 1 }, "refresh" => next with { RefreshRequests = 3 },
                "processed" => next with { ProcessedOperations = 255 }, _ => next
            };
            window.Observe(snapshot.SessionId, 1, true, 1, 0, next, snapshot);
        }
        Assert.Throws<InvalidOperationException>(() => window.Finish());
    }

    [Fact]
    public void Measurement_ExecutesOnceAndNeverRetriesAFailure()
    {
        int calls = 0;
        object value = FlowResourceCost.Measure(() => { calls++; return new object(); }, out FlowResourceCost cost);
        Assert.NotNull(value); Assert.Equal(1, calls); Assert.True(cost.ElapsedTicks >= 0); Assert.True(cost.AllocatedBytes > 0);
        var failure = new InvalidOperationException("one operation failed");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => FlowResourceCost.Measure((Action)(() => { calls++; throw failure; }))));
        Assert.Equal(2, calls);
    }

    private static FlowTickCounters Start => new(0, 0, 256, 0, 0, 0, 0, 0, 0);
    private static FlowTickCounters Step(int tick, int pending, int effects) => new(tick, tick, pending, 0, tick * 64, 64, tick, 0, effects);
}
