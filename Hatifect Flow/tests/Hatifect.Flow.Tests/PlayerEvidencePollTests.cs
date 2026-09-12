using System;
using System.Collections.Generic;
using Hatifect.Flow.Diagnostics;
using Xunit;

namespace Hatifect.Flow.Tests;

public sealed class PlayerEvidencePollTests
{
    [Fact]
    public void FirstPollIsImmediateButTheNextFiftyNineTicksDoNoEvidenceWork()
    {
        var poll = new FlowPlayerEvidencePoll();
        Assert.True(poll.TryBegin(1916));
        for (int tick = 1917; tick < 1976; tick++) Assert.False(poll.TryBegin(tick));
        Assert.True(poll.TryBegin(1976));
    }

    [Fact]
    public void RepeatedCallsForTheSameUpdateDoNotRepeatFileWork()
    {
        var poll = new FlowPlayerEvidencePoll();
        Assert.True(poll.TryBegin(0));
        for (int attempt = 0; attempt < 100; attempt++) Assert.False(poll.TryBegin(0));
        Assert.True(poll.TryBegin(60));
        Assert.False(poll.TryBegin(60));
    }

    [Fact]
    public void PendingEvidenceAndItsPublicationRunOnlyOncePerPollBudget()
    {
        var poll = new FlowPlayerEvidencePoll();
        var workTicks = new List<int>();
        int pendingPublications = 0;
        for (int tick = 1; tick <= 600; tick++)
        {
            if (!poll.TryBegin(tick)) continue;
            workTicks.Add(tick);
            pendingPublications++;
        }
        Assert.Equal(new[] { 1, 61, 121, 181, 241, 301, 361, 421, 481, 541 }, workTicks);
        Assert.Equal(10, pendingPublications);
    }

    [Fact]
    public void SkippedIntervalsDoNotReplayAReadBacklog()
    {
        var poll = new FlowPlayerEvidencePoll();
        Assert.True(poll.TryBegin(1));
        Assert.True(poll.TryBegin(1000));
        Assert.False(poll.TryBegin(1000));
        Assert.False(poll.TryBegin(1059));
        Assert.True(poll.TryBegin(1060));
    }

    [Fact]
    public void AGrantedPollStillRequiresCurrentEvidenceAndDoesNotCacheSuccess()
    {
        var poll = new FlowPlayerEvidencePoll();
        int reads = 0;
        bool evidence = false;
        bool Observe(int tick)
        {
            if (!poll.TryBegin(tick)) return false;
            reads++;
            return evidence;
        }
        Assert.False(Observe(1));
        evidence = true;
        Assert.False(Observe(60));
        Assert.True(Observe(61));
        evidence = false;
        Assert.False(Observe(121));
        Assert.Equal(3, reads);
    }

    [Fact]
    public void AValidationExceptionIsNotSwallowedOrImmediatelyRetried()
    {
        var poll = new FlowPlayerEvidencePoll();
        void Observe()
        {
            if (poll.TryBegin(1)) throw new InvalidOperationException("foreign evidence");
        }
        Assert.Equal("foreign evidence", Assert.Throws<InvalidOperationException>(Observe).Message);
        Assert.False(poll.TryBegin(2));
        Assert.True(poll.TryBegin(61));
    }

    [Fact]
    public void PollingStateBelongsToOneSequenceNotToTheProcess()
    {
        var first = new FlowPlayerEvidencePoll();
        var second = new FlowPlayerEvidencePoll();
        Assert.True(first.TryBegin(100));
        Assert.True(second.TryBegin(100));
        Assert.False(first.TryBegin(101));
        Assert.False(second.TryBegin(101));
    }

    [Fact]
    public void NegativeOrBackwardTicksAreRejectedWithoutGrantingWork()
    {
        var poll = new FlowPlayerEvidencePoll();
        Assert.Throws<ArgumentOutOfRangeException>(() => poll.TryBegin(-1));
        Assert.True(poll.TryBegin(10));
        Assert.Throws<InvalidOperationException>(() => poll.TryBegin(9));
        Assert.False(poll.TryBegin(69));
        Assert.True(poll.TryBegin(70));
    }

    [Fact]
    public void MaximumTickDoesNotOverflowIntoRepeatedPolling()
    {
        var poll = new FlowPlayerEvidencePoll();
        Assert.True(poll.TryBegin(int.MaxValue));
        Assert.False(poll.TryBegin(int.MaxValue));
    }

    [Fact]
    public void FinalReopenRequestsOneWarpUntilThePlayerReturnsToTheFixture()
    {
        var gate = new FlowPlayerReopenTargetGate();
        Assert.Equal(FlowPlayerReopenTargetState.RequestWarp, gate.Observe(false, 1700, 800, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(false, 1700, 800, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(true, 735, 478, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.Ready, gate.Observe(true, 735, 478, 1470, 956));
    }

    [Fact]
    public void FinalReopenDoesNotPublishAnOffscreenTargetEvenWhenThePlayerTileMatches()
    {
        var gate = new FlowPlayerReopenTargetGate();
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(true, 1791, 830, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(true, 1791, 830, 1470, 956));
    }

    [Fact]
    public void CameraMotionRestartsTheTwoSampleStabilityRequirement()
    {
        var gate = new FlowPlayerReopenTargetGate();
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(true, 700, 450, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(true, 704, 450, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.Ready, gate.Observe(true, 704, 450, 1470, 956));
    }

    [Fact]
    public void LeavingTheFixtureRearmsAFutureWarpRequest()
    {
        var gate = new FlowPlayerReopenTargetGate();
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(true, 700, 450, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.RequestWarp, gate.Observe(false, 700, 450, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.Waiting, gate.Observe(true, 700, 450, 1470, 956));
        Assert.Equal(FlowPlayerReopenTargetState.RequestWarp, gate.Observe(false, 700, 450, 1470, 956));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1469, 955, true)]
    [InlineData(1470, 100, false)]
    [InlineData(100, 956, false)]
    [InlineData(-1, 100, false)]
    [InlineData(100, -1, false)]
    public void FinalReopenUsesTheSameHalfOpenViewportContractAsNativePointerMotion(double x, double y, bool canBecomeReady)
    {
        var gate = new FlowPlayerReopenTargetGate();
        FlowPlayerReopenTargetState first = gate.Observe(true, x, y, 1470, 956);
        FlowPlayerReopenTargetState second = gate.Observe(true, x, y, 1470, 956);
        Assert.Equal(canBecomeReady ? FlowPlayerReopenTargetState.Ready : FlowPlayerReopenTargetState.Waiting, second);
        if (canBecomeReady) Assert.Equal(FlowPlayerReopenTargetState.Waiting, first);
    }

    [Fact]
    public void FinalReopenRejectsInvalidProjectionGeometry()
    {
        var gate = new FlowPlayerReopenTargetGate();
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Observe(true, double.NaN, 1, 1470, 956));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Observe(true, 1, double.PositiveInfinity, 1470, 956));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Observe(true, 1, 1, 0, 956));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Observe(true, 1, 1, 1470, -1));
    }
}
