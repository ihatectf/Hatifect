using System.Linq;
using Hatifect.Flow.Domain.Checkpoints;

namespace Hatifect.Flow.Infrastructure.Persistence;

internal static class PortCapacityProjection
{
    public static DurableProviderImage Provider(DurableProviderImage before, PortCapacityIntent intent)
        => before with
        {
            Revision = checked(before.Revision + 1),
            ConfigurationRevision = checked(before.ConfigurationRevision + 1),
            Stations = Stations(before.Stations, intent)
        };

    public static FlowCheckpoint Core(FlowCheckpoint before, PortCapacityIntent intent)
        => before with { Stations = Stations(before.Stations, intent) };

    private static StationCheckpoint[] Stations(StationCheckpoint[] before, PortCapacityIntent intent)
        => before.Select(station => station.Id == intent.StationId
            ? station with { Port = station.Port with { MaxCargoBatches = intent.MaxCargoBatches,
                MaxReceipts = intent.MaxReceipts } } : station).ToArray();
}
