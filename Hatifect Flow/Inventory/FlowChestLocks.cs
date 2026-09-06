using System;
using StardewValley.Network;
using StardewValley.Objects;

namespace Hatifect.Flow.Inventory;

internal sealed class FlowChestLocks : IDisposable
{
    private readonly FlowInventoryLocks _locks;
    private readonly Func<bool> _required;
    private readonly Func<Chest, IFlowInventoryMutex> _mutex;
    internal FlowChestLocks(Func<bool> required, Action<Exception> report, Func<Chest, IFlowInventoryMutex>? mutex = null)
    {
        _required = required;
        _locks = new FlowInventoryLocks(report);
        _mutex = mutex ?? (static chest => new ChestMutex(chest.GetMutex()));
    }
    internal bool Required => _required();
    internal bool HasPending => _locks.HasPending;
    internal void RetryCleanup() => _locks.RetryCleanup();
    internal bool Owns(Chest chest) => _locks.Owns(chest);
    internal IDisposable? TryAcquire(Chest chest)
    {
        if (!Required) return chest.GetMutex().IsLocked() ? null : NoLock.Instance;
        return _locks.TryAcquire(chest, _mutex(chest));
    }
    public void Dispose() => _locks.Dispose();

    private sealed class NoLock : IDisposable
    {
        internal static readonly NoLock Instance = new();
        public void Dispose() { }
    }
    private sealed class ChestMutex : IFlowInventoryMutex
    {
        private readonly NetMutex _mutex;
        internal ChestMutex(NetMutex mutex) => _mutex = mutex;
        public bool IsLocked => _mutex.IsLocked();
        public bool IsHeld => _mutex.IsLockHeld();
        public void Request(Action acquired, Action failed) => _mutex.RequestLock(acquired, failed);
        public void Release() => _mutex.ReleaseLock();
    }
}
