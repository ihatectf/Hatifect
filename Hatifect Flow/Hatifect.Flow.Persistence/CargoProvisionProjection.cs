using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

// Pure projections shared by preflight, publication and recovery. Validation and
// private owner permission remain at their respective boundaries.
internal static class CargoProvisionProjection
{
    public static DurableProviderImage Provider(DurableProviderImage before, CargoProvisionIntent intent)
        => before with
        {
            Revision = checked(before.Revision + 1),
            ConfigurationRevision = checked(before.ConfigurationRevision + 1),
            Stations = Stations(before.Stations, intent)
        };

    public static FlowCheckpoint Core(FlowCheckpoint before, CargoProvisionIntent intent)
        => before with
        {
            Stations = Stations(before.Stations, intent),
            Cargo = before.Cargo.Append(new CargoCheckpoint(intent.CargoId, intent.Manifest,
                new OwnerCheckpoint(intent.StationId, null, null), null, intent.StationId, 0)).ToArray()
        };

    private static StationCheckpoint[] Stations(StationCheckpoint[] before, CargoProvisionIntent intent)
        => before.Select(station => station.Id == intent.StationId
            ? station with { Port = station.Port with
                { Inventory = station.Port.Inventory.Append(new InventoryCheckpoint(intent.CargoId, intent.Manifest)).ToArray() } }
            : station).ToArray();
}
