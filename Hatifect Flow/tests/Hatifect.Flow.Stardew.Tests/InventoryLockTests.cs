using System;
using System.Collections.Generic;
using Hatifect.Flow.Inventory;
using Xunit;

namespace Hatifect.Flow.Stardew.Tests;

[Trait("Category", "flow")]
public sealed class InventoryLockTests
{
    [Fact]
    public void ImmediateLeaseReleasesOnlyItsAcquiredMutexEvenOnFailure()
    {
        var errors = new List<Exception>();
        using var locks = new FlowInventoryLocks(errors.Add);
        var mutex = new Mutex { Immediate = true };
        object identity = new();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using IDisposable lease = Assert.IsAssignableFrom<IDisposable>(locks.TryAcquire(identity, mutex));
            Assert.True(locks.Owns(identity));
            Assert.Null(locks.TryAcquire(identity, mutex));
            throw new InvalidOperationException("Physical callback failed.");
        }));
        Assert.False(mutex.IsLocked);
        Assert.False(locks.Owns(identity));
        Assert.Equal(1, mutex.Releases);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingForeignOrPlayerMenuMutexIsNeverBorrowedOrReleased(bool heldByLocalPlayer)
    {
        using var locks = new FlowInventoryLocks(_ => throw new Exception("No callback is expected."));
        var mutex = new Mutex { IsLocked = true, IsHeld = heldByLocalPlayer };
        Assert.Null(locks.TryAcquire(new object(), mutex));
        Assert.Equal(0, mutex.Requests);
        Assert.Equal(0, mutex.Releases);
        Assert.True(mutex.IsLocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredGrantIsReleasedWithoutIssuingLeaseEvenAfterOwnerDisposal(bool dispose)
    {
        var errors = new List<Exception>();
        using var locks = new FlowInventoryLocks(errors.Add);
        object identity = new();
        var mutex = new Mutex();
        Assert.Null(locks.TryAcquire(identity, mutex));
        Assert.Null(locks.TryAcquire(identity, mutex));
        Assert.Equal(1, mutex.Requests);
        if (dispose) locks.Dispose();
        Assert.Equal(0, mutex.Releases);
        mutex.Grant();
        Assert.False(mutex.IsLocked);
        Assert.False(locks.Owns(identity));
        Assert.Equal(1, mutex.Releases);
        mutex.Immediate = true;
        using IDisposable? retry = locks.TryAcquire(identity, mutex);
        if (dispose) Assert.Null(retry);
        else { Assert.NotNull(retry); Assert.True(locks.Owns(identity)); }
        Assert.Empty(errors);
    }

    [Fact]
    public void FailedRequestCanRetryAndLostLeaseDoesNotReleaseAnotherOwner()
    {
        using var locks = new FlowInventoryLocks(_ => throw new Exception("No callback failure is expected."));
        object identity = new();
        var mutex = new Mutex();
        Assert.Null(locks.TryAcquire(identity, mutex));
        mutex.Fail();
        mutex.Immediate = true;
        using IDisposable lease = Assert.IsAssignableFrom<IDisposable>(locks.TryAcquire(identity, mutex));
        mutex.IsHeld = false;
        lease.Dispose();
        Assert.Equal(0, mutex.Releases);
        Assert.True(mutex.IsLocked);
    }

    private sealed class Mutex : IFlowInventoryMutex
    {
        internal bool Immediate;
        internal int Requests, Releases;
        internal bool FailNextRelease;
        private Action? _acquired, _failed;
        public bool IsLocked { get; set; }
        public bool IsHeld { get; set; }
        public void Request(Action acquired, Action failed)
        {
            Requests++;
            _acquired = acquired; _failed = failed;
            if (Immediate) { IsLocked = IsHeld = true; }
        }
        internal void Grant() { IsLocked = IsHeld = true; _acquired?.Invoke(); }
        internal void Fail() => _failed?.Invoke();
        public void Release()
        {
            Releases++;
            if (FailNextRelease) { FailNextRelease = false; throw new InvalidOperationException("Release observer failed."); }
            IsLocked = IsHeld = false; _acquired = _failed = null;
        }
    }

    [Fact]
    public void FailedDeferredReleaseRetainsCleanupWithoutGrantingCargoAuthority()
    {
        var errors = new List<Exception>();
        using var locks = new FlowInventoryLocks(errors.Add);
        object identity = new();
        var mutex = new Mutex { FailNextRelease = true };
        Assert.Null(locks.TryAcquire(identity, mutex));
        mutex.Grant();
        Assert.True(mutex.IsHeld);
        Assert.True(locks.HasPending);
        Assert.False(locks.Owns(identity));
        Assert.Single(errors);
        locks.Dispose();
        Assert.False(mutex.IsLocked);
        Assert.False(locks.HasPending);
        Assert.Equal(2, mutex.Releases);
    }

    [Fact]
    public void FailedOwnedReleaseCanBeRetriedByTheOriginalLease()
    {
        using var locks = new FlowInventoryLocks(_ => { });
        object identity = new();
        var mutex = new Mutex { Immediate = true, FailNextRelease = true };
        using IDisposable lease = Assert.IsAssignableFrom<IDisposable>(locks.TryAcquire(identity, mutex));
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        Assert.True(locks.Owns(identity));
        lease.Dispose();
        Assert.False(locks.Owns(identity));
        Assert.False(mutex.IsLocked);
        Assert.Equal(2, mutex.Releases);
    }
}
