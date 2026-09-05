using System;
using System.Linq;
using Hatifect.Flow.Application;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Sessions;

namespace Hatifect.Flow.Multiplayer;

internal enum FlowPeerOperation { Parcel, Network, Send, Recovery, Inventory, Route, Target, Refresh }
internal sealed record FlowPeerHello(int Version, ulong SaveId, Guid ClientId);
internal sealed record FlowPeerIntent(FlowPeerOperation Operation, FlowParcelCommand? Parcel = null,
    FlowNetworkCommand? Network = null, FlowSendCommand? Send = null, FlowRecoveryCommand? Recovery = null,
    Guid Station = default, Guid Destination = default, string Location = "", int X = 0, int Y = 0);
internal sealed record FlowPeerRequest(int Version, ulong SaveId, Guid ClientId, Guid SessionId, long Sequence, FlowPeerIntent Intent);
internal sealed record FlowPeerProjection(Guid SessionId, Guid NetworkId, long Revision, FlowApplicationState State,
    FlowStationDetails[] Stations, FlowLinkSnapshot[] Links, FlowParcelSnapshot[] Parcels,
    Guid Target, string TargetDescription, FlowRecoveryIssue[] Recovery,
    FlowProviderMode ProviderMode = FlowProviderMode.DiagnosticFake, FlowParcelActions SupportedOperations = FlowParcelActions.All);
internal sealed record FlowPeerReply(int Version, ulong SaveId, Guid ClientId, long Sequence, FlowPeerProjection Projection,
    FlowCommandResult? Result = null, Guid InventoryStation = default, FlowInventorySlot[]? Inventory = null,
    Guid RouteSource = default, Guid RouteDestination = default, FlowRoutePreview? Route = null);

// Wire data contains only bounded UI projections and typed intentions. Checkpoints, item XML,
// game objects and port receipts never become client mutation authority.
internal static class FlowPeerProtocol
{
    internal const int Version = 1;
    internal const string HelloType = "flow/hello-v1", RequestType = "flow/request-v1", ReplyType = "flow/reply-v1";

    internal static bool Valid(FlowPeerRequest request)
    {
        if (request is null || request.Version != Version || request.SaveId == 0 || request.ClientId == Guid.Empty
            || request.SessionId == Guid.Empty || request.Sequence <= 0 || request.Intent is not { } intent
            || !Enum.IsDefined(typeof(FlowPeerOperation), intent.Operation) || !Text(intent.Location, 256)) return false;
        int bodies = (intent.Parcel is null ? 0 : 1) + (intent.Network is null ? 0 : 1)
            + (intent.Send is null ? 0 : 1) + (intent.Recovery is null ? 0 : 1);
        bool Header(Guid session, long revision) => session == request.SessionId && revision >= 0;
        return intent.Operation switch
        {
            FlowPeerOperation.Parcel => bodies == 1 && intent.Parcel is { } command && Header(command.SessionId, command.ExpectedRevision)
                && command.ParcelId != Guid.Empty && Enum.IsDefined(typeof(FlowParcelAction), command.Action),
            FlowPeerOperation.Network => bodies == 1 && intent.Network is { } command && Header(command.SessionId, command.ExpectedRevision)
                && Enum.IsDefined(typeof(FlowNetworkAction), command.Action) && Text(command.Name, 32)
                && command.Capacity is > 0 and <= 999 && command.TransitTicks is > 0 and <= 36000,
            FlowPeerOperation.Send => bodies == 1 && intent.Send is { } command && Header(command.SessionId, command.ExpectedRevision)
                && command.Source != Guid.Empty && command.Destination != Guid.Empty && command.Source != command.Destination
                && command.Slot is >= 0 and < 128 && Fingerprint(command.Fingerprint)
                && (command.Quantity is null or (> 0 and <= 999)),
            FlowPeerOperation.Recovery => bodies == 1 && intent.Recovery is { } command && Header(command.SessionId, command.ExpectedRevision)
                && command.ParcelId != Guid.Empty,
            FlowPeerOperation.Inventory => bodies == 0 && intent.Station != Guid.Empty,
            FlowPeerOperation.Route => bodies == 0 && intent.Station != Guid.Empty && intent.Destination != Guid.Empty && intent.Station != intent.Destination,
            FlowPeerOperation.Target => bodies == 0 && intent.Location.Length > 0 && intent.X is >= 0 and <= 10000 && intent.Y is >= 0 and <= 10000,
            FlowPeerOperation.Refresh => bodies == 0,
            _ => false
        };
    }

    internal static FlowPeerProjection Capture(IFlowNetworkApplication application, Guid target = default, string description = "")
    {
        FlowNetworkSnapshot network = application.ReadNetwork();
        FlowSnapshot snapshot = network.Transport;
        return new FlowPeerProjection(snapshot.SessionId, snapshot.NetworkId, snapshot.Revision, snapshot.State,
            network.Stations.ToArray(), snapshot.Links.ToArray(), snapshot.Parcels.ToArray(), target, description, application.ReadRecovery().ToArray(),
            snapshot.ProviderMode, snapshot.SupportedOperations);
    }

    internal static bool Valid(FlowPeerProjection value)
    {
        if (value is null || value.SessionId == Guid.Empty || value.NetworkId == Guid.Empty || value.Revision < 0
            || !Enum.IsDefined(typeof(FlowApplicationState), value.State) || !Text(value.TargetDescription, 300)
            || !Enum.IsDefined(typeof(FlowProviderMode), value.ProviderMode) || (value.SupportedOperations & ~FlowParcelActions.All) != 0
            || value.Stations is null || value.Stations.Length > FlowGameSession.MaxStations
            || value.Links is null || value.Links.Length > FlowGameSession.MaxLinks
            || value.Parcels is null || value.Parcels.Length > FlowGameSession.MaxCargo
            || value.Recovery is null || value.Recovery.Length > FlowGameSession.MaxCargo) return false;
        if (value.Stations.Any(station => station is null || station.Id == Guid.Empty || !Text(station.Name, 32)
                || station.Name.Length == 0 || !Text(station.Location, 300))) return false;
        var stations = value.Stations.Select(station => station.Id).ToHashSet();
        if (stations.Count != value.Stations.Length || value.Links.Any(link => link is null || link.Id == Guid.Empty
                || !stations.Contains(link.Origin) || !stations.Contains(link.Destination) || link.Origin == link.Destination
                || link.Capacity is <= 0 or > 999 || link.TransitTicks is <= 0 or > 36000)
            || value.Links.Select(link => link.Id).Distinct().Count() != value.Links.Length) return false;
        if (value.Parcels.Any(parcel => parcel is null || parcel.Id == Guid.Empty || parcel.ShipmentId == Guid.Empty || parcel.CargoId == Guid.Empty
                || !Text(parcel.ItemKey, 256) || parcel.ItemKey.Length == 0 || parcel.Quantity is <= 0 or > 999 || parcel.Version < 0
                || parcel.DeliveryAttempts is < 0 or > 16 || !stations.Contains(parcel.Origin) || !stations.Contains(parcel.Destination)
                || !stations.Contains(parcel.CurrentStation) || !Enum.IsDefined(typeof(ParcelState), parcel.State)
                || !Valid(parcel.Availability) || parcel.Actions != parcel.Availability.Actions
                || (parcel.Actions & ~value.SupportedOperations) != 0)) return false;
        var parcels = value.Parcels.Select(parcel => parcel.Id).ToHashSet();
        return parcels.Count == value.Parcels.Length && value.Recovery.All(issue => issue is not null && parcels.Contains(issue.ParcelId)
            && Text(issue.ItemKey, 256) && issue.Quantity is > 0 and <= 999 && Text(issue.Phase, 64)
            && issue.Receipt is "Missing" or "Applied" or "Rejected")
            && value.Recovery.Select(issue => issue.ParcelId).Distinct().Count() == value.Recovery.Length;
    }

    internal static bool Valid(FlowPeerReply reply)
    {
        if (reply is null || reply.Version != Version || reply.SaveId == 0 || reply.ClientId == Guid.Empty || reply.Sequence < 0
            || !Valid(reply.Projection) || reply.Result is { } result && (result.Revision < 0 || !Enum.IsDefined(typeof(FlowCommandStatus), result.Status)
                || !Enum.IsDefined(typeof(FlowRejectionCode), result.Code) || (result.Status == FlowCommandStatus.Applied) != (result.Code == FlowRejectionCode.None))) return false;
        if (reply.Inventory is { } slots && (slots.Length > 128 || reply.InventoryStation == Guid.Empty
            || slots.Any(slot => slot is null || slot.Index is < 0 or >= 128 || !Text(slot.ItemKey, 256)
                || slot.ItemKey.Length == 0 || slot.Quantity is <= 0 or > 999 || !Fingerprint(slot.Fingerprint) || !Text(slot.Detail, 24))
            || slots.Select(slot => slot.Index).Distinct().Count() != slots.Length)) return false;
        if (reply.Route is { } route && (reply.RouteSource == Guid.Empty || reply.RouteDestination == Guid.Empty
            || reply.RouteSource == reply.RouteDestination || route.LinkCount is < 0 or > FlowGameSession.MaxLinks
            || route.TransitTicks is < 0 or > 4608000 || route.AvailableUnits is < 0 or > 999)) return false;
        return true;
    }

    internal static FlowNetworkSnapshot Materialize(FlowPeerProjection value)
    {
        if (!Valid(value)) throw new ArgumentException("Invalid Flow peer projection.", nameof(value));
        var snapshot = new FlowSnapshot(value.SessionId, value.NetworkId, value.Revision, value.State,
            value.Stations.Select(station => new FlowStationSnapshot(station.Id)).ToArray(), value.Links, value.Parcels,
            value.ProviderMode, value.SupportedOperations);
        return new FlowNetworkSnapshot(snapshot, value.Stations, value.Target, value.TargetDescription);
    }

    private static bool Valid(FlowActionAvailabilitySet? value) => value is not null
        && Valid(value.Reserve, FlowParcelAction.Reserve) && Valid(value.Cancel, FlowParcelAction.Cancel)
        && Valid(value.RetryDelivery, FlowParcelAction.RetryDelivery) && Valid(value.ReconcileTransfer, FlowParcelAction.ReconcileTransfer)
        && Valid(value.ReturnToSource, FlowParcelAction.ReturnToSource);
    private static bool Valid(FlowActionAvailability? value, FlowParcelAction action) => value is not null && value.Action == action
        && Enum.IsDefined(typeof(FlowRejectionCode), value.Code) && value.Available == (value.Code == FlowRejectionCode.None)
        && value.ReasonKey == FlowReasons.Key(value.Code);
    private static bool Text(string? value, int maximum) => value is not null && value.Length <= maximum;
    private static bool Fingerprint(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
}
