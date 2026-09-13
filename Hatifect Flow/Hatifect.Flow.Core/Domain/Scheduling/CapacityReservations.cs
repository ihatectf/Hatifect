using System;
using System.Collections.Generic;
using Hatifect.Flow.Domain.Identity;
using Hatifect.Flow.Domain.Network;

namespace Hatifect.Flow.Domain.Scheduling;

internal sealed class CapacityReservations
{
    private readonly Dictionary<LinkId, int> _units = new();

    internal int ReservedUnits(LinkId id) => _units.GetValueOrDefault(id);

    internal bool TryReserve(IReadOnlyList<Link> links, int quantity)
    {
        foreach (Link link in links)
        {
            if (quantity > link.Capacity - ReservedUnits(link.Id))
            {
                return false;
            }
        }
        foreach (Link link in links)
        {
            _units[link.Id] = ReservedUnits(link.Id) + quantity;
        }
        return true;
    }

    internal void Release(LinkId id, int quantity)
    {
        int remaining = ReservedUnits(id) - quantity;
        if (remaining < 0)
        {
            throw new InvalidOperationException("Capacity release exceeds the authoritative reservation.");
        }
        if (remaining == 0)
        {
            _units.Remove(id);
        }
        else
        {
            _units[id] = remaining;
        }
    }
}
