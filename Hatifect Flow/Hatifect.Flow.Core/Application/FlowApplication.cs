using System;
using System.Linq;
using Hatifect.Flow.Application.Dispatch;
using Hatifect.Flow.Domain.Identity;

namespace Hatifect.Flow.Application;

// The host calls Refresh after its committed work, never on an unchanged frame.
// The executor keeps existing durable/session ownership rules outside this UI-free boundary.
internal sealed class FlowApplication : IFlowApplication, IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly FlowRuntime _runtime;
    private readonly Action<Action<FlowRuntime>> _execute;
    private readonly Action<Exception> _reportError;
    private readonly Func<bool> _canExecute;
    private FlowSnapshot _snapshot;
    private bool _operating;
    private bool _notifying;
    private FlowApplicationState _availability = FlowApplicationState.Active;

    internal FlowApplication(FlowRuntime runtime, Action<Action<FlowRuntime>> execute, Action<Exception> reportError,
        Func<bool>? canExecute = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _canExecute = canExecute ?? (() => true);
        Guid session = Guid.NewGuid();
        _snapshot = runtime.ReadApplicationSnapshot(session, 0);
    }

    public event Action<long>? RevisionChanged;

    internal FlowCommandStatus? ValidateExternalCommand(Guid session, long revision, bool recovery = false)
    {
        RequireIdle();
        if (_snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted) return FlowCommandStatus.SessionClosed;
        if (recovery ? _snapshot.State != FlowApplicationState.RecoveryRequired
            : _snapshot.State != FlowApplicationState.Active || !_canExecute()) return FlowCommandStatus.Rejected;
        return session != _snapshot.SessionId || revision != _snapshot.Revision ? FlowCommandStatus.Conflict : null;
    }

    public FlowSnapshot ReadSnapshot()
    {
        RequireThread();
        return _snapshot;
    }

    public FlowCommandResult Execute(FlowParcelCommand command)
    {
        RequireIdle();
        ArgumentNullException.ThrowIfNull(command);
        if (_snapshot.State != FlowApplicationState.Active)
        {
            return Result(_snapshot.State is FlowApplicationState.Paused or FlowApplicationState.RecoveryRequired
                ? FlowCommandStatus.Rejected : FlowCommandStatus.SessionClosed);
        }
        if (command.SessionId != _snapshot.SessionId || command.ExpectedRevision != _snapshot.Revision)
        {
            return Result(FlowCommandStatus.Conflict);
        }
        if (command.ParcelId == Guid.Empty || !Enum.IsDefined(typeof(FlowParcelAction), command.Action)
            || !_snapshot.Parcels.Any(parcel => parcel.Id == command.ParcelId))
        {
            return Result(FlowCommandStatus.InvalidCommand);
        }
        bool applied = false;
        if (!_canExecute()) return Result(FlowCommandStatus.Rejected);
        _operating = true;
        try
        {
            _execute(runtime =>
            {
                var id = new ParcelId(command.ParcelId);
                applied = command.Action switch
                {
                    FlowParcelAction.Reserve => runtime.TryReserve(id),
                    FlowParcelAction.Cancel => runtime.Cancel(id),
                    FlowParcelAction.RetryDelivery => runtime.RetryDelivery(id),
                    FlowParcelAction.ReconcileTransfer => runtime.ReconcileTransfer(id),
                    FlowParcelAction.ReturnToSource => runtime.ReturnToSource(id),
                    _ => false
                };
            });
        }
        catch (Exception error)
        {
            _operating = false;
            Close(FlowApplicationState.Faulted);
            Report(error);
            return Result(FlowCommandStatus.Faulted);
        }
        finally
        {
            _operating = false;
        }
        try { Refresh(); }
        catch (Exception error)
        {
            Close(FlowApplicationState.Faulted);
            Report(error);
            return Result(FlowCommandStatus.Faulted);
        }
        return Result(applied ? FlowCommandStatus.Applied : FlowCommandStatus.Rejected);
    }

    internal void Refresh(bool force = false)
    {
        RequireIdle();
        if (_snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted)
        {
            return;
        }
        FlowSnapshot next;
        _operating = true;
        try
        {
            next = _runtime.ReadApplicationSnapshot(_snapshot.SessionId, checked(_snapshot.Revision + 1));
            if (_availability != FlowApplicationState.Active)
                next = new FlowSnapshot(next.SessionId, next.NetworkId, next.Revision, _availability,
                    next.Stations.ToArray(), next.Links.ToArray(), next.Parcels.Select(parcel => parcel with { Actions = FlowParcelActions.None }).ToArray());
        }
        finally
        {
            _operating = false;
        }
        if (!force && _snapshot.State == next.State && _snapshot.Stations.SequenceEqual(next.Stations) && _snapshot.Links.SequenceEqual(next.Links)
            && _snapshot.Parcels.SequenceEqual(next.Parcels))
        {
            return;
        }
        _snapshot = next;
        Notify();
    }

    public void Dispose() => Close(FlowApplicationState.Closed);

    internal void SetAvailability(FlowApplicationState state)
    {
        RequireIdle();
        if (state is not (FlowApplicationState.Active or FlowApplicationState.Paused or FlowApplicationState.RecoveryRequired))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (_availability == state || _snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted) return;
        _availability = state;
        Refresh();
    }

    private void Close(FlowApplicationState state)
    {
        RequireIdle();
        if (_snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted)
        {
            return;
        }
        _snapshot = new FlowSnapshot(_snapshot.SessionId, _snapshot.NetworkId, checked(_snapshot.Revision + 1), state,
            Array.Empty<FlowStationSnapshot>(), Array.Empty<FlowLinkSnapshot>(), Array.Empty<FlowParcelSnapshot>());
        Notify();
        RevisionChanged = null;
    }

    private void Notify()
    {
        _notifying = true;
        try
        {
            if (RevisionChanged is not Action<long> handlers)
            {
                return;
            }
            foreach (Action<long> handler in handlers.GetInvocationList())
            {
                try { handler(_snapshot.Revision); }
                catch (Exception error) { Report(error); }
            }
        }
        finally { _notifying = false; }
    }

    private void Report(Exception error)
    {
        // Observer/diagnostic failures cannot undo an already committed command or suppress later observers.
        try { _reportError(error); }
        catch { /* The host owns logging; the application outcome remains authoritative. */ }
    }

    private FlowCommandResult Result(FlowCommandStatus status) => new(status, _snapshot.Revision);
    private void RequireThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException("Flow application access requires its owning thread.");
        }
    }
    private void RequireIdle()
    {
        RequireThread();
        if (_operating || _notifying)
        {
            throw new InvalidOperationException("Flow application commands cannot reenter a commit or notification.");
        }
    }
}
