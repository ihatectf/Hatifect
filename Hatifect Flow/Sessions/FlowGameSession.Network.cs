using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Application.Planning;
using Hatifect.Flow.Diagnostics;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Shipments;
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
            return ExecuteSendCommand(command);
        }
        catch (CommandAdmissionFailure error)
        {
            FlowCommandStatus status = error.Code == FlowRejectionCode.StateChanged
                ? FlowCommandStatus.Conflict : FlowCommandStatus.Rejected;
            return new FlowCommandResult(status, ReadSnapshot().Revision, error.Code);
        }
        catch (FlowResourceLimitException error)
        { return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision, ResourceLimitCode(error.Resource)); }
        catch (ArgumentOutOfRangeException)
        { return new FlowCommandResult(FlowCommandStatus.InvalidCommand, ReadSnapshot().Revision); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { return new FlowCommandResult(_faulted ? FlowCommandStatus.Faulted : FlowCommandStatus.Rejected, ReadSnapshot().Revision); }
        finally { _managing = false; }
    }

    // Station resolution, command-shape validation and SendCore dispatch as one visible sequence.
    private FlowCommandResult ExecuteSendCommand(FlowSendCommand command)
    {
        StationBinding source = RequireStation(command.Source), destination = RequireStation(command.Destination);
        if (string.IsNullOrEmpty(command.Fingerprint) || command.Fingerprint.Length != 64 || command.Quantity is <= 0 or > 999)
            return new FlowCommandResult(FlowCommandStatus.InvalidCommand, ReadSnapshot().Revision);
        if (!SendCore(source.Name, destination.Name, command.Slot, command.Fingerprint, command.Quantity).HasValue)
            return new FlowCommandResult(FlowCommandStatus.Conflict, ReadSnapshot().Revision);
        return new FlowCommandResult(FlowCommandStatus.Applied, ReadSnapshot().Revision);
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
                    if (BindOrRebindStation(command, capturedTarget) is { } stationResult) return stationResult;
                    break;
                case FlowNetworkAction.RenameStation:
                    StationBinding selected = RequireStation(command.Station);
                    if (_stations.Values.Any(value => value.Id != selected.Id
                        && string.Equals(value.Name, command.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new CommandAdmissionFailure(FlowRejectionCode.StateChanged);
                    RenameStation(selected.Name, command.Name);
                    break;
                case FlowNetworkAction.AddLink:
                    Link(RequireStation(command.Station).Name, RequireStation(command.Destination).Name, command.Capacity, command.TransitTicks);
                    break;
                case FlowNetworkAction.RemoveLink:
                    if (command.Target == Guid.Empty || !ReadSnapshot().Links.Any(link => link.Id == command.Target)
                        || !_runtime.RemoveLink(new LinkId(command.Target)))
                        throw new CommandAdmissionFailure(FlowRejectionCode.StateChanged);
                    Application.Refresh();
                    break;
            }
            return new FlowCommandResult(FlowCommandStatus.Applied, ReadSnapshot().Revision);
        }
        catch (CommandAdmissionFailure error)
        {
            FlowCommandStatus status = error.Code == FlowRejectionCode.StateChanged
                ? FlowCommandStatus.Conflict : FlowCommandStatus.Rejected;
            return new FlowCommandResult(status, ReadSnapshot().Revision, error.Code);
        }
        catch (FlowResourceLimitException error)
        { return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision, ResourceLimitCode(error.Resource)); }
        catch (FlowInventoryUnavailableException)
        { return new FlowCommandResult(FlowCommandStatus.Rejected, ReadSnapshot().Revision, FlowRejectionCode.ProviderUnavailable); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { return new FlowCommandResult(FlowCommandStatus.InvalidCommand, ReadSnapshot().Revision); }
        catch (InvalidOperationException)
        {
            return new FlowCommandResult(_faulted ? FlowCommandStatus.Faulted : FlowCommandStatus.Rejected, ReadSnapshot().Revision);
        }
        finally { _managing = false; }
    }

    private StationBinding RequireStation(Guid id) => _stations.TryGetValue(id, out StationBinding? station)
        ? station : throw new ArgumentException("Unknown station identity.");

    // The captured target must still name the same physical chest that was pointed at; a stale
    // or foreign reference is a Conflict, never an admission failure inside the switch.
    private FlowCommandResult? BindOrRebindStation(FlowNetworkCommand command, FlowCapturedTarget? capturedTarget)
    {
        if (capturedTarget is not { } target || target.Token != command.Target
            || !ReferenceEquals(_resolve(target.Binding), target.Chest))
            return new FlowCommandResult(FlowCommandStatus.Conflict, ReadSnapshot().Revision);
        if (!ChestInventoryAccess.IsSupported(target.Chest) || target.Chest.GetMutex().IsLocked())
            throw new CommandAdmissionFailure(FlowRejectionCode.ProviderUnavailable);
        if (command.Action == FlowNetworkAction.RegisterStation)
            RegisterCapturedStation(command, target);
        else
            RebindCapturedStation(command, target);
        return null;
    }

    private void RegisterCapturedStation(FlowNetworkCommand command, FlowCapturedTarget target)
    {
        if (_stations.Values.Any(station => string.Equals(station.Name, command.Name, StringComparison.OrdinalIgnoreCase)
                || station.Location == target.Binding.Location && station.X == target.Binding.X && station.Y == target.Binding.Y)
            || target.Chest.modData.TryGetValue(StationKey, out string previous)
                && Guid.TryParse(previous, out Guid old) && _stations.ContainsKey(old))
            throw new CommandAdmissionFailure(FlowRejectionCode.StateChanged);
        RegisterStation(command.Name, target.Binding.Location, target.Binding.X, target.Binding.Y, target.Chest);
    }

    private void RebindCapturedStation(FlowNetworkCommand command, FlowCapturedTarget target)
    {
        StationBinding station = RequireStation(command.Station);
        if (_stations.Values.Any(value => value.Id != station.Id && value.Location == target.Binding.Location
                && value.X == target.Binding.X && value.Y == target.Binding.Y)
            || target.Chest.modData.TryGetValue(StationKey, out string tag) && tag != station.Id.ToString("D"))
            throw new CommandAdmissionFailure(FlowRejectionCode.StateChanged);
        if (ReadSnapshot().Parcels.Any(parcel => parcel.Origin == station.Id
            && parcel.State is ParcelState.Created or ParcelState.Reserved or ParcelState.ExtractionUncertain))
            throw new CommandAdmissionFailure(FlowRejectionCode.OperationPending);
        RebindStation(station.Name, target.Binding.Location, target.Binding.X, target.Binding.Y, target.Chest);
    }

    private static FlowRejectionCode ResourceLimitCode(FlowAdmissionResource resource) => resource switch
    {
        FlowAdmissionResource.Stations => FlowRejectionCode.StationLimit,
        FlowAdmissionResource.LifetimeLinks => FlowRejectionCode.LifetimeLinkLimit,
        FlowAdmissionResource.RetainedCargo => FlowRejectionCode.RetainedCargoLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(resource))
    };

    private sealed class CommandAdmissionFailure : InvalidOperationException
    {
        internal CommandAdmissionFailure(FlowRejectionCode code)
            : base(code switch
            {
                FlowRejectionCode.RouteSearchLimit => "The route search exceeded its supported bound.",
                FlowRejectionCode.RouteUnavailable => "No route connects the selected stations.",
                FlowRejectionCode.ProviderUnavailable => "The source inventory is temporarily unavailable.",
                FlowRejectionCode.OperationPending => "The selected stack already belongs to an active shipment.",
                FlowRejectionCode.StateChanged => "The selected source changed before admission.",
                _ => "The command cannot be admitted."
            }) => Code = code;
        internal FlowRejectionCode Code { get; }
    }
}
