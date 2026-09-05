using System;
using System.Collections.Generic;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Ports;
using Hatifect.Flow.Domain.Shipments;

namespace Hatifect.Flow.Sessions;

internal sealed partial class FlowGameSession
{
    public IReadOnlyList<FlowRecoveryIssue> ReadRecovery()
    {
        RequireThread();
        var issues = new List<FlowRecoveryIssue>();
        foreach (FlowParcelSnapshot value in ReadSnapshot().Parcels)
        {
            Parcel parcel = _runtime.GetParcel(new ParcelId(value.Id));
            if (parcel.PendingTransfer is not PortTransfer transfer) continue;
            PortResult receipt = _ports[transfer.StationId.Value].ReadResult(transfer);
            issues.Add(new FlowRecoveryIssue(value.Id, value.ItemKey, value.Quantity, parcel.State.ToString(), receipt.ToString(),
                ResolveChest(transfer.StationId.Value) is not null, _faulted && receipt is PortResult.Applied or PortResult.Rejected));
        }
        return issues.AsReadOnly();
    }

    public FlowCommandResult Execute(FlowRecoveryCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireThread();
        if (_hostOperation) return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision);
        if (_managing) throw new InvalidOperationException("Recovery cannot reenter the session owner.");
        FlowCommandStatus? rejected = Application.ValidateExternalCommand(command.SessionId, command.ExpectedRevision, recovery: true);
        if (rejected.HasValue) return new FlowCommandResult(rejected.Value, ReadSnapshot().Revision);
        if (_closed || _saving || !_canMutate()) return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision);
        if (!ReadSnapshot().Parcels.Any(parcel => parcel.Id == command.ParcelId))
            return new FlowCommandResult(FlowCommandStatus.InvalidCommand, ReadSnapshot().Revision);
        _managing = true;
        try
        {
            Parcel parcel = _runtime.GetParcel(new ParcelId(command.ParcelId));
            if (parcel.PendingTransfer is not PortTransfer transfer || _ports[transfer.StationId.Value].ReadResult(transfer) == PortResult.Missing)
                return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision);
            if (!_runtime.ReconcileTransfer(parcel.Id)) return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision);
            // Reconciliation only reads a settled receipt; it cannot write a physical inventory.
            _faulted = ReadSnapshot().Parcels.Any(value => _runtime.GetParcel(new ParcelId(value.Id)).PendingTransfer is not null);
            UpdateAvailability();
            Application.Refresh();
            return new FlowCommandResult(FlowCommandStatus.Applied, ReadSnapshot().Revision);
        }
        catch (Exception error)
        {
            _faulted = true;
            UpdateAvailability();
            _report(error);
            return new FlowCommandResult(FlowCommandStatus.Faulted, ReadSnapshot().Revision);
        }
        finally { _managing = false; }
    }
}
