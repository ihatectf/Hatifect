using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Inventory;
using StardewValley.Objects;

namespace Hatifect.Flow.Sessions;

internal sealed partial class FlowGameSession : IFlowNetworkApplication
{
    private FlowCapturedTarget? _target;
    private bool _managing;

    public event Action<long>? RevisionChanged
    { add => Application.RevisionChanged += value; remove => Application.RevisionChanged -= value; }
    public FlowSnapshot ReadSnapshot() => Application.ReadSnapshot();
    public FlowCommandResult Execute(FlowParcelCommand command) => Application.Execute(command);

    public IReadOnlyList<FlowInventorySlot> ReadInventory(Guid station)
    {
        RequireThread();
        return _stations.ContainsKey(station) ? _inventory.ReadInventory(station) : Array.Empty<FlowInventorySlot>();
    }

    public FlowCommandResult Execute(FlowSendCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_managing) throw new InvalidOperationException("Network commands cannot reenter their owner.");
        FlowCommandResult? rejected = Application.ValidateExternalCommand(command.SessionId, command.ExpectedRevision);
        if (rejected is not null) return rejected;
        _managing = true;
        try
        {
            StationBinding source = RequireStation(command.Source), destination = RequireStation(command.Destination);
            if (string.IsNullOrEmpty(command.Fingerprint) || command.Fingerprint.Length != 64 || command.Quantity is <= 0 or > 999)
                return new FlowCommandResult(FlowCommandStatus.InvalidCommand, ReadSnapshot().Revision);
            if (!SendCore(source.Name, destination.Name, command.Slot, command.Fingerprint, command.Quantity).HasValue)
                return new FlowCommandResult(FlowCommandStatus.Conflict, ReadSnapshot().Revision);
            return new FlowCommandResult(FlowCommandStatus.Applied, ReadSnapshot().Revision);
        }
        catch (SendAdmissionFailure error)
        { return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision, error.Code); }
        catch (ArgumentOutOfRangeException)
        { return new FlowCommandResult(FlowCommandStatus.InvalidCommand, ReadSnapshot().Revision); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return new FlowCommandResult(_faulted ? FlowCommandStatus.Faulted : FlowCommandStatus.Rejected, ReadSnapshot().Revision); }
        finally { _managing = false; }
    }

    internal void CaptureTarget(string location, int x, int y, Chest chest)
    {
        _target = CapturePeerTarget(location, x, y, chest);
        Application.Refresh(force: true);
    }

    // Opening the network away from a chest must not reuse a previous physical capability.
    // Inspection is also available during recovery; clearing a target does not mutate inventory.
    internal void PreparePlayerTarget(string location, int x, int y, Chest? chest)
    {
        ClearTarget();
        if (ReadSnapshot().State == FlowApplicationState.Active && chest is not null && ChestInventoryAccess.IsSupported(chest))
            CaptureTarget(location, x, y, chest);
    }

    private void ClearTarget()
    {
        RequireOwnerIdle();
        if (_closed) throw new InvalidOperationException("The game session is closed.");
        if (_target is null) return;
        _target = null;
        Application.Refresh(force: true);
    }

    internal FlowCapturedTarget CapturePeerTarget(string location, int x, int y, Chest chest)
    {
        using HostOperation operation = EnterHostMutation();
        var binding = new StationBinding(Guid.NewGuid(), "target", location, x, y);
        ValidateStation(binding);
        if (!ChestInventoryAccess.IsSupported(chest) || !ReferenceEquals(_resolve(binding), chest))
            throw new InvalidOperationException("Point at an ordinary player chest.");
        return new FlowCapturedTarget(Guid.NewGuid(), binding, chest);
    }

    public FlowNetworkSnapshot ReadNetwork()
    {
        RequireThread();
        FlowSnapshot snapshot = ReadSnapshot();
        return new FlowNetworkSnapshot(snapshot, snapshot.Stations.Select(value => _stations[value.Id])
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(value => new FlowStationDetails(value.Id, value.Name, $"{value.Location} ({value.X}, {value.Y})",
                ResolveChest(value.Id) is Chest chest && ChestInventoryAccess.IsSupported(chest) && !chest.GetMutex().IsLocked())).ToArray(),
            _target?.Token ?? Guid.Empty, _target is { } target ? $"{target.Binding.Location} ({target.Binding.X}, {target.Binding.Y})" : string.Empty);
    }

    public FlowRoutePreview PreviewRoute(Guid source, Guid destination)
    {
        RequireThread();
        if (_closed || !_stations.ContainsKey(source) || !_stations.ContainsKey(destination) || source == destination)
            return new FlowRoutePreview(false, 0, 0, 0);
        RoutePlan plan = _runtime.PlanRoute(new StationId(source), new StationId(destination));
        if (plan.Status != RouteStatus.Found) return new FlowRoutePreview(false, 0, 0, 0);
        return new FlowRoutePreview(true, plan.Links.Count, plan.Links.Sum(link => link.TransitTicks),
            plan.Links.Min(link => link.Capacity - _runtime.ReservedUnits(link.Id)));
    }

    public FlowCommandResult Execute(FlowNetworkCommand command)
        => Execute(command, _target);

    internal FlowCommandResult Execute(FlowNetworkCommand command, FlowCapturedTarget? capturedTarget)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_managing) throw new InvalidOperationException("Network commands cannot reenter their owner.");
        FlowCommandResult? rejected = Application.ValidateExternalCommand(command.SessionId, command.ExpectedRevision);
        if (rejected is not null) return rejected;
        if (!Enum.IsDefined(typeof(FlowNetworkAction), command.Action))
            return new FlowCommandResult(FlowCommandStatus.InvalidCommand, ReadSnapshot().Revision);
        _managing = true;
        try
        {
            switch (command.Action)
            {
                case FlowNetworkAction.RegisterStation:
                case FlowNetworkAction.RebindStation:
                    if (capturedTarget is not { } target || target.Token != command.Target
                        || !ReferenceEquals(_resolve(target.Binding), target.Chest))
                        return new FlowCommandResult(FlowCommandStatus.Conflict, ReadSnapshot().Revision);
                    if (command.Action == FlowNetworkAction.RegisterStation)
                        RegisterStation(command.Name, target.Binding.Location, target.Binding.X, target.Binding.Y, target.Chest);
                    else RebindStation(RequireStation(command.Station).Name, target.Binding.Location, target.Binding.X, target.Binding.Y, target.Chest);
                    break;
                case FlowNetworkAction.RenameStation:
                    RenameStation(RequireStation(command.Station).Name, command.Name);
                    break;
                case FlowNetworkAction.AddLink:
                    Link(RequireStation(command.Station).Name, RequireStation(command.Destination).Name, command.Capacity, command.TransitTicks);
                    break;
                case FlowNetworkAction.RemoveLink:
                    if (command.Target == Guid.Empty || !ReadSnapshot().Links.Any(link => link.Id == command.Target)
                        || !_runtime.RemoveLink(new LinkId(command.Target)))
                        return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision);
                    Application.Refresh();
                    break;
            }
            return new FlowCommandResult(FlowCommandStatus.Applied, ReadSnapshot().Revision);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return new FlowCommandResult(_faulted ? FlowCommandStatus.Faulted : FlowCommandStatus.Rejected, ReadSnapshot().Revision);
        }
        finally { _managing = false; }
    }

    private StationBinding RequireStation(Guid id) => _stations.TryGetValue(id, out StationBinding? station)
        ? station : throw new ArgumentException("Unknown station identity.");

    private sealed class SendAdmissionFailure : InvalidOperationException
    {
        internal SendAdmissionFailure(FlowRejectionCode code)
            : base(code == FlowRejectionCode.RouteSearchLimit
                ? "The route search exceeded its supported bound."
                : "No route connects the selected stations.") => Code = code;
        internal FlowRejectionCode Code { get; }
    }
}
