using System;

namespace Hatifect.Flow.Sessions;

// Stored by SMAPI in the same game-save aggregate as the world's inventories.
// Payloads at a station are historical descriptions, never instructions to recreate items.
internal sealed record FlowGameSave(int Version, ulong SaveId, byte[] Checkpoint,
    StationBinding[] Stations, CargoPayload[] Payloads, bool RequiresRecovery = false);

internal sealed record StationBinding(Guid Id, string Name, string Location, int X, int Y);
internal sealed record CargoPayload(Guid Id, string Xml);
