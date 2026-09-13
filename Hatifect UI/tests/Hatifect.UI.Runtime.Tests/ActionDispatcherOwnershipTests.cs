using System;
using System.Threading;
using System.Threading.Tasks;
using Hatifect.UI.Experience;
using Hatifect.UI.Runtime.Actions;
using Xunit;

namespace Hatifect.UI.Runtime.Tests;

public sealed class ActionDispatcherOwnershipTests
{
    [Fact]
    public void InventoryReadsActualBoundActionsWithoutMutatingState()
    {
        Guid session = Guid.NewGuid(), generation = Guid.NewGuid();
        using var owner = new UiActionDispatcher(session, generation);
        int availabilityReads = 0;
        owner.Bind(Action(Id("available")));
        owner.Bind(Action(Id("disabled"), availability: () =>
        { availabilityReads++; return UiActionAvailability.Disabled(new("domain.field", "Choose a target.")); }));
        var pending = Completion();
        var running = owner.Bind(Action(Id("running"), () => pending.Task));
        running.Invoke(1);

        UiActionDispatcherOwnershipSnapshot snapshot = owner.CaptureOwnership();

        Assert.Equal(session, snapshot.SessionId);
        Assert.Equal(generation, snapshot.GenerationId);
        Assert.Equal(Environment.CurrentManagedThreadId, snapshot.OwnerThread);
        Assert.False(snapshot.Terminal);
        Assert.Equal(new[]
        {
            new UiActionOwnershipEntry(Id("available"), UiActionState.Available, false, 0),
            new UiActionOwnershipEntry(Id("disabled"), UiActionState.Available, false, 0),
            new UiActionOwnershipEntry(Id("running"), UiActionState.Running, false, 0),
        }, snapshot.Actions);
        // Capturing must not itself refresh availability or complete pending work.
        Assert.Equal(0, availabilityReads);
        Assert.Equal(UiActionState.Running, owner.CaptureOwnership().Actions[2].State);
        pending.SetResult(UiActionResult<int>.Success(1));
        PumpCompletion(owner);
    }

    [Fact]
    public void InventoryIsSortedByActionIdRegardlessOfBindOrder()
    {
        using var owner = new UiActionDispatcher(Guid.NewGuid(), Guid.NewGuid());
        owner.Bind(Action(Id("zebra")));
        owner.Bind(Action(Id("alpha")));
        owner.Bind(Action(Id("mango")));

        Assert.Equal(new[] { Id("alpha"), Id("mango"), Id("zebra") },
            owner.CaptureOwnership().Actions.Select(entry => entry.Action));
    }

    [Fact]
    public void QueuedWaitingCountIsVisibleInTheCapturedInventory()
    {
        using var owner = new UiActionDispatcher(Guid.NewGuid(), Guid.NewGuid());
        var pending = Completion();
        var execution = owner.Bind(Action(Id("queued"), () => pending.Task, UiActionConcurrency.Queue(4)));
        execution.Invoke(1);
        execution.Invoke(2);
        execution.Invoke(3);

        UiActionOwnershipEntry entry = Assert.Single(owner.CaptureOwnership().Actions);
        Assert.Equal(UiActionState.Running, entry.State);
        Assert.Equal(2, entry.WaitingCount);
        Assert.False(entry.IsRetired);

        pending.SetResult(UiActionResult<int>.Success(1));
        PumpCompletion(owner);
        PumpCompletion(owner);
    }

    [Fact]
    public void RetiredActionRemainsVisibleWithTerminalStateWhileItsSiblingDoesNot()
    {
        using var owner = new UiActionDispatcher(Guid.NewGuid(), Guid.NewGuid());
        var retired = owner.Bind(Action(Id("retired")));
        var sibling = owner.Bind(Action(Id("sibling")));
        retired.Retire();

        UiActionDispatcherOwnershipSnapshot snapshot = owner.CaptureOwnership();

        Assert.False(snapshot.Terminal);
        Assert.Equal(new[]
        {
            new UiActionOwnershipEntry(Id("retired"), UiActionState.Cancelled, true, 0),
            new UiActionOwnershipEntry(Id("sibling"), UiActionState.Available, false, 0),
        }, snapshot.Actions);
    }

    [Fact]
    public void DispatcherDisposalPublishesTerminalEmptyInventory()
    {
        var owner = new UiActionDispatcher(Guid.NewGuid(), Guid.NewGuid());
        owner.Bind(Action(Id("a")));
        owner.Bind(Action(Id("b")));
        Assert.Equal(2, owner.CaptureOwnership().Actions.Count);

        owner.Dispose();

        UiActionDispatcherOwnershipSnapshot snapshot = owner.CaptureOwnership();
        Assert.True(snapshot.Terminal);
        Assert.Empty(snapshot.Actions);
    }

    [Fact]
    public void ForeignThreadCannotReadTheActionDispatcherOwner()
    {
        using var owner = new UiActionDispatcher(Guid.NewGuid(), Guid.NewGuid());
        owner.Bind(Action(Id("owned")));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { owner.CaptureOwnership(); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Worker did not complete.");

        Assert.IsType<InvalidOperationException>(failure);
        Assert.False(owner.CaptureOwnership().Terminal);
        Assert.Single(owner.CaptureOwnership().Actions);
    }

    private static UiSymbolId Id(string path) => new("Tests", "action-ownership/" + path);

    private static UiAction<int, int> Action(UiSymbolId id,
        Func<Task<UiActionResult<int>>>? execute = null,
        UiActionConcurrency? concurrency = null,
        Func<UiActionAvailability>? availability = null)
        => new(id, "Action",
            (_, _) => execute is null ? new(UiActionResult<int>.Success(0)) : new(execute()),
            concurrency ?? UiActionConcurrency.RejectWhileRunning, availability);

    private static TaskCompletionSource<UiActionResult<int>> Completion() => new();

    private static void PumpCompletion(UiActionDispatcher owner)
        => Assert.True(SpinWait.SpinUntil(owner.Pump, TimeSpan.FromSeconds(10)), "No completion reached the owning-thread dispatcher.");
}
