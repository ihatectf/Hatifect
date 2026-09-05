using System;
using System.IO;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Infrastructure.Persistence;
using Xunit;

namespace Hatifect.Flow.Tests;

[Trait("Category", "flow")]
public sealed class DurableFlowHostTests
{
    [Fact]
    public void Host_StartSameIdentityIsNoOpAndDifferentIdentityPreservesActiveSession()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost(); int opens = 0;
        Assert.Equal(DurableFlowHostState.Inactive, host.State); Assert.Equal(0, host.Tick());
        Assert.Throws<InvalidOperationException>(() => host.Execute(_ => { }));
        Assert.Throws<InvalidOperationException>(() => host.Runtime);
        Assert.Throws<InvalidOperationException>(() => host.GetPortSnapshot(CheckpointFixture.Origin));
        Assert.True(host.Start("save-a", () => { opens++; return Create(directory); }));
        FlowRuntime runtime = host.Runtime; byte[] core = CoreBytes(directory), provider = ProviderBytes(directory);
        Assert.False(host.Start("save-a", () => { opens++; throw new HostFailure(); }));
        Assert.Throws<InvalidOperationException>(() => host.Start("save-b", () => { opens++; throw new HostFailure(); }));
        Assert.Equal(1, opens); Assert.Same(runtime, host.Runtime); Assert.Equal(DurableFlowHostState.Active, host.State);
        Assert.Equal(core, CoreBytes(directory)); Assert.Equal(provider, ProviderBytes(directory));
        host.Stop(); Assert.Equal(DurableFlowHostState.Inactive, host.State);
        Assert.True(host.Start("save-b", () => DurableFlowSession.Open(directory.Core, directory.Provider)));
        Assert.NotSame(runtime, host.Runtime); Assert.Equal(CheckpointFixture.Cargo.Value, Assert.Single(host.GetPortSnapshot(CheckpointFixture.Origin).Inventory).CargoId);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void Host_InvalidIdentityDoesNotInvokeFactoryOrFaultActiveSession(string? identity)
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost(); int calls = 0;
        Assert.ThrowsAny<ArgumentException>(() => host.Start(identity!, () => { calls++; return Create(directory); }));
        Assert.Equal(0, calls); Assert.Equal(DurableFlowHostState.Inactive, host.State);
        host.Start("save", () => Create(directory));
        Assert.ThrowsAny<ArgumentException>(() => host.Start(identity!, () => { calls++; throw new HostFailure(); }));
        Assert.Equal(0, calls); Assert.Equal(DurableFlowHostState.Active, host.State);
        Assert.Throws<ArgumentNullException>(() => host.Execute(null!));
        Assert.Throws<ArgumentNullException>(() => host.Start("another", null!));
        Assert.Equal(DurableFlowHostState.Active, host.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Host_FactoryFailureLatchesUntilStopThenAllowsFreshStart(bool returnsNull)
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        if (returnsNull) Assert.Throws<InvalidOperationException>(() => host.Start("save", () => null!));
        else Assert.Throws<HostFailure>(() => host.Start("save", () => throw new HostFailure()));
        Assert.Equal(DurableFlowHostState.Faulted, host.State);
        Assert.Throws<InvalidOperationException>(() => host.Start("save", () => Create(directory)));
        Assert.Throws<InvalidOperationException>(() => host.Tick());
        host.Stop(); Assert.Equal(DurableFlowHostState.Inactive, host.State);
        Assert.True(host.Start("save", () => Create(directory))); Assert.Equal(0, host.LogicalTick);
    }

    [Fact]
    public void Host_IdleAndPausedTicksDoNotWriteCheckpointsOrSearchRoutes()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        host.Start("save", () => Create(directory));
        byte[] core = CoreBytes(directory), provider = ProviderBytes(directory); long revision = host.Revision;
        long searches = host.Runtime.RouteSearchCount;
        for (int i = 0; i < 250; i++) Assert.Equal(0, host.Tick());
        Assert.Equal(250, host.LogicalTick); Assert.Equal(0, host.Runtime.Now);
        for (int i = 0; i < 25; i++) Assert.Equal(0, host.Tick(paused: true));
        Assert.Equal(250, host.LogicalTick); Assert.Equal(revision, host.Revision); Assert.Equal(0, host.ProviderRevision);
        Assert.Equal(searches, host.Runtime.RouteSearchCount);
        Assert.Equal(core, CoreBytes(directory)); Assert.Equal(provider, ProviderBytes(directory));
    }

    [Fact]
    public void Host_CommandAfterIdleUsesCurrentLogicalTimeAndDueTicksPersistOnlyWhenDue()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        host.Start("save", () => Create(directory));
        for (int i = 0; i < 100; i++) host.Tick();
        host.Execute(runtime => { Assert.Equal(100, runtime.Now); Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)); });
        Assert.Equal(101, host.Runtime.PeekNextOperation()!.DueTick);
        byte[] reservedCore = CoreBytes(directory), reservedProvider = ProviderBytes(directory);
        Assert.Equal(0, host.Tick(paused: true)); Assert.Equal(100, host.LogicalTick);
        Assert.Equal(ParcelState.Reserved, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(reservedCore, CoreBytes(directory)); Assert.Equal(reservedProvider, ProviderBytes(directory));
        Assert.Equal(1, host.Tick()); Assert.Equal(ParcelState.InTransit, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        byte[] core = CoreBytes(directory), provider = ProviderBytes(directory);
        Assert.Equal(0, host.Tick()); Assert.Equal(0, host.Tick());
        Assert.Equal(core, CoreBytes(directory)); Assert.Equal(provider, ProviderBytes(directory));
        Assert.Equal(2, host.Tick()); Assert.Equal(104, host.LogicalTick);
        Assert.Equal(ParcelState.Delivered, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(2, host.ProviderRevision); Assert.Null(host.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
    }

    [Fact]
    public void Host_DefaultOperationBudgetDefersDueBacklogAcrossTicks()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        FlowCheckpoint checkpoint = new CheckpointFixture().Runtime.CaptureCheckpoint();
        checkpoint = checkpoint with { Limits = checkpoint.Limits with { MaxOperationsPerAdvance = 1 } };
        host.Start("save", () => DurableFlowSession.Create(directory.Core, directory.Provider, FlowRuntime.RestoreCheckpoint(checkpoint)));
        host.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel)));
        Assert.Equal(1, host.Tick()); Assert.Equal(0, host.Tick()); Assert.Equal(0, host.Tick());
        Assert.Equal(1, host.Tick()); Assert.Equal(ParcelState.Arrived, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(1, host.Runtime.PendingOperationCount); Assert.Equal(4, host.Runtime.PeekNextOperation()!.DueTick);
        Assert.Equal(1, host.Tick()); Assert.Equal(ParcelState.Delivered, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
        Assert.Equal(0, host.Runtime.PendingOperationCount); Assert.Equal(5, host.LogicalTick);
    }

    [Fact]
    public void Host_ExplicitFutureAdvanceMovesLogicalClockForward()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        host.Start("save", () => Create(directory)); host.Execute(runtime => Assert.Equal(0, runtime.AdvanceTo(200)));
        Assert.Equal(200, host.LogicalTick); Assert.Equal(0, host.Tick()); Assert.Equal(201, host.LogicalTick);
        host.Execute(runtime => Assert.Equal(201, runtime.Now));
    }

    [Fact]
    public void Host_StopFlushesIdleClockReleasesLeaseAndFencesOldRuntime()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        host.Start("save", () => Create(directory)); FlowRuntime retired = host.Runtime;
        for (int i = 0; i < 37; i++) host.Tick();
        byte[] oldCore = CoreBytes(directory), oldProvider = ProviderBytes(directory);
        host.Stop(); Assert.Equal(DurableFlowHostState.Inactive, host.State);
        Assert.NotEqual(oldCore, CoreBytes(directory)); Assert.Equal(oldProvider, ProviderBytes(directory));
        Assert.Throws<InvalidOperationException>(() => retired.Cancel(CheckpointFixture.Parcel));
        byte[] core = CoreBytes(directory); host.Stop(); Assert.Equal(core, CoreBytes(directory));
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(37, reopened.Runtime.Now); Assert.Equal(0, reopened.ProviderRevision);
        Assert.Equal(CheckpointFixture.Parcel, reopened.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
    }

    [Fact]
    public void Host_DisposeIsTerminalAndClosesActiveLease()
    {
        using var directory = new DurableTestDirectory(); var host = new DurableFlowHost();
        host.Start("save", () => Create(directory)); host.Tick(); host.Dispose(); host.Dispose();
        Assert.Equal(DurableFlowHostState.Disposed, host.State);
        Assert.Throws<ObjectDisposedException>(() => host.Start("save", () => Create(directory)));
        Assert.Throws<ObjectDisposedException>(() => host.Tick());
        Assert.Throws<ObjectDisposedException>(() => host.Execute(_ => { }));
        using var reopened = DurableFlowSession.Open(directory.Core, directory.Provider); Assert.Equal(1, reopened.Runtime.Now);
    }

    [Fact]
    public void Host_FactoryCannotReenterLifecycle()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        host.Start("save", () =>
        {
            Assert.Throws<InvalidOperationException>(() => host.Start("save", () => Create(directory)));
            Assert.Throws<InvalidOperationException>(() => host.Tick());
            Assert.Throws<InvalidOperationException>(() => host.Execute(_ => { }));
            Assert.Throws<InvalidOperationException>(() => host.Stop()); Assert.Throws<InvalidOperationException>(() => host.Dispose());
            return Create(directory);
        });
        Assert.Equal(DurableFlowHostState.Active, host.State); Assert.Equal(0, host.LogicalTick);
    }

    [Fact]
    public void Host_WrongThreadCannotTickExecuteStopOrDispose()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost(); host.Start("save", () => Create(directory));
        Exception? tick = null, execute = null, stop = null, dispose = null;
        var thread = new System.Threading.Thread(() =>
        {
            tick = Record.Exception(() => host.Tick()); execute = Record.Exception(() => host.Execute(_ => { }));
            stop = Record.Exception(() => host.Stop()); dispose = Record.Exception(() => host.Dispose());
        });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(tick); Assert.IsType<InvalidOperationException>(execute);
        Assert.IsType<InvalidOperationException>(stop); Assert.IsType<InvalidOperationException>(dispose);
        Assert.Equal(DurableFlowHostState.Active, host.State); Assert.Equal(0, host.LogicalTick);
        Assert.Throws<InvalidOperationException>(() => host.Runtime.AdvanceTo(1));
        host.Execute(runtime => Assert.Equal(0, runtime.AdvanceTo(1))); Assert.Equal(1, host.LogicalTick);
    }

    [Fact]
    public void Host_CommandFailureFencesSessionUntilStopReset()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost(); host.Start("save", () => Create(directory));
        FlowRuntime old = host.Runtime;
        Assert.Throws<HostFailure>(() => host.Execute(_ => throw new HostFailure()));
        Assert.Equal(DurableFlowHostState.Faulted, host.State);
        Assert.Throws<InvalidOperationException>(() => host.Execute(_ => { })); Assert.Throws<InvalidOperationException>(() => host.Tick());
        Assert.Throws<InvalidOperationException>(() => old.Cancel(CheckpointFixture.Parcel));
        host.Stop(); Assert.Equal(DurableFlowHostState.Inactive, host.State);
        Assert.True(host.Start("save", () => DurableFlowSession.Open(directory.Core, directory.Provider)));
        Assert.Equal(ParcelState.Created, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Host_PublicationCallbacksCannotReenterOrReleaseLease(int boundary)
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost(); bool armed = false, observed = false;
        host.Start("save", () => Create(directory, stage =>
        {
            if (!armed || (int)stage != boundary) return;
            Assert.Throws<InvalidOperationException>(() => host.Tick()); Assert.Throws<InvalidOperationException>(() => host.Execute(_ => { }));
            Assert.Throws<InvalidOperationException>(() => host.Stop()); Assert.Throws<InvalidOperationException>(() => host.Dispose());
            Assert.Throws<InvalidOperationException>(() => host.Start("save", () => Create(directory)));
            Assert.Throws<IOException>(() => DurableFlowSession.Open(directory.Core, directory.Provider)); observed = true;
        }));
        host.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel))); armed = true;
        Assert.Equal(1, host.Tick()); Assert.True(observed); Assert.Equal(DurableFlowHostState.Active, host.State);
        Assert.Equal(ParcelState.InTransit, host.Runtime.GetParcel(CheckpointFixture.Parcel).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Host_DueTickFailureLatchesAndFreshSessionRecoversCommittedCustody(int boundary)
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost(); bool armed = false;
        host.Start("save", () => Create(directory, stage => { if (armed && (int)stage == boundary) throw new HostFailure(); }));
        host.Execute(runtime => Assert.True(runtime.TryReserve(CheckpointFixture.Parcel))); armed = true;
        Assert.Throws<HostFailure>(() => host.Tick()); Assert.Equal(DurableFlowHostState.Faulted, host.State);
        Assert.Throws<InvalidOperationException>(() => host.Tick()); host.Stop();
        using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
        bool committed = boundary >= (int)DurableCommitStage.AfterProvider;
        Assert.Equal(committed ? 1 : 0, restored.ProviderRevision);
        Assert.Equal(committed ? 0 : 1, restored.GetPortSnapshot(CheckpointFixture.Origin).Inventory.Length);
        Assert.Equal(CheckpointFixture.Parcel, restored.Runtime.GetCargo(CheckpointFixture.Cargo).ClaimedBy);
    }

    [Theory]
    [InlineData(4)] [InlineData(5)]
    public void Host_StopFlushFailureFencesAndCanBeReset(int boundary)
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost(); bool armed = false;
        host.Start("save", () => Create(directory, stage => { if (armed && (int)stage == boundary) throw new HostFailure(); }));
        host.Tick(); FlowRuntime old = host.Runtime; armed = true;
        Assert.Throws<HostFailure>(() => host.Stop()); Assert.Equal(DurableFlowHostState.Faulted, host.State);
        Assert.Throws<InvalidOperationException>(() => old.Cancel(CheckpointFixture.Parcel));
        host.Stop(); Assert.Equal(DurableFlowHostState.Inactive, host.State);
        using var restored = DurableFlowSession.Open(directory.Core, directory.Provider);
        Assert.Equal(boundary == 5 ? 1 : 0, restored.Runtime.Now); Assert.Equal(0, restored.ProviderRevision);
    }

    [Fact]
    public void Host_LogicalClockOverflowFaultsWithoutPublishingAndReleasesLease()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        using (var initial = Create(directory)) initial.Execute(runtime => runtime.AdvanceTo(long.MaxValue));
        host.Start("save", () => DurableFlowSession.Open(directory.Core, directory.Provider));
        byte[] core = CoreBytes(directory), provider = ProviderBytes(directory);
        Assert.Equal(long.MaxValue, host.LogicalTick); Assert.Equal(0, host.Tick(paused: true));
        Assert.Throws<OverflowException>(() => host.Tick()); Assert.Equal(DurableFlowHostState.Faulted, host.State);
        Assert.Equal(core, CoreBytes(directory)); Assert.Equal(provider, ProviderBytes(directory));
        using var restored = DurableFlowSession.Open(directory.Core, directory.Provider); Assert.Equal(long.MaxValue, restored.Runtime.Now);
        host.Stop(); Assert.Equal(DurableFlowHostState.Inactive, host.State);
    }

    [Fact]
    public void Host_StopAtPersistedClockDoesNotPublishAnotherCheckpoint()
    {
        using var directory = new DurableTestDirectory(); using var host = new DurableFlowHost();
        host.Start("save", () => Create(directory)); host.Execute(runtime => runtime.AdvanceTo(17));
        byte[] core = CoreBytes(directory), provider = ProviderBytes(directory);
        host.Stop(); Assert.Equal(core, CoreBytes(directory)); Assert.Equal(provider, ProviderBytes(directory));
        host.Start("save", () => DurableFlowSession.Open(directory.Core, directory.Provider));
        Assert.Equal(17, host.LogicalTick); Assert.Equal(0, host.Tick()); Assert.Equal(18, host.LogicalTick);
    }

    private static DurableFlowSession Create(DurableTestDirectory directory, Action<DurableCommitStage>? fault = null)
        => DurableFlowSession.Create(directory.Core, directory.Provider, new CheckpointFixture().Runtime, fault);
    private static byte[] CoreBytes(DurableTestDirectory directory) => File.ReadAllBytes(Path.Combine(directory.Core, DurableFlowSession.CheckpointFileName));
    private static byte[] ProviderBytes(DurableTestDirectory directory) => File.ReadAllBytes(directory.ProviderPath);
    private sealed class HostFailure : Exception { }
}
