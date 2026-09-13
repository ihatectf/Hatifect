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
    private readonly FlowProviderMode _providerMode;
    private readonly FlowParcelActions _supportedOperations;
    private FlowSnapshot _snapshot;
    private bool _operating;
    private bool _notifying;
    private FlowApplicationState _availability = FlowApplicationState.Active;

    internal FlowApplication(FlowRuntime runtime, Action<Action<FlowRuntime>> execute, Action<Exception> reportError,
        Func<bool>? canExecute = null, FlowProviderMode providerMode = FlowProviderMode.DiagnosticFake,
        FlowParcelActions supportedOperations = FlowParcelActions.All)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
        _canExecute = canExecute ?? (() => true);
        if (!Enum.IsDefined(typeof(FlowProviderMode), providerMode)) throw new ArgumentOutOfRangeException(nameof(providerMode));
        if ((supportedOperations & ~FlowParcelActions.All) != 0) throw new ArgumentOutOfRangeException(nameof(supportedOperations));
        _providerMode = providerMode;
        _supportedOperations = supportedOperations;
        Guid session = Guid.NewGuid();
        _snapshot = runtime.ReadApplicationSnapshot(session, 0, _providerMode, _supportedOperations);
    }

    public event Action<long>? RevisionChanged;

    internal FlowCommandResult? ValidateExternalCommand(Guid session, long revision, bool recovery = false)
    {
        RequireIdle();
        if (_snapshot.State is FlowApplicationState.Closed or FlowApplicationState.Faulted)
            return Result(FlowCommandStatus.SessionClosed, _snapshot.Code);
        if (recovery ? _snapshot.State != FlowApplicationState.RecoveryRequired : _snapshot.State != FlowApplicationState.Active)
            return Result(FlowCommandStatus.Rejected, _snapshot.Code);
        if (!recovery && !_canExecute()) return Result(FlowCommandStatus.Rejected, FlowRejectionCode.ProviderUnavailable);
        if (session != _snapshot.SessionId) return Result(FlowCommandStatus.Conflict, FlowRejectionCode.StaleSession);
        return revision != _snapshot.Revision ? Result(FlowCommandStatus.Conflict, FlowRejectionCode.StaleRevision) : null;
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
        FlowCommandResult? invalid = ValidateExternalCommand(command.SessionId, command.ExpectedRevision);
        if (invalid is not null) return invalid;
        if (!Enum.IsDefined(typeof(FlowParcelAction), command.Action)) return Result(FlowCommandStatus.InvalidCommand);
        FlowParcelSnapshot? parcel = _snapshot.Parcels.FirstOrDefault(value => value.Id == command.ParcelId);
        if (parcel is null) return Result(FlowCommandStatus.InvalidCommand, FlowRejectionCode.ParcelNotFound);
        FlowActionAvailability availability = parcel.Availability[command.Action];
        if (!availability.Available) return Result(FlowCommandStatus.Rejected, availability.Code);
        bool applied = false;
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
        return applied ? Result(FlowCommandStatus.Applied)
            : Result(FlowCommandStatus.Rejected, _snapshot.Parcels.FirstOrDefault(value => value.Id == command.ParcelId)?.Availability[command.Action].Code
                ?? FlowRejectionCode.ParcelNotFound);
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
            next = _runtime.ReadApplicationSnapshot(_snapshot.SessionId, checked(_snapshot.Revision + 1), _providerMode, _supportedOperations);
            if (_availability != FlowApplicationState.Active)
            {
                FlowActionAvailabilitySet blocked = FlowActionAvailabilitySet.Disabled(FlowReasons.State(_availability));
                if (_supportedOperations != FlowParcelActions.All) blocked = blocked.Restrict(_supportedOperations);
                next = new FlowSnapshot(next.SessionId, next.NetworkId, next.Revision, _availability,
                    next.Stations.ToArray(), next.Links.ToArray(), next.Parcels.Select(parcel => parcel with { Actions = FlowParcelActions.None, Availability = blocked }).ToArray(),
                    _providerMode, _supportedOperations);
            }
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
        if (_snapshot.State == FlowApplicationState.Closed
            || _snapshot.State == FlowApplicationState.Faulted && state != FlowApplicationState.Closed)
        {
            return;
        }
        _snapshot = new FlowSnapshot(_snapshot.SessionId, _snapshot.NetworkId, checked(_snapshot.Revision + 1), state,
            Array.Empty<FlowStationSnapshot>(), Array.Empty<FlowLinkSnapshot>(), Array.Empty<FlowParcelSnapshot>(), _providerMode, _supportedOperations);
        Notify();
        // A fault stops operations; owner disposal is the final lifetime notification for retained projections.
        if (state == FlowApplicationState.Closed) RevisionChanged = null;
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

    private FlowCommandResult Result(FlowCommandStatus status, FlowRejectionCode code = FlowRejectionCode.None) => new(status, _snapshot.Revision, code);
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
