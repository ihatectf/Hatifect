using System;
using System.Collections.Generic;

namespace Hatifect.Flow.Inventory;

internal interface IFlowInventoryMutex
{
    bool IsLocked { get; }
    bool IsHeld { get; }
    void Request(Action acquired, Action failed);
    void Release();
}

// A lease never spans a game update. Deferred requests grant no cargo authority: their eventual
// grant is released, and dispatch retries on a later tick. Keep pending requests until their
// callback settles, even after Dispose; releasing a mutex cannot cancel a queued NetMutex event.
internal sealed class FlowInventoryLocks : IDisposable
{
    private readonly Dictionary<object, Request> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Lease> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly Action<Exception> _report;
    private bool _disposed;
    internal FlowInventoryLocks(Action<Exception> report) => _report = report;

    internal bool Owns(object identity) => _owned.TryGetValue(identity, out Lease? lease) && lease.Mutex.IsHeld;
    internal bool HasPending => _pending.Count != 0;
    internal void RetryCleanup()
    {
        if (_pending.Count == 0) return;
        foreach (Request request in new List<Request>(_pending.Values))
            if (request.NeedsRelease) request.Acquired();
    }

    internal IDisposable? TryAcquire(object identity, IFlowInventoryMutex mutex)
    {
        if (_disposed || _pending.ContainsKey(identity) || _owned.ContainsKey(identity) || mutex.IsLocked
            || _pending.Count + _owned.Count >= 64) return null;
        var request = new Request(this, identity, mutex);
        _pending.Add(identity, request);
        try
        {
            mutex.Request(request.Acquired, request.Failed);
            if (request.Finished || !mutex.IsHeld) { request.Deferred = true; return null; }
            _pending.Remove(identity);
            var lease = new Lease(this, identity, mutex);
            request.Lease = lease;
            _owned.Add(identity, lease);
            return lease;
        }
        catch
        {
            request.Deferred = true;
            // A throwing accessor can still have queued or granted the request.
            if (mutex.IsHeld) request.Acquired();
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (Request request in _pending.Values) request.Deferred = true;
        List<Exception>? errors = null;
        foreach (Lease lease in new List<Lease>(_owned.Values))
            try { lease.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        RetryCleanup();
        if (errors is not null) throw new AggregateException(errors);
    }

    private sealed class Request
    {
        private readonly FlowInventoryLocks _owner;
        private readonly object _identity;
        private readonly IFlowInventoryMutex _mutex;
        internal bool Deferred;
        internal bool Finished;
        internal bool NeedsRelease;
        internal Lease? Lease;
        internal Request(FlowInventoryLocks owner, object identity, IFlowInventoryMutex mutex)
        { _owner = owner; _identity = identity; _mutex = mutex; }

        internal void Acquired()
        {
            if (Finished || Lease is not null || !Deferred && !_owner._disposed) return;
            NeedsRelease = true;
            try
            {
                if (_mutex.IsHeld) _mutex.Release();
                Finished = true;
                NeedsRelease = false;
                RemoveCurrent();
            }
            catch (Exception error) { _owner._report(error); }
        }
        internal void Failed()
        {
            if (Finished || Lease is not null || NeedsRelease) return;
            Finished = true;
            RemoveCurrent();
        }
        private void RemoveCurrent()
        {
            if (_owner._pending.TryGetValue(_identity, out Request? current) && ReferenceEquals(current, this))
                _owner._pending.Remove(_identity);
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly FlowInventoryLocks _owner;
        private readonly object _identity;
        private bool _disposed;
        internal IFlowInventoryMutex Mutex { get; }
        internal Lease(FlowInventoryLocks owner, object identity, IFlowInventoryMutex mutex)
        { _owner = owner; _identity = identity; Mutex = mutex; }
        public void Dispose()
        {
            if (_disposed) return;
            if (Mutex.IsHeld) Mutex.Release();
            _disposed = true;
            _owner._owned.Remove(_identity);
        }
    }
}
