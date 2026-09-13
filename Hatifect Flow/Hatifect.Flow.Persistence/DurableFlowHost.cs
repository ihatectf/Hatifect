using System;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Checkpoints;
using Hatifect.Flow.Domain.Identity;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal enum DurableFlowHostState
{
    Inactive, Active, Faulted, Disposed
}

// Host-neutral lifecycle and logical clock for one explicitly opened fake pair.
// Idle updates inspect only the queue head; durable work stays in session commands.
internal sealed class DurableFlowHost : IDisposable
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private DurableFlowSession? _session;
    private string? _identity;
    private DurableFlowHostState _state;
    private long _logicalTick;
    private bool _operating;

    public DurableFlowHostState State { get { RequireThreadAndIdle(); return _state; } }
    public long LogicalTick { get { RequireThreadAndIdle(); return _logicalTick; } }
    public FlowRuntime Runtime { get { RequireActive(); return _session!.Runtime; } }
    public long Revision { get { RequireActive(); return _session!.Revision; } }
    public long ProviderRevision { get { RequireActive(); return _session!.ProviderRevision; } }

    public PortCheckpoint GetPortSnapshot(StationId station)
    {
        RequireActive();
        return _session!.GetPortSnapshot(station);
    }

    public bool Start(string identity, Func<DurableFlowSession> open)
    {
        RequireAvailable();
        if (string.IsNullOrWhiteSpace(identity))
            throw new ArgumentException("A durable host identity is required.", nameof(identity));
        ArgumentNullException.ThrowIfNull(open);
        if (_state == DurableFlowHostState.Active)
        {
            if (string.Equals(_identity, identity, StringComparison.Ordinal)) return false;
            throw new InvalidOperationException("Stop the active durable host before changing its identity.");
        }
        if (_state == DurableFlowHostState.Faulted)
            throw new InvalidOperationException("Stop the faulted durable host before starting again.");
        _operating = true;
        try
        {
            _session = open() ?? throw new InvalidOperationException("The durable host factory returned no session.");
            _logicalTick = _session.Runtime.Now;
            _identity = identity;
            _state = DurableFlowHostState.Active;
            return true;
        }
        catch (Exception error)
        {
            FaultAndClose(error);
            throw;
        }
        finally { _operating = false; }
    }

    public int Tick(bool paused = false)
    {
        RequireAvailable();
        if (_state == DurableFlowHostState.Inactive) return 0;
        RequireActive();
        if (paused) return 0;
        _operating = true;
        try
        {
            _logicalTick = checked(_logicalTick + 1);
            FlowRuntime runtime = _session!.Runtime;
            var next = runtime.PeekNextOperation();
            if (next is null || next.DueTick > _logicalTick) return 0;
            int processed = 0;
            _session.Execute(flow => processed = flow.AdvanceTo(_logicalTick));
            return processed;
        }
        catch (Exception error)
        {
            FaultAndClose(error);
            throw;
        }
        finally { _operating = false; }
    }

    public void Execute(Action<FlowRuntime> action)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(action);
        _operating = true;
        try
        {
            _session!.Execute(runtime =>
            {
                runtime.AdvanceTo(_logicalTick);
                action(runtime);
            });
            // An explicit command may advance farther than the update clock.
            _logicalTick = _session.Runtime.Now;
        }
        catch (Exception error)
        {
            FaultAndClose(error);
            throw;
        }
        finally { _operating = false; }
    }

    public void Stop()
    {
        RequireAvailable();
        _operating = true;
        try { StopCore(); }
        finally { _operating = false; }
    }

    public void Dispose()
    {
        RequireThreadAndIdle();
        if (_state == DurableFlowHostState.Disposed) return;
        _operating = true;
        try { StopCore(); }
        finally
        {
            _state = DurableFlowHostState.Disposed;
            _operating = false;
        }
    }

    private void StopCore()
    {
        if (_state == DurableFlowHostState.Inactive) return;
        try
        {
            if (_state == DurableFlowHostState.Active && _session!.Runtime.Now < _logicalTick)
            {
                // Due updates already advanced Core.Now, even when bounded work
                // remains. This publishes only the idle clock accumulated since.
                _session.Execute(runtime => runtime.AdvanceTo(_logicalTick));
            }
            _session?.Dispose();
            _session = null;
            _identity = null;
            _state = DurableFlowHostState.Inactive;
        }
        catch (Exception error)
        {
            FaultAndClose(error);
            throw;
        }
    }

    private void FaultAndClose(Exception error)
    {
        _state = DurableFlowHostState.Faulted;
        if (_session is not null)
        {
            CheckpointWriterLease.DisposeAfterFailure(_session, error);
            _session = null;
        }
        _identity = null;
    }

    private void RequireActive()
    {
        RequireAvailable();
        if (_state != DurableFlowHostState.Active)
            throw new InvalidOperationException("The durable host is not active.");
    }

    private void RequireAvailable()
    {
        RequireThreadAndIdle();
        if (_state == DurableFlowHostState.Disposed)
            throw new ObjectDisposedException(nameof(DurableFlowHost));
    }

    private void RequireThreadAndIdle()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread || _operating)
            throw new InvalidOperationException("The durable host requires its owning thread outside an active operation.");
    }
}
