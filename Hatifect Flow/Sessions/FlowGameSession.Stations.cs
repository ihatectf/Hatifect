using System;
using System.Linq;
using Hatifect.Flow.Domain.Shipments;
using Hatifect.Flow.Inventory;
using StardewValley.Objects;

namespace Hatifect.Flow.Sessions;

internal sealed partial class FlowGameSession
{
    internal void RenameStation(string name, string replacement)
    {
        using HostOperation operation = EnterHostMutation();
        StationBinding station = FindStation(name);
        StationBinding updated = station with { Name = replacement };
        ValidateStation(updated);
        if (_stations.Values.Any(value => value.Id != station.Id
            && string.Equals(value.Name, replacement, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A station already uses this name.");
        if (station == updated) return;
        _stations[station.Id] = updated;
        Application.Refresh(force: true);
    }

    internal void RebindStation(string name, string location, int x, int y, Chest chest)
    {
        using HostOperation operation = EnterHostMutation();
        StationBinding station = FindStation(name);
        StationBinding updated = station with { Location = location, X = x, Y = y };
        ValidateStation(updated);
        if (_stations.Values.Any(value => value.Id != station.Id && value.Location == location && value.X == x && value.Y == y)
            || !ChestInventoryAccess.IsSupported(chest) || chest.GetMutex().IsLocked() || !ReferenceEquals(_resolve(updated), chest)
            || chest.modData.TryGetValue(StationKey, out string tag) && tag != station.Id.ToString("D"))
            throw new InvalidOperationException("The selected chest is unavailable or belongs to another station.");
        Chest? previous = ResolveChest(station.Id);
        if (station == updated && ReferenceEquals(previous, chest)) return;
        if (Application.ReadSnapshot().Parcels.Any(parcel => parcel.Origin == station.Id
            && parcel.State is ParcelState.Created or ParcelState.Reserved or ParcelState.ExtractionUncertain))
            throw new InvalidOperationException("Cancel queued shipments before moving their source station.");
        using IDisposable access = _inventory.EnterChest(chest);
        using IDisposable? previousAccess = previous is not null && !ReferenceEquals(previous, chest) ? _inventory.EnterChest(previous) : null;
        // Keep the logical port, receipts and cargo identity. Rebinding never moves or recreates items.
        try
        {
            chest.modData[StationKey] = station.Id.ToString("D");
            _stations[station.Id] = updated;
            if (previous is not null && !ReferenceEquals(previous, chest)) previous.modData.Remove(StationKey);
        }
        catch
        {
            _faulted = true;
            UpdateAvailability();
            throw;
        }
        Application.Refresh(force: true);
    }
}
